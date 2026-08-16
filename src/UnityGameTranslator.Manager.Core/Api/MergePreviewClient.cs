using System.Net.Http.Headers;
using System.Text.Json;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>A comparison opened in a browser, and where to send somebody to settle it.</summary>
public sealed record MergePreview(string Token, string Url);

/// <summary>
/// Settling a conflict where there is a screen for it: the site's side-by-side merge.
///
/// ⚠ **A round trip, and half of it would be worse than none.** Sending the file and opening the
/// browser is the easy half; somebody then reads two versions of every contested line and chooses.
/// If nothing came back for the answer, those choices would sit on the site and the file here would
/// never change — the work would evaporate, silently, which is exactly the shape of failure this
/// whole area keeps producing.
///
/// ⚠ **destination = local.** The result comes back HERE rather than being published: this is a
/// comparison whose outcome lands in a game folder, and the site allows it against any translation
/// the caller could already download. Publishing is a separate act with its own gate.
///
/// ⚠ The mod does the same thing and is told about the answer over SSE. This one polls: a desktop
/// tool behind a corporate proxy is where a long-lived stream fails silently, and the answer is a
/// few hundred bytes until it exists at all.
/// </summary>
public sealed class MergePreviewClient
{
    private readonly HttpClient _http;

    public MergePreviewClient(HttpClient? http = null)
    {
        _http = http ?? Http.Create(TimeSpan.FromSeconds(60));
    }

    /// <summary>Why the last call failed, in words a user can act on. Null after a success.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Hand the site the local file and get a page where the two can be settled.
    /// </summary>
    public async Task<MergePreview?> OpenAsync(int translationId, string localJson, string apiToken,
                                               CancellationToken ct = default)
    {
        LastError = null;

        JsonDocument local;
        try
        {
            local = JsonDocument.Parse(localJson);
        }
        catch (Exception ex)
        {
            LastError = $"This game's translation file is not valid JSON: {ex.Message}";
            return null;
        }

        using (local)
        {
            if (local.RootElement.ValueKind != JsonValueKind.Object)
            {
                LastError = "This game's translation file is not a JSON object. Nothing was sent.";
                return null;
            }

            var payload = new MemoryStream();
            using (var writer = new Utf8JsonWriter(payload))
            {
                writer.WriteStartObject();
                writer.WriteNumber("translation_id", translationId);
                writer.WritePropertyName("local_content");
                local.RootElement.WriteTo(writer);
                // The result comes back to this machine rather than being published.
                writer.WriteString("destination", "local");
                writer.WriteEndObject();
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{BuildInfo.ApiBaseUrl}/merge-preview/init");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
                request.Content = new ByteArrayContent(payload.ToArray());
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    LastError = Describe((int)response.StatusCode, body);
                    return null;
                }

                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                var token = Text(root, "token");
                var url = Text(root, "url");

                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(url))
                {
                    LastError = "The site opened a comparison but did not say where to find it.";
                    return null;
                }

                return new MergePreview(token, url);
            }
            catch (Exception ex)
            {
                LastError = Net.Http.Describe(ex, "the community site");
                return null;
            }
        }
    }

    /// <summary>
    /// The settled file, once somebody has settled it. Null while there is nothing yet.
    ///
    /// ⚠ "Nothing yet" and "this comparison is gone" look the same from here — both answer 404 —
    /// so the caller bounds the wait rather than trusting the difference. Guessing between them
    /// would either abandon somebody mid-decision or wait for ever on a page that was closed.
    /// </summary>
    public async Task<string?> ResultAsync(string token, string apiToken, CancellationToken ct = default)
    {
        LastError = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{BuildInfo.ApiBaseUrl}/merge-preview/{Uri.EscapeDataString(token)}/result");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LastError = Describe((int)response.StatusCode, body);
                return null;
            }

            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Object)
            {
                LastError = "The site sent a result that is not a translation file. Nothing was written.";
                return null;
            }

            return content.GetRawText();
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return null;
        }
    }

    /// <summary>
    /// The server's own words when it sent any. ⚠ Only from known fields and bounded: a remote
    /// server does not get to write what this window says.
    /// </summary>
    private static string Describe(int status, string? body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                    && error.GetString() is { Length: > 0 } message)
                {
                    return message.Length > 300 ? message[..300] + "…" : message;
                }
            }
            catch
            {
                // Not JSON, or not shaped as expected: the status code says enough.
            }
        }

        return status switch
        {
            401 => "The site did not accept this account's sign-in.",
            403 => "This translation is not one this account may compare against.",
            409 => "That comparison was published rather than brought back here.",
            429 => "The site is asking us to slow down. Try again in a moment.",
            _ => $"The server answered {status}.",
        };
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
