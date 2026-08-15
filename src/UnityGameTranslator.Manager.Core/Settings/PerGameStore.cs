using System.Text.Json;
using System.Text.Json.Serialization;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Settings;

/// <summary>
/// Something remembered per game folder, kept in one file beside the tool's own settings.
///
/// Written once and inherited twice rather than copied: two files were already going to hold a
/// dictionary keyed by game path, load it forgivingly, and write it through a temporary file —
/// and the half that is easy to get subtly wrong is the key. A path compared as typed makes
/// "E:\Games\X" and "E:/games/x/" two different games, so a setting saved from one screen would
/// be invisible from another, which reads as the tool forgetting what it was told.
///
/// ⚠ Losing this file must never stop the program. Everything in it can be asked again; not
/// starting cannot be undone by the person it happens to.
/// </summary>
public abstract class PerGameStore<T> where T : class
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    /// <summary>
    /// ⚠ Loaded on first use, NOT in the constructor, and that is not a performance choice.
    ///
    /// <see cref="AfterLoad"/> is virtual, and a virtual call made from a base constructor runs
    /// before the derived class has finished being built — its fields still null, on an object it
    /// has not seen yet. Nothing in this hierarchy holds such a field today, which is exactly why
    /// the trap would be sprung by the first one added rather than by the code that laid it.
    /// Deferring the load puts the call after construction, permanently.
    /// </summary>
    private Dictionary<string, T>? _loaded;

    private Dictionary<string, T> Entries => _loaded ??= Load();

    protected PerGameStore(IPlatform platform, string fileName) =>
        _path = Path.Combine(platform.UserDataDirectory, fileName);

    /// <summary>What is remembered for this game, or null when nothing is.</summary>
    public T? For(string gamePath) =>
        Entries.TryGetValue(Key(gamePath), out var value) ? value : null;

    public void Set(string gamePath, T value)
    {
        BeforeSave(value);
        Entries[Key(gamePath)] = value;
        Save();
    }

    /// <summary>
    /// A last pass over one entry before it is serialised. Does nothing here; a store holding
    /// secrets encrypts them there, so that the only path from plaintext to a file goes through
    /// one place.
    /// </summary>
    protected virtual void BeforeSave(T value) { }

    /// <summary>
    /// A first pass over one entry as it comes off disk, called once per entry on the first read.
    ///
    /// ⚠ It must never throw: one unreadable entry has to cost that entry, not the program. The
    /// whole load is already wrapped for that reason.
    /// </summary>
    protected virtual void AfterLoad(T value) { }

    public void Clear(string gamePath)
    {
        if (Entries.Remove(Key(gamePath))) Save();
    }

    /// <summary>
    /// One canonical spelling of a folder, so the same game is the same key whichever screen or
    /// scanner produced the path.
    /// </summary>
    private static string Key(string gamePath) => Path.GetFullPath(gamePath).ToLowerInvariant();

    private Dictionary<string, T> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, T>>(
                    File.ReadAllText(_path), JsonOptions);

                if (loaded is not null)
                {
                    foreach (var entry in loaded.Values) AfterLoad(entry);
                    return loaded;
                }
            }
        }
        catch
        {
            // Losing these means asking again, which is recoverable; refusing to start is not.
        }

        return new Dictionary<string, T>();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Beside the target, then moved: a file half-written by a crash or a full disk is
            // worse than the old one, because it takes the previous answers with it.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Entries, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Not persisting only costs the answer at the next launch.
        }
    }
}
