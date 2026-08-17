using System.Text.Json;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Ai;

/// <summary>What asking the provider about a key came back with.</summary>
/// <param name="Works">True only when the provider itself accepted the key.</param>
/// <param name="Message">
/// One sentence, plain, for a person to read. Never a status code on its own: "403" tells somebody
/// nothing about what to do next, and this is the screen where they can still do something.
/// </param>
public sealed record KeyCheck(bool Works, string Message);

/// <summary>
/// Asks Google or DeepL whether a key is any good, before a game is set up with it.
///
/// 🔴 **Written because there was no way to find out.** A key was typed, encrypted, stored and
/// written into a game's config.json, and the first thing that ever tested it was the mod failing
/// to translate a line mid-game — with nothing on any screen saying the key was the reason. The
/// local AI server has had a Refresh and a test bench since the beginning; these two had nothing.
///
/// ⚠ **Both calls are free and translate nothing.** DeepL's usage endpoint reports an allowance
/// and consumes none of it; Google's language list returns what it can translate into and bills no
/// characters. A "test" that quietly spends somebody's allowance would be a trap, so neither
/// translates a word.
///
/// ⚠ **It answers about the key, never about the network.** Unreachable, timed out or refused by a
/// proxy are all reported as "could not ask", not as a bad key — telling somebody their key is
/// wrong because their wifi dropped is how a working key ends up deleted and retyped.
/// </summary>
public sealed class TranslatorKeyProbe
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Google Translate v2. The language list is the cheapest authenticated call it offers.
    /// </summary>
    public async Task<KeyCheck> CheckGoogleAsync(string? key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return new KeyCheck(false, "No key yet.");

        var url = "https://translation.googleapis.com/language/translate/v2/languages"
                  + "?key=" + Uri.EscapeDataString(key.Trim());

        return await AskAsync(url, request => { }, ct, (status, body) => status switch
        {
            // ⚠ Google answers 400 for a malformed key and 403 for a real key the project has not
            // enabled the API on — two different jobs for the person, so two different sentences.
            System.Net.HttpStatusCode.OK => new KeyCheck(true, GoogleCount(body)),

            System.Net.HttpStatusCode.BadRequest =>
                new KeyCheck(false, "Google does not recognise this key."),

            System.Net.HttpStatusCode.Forbidden =>
                new KeyCheck(false, "Google refused this key. It usually means the Cloud "
                                    + "Translation API is not switched on for it, or billing is not "
                                    + "set up."),

            _ => new KeyCheck(false, $"Google answered {(int)status}."),
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// DeepL. Usage rather than translate: it reports the allowance and spends none of it.
    /// </summary>
    /// <param name="free">
    /// Which host to ask. ⚠ It is not cosmetic — a free key on the paid host and a paid key on the
    /// free host are both rejected as invalid, so testing against the wrong one would condemn a
    /// perfectly good key.
    /// </param>
    public async Task<KeyCheck> CheckDeeplAsync(string? key, bool free, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return new KeyCheck(false, "No key yet.");

        var url = (free ? "https://api-free.deepl.com" : "https://api.deepl.com") + "/v2/usage";
        var trimmed = key.Trim();

        return await AskAsync(url,
            request => request.Headers.TryAddWithoutValidation("Authorization",
                                                               "DeepL-Auth-Key " + trimmed),
            ct,
            (status, body) => status switch
            {
                System.Net.HttpStatusCode.OK => new KeyCheck(true, DeeplAllowance(body)),

                System.Net.HttpStatusCode.Forbidden =>
                    new KeyCheck(false, free
                        ? "DeepL refused this key on the free host. If it is a Pro key, untick "
                          + "Free tier."
                        : "DeepL refused this key on the paid host. If it is a free key, tick "
                          + "Free tier."),

                (System.Net.HttpStatusCode)456 =>
                    new KeyCheck(false, "The key works, but its allowance for this month is used up."),

                _ => new KeyCheck(false, $"DeepL answered {(int)status}."),
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// One request, and every way it can fail turned into a sentence.
    ///
    /// ⚠ The catch is at a process boundary — somebody else's server, over a network we do not
    /// control — which is the one place this project allows one. It is reported, never swallowed.
    /// </summary>
    private static async Task<KeyCheck> AskAsync(
        string url, Action<HttpRequestMessage> prepare, CancellationToken ct,
        Func<System.Net.HttpStatusCode, string, KeyCheck> read)
    {
        try
        {
            using var client = Http.Create(Patience);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            prepare(request);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return read(response.StatusCode, body);
        }
        catch (TaskCanceledException)
        {
            return new KeyCheck(false, "No answer in twelve seconds. The key was not tested.");
        }
        catch (HttpRequestException ex)
        {
            return new KeyCheck(false, $"Could not reach the provider ({ex.Message}). The key was "
                                       + "not tested.");
        }
    }

    /// <summary>
    /// Turns DeepL's usage into the one figure worth reading: what is left.
    ///
    /// ⚠ Falls back to a plain "it works" rather than to an error. The key was accepted — that is
    /// the question that was asked — and a body we cannot parse must not turn a yes into a no.
    /// </summary>
    private static string DeeplAllowance(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            if (root.TryGetProperty("character_count", out var used)
                && root.TryGetProperty("character_limit", out var limit)
                && limit.GetInt64() > 0)
            {
                var left = limit.GetInt64() - used.GetInt64();
                return $"The key works. {left:N0} characters left of {limit.GetInt64():N0} this month.";
            }
        }
        catch (JsonException)
        {
            // Answered, and accepted the key. What it said about the allowance is a bonus.
        }

        return "The key works.";
    }

    /// <summary>How many languages it offered, which is the proof it answered as itself.</summary>
    private static string GoogleCount(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);

            if (json.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("languages", out var languages)
                && languages.ValueKind == JsonValueKind.Array)
            {
                return $"The key works. Google offers {languages.GetArrayLength()} languages.";
            }
        }
        catch (JsonException)
        {
        }

        return "The key works.";
    }
}
