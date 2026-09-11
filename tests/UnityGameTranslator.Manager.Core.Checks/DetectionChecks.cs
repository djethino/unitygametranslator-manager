using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// What makes a folder a game, and how far below a folder one is looked for.
///
/// 🔴 **The stake is a list nobody can check.** A scanner that misses a game says nothing at all —
/// the game is simply absent, and absence looks exactly like "you do not own that one". A scanner
/// that invents one is no better: it offers to install a loader into a folder that cannot run it.
/// Both were happening at once on 2026-09-11, and neither was noticed until two tools that scan
/// the same machine were made to print their counts side by side.
///
/// ⚠ **These cases build real folders, which the other checks here deliberately never do.** The
/// protocol in Program.cs asks for rules that answer without a disk, and it is the right default —
/// but detection IS a question about a disk, and the defect it hid could not be phrased any other
/// way. The two that cost something were shapes on a filesystem: a folder left behind by an
/// uninstall, and a game one level below where the store said it was. So the fixtures are made in
/// a temp folder, are a few empty files, and are removed whatever happens.
///
/// ⚠ Nothing here reads the machine's real libraries. A check whose answer depends on which games
/// somebody owns is a check that fails for the next person.
/// </summary>
internal static class DetectionChecks
{
    internal static void WhatMakesAFolderAGame()
    {
        Program.Section("What makes a folder a game");

        using var sandbox = new Sandbox();

        // 🔴 The case that shipped a wrong answer. Uninstalling leaves behind what the store never
        // wrote — a mod folder under <Game>_Data/Managed/ — and "a *_Data holding Managed" was the
        // whole test. 60 MB of Harmony and Cecil were reported as an installed game.
        var leftover = sandbox.Folder("Leftover");
        sandbox.File(leftover, "Leftover_Data/Managed/0Harmony.dll");
        sandbox.File(leftover, "Leftover_Data/Managed/Mono.Cecil.dll");
        sandbox.File(leftover, "unins000.exe");

        Program.Check(!UnityGameProbe.IsGameFolder(leftover),
            "mod DLLs under Managed/ are not a game",
            "an uninstalled game keeps its mod folder for ever; it was listed as installed");

        // ⚠ And the same folder must stay refused through the walk, not only through the probe:
        // the two used to disagree, which is how it reached the list.
        Program.Check(UnityGameProbe.FindGameFolders(sandbox.Root).All(f => f != leftover),
            "and the walk does not return it either",
            "a folder refused by the probe and returned by the walk is a row nothing can explain");

        var mono = sandbox.Folder("MonoGame");
        sandbox.File(mono, "MonoGame_Data/Managed/UnityEngine.dll");
        Program.Check(UnityGameProbe.IsGameFolder(mono),
            "a runtime assembly under Managed/ is a game",
            "refusing it would drop every Mono game whose assets we cannot read");

        var assets = sandbox.Folder("AssetsOnly");
        sandbox.File(assets, "AssetsOnly_Data/globalgamemanagers");
        Program.Check(UnityGameProbe.IsGameFolder(assets),
            "the serialised assets are a game on their own",
            "they are what Unity writes; nothing else produces them");

        var il2cpp = sandbox.Folder("Il2CppGame");
        sandbox.File(il2cpp, "Il2CppGame_Data/il2cpp_data/Metadata/global-metadata.dat");
        Program.Check(UnityGameProbe.IsGameFolder(il2cpp),
            "IL2CPP metadata is a game",
            "an IL2CPP game has no Managed/ at all");

        var player = sandbox.Folder("PlayerOnly");
        sandbox.File(player, "UnityPlayer.dll");
        Program.Check(UnityGameProbe.IsGameFolder(player),
            "UnityPlayer alone is a game",
            "a game can lay its data out in a way we do not read, but it cannot run without it");

        var empty = sandbox.Folder("NotAGame");
        sandbox.File(empty, "readme.txt");
        Program.Check(!UnityGameProbe.IsGameFolder(empty),
            "an ordinary folder is not a game",
            "the cheapest way to be sure the test says no to something");
    }

    internal static void HowFarBelowAFolderWeLook()
    {
        Program.Section("How far below a folder a game is looked for");

        using var sandbox = new Sandbox();

        // The layout publishers actually use: a launcher beside the game, or a repack that nests
        // the real thing one level down. Both were invisible to the Steam scanner.
        var nested = sandbox.Folder("Publisher");
        sandbox.File(nested, "launcher/launcher.exe");
        sandbox.File(nested, "fs/fs_Data/globalgamemanagers");

        var atOne = UnityGameProbe.FindGameFolders(nested).ToList();
        Program.Check(atOne.Count == 1 && atOne[0].EndsWith("fs"),
            "a game one level down is found",
            "two games on this machine were installed exactly like this and were reported as none");

        var deep = sandbox.Folder("Deep");
        sandbox.File(deep, "tools/thing/win64/thing_Data/globalgamemanagers");
        Program.Check(UnityGameProbe.FindGameFolders(deep).Count() == 0,
            "three levels down is out of reach, on purpose",
            "the only thing found that deep was a lot of accessory tools wearing another product's name");

        // 🔴 A game folder is a LEAF. A game ships data that looks like a game from outside; going
        // in would return its own innards as siblings of it.
        var leaf = sandbox.Folder("Leaf");
        sandbox.File(leaf, "Leaf_Data/globalgamemanagers");
        sandbox.File(leaf, "Leaf_Data/inner/inner_Data/globalgamemanagers");

        var leaves = UnityGameProbe.FindGameFolders(leaf).ToList();
        Program.Check(leaves.Count == 1 && leaves[0] == leaf,
            "a game folder is a leaf",
            "descending into one turns a single game into a game plus whatever its data resembles");

        Program.Check(UnityGameProbe.NestingDepth == 2,
            "the depth is stated once, and it is two",
            "written per call site it was 2 three times and 1 for GOG, for no reason anybody gave");
    }

    internal static void WhatAStoreManifestNames()
    {
        Program.Section("What a store manifest names");

        using var sandbox = new Sandbox();

        var one = sandbox.Folder("OneGame");
        sandbox.File(one, "Simulator/Simulator_Data/globalgamemanagers");

        var single = UnityGameProbe.ProbeDeclaredFolder(one, "Declared Name", GameStore.Steam, "1743860");
        Program.Check(single.Count == 1
                      && single[0].Name == "Declared Name"
                      && single[0].SteamAppId == "1743860",
            "one game found: the manifest names it",
            "the app id is what finds a community translation; dropping it searches by name instead");

        // 🔴 A manifest names a PRODUCT, and a product is not always one game. SteamVR ships three
        // Unity applications; handing each the manifest's name and id makes three identical rows
        // pointing the community lookup at a title none of them is.
        var several = sandbox.Folder("Suite");
        sandbox.File(several, "tool_a/tool_a_Data/globalgamemanagers");
        sandbox.File(several, "tool_b/tool_b_Data/globalgamemanagers");

        var many = UnityGameProbe.ProbeDeclaredFolder(several, "Declared Name", GameStore.Steam, "250820");
        Program.Check(many.Count == 2,
            "several games found: all of them are kept",
            "picking one would be picking arbitrarily, and dropping them hides installed games");

        Program.Check(many.All(g => g.SteamAppId is null),
            "and none of them inherits the app id",
            "a wrong name is read and dismissed; a wrong id answers quietly with another game's translations");

        Program.Check(many.All(g => g.Name != "Declared Name"),
            "nor the declared name",
            "two rows with one name is a list nobody can act on");
    }

    /// <summary>
    /// A throwaway tree of empty files. Deleted on the way out, including when a case throws —
    /// a check that leaves folders behind makes the next run answer about the last one.
    /// </summary>
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "ugt-detection-checks-" + Guid.NewGuid().ToString("N"));

        public Sandbox() => Directory.CreateDirectory(Root);

        public string Folder(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>Creates an empty file at a relative path, parents included.</summary>
        public void File(string folder, string relative)
        {
            var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* a locked temp folder is not a failed check */ }
        }
    }
}
