using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>What a file would do to the game: arrive, replace one that differs, or change nothing.</summary>
public enum AssetChange
{
    Add,
    Replace,
    Same,
}

/// <summary>
/// One font or image offered to a game — a file dropped on its own, or an entry of a pack.
/// </summary>
/// <param name="From">What the person handed over, as they would recognise it: the file's name, or the pack's.</param>
/// <param name="SourcePath">The file on disk: the font or image itself, or the pack holding it.</param>
/// <param name="EntryName">The entry inside the pack; null for a file given on its own.</param>
public sealed record IncomingAsset(AssetKind Kind, string Name, string From, string SourcePath,
                                   string? EntryName, long Length, string Sha256);

public sealed record PlannedAsset(IncomingAsset Asset, AssetChange Change);

/// <summary>An image definition a pack carries, measured against the game's translation.</summary>
public sealed record PlannedDefinition(string SpriteName, string File, JsonObject Definition,
                                       AssetChange Change, string From);

/// <summary>Something handed over that will not be written, and why — in the words a screen shows.</summary>
public sealed record RefusedAsset(string Name, string Reason);

/// <summary>
/// Everything a set of dropped files would do, before anything is written.
/// </summary>
/// <param name="MadeFor">Games a pack names that are not this one — said, never refused.</param>
public sealed record AssetPlan(IReadOnlyList<PlannedAsset> Files,
                               IReadOnlyList<PlannedDefinition> Definitions,
                               IReadOnlyList<RefusedAsset> Refused,
                               IReadOnlyList<string> MadeFor)
{
    public static readonly AssetPlan Empty = new([], [], [], []);

    /// <summary>How many offers would change something.</summary>
    public int Changes => Offers.Count(o => o.Change != AssetChange.Same);

    /// <summary>
    /// What a person decides on, one row each: a font, or an image WITH its setting.
    ///
    /// 🔴 **An image and its setting are one choice.** Declining the replacement of a picture while
    /// its changed setting went through left the translation describing a new pivot for the old
    /// picture. Paired here, so no screen can offer them apart.
    /// </summary>
    public IReadOnlyList<AssetOffer> Offers
    {
        get
        {
            var offers = new List<AssetOffer>();

            foreach (var font in Files.Where(f => f.Asset.Kind == AssetKind.Font))
                offers.Add(new AssetOffer(AssetKind.Font, font.Asset.Name, font.Change, font.Asset.From, [font], []));

            var images = Files.Where(f => f.Asset.Kind == AssetKind.Image)
                              .ToDictionary(f => f.Asset.Name, StringComparer.OrdinalIgnoreCase);
            var paired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var definition in Definitions)
            {
                images.TryGetValue(definition.File, out var file);
                if (file is not null) paired.Add(definition.File);

                var change = Strongest(definition.Change, file?.Change ?? AssetChange.Same);
                offers.Add(new AssetOffer(AssetKind.Image, definition.SpriteName, change, definition.From,
                                          file is null ? [] : [file], [definition]));
            }

            // A picture given on its own, filling a file the translation already names.
            foreach (var (name, file) in images)
            {
                if (!paired.Contains(name))
                    offers.Add(new AssetOffer(AssetKind.Image, name, file.Change, file.Asset.From, [file], []));
            }

            return offers;
        }
    }

    private static AssetChange Strongest(AssetChange a, AssetChange b) =>
        a == AssetChange.Replace || b == AssetChange.Replace ? AssetChange.Replace
        : a == AssetChange.Add || b == AssetChange.Add ? AssetChange.Add
        : AssetChange.Same;
}

/// <summary>One row of a plan: a font, or an image with its setting — accepted or declined as a whole.</summary>
/// <param name="Name">The font's file name, or the image's sprite name (what the mod's inspector shows).</param>
public sealed record AssetOffer(AssetKind Kind, string Name, AssetChange Change, string From,
                                IReadOnlyList<PlannedAsset> Files, IReadOnlyList<PlannedDefinition> Definitions)
{
    /// <summary>Stable across plans of the same files, so a screen can remember a declined row.</summary>
    public string Key => $"{Kind}:{Name}";
}

/// <summary>A font this game holds, and whether its translation uses it.</summary>
/// <param name="Used">Named by `_fonts[…].fallback` or `_font_overrides[].replacement` — what an export carries.</param>
public sealed record GameFont(string Name, long Length, bool Used);

/// <summary>An image this game's translation defines, and whether its file is there.</summary>
public sealed record GameImage(string SpriteName, string? File, bool Present);

/// <summary>What a game holds today — the fonts in its folder, the images its translation defines.</summary>
/// <param name="HasTranslation">Whether a translation file exists to hold image definitions.</param>
public sealed record GameAssetsState(IReadOnlyList<GameFont> Fonts, IReadOnlyList<GameImage> Images,
                                     bool HasTranslation)
{
    public int ImagesPresent => Images.Count(i => i.Present);
}

/// <summary>The outcome of writing or exporting.</summary>
public sealed record AssetWriteResult(bool Done, int Written, string? Failure);

/// <summary>
/// Fonts and replacement images for one game: what it holds, what a set of dropped files would
/// change, the write itself, and the `.ugtpack` that carries them to somebody else.
///
/// ⚠ The FORMAT and which name is safe are the socle's (<see cref="AssetPacks"/>); this class reads
/// and writes zips and files with them. Design, and the user's decisions it follows:
/// analyse/manager-onglet-assets.md (root).
///
/// 🔴 **A plan first, then the act.** Planning reads everything and writes nothing, so a screen
/// can say what would arrive and what would be replaced before anybody agrees — the validation the
/// user asked for. The act then takes only what the person kept.
/// </summary>
public static class GameAssets
{
    // ── What the game holds ──────────────────────────────────────────────────────────────────

    public static GameAssetsState Read(string gamePath, LoaderDescriptor descriptor)
    {
        var folder = UserDataInventory.DataFolder(gamePath, descriptor);
        if (folder is null) return new GameAssetsState([], [], false);

        var translation = TranslationPath(folder);
        var root = ReadTranslation(translation);
        var named = FontStemsNamed(root);

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
        var images = new List<GameImage>();

        foreach (var definition in Definitions(root))
        {
            var file = FileOf(definition);
            var present = file is not null && AssetPacks.IsSafeFileName(file)
                          && File.Exists(Path.Combine(folder, AssetPacks.ImagesFolder, file));
            images.Add(new GameImage(SpriteOf(definition) ?? "", file, present));
        }

        return new GameAssetsState(fonts, images, root is not null);
    }

    // ── What dropped files would do ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads every file handed over and says what each would do to this game. Writes nothing.
    ///
    /// ⚠ The same name handed over twice: the LAST one counts, as dropping a newer copy of a file
    /// means. Refusals are said per file, never silently dropped — a file that vanished from the
    /// list without a word is the thing somebody would search for.
    /// </summary>
    public static AssetPlan Plan(GameInstall game, LoaderDescriptor descriptor, IEnumerable<string> paths) =>
        Plan(game, descriptor, paths, room: null);

    /// <param name="room">
    /// How many bytes the drop may read in all — the drive's free space when null, which is what
    /// every caller but the checks passes (they need a drive that is "full" on demand).
    /// </param>
    public static AssetPlan Plan(GameInstall game, LoaderDescriptor descriptor, IEnumerable<string> paths, long? room)
    {
        var folder = UserDataInventory.DataFolder(game.Path, descriptor);
        if (folder is null) return AssetPlan.Empty with { Refused = [new("", UserDataInventory.OutsideGameRefusal)] };

        var root = ReadTranslation(TranslationPath(folder));
        var existing = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var definition in Definitions(root))
        {
            if (SpriteOf(definition) is { Length: > 0 } sprite) existing[sprite] = definition;
        }

        // Files the translation already names — what a picture given on its own may fill in.
        var named = new HashSet<string>(existing.Values.Select(FileOf).OfType<string>(), StringComparer.OrdinalIgnoreCase);

        var files = new Dictionary<(AssetKind, string), IncomingAsset>();
        var definitions = new Dictionary<string, (JsonObject Definition, string File, string From)>(StringComparer.Ordinal);
        var refused = new List<RefusedAsset>();
        var madeFor = new List<string>();

        // 🔴 **Never read more than could be written.** Everything handed over is read to be
        // measured, and a zip entry can unpack to terabytes from a few kilobytes. The bound is the
        // room left on the drive this game sits on — nothing larger could ever be written there —
        // shared by every file of the drop, and asked of the system, not decided here.
        var budget = new ReadBudget(room ?? FreeSpace(folder) ?? long.MaxValue);

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);

            try
            {
                if (!AssetPacks.IsSafeFileName(name))
                {
                    refused.Add(new(name, "Its name cannot be used as a file name in every game folder."));
                    continue;
                }

                if (AssetPacks.IsPack(name))
                {
                    ReadPack(game, path, name, files, definitions, named, refused, madeFor, hasTranslation: root is not null, budget);
                    continue;
                }

                switch (AssetPacks.KindOfFile(name))
                {
                    case AssetKind.Font:
                        if (Loose(AssetKind.Font, path, name, refused, budget) is { } font) files[(AssetKind.Font, name)] = font;
                        break;

                    case AssetKind.Image when named.Contains(name):
                        if (Loose(AssetKind.Image, path, name, refused, budget) is { } image) files[(AssetKind.Image, name)] = image;
                        break;

                    case AssetKind.Image:
                        refused.Add(new(name, "No image of this game's translation uses this file. "
                                              + "Images come with their settings in a .ugtpack."));
                        break;

                    default:
                        refused.Add(new(name, "Not a font (.ttf, .otf), an image (.png) or a .ugtpack."));
                        break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                refused.Add(new(name, $"Could not be read: {e.Message}"));
            }
        }

        var plannedFiles = files.Values
            .Select(asset => new PlannedAsset(asset, ChangeOf(folder, asset)))
            .OrderBy(p => p.Asset.Kind).ThenBy(p => p.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var plannedDefinitions = definitions
            .Select(d => new PlannedDefinition(d.Key, d.Value.File, d.Value.Definition,
                existing.TryGetValue(d.Key, out var here)
                    ? JsonNode.DeepEquals(here, d.Value.Definition) ? AssetChange.Same : AssetChange.Replace
                    : AssetChange.Add,
                d.Value.From))
            .OrderBy(d => d.SpriteName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AssetPlan(plannedFiles, plannedDefinitions, refused, madeFor.Distinct().ToList());
    }

    private static IncomingAsset? Loose(AssetKind kind, string path, string name, List<RefusedAsset> refused,
                                        ReadBudget budget)
    {
        // A file on disk states its real size: too large for the drive, it is not even opened.
        var size = new FileInfo(path).Length;
        if (size > budget.Left)
        {
            refused.Add(new(name, TooLarge(size, budget)));
            return null;
        }

        using var stream = File.OpenRead(path);
        var measured = Measure(stream, size);
        budget.Spend(measured.Length);

        if (!AssetPacks.ContentMatches(name, measured.Head, measured.HeadCount))
        {
            refused.Add(new(name, NotWhatItsNameSays(kind)));
            return null;
        }

        return new IncomingAsset(kind, name, name, path, null, measured.Length, measured.Sha256);
    }

    /// <summary>What a screen says about a file larger than the drive can take.</summary>
    private static string TooLarge(long size, ReadBudget budget) =>
        $"Too large: {Megabytes(size)}, and the drive holding this game has {Megabytes(Math.Max(0, budget.Left))} free.";

    /// <summary>What may still be read for one drop — the drive's free room, less what was read.</summary>
    private sealed class ReadBudget(long left)
    {
        public long Left { get; private set; } = left;

        public void Spend(long bytes) => Left -= bytes;
    }

    /// <summary>What a screen says about a file whose bytes are not what its extension claims.</summary>
    private static string NotWhatItsNameSays(AssetKind kind) =>
        kind == AssetKind.Font
            ? "Its content is not a font, whatever its name says."
            : "Its content is not a PNG image, whatever its name says.";

    /// <summary>
    /// Reads one pack into the plan — or nothing of it at all.
    ///
    /// 🔴 **A pack that lies is refused whole.** Every size is checked against what the zip DECLARES
    /// before a byte is unpacked: an entry declaring terabytes is refused on its declaration. One
    /// declaring kilobytes cannot unpack to more — .NET's reader stops at the declared size (measured
    /// on a forged archive, 2026-09-27), and the read here stops one byte past it as well, should that
    /// ever change. What such an entry then yields is a truncated file, which its CRC-32 — declared
    /// by the zip beside the size — exposes. Either way nothing from that pack is kept: a file built
    /// to mislead is not sorted into its honest and dishonest halves.
    /// </summary>
    private static void ReadPack(GameInstall game, string path, string packName,
                                 Dictionary<(AssetKind, string), IncomingAsset> files,
                                 Dictionary<string, (JsonObject, string, string)> definitions,
                                 HashSet<string> named, List<RefusedAsset> refused, List<string> madeFor,
                                 bool hasTranslation, ReadBudget budget)
    {
        using var zip = ZipFile.OpenRead(path);

        // What the pack declares, from its directory alone — nothing is unpacked yet.
        var assetEntries = zip.Entries.Where(e => AssetPacks.TryEntry(e.FullName, out _, out _)).ToList();
        var declared = assetEntries.Sum(e => e.Length);
        if (declared > budget.Left)
        {
            refused.Add(new(packName, TooLarge(declared, budget)));
            return;
        }

        var manifestEntry = zip.GetEntry(AssetPacks.ManifestName);
        if (manifestEntry is null)
        {
            refused.Add(new(packName, "Not a UGT asset pack: it has no manifest."));
            return;
        }

        // ⚠ The manifest is parsed in memory, so it is bounded by what it can legitimately hold:
        // a few hundred bytes of settings per picture the pack carries.
        var manifestRoom = ManifestBytesPerImage * (assetEntries.Count + 1);
        if (manifestEntry.Length > manifestRoom)
        {
            refused.Add(new(packName, Misleading));
            return;
        }

        JsonObject manifest;
        using (var stream = manifestEntry.Open())
        {
            var bytes = ReadAtMost(stream, manifestEntry.Length);
            if (bytes is null)
            {
                refused.Add(new(packName, Misleading));
                return;
            }

            try
            {
                if (JsonNode.Parse(Encoding.UTF8.GetString(bytes), documentOptions: Lenient) is not JsonObject parsed)
                {
                    refused.Add(new(packName, "Not a UGT asset pack: its manifest cannot be read."));
                    return;
                }

                manifest = parsed;
            }
            catch (JsonException)
            {
                refused.Add(new(packName, "Not a UGT asset pack: its manifest cannot be read."));
                return;
            }
        }

        if (IntOf(manifest["format"]) is not { } format || format < 1)
        {
            refused.Add(new(packName, "Not a UGT asset pack: its manifest has no format."));
            return;
        }

        if (format > AssetPacks.Format)
        {
            refused.Add(new(packName, "Made by a newer UGT Manager. Update UGT Manager to open it."));
            return;
        }

        // Read into the pack's own lists first: they reach the plan only if the whole pack is honest.
        var fonts = new List<IncomingAsset>();
        var carried = new Dictionary<string, IncomingAsset>(StringComparer.OrdinalIgnoreCase);
        var packRefused = new List<RefusedAsset>();
        var ignored = zip.Entries.Count(e => e.FullName != AssetPacks.ManifestName && !e.FullName.EndsWith('/')
                                             && !AssetPacks.TryEntry(e.FullName, out _, out _));

        foreach (var entry in assetEntries)
        {
            AssetPacks.TryEntry(entry.FullName, out var kind, out var bare);

            using var stream = entry.Open();
            var measured = Measure(stream, entry.Length);
            budget.Spend(measured.Length);

            if (measured.Over || measured.Crc32 != entry.Crc32)
            {
                refused.Add(new(packName, Misleading));
                return;
            }

            if (!AssetPacks.ContentMatches(bare, measured.Head, measured.HeadCount))
            {
                packRefused.Add(new(bare, NotWhatItsNameSays(kind)));
                continue;
            }

            var asset = new IncomingAsset(kind, bare, packName, path, entry.FullName, measured.Length, measured.Sha256);

            if (kind == AssetKind.Font) fonts.Add(asset);
            else carried[bare] = asset;
        }

        if (ignored > 0)
        {
            packRefused.Add(new(packName, ignored == 1
                ? "1 file in it is not a font or an image, and was left out."
                : $"{ignored} files in it are not fonts or images, and were left out."));
        }

        // The definitions, each tied to a picture the pack carries — an image is only ever written
        // together with what makes the mod use it.
        var packDefinitions = new Dictionary<string, (JsonObject, string, string)>(StringComparer.Ordinal);
        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (manifest["images"] is JsonArray images)
        {
            foreach (var item in images)
            {
                if (item is not JsonObject definition || SpriteOf(definition) is not { Length: > 0 } sprite) continue;

                var file = FileOf(definition);
                if (file is null || !carried.ContainsKey(file))
                {
                    packRefused.Add(new(sprite, $"Its image is missing from {packName}."));
                    continue;
                }

                if (!hasTranslation)
                {
                    packRefused.Add(new(sprite, "This game has no translation file yet. Play it once with "
                                                + "UGT Mod, then add the pack again."));
                    continue;
                }

                packDefinitions[sprite] = (KnownFieldsOf(definition, sprite, file), file, packName);
                defined.Add(file);
            }
        }

        // Honest throughout: now, and only now, what it carries joins the plan.
        if (OtherGame(game, manifest["game"] as JsonObject) is { } other) madeFor.Add(other);

        foreach (var font in fonts) files[(AssetKind.Font, font.Name)] = font;
        foreach (var (sprite, definition) in packDefinitions) definitions[sprite] = definition;

        foreach (var (bare, asset) in carried)
        {
            if (defined.Contains(bare) || named.Contains(bare))
                files[(AssetKind.Image, bare)] = asset;
            else
                packRefused.Add(new(bare, $"No image setting in {packName} or in this game's translation uses it."));
        }

        refused.AddRange(packRefused);
    }

    /// <summary>What a screen says about a pack whose sizes do not match what it declares.</summary>
    private const string Misleading =
        "Damaged, or built to mislead: a file in it is not what the pack says. Nothing from it was used.";

    /// <summary>
    /// Room for one picture's settings in a manifest — a ratio to what the pack carries, not a size
    /// picked for manifests. A setting is a dozen short fields, a few hundred bytes written out; this
    /// leaves ten times that, and a manifest past it describes pictures the pack does not hold.
    /// </summary>
    private const int ManifestBytesPerImage = 4096;

    /// <summary>Reads a stream that must hold exactly <paramref name="declared"/> bytes — null when it holds more.</summary>
    private static byte[]? ReadAtMost(Stream stream, long declared)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > declared) return null;
        }

        return memory.ToArray();
    }

    /// <summary>
    /// A definition from a pack, rebuilt from the fields the mod reads and nothing more — text where
    /// text is expected, numbers where numbers are. Written as the mod writes it: the current file
    /// field, whatever spelling the pack used.
    ///
    /// 🔴 **Never copied whole.** What goes into the translation file travels with it to the site and
    /// to everybody who downloads it; a field nobody reads is a place to carry anything.
    /// </summary>
    private static JsonObject KnownFieldsOf(JsonObject definition, string sprite, string file)
    {
        var known = new JsonObject { [TranslationFiles.ImageSpriteField] = sprite };

        if (definition[TranslationFiles.ImagePathField] is JsonValue path && path.TryGetValue<string>(out var text))
            known[TranslationFiles.ImagePathField] = text;

        foreach (var field in TranslationFiles.ImageNumberFields)
        {
            if (definition[field] is JsonValue value && value.TryGetValue<double>(out var number)
                && double.IsFinite(number))
            {
                known[field] = number;
            }
        }

        known[TranslationFiles.ImageFileField] = file;
        return known;
    }

    /// <summary>The game a pack names, when it is visibly another one — null when it matches or says nothing.</summary>
    private static string? OtherGame(GameInstall game, JsonObject? named)
    {
        if (named is null) return null;

        var name = named["name"]?.GetValue<string>();
        var steam = named["steam_id"]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(steam) && !string.IsNullOrWhiteSpace(game.SteamAppId))
            return string.Equals(steam, game.SteamAppId, StringComparison.Ordinal) ? null : name ?? steam;

        if (string.IsNullOrWhiteSpace(name)) return null;

        var matches = string.Equals(name.Trim(), game.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                      || string.Equals(name.Trim(), game.ProductName?.Trim(), StringComparison.OrdinalIgnoreCase);
        return matches ? null : name;
    }

    private static AssetChange ChangeOf(string folder, IncomingAsset asset)
    {
        var target = Path.Combine(folder, AssetPacks.FolderOf(asset.Kind), asset.Name);
        if (!File.Exists(target)) return AssetChange.Add;

        using var stream = File.OpenRead(target);
        return string.Equals(Sha256Of(stream), asset.Sha256, StringComparison.Ordinal)
            ? AssetChange.Same
            : AssetChange.Replace;
    }

    // ── Writing ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes what the person kept of a plan: the files, then the definitions.
    ///
    /// ⚠ Files first: a definition naming a picture that is not there yet is what the mod would read
    /// if the write stopped halfway, and a picture with no definition yet is merely unused.
    /// ⚠ A backup WITH the assets is taken first whenever something is replaced or the translation
    /// is touched — a replaced picture may be one somebody retouched by hand.
    /// </summary>
    public static AssetWriteResult Apply(IPlatform? platform, GameInstall game, LoaderDescriptor descriptor,
                                         IEnumerable<AssetOffer> accepted)
    {
        var offers = accepted.ToList();
        var files = offers.SelectMany(o => o.Files).ToList();
        var definitions = offers.SelectMany(o => o.Definitions).ToList();

        if (GameWrites.WhyNotNow(platform, game) is { } refusal) return new(false, 0, refusal);

        var folder = UserDataInventory.DataFolder(game.Path, descriptor);
        if (folder is null) return new(false, 0, UserDataInventory.OutsideGameRefusal);

        var toWrite = files.Where(f => f.Change != AssetChange.Same).ToList();
        var toDefine = definitions.Where(d => d.Change != AssetChange.Same).ToList();
        if (toWrite.Count == 0 && toDefine.Count == 0) return new(true, 0, null);

        // ⚠ Room on the drive, measured now — the bound is the disk this game sits on, not a size
        // decided here. A pack that would fill it is refused before a byte is written.
        var needed = toWrite.Sum(f => f.Asset.Length);
        if (FreeSpace(folder) is { } free && needed > free)
        {
            return new(false, 0, $"Not enough free space on this drive: {Megabytes(needed)} needed, "
                                 + $"{Megabytes(free)} free.");
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

                // 🔴 **What is written is what was read and agreed to.** The pack or the file can change
                // between the list on screen and Apply; copied through a hash and a counter, a single
                // byte more or different aborts before the file takes its place.
                try
                {
                    using (var source = OpenSource(asset))
                    using (var output = File.Create(temp))
                    {
                        CopyExactly(source, output, asset);
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
                var translation = TranslationPath(folder);
                if (ReadTranslation(translation) is not { } root)
                    return new(false, written, "This game has no translation file to hold the image settings.");

                if (root[TranslationFiles.ImagesSection] is not JsonArray section)
                {
                    section = new JsonArray();
                    root[TranslationFiles.ImagesSection] = section;
                }

                foreach (var planned in toDefine)
                {
                    var index = -1;
                    for (var i = 0; i < section.Count; i++)
                    {
                        if (section[i] is JsonObject entry && SpriteOf(entry) == planned.SpriteName) { index = i; break; }
                    }

                    var definition = planned.Definition.DeepClone();
                    if (index >= 0) section[index] = definition;
                    else section.Add(definition);
                    written++;
                }

                // What the mod raises when its own settings change: the translation now carries
                // something the site does not, and the next sync offers to publish it.
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

    private static Stream OpenSource(IncomingAsset asset)
    {
        if (asset.EntryName is null) return File.OpenRead(asset.SourcePath);

        var zip = ZipFile.OpenRead(asset.SourcePath);
        var entry = zip.GetEntry(asset.EntryName);
        if (entry is null)
        {
            zip.Dispose();
            throw new InvalidDataException($"{asset.Name} is no longer in {asset.From}.");
        }

        return new OwnedStream(entry.Open(), zip);
    }

    /// <summary>A zip entry's stream that closes its archive with it.</summary>
    private sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // ── Exporting ────────────────────────────────────────────────────────────────────────────

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
                                          string madeBy)
    {
        var folder = UserDataInventory.DataFolder(game.Path, descriptor);
        if (folder is null) return new(false, 0, UserDataInventory.OutsideGameRefusal);

        var temp = destination + ".tmp";

        try
        {
            var written = 0;
            var root = ReadTranslation(TranslationPath(folder));

            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var images = new JsonArray();

                foreach (var definition in Definitions(root))
                {
                    var file = FileOf(definition);
                    if (file is null || !AssetPacks.IsSafeFileName(file) || !AssetPacks.IsImageFile(file)) continue;

                    var source = Path.Combine(folder, AssetPacks.ImagesFolder, file);
                    if (!File.Exists(source)) continue;

                    zip.CreateEntryFromFile(source, AssetPacks.ImagesFolder + "/" + file, CompressionLevel.Optimal);
                    images.Add(definition.DeepClone());
                    written++;
                }

                var named = FontStemsNamed(root);
                var fontsFolder = Path.Combine(folder, AssetPacks.FontsFolder);
                if (Directory.Exists(fontsFolder))
                {
                    foreach (var file in Directory.EnumerateFiles(fontsFolder))
                    {
                        var name = Path.GetFileName(file);
                        if (!AssetPacks.IsFontFile(name) || !AssetPacks.IsSafeFileName(name)) continue;
                        if (!named.Any(stem => AssetPacks.IsFontFileFor(name, stem))) continue;

                        zip.CreateEntryFromFile(file, AssetPacks.FontsFolder + "/" + name, CompressionLevel.Optimal);
                        written++;
                    }
                }

                if (written == 0) return new(false, 0, "This game's translation uses no added font or image.");

                var gameNode = new JsonObject { ["name"] = game.ProductName ?? game.Name };
                if (!string.IsNullOrWhiteSpace(game.SteamAppId)) gameNode["steam_id"] = game.SteamAppId;

                var manifest = new JsonObject
                {
                    ["format"] = AssetPacks.Format,
                    ["game"] = gameNode,
                    ["made_by"] = madeBy,
                    ["images"] = images,
                };

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

    private static JsonObject? ReadTranslation(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) as JsonObject;
        }
        catch (JsonException)
        {
            // A damaged translation defines nothing we can read; the screen says it holds none.
            return null;
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
            if (reference is JsonValue value && value.TryGetValue<string>(out var text)
                && AssetPacks.FontFileStem(text) is { } stem)
            {
                stems.Add(stem);
            }
        }

        if (root?[SettingsSections.FontsKey] is JsonObject fonts)
        {
            foreach (var (_, settings) in fonts)
                if (settings is JsonObject font) Take(font["fallback"]);
        }

        if (root?[SettingsSections.FontRulesKey] is JsonArray rules)
        {
            foreach (var rule in rules.OfType<JsonObject>()) Take(rule["replacement"]);
        }

        return stems;
    }

    private static IEnumerable<JsonObject> Definitions(JsonObject? root) =>
        root?[TranslationFiles.ImagesSection] is JsonArray section
            ? section.OfType<JsonObject>()
            : [];

    private static string? SpriteOf(JsonObject definition) =>
        definition["sprite_name"] is JsonValue value && value.TryGetValue<string>(out var sprite) ? sprite : null;

    /// <summary>The image file a definition names, under its current field or an older one.</summary>
    private static string? FileOf(JsonObject definition)
    {
        foreach (var field in new[] { TranslationFiles.ImageFileField }.Concat(TranslationFiles.ImageFileLegacyFields))
        {
            if (definition[field] is JsonValue value && value.TryGetValue<string>(out var file) && !string.IsNullOrWhiteSpace(file))
                return file;
        }

        return null;
    }

    private static int? IntOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static string Sha256Of(Stream stream) => Measure(stream, long.MaxValue).Sha256;

    /// <summary>
    /// The zip checksum (CRC-32, IEEE), which every entry declares beside its size — so a truncated
    /// or altered entry is told from the one its pack describes. Written here rather than taken from
    /// a package: twenty lines, against a new dependency in a tool that ships as one signed file.
    /// </summary>
    private static readonly uint[] Crc32Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32Step(uint crc, byte[] buffer, int count)
    {
        for (var i = 0; i < count; i++) crc = Crc32Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>
    /// What a stream actually holds — its length, its hash and its first bytes, in one read — and
    /// never more than <paramref name="limit"/> bytes: past it, the read stops and says so (Over).
    /// </summary>
    private static (long Length, string Sha256, byte[] Head, int HeadCount, bool Over, uint Crc32) Measure(Stream stream, long limit)
    {
        var crc = 0xFFFFFFFFu;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var head = new byte[AssetPacks.HeaderLength];
        var headCount = 0;
        long length = 0;
        var buffer = new byte[81920];

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (headCount < head.Length)
            {
                var take = Math.Min(head.Length - headCount, read);
                Array.Copy(buffer, 0, head, headCount, take);
                headCount += take;
            }

            hash.AppendData(buffer, 0, read);
            crc = Crc32Step(crc, buffer, read);
            length += read;

            if (length > limit) return (length, "", head, headCount, true, 0);
        }

        return (length, Convert.ToHexString(hash.GetHashAndReset()), head, headCount, false, ~crc);
    }

    /// <summary>Copies a source that must still be exactly what was planned — its length and its hash.</summary>
    private static void CopyExactly(Stream source, Stream output, IncomingAsset asset)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long copied = 0;

        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > asset.Length)
                throw new InvalidDataException($"{asset.Name} changed since it was read. Add it again.");

            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }

        if (copied != asset.Length
            || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), asset.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{asset.Name} changed since it was read. Add it again.");
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

    private static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:0.#} MB";
}
