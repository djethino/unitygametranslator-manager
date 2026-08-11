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
            ForeignPluginCount = CountForeignPlugins(gamePath, best),
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

        return PeFile.ReadFileVersion(path);
    }

    /// <summary>
    /// How many other mods live alongside ours. Used to refuse removing a loader that someone
    /// else's mods still depend on — the single most damaging thing an uninstaller can do.
    /// </summary>
    private static int CountForeignPlugins(string gamePath, LoaderDescriptor descriptor)
    {
        var pluginRoot = descriptor.PluginDirShared
            ? Path.Combine(gamePath, descriptor.PluginDir)
            : Path.GetDirectoryName(Path.Combine(gamePath, descriptor.PluginDir));

        if (pluginRoot is null || !Directory.Exists(pluginRoot)) return 0;

        try
        {
            var count = 0;

            foreach (var file in Directory.EnumerateFiles(pluginRoot, "*.dll"))
            {
                if (IsOurs(Path.GetFileName(file))) continue;
                count++;
            }

            if (!descriptor.PluginDirShared)
            {
                foreach (var dir in Directory.EnumerateDirectories(pluginRoot))
                {
                    if (IsOurs(Path.GetFileName(dir))) continue;
                    count++;
                }
            }

            return count;
        }
        catch
        {
            // Unreadable folder: report "unknown" as zero would be a lie in the dangerous
            // direction, so report one to keep the uninstall conservative.
            return 1;
        }
    }

    private static bool IsOurs(string name) =>
        name.StartsWith("UnityGameTranslator", StringComparison.OrdinalIgnoreCase);
}
