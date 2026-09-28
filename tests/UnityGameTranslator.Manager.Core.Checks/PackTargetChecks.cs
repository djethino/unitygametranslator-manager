using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// A pack double-clicked in the file explorer: which game it goes to, and the file handed over.
/// </summary>
internal static class PackTargetChecks
{
    private static GameInstall Game(string name, string? steamId = null, string? product = null) =>
        new() { Name = name, Path = @"C:\games\" + name, SteamAppId = steamId, ProductName = product };

    private static PackManifest Manifest(string? name, string? steamId = null) =>
        new() { Format = 1, GameName = name, SteamId = steamId };

    internal static void WhichGameAPackGoesTo()
    {
        Program.Section("Which game a pack opened from the file explorer goes to");

        var foo = Game("Foo", "100");
        var bar = Game("Bar Deluxe", product: "Bar");
        var other = Game("Other");
        var games = new[] { foo, bar, other };

        Program.Check(PackTargets.For(Manifest("Renamed", "100"), games).Found == foo,
            "the Steam id finds the game, whatever the name", "a folder named otherwise is still that game");

        Program.Check(PackTargets.For(Manifest("Foo", "999"), games).Found is null,
            "two different Steam ids are two games, even under one name",
            "the Assets tab would call it made for another game");

        Program.Check(PackTargets.For(Manifest("bar"), games).Found == bar,
            "the product name counts as the name", "the export writes the product name when it has one");

        var twice = new[] { foo, Game("Foo", "100") };
        var two = PackTargets.For(Manifest("Foo", "100"), twice);
        Program.Check(two.Found is null && two.Likely.Count == 2,
            "two installs of the same game are asked about, listed first",
            "found means one game, not a guess between two");

        var loose = PackTargets.For(Manifest("Other: Special Edition"), games);
        Program.Check(loose.Found is null && loose.Likely.SequenceEqual([other]),
            "a name that only resembles one is a suggestion, never a choice", "the person picks");

        Program.Check(PackTargets.For(Manifest(null, "100"), [Game("NoId")]).Found is null
                      && PackTargets.For(Manifest(null), games) is { Found: null, Likely.Count: 0 },
            "a pack that names nothing comparable matches nothing", "nothing to compare is not a match");

        Program.Check(PackFileType.PackIn(["--flag", "C:\\nowhere\\missing.ugtpack", "notes.txt"]) is null,
            "only an existing .ugtpack is a pack handed over", "the same test guards what another copy sends");

        Program.Check(PackFileType.IconSizes.All(size => PackFileType.Resource(PackFileType.PngFile(size)).Length > 0)
                      && PackFileType.Resource(PackFileType.WindowsIconFile).Length > 0,
            "every icon the file type declares is in the build",
            "a missing one throws at install, on the person's machine");

        // The default xdg-mime recorded, taken back at uninstall (seen left behind on Bazzite).
        const string ours = "unitygametranslator-manager-ugtpack.desktop";
        const string list = "[Default Applications]\ntext/plain=org.kde.kate.desktop\n"
                            + "application/x-ugtpack=" + ours + "\n";
        Program.Check(PackFileType.WithoutOurDefault(list, ours)
                      == "[Default Applications]\ntext/plain=org.kde.kate.desktop\n",
            "uninstall takes our default out of mimeapps.list",
            "the line was left pointing at a desktop file that no longer exists");

        Program.Check(PackFileType.WithoutOurDefault("application/x-ugtpack=other.desktop;" + ours + ";\n", ours)
                      == "application/x-ugtpack=other.desktop;\n"
                      && PackFileType.WithoutOurDefault(list.Replace(ours, "other.desktop"), ours)
                         == list.Replace(ours, "other.desktop"),
            "another program chosen for the type is kept, and nothing else is touched",
            "the file is the person's; only our own entry is ours to remove");
    }
}
