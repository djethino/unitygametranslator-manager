using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>One file this game holds, as it would be listed for removal.</summary>
/// <param name="RelativePath">Relative to the mod's data folder, with forward slashes.</param>
public sealed record UserDataItem(string RelativePath, long Bytes);

/// <summary>
/// A set of files that stand or fall together in somebody's mind — a translation and its
/// ancestors, the settings, the fonts.
/// </summary>
/// <param name="Label">What this is, in the words the screen uses.</param>
/// <param name="Consequence">
/// What is actually lost by removing it, in one clause. ⚠ Never "these files will be deleted" —
/// that is visible from the list. The only thing worth writing here is what cannot be got back.
/// </param>
public sealed record UserDataGroup(string Label, string Consequence, IReadOnlyList<UserDataItem> Items)
{
    public long Bytes => Items.Sum(i => i.Bytes);
}

/// <summary>
/// What the mod has written into one game, grouped so a person can decide per kind rather than
/// all-or-nothing.
///
/// ⚠ **Read from disk, never from a list of expected names.** The removal path used to work from
/// four hard-coded file names, so a font pack, a replacement image or a dated backup was simply
/// never removed — "delete my settings and translations" left most of the folder behind, and
/// nothing said so. Scanning also means a file the mod starts writing tomorrow appears here
/// without this class being taught about it.
///
/// ⚠ Nothing here deletes. It describes; <see cref="UninstallEngine"/> acts on what was ticked.
/// </summary>
public static class UserDataInventory
{
    /// <summary>
    /// The folder the mod's data belongs in for this game, whether or not it exists yet — or null
    /// when the catalogue's `userdata_dir` would leave the game.
    ///
    /// 🔴 **Every path composed from `userdata_dir` starts here, readers and writers alike.**
    /// `userdata_dir` comes from the FETCHED catalog — GitHub, then the site mirror, then a cache —
    /// so it is a remote string, and `Path.Combine(gamePath, "../somewhere")` resolves happily
    /// outside the game the user chose. <see cref="FolderFor"/> was the choke point for the
    /// inventory, the backups and the uninstall — while six other classes composed the same path
    /// themselves, so "Apply Mod defaults" and "Take translation" would have written, and "Remove
    /// translation" deleted, wherever a fetched catalogue pointed. The guard held at the border
    /// and not in the middle. Found by the audit of 2026-08-27; `check-path-guard.ps1` refuses a
    /// build that composes the path anywhere but here.
    ///
    /// ⚠ Existence is deliberately NOT checked: writers create the folder. <see cref="FolderFor"/>
    /// is this plus the existence test, for callers that only ever read what is there.
    ///
    /// ⚠ An empty `userdata_dir` is refused too, deliberately. It would resolve to the game root,
    /// and then "the mod's data" would be the whole game.
    /// </summary>
    public static string? DataFolder(string gamePath, LoaderDescriptor descriptor) =>
        new FileOperations(gamePath).TryResolveInsideGame(descriptor.UserDataDir, out var folder)
            ? folder
            : null;

    /// <summary>
    /// What a screen says when <see cref="DataFolder"/> refused. Names the cause — the catalogue,
    /// not the game — because the person can do nothing about the game folder they chose.
    /// </summary>
    public const string OutsideGameRefusal =
        "The loader catalogue places this game's mod data outside the game folder. Nothing was touched.";

    /// <summary>
    /// Where the mod keeps its own files for this game, or null when there is none.
    ///
    /// ⚠ It used to combine without checking, and the per-file guard downstream only verified that
    /// a file sat inside this folder — which says nothing when the folder itself is elsewhere.
    /// Demonstrated: with a catalog naming "../ESCAPED", an uninstall deleted the config and the
    /// fonts from a folder outside the game. The check now lives in <see cref="DataFolder"/>.
    /// </summary>
    public static string? FolderFor(string gamePath, LoaderDescriptor descriptor)
    {
        var folder = DataFolder(gamePath, descriptor);

        return folder is not null && Directory.Exists(folder) ? folder : null;
    }

    public static IReadOnlyList<UserDataGroup> Scan(string gamePath, LoaderDescriptor descriptor)
    {
        var folder = FolderFor(gamePath, descriptor);
        if (folder is null) return Array.Empty<UserDataGroup>();

        var translation = new List<UserDataItem>();
        var setAside = new List<UserDataItem>();
        var configuration = new List<UserDataItem>();
        var fonts = new List<UserDataItem>();
        var images = new List<UserDataItem>();
        var other = new List<UserDataItem>();

        foreach (var path in Enumerate(folder))
        {
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            var item = new UserDataItem(relative, Size(path));

            // ⚠ The plugin itself is not data and never appears here. Under BepInEx it sits in
            // this very folder, and offering it beside the translations would put "the mod" and
            // "your work" behind the same tick.
            if (string.Equals(relative, LocalTranslationProbe.PluginAssemblyName,
                              StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var top = relative.Split('/')[0];

            if (top.StartsWith(LocalTranslationProbe.TranslationFileName, StringComparison.OrdinalIgnoreCase))
                translation.Add(item);
            // 🔴 Its own group, not "Other files". These are TRANSLATIONS — replaced earlier and
            // kept so Restore local can bring one back. Filed as unrecognised, they sat under a
            // label that says "judge them yourself" beside a consequence written for stray files,
            // and somebody clearing what they thought was clutter would have thrown away the only
            // copy of work they had replaced by mistake.
            else if (IsBackup(relative))
                setAside.Add(item);
            else if (string.Equals(top, LocalTranslationProbe.ConfigFileName, StringComparison.OrdinalIgnoreCase))
                configuration.Add(item);
            else if (string.Equals(top, "fonts", StringComparison.OrdinalIgnoreCase))
                fonts.Add(item);
            else if (string.Equals(top, "images", StringComparison.OrdinalIgnoreCase))
                images.Add(item);
            else
                other.Add(item);
        }

        var groups = new List<UserDataGroup>();

        // Translation first, and it is not alphabetical: it is the one thing here that may exist
        // nowhere else, so it is the one somebody must see before ticking anything.
        Add(groups, "Translation", translation,
            "Lines captured while playing may exist nowhere else. Anything never uploaded is gone.");

        // Right after the translation in place, because it is the same thing at an earlier date.
        // ⚠ Named from Common.Backups, not spelled out here: this list and the screen that manages
        // the same folder must call it the same thing, or ticking this reads as removing something
        // else. One word for the thing — see the vocabulary note in Backups.
        Add(groups, Common.Backups.ScreenTitle, setAside,
            "Earlier versions of this game's translation, taken when something replaced them and "
            + $"whenever you asked. {Common.Backups.ScreenTitle} restores one; deleting them ends "
            + "that.");

        Add(groups, "Settings", configuration,
            "Your language, translator and sign-in for this game. The mod asks again from scratch.");

        Add(groups, "Fonts", fonts,
            "Font files and their generated atlases. Rebuilt on demand, so nothing is lost for good.");

        Add(groups, "Replacement images", images,
            "Images you put in place of the game's own. Not recoverable unless you have the "
            + "originals elsewhere.");

        Add(groups, "Other files", other,
            "Written here by the mod or by you. Not recognised, so judge them yourself.");

        return groups;
    }

    /// <summary>
    /// The entries in one folder that the mod itself wrote — by name, never by sweep.
    ///
    /// ⚠ Exists for the misplaced-install case, where the folder to read is SHARED. Under BepInEx
    /// the mod keeps its files beside its own assembly (userdata_dir == plugin_dir), so a copy
    /// dropped straight into plugins/ writes the translation there, among every other mod's files.
    /// Moving the assembly back without them would leave the mod starting from nothing while the
    /// work sat two folders away — which is how "repair" turns into "lost my translation".
    ///
    /// ⚠ Recognised kinds ONLY. Whatever <see cref="Scan"/> files under "Other" is deliberately
    /// left out here: in a shared folder, unrecognised means somebody else's.
    /// </summary>
    public static IReadOnlyList<string> RecognisedDataIn(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<string>();

        var found = new List<string>();

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(entry);

            // The assembly is the thing being moved, not data that travels with it.
            if (string.Equals(name, LocalTranslationProbe.PluginAssemblyName, StringComparison.OrdinalIgnoreCase))
                continue;

            var ours = name.StartsWith(LocalTranslationProbe.TranslationFileName, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, LocalTranslationProbe.ConfigFileName, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, "fonts", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, "images", StringComparison.OrdinalIgnoreCase);

            if (ours) found.Add(name);
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>
    /// Whether one file inside the mod's folder is part of the translation's history.
    ///
    /// 🔴 Its own group, not "Other files". These are TRANSLATIONS — replaced earlier and kept so
    /// one can be restored. Filed as unrecognised, they sat under a label that says "judge them
    /// yourself" beside a consequence written for stray files, and somebody clearing what they
    /// thought was clutter would have thrown away the only copy of work they replaced by mistake.
    ///
    /// ⚠ **Public, and asked rather than re-derived.** The uninstall has to know the same thing —
    /// whether the history is among what is being deleted, because that decides whether it takes a
    /// last backup first. Two spellings of "is this a backup" would eventually disagree, and the
    /// direction they would disagree in is the one that deletes without keeping anything.
    /// </summary>
    public static bool IsBackup(string relativePath)
    {
        var top = relativePath.Replace('\\', '/').Split('/')[0];

        return string.Equals(top, TranslationInstaller.BackupFolderName, StringComparison.OrdinalIgnoreCase)
               // ⚠ And the folder that replaced it. Filed as unrecognised it would sit under
               // "judge them yourself" — the exact trap this closes for `removed/`, reopened by
               // the folder that took its place.
               || string.Equals(top, Common.Backups.FolderName, StringComparison.OrdinalIgnoreCase)
               // The two loose files the mod used to write. Each is a translation; unnamed,
               // somebody clearing what looks like clutter throws away the only copy.
               || top.StartsWith(LocalTranslationProbe.TranslationFileName + ".",
                                 StringComparison.OrdinalIgnoreCase);
    }

    private static void Add(List<UserDataGroup> groups, string label,
                            List<UserDataItem> items, string consequence)
    {
        if (items.Count == 0) return;

        groups.Add(new UserDataGroup(label, consequence,
            items.OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase).ToList()));
    }

    private static IEnumerable<string> Enumerate(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
        }
        catch
        {
            // A folder we cannot walk is reported as empty rather than as an error: the uninstall
            // screen still works, it simply offers nothing to tick.
            return Array.Empty<string>();
        }
    }

    private static long Size(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>Bytes as somebody reads them, for a line that only exists to give a sense of scale.</summary>
    public static string Describe(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}
