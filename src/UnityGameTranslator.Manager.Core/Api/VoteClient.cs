using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>What the site said after a vote.</summary>
public sealed class VoteOutcome
{
    public int Count { get; init; }

    /// <summary>This account's vote afterwards: 1, -1, or null once withdrawn.</summary>
    public int? Mine { get; init; }
}

/// <summary>
/// Rating somebody else's published translation.
///
/// ⚠ **Who may do it is decided by <see cref="Common.Voting"/>, never here.** The server refuses a
/// self-vote, an unpublished translation and an anonymous caller; the client restates those so it
/// never draws an arrow that would come back 403, and adds the one the server cannot see — whether
/// this machine has actually run the translation. This class only carries the request.
///
/// ⚠ The endpoint takes 1 or -1 and nothing else. Sending the same value again is how the site
/// withdraws a vote, so the caller must not treat an unchanged count as a failure.
/// </summary>
public sealed class VoteClient
{
    private readonly HttpClient _http;

    public VoteClient(HttpClient? http = null)
    {
        _http = http ?? Http.Create(TimeSpan.FromSeconds(10));
    }

    /// <summary>The reason the last call gave nothing back, for showing rather than swallowing.</summary>
    public string? LastError { get; private set; }

    public async Task<VoteOutcome?> CastAsync(int translationId, int value, string apiToken)
    {
        LastError = null;

        if (value != 1 && value != -1)
        {
            // Not an exception: a caller that got this wrong has a bug, and a crash in a GUI thread
            // helps nobody. It is stated and refused.
            LastError = "A vote is 1 or -1.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(apiToken))
        {
            LastError = "Sign in to rate this translation.";
            return null;
        }

        try
        {
            var url = $"{BuildInfo.ApiBaseUrl}/translations/{translationId}/vote";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent($"{{\"value\":{value}}}", Encoding.UTF8, "application/json"),
            };

            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);

            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // ⚠ 403 is the ordinary refusal, not a fault: the rules moved under us, or this
                // translation became ours. Said in the server's terms rather than as a code.
                LastError = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "The site refused this vote — it may be your own translation."
                    : $"The site answered {(int)response.StatusCode}.";
                return null;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return new VoteOutcome
            {
                Count = root.TryGetProperty("vote_count", out var count) ? count.GetInt32() : 0,
                Mine = root.TryGetProperty("user_vote", out var mine) && mine.ValueKind != JsonValueKind.Null
                    ? mine.GetInt32()
                    : null,
            };
        }
        catch (Exception error)
        {
            // A frontier with somebody else's server: it is caught, and it is REPORTED. The caller
            // shows LastError rather than a silent absence of arrows.
            LastError = error.Message;
            return null;
        }
    }
}
