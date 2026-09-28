using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Fonts and images handed to a game — a pack or loose files — replayed on real files.
///
/// 🔴 A sequence, because what matters is what lands on disk: a plan can read right and the write
/// put a file outside the folder, drop the definition that makes a picture count, or replace a
/// picture somebody retouched without keeping it. None of that would throw.
/// </summary>
internal static class GameAssetsChecks
{
    private const string DefinitionTitle =
        "{\"sprite_name\":\"Title\",\"path\":\"Canvas/Title\",\"original_width\":256,\"original_height\":64,"
        + "\"pivot_x\":0.5,\"pivot_y\":0.5,\"pixels_per_unit\":100,\"file\":\"title.png\"}";

    internal static void WhatAPackPutsIntoAGame()
    {
        Program.Section("Fonts and images added to a game, and the pack that carries them");

        var descriptor = new LoaderDescriptor { Id = "bepinex5", UserDataDir = "BepInEx/plugins/UnityGameTranslator" };
        var root = Path.Combine(Path.GetTempPath(), "ugt-assets-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "game");
        var game = new GameInstall { Name = "Assets game", Path = gamePath };
        var folder = Path.Combine(gamePath, "BepInEx", "plugins", "UnityGameTranslator");
        var translation = Path.Combine(folder, LocalTranslationProbe.TranslationFileName);
        var outside = Path.Combine(gamePath, "BepInEx", "plugins");

        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "fonts"));
            Directory.CreateDirectory(Path.Combine(folder, "images"));
            File.WriteAllBytes(Path.Combine(folder, "fonts", "a.ttf"), Asset("a.ttf", "font a"));
            File.WriteAllBytes(Path.Combine(folder, "images", "title.png"), Asset("title.png", "retouched by hand"));
            File.WriteAllText(translation,
                "{\"_uuid\":\"u\",\"hello\":{\"v\":\"Bonjour\",\"t\":\"A\"},"
                + "\"_fonts\":{\"LiberationSans SDF\":{\"enabled\":true,\"fallback\":\"[Custom] a\",\"type\":\"TMP\"}},"
                + "\"_image_replacements\":[" + DefinitionTitle + "]}");

            var state = GameAssets.Read(gamePath, descriptor, _ => false);
            Program.Check(state is { Fonts.Count: 1, Images.Count: 1, HasTranslation: true } && state.Images[0].Present
                          && state.Fonts[0].Use == FontUse.Used,
                "the game is read: one font, one image defined and present",
                "the tab says what is there before anything is added");

            // A pack: one font identical, one new, a new image with its definition, the defined
            // image changed, and three entries that must never reach the game.
            var pack = Path.Combine(root, "Assets game assets.ugtpack");
            using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
            {
                Entry(zip, "manifest.json", new JsonObject
                {
                    ["format"] = 1,
                    ["game"] = new JsonObject { ["name"] = "Another game" },
                    ["images"] = new JsonArray(
                        JsonNode.Parse(DefinitionTitle.Replace("\"pivot_x\":0.5", "\"pivot_x\":0.25")),
                        JsonNode.Parse("{\"sprite_name\":\"Logo\",\"original_file\":\"logo.png\",\"pixels_per_unit\":100}")),
                }.ToJsonString());
                Entry(zip, "fonts/a.ttf", "font a");
                Entry(zip, "fonts/b.otf", "font b");
                Entry(zip, "images/logo.png", "logo");
                Entry(zip, "images/title.png", "new title");
                Entry(zip, "images/orphan.png", "nobody uses me");
                Entry(zip, "fonts/../evil.ttf", "escape");
                Entry(zip, "BepInEx/plugins/evil.dll", "code");
            }

            var loosePng = Path.Combine(root, "stray.png");
            File.WriteAllBytes(loosePng, Asset("stray.png", "stray"));
            var looseZip = Path.Combine(root, "fonts.zip");
            File.WriteAllBytes(looseZip, Bytes("zip"));

            var plan = GameAssets.Plan(game, descriptor, [pack, loosePng, looseZip]);

            AssetChange? Of(string name) => plan.Files.FirstOrDefault(f => f.Asset.Name == name)?.Change;

            Program.Check(Of("a.ttf") == AssetChange.Same && Of("b.otf") == AssetChange.Add
                          && Of("logo.png") == AssetChange.Add && Of("title.png") == AssetChange.Replace,
                "each file is measured against the game: same, new, or replacing",
                "the confirmation says what arrives and what would be replaced — the user's condition");

            Program.Check(plan.Definitions.Any(d => d.SpriteName == "Logo" && d.Change == AssetChange.Add)
                          && plan.Definitions.Any(d => d.SpriteName == "Title" && d.Change == AssetChange.Replace),
                "each image setting is measured too: a new sprite, a changed one",
                "a replaced setting silently moves a picture the translation already placed");

            Program.Check(plan.Files.All(f => f.Asset.Name is not ("evil.ttf" or "evil.dll" or "orphan.png")),
                "an entry walking out, a .dll and a picture nothing uses are not planned",
                "the first two would write outside the folder or next to the game's code");

            Program.Check(plan.Refused.Any(r => r.Name == "orphan.png")
                          && plan.Refused.Any(r => r.Name == "stray.png")
                          && plan.Refused.Any(r => r.Name == "fonts.zip")
                          && plan.Refused.Any(r => r.Name.EndsWith(".ugtpack") && r.Reason.Contains("2 files")),
                "every refusal is said, with the file it concerns",
                "a file that vanished from the list without a word is what somebody searches for");

            Program.Check(plan.MadeFor.SequenceEqual(["Another game"]),
                "a pack made for another game is said, not refused",
                "editions and repacks carry different names for the same game");

            var titleOffer = plan.Offers.Single(o => o.Name == "Title");
            Program.Check(titleOffer.Change == AssetChange.Replace && titleOffer.Files.Count == 1 && titleOffer.Definitions.Count == 1
                          && plan.Offers.Count(o => o.Kind == AssetKind.Image) == 2,
                "an image and its setting are ONE row",
                "declined apart, the translation described a new pivot for the old picture");

            // Keep the picture retouched by hand: its row is declined, file and setting together.
            var kept = plan.Offers.Where(o => o.Name != "Title").ToList();
            var result = GameAssets.Apply(null, game, descriptor, [pack, loosePng, looseZip], kept);

            Program.Check(result.Done && Holds(Path.Combine(folder, "images", "title.png"), "retouched by hand")
                          && Holds(Path.Combine(folder, "fonts", "b.otf"), "font b")
                          && File.Exists(Path.Combine(folder, "images", "logo.png")),
                "what was kept is written, and a declined replacement leaves the file alone",
                "declining must mean declining — the retouched picture exists nowhere else");

            Program.Check(!File.Exists(Path.Combine(outside, "evil.dll")) && !File.Exists(Path.Combine(outside, "evil.ttf"))
                          && !File.Exists(Path.Combine(folder, "evil.ttf")),
                "nothing is written outside the two folders",
                "a pack comes from somebody else and lands next to code the game loads");

            var written = JsonNode.Parse(File.ReadAllText(translation))!.AsObject();
            var section = written[TranslationFiles.ImagesSection]!.AsArray();
            var logo = section.OfType<JsonObject>().Single(d => d["sprite_name"]!.GetValue<string>() == "Logo");
            var title = section.OfType<JsonObject>().Single(d => d["sprite_name"]!.GetValue<string>() == "Title");
            Program.Check(title["pivot_x"]!.GetValue<double>() == 0.5,
                "the declined image's setting is left as it was",
                "the picture stayed, so its setting must stay with it");

            Program.Check(section.Count == 2 && logo[TranslationFiles.ImageFileField]?.GetValue<string>() == "logo.png"
                          && logo["original_file"] is null
                          && written["hello"] is not null && written["_metadata_dirty"]?.GetValue<bool>() == true,
                "the settings are merged into the translation, in the field the mod writes, lines untouched",
                "and marked changed, so the next sync offers to publish them as the mod does for its own");

            var backups = Path.Combine(folder, Backups.FolderName);
            var backup = Directory.Exists(backups) ? Directory.GetDirectories(backups).FirstOrDefault() : null;
            Program.Check(backup is not null && File.Exists(Path.Combine(backup, "images", "title.png"))
                          && Holds(Path.Combine(backup, "images", "title.png"), "retouched by hand"),
                "a backup WITH the pictures is taken before",
                "replacing a picture or a setting must never be the last copy of what was there");

            // The pack this game now makes, read back into the same game: nothing to do.
            var exported = Path.Combine(root, "export.ugtpack");
            var export = GameAssets.Export(game, descriptor, exported, "checks");
            var again = GameAssets.Plan(game, descriptor, [exported]);
            Program.Check(export.Done && again.Changes == 0 && again.Refused.Count == 0
                          && again.Files.Count == 3 && again.Definitions.Count == 2 && again.Offers.Count == 3,
                "an exported pack read back into its own game changes nothing",
                "the format written and the format read are the same format");

            Program.Check(again.Files.Any(f => f.Asset.Name == "a.ttf") && again.Files.All(f => f.Asset.Name != "b.otf")
                          && GameAssets.Read(gamePath, descriptor, _ => false).Fonts.Single(f => f.Name == "b.otf").Use == FontUse.NotUsed,
                "only the fonts the translation uses are exported, and the tab says which",
                "a font tried once and left in fonts/ is noise to whoever receives the pack (user, 2026-09-27)");

            Program.Check(!Directory.GetFiles(root).Any(f => f.EndsWith(".tmp")) && !Directory.GetFiles(Path.Combine(folder, "fonts")).Any(f => f.EndsWith(".tmp")),
                "no temporary file is left behind",
                "a leftover .tmp in fonts/ is a file the mod would try to read");

            // A pack from a newer tool, and one that is not a pack at all.
            var newer = Path.Combine(root, "newer.ugtpack");
            using (var zip = ZipFile.Open(newer, ZipArchiveMode.Create))
            {
                Entry(zip, "manifest.json", "{\"format\":99}");
                Entry(zip, "fonts/c.ttf", "font c");
            }

            var fake = Path.Combine(root, "fake.ugtpack");
            using (var zip = ZipFile.Open(fake, ZipArchiveMode.Create)) Entry(zip, "fonts/d.ttf", "font d");

            var refusedWhole = GameAssets.Plan(game, descriptor, [newer, fake]);
            Program.Check(refusedWhole.Files.Count == 0 && refusedWhole.Refused.Count == 2,
                "a pack from a newer tool, or with no manifest, is refused whole",
                "half-understanding a newer layout writes files in the wrong place");

            // Opened from the file explorer: the manifest alone names the game, and a pack the plan
            // would refuse is refused before any game is offered, in the same words.
            var named = GameAssets.ReadManifest(exported, out _);
            Program.Check(named?.GameName == "Assets game"
                          && GameAssets.ReadManifest(fake, out var noManifest) is null
                          && noManifest == refusedWhole.Refused.Single(r => r.Name == "fake.ugtpack").Reason
                          && GameAssets.ReadManifest(newer, out var tooNew) is null && tooNew.Contains("newer"),
                "a pack opened on its own names its game, or is refused as the plan would refuse it",
                "offering a game for a pack the tab then refuses is a way to nowhere");

            // ── Somebody else's pack, made to do harm ──────────────────────────────────────────

            // A program renamed to a font and to a picture, in a pack and on its own.
            var disguisedPack = Path.Combine(root, "disguised.ugtpack");
            using (var zip = ZipFile.Open(disguisedPack, ZipArchiveMode.Create))
            {
                Entry(zip, "manifest.json", "{\"format\":1}");
                Raw(zip, "fonts/tool.ttf", Program_);
            }

            var disguisedLoose = Path.Combine(root, "setup.otf");
            File.WriteAllBytes(disguisedLoose, Program_);

            var disguised = GameAssets.Plan(game, descriptor, [disguisedPack, disguisedLoose]);
            Program.Check(disguised.Files.Count == 0
                          && disguised.Refused.Any(r => r.Name == "tool.ttf" && r.Reason.Contains("not a font"))
                          && disguised.Refused.Any(r => r.Name == "setup.otf" && r.Reason.Contains("not a font")),
                "a program renamed to a font is refused on its bytes, in a pack or on its own",
                "the name is a claim: it would be handed to the mod's font reader");

            // A setting carrying fields nobody reads, and a field of the wrong kind.
            var loaded = Path.Combine(root, "loaded.ugtpack");
            using (var zip = ZipFile.Open(loaded, ZipArchiveMode.Create))
            {
                Entry(zip, "manifest.json", "{\"format\":1,\"images\":[{\"sprite_name\":\"Hero\",\"file\":\"hero.png\","
                                            + "\"pivot_x\":0.5,\"path\":{\"not\":\"text\"},\"payload\":{\"run\":\"anything\"},"
                                            + "\"pixels_per_unit\":\"100\"}]}");
                Entry(zip, "images/hero.png", "hero");
            }

            var loadedPlan = GameAssets.Plan(game, descriptor, [loaded]);
            GameAssets.Apply(null, game, descriptor, [loaded], loadedPlan.Offers);
            var hero = JsonNode.Parse(File.ReadAllText(translation))![TranslationFiles.ImagesSection]!.AsArray()
                .OfType<JsonObject>().Single(d => d["sprite_name"]!.GetValue<string>() == "Hero");
            Program.Check(hero["payload"] is null && hero["path"] is null && hero["pixels_per_unit"] is null
                          && hero["pivot_x"]!.GetValue<double>() == 0.5 && hero["file"]!.GetValue<string>() == "hero.png",
                "a setting from a pack keeps the fields the mod reads, of the right kind, and nothing else",
                "what is written into the translation travels to the site and to every player who downloads it");

            // More than the drive can take: refused on what the pack declares, nothing unpacked.
            var tooBig = GameAssets.Plan(game, descriptor, [pack], room: 10);
            Program.Check(tooBig.Files.Count == 0 && tooBig.Refused.Any(r => r.Reason.StartsWith("Too large")),
                "a pack larger than the drive's free space is refused before anything is unpacked",
                "nothing larger could ever be written, so there is no reason to read it");

            // A zip that lies about a size: declared small, holding more. The reader stops at the
            // declared size, so it cannot expand; what it yields is cut short, and its checksum says so.
            var lying = Path.Combine(root, "lying.ugtpack");
            using (var zip = ZipFile.Open(lying, ZipArchiveMode.Create))
            {
                Entry(zip, "manifest.json", "{\"format\":1}");
                Entry(zip, "fonts/honest.ttf", "honest");
                Raw(zip, "fonts/bomb.ttf", Asset("bomb.ttf", new string('0', 50_000)));
            }

            DeclareSize(lying, "fonts/bomb.ttf", 16);

            var bomb = GameAssets.Plan(game, descriptor, [lying]);
            Program.Check(bomb.Files.Count == 0 && bomb.Refused.Count > 0,
                "a pack that holds more than it declares is refused whole, the honest file with it",
                "it cannot expand past its declared size, and the file it yields is cut short — its checksum says so");

            // No translation yet: the fonts come in, the image settings wait.
            File.Delete(translation);
            var noTranslation = GameAssets.Plan(game, descriptor, [pack]);
            Program.Check(noTranslation.Definitions.Count == 0
                          && noTranslation.Refused.Any(r => r.Name == "Logo" && r.Reason.Contains("no translation"))
                          && noTranslation.Files.Any(f => f.Asset.Name == "b.otf"),
                "with no translation file, fonts are offered and image settings are refused with the reason",
                "creating a translation here would invent a lineage the mod has not started");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder left behind proves nothing */ }
        }
    }

    /// <summary>
    /// What a pack does to the translation file itself: one entry per sprite as the mod reads them,
    /// nothing written over a file that cannot be read, and the same pack twice changing nothing.
    /// </summary>
    internal static void WhatTheTranslationFileKeeps()
    {
        Program.Section("The translation file a pack writes into");

        var descriptor = new LoaderDescriptor { Id = "bepinex5", UserDataDir = "BepInEx/plugins/UnityGameTranslator" };
        var root = Path.Combine(Path.GetTempPath(), "ugt-assets-file-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "game");
        var game = new GameInstall { Name = "File game", Path = gamePath };
        var folder = Path.Combine(gamePath, "BepInEx", "plugins", "UnityGameTranslator");
        var translation = Path.Combine(folder, LocalTranslationProbe.TranslationFileName);

        try
        {
            Directory.CreateDirectory(folder);

            // "Title" twice already — the second, stale, is the one the mod reads (last wins).
            File.WriteAllText(translation,
                "{\"_uuid\":\"u\",\"hello\":{\"v\":\"Bonjour\",\"t\":\"A\"},\"_image_replacements\":["
                + "{\"sprite_name\":\"Title\",\"pivot_x\":0.5,\"file\":\"title.png\"},"
                + "{\"sprite_name\":\"Other\",\"file\":\"other.png\"},"
                + "{\"sprite_name\":\"TITLE\",\"pivot_x\":0.1,\"file\":\"title.png\"}]}");

            // The pack names the sprite in a third spelling.
            var pack = Path.Combine(root, "title.ugtpack");
            using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
            {
                Entry(zip, "manifest.json", "{\"format\":1,\"images\":[{\"sprite_name\":\"title\",\"pivot_x\":0.9,\"file\":\"title.png\"}]}");
                Entry(zip, "images/title.png", "new title");
            }

            var plan = GameAssets.Plan(game, descriptor, [pack]);
            Program.Check(plan.Definitions.Count == 1 && plan.Definitions[0].Change == AssetChange.Replace,
                "a sprite spelled differently is the same sprite, as the mod reads it",
                "added beside it as new, the mod would read one sprite and keep whichever came last");

            GameAssets.Apply(null, game, descriptor, [pack], plan.Offers);

            var section = JsonNode.Parse(File.ReadAllText(translation))![TranslationFiles.ImagesSection]!.AsArray();
            var titles = section.OfType<JsonObject>()
                .Where(d => TranslationFiles.SpriteNames.Equals(d["sprite_name"]!.GetValue<string>(), "title")).ToList();
            Program.Check(titles.Count == 1 && titles[0]["pivot_x"]!.GetValue<double>() == 0.9
                          && section.OfType<JsonObject>().Any(d => d["sprite_name"]!.GetValue<string>() == "Other"),
                "after the write, one entry per sprite, holding the new setting; the others untouched",
                "a stale duplicate left after ours would override it in the game, without a word");

            var again = GameAssets.Plan(game, descriptor, [pack]);
            var before = File.ReadAllBytes(translation);
            GameAssets.Apply(null, game, descriptor, [pack], again.Offers);
            Program.Check(again.Changes == 0 && File.ReadAllBytes(translation).AsSpan().SequenceEqual(before),
                "the same pack a second time changes nothing, not a byte of the file",
                "adding what is already there must not grow the file or rewrite it");

            // ── The language of the pictures, and names in any script ──────────────────────────
            File.WriteAllBytes(Path.Combine(folder, "images", "标题.png"), Asset("标题.png", "LOYAU"));
            File.WriteAllText(translation,
                "{\"_uuid\":\"u\",\"_target_language\":\"French\",\"_image_replacements\":["
                + "{\"sprite_name\":\"标题\",\"original_width\":926,\"original_height\":262,\"file\":\"标题.png\"}]}");

            var french = Path.Combine(root, "french.ugtpack");
            var exported = GameAssets.Export(game, descriptor, french, "checks");
            var roundTrip = GameAssets.Plan(game, descriptor, [french]);
            Program.Check(exported.Done && roundTrip.Changes == 0 && roundTrip.Files.Single().Asset.Name == "标题.png"
                          && roundTrip.OtherLanguages.Count == 0,
                "a picture named in Chinese leaves in a pack and comes back unchanged",
                "sprite and file names are the game's own, in whatever script the game is written");

            File.WriteAllText(translation,
                "{\"_uuid\":\"u\",\"_target_language\":\"German\",\"_image_replacements\":[]}");
            var german = GameAssets.Plan(game, descriptor, [french]);
            Program.Check(german.OtherLanguages.Count == 1
                          && german.OtherLanguages[0] is { PackLanguage: "French", GameLanguage: "German" }
                          && german.Definitions.Count == 1 && german.Refused.Count == 0,
                "pictures made for French, offered to a game translated into German: said, and still offered",
                "they show French text — and name exactly which pictures to remake, settings included");

            File.WriteAllText(translation,
                "{\"_uuid\":\"u\",\"_target_language\":\"fr\",\"_image_replacements\":[]}");
            Program.Check(GameAssets.Plan(game, descriptor, [french]).OtherLanguages.Count == 0,
                "the same language spelled as a code is the same language",
                "\"fr\" against \"French\" is not a warning somebody should read");

            // A file that cannot be read safely: a key written twice, and a section of the wrong shape.
            foreach (var damaged in new[]
            {
                "{\"_uuid\":\"u\",\"_uuid\":\"v\",\"_image_replacements\":[]}",
                "{\"_uuid\":\"u\",\"_image_replacements\":{\"sprite_name\":\"Kept\"}}",
                "{\"_uuid\":\"u\",\"_image_replacements\":[{\"sprite_name\":\"A\",\"sprite_name\":\"B\"}]}",
                "{\"_uuid\":\"u\",\"hello\":",
            })
            {
                File.WriteAllText(translation, damaged);
                var damagedPlan = GameAssets.Plan(game, descriptor, [pack]);
                var forced = GameAssets.Apply(null, game, descriptor, [],
                    [new AssetOffer(AssetKind.Image, "title", AssetChange.Add, "checks", [],
                        [new PlannedDefinition(ImageDefinition.Read(f => f == "sprite_name" ? "title" : f == "file" ? "title.png" : null)!,
                                               AssetChange.Add, "checks")])]);

                Program.Check(damagedPlan.Definitions.Count == 0 && damagedPlan.Refused.Any(r => r.Reason.Contains("cannot be read"))
                              && !forced.Done && File.ReadAllText(translation) == damaged
                              && GameAssets.Read(gamePath, descriptor, _ => false).TranslationDamaged,
                    "a translation that cannot be read safely is said so, and never written over: " + damaged[..Math.Min(40, damaged.Length)],
                    "read as \"no translation\", it would be given a fresh image section — and lose everything else");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder left behind proves nothing */ }
        }
    }

    /// <summary>
    /// The installed fonts a translation uses, found as the mod finds them, and carried by an export
    /// only when asked — under the name the translation uses.
    /// </summary>
    internal static void WhatASystemFontExportCarries()
    {
        Program.Section("Installed fonts a translation uses, in an export");

        var descriptor = new LoaderDescriptor { Id = "bepinex5", UserDataDir = "BepInEx/plugins/UnityGameTranslator" };
        var root = Path.Combine(Path.GetTempPath(), "ugt-assets-sysfonts-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "game");
        var game = new GameInstall { Name = "Fonts game", Path = gamePath };
        var folder = Path.Combine(gamePath, "BepInEx", "plugins", "UnityGameTranslator");
        var system = Path.Combine(root, "system-fonts");

        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "fonts"));
            Directory.CreateDirectory(Path.Combine(system, "nested"));
            File.WriteAllBytes(Path.Combine(folder, "fonts", "mine.ttf"), Asset("mine.ttf", "mine"));
            File.WriteAllBytes(Path.Combine(system, "candara.ttf"), Asset("candara.ttf", "candara"));
            File.WriteAllBytes(Path.Combine(system, "nested", "comicbd.ttf"), Asset("comicbd.ttf", "comic"));
            File.WriteAllBytes(Path.Combine(system, "cambria.ttc"), Bytes("collection"));
            File.WriteAllBytes(Path.Combine(system, "arial.ttf"), Asset("arial.ttf", "arial"));
            File.WriteAllBytes(Path.Combine(system, "Legacy.ttf"), Asset("Legacy.ttf", "legacy"));

            File.WriteAllText(Path.Combine(folder, LocalTranslationProbe.TranslationFileName),
                "{\"_uuid\":\"u\",\"_fonts\":{"
                + "\"A\":{\"fallback\":\"[Custom] mine\",\"type\":\"TMP\"},\"B\":{\"fallback\":\"Candara\",\"type\":\"TMP\"},"
                + "\"C\":{\"fallback\":\"comicbd\",\"type\":\"TMP\"},\"D\":{\"fallback\":\"Cambria\",\"type\":\"TMP\"},"
                + "\"E\":{\"fallback\":\"Missing\",\"type\":\"TMP\"},\"F\":{\"fallback\":\"[Game] LiberationSans SDF\",\"type\":\"TMP\"},"
                + "\"G\":{\"enabled\":false,\"fallback\":\"SwitchedOff\",\"type\":\"TMP\"},"
                + "\"H\":{\"fallback\":\"Legacy\",\"type\":\"Unity\"},\"I\":{\"fallback\":\"[Custom] arial\",\"type\":\"TMP\"}},"
                + "\"_font_overrides\":[{\"match\":\"\",\"replacement\":\"NoMatch\"},"
                + "{\"match\":\"Title\",\"enabled\":false,\"replacement\":\"RuleOff\"}]}");

            (string, string)[] table = [("Cambria", Path.Combine(system, "cambria.ttc"))];
            var uses = GameAssets.SystemFontsUsed(game, descriptor, [system], () => table);
            SystemFontUse Use(string name) => uses.Single(u => u.Reference == name);

            Program.Check(uses.Count == 5 && Use("Candara").Includable && Use("comicbd").Includable
                          && !Use("Cambria").Includable && Use("Cambria").Why!.Contains(".ttc")
                          && !Use("Missing").Includable && Use("Missing").Why!.Contains("not on this computer"),
                "installed fonts are found as the mod finds them, and each one that cannot go says why",
                "a name, a file name in a subfolder, the system's table; a collection and a missing font are named, not skipped");

            Program.Check(Use("Legacy").Includable,
                "an installed font used by legacy text is offered like any other",
                "legacy text reads a fonts/ copy too since the mod shows fonts/ to the engine (2026-09-28)");

            Program.Check(uses.All(u => u.Reference is not ("arial" or "[Custom] arial")),
                "a reference to fonts/ is never carried from the system, even when that font is installed",
                "\"[Custom] arial\" means the copy in fonts/; taking the system's would change the font it names");

            Program.Check(uses.All(u => u.Reference is not ("SwitchedOff" or "NoMatch" or "RuleOff")),
                "a font whose setting is switched off, or a rule with nothing to match, is not used",
                "read as the mod reads them: exporting them would carry fonts the game never shows");

            Program.Check(uses.All(u => u.Reference is not ("mine" or "LiberationSans SDF")),
                "a font fonts/ already provides, or one of the game's own, is not an installed font",
                "the first is exported anyway; the second is inside the game");

            var without = Path.Combine(root, "without.ugtpack");
            GameAssets.Export(game, descriptor, without, "checks");
            using (var zip = ZipFile.OpenRead(without))
            {
                Program.Check(zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("fonts/")).SequenceEqual(["fonts/mine.ttf"]),
                    "an export carries no installed font unless asked",
                    "off by default: their licences are the sharer's to check (user, 2026-09-27)");
            }

            var with = Path.Combine(root, "with.ugtpack");
            GameAssets.Export(game, descriptor, with, "checks", uses);
            using (var zip = ZipFile.OpenRead(with))
            {
                var fonts = zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("fonts/")).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Program.Check(fonts.SequenceEqual(["fonts/Candara.ttf", "fonts/Legacy.ttf", "fonts/comicbd.ttf", "fonts/mine.ttf"]),
                    "asked, it carries them under the name the translation uses — \"Candara.ttf\" from candara.ttf",
                    "the mod receiving the pack looks the name up exactly, among its own fonts first");
            }

            // That pack laid into the game itself — copies of installed fonts now in fonts/.
            File.WriteAllBytes(Path.Combine(folder, "fonts", "Candara.ttf"), Asset("Candara.ttf", "candara"));
            File.WriteAllBytes(Path.Combine(folder, "fonts", "Legacy.ttf"), Asset("Legacy.ttf", "legacy"));
            File.WriteAllBytes(Path.Combine(folder, "fonts", "unused.ttf"), Asset("unused.ttf", "unused"));
            File.WriteAllBytes(Path.Combine(folder, "fonts", "comicbd.ttf"), Asset("comicbd.ttf", "comic"));
            // A retouched copy of the game's own font, under its name — "[Game] LiberationSans SDF" above.
            File.WriteAllBytes(Path.Combine(folder, "fonts", "LiberationSans SDF.ttf"), Asset("LiberationSans SDF.ttf", "retouched"));

            var held = GameAssets.Read(gamePath, descriptor, name => name is "Candara" or "Legacy")
                .Fonts.ToDictionary(f => f.Name, f => f.Use);
            Program.Check(held["mine.ttf"] == FontUse.Used && held["comicbd.ttf"] == FontUse.Used
                          && held["Candara.ttf"] == FontUse.InstalledInstead && held["Legacy.ttf"] == FontUse.InstalledInstead
                          && held["LiberationSans SDF.ttf"] == FontUse.GameInstead && held["unused.ttf"] == FontUse.NotUsed,
                "the tab says which font is shown where the translation names a file's name: this file, the installed one, the game's",
                "a file carrying the name of an installed or game font is not the one shown — whoever retouched it must be told (user, 2026-09-28)");

            var copies = Path.Combine(root, "copies.ugtpack");
            GameAssets.Export(game, descriptor, copies, "checks");
            using (var zip = ZipFile.OpenRead(copies))
            {
                var fonts = zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("fonts/")).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Program.Check(fonts.SequenceEqual(["fonts/mine.ttf"]),
                    "unasked, only the Custom fonts go — copies of System fonts in fonts/ stay, like the installed ones",
                    "the licences of System fonts are the sharer's to check, wherever the file lies (user, 2026-09-28)");
            }

            var asked = GameAssets.SystemFontsUsed(game, descriptor, [system], () => table);
            Program.Check(asked.Any(u => u.Reference == "Candara" && u.Includable && u.Path!.StartsWith(Path.Combine(folder, "fonts")))
                          && asked.Any(u => u.Reference == "Legacy" && u.Includable),
                "a System font with a copy in fonts/ is still offered, from that copy",
                "the box is there as soon as the translation uses System fonts, whatever already lies in fonts/");

            var copiesAsked = Path.Combine(root, "copies-asked.ugtpack");
            GameAssets.Export(game, descriptor, copiesAsked, "checks", asked.Where(u => u.Includable).ToList());
            using (var zip = ZipFile.OpenRead(copiesAsked))
            {
                var fonts = zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("fonts/")).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Program.Check(fonts.SequenceEqual(["fonts/Candara.ttf", "fonts/Legacy.ttf", "fonts/comicbd.ttf", "fonts/mine.ttf"]),
                    "asked, the System fonts go, the copies from fonts/ included; the retouched game font and the unused one never",
                    "a file named like a game font is not the one shown, and one nothing uses is noise");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder left behind proves nothing */ }
        }
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>What a program starts with — the bytes every Windows executable opens on.</summary>
    private static readonly byte[] Program_ = [(byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00];

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] FontSignature = [0x00, 0x01, 0x00, 0x00, 0x00, 0x10, 0x01, 0x00];

    /// <summary>A file's bytes as a real one of its kind begins — the signature its extension promises, then the text.</summary>
    private static byte[] Asset(string name, string text)
    {
        var signature = AssetPacks.IsImageFile(name) ? PngSignature : AssetPacks.IsFontFile(name) ? FontSignature : [];
        return [.. signature, .. Bytes(text)];
    }

    private static bool Holds(string path, string text) =>
        File.ReadAllBytes(path).AsSpan().SequenceEqual(Asset(Path.GetFileName(path), text));

    private static void Entry(ZipArchive zip, string name, string content) => Raw(zip, name, Asset(name, content));

    private static void Raw(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }

    /// <summary>
    /// Rewrites the uncompressed size an archive DECLARES for one entry — in its local header and in
    /// the central directory — leaving what it actually holds untouched. How a zip bomb is built.
    /// </summary>
    private static void DeclareSize(string archive, string entryName, uint size)
    {
        var bytes = File.ReadAllBytes(archive);
        var name = Encoding.UTF8.GetBytes(entryName);

        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4B) continue;

            if (bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04
                && BitConverter.ToUInt16(bytes, i + 26) == name.Length
                && bytes.AsSpan(i + 30, name.Length).SequenceEqual(name))
            {
                BitConverter.GetBytes(size).CopyTo(bytes, i + 22);
            }
            else if (bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02
                     && BitConverter.ToUInt16(bytes, i + 28) == name.Length
                     && bytes.AsSpan(i + 46, name.Length).SequenceEqual(name))
            {
                BitConverter.GetBytes(size).CopyTo(bytes, i + 24);
            }
        }

        File.WriteAllBytes(archive, bytes);
    }
}
