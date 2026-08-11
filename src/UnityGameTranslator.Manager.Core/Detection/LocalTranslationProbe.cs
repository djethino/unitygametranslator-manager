using System.Text.Json;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Detection;

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

    /// <summary>
    /// The target language this game is set to, as the mod stores it — a NAME ("French"), not a
    /// code. Null when there is no config yet, or when it says "auto".
    ///
    /// Read because installing a translation into a game aimed at another language leaves the mod
    /// hunting for a language nobody provided: the file would sit there while the mod carried on
    /// trying to translate into something else. Knowing it is what lets us say so.
    /// </summary>
    public static string? ReadTargetLanguage(string gamePath, LoaderDescriptor descriptor) =>
        ReadLanguages(gamePath, descriptor).Target;

    /// <summary>
    /// Both languages this game is set to, as NAMES, or null for either when unset or "auto".
    ///
    /// Read together because they answer one question: what is this game already doing. A screen
    /// that offers translations should open on that, not on a default that ignores the choice
    /// somebody already made here.
    /// </summary>
    public static (string? Source, string? Target) ReadLanguages(string gamePath,
                                                                 LoaderDescriptor descriptor)
    {
        var path = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar), ConfigFileName);

        if (!File.Exists(path)) return (null, null);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            return (Named(root, "source_language"), Named(root, "target_language"));
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The language pair of the file installed in a game, in the same shape the community cards
    /// use: "English → Swedish".
    ///
    /// Written because its absence was actively misleading. A local file showing only "128 entries"
    /// beside a community entry reading "English → Swedish" invites the reader to assume they are
    /// the same pair — and here they were not: the local one detects its source, the published one
    /// is fixed to English.
    ///
    /// "auto" is spelled out rather than hidden: it is a real answer, and the one that explains why
    /// a source filter cannot be preselected from it.
    /// </summary>
    public static string? DescribeLanguages(string gamePath, LoaderDescriptor descriptor)
    {
        var (source, target) = ReadLanguages(gamePath, descriptor);

        // Nothing configured at all: the mod has not been through its first run here, and saying
        // "auto → auto" would dress that up as a choice somebody made.
        if (source is null && target is null) return null;

        return $"{source ?? "auto-detected"} → {target ?? "auto"}";
    }

    /// <summary>One language field, with "auto" and blanks reported as "not set".</summary>
    private static string? Named(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)) return null;

        var language = value.GetString();
        return string.IsNullOrWhiteSpace(language)
               || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language;
    }

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
            int human = 0, validated = 0, ai = 0, captured = 0, skipped = 0;

            foreach (var property in root.EnumerateObject())
            {
                // Metadata keys are underscore-prefixed; everything else is a translated line.
                if (!property.Name.StartsWith('_'))
                {
                    entryCount++;
                    Count(property.Value, ref human, ref validated, ref ai, ref captured, ref skipped);
                    continue;
                }

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
                Counts = new TagCounts(human, validated, ai, captured, skipped),
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

    /// <summary>
    /// Puts one entry in its bucket, following the website's rules to the letter — the reasoning
    /// for each of them is on <see cref="TagCounts"/>, and neither copy may drift from the other.
    /// </summary>
    private static void Count(JsonElement value, ref int human, ref int validated, ref int ai,
                              ref int captured, ref int skipped)
    {
        // The old format is a bare string, and it predates tags entirely: AI is what it was.
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("t", out var tagNode))
        {
            ai++;
            return;
        }

        var tag = tagNode.ValueKind == JsonValueKind.String ? tagNode.GetString() : null;

        var text = value.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

        // Human with nothing in it is a capture: the mod met this text and nobody has dealt with
        // it. Counting it as human work would report an untouched file as fully written by hand.
        if (tag == "H" && string.IsNullOrEmpty(text))
        {
            captured++;
            return;
        }

        switch (tag)
        {
            case "H": human++; break;
            case "V": validated++; break;
            case "S": skipped++; break;
            case "M": break; // the mod's own interface — counted nowhere
            default: ai++; break;
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
