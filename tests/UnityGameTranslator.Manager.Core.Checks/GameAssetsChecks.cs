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
            File.WriteAllBytes(Path.Combine(folder, "fonts", "a.ttf"), Bytes("font a"));
            File.WriteAllBytes(Path.Combine(folder, "images", "title.png"), Bytes("retouched by hand"));
            File.WriteAllText(translation,
                "{\"_uuid\":\"u\",\"hello\":{\"v\":\"Bonjour\",\"t\":\"A\"},"
                + "\"_fonts\":{\"LiberationSans SDF\":{\"enabled\":true,\"fallback\":\"[Custom] a\",\"type\":\"TMP\"}},"
                + "\"_image_replacements\":[" + DefinitionTitle + "]}");

            var state = GameAssets.Read(gamePath, descriptor);
            Program.Check(state is { Fonts.Count: 1, Images.Count: 1, HasTranslation: true } && state.Images[0].Present
                          && state.Fonts[0].Used,
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
            File.WriteAllBytes(loosePng, Bytes("stray"));
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
            var result = GameAssets.Apply(null, game, descriptor, kept);

            Program.Check(result.Done && File.ReadAllText(Path.Combine(folder, "images", "title.png")) == "retouched by hand"
                          && File.ReadAllText(Path.Combine(folder, "fonts", "b.otf")) == "font b"
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
                          && File.ReadAllText(Path.Combine(backup, "images", "title.png")) == "retouched by hand",
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
                          && !GameAssets.Read(gamePath, descriptor).Fonts.Single(f => f.Name == "b.otf").Used,
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

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
