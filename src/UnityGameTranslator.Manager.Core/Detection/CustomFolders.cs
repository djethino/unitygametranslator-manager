using System.Text.Json;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Folders the user told us to look in, remembered between runs.
///
/// This exists because the alternative is guessing. Scanning every drive for folders named
/// "Games" or "Jeux" was tried and removed: such a folder is a personal way of organising a
/// library, not a convention, and guessing means the tool is right on one machine and wrong on
/// the next. Only launcher defaults are automatic; everything else is added once, explicitly,
/// and kept.
/// </summary>
public sealed class CustomFolders
{
    private const string FileName = "folders.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private List<string> _folders = new();

    public CustomFolders(IPlatform platform)
    {
        _path = Path.Combine(platform.UserDataDirectory, FileName);
        Load();
    }

    /// <summary>Folders as recorded, including ones that no longer exist on disk.</summary>
    public IReadOnlyList<string> All => _folders;

    /// <summary>
    /// A folder in the list that has since disappeared — an unplugged drive, a moved library.
    /// Reported rather than silently dropped: forgetting it would be a decision taken on the
    /// user's behalf about something they asked us to remember.
    /// </summary>
    public bool IsMissing(string folder) => !Directory.Exists(folder);

    public bool Add(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (_folders.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase))) return false;

        _folders.Add(full);
        Save();
        return true;
    }

    public bool Remove(string folder)
    {
        var removed = _folders.RemoveAll(
            f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save();
        return removed;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            _folders = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (Reading.Failed(ex))
        {
            // Refusing to start over a corrupt list would be worse than starting without it — but
            // the list is set aside or left alone rather than overwritten by the next save.
            _leftInPlace = Reading.Unreadable(_path, ex, "CustomFolders.Load") == Reading.OwnFile.LeftInPlace;
            _folders = new List<string>();
        }
    }

    /// <summary>The file could not be opened and is most likely intact: never written this run.</summary>
    private bool _leftInPlace;

    private void Save()
    {
        // Kept for this run, never written over the file it could not read (the window says so).
        if (_leftInPlace)
        {
            Journal.Note("CustomFolders.Save", $"{FileName} not written: it could not be read at launch");
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_folders, JsonOptions));
        }
        catch (Exception ex) when (Reading.WriteFailed(ex))
        {
            // Failing to persist must not lose the folder for this session — but it will be gone
            // at the next launch, and that is said.
            Faults.Say("CustomFolders.Save", ex, Sanitize.Path(_path));
        }
    }
}
