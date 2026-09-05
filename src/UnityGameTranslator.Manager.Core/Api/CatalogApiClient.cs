using System.Net.Http;
using System.Text;
using System.Text.Json;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>
/// Talks to the community site, read-only.
///
/// This is the part no other installer can offer: Steam hands us the app id for free, and the
/// public search endpoint turns it into "this game already has a French translation by someone".
/// The endpoint is public, rate limited, and a search that fails simply means we show nothing.
///
/// ⚠ **A token is sent only when this tool has one, and only for `user_vote`** — never for
/// permission. Nothing here is refused without it and no result changes; what the server cannot do
/// for an unnamed caller is say whether THIS account has already rated each translation, and
/// without that the arrows cannot show what somebody already chose.
/// </summary>
public sealed class CatalogApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public CatalogApiClient(HttpClient? http = null)
    {
        _http = http ?? Http.Create(TimeSpan.FromSeconds(10));
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"UnityGameTranslatorManager/{BuildInfo.Version}");
        }
    }

    /// <summary>
    /// Why the last search returned nothing, when the reason was a failure rather than an empty
    /// result. "No translation found" and "the search broke" look identical to a user, and only
    /// one of them is our fault — so the difference is kept instead of swallowed.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// What the server ANSWERED, when it answered at all.
    ///
    /// 🔴 **Written because nothing in this tool could tell a refusal from an outage.** Every call
    /// went through `EnsureSuccessStatusCode()`, which turns 429, 404 and a dead socket into the
    /// same exception and the same sentence. So a caller could not slow down when told to slow
    /// down, could not fall back when an endpoint did not exist, and could not say which had
    /// happened.
    ///
    /// Null when no answer came back — a timeout, a refused connection, a proxy that dropped it.
    /// </summary>
    public System.Net.HttpStatusCode? LastStatus { get; private set; }

    /// <summary>Whether the last call was refused for asking too often, rather than failing.</summary>
    public bool WasRateLimited => LastStatus == (System.Net.HttpStatusCode)429;

    /// <summary>
    /// Community translations for a Steam app id. Never throws: a failed search is a missing
    /// convenience, not a reason to block an install. It is recorded in <see cref="LastError"/>.
    /// </summary>
    /// <param name="targetLanguage">
    /// A language NAME as the API expects it ("French"), or null for every language.
    ///
    /// Filtered by the server rather than here, and not only out of tidiness: the search returns
    /// at most fifty results after ranking, so on a heavily translated game the French ones could
    /// fall outside a top-fifty taken across all languages. Asking the server for French gets the
    /// top fifty French.
    /// </param>
    /// <param name="sourceLanguage">Same, for the language translated FROM.</param>
    /// <param name="apiToken">
    /// 🔴 **Optional, and NOT for permission — for `user_vote`.** The listing is public and answers
    /// perfectly well without a name; what it cannot do without one is say whether YOU have already
    /// rated each translation. `routes/api.php` states it outright: "the caller sends one so the
    /// response can carry that user's own vote."
    ///
    /// ⚠ Leaving it out is invisible until somebody votes. The arrows then draw in the neutral
    /// tone whatever this account did, so a person who has already voted is shown an unvoted
    /// control, clicks it, and WITHDRAWS the vote they meant to confirm. That is what happened.
    /// </param>
    public async Task<IReadOnlyList<OnlineTranslation>> SearchBySteamIdAsync(
        string steamAppId, string? targetLanguage = null, string? sourceLanguage = null,
        string? apiToken = null, CancellationToken ct = default)
    {
        LastError = null;
        LastStatus = null;
        if (string.IsNullOrWhiteSpace(steamAppId)) return Array.Empty<OnlineTranslation>();

        var url = $"{BuildInfo.ApiBaseUrl}/translations?steam_id={Uri.EscapeDataString(steamAppId)}"
                + LanguageFilters(targetLanguage, sourceLanguage);

        try
        {
            var json = await GetAsync(url, apiToken, ct).ConfigureAwait(false);
            var results = Parse(json, out var parseError);
            if (parseError is not null) LastError = parseError;
            return results;
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return Array.Empty<OnlineTranslation>();
        }
    }

    /// <summary>
    /// What the site knows about a game somebody is about to publish under.
    ///
    /// ⚠ Ranked by the caller through <see cref="Common.GameCandidates"/>, never here: the order
    /// is the same decision in every product. This only carries what the server said.
    /// </summary>
    /// <param name="Source">Where the site found it: "local" (its own catalogue), "steam", "igdb", "rawg".</param>
    public sealed record GameCandidate(int Id, string? Name, string? SteamId, string? Source,
                                       int TranslationsCount);

    /// <summary>
    /// The games the site offers for a name or a Steam id — its own catalogue first, then the
    /// stores and the game databases. The same endpoint the mod asks before a first upload.
    ///
    /// ⚠ Null when the question could not be asked, which is not an empty answer: a caller falls
    /// back on what this machine detected, and says so, rather than on "no such game".
    /// </summary>
    public async Task<IReadOnlyList<GameCandidate>?> SearchGamesAsync(string? query, string? steamId,
                                                                      CancellationToken ct = default)
    {
        LastError = null;
        LastStatus = null;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query)) parts.Add("q=" + Uri.EscapeDataString(query.Trim()));
        if (!string.IsNullOrWhiteSpace(steamId)) parts.Add("steam_id=" + Uri.EscapeDataString(steamId.Trim()));
        if (parts.Count == 0) return Array.Empty<GameCandidate>();

        var url = $"{BuildInfo.ApiBaseUrl}/games/search?{string.Join("&", parts)}";

        try
        {
            var json = await GetAsync(url, null, ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("games", out var games)
                || games.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<GameCandidate>();
            }

            var found = new List<GameCandidate>();
            foreach (var game in games.EnumerateArray())
            {
                if (game.ValueKind != JsonValueKind.Object) continue;

                found.Add(new GameCandidate(
                    game.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) ? number : 0,
                    Text(game, "name"),
                    // A number on one source, a string on another: read either way.
                    game.TryGetProperty("steam_id", out var steam)
                        ? steam.ValueKind == JsonValueKind.Number ? steam.GetRawText() : steam.GetString()
                        : null,
                    Text(game, "source"),
                    game.TryGetProperty("translations_count", out var count) && count.TryGetInt32(out var n) ? n : 0));
            }

            return found;
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>One game to ask about: how to find it, and what this machine already holds of it.</summary>
    /// <param name="Key">The caller's own key, handed back untouched so answers can be matched up.</param>
    /// <param name="Uuid">
    /// The lineage of the file installed here, when there is one. It is what lets the server
    /// resolve a translation that has left the CATALOGUE but is still the one this game runs —
    /// a Main delisted for holding no translated line is out of every listing and still the Main.
    /// </param>
    public sealed record GameLookup(string Key, string? SteamId, string? Name, string? Uuid);

    /// <summary>What the site knows about one game we asked about.</summary>
    /// <param name="Translations">
    /// Every published translation of it — complete, not a top-N. Empty when nothing is published.
    /// </param>
    /// <param name="Matching">
    /// The published entry of the lineage this game runs, even when it is out of the listings.
    /// Null when we sent no uuid, or nothing of that lineage is published.
    /// </param>
    /// <param name="Ambiguous">
    /// Several games carry this name and none matches it exactly, so what came back describes more
    /// than one game. ⚠ Nothing is dropped — the caller is told, rather than being handed a pile.
    /// </param>
    public sealed record GameCatalog(IReadOnlyList<OnlineTranslation> Translations,
                                     OnlineTranslation? Matching,
                                     bool Ambiguous);

    /// <summary>
    /// Everything the site knows about a whole library, in ONE request.
    ///
    /// 🔴 **Why it exists.** The per-game search is the only call that grows with the library: one
    /// request per game, against an endpoint that allows sixty a minute per IP. Under sixty games
    /// it passes by luck; past that the rest are refused, nothing is cached for them, and the next
    /// launch repeats it identically — a game past the sixtieth never gets an answer at all.
    ///
    /// ⚠ **Null means the site does not have this endpoint** (404), which is an ordinary state:
    /// somebody self-hosting an older site with a newer Manager. The caller falls back to asking
    /// game by game. Null is NOT "nothing published" and never may be read as such.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, GameCatalog>?> ForGamesAsync(
        IReadOnlyList<GameLookup> games, string? apiToken = null, CancellationToken ct = default)
    {
        LastError = null;
        LastStatus = null;

        if (games.Count == 0) return new Dictionary<string, GameCatalog>();

        var body = new StringBuilder("{\"games\":[");
        for (var i = 0; i < games.Count; i++)
        {
            if (i > 0) body.Append(',');
            body.Append('{');

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(games[i].SteamId))
                parts.Add($"\"steam_id\":{JsonSerializer.Serialize(games[i].SteamId)}");
            if (!string.IsNullOrWhiteSpace(games[i].Name))
                parts.Add($"\"name\":{JsonSerializer.Serialize(games[i].Name)}");
            if (!string.IsNullOrWhiteSpace(games[i].Uuid))
                parts.Add($"\"uuid\":{JsonSerializer.Serialize(games[i].Uuid)}");

            body.Append(string.Join(",", parts)).Append('}');
        }
        body.Append("]}");

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{BuildInfo.ApiBaseUrl}/translations/for-games")
            {
                Content = new StringContent(body.ToString(), System.Text.Encoding.UTF8,
                                            "application/json"),
            };

            if (!string.IsNullOrWhiteSpace(apiToken))
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            LastStatus = response.StatusCode;

            // The one status that is not a failure: this site is older than this Manager.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseBatch(json, games, out var error) is { } parsed && error is null
                ? parsed
                : Failed(error);
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return new Dictionary<string, GameCatalog>();
        }

        IReadOnlyDictionary<string, GameCatalog> Failed(string? error)
        {
            LastError = error;
            return new Dictionary<string, GameCatalog>();
        }
    }

    /// <summary>
    /// Reads one batch answer, keyed back onto what the caller asked.
    ///
    /// ⚠ **Which game, when a name matches several.** The exact name wins; failing that, a single
    /// candidate wins. Only when several candidates match loosely and none exactly is everything
    /// kept — flagged as ambiguous rather than silently attributed to one game. That last case is
    /// what the old flat search did for ALL of them, which is how a translation of another game
    /// could be offered for install here.
    /// </summary>
    private static Dictionary<string, GameCatalog>? ParseBatch(
        string json, IReadOnlyList<GameLookup> asked, out string? error)
    {
        error = null;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                error = "unexpected response shape (no results array)";
                return null;
            }

            var answers = new Dictionary<string, GameCatalog>(StringComparer.OrdinalIgnoreCase);
            var index = 0;

            foreach (var result in results.EnumerateArray())
            {
                // Answered in the order asked — the server promises one result per entry.
                if (index >= asked.Count) break;
                var lookup = asked[index++];

                OnlineTranslation? matching = null;
                if (result.TryGetProperty("matching", out var m) && m.ValueKind == JsonValueKind.Object)
                    matching = Read(m);

                var groups = result.TryGetProperty("games", out var g)
                             && g.ValueKind == JsonValueKind.Array
                    ? g.EnumerateArray().ToList()
                    : new List<JsonElement>();

                var chosen = Choose(groups, lookup.Name, out var ambiguous);

                var translations = new List<OnlineTranslation>();
                foreach (var group in chosen)
                {
                    if (!group.TryGetProperty("translations", out var rows)
                        || rows.ValueKind != JsonValueKind.Array) continue;

                    foreach (var row in rows.EnumerateArray())
                    {
                        if (Read(row) is { } translation) translations.Add(translation);
                    }
                }

                answers[lookup.Key] = new GameCatalog(translations, matching, ambiguous);
            }

            return answers;
        }
        catch (Exception ex)
        {
            error = Net.Http.Describe(ex, "the community site");
            return null;
        }
    }

    /// <summary>
    /// Which of the games that came back the caller actually asked about.
    ///
    /// ⚠ **The rule is the socle's** (<see cref="UnityGameTranslator.Common.GameNames"/>), because
    /// the mod faces the same question about its own game — and two answers to "which game is this
    /// file for" would eventually disagree about where a file gets written. Reading names out of
    /// JSON is all that belongs here.
    /// </summary>
    private static List<JsonElement> Choose(List<JsonElement> groups, string? name, out bool ambiguous)
    {
        var names = groups.Select(group =>
            group.TryGetProperty("game", out var game)
            && game.TryGetProperty("name", out var found)
                ? found.GetString() ?? ""
                : "").ToList();

        var outcome = UnityGameTranslator.Common.GameNames.Which(names, name!);
        ambiguous = outcome.Ambiguous;

        return outcome.Chosen.Select(i => groups[i]).ToList();
    }

    /// <summary>One translation row, with the uploader normalised as <see cref="Parse"/> does.</summary>
    private static OnlineTranslation? Read(JsonElement element)
    {
        var item = element.Deserialize<OnlineTranslation>(JsonOptions);
        if (item is null) return null;

        if (item.Author is null && element.TryGetProperty("uploader", out var uploader))
        {
            item.Author = uploader.ValueKind switch
            {
                JsonValueKind.String => uploader.GetString(),
                JsonValueKind.Object when uploader.TryGetProperty("name", out var name)
                    => name.GetString(),
                _ => null,
            };
        }

        return item;
    }

    /// <summary>
    /// A public GET that carries a name when we have one.
    ///
    /// ⚠ Not authentication: nothing here is refused without it. It only lets the server add what
    /// it can only know for a named caller — this account's own vote on each translation.
    /// </summary>
    private async Task<string> GetAsync(string url, string? apiToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrWhiteSpace(apiToken))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        // ⚠ Recorded BEFORE the throw, or the status is lost with the exception — which is exactly
        // how "we were refused for asking too often" became indistinguishable from "the network is
        // down" everywhere in this tool.
        LastStatus = response.StatusCode;
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Community translations for a game we have no Steam id for — Epic, GOG, anything installed
    /// by hand. Without this most of a library is invisible to the catalog: the id only exists
    /// for Steam, so games bought elsewhere would read "no translation yet" for ever.
    ///
    /// Uses q=, the same parameter the mod uses (ApiClient, the branch taken when there is no
    /// Steam id). That matters more than it looks: the endpoint offers three different matchers
    /// — steam_id exact, game= exact on the slug, q= a LIKE on the name — and picking a
    /// different one would make the installer and the mod disagree about the very same game.
    /// </summary>
    public async Task<IReadOnlyList<OnlineTranslation>> SearchByNameAsync(
        string gameName, string? targetLanguage = null, string? sourceLanguage = null,
        string? apiToken = null, CancellationToken ct = default)
    {
        LastError = null;
        LastStatus = null;
        var name = gameName.Trim();
        if (name.Length < 2) return Array.Empty<OnlineTranslation>();

        var url = $"{BuildInfo.ApiBaseUrl}/translations?q={Uri.EscapeDataString(name)}"
                + LanguageFilters(targetLanguage, sourceLanguage);

        try
        {
            var json = await GetAsync(url, apiToken, ct).ConfigureAwait(false);
            var results = Parse(json, out var parseError);
            if (parseError is not null) LastError = parseError;
            return results;
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return Array.Empty<OnlineTranslation>();
        }
    }

    /// <summary>
    /// The endpoint wraps its results in a "translations" envelope alongside a count. A bare
    /// array and a "data" envelope are also accepted, so a future pagination change does not
    /// silently reduce every search to zero results.
    /// </summary>
    private static IReadOnlyList<OnlineTranslation> Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var array = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("translations", out var t) => t,
                JsonValueKind.Object when root.TryGetProperty("data", out var data) => data,
                _ => default,
            };

            if (array.ValueKind != JsonValueKind.Array)
            {
                error = "unexpected response shape (no translations array)";
                return Array.Empty<OnlineTranslation>();
            }

            var results = new List<OnlineTranslation>();
            foreach (var element in array.EnumerateArray())
            {
                var item = element.Deserialize<OnlineTranslation>(JsonOptions);
                if (item is null) continue;

                // The uploader is a plain string today but an object in some older payloads.
                // Normalising here keeps every caller from wondering which it got.
                if (item.Author is null && element.TryGetProperty("uploader", out var uploader))
                {
                    item.Author = uploader.ValueKind switch
                    {
                        JsonValueKind.String => uploader.GetString(),
                        JsonValueKind.Object when uploader.TryGetProperty("name", out var name)
                            => name.GetString(),
                        _ => null,
                    };
                }

                results.Add(item);
            }
            return results;
        }
        catch (Exception ex)
        {
            error = Net.Http.Describe(ex, "the community site");
            return Array.Empty<OnlineTranslation>();
        }
    }

    /// <summary>
    /// Fetches a translation file, as JSON text, exactly as the server holds it.
    ///
    /// Returned as text rather than parsed on purpose: this file belongs to the mod, and whatever
    /// keys it carries — including ones this tool has never heard of — must reach the game
    /// byte for byte. Deserialising and re-serialising would quietly drop them.
    ///
    /// <paramref name="apiToken"/> is optional and only matters for a branch: the endpoint is
    /// public for everything published, and resolves the caller when a token is sent so its
    /// author can fetch their own work.
    /// </summary>
    public async Task<string?> DownloadAsync(int translationId, string? apiToken = null,
                                             CancellationToken ct = default)
    {
        LastError = null;
        LastStatus = null;

        try
        {
            var url = $"{BuildInfo.ApiBaseUrl}/translations/{translationId}/download";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (!string.IsNullOrWhiteSpace(apiToken))
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // Said in the terms that actually apply, rather than as a bare 403: this is what a
                // branch belonging to somebody else answers.
                LastError = "This translation is a private branch: only its author and the owner "
                          + "of the main version can fetch it.";
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                LastError = $"The server answered {(int)response.StatusCode}.";
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // A truncated transfer produces text that is not a JSON object, and writing that over
            // somebody's translation would be worse than not downloading at all.
            if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{'))
            {
                LastError = "The server sent something that is not a translation file. "
                          + "Nothing was written.";
                return null;
            }

            return text;
        }
        catch (Exception ex)
        {
            LastError = Net.Http.Describe(ex, "the community site");
            return null;
        }
    }

    /// <summary>
    /// The lang and source_lang parameters, or nothing when neither is set.
    ///
    /// ⚠ Both take a language NAME, not an ISO code — the API compares them to the stored
    /// target_language and source_language, which the mod writes as names. Sending "fr" here
    /// silently matches nothing at all, which is indistinguishable from "no translation exists".
    /// </summary>
    private static string LanguageFilters(string? targetLanguage, string? sourceLanguage)
    {
        var filters = "";

        if (!string.IsNullOrWhiteSpace(targetLanguage))
            filters += $"&lang={Uri.EscapeDataString(targetLanguage)}";

        if (!string.IsNullOrWhiteSpace(sourceLanguage))
            filters += $"&source_lang={Uri.EscapeDataString(sourceLanguage)}";

        return filters;
    }


}
