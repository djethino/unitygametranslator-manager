using System.Text.Json;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>What the site hands back to start a sign-in.</summary>
public sealed record DeviceFlowStart(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds);

/// <summary>How a sign-in ended.</summary>
public sealed record DeviceFlowResult(
    bool Authorised,
    string? AccessToken,
    string? UserName,
    string? Failure);

/// <summary>
/// Signing in without ever handling a password: the site shows a short code, the person types it
/// on their own screen, and we are told when it is done.
///
/// ⚠ The token obtained here is the INSTALLER's, deliberately separate from the mod's. Revoking one
/// must not disconnect the other, and a tool that installs things has no business holding the
/// credential a game uses. The installer may later offer to write its own into a game, never
/// silently and never by copying the mod's.
///
/// The wait is a server-sent event stream, which is what the mod uses — same endpoint, same three
/// events. Polling was documented once and no longer exists server-side, so it is not an option to
/// fall back to; a stream that cannot be opened is reported as such, with the code still on screen,
/// rather than left spinning.
/// </summary>
public sealed class DeviceFlowClient
{
    private readonly HttpClient _http;

    public DeviceFlowClient(HttpClient? http = null) =>
        _http = http ?? Http.Create(TimeSpan.FromSeconds(20));

    /// <summary>Asks for a code. Null when the site could not be reached.</summary>
    public async Task<DeviceFlowStart?> BeginAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http
                .PostAsync($"{BuildInfo.ApiBaseUrl}/auth/device", null, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var deviceCode = root.TryGetProperty("device_code", out var d) ? d.GetString() : null;
            var userCode = root.TryGetProperty("user_code", out var u) ? u.GetString() : null;
            if (deviceCode is null || userCode is null) return null;

            var uri = root.TryGetProperty("verification_uri", out var v) ? v.GetString() : null;
            var expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds)
                ? seconds
                : 900;

            return new DeviceFlowStart(deviceCode, userCode,
                uri ?? BuildInfo.WebsiteBaseUrl + "/link", expires);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Waits on the stream until the person authorises, refuses, or the code expires.
    ///
    /// ⚠ The stream is served by a separate micro-server, not by the site itself. If it is not
    /// running — a self-hosted instance, an outage — nothing will ever arrive, so a failure to open
    /// it is reported straight away instead of waiting fifteen minutes on a connection that was
    /// never going to speak.
    /// </summary>
    public async Task<DeviceFlowResult> WaitAsync(string deviceCode, CancellationToken ct = default)
    {
        var url = $"{BuildInfo.SseBaseUrl}/auth/device/{Uri.EscapeDataString(deviceCode)}/stream";

        try
        {
            // No timeout of its own: this connection is meant to stay open until somebody acts,
            // and the shared client's would cut it off mid-wait.
            using var client = Http.Create(Timeout.InfiniteTimeSpan);
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new DeviceFlowResult(false, null, null,
                    $"The sign-in service answered {(int)response.StatusCode}. Your code is still "
                    + "valid — try again in a moment.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            string? eventName = null;
            var data = new System.Text.StringBuilder();

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                // A blank line ends an event. Everything gathered so far is the message.
                if (line.Length == 0)
                {
                    var outcome = Interpret(eventName, data.ToString());
                    if (outcome is not null) return outcome;

                    eventName = null;
                    data.Clear();
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                    eventName = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                    data.Append(line[5..].Trim());

                // Anything else — id:, retry:, a comment — is not ours to act on.
            }

            // The stream ended without a verdict: the server closed it, or the network did.
            return new DeviceFlowResult(false, null, null,
                "The connection closed before you finished. Nothing was changed — start again "
                + "when you are ready.");
        }
        catch (OperationCanceledException)
        {
            return new DeviceFlowResult(false, null, null, null);
        }
        catch (Exception ex)
        {
            return new DeviceFlowResult(false, null, null, Http.Describe(ex, "the sign-in service"));
        }
    }

    /// <summary>
    /// Turns one event into a verdict, or null to keep waiting.
    ///
    /// The three names come from the mod, which reads the same stream: authorized, expired, error.
    /// Anything else is a heartbeat or something added since, and waiting through it is the only
    /// safe reading — treating an unknown event as a failure would break sign-in the day the
    /// server gains one.
    /// </summary>
    private static DeviceFlowResult? Interpret(string? eventName, string payload)
    {
        if (eventName is null || payload.Length == 0) return null;

        switch (eventName)
        {
            case "authorized":
                try
                {
                    using var document = JsonDocument.Parse(payload);
                    var root = document.RootElement;

                    var token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        return new DeviceFlowResult(false, null, null,
                            "The site said you were signed in but sent no token.");
                    }

                    var name = root.TryGetProperty("user", out var user)
                               && user.TryGetProperty("name", out var n)
                        ? n.GetString()
                        : null;

                    return new DeviceFlowResult(true, token, name, null);
                }
                catch
                {
                    return new DeviceFlowResult(false, null, null,
                        "The site's answer could not be read.");
                }

            case "expired":
                return new DeviceFlowResult(false, null, null,
                    "That code expired. Ask for a new one — they last fifteen minutes.");

            case "error":
                return new DeviceFlowResult(false, null, null,
                    "The site refused the sign-in. Nothing was changed.");

            default:
                return null;
        }
    }

    /// <summary>
    /// Hands this tool's access back to the site. False when the site could not be reached — the
    /// caller signs out locally regardless and says so.
    /// </summary>
    /// <remarks>
    /// ⚠ Cuts nothing but this installation's own token, which is the point of it being separate
    /// from the mod's (see the note on this class). Without this call the access simply stays in
    /// the account's list, and the site cannot tell a forgotten token from a quiet one.
    ///
    /// ⚠ The token is passed in rather than read from settings, because signing out clears those
    /// first: what is stored locally must never wait on a network call.
    /// </remarks>
    public async Task<bool> RevokeAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return true;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{BuildInfo.ApiBaseUrl}/auth/token");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            // Already gone is the outcome we wanted, not a failure.
            return response.IsSuccessStatusCode
                || response.StatusCode == System.Net.HttpStatusCode.NotFound;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The short code the site names THIS access by, or null when it cannot be asked.
    /// </summary>
    /// <remarks>
    /// 🔴 **Without it, "Linked devices" is an impasse.** That page names every line "#QKADJN" and
    /// offers to rename the machine it belongs to — while the code appeared in no program of ours,
    /// so somebody was asked to name a machine nothing let them identify, and to choose between
    /// accesses they could not tell apart. Reported from production on 2026-08-27.
    ///
    /// ⚠ Retroactive: the code has been stored against this token since it was issued, so an
    /// installation linked months ago becomes identifiable as soon as this is asked.
    ///
    /// ⚠ Not a secret and not a credential. No endpoint accepts it as input — which is what keeps
    /// six characters from becoming an enumeration surface — and whoever can read this answer
    /// already holds the token, which is the strong one.
    /// </remarks>
    public async Task<string?> AccessCodeAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BuildInfo.ApiBaseUrl}/me");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            return document.RootElement.TryGetProperty("access_code", out var code)
                ? code.GetString()
                : null;
        }
        catch
        {
            // An unreachable site is an ordinary answer here: the row simply shows no code rather
            // than a code that might be wrong.
            return null;
        }
    }
}
