using System.Text.Json;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>
/// Remembers what the community catalog answered, per game.
///
/// Saying "this game is playable in your language" on every row means knowing, for every game,
/// what exists online. Asking at the moment a row is drawn would be dozens of calls at once
/// against a rate-limited endpoint, and a list that fills in over a minute every single launch.
///
/// So answers are kept on disk and refreshed in the background. A stale answer is shown while
/// the fresh one is on its way, because a slightly old "a French translation exists" is far more
/// useful than an empty row.
/// </summary>
public sealed class OnlineCatalogCache
{
    private const string FileName = "catalog-cache.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class Entry
    {
        public DateTimeOffset FetchedAt { get; set; }
        public List<OnlineTranslation> Translations { get; set; } = new();

        /// <summary>
        /// The published entry of the lineage this game runs, when it is not in the listing above.
        ///
        /// 🔴 **Kept apart, never merged into the list.** A Main delisted for holding no translated
        /// line is out of every catalogue and is still the translation the game is running: folding
        /// it in would count it in "N translations are published for this game", which is the kind
        /// of quiet lie the whole batch rewrite exists to end. Held beside, it answers a different
        /// question — whose file is this, and where does it stand — and answers nothing else.
        /// </summary>
        public OnlineTranslation? Matching { get; set; }
    }

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;
    private readonly CatalogApiClient _api;
    private readonly TimeSpan _lifetime;

    public OnlineCatalogCache(IPlatform platform, CatalogApiClient? api = null, TimeSpan? lifetime = null)
    {
        _path = Path.Combine(platform.UserDataDirectory, FileName);
        _api = api ?? new CatalogApiClient();
        _lifetime = lifetime ?? TimeSpan.FromHours(6);
        _entries = Load();
    }

    /// <summary>
    /// How a game is looked up. Steam hands us an id, which matches exactly; everything else has
    /// only its name. Same split the mod makes.
    ///
    /// ⚠ **The product name before the display name.** `Name` is what the reader sees — a store
    /// manifest, or a folder called "LONESTARuBxQC" — while `ProductName` is what Unity wrote and
    /// what the site records as `unity_name` when somebody publishes. Asking with the display name
    /// is how a game gets looked up under a repack's folder.
    /// </summary>
    public static string KeyFor(GameInstall game) =>
        game.SteamAppId is { Length: > 0 } id
            ? $"steam:{id}"
            : $"name:{(game.ProductName ?? game.Name).Trim().ToLowerInvariant()}";

    /// <summary>Whatever we already know, without asking anyone. Null when never fetched.</summary>
    public IReadOnlyList<OnlineTranslation>? Peek(GameInstall game) =>
        _entries.TryGetValue(KeyFor(game), out var entry) ? entry.Translations : null;

    /// <summary>
    /// The published entry of the lineage this game runs, when the listing does not carry it.
    ///
    /// ⚠ Answers only what the server resolved for the uuid we sent — never a guess, and null on a
    /// site that has no batch endpoint, where nothing was asked.
    /// </summary>
    public OnlineTranslation? PeekMatching(GameInstall game) =>
        _entries.TryGetValue(KeyFor(game), out var entry) ? entry.Matching : null;

    public bool IsStale(string key) =>
        !_entries.TryGetValue(key, out var entry)
        || DateTimeOffset.UtcNow - entry.FetchedAt > _lifetime;

    /// <summary>
    /// How many games one batch asks about. The server refuses more, and answers COMPLETELY for
    /// every one it accepts — so this is a transport size, never a truncation.
    /// </summary>
    private const int GamesPerBatch = 100;

    /// <summary>
    /// Refreshes the games whose answers are missing or old, calling back as answers land so rows
    /// can fill in progressively instead of after everything.
    ///
    /// 🔴 **In batches, and this is the only call in the tool that grew with the library.** It was
    /// one request per game, 120 ms apart — a cadence of ~500 a minute against an endpoint that
    /// allows sixty per IP. Under sixty games it passed by luck; past that the rest came back
    /// refused, nothing was cached for them (a failure is not an empty catalogue), and the next
    /// launch repeated it identically, so a game past the sixtieth never got an answer.
    ///
    /// ⚠ **The one-by-one path stays**, and is not dead code: it is what runs against a site that
    /// predates the batch endpoint — somebody self-hosting, with a newer Manager.
    /// </summary>
    /// <param name="lineageOf">
    /// The uuid of the file installed for a game, when there is one. It lets the server resolve a
    /// translation that has left the catalogue and is still the one that game runs.
    /// </param>
    public async Task RefreshAsync(IEnumerable<string> keys,
                                   Func<string, IReadOnlyList<OnlineTranslation>, Task> onUpdated,
                                   CancellationToken ct = default,
                                   Func<string, string?>? lineageOf = null)
    {
        var pending = keys.Distinct().Where(IsStale).ToList();
        if (pending.Count == 0) return;

        var changed = false;

        for (var from = 0; from < pending.Count; from += GamesPerBatch)
        {
            if (ct.IsCancellationRequested) break;

            var slice = pending.Skip(from).Take(GamesPerBatch).ToList();

            var lookups = slice.Select(key =>
            {
                var value = key[(key.IndexOf(':') + 1)..];
                var steam = key.StartsWith("steam:", StringComparison.Ordinal);

                return new CatalogApiClient.GameLookup(
                    key, steam ? value : null, steam ? null : value, lineageOf?.Invoke(key));
            }).ToList();

            var answers = await _api.ForGamesAsync(lookups, ct: ct).ConfigureAwait(false);

            // Null is the site saying it has no such endpoint — not a failure, and not an empty
            // catalogue. Everything left goes through the old road, once.
            if (answers is null)
            {
                if (await RefreshOneByOneAsync(pending.Skip(from), onUpdated, ct).ConfigureAwait(false))
                    changed = true;
                break;
            }

            if (_api.LastError is not null) break;

            foreach (var key in slice)
            {
                if (!answers.TryGetValue(key, out var answer)) continue;

                _entries[key] = new Entry
                {
                    FetchedAt = DateTimeOffset.UtcNow,
                    Translations = answer.Translations.ToList(),

                    // Only when the listing does not already carry it: held twice, the two copies
                    // would answer separately the day one of them goes stale.
                    Matching = answer.Matching is { Uuid.Length: > 0 } resolved
                               && !answer.Translations.Any(t => string.Equals(
                                   t.Uuid, resolved.Uuid, StringComparison.OrdinalIgnoreCase))
                        ? resolved
                        : null,
                };
                changed = true;

                await onUpdated(key, answer.Translations).ConfigureAwait(false);
            }
        }

        if (changed) Save();
    }

    /// <summary>
    /// One request per game — what this class did for every site, and now only for one that does
    /// not carry the batch endpoint.
    /// </summary>
    /// <summary>
    /// How many games are asked about at speed before the pace drops to one a second.
    ///
    /// 🔴 **Derived from the endpoint's own limit, not chosen.** `GET /translations` allows sixty a
    /// minute per address (`throttle:60,1` in routes/api.php), and that address is shared with the
    /// mod running inside a game and with every other machine behind the same connection. Below
    /// this many games nothing changes — a library of forty still fills in seconds — and past it
    /// the pace settles at one a second, which cannot exhaust the allowance whatever the size.
    ///
    /// ⚠ Five short of sixty on purpose: the whole budget is not ours to spend.
    /// </summary>
    private const int BurstBeforePacing = 55;

    private async Task<bool> RefreshOneByOneAsync(
        IEnumerable<string> keys,
        Func<string, IReadOnlyList<OnlineTranslation>, Task> onUpdated,
        CancellationToken ct)
    {
        var changed = false;
        var asked = 0;

        foreach (var key in keys)
        {
            if (ct.IsCancellationRequested) break;
            asked++;

            var value = key[(key.IndexOf(':') + 1)..];
            var translations = key.StartsWith("steam:", StringComparison.Ordinal)
                ? await _api.SearchBySteamIdAsync(value, ct: ct).ConfigureAwait(false)
                : await _api.SearchByNameAsync(value, ct: ct).ConfigureAwait(false);

            // 🔴 **Refused, not broken — and it must stop the sweep.** Carrying on hammers a server
            // that has just said no, at eight requests a second, for every game left. Nothing is
            // cached either way, so the next launch asks again; what changes is that we stop asking
            // now.
            if (_api.WasRateLimited) break;

            // A failed lookup is not an empty catalog. Recording it as "no translations" would
            // turn one blocked request into a wrong answer that survives for hours.
            if (_api.LastError is not null) continue;

            _entries[key] = new Entry
            {
                FetchedAt = DateTimeOffset.UtcNow,
                Translations = translations.ToList(),
            };
            changed = true;

            await onUpdated(key, translations).ConfigureAwait(false);

            // ⚠ Fast while there is budget, then paced — rather than one flat figure, which would
            // have to be wrong one way or the other. The 120 ms this always waited is a cadence of
            // five hundred a minute against an allowance of sixty: fine for the first fifty-five
            // games and a wall for everything after them. Slowing everybody to one a second instead
            // would turn a library of forty from seven seconds into forty.
            await Task.Delay(asked < BurstBeforePacing ? 120 : 1000, ct).ConfigureAwait(false);
        }

        return changed;
    }

    private Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                    File.ReadAllText(_path), JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // A damaged cache costs one refresh, nothing more.
        }
        return new Dictionary<string, Entry>();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Not persisting only means asking again next time.
        }
    }
}
