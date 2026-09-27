using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>A font this game holds, and whether its translation uses it.</summary>
/// <param name="Used">Named by an active `_fonts[…].fallback` or `_font_overrides[].replacement` — what an export carries.</param>
public sealed record GameFont(string Name, long Length, bool Used);

/// <summary>An image this game's translation defines, and whether its file is there.</summary>
public sealed record GameImage(string SpriteName, string? File, bool Present);

/// <summary>What a game holds today — the fonts in its folder, the images its translation defines.</summary>
/// <param name="HasTranslation">Whether a translation file exists to hold image definitions.</param>
/// <param name="TranslationDamaged">It exists and cannot be read safely — never written over.</param>
public sealed record GameAssetsState(IReadOnlyList<GameFont> Fonts, IReadOnlyList<GameImage> Images,
                                     bool HasTranslation, bool TranslationDamaged = false)
{
    public int ImagesPresent => Images.Count(i => i.Present);
}

/// <summary>
/// A font installed on this computer that the translation uses by name — what an export may carry
/// when somebody ticks it.
/// </summary>
/// <param name="Reference">The name the translation uses, and the name the file takes in the pack.</param>
/// <param name="Path">The file found, when it can go in a pack; null otherwise.</param>
/// <param name="Why">Why it cannot, when it cannot.</param>
public sealed record SystemFontUse(string Reference, string? Path, string? Why)
{
    public bool Includable => Path is not null;
}

/// <summary>The outcome of writing or exporting.</summary>
public sealed record AssetWriteResult(bool Done, int Written, string? Failure);

/// <summary>
/// Fonts and replacement images for one game, on this computer: what it holds, what dropped files
/// would change, the write itself, and the `.ugtpack` that carries them to somebody else.
///
/// 🔴 **Every decision is the socle's** (<see cref="AssetPlanner"/>, <see cref="AssetPackReader"/>,
/// <see cref="AssetPacks"/>) — the mod takes the same ones, so both refuse the same packs. What is
/// left here is this product's part: the files and the drive, the backup, and JSON read into the
/// socle's model and written back out of it. Design: analyse/manager-onglet-assets.md (root).
///
/// 🔴 **A plan first, then the act.** Planning reads and writes nothing, so a screen can say what
/// would arrive and what would be replaced before anybody agrees; the act takes only what was kept.
/// </summary>
public static class GameAssets
{
    // ── What the game holds ──────────────────────────────────────────────────────────────────

    public static GameAssetsState Read(string gamePath, LoaderDescriptor descriptor)
    {
        var folder = UserDataInventory.DataFolder(gamePath, descriptor);
        if (folder is null) return new GameAssetsState([], [], false);

        var translation = ReadTranslation(TranslationPath(folder));
        var named = FontStemsNamed(translation.Root);

        var fonts = new List<GameFont>();
        var fontsFolder = Path.Combine(folder, AssetPacks.FontsFolder);
        if (Directory.Exists(fontsFolder))
        {
            foreach (var file in Directory.EnumerateFiles(fontsFolder))
            {
                var name = Path.GetFileName(file);
                if (AssetPacks.IsFontFile(name))
                    fonts.Add(new GameFont(name, new FileInfo(file).Length, named.Any(stem => AssetPacks.IsFontFileFor(name, stem))));
            }
        }

        fonts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var images = Definitions(translation.Root)
            .Select(d => new GameImage(d.Sprite, d.File, d.File is not null && AssetPacks.IsSafeFileName(d.File)
                                                         && File.Exists(Path.Combine(folder, AssetPacks.ImagesFolder, d.File))))
            .ToList();

        return new GameAssetsState(fonts, images, translation.Exists, translation.Damaged);
    }

    // ── What dropped files would do ──────────────────────────────────────────────────────────

    /// <summary>What dropped files would do to this game — decided by the socle. Writes nothing.</summary>
    public static AssetPlan Plan(GameInstall game, LoaderDescriptor descriptor, IEnumerable<string> paths) =>
        Plan(game, descriptor, paths, room: null);

    /// <param name="room">
    /// How many bytes the drop may read in all — the drive's free space when null, which is what
    /// every caller but the checks passes (they need a drive that is "full" on demand).
    /// </param>
    public static AssetPlan Plan(GameInstall game, LoaderDescriptor descriptor, IEnumerable<string> paths, long? room)
    {
        var folder = UserDataInventory.DataFolder(game.Path, descriptor);
        if (folder is null) return AssetPlan.Empty.WithRefused(new RefusedAsset("", UserDataInventory.OutsideGameRefusal));

        var dropped = paths.Select(path => new DroppedFile(Path.GetFileName(path), () => File.OpenRead(path))).ToList();
        return AssetPlanner.Plan(SideOf(game, folder, room), dropped, ParseManifest);
    }

    /// <summary>What the game holds, in the socle's terms.</summary>
    private static GameAssetSide SideOf(GameInstall game, string folder, long? room)
    {
        var translation = ReadTranslation(TranslationPath(folder));

        return new GameAssetSide
        {
            GameName = game.Name,
            ProductName = game.ProductName,
            SteamId = game.SteamAppId,
            TranslationExists = translation.Exists,
            TranslationDamaged = translation.Damaged,
            TargetLanguage = TextOf(translation.Root?["_target_language"]),
            Definitions = Definitions(translation.Root).ToList(),
            ExistingSha256 = (kind, name) =>
            {
                var path = Path.Combine(folder, AssetPacks.FolderOf(kind), name);
                if (!File.Exists(path)) return null;
                using var stream = File.OpenRead(path);
                return AssetPackReader.Measure(stream, long.MaxValue).Sha256;
            },

            // 🔴 Never read more than could be written: the room left on the drive this game sits on.
            Room = room ?? FreeSpace(folder) ?? long.MaxValue,
        };
    }

    /// <summary>A manifest, read with this product's JSON into the socle's model — null when it is not JSON.</summary>
    private static PackManifest? ParseManifest(byte[] bytes)
    {
        try
        {
            if (JsonNode.Parse(Encoding.UTF8.GetString(bytes), documentOptions: Lenient) is not JsonObject root) return null;

            var game = root[PackManifest.GameField] as JsonObject;
            return new PackManifest
            {
                Format = root[PackManifest.FormatField] is JsonValue f && f.TryGetValue<int>(out var format) ? format : null,
                GameName = TextOf(game?[PackManifest.GameNameField]),
                SteamId = TextOf(game?[PackManifest.SteamIdField]),
                TargetLanguage = TextOf(root[PackManifest.TargetLanguageField]),
                Images = (root[PackManifest.ImagesField] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(ToDefinition).OfType<ImageDefinition>().ToList(),
            };
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes what the person kept of a plan: the files, then the settings.
    ///
    /// ⚠ Files first: a setting naming a picture that is not there yet is what the mod would read if
    /// the write stopped halfway, and a picture with no setting yet is merely unused.
    /// ⚠ A backup WITH the assets is taken first whenever something is replaced or the translation is
    /// touched — a replaced picture may be one somebody retouched by hand.
    /// </summary>
    /// <param name="sources">The paths the plan was made from, in the same order — what its offers point back to.</param>
    public static AssetWriteResult Apply(IPlatform? platform, GameInstall game, LoaderDescriptor descriptor,
                                         IReadOnlyList<string> sources, IEnumerable<AssetOffer> accepted)
    {
        var offers = accepted.ToList();

        if (GameWrites.WhyNotNow(platform, game) is { } refusal) return new(false, 0, refusal);

        var folder = UserDataInventory.DataFolder(game.Path, descriptor);
        if (folder is null) return new(false, 0, UserDataInventory.OutsideGameRefusal);

        var toWrite = offers.SelectMany(o => o.Files).Where(f => f.Change != AssetChange.Same).ToList();
        var toDefine = offers.SelectMany(o => o.Definitions).Where(d => d.Change != AssetChange.Same).ToList();
        if (toWrite.Count == 0 && toDefine.Count == 0) return new(true, 0, null);

        // ⚠ Room on the drive, measured now — the bound is the disk this game sits on.
        var needed = toWrite.Sum(f => f.Asset.Length);
        if (FreeSpace(folder) is { } free && needed > free)
        {
            return new(false, 0, $"Not enough free space on this drive: {AssetPlanner.Megabytes(needed)} needed, "
                                 + $"{AssetPlanner.Megabytes(free)} free.");
        }

        try
        {
            if (toDefine.Count > 0 || toWrite.Any(f => f.Change == AssetChange.Replace))
                TranslationBackupStore.TakeAutomatic(game.Path, descriptor, BackupReason.AssetsAdded, withAssets: true);

            var written = 0;

            foreach (var planned in toWrite)
            {
                var asset = planned.Asset;
                if (!AssetPacks.IsSafeFileName(asset.Name)) continue;   // decided at planning; held again at the door

                var directory = Path.Combine(folder, AssetPacks.FolderOf(asset.Kind));
                Directory.CreateDirectory(directory);

                var target = Path.Combine(directory, asset.Name);
                var temp = target + ".tmp";

                try
                {
                    using (var file = File.OpenRead(sources[asset.Source]))
                    using (var source = asset.EntryName is null ? file : AssetPackReader.OpenByName(file, asset.EntryName))
                    using (var output = File.Create(temp))
                    {
                        AssetPackReader.CopyExactly(source, output, asset.Length, asset.Sha256, asset.Name);
                    }

                    File.Move(temp, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }

                written++;
            }

            if (toDefine.Count > 0)
            {
                // Read again NOW: the file on disk is what gets merged into, not the one planned from.
                var translation = TranslationPath(folder);
                var read = ReadTranslation(translation);
                if (read.Damaged) return new(false, written, AssetPlanner.DamagedTranslation);
                if (!read.Exists) return new(false, written, AssetPlanner.NoTranslationYet);

                var root = read.Root!;

                // ⚠ Created only where it is ABSENT. A section there in another shape is a damaged file.
                if (root[TranslationFiles.ImagesSection] is not JsonArray section)
                {
                    section = new JsonArray();
                    root[TranslationFiles.ImagesSection] = section;
                }

                var sprites = section.Select(entry => entry is JsonObject o ? TextOf(o[TranslationFiles.ImageSpriteField]) : null).ToList();

                foreach (var edit in AssetPlanner.Merge(sprites, toDefine.Select(d => d.Definition)))
                {
                    foreach (var at in edit.RemoveAt) section.RemoveAt(at);

                    var node = ToJson(edit.Definition);
                    if (edit.ReplaceAt is int replace) section[replace] = node;
                    else section.Add(node);

                    written++;
                }

                // What the mod raises when its own settings change: the next sync offers to publish them.
                root["_metadata_dirty"] = true;

                var temp = translation + ".tmp";
                File.WriteAllText(temp, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
                File.Move(temp, translation, overwrite: true);
            }

            return new(true, written, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(false, 0, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Free bytes on the drive holding this folder — null when the system cannot say.</summary>
    private static long? FreeSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A drive that cannot be measured is not refused: the write itself will say if it fails.
            return null;
        }
    }

    // ── Exporting ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The installed fonts this game's translation uses by name — those its fonts/ folder does not
    /// already provide — each with the file an export would carry, or why it cannot.
    ///
    /// 🔴 **Found the way the mod finds them**: the system's font table first, then the file names
    /// the socle derives from the name (<see cref="SystemFontNames.Candidates"/>), then the loose
    /// match (<see cref="SystemFontNames.Normalize"/>), in the folders the mod searches
    /// (<see cref="IPlatform.FontFolders"/>). Another rule would export a font the game never showed.
    ///
    /// ⚠ Remembered per game until the translation or a font folder changes: the tab draws this on
    /// every redraw, and the system's font folder holds a thousand files.
    /// </summary>
    public static IReadOnlyList<SystemFontUse> SystemFontsUsed(IPlatform platform, GameInstall game, LoaderDescriptor descriptor) =>
        SystemFontsUsed(game, descriptor, platform.FontFolders(), platform.RegisteredFonts);

    /// <param name="fontFolders">Where installed fonts are — the platform's, or a folder a check made.</param>
    /// <param name="registered">The system's font table, asked only when the answer is not remembered.</param>
    public static IReadOnlyList<SystemFontUse> SystemFontsUsed(GameInstall game, LoaderDescriptor descriptor,
                                                               IEnumerable<string> fontFolders,
                                                               Func<IEnumerable<(string Name, string Path)>> registered)
    {
        var folder = UserDataInventory.DataFolder(game.Path, descriptor);
        if (folder is null) return [];

        var translationPath = TranslationPath(folder);
        var folders = fontFolders.ToList();
        var stamp = string.Join("|", new[] { translationPath, Path.Combine(folder, AssetPacks.FontsFolder) }
            .Concat(folders)
            .Select(p => File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks.ToString()
                       : Directory.Exists(p) ? Directory.GetLastWriteTimeUtc(p).Ticks.ToString() : "-"));

        if (SystemFontMemory.TryGetValue(translationPath, out var kept) && kept.Stamp == stamp) return kept.Uses;

        var read = ReadTranslation(translationPath);
        var local = new List<string>();
        var fontsFolder = Path.Combine(folder, AssetPacks.FontsFolder);
        if (Directory.Exists(fontsFolder)) local.AddRange(Directory.EnumerateFiles(fontsFolder).Select(Path.GetFileName).OfType<string>());

        var table = registered().ToList();
        var uses = new List<SystemFontUse>();

        foreach (var stem in FontStemsNamed(read.Root).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            if (local.Any(file => AssetPacks.IsFontFileFor(file, stem))) continue;   // fonts/ provides it

            var found = FindInstalledFont(stem, table, folders);
            uses.Add(found switch
            {
                null => new SystemFontUse(stem, null, "not installed on this computer"),
                _ when AssetPacks.IsFontFile(found) => new SystemFontUse(stem, found, null),
                _ => new SystemFontUse(stem, null, "a font collection (.ttc), which UGT Mod cannot load from a pack"),
            });
        }

        SystemFontMemory[translationPath] = (stamp, uses);
        return uses;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, IReadOnlyList<SystemFontUse> Uses)>
        SystemFontMemory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The installed file for a font name, found as the mod finds it — or null.</summary>
    private static string? FindInstalledFont(string name, List<(string Name, string Path)> registered, List<string> folders)
    {
        // 1. The system's own table: the name it files the font under.
        foreach (var (registeredName, path) in registered)
        {
            if (string.Equals(registeredName, name, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return path;
        }

        // A name is never a location: the translation comes from somebody else.
        if (!AssetPacks.IsSafeFileName(name)) return null;

        var candidates = SystemFontNames.Candidates(name);

        foreach (var dir in folders)
        {
            // 2. The file names the name suggests, as the mod tries them.
            foreach (var candidate in candidates)
            {
                foreach (var extension in AssetPacks.FontExtensions)
                {
                    var path = Path.Combine(dir, candidate + extension);
                    if (File.Exists(path)) return path;
                }
            }

            // 3. The loose match, over every font file below the folder.
            var wanted = SystemFontNames.Normalize(name);
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (AssetPacks.IsFontFile(file)
                        && string.Equals(SystemFontNames.Normalize(Path.GetFileNameWithoutExtension(file)), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder we may not walk holds nothing we can carry.
            }
        }

        return null;
    }

    /// <summary>What an export would carry: the fonts the translation uses, and every defined image whose file is there.</summary>
    public static (int Fonts, int Images) Exportable(GameAssetsState state) =>
        (state.Fonts.Count(f => f.Used), state.ImagesPresent);

    /// <summary>
    /// Writes this game's fonts and replacement images into a `.ugtpack` at <paramref name="destination"/>.
    ///
    /// 🔴 **Only what the translation USES** (user, 2026-09-27: « ça évite le bruit, et l'exporteur est
    /// celui qui bosse sur la trad »). An image goes with its definition; a font goes when a font
    /// setting or rule of the translation names it. A font sitting in fonts/ that nothing picks —
    /// tried once, left behind — is noise to whoever receives the pack.
    /// ⚠ Generated atlases are never carried (<see cref="AssetPacks.IsFontFile"/> refuses them).
    /// </summary>
    public static AssetWriteResult Export(GameInstall game, LoaderDescriptor descriptor, string destination,
                                          string madeBy) =>
        Export(game, descriptor, destination, madeBy, systemFonts: []);

    /// <param name="systemFonts">
    /// Installed fonts to carry as well — only when somebody ticked it (off by default, user
    /// 2026-09-27), and each written under the name the translation uses, so the mod receiving
    /// the pack finds it among its own fonts before looking at the system.
    /// </param>
    public static AssetWriteResult Export(GameInstall game, LoaderDescriptor descriptor, string destination,
                                          string madeBy, IReadOnlyList<SystemFontUse> systemFonts)
    {
        var folder = UserDataInventory.DataFolder(game.Path, descriptor);
        if (folder is null) return new(false, 0, UserDataInventory.OutsideGameRefusal);

        var temp = destination + ".tmp";

        try
        {
            var written = 0;
            var read = ReadTranslation(TranslationPath(folder));
            if (read.Damaged) return new(false, 0, AssetPlanner.DamagedTranslation);
            var root = read.Root;

            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var images = new JsonArray();

                foreach (var definition in Definitions(root))
                {
                    var file = definition.File;
                    if (file is null || !AssetPacks.IsSafeFileName(file) || !AssetPacks.IsImageFile(file)) continue;

                    var source = Path.Combine(folder, AssetPacks.ImagesFolder, file);
                    if (!File.Exists(source)) continue;

                    zip.CreateEntryFromFile(source, AssetPacks.ImagesFolder + "/" + file, CompressionLevel.Optimal);
                    images.Add(ToJson(definition));   // the allow-list again: a pack carries what the mod reads
                    written++;
                }

                var named = FontStemsNamed(root);
                var packed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fontsFolder = Path.Combine(folder, AssetPacks.FontsFolder);
                if (Directory.Exists(fontsFolder))
                {
                    foreach (var file in Directory.EnumerateFiles(fontsFolder))
                    {
                        var name = Path.GetFileName(file);
                        if (!AssetPacks.IsFontFile(name) || !AssetPacks.IsSafeFileName(name)) continue;
                        if (!named.Any(stem => AssetPacks.IsFontFileFor(name, stem))) continue;

                        zip.CreateEntryFromFile(file, AssetPacks.FontsFolder + "/" + name, CompressionLevel.Optimal);
                        packed.Add(name);
                        written++;
                    }
                }

                foreach (var system in systemFonts.Where(f => f.Includable))
                {
                    var name = system.Reference + Path.GetExtension(system.Path!).ToLowerInvariant();
                    if (!AssetPacks.IsSafeFileName(name) || !AssetPacks.IsFontFile(name) || !packed.Add(name)) continue;

                    zip.CreateEntryFromFile(system.Path!, AssetPacks.FontsFolder + "/" + name, CompressionLevel.Optimal);
                    written++;
                }

                if (written == 0) return new(false, 0, "This game's translation uses no added font or image.");

                var gameNode = new JsonObject { [PackManifest.GameNameField] = game.ProductName ?? game.Name };
                if (!string.IsNullOrWhiteSpace(game.SteamAppId)) gameNode[PackManifest.SteamIdField] = game.SteamAppId;

                var manifest = new JsonObject
                {
                    [PackManifest.FormatField] = AssetPacks.Format,
                    [PackManifest.GameField] = gameNode,
                    [PackManifest.MadeByField] = madeBy,
                    [PackManifest.ImagesField] = images,
                };

                // The language the pictures' text is in — the translation's target. A player of
                // another language is told, and knows which pictures to remake.
                var language = TextOf(root?["_target_language"]);
                if (Languages.IsSettled(language)) manifest[PackManifest.TargetLanguageField] = language;

                var entry = zip.CreateEntry(AssetPacks.ManifestName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(manifest.ToJsonString(WriteOptions));
            }

            File.Move(temp, destination, overwrite: true);
            return new(true, written, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(false, 0, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ── Reading the translation ──────────────────────────────────────────────────────────────

    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string TranslationPath(string folder) => Path.Combine(folder, LocalTranslationProbe.TranslationFileName);

    /// <summary>A translation file as read: absent, present and readable (Root), or present and damaged.</summary>
    private sealed record TranslationRead(bool Exists, bool Damaged, JsonObject? Root);

    /// <summary>
    /// Reads the translation file, and says so when it cannot be read SAFELY — then it is never
    /// written over.
    ///
    /// 🔴 **Damaged is not absent.** Read as "no translation", a file that failed to parse would be
    /// offered a fresh image section, and the next write would replace every line it held.
    ///
    /// ⚠ Two things System.Text.Json only finds when an object is first touched (measured,
    /// 2026-09-27): a key written twice at the top level, or inside an image entry, throws there
    /// rather than at parse. Both are touched here, so the answer is known before anything is
    /// planned. A key written twice inside a LINE is left alone: it is not touched and it is written
    /// back exactly as it was read.
    /// </summary>
    private static TranslationRead ReadTranslation(string path)
    {
        if (!File.Exists(path)) return new(false, false, null);

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) is not JsonObject root)
                return new(true, true, null);

            _ = root.Count;

            var section = root[TranslationFiles.ImagesSection];
            if (section is not null and not JsonArray) return new(true, true, null);

            foreach (var entry in (section as JsonArray ?? []).OfType<JsonObject>()) _ = entry.Count;

            return new(true, false, root);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return new(true, true, null);
        }
    }

    /// <summary>
    /// The font files the translation names — `_fonts[…].fallback` and `_font_overrides[].replacement`,
    /// as file names without extension (<see cref="AssetPacks.FontFileStem"/>).
    /// </summary>
    private static HashSet<string> FontStemsNamed(JsonObject? root)
    {
        var stems = new HashSet<string>(StringComparer.Ordinal);

        void Take(JsonNode? reference)
        {
            if (TextOf(reference) is { } text && AssetPacks.FontFileStem(text) is { } stem) stems.Add(stem);
        }

        // ⚠ Read as the mod reads them (TranslatorCore.ParseFontsSection / ParseFontOverridesSection):
        // a setting switched off is not used — absent means on — and a rule with nothing to match
        // is dropped. Counting them would carry fonts the game never shows.
        static bool On(JsonObject setting) =>
            setting["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out var on) || on;

        if (root?[SettingsSections.FontsKey] is JsonObject fonts)
        {
            foreach (var (_, settings) in fonts)
                if (settings is JsonObject font && On(font)) Take(font["fallback"]);
        }

        if (root?[SettingsSections.FontRulesKey] is JsonArray rules)
        {
            foreach (var rule in rules.OfType<JsonObject>())
            {
                if (On(rule) && TextOf(rule["match"]) is not null) Take(rule["replacement"]);
            }
        }

        return stems;
    }

    /// <summary>The translation's image settings, in file order, read into the socle's model.</summary>
    private static IEnumerable<ImageDefinition> Definitions(JsonObject? root) =>
        (root?[TranslationFiles.ImagesSection] as JsonArray ?? []).OfType<JsonObject>()
            .Select(ToDefinition).OfType<ImageDefinition>();

    /// <summary>One entry, through the socle's allow-list: text, numbers, nothing else.</summary>
    private static ImageDefinition? ToDefinition(JsonObject entry) =>
        ImageDefinition.Read(field => entry[field] is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text
            : value.TryGetValue<double>(out var number) ? number
            : null
            : null);

    /// <summary>A setting written back out, in the order the mod writes it.</summary>
    private static JsonObject ToJson(ImageDefinition definition)
    {
        var node = new JsonObject();
        foreach (var (field, value) in definition.Fields())
            node[field] = value is double number ? JsonValue.Create(number) : JsonValue.Create((string)value);
        return node;
    }

    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
