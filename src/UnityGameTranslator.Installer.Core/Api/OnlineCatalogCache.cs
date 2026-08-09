using System.Text.Json;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;

namespace UnityGameTranslator.Installer.Core.Api;

/// <summary>
/// Remembers what the community catalog answered, per Steam app id.
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

    /// <summary>Whatever we already know, without asking anyone. Null when never fetched.</summary>
    public IReadOnlyList<OnlineTranslation>? Peek(string? steamAppId) =>
        steamAppId is not null && _entries.TryGetValue(steamAppId, out var entry)
            ? entry.Translations
            : null;

    public bool IsStale(string steamAppId) =>
        !_entries.TryGetValue(steamAppId, out var entry)
        || DateTimeOffset.UtcNow - entry.FetchedAt > _lifetime;

    /// <summary>
    /// Refreshes the games whose answers are missing or old, a few at a time, calling back as
    /// each one lands so rows can fill in progressively instead of after everything.
    /// </summary>
    public async Task RefreshAsync(IEnumerable<string> steamAppIds,
                                   Func<string, IReadOnlyList<OnlineTranslation>, Task> onUpdated,
                                   CancellationToken ct = default)
    {
        var pending = steamAppIds.Where(IsStale).Distinct().ToList();
        if (pending.Count == 0) return;

        var changed = false;

        foreach (var appId in pending)
        {
            if (ct.IsCancellationRequested) break;

            var translations = await _api.SearchBySteamIdAsync(appId, ct).ConfigureAwait(false);

            // A failed lookup is not an empty catalog. Recording it as "no translations" would
            // turn one blocked request into a wrong answer that survives for hours.
            if (_api.LastError is not null) continue;

            _entries[appId] = new Entry
            {
                FetchedAt = DateTimeOffset.UtcNow,
                Translations = translations.ToList(),
            };
            changed = true;

            await onUpdated(appId, translations).ConfigureAwait(false);

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
