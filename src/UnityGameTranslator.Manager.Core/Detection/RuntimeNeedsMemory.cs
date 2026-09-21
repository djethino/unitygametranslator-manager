using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// What each game was found to lack, remembered between runs against the stamps of the files it was
/// read from — so that a game nothing has touched is not read again at every launch.
///
/// 🔴 **Why it exists** (measured 2026-09-21): reading what a game lacks parses its class libraries,
/// its engine modules and its player — about three seconds for forty Mono games even in parallel,
/// against 0.6 s for the whole scan before. Games change rarely; their files say when they do.
///
/// ⚠ **The stamp is the size and time of every library read and of the player.** A game update,
/// Steam's "verify files", anything that rewrites one of them gives another stamp, and the game is
/// read again. An answer is never served for files that moved.
///
/// ⚠ A memory, not a source of truth: a file that cannot be read or written is ignored, and the
/// answer is simply worked out again.
/// </summary>
public sealed class RuntimeNeedsMemory
{
    public sealed class Entry
    {
        [JsonPropertyName("stamp")] public string Stamp { get; set; } = "";
        [JsonPropertyName("missing")] public List<string> Missing { get; set; } = new();
        [JsonPropertyName("build")] public string? Build { get; set; }
        [JsonPropertyName("changeset")] public string? Changeset { get; set; }

        /// <summary>The engine modules the build stripped of what the mod calls; null when none.</summary>
        [JsonPropertyName("stripped")] public List<string>? Stripped { get; set; }
        [JsonPropertyName("module_set")] public List<string>? ModuleSet { get; set; }
        [JsonPropertyName("module_build")] public string? ModuleBuild { get; set; }
        [JsonPropertyName("module_changeset")] public string? ModuleChangeset { get; set; }
    }

    private readonly string _path;
    private readonly ConcurrentDictionary<string, Entry> _entries;
    private bool _changed;

    public RuntimeNeedsMemory(string directory)
    {
        _path = Path.Combine(directory, "runtime-needs.json");
        _entries = new ConcurrentDictionary<string, Entry>(Load(_path), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What was remembered for this game, if its files still carry this stamp.</summary>
    public Entry? Find(string gamePath, string stamp) =>
        _entries.TryGetValue(Key(gamePath), out var entry) && entry.Stamp == stamp ? entry : null;

    public void Remember(string gamePath, Entry entry)
    {
        _entries[Key(gamePath)] = entry;
        _changed = true;
    }

    /// <summary>Written once, at the end of a scan — not once per game from parallel threads.</summary>
    public void Save()
    {
        if (!_changed) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Dictionary<string, Entry>(_entries)));
            File.Move(temporary, _path, overwrite: true);
            _changed = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not written: the next launch reads the games again. Nothing is wrong on screen.
        }
    }

    private static string Key(string gamePath) => Path.GetFullPath(gamePath);

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable memory is an empty one: everything is read again, and rewritten.
            return new();
        }
    }
}
