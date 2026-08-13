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
    /// <summary>Where the mod keeps its own files for this game, or null when there is none.</summary>
    public static string? FolderFor(string gamePath, LoaderDescriptor descriptor)
    {
        var folder = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar));

        return Directory.Exists(folder) ? folder : null;
    }

    public static IReadOnlyList<UserDataGroup> Scan(string gamePath, LoaderDescriptor descriptor)
    {
        var folder = FolderFor(gamePath, descriptor);
        if (folder is null) return Array.Empty<UserDataGroup>();

        var translation = new List<UserDataItem>();
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

        Add(groups, "Settings", configuration,
            "Your language, translator and sign-in for this game. The mod asks again from scratch.");

        Add(groups, "Fonts", fonts,
            "Font files and their generated atlases. Rebuilt on demand, so nothing is lost for good.");

        Add(groups, "Replacement images", images,
            "Images you put in place of the game's own. Not recoverable unless you kept a copy.");

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
