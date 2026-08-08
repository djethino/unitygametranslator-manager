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
            string? uuid = null, gameName = null, steamId = null;
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

    /// <summary>Version of the deployed plugin, or null when it is not installed.</summary>
    public static string? ReadInstalledPluginVersion(string gamePath, LoaderDescriptor descriptor)
    {
        var path = Path.Combine(gamePath,
            descriptor.PluginDir.Replace('/', Path.DirectorySeparatorChar),
            PluginAssemblyName);

        return File.Exists(path) ? PeFile.ReadFileVersion(path) : null;
    }

    public static bool HasConfig(string gamePath, LoaderDescriptor descriptor) =>
        File.Exists(Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            ConfigFileName));
}
