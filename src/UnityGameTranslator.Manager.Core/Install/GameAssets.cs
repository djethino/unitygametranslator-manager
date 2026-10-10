using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Common;
using UnityGameTranslator.Common.UnityFiles;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Diagnostics;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// Which font the translation shows where it names this file's name, on THIS computer
/// (<see cref="FontReferences"/>).
///
/// 🔴 The point is the warning (user, 2026-09-28): a file in fonts/ carrying the name of an installed
/// font or of a game font — a retouched copy of the game's own font, say — is NOT the one shown
/// unless the translation picks it as a custom font. Whoever thinks they use it must be told.
/// </summary>
public enum FontUse
{
    /// <summary>This file is the font shown.</summary>
    Used,

    /// <summary>The translation names this font, and the one installed on this computer is shown instead.</summary>
    InstalledInstead,

    /// <summary>The translation names this font, and the game's own font of that name is shown instead.</summary>
    GameInstead,

    /// <summary>Named by nothing the translation applies.</summary>
    NotUsed,
}

/// <summary>A font this game holds, what its translation shows in its name, and whether an export carries it.</summary>
/// <param name="Exported">Carried by every export: a Custom font the translation uses. A copy of a System font goes only when System fonts are asked for.</param>
public sealed record GameFont(string Name, long Length, FontUse Use, bool Exported);

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

    public static GameAssetsState Read(IPlatform platform, GameInstall game, LoaderDescriptor descriptor)
    {
        var (folders, registered) = FontsSeenBy(platform, game);
        return Read(game.Path, descriptor, name => IsInstalled(name, folders, registered));
    }

    /// <summary>
    /// The installed fonts AS THE GAME SEES THEM — its folders, and the system's table of them.
    ///
    /// 🔴 A game under Proton sees its Wine prefix's fonts, not this computer's (audit of 2026-09-28,
    /// analyse/audit-os-2026-09-28.md): read from the host, "Used / Not used" was wrong and an export
    /// carried files the game never drew. So its folders are the prefix's, by the socle's one list
    /// for Windows; Wine keeps its font table in its registry file, which is not read — fonts are
    /// then found by file name, as on Linux.
    /// </summary>
    public static (List<string> Folders, Func<IEnumerable<(string Name, string Path)>> Registered)
        FontsSeenBy(IPlatform platform, GameInstall game)
    {
        if (game.RunsUnderProton && game.ProtonPrefix is { } prefix)
        {
            var driveC = Path.Combine(prefix, "pfx", "drive_c");
            var folders = SystemFontFolders.For(SystemFontFolders.Os.Windows,
                    windowsDir: Path.Combine(driveC, "windows"),
                    localAppData: Path.Combine(driveC, "users", "steamuser", "AppData", "Local"))
                .Where(Directory.Exists).ToList();
            return (folders, () => []);
        }

        return (platform.FontFolders().ToList(), platform.RegisteredFonts);
    }

    /// <param name="installed">Whether a font of that name is installed on this computer, found as the mod finds it.</param>
    public static GameAssetsState Read(string gamePath, LoaderDescriptor descriptor, Func<string, bool> installed)
    {
        var folder = UserDataInventory.DataFolder(gamePath, descriptor);
        if (folder is null) return new GameAssetsState([], [], false);

        var translation = ReadTranslation(TranslationPath(folder));
        var references = FontReferencesNamed(translation.Root);

        var fonts = new List<GameFont>();
        var fontsFolder = Path.Combine(folder, AssetPacks.FontsFolder);
        if (Directory.Exists(fontsFolder))
        {
            foreach (var file in Directory.EnumerateFiles(fontsFolder))
            {
                var name = Path.GetFileName(file);
                if (AssetPacks.IsFontFile(name))
                    fonts.Add(new GameFont(name, new FileInfo(file).Length, UseOf(name, references, installed), AssetPackWriter.IsExported(name, references)));
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

    /// <summary>
    /// The manifest of a pack opened before any game is chosen — what names the game to offer —
    /// or null with the reason, in the words a plan would give.
    /// </summary>
    public static PackManifest? ReadManifest(string packPath, out string refusal)
    {
        try
        {
            using var zip = File.OpenRead(packPath);
            return AssetPlanner.ReadManifest(zip, ParseManifest, out refusal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The plan's words for the same failure, so the two cannot describe it differently.
            refusal = "Could not be read: " + e.Message;
            return null;
        }
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
            // The pack is refused as "not a pack" by the caller; the parser's own words are here.
            Faults.Say("GameAssets.ParseManifest", e);
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

    // ── The game's own fonts (common's UnityFiles.GameFonts) ────────────────────────────────

    /// <summary>
    /// The fonts the game carries in its data files, each with or without its file — the index UGT
    /// Mod keeps too (fonts/.ugt-game-fonts/index.txt): taken from there while the game's files have
    /// not changed, read from the game otherwise and, when <paramref name="mayWrite"/>, kept there for
    /// both. Null when the game has no data folder this can read. Slow on a large game: off the UI thread.
    /// </summary>
    public static GameFonts.Reading? ReadGameFonts(GameInstall game, LoaderDescriptor descriptor, bool mayWrite, Action<int, int>? progress = null)
    {
        var data = UnityGameProbe.FindDataDirectory(game.Path);
        var folder = UserDataInventory.DataFolder(game.Path, descriptor);
        if (data is null) return null;
        var saved = folder is null ? null : Path.Combine(folder, AssetPacks.FontsFolder, GameFonts.CacheFolder, GameFonts.IndexFile);

        try
        {
            string stamp = GameFonts.Stamp(data);
            if (saved is not null && GameFonts.LoadIndex(saved) is { } known && known.Stamp == stamp) return known;
            var index = GameFonts.FromDataFolder(data, game.UnityVersion, withData: false, progress: progress);
            if (mayWrite && saved is not null) GameFonts.SaveIndex(index, saved);
            return index;
        }
        catch (Exception e) when (Reading.Failed(e))
        {
            Journal.Note("GameAssets.ReadGameFonts", $"{Sanitize.Path(game.Path)}: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>What an export of game fonts did: files written, files of that name already there (left as they are), or why it stopped.</summary>
    public sealed record GameFontExport(bool Done, int Written, int AlreadyThere, string? Failure);

    /// <summary>
    /// Saves the files of these game fonts into a folder somebody chose — read from the game, nothing
    /// written into it (user, 2026-10-10: an export, out of the game). Named as the mod's Extract names
    /// the same file (GameFonts.FileNameFor); a file of that name already in the folder is left as it is.
    /// </summary>
    public static GameFontExport ExportGameFonts(GameInstall game, IReadOnlyList<GameFonts.Font> fonts, string destination)
    {
        var data = UnityGameProbe.FindDataDirectory(game.Path);
        if (data is null) return new(false, 0, 0, "This game's data folder cannot be found.");

        var written = 0;
        var already = 0;
        // Two different fonts of one name (other lengths, GameFonts.Distinct keeps both) in one export:
        // the second is "Name-2", never taken for the first one "already there".
        var namesThisExport = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(destination);
            foreach (var font in GameFonts.Distinct(fonts))
            {
                if (!font.HasFile) continue;
                var stem = font.Name;
                if (namesThisExport.TryGetValue(font.Name, out var seen)) stem = $"{font.Name}-{seen + 1}";
                namesThisExport[font.Name] = seen + 1;
                if (ExtractedFile(destination, stem) is not null) { already++; continue; }
                var bytes = GameFonts.ReadData(data, font, game.UnityVersion);
                if (bytes is null) continue;

                var target = Path.Combine(destination, GameFonts.FileNameFor(stem, GameFonts.ExtensionOf(bytes)));
                var temp = target + ".tmp";
                try
                {
                    File.WriteAllBytes(temp, bytes);
                    File.Move(temp, target, overwrite: false);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                written++;
            }
            return new(true, written, already, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UnityFileFormatException)
        {
            return new(false, written, already, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The file of a game font already in a folder, or null — by the shared name, any extension.</summary>
    private static string? ExtractedFile(string folder, string fontName)
    {
        foreach (var extension in new[] { ".ttf", ".otf", ".ttc" })
        {
            var path = Path.Combine(folder, GameFonts.FileNameFor(fontName, extension));
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Free bytes on the drive holding this folder — null when the system cannot say.</summary>
    private static long? FreeSpace(string folder)
    {
        try
        {
            // 🔴 The mount that HOLDS the folder, not the root of its path (2026-09-28). On Linux the
            // root is always "/", which on Bazzite and SteamOS is the read-only system image with
            // 0 bytes free — every pack was refused as too large while /var had gigabytes. The
            // longest mount point that contains the folder is the drive it is on (on Windows,
            // its drive letter, as before).
            var path = Detection.RealPath.Of(folder);
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
            var mount = MountHolding(drives.Select(d => d.RootDirectory.FullName), path);
            if (mount is null) return null;

            return drives.First(d => d.RootDirectory.FullName == mount).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A drive that cannot be measured is not refused: the write itself will say if it fails.
            Journal.Note("GameAssets.FreeSpace", $"{Sanitize.Path(folder)}: free space unknown ({e.GetType().Name})");
            return null;
        }
    }

    /// <summary>
    /// The mount point, among <paramref name="mounts"/>, that holds <paramref name="path"/>: the
    /// longest one it lies under. Null when none does. Pure, for the check.
    /// </summary>
    public static string? MountHolding(IEnumerable<string> mounts, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? best = null;

        foreach (var mount in mounts)
        {
            // Either separator: the mount "/" trims to "" and "C:\" to "C:", both then followed by
            // the separator the path itself uses.
            var trimmed = mount.TrimEnd('/', '\\');
            var under = path.StartsWith(trimmed + "/", comparison)
                        || path.StartsWith(trimmed + "\\", comparison)
                        || string.Equals(path.TrimEnd('/', '\\'), trimmed, comparison);
            if (under && (best is null || mount.Length > best.Length)) best = mount;
        }

        return best;
    }

    // ── Exporting ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The System fonts this game's translation uses (bare references), each with the file an export
    /// would carry — its copy in fonts/ when there is one, the installed file otherwise — or why it cannot.
    ///
    /// 🔴 **Found the way the mod finds them**: the system's font table first, then the socle's search
    /// (<see cref="FontFileNames.FindFile"/>: a file the name names, else the font whose name table
    /// carries it), in the folders the mod searches (<see cref="IPlatform.FontFolders"/>). Another
    /// rule would export a font the game never showed.
    ///
    /// ⚠ Remembered per game until the translation or a font folder changes: the tab draws this on
    /// every redraw, and the system's font folder holds a thousand files.
    /// </summary>
    public static IReadOnlyList<SystemFontUse> SystemFontsUsed(IPlatform platform, GameInstall game, LoaderDescriptor descriptor)
    {
        var (folders, registered) = FontsSeenBy(platform, game);
        return SystemFontsUsed(game, descriptor, folders, registered);
    }

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
        var table = registered().ToList();

        // The rule is the socle's (AssetPackWriter.SystemFonts), the mod's export applies the same;
        // what is this product's is how an installed font is found.
        var uses = AssetPackWriter.SystemFonts(FontReferencesNamed(read.Root), folder,
                                               stem => FindInstalledFont(stem, table, folders))
            .Select(c => new SystemFontUse(c.Reference, c.Path, c.Why))
            .ToList();

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

        // 2. The socle's search, the mod's own: a file the name names, else the single font whose
        //    name table carries it — an export carries a file as it is, so never a collection.
        return FontFileNames.FindFile(name, folders, collections: false, out _);
    }

    /// <summary>What an export would carry: the fonts the translation uses, and every defined image whose file is there.</summary>
    public static (int Fonts, int Images) Exportable(GameAssetsState state) =>
        (state.Fonts.Count(f => f.Exported), state.ImagesPresent);

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
            var read = ReadTranslation(TranslationPath(folder));
            if (read.Damaged) return new(false, 0, AssetPlanner.DamagedTranslation);
            var root = read.Root;

            // 🔴 What goes, and how it is written, are the socle's (AssetPackWriter) — the mod exports
            // through the same code, so the two products make the same pack from the same game.
            var plan = AssetPackWriter.Plan(folder, FontReferencesNamed(root), Definitions(root),
                systemFonts.Where(f => f.Includable)
                           .Select(f => new KeyValuePair<string, string>(f.Reference, f.Path!)));

            if (plan.IsEmpty) return new(false, 0, AssetPackWriter.NothingToExport);

            // The language the pictures' text is in — the translation's target. A player of another
            // language is told, and knows which pictures to remake.
            var manifest = AssetPackWriter.ManifestJson(game.ProductName ?? game.Name, game.SteamAppId, madeBy,
                                                        TextOf(root?["_target_language"]), plan.Images);

            using (var output = File.Create(temp))
                AssetPackWriter.Write(output, plan.Files, manifest);

            File.Move(temp, destination, overwrite: true);
            return new(true, plan.Files.Count, null);
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
            // Damaged, and therefore never written over (above); the screen says "damaged", the
            // journal what the parser met.
            Faults.Say("GameAssets.ReadTranslation", e, Sanitize.Path(path));
            return new(true, true, null);
        }
    }

    /// <summary>
    /// Which font is shown on this computer where the translation names this file's name — the rule
    /// the mod serves fonts by (<see cref="FontReferences"/>).
    ///
    /// 🔴 Named is not used. "[Game] X" is the game's X even with fonts/X.ttf beside it, and a bare
    /// "X" is the installed X first. Every kind of text reads a fonts/ file otherwise — TextMeshPro
    /// through the mod's atlas, legacy text since the mod shows fonts/ to the engine (2026-09-28).
    /// </summary>
    private static FontUse UseOf(string fileName, List<string> references, Func<string, bool> installed)
    {
        var naming = references
            .Where(r => AssetPacks.IsFontFileFor(fileName, FontReferences.Name(r)))
            .ToList();

        if (naming.Count == 0) return FontUse.NotUsed;

        if (naming.Any(r => FontReferences.Order(r)[0] == FontSource.Custom)) return FontUse.Used;

        if (naming.Any(r => FontReferences.Order(r)[0] == FontSource.System))
            return installed(FontReferences.Name(naming[0])) ? FontUse.InstalledInstead : FontUse.Used;

        return FontUse.GameInstead;
    }


    /// <summary>Whether a font of this name is installed, found as the mod finds it — remembered while the font folders stay as they are.</summary>
    private static bool IsInstalled(string name, IEnumerable<string> fontFolders, Func<IEnumerable<(string Name, string Path)>> registered)
    {
        var folders = fontFolders.ToList();
        var stamp = string.Join("|", folders.Select(p => Directory.Exists(p) ? Directory.GetLastWriteTimeUtc(p).Ticks.ToString() : "-"));

        if (InstalledMemory.TryGetValue(name, out var kept) && kept.Stamp == stamp) return kept.Installed;

        var found = FindInstalledFont(name, registered().ToList(), folders) is not null;
        InstalledMemory[name] = (stamp, found);
        return found;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, bool Installed)>
        InstalledMemory = new(StringComparer.Ordinal);

    /// <summary>
    /// Every font reference the translation applies, as written (origin mark included).
    /// </summary>
    private static List<string> FontReferencesNamed(JsonObject? root)
    {
        var references = new List<string>();

        void Take(JsonNode? reference)
        {
            if (TextOf(reference) is { } text) references.Add(text);
        }

        // ⚠ Read as the mod reads them (TranslatorCore.ParseFontsSection / ParseFontOverridesSection):
        // a setting switched off is not used — absent means on — and a rule with nothing to match
        // is dropped. Counting them would carry fonts the game never shows.
        static bool On(JsonObject setting) =>
            setting["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out var on) || on;

        if (root?[SettingsSections.FontsKey] is JsonObject fonts)
        {
            foreach (var (_, settings) in fonts)
                if (settings is JsonObject font && On(font))
                    Take(font["fallback"]);
        }

        if (root?[SettingsSections.FontRulesKey] is JsonArray rules)
        {
            foreach (var rule in rules.OfType<JsonObject>())
            {
                if (On(rule) && TextOf(rule["match"]) is not null) Take(rule["replacement"]);
            }
        }

        return references;
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
