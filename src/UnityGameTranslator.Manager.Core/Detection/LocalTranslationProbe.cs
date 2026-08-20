using System.Text.Json;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Common;

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
    /// An unstated SOURCE is a real answer and says so: the mod detects it line by line, and it is
    /// what explains why a source filter cannot be preselected from it.
    ///
    /// ⚠ An unstated TARGET is not. The mod resolves it from the machine's locale at launch, so a
    /// game left that way means something different on every machine — and over a translation that
    /// exists, it means the mod may well be working towards a language that file is not in. It is
    /// therefore worded as the gap it is, not as "auto", so it reads like something to settle
    /// rather than like a setting somebody chose. What settles it is the difference list on the
    /// game's card, which offers to write the real target in.
    /// </summary>
    public static string? DescribeLanguages(string gamePath, LoaderDescriptor descriptor)
    {
        var (source, target) = ReadLanguages(gamePath, descriptor);

        // Nothing configured at all: the mod has not been through its first run here, and saying
        // "auto → auto" would dress that up as a choice somebody made.
        if (source is null && target is null) return null;

        return $"{source ?? "auto-detected"} → {target ?? "no target set"}";
    }

    /// <summary>
    /// The content hash of the translation in a game, or null when there is none to hash.
    ///
    /// ⚠ Computed ON DEMAND and never as part of reading a game, because it is the one expensive
    /// thing here: a 1.6 MB file parses in ~19 ms and hashing walks every line again. Across fifty
    /// games, on every language change, that is a second nobody asked for — and the answer is only
    /// ever useful for a game whose translation also exists on the server, which is a handful.
    ///
    /// The rule itself is <see cref="ContentHash"/>, shared with the mod and ported from the
    /// website: what comes out is the same string the server issues as file_hash.
    /// </summary>
    /// <summary>
    /// The same hash, remembered against the file it was taken from.
    ///
    /// 🔴 **So the list of games may ask the question the game's own card asks.** The card knew
    /// this game's translation was behind the published one and the row beside it said "Ready to
    /// play", because the row is rebuilt on every lookup that comes back — and paying the parse
    /// above, per game, per lookup, is what the note above rules out. Remembered, it is paid once
    /// per file and per change to it.
    ///
    /// ⚠ Keyed on the file's stamp AND its length, not on a duration: this file is rewritten by
    /// the mod while somebody plays, and a cache with a lifetime would answer about a translation
    /// that has since moved. A write that changed neither the length nor the last-write time is
    /// not a write NTFS can produce — its stamp is finer than the time it takes to save.
    /// </summary>
    public static string? ContentHashOf(string gamePath, LoaderDescriptor descriptor)
    {
        var path = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            TranslationFileName);

        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists) return null;
        }
        catch (Exception)
        {
            // An unreadable path is not a hash and not a crash: the caller reads null as "no
            // comparison possible", which is exactly what this is.
            return null;
        }

        var stamp = (file.LastWriteTimeUtc, file.Length);

        if (_hashes.TryGetValue(path, out var known) && known.Stamp == stamp) return known.Hash;

        var hash = ComputeContentHash(gamePath, descriptor);
        _hashes[path] = (stamp, hash);
        return hash;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, ((DateTime Written, long Length) Stamp, string? Hash)> _hashes = new();

    public static string? ComputeContentHash(string gamePath, LoaderDescriptor descriptor)
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

            string? uuid = null;
            var lines = new List<KeyValuePair<string, TranslationLine>>();

            foreach (var property in root.EnumerateObject())
            {
                if (ContentHash.IsMetadataKey(property.Name))
                {
                    if (property.Name == ContentHash.UuidKey && property.Value.ValueKind == JsonValueKind.String)
                        uuid = property.Value.GetString();
                    continue;
                }

                lines.Add(new KeyValuePair<string, TranslationLine>(property.Name, LineOf(property.Value)));
            }

            return ContentHash.Of(lines, uuid ?? "");
        }
        catch
        {
            // A file we cannot read has no identity we can vouch for, and saying so is the point:
            // every caller treats null as "we do not know", never as "it differs".
            return null;
        }
    }

    /// <summary>
    /// The snapshot the mod keeps of the file as it stood at the last sync, beside the file itself.
    ///
    /// ⚠ It is what makes a three-way comparison possible at all: without it there is no way to
    /// tell "I changed this" from "the published version moved", and every difference has to be
    /// reported as an unknown.
    /// </summary>
    public const string AncestorFileName = TranslationFileName + ".ancestor";

    /// <summary>
    /// Every translated line of a file, keyed as the file keys them. Null when there is no such
    /// file or it cannot be read — which is not the same as an empty one, and callers must keep
    /// those apart.
    /// </summary>
    public static Dictionary<string, TranslationLine>? ReadLines(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var lines = new Dictionary<string, TranslationLine>(StringComparer.Ordinal);

            foreach (var property in root.EnumerateObject())
            {
                if (ContentHash.IsMetadataKey(property.Name)) continue;
                lines[property.Name] = LineOf(property.Value);
            }

            return lines;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// How many lines here differ from the snapshot taken at the last sync — measured, not asked.
    ///
    /// ⚠ **This is the number the mod used to be the only one able to give.** It kept a running
    /// count in the file (_local_changes), so anything outside a game had to take its word for it,
    /// and a file edited by any other means carried a count that no longer described it. The
    /// ancestor sits on disk next to the translation: the answer was always computable here.
    ///
    /// ⚠ Null means "no ancestor, so nobody can say" — a file never synced, or written before the
    /// snapshot existed. It must never be read as zero: zero is a claim that nothing was changed,
    /// and acting on it would offer to replace work with the published version.
    ///
    /// A line counts as changed when its value or its tag differs, by the same rule the merge uses
    /// — the same words tagged H rather than A means somebody read it, which is a change.
    /// </summary>
    public static int? CountChangedSinceAncestor(string gamePath, LoaderDescriptor descriptor)
    {
        var folder = Path.Combine(gamePath, descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar));

        var ancestor = ReadLines(Path.Combine(folder, AncestorFileName));
        if (ancestor is null) return null;

        var local = ReadLines(Path.Combine(folder, TranslationFileName));
        if (local is null) return null;

        var changed = 0;

        foreach (var line in local)
        {
            if (!ancestor.TryGetValue(line.Key, out var before))
            {
                // Added since the last sync: work that exists nowhere else.
                changed++;
                continue;
            }

            if (!Merge.Same(line.Value, before)) changed++;
        }

        // Removed since the last sync counts too: a deletion is a change somebody made.
        foreach (var line in ancestor)
        {
            if (!local.ContainsKey(line.Key)) changed++;
        }

        return changed;
    }

    /// <summary>
    /// One entry exactly as the file holds it.
    ///
    /// ⚠ Nothing is tidied here, and that is the whole point: the server hashes the file as
    /// written, so a bare string stays bare, a missing tag stays missing and a null value stays
    /// null. Filling any of them in with a sensible default produces a different hash for a file
    /// nobody has touched — which reads as "permanently out of sync" and never as a bug in us.
    /// </summary>
    private static TranslationLine LineOf(JsonElement value)
    {
        // The format from before tags existed. Old published translations are still made of these.
        if (value.ValueKind == JsonValueKind.String)
            return TranslationLine.Bare(value.GetString());

        if (value.ValueKind != JsonValueKind.Object) return TranslationLine.Bare(null);

        string? text = null;
        if (value.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.String)
            text = v.GetString();

        string? tag = null;
        if (value.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String)
            tag = t.GetString();

        return new TranslationLine(text, tag);
    }

    /// <summary>
    /// The site account a game is signed in with, or null when it is signed in with none.
    ///
    /// ⚠ **The token is never read, and this is how that promise is kept while still answering
    /// the question.** The mod clears api_token, api_user and api_token_server together — see
    /// ClearApiSession, which is also what runs when the server refuses a token — so the presence
    /// of a username IS the presence of a session, and the secret never has to be looked at.
    ///
    /// ⚠ Nothing here is written back, ever. This tool's own token and the mod's are deliberately
    /// separate so that revoking one does not disconnect the other; reading a name to display it
    /// is the whole of the interest we take in the other one.
    /// </summary>
    /// <returns>The account name and the server that issued it, or (null, null).</returns>
    public static (string? User, string? Server) ReadSiteAccount(string gamePath,
                                                                 LoaderDescriptor descriptor)
    {
        var path = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar), ConfigFileName);

        if (!File.Exists(path)) return (null, null);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            var user = root.TryGetProperty("api_user", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(user)) return (null, null);

            var server = root.TryGetProperty("api_token_server", out var s)
                         && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;

            return (user, string.IsNullOrWhiteSpace(server) ? null : server);
        }
        catch
        {
            // A config we cannot parse is reported elsewhere; here it simply means we cannot say.
            return (null, null);
        }
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
                // Measured rather than believed. Costs one more read of a file we have just read,
                // and only when the mod left a snapshot to compare against.
                ChangedSinceAncestor = CountChangedSinceAncestor(gamePath, descriptor),
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

        var candidates = new List<(string Dir, bool Canonical)>
        {
            (root, true),

            // MelonLoader's case: the documented place is Mods/, so a stray copy sits in a
            // subfolder of it.
            (Path.Combine(root, PluginFolderName), false),
        };

        // ⚠ BepInEx's case, and it is the mirror image — which is why it was missed. There the
        // documented place IS BepInEx/plugins/UnityGameTranslator/, so a copy dropped in by hand
        // lands in its PARENT, plugins/, beside every other mod. BepInEx loads that one too, and
        // the loader reads plugins/ before its subfolders: every update would go to the right
        // folder while the game kept running the old assembly, with nothing to explain why.
        //
        // Only when our own folder name is the last segment — under MelonLoader the parent is the
        // game's root, and hunting for a DLL there would be looking outside the loader entirely.
        if (string.Equals(Path.GetFileName(root), PluginFolderName, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(root) is { } parent)
        {
            candidates.Add((parent, false));
        }

        foreach (var (directory, canonical) in candidates)
        {
            var dll = Path.Combine(directory, PluginAssemblyName);
            if (File.Exists(dll))
                return new InstalledPlugin(directory, PeFile.ReadFileVersion(dll), canonical);
        }

        return null;
    }

    /// <summary>
    /// Every copy of our assembly that is NOT where it belongs, as paths relative to the game.
    ///
    /// Separate from FindInstalledPlugin, and the difference is the whole point: that one answers
    /// "is the mod installed" and stops at the first hit, canonical first. So on a game holding
    /// two copies it reported the good one and nothing else — the stray was invisible precisely
    /// when it mattered, since one copy alone is not a conflict.
    ///
    /// Two of our assemblies in one game is the worst kind of bug to meet, and NOT because of any
    /// rule we know: which one a loader keeps is its own business — scan order, plugin GUID,
    /// version comparison, and it differs between loaders and between their versions. What is
    /// certain is that this tool updates ONE of the two, so an update can land correctly and
    /// change nothing anybody can see.
    ///
    /// ⚠ Do not write "the older one runs" anywhere. It was written here and on the card, inferred
    /// from scan order alone and never measured — and it is topologically backwards for BepInEx,
    /// where the stray sits in the PARENT of the documented folder rather than under it.
    /// </summary>
    public static IReadOnlyList<string> FindStrayPlugins(string gamePath, LoaderDescriptor descriptor)
    {
        var root = Path.Combine(gamePath, descriptor.PluginDir.Replace('/', Path.DirectorySeparatorChar));

        var elsewhere = new List<string> { Path.Combine(root, PluginFolderName) };

        if (string.Equals(Path.GetFileName(root), PluginFolderName, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(root) is { } parent)
        {
            elsewhere.Add(parent);
        }

        var found = new List<string>();

        foreach (var directory in elsewhere)
        {
            if (!File.Exists(Path.Combine(directory, PluginAssemblyName))) continue;

            found.Add(Path.GetRelativePath(gamePath, directory).Replace('\\', '/'));
        }

        return found;
    }

    /// <summary>
    /// Whether the documented folder actually holds our assembly.
    ///
    /// ⚠ Asked of the disk, and separate from "is the mod installed": FindInstalledPlugin answers
    /// yes for a copy sitting anywhere it looks, so it cannot tell a duplicate (remove the stray)
    /// from a misplaced install (there is nothing to remove — deleting it uninstalls the mod).
    /// </summary>
    public static bool IsPluginInPlace(string gamePath, LoaderDescriptor descriptor) =>
        File.Exists(Path.Combine(gamePath,
            descriptor.PluginDir.Replace('/', Path.DirectorySeparatorChar), PluginAssemblyName));

    /// <summary>
    /// The folder this mod carries its own name in. Written once: it appears as the documented
    /// location under BepInEx and as the stray one under MelonLoader, and the two must stay the
    /// same string.
    /// </summary>
    public const string PluginFolderName = "UnityGameTranslator";

    /// <summary>Version of the deployed plugin, or null when it is not installed.</summary>
    public static string? ReadInstalledPluginVersion(string gamePath, LoaderDescriptor descriptor) =>
        FindInstalledPlugin(gamePath, descriptor)?.Version;

    public static bool HasConfig(string gamePath, LoaderDescriptor descriptor) =>
        File.Exists(Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            ConfigFileName));
}
