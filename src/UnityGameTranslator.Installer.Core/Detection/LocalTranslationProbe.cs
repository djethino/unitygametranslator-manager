using System.Text.Json;
using UnityGameTranslator.Installer.Core.Model;

namespace UnityGameTranslator.Installer.Core.Detection;

/// <summary>
/// Reads what the mod already stores in a game: the translation file and the deployed plugin
/// version.
///
/// Deliberately read-only and shallow. The tool reports that a translation exists and how big
/// it is; it never merges, never rewrites, never resolves a conflict. Three-way merge is the
/// mod's job, it has the ancestor files and the screens for it, and a second implementation
/// here would be a second source of truth for the same thing.
/// </summary>
public static class LocalTranslationProbe
{
    public const string TranslationFileName = "translations.json";
    public const string ConfigFileName = "config.json";
    public const string PluginAssemblyName = "UnityGameTranslator.dll";

    public static LocalTranslation? Read(string gamePath, LoaderDescriptor descriptor)
    {
        var path = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            TranslationFileName);

        if (!File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var entryCount = 0;
            string? uuid = null, gameName = null, steamId = null, sourceHash = null;
            var localChanges = 0;

            foreach (var property in root.EnumerateObject())
            {
                // Metadata keys are underscore-prefixed; everything else is a translated line.
                if (!property.Name.StartsWith('_')) { entryCount++; continue; }

                switch (property.Name)
                {
                    case "_uuid":
                        uuid = property.Value.GetString();
                        break;
                    case "_local_changes" when property.Value.TryGetInt32(out var changes):
                        localChanges = changes;
                        break;
                    case "_source" when property.Value.ValueKind == JsonValueKind.Object:
                        // The hash the mod last synced with. Read here so the tool can tell a
                        // file that is still the server's from one somebody has worked on.
                        if (property.Value.TryGetProperty("hash", out var hash))
                            sourceHash = hash.GetString();
                        break;
                    case "_game" when property.Value.ValueKind == JsonValueKind.Object:
                        if (property.Value.TryGetProperty("name", out var n)) gameName = n.GetString();
                        if (property.Value.TryGetProperty("steam_id", out var s))
                        {
                            steamId = s.ValueKind == JsonValueKind.Number
                                ? s.GetRawText()
                                : s.GetString();
                        }
                        break;
                }
            }

            return new LocalTranslation
            {
                Path = path,
                Uuid = uuid,
                GameName = gameName,
                SteamId = steamId,
                EntryCount = entryCount,
                LocalChanges = localChanges,
                SourceHash = sourceHash,
                LastWrite = File.GetLastWriteTimeUtc(path),
            };
        }
        catch
        {
            // A translation file we cannot parse still exists, and that fact alone must reach
            // the user: reporting "no translation" would invite overwriting it.
            return new LocalTranslation
            {
                Path = path,
                EntryCount = -1,
                LastWrite = File.GetLastWriteTimeUtc(path),
            };
        }
    }

    /// <summary>Where a plugin assembly was found, and whether that is the documented place.</summary>
    public readonly record struct InstalledPlugin(string Directory, string? Version, bool IsCanonical);

    /// <summary>
    /// Finds the deployed plugin, wherever it actually sits.
    ///
    /// The catalog holds the documented location; real installs are not always there. A
    /// Mods/UnityGameTranslator/ subfolder is in use and does load — a MelonLoader 0.7.1 log
    /// reads "Melon Assembly loaded: '.\Mods\UnityGameTranslator\UnityGameTranslator.dll'" — but
    /// only on some setups, and MelonLoader's changelog says why: recursive folder scanning
    /// arrived in 0.6.6, up to 0.7.0 a subfolder was only scanned when it held a manifest.json
    /// ("Removed 'manifest.json' Requirement for Recursive Melon Subfolder scanning", 0.7.1), and
    /// since 0.7.2 a config option can switch subfolder loading off entirely.
    ///
    /// It is a version question, not a Mono/IL2CPP one.
    ///
    /// So both places are searched, because a game whose plugin we miss is reported as not set up
    /// and offers to install a mod that is already running. Where to WRITE is a separate
    /// question, answered by IsCanonical rather than by wherever a copy happens to be.
    /// </summary>
    public static InstalledPlugin? FindInstalledPlugin(string gamePath, LoaderDescriptor descriptor)
    {
        var root = Path.Combine(gamePath, descriptor.PluginDir.Replace('/', Path.DirectorySeparatorChar));

        var candidates = new[] { (Dir: root, Canonical: true),
                                 (Dir: Path.Combine(root, "UnityGameTranslator"), Canonical: false) };

        foreach (var (directory, canonical) in candidates)
        {
            var dll = Path.Combine(directory, PluginAssemblyName);
            if (File.Exists(dll))
                return new InstalledPlugin(directory, PeFile.ReadFileVersion(dll), canonical);
        }

        return null;
    }

    /// <summary>Version of the deployed plugin, or null when it is not installed.</summary>
    public static string? ReadInstalledPluginVersion(string gamePath, LoaderDescriptor descriptor) =>
        FindInstalledPlugin(gamePath, descriptor)?.Version;

    public static bool HasConfig(string gamePath, LoaderDescriptor descriptor) =>
        File.Exists(Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            ConfigFileName));
}
