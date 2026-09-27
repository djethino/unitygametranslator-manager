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

/// <summary>A font this game holds.</summary>
public sealed record GameFont(string Name, long Length);

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

        var fonts = new List<GameFont>();
        var fontsFolder = Path.Combine(folder, AssetPacks.FontsFolder);
        if (Directory.Exists(fontsFolder))
        {
            foreach (var file in Directory.EnumerateFiles(fontsFolder))
            {
                if (AssetPacks.IsFontFile(file))
                    fonts.Add(new GameFont(Path.GetFileName(file), new FileInfo(file).Length));
            }
        }

        fonts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var translation = TranslationPath(folder);
        var root = ReadTranslation(translation);
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
    public static AssetPlan Plan(GameInstall game, LoaderDescriptor descriptor, IEnumerable<string> paths)
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
                    ReadPack(game, path, name, files, definitions, named, refused, madeFor, hasTranslation: root is not null);
                    continue;
                }

                switch (AssetPacks.KindOfFile(name))
                {
                    case AssetKind.Font:
                        files[(AssetKind.Font, name)] = Loose(AssetKind.Font, path, name);
                        break;

                    case AssetKind.Image when named.Contains(name):
                        files[(AssetKind.Image, name)] = Loose(AssetKind.Image, path, name);
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

    private static IncomingAsset Loose(AssetKind kind, string path, string name)
    {
        using var stream = File.OpenRead(path);
        return new IncomingAsset(kind, name, name, path, null, stream.Length, Sha256Of(stream));
    }

    private static void ReadPack(GameInstall game, string path, string packName,
                                 Dictionary<(AssetKind, string), IncomingAsset> files,
                                 Dictionary<string, (JsonObject, string, string)> definitions,
                                 HashSet<string> named, List<RefusedAsset> refused, List<string> madeFor,
                                 bool hasTranslation)
    {
        using var zip = ZipFile.OpenRead(path);

        var manifestEntry = zip.GetEntry(AssetPacks.ManifestName);
        if (manifestEntry is null)
        {
            refused.Add(new(packName, "Not a UGT asset pack: it has no manifest."));
            return;
        }

        JsonObject manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            if (JsonNode.Parse(reader.ReadToEnd(), documentOptions: Lenient) is not JsonObject parsed)
            {
                refused.Add(new(packName, "Not a UGT asset pack: its manifest cannot be read."));
                return;
            }

            manifest = parsed;
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

        if (OtherGame(game, manifest["game"] as JsonObject) is { } other) madeFor.Add(other);

        var carried = new Dictionary<string, IncomingAsset>(StringComparer.OrdinalIgnoreCase);
        var ignored = 0;

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == AssetPacks.ManifestName || entry.FullName.EndsWith('/')) continue;

            if (!AssetPacks.TryEntry(entry.FullName, out var kind, out var bare))
            {
                ignored++;
                continue;
            }

            using var stream = entry.Open();
            var asset = new IncomingAsset(kind, bare, packName, path, entry.FullName, entry.Length, Sha256Of(stream));

            if (kind == AssetKind.Font) files[(AssetKind.Font, bare)] = asset;
            else carried[bare] = asset;
        }

        if (ignored > 0)
        {
            refused.Add(new(packName, ignored == 1
                ? "1 file in it is not a font or an image, and was left out."
                : $"{ignored} files in it are not fonts or images, and were left out."));
        }

        // The definitions, each tied to a picture the pack carries — an image is only ever written
        // together with what makes the mod use it.
        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (manifest["images"] is JsonArray images)
        {
            foreach (var item in images)
            {
                if (item is not JsonObject definition || SpriteOf(definition) is not { Length: > 0 } sprite) continue;

                var file = FileOf(definition);
                if (file is null || !carried.ContainsKey(file))
                {
                    refused.Add(new(sprite, $"Its image is missing from {packName}."));
                    continue;
                }

                if (!hasTranslation)
                {
                    refused.Add(new(sprite, "This game has no translation file yet. Play it once with "
                                            + "UGT Mod, then add the pack again."));
                    continue;
                }

                // Written as the mod writes it: the current field name, whatever the pack used.
                var copy = (JsonObject)definition.DeepClone();
                foreach (var legacy in TranslationFiles.ImageFileLegacyFields) copy.Remove(legacy);
                copy[TranslationFiles.ImageFileField] = file;

                definitions[sprite] = (copy, file, packName);
                defined.Add(file);
            }
        }

        foreach (var (bare, asset) in carried)
        {
            if (defined.Contains(bare) || named.Contains(bare))
                files[(AssetKind.Image, bare)] = asset;
            else
                refused.Add(new(bare, $"No image setting in {packName} or in this game's translation uses it."));
        }
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

                using (var source = OpenSource(asset))
                using (var output = File.Create(temp))
                {
                    source.CopyTo(output);
                }

                File.Move(temp, target, overwrite: true);
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

    /// <summary>What an export would carry: every font source, and every defined image whose file is there.</summary>
    public static (int Fonts, int Images) Exportable(GameAssetsState state) =>
        (state.Fonts.Count, state.ImagesPresent);

    /// <summary>
    /// Writes this game's fonts and replacement images into a `.ugtpack` at <paramref name="destination"/>.
    ///
    /// ⚠ Generated atlases are never carried (<see cref="AssetPacks.IsFontFile"/> refuses them),
    /// and an image is carried only with its definition — the pair the mod needs.
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

                var fontsFolder = Path.Combine(folder, AssetPacks.FontsFolder);
                if (Directory.Exists(fontsFolder))
                {
                    foreach (var file in Directory.EnumerateFiles(fontsFolder))
                    {
                        var name = Path.GetFileName(file);
                        if (!AssetPacks.IsFontFile(name) || !AssetPacks.IsSafeFileName(name)) continue;

                        zip.CreateEntryFromFile(file, AssetPacks.FontsFolder + "/" + name, CompressionLevel.Optimal);
                        written++;
                    }
                }

                if (written == 0) return new(false, 0, "This game has no fonts or images to export.");

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

    private static string Sha256Of(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
}
