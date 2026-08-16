using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Recognises a mod loader already installed in a game folder, using the catalog rules rather
/// than anything hardcoded.
///
/// This matters more than it looks. BepInEx 5 and 6 share winhttp.dll and doorstop_config.ini
/// and differ only by which preloader assembly sits in BepInEx/core — and BepInEx 6 renamed
/// the keys inside doorstop_config.ini, so mistaking one for the other produces a game that
/// starts without the loader and no error anywhere.
/// </summary>
public static class LoaderProbe
{
    /// <summary>Returns the loader installed in this game, or null when there is none.</summary>
    public static DetectedLoader? Detect(string gamePath, LoaderCatalogDocument catalog)
    {
        LoaderDescriptor? best = null;
        var bestScore = -1;

        foreach (var descriptor in catalog.Loaders)
        {
            var score = Match(gamePath, descriptor.Detect);
            if (score < 0) continue;

            // Ties are broken by catalog preference, so the answer stays controllable from data.
            var weighted = score * 1000 + descriptor.Preference;
            if (weighted > bestScore)
            {
                bestScore = weighted;
                best = descriptor;
            }
        }

        if (best is null) return null;

        return new DetectedLoader
        {
            Id = best.Id,
            Display = best.Display,
            Version = ReadVersion(gamePath, best),
            PluginDir = best.PluginDir,
            ForeignMods = FindForeignMods(gamePath, best),
        };
    }

    /// <summary>
    /// Returns how many required entries matched, or -1 when the rules exclude this loader.
    /// </summary>
    private static int Match(string gamePath, LoaderDetect detect)
    {
        foreach (var forbidden in detect.None)
        {
            if (Exists(gamePath, forbidden)) return -1;
        }

        var score = 0;
        foreach (var required in detect.All)
        {
            if (!Exists(gamePath, required)) return -1;
            score++;
        }

        if (detect.Any.Count > 0)
        {
            var anyMatched = detect.Any.Any(entry => Exists(gamePath, entry));
            if (!anyMatched) return -1;
            score++;
        }

        // Rules that require nothing must not match every game on the disk.
        return detect.All.Count == 0 && detect.Any.Count == 0 ? -1 : score;
    }

    /// <summary>A rule entry matches a file or a directory — loaders ship both.</summary>
    private static bool Exists(string gamePath, string relative)
    {
        var full = Path.Combine(gamePath, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) || Directory.Exists(full);
    }

    private static string? ReadVersion(string gamePath, LoaderDescriptor descriptor)
    {
        if (string.IsNullOrEmpty(descriptor.Detect.VersionFile)) return null;

        var path = Path.Combine(gamePath,
            descriptor.Detect.VersionFile.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;

        // 🔴 ProductVersion first, and it is not a preference — it is the only field that can tell
        // two loader builds apart. BepInEx 6 stamps FileVersion 6.0.0.0 on every Bleeding Edge
        // build there has ever been, so reading that made every one of them look identical and no
        // update could ever be announced. ProductVersion carries "6.0.0-be.697".
        //
        // ⚠ It is better on the others too, not merely harmless: MelonLoader reads "0.7.3" instead
        // of "0.7.3.0" — the string the catalogue itself uses — and BepInEx 5 reads the same
        // "5.4.23.4" either way.
        //
        // FileVersion stays as the fallback for anything that ships without a ProductVersion.
        return PeFile.ReadProductVersion(path) ?? PeFile.ReadFileVersion(path);
    }

    /// <summary>
    /// Everything living alongside our mod that is not ours, named rather than counted.
    ///
    /// Used to refuse removing a loader that someone else's mods still depend on — the single
    /// most damaging thing an uninstaller can do.
    ///
    /// ⚠ **Named, because a number cannot be checked.** "2 other mods still use it" leaves the
    /// reader to guess which, and a refusal nobody can verify reads as the tool being difficult.
    /// The dialogue lists them.
    ///
    /// ⚠ **Both roots, and how many there are depends on the loader.** BepInEx keeps plugins and
    /// their data in one tree; MelonLoader separates Mods/ from UserData/, and looking only at
    /// Mods/ missed every mod whose presence shows in its data folder. The two roots come from the
    /// catalog — plugin_dir and userdata_dir — never from names written here.
    ///
    /// ⚠ **BepInEx/config/ is deliberately NOT consulted**, although it holds other mods'
    /// settings. A removed mod leaves its .cfg behind for good, so counting those would refuse to
    /// remove a loader because of a mod that left six months ago. A mod must be in plugins/ to
    /// run, and running is what the question is about.
    /// </summary>
    private static IReadOnlyList<string> FindForeignMods(string gamePath, LoaderDescriptor descriptor)
    {
        var roots = new List<string>();

        Add(descriptor.PluginDir, descriptor.PluginDirShared);

        // Only when it is somewhere else entirely: under BepInEx the two are the same folder, and
        // scanning it twice would report every neighbour as two mods.
        if (!string.IsNullOrWhiteSpace(descriptor.UserDataDir)
            && !PathsAgree(descriptor.UserDataDir, descriptor.PluginDir))
        {
            Add(descriptor.UserDataDir, shared: false);
        }

        var found = new List<string>();

        foreach (var root in roots)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.dll"))
                {
                    if (!IsOurs(Path.GetFileName(file))) found.Add(Relative(file));
                }

                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (!IsOurs(Path.GetFileName(dir))) found.Add(Relative(dir) + "/");
                }
            }
            catch
            {
                // Unreadable folder: reporting "nothing there" would be a lie in the dangerous
                // direction, so report one unnamed neighbour and stay conservative.
                found.Add(Relative(root) + "/ (could not be read)");
            }
        }

        return found;

        void Add(string relative, bool shared)
        {
            // A folder of our own sits inside the shared root; a shared plugin_dir IS that root.
            var full = shared
                ? Path.Combine(gamePath, relative)
                : Path.GetDirectoryName(Path.Combine(gamePath, relative));

            if (full is not null && Directory.Exists(full) && !roots.Contains(full))
                roots.Add(full);
        }

        string Relative(string full) =>
            Path.GetRelativePath(gamePath, full).Replace('\\', '/');
    }

    /// <summary>Whether two catalog paths name the same place, whatever separators they use.</summary>
    private static bool PathsAgree(string a, string b) =>
        string.Equals(a.Replace('\\', '/').TrimEnd('/'),
                      b.Replace('\\', '/').TrimEnd('/'),
                      StringComparison.OrdinalIgnoreCase);

    private static bool IsOurs(string name) =>
        name.StartsWith("UnityGameTranslator", StringComparison.OrdinalIgnoreCase);
}
