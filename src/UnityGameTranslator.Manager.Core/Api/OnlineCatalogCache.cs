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
    /// only its name, which the endpoint matches loosely. Same split the mod makes.
    /// </summary>
    public static string KeyFor(GameInstall game) =>
        game.SteamAppId is { Length: > 0 } id
            ? $"steam:{id}"
            : $"name:{game.Name.Trim().ToLowerInvariant()}";

    /// <summary>Whatever we already know, without asking anyone. Null when never fetched.</summary>
    public IReadOnlyList<OnlineTranslation>? Peek(GameInstall game) =>
        _entries.TryGetValue(KeyFor(game), out var entry) ? entry.Translations : null;

    public bool IsStale(string key) =>
        !_entries.TryGetValue(key, out var entry)
        || DateTimeOffset.UtcNow - entry.FetchedAt > _lifetime;

    /// <summary>
    /// Refreshes the games whose answers are missing or old, a few at a time, calling back as
    /// each one lands so rows can fill in progressively instead of after everything.
    /// </summary>
    public async Task RefreshAsync(IEnumerable<string> keys,
                                   Func<string, IReadOnlyList<OnlineTranslation>, Task> onUpdated,
                                   CancellationToken ct = default)
    {
        var pending = keys.Distinct().Where(IsStale).ToList();
        if (pending.Count == 0) return;

        var changed = false;

        foreach (var key in pending)
        {
            if (ct.IsCancellationRequested) break;

            var value = key[(key.IndexOf(':') + 1)..];
            var translations = key.StartsWith("steam:", StringComparison.Ordinal)
                ? await _api.SearchBySteamIdAsync(value, ct: ct).ConfigureAwait(false)
                : await _api.SearchByNameAsync(value, ct: ct).ConfigureAwait(false);

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

            // The public endpoint is rate limited. Pacing here keeps a large library from
            // spending its allowance in the first two seconds and getting refused the rest.
            await Task.Delay(120, ct).ConfigureAwait(false);
        }

        if (changed) Save();
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
