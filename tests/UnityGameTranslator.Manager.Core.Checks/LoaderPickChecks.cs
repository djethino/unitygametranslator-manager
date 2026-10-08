using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// The loader picked on a game's card is the one the one-click names AND the one it installs.
///
/// 🔴 **Measured 2026-10-08: MelonLoader picked, "install BepInEx" announced.** The pick lived in
/// the picker and only the install read it back; the steps and the confirmation read the report's
/// recommendation. Every redraw put the picker back on the first loader, and the closure followed
/// the reader onto the next game opened on its other tab. The pick now goes through LoaderPicks,
/// which the inventory lays over GameReport.RecommendedLoader — the field both halves read.
/// </summary>
internal static class LoaderPickChecks
{
    private static LoaderDescriptor Loader(string id) => new()
    {
        Id = id,
        Display = id,
        Runtimes = new() { "mono" },
        PluginDir = "Mods",
    };

    internal static void WhichLoaderIsUsed()
    {
        Program.Section("Loader picks: the one picked is the one named and installed");

        var bepinex = Loader("bepinex5");
        var melon = Loader("melonloader");
        var fits = new[] { bepinex, melon };

        Program.Check(LoaderPicks.Resolve(fits, null) == bepinex,
            "nothing picked: the first that fits",
            "the catalogue's order is the default, and nobody has answered");
        Program.Check(LoaderPicks.Resolve(fits, "MelonLoader") == melon,
            "a pick among those that fit: the pick, whatever its case",
            "this is the case that announced BepInEx while installing MelonLoader");
        Program.Check(LoaderPicks.Resolve(fits, "bepinex6-mono") == bepinex,
            "a pick that no longer fits: the first that fits",
            "a pick never makes a loader fit — the catalogue or a preference may have moved since");
        Program.Check(LoaderPicks.Resolve(Array.Empty<LoaderDescriptor>(), "melonloader") is null,
            "nothing fits: nothing, pick or not",
            "a game no loader can host is refused before anybody picks");

        // Held per game, the build with its loader.
        var game = Path.Combine(Path.GetTempPath(), "ugt-loader-pick-check");
        var other = Path.Combine(Path.GetTempPath(), "ugt-loader-pick-other");
        var older = new LoaderBuild("0.6.0", null, Array.Empty<LoaderAsset>(), "test", false);

        LoaderPicks.PickLoader(game, melon.Id);
        LoaderPicks.PickBuild(game, melon.Id, older);

        Program.Check(LoaderPicks.LoaderFor(game + Path.DirectorySeparatorChar + ".") == melon.Id,
            "the pick is the game's, however its path is spelled",
            "the card and the inventory reach the same folder by different strings");
        Program.Check(LoaderPicks.LoaderFor(other) is null && !LoaderPicks.AnyFor(other),
            "another game has no pick",
            "the closure that held the picker followed the reader onto the next game");
        Program.Check(LoaderPicks.BuildFor(game, bepinex.Id) is null && LoaderPicks.BuildFor(game, melon.Id) == older,
            "a build is handed only to its own loader",
            "a MelonLoader build given to a BepInEx install would fetch nothing that fits");

        LoaderPicks.PickLoader(game, bepinex.Id);
        Program.Check(LoaderPicks.BuildFor(game, melon.Id) is null,
            "picking another loader drops the build held for the previous one",
            "switching back later must not revive a build chosen for a loader since abandoned");

        LoaderPicks.PickBuild(game, bepinex.Id, older);
        LoaderPicks.Settled(game);
        Program.Check(!LoaderPicks.AnyFor(game),
            "an install that put the loader in place clears the picks",
            "the installed loader wins from then on; a pick left behind would light Undo over nothing");

        // The plan follows the report: what the steps name is what is installed.
        var report = new GameReport
        {
            Game = new GameInstall
            {
                Name = "A game",
                Path = game,
                Verdict = ModdabilityVerdict.Ok,
                Runtime = UnityRuntime.Mono,
            },
            EligibleLoaders = fits,
            RecommendedLoader = LoaderPicks.Resolve(fits, melon.Id),
        };

        var catalog = new LoaderCatalogDocument
        {
            Loaders = fits.ToList(),
            PluginBuilds = { ["bepinex5:mono"] = "a-{version}.zip", ["melonloader:mono"] = "b-{version}.zip" },
        };

        var plan = new InstallEngine(PlatformFactory.Create(), catalog).Plan(report);
        Program.Check(plan?.Loader == melon,
            "with no override, the plan installs the report's loader",
            "the window passes none, so the loader its steps name is the one planned");
    }

    /// <summary>
    /// 🔴 The window holds no loader of its own. A loader kept by a control's closure is how the
    /// steps and the install came to read two different answers — lexical, like DropdownChecks.
    /// </summary>
    internal static void NoLoaderHeldByTheWindow()
    {
        Program.Section("Loader picks: none held by a control");

        var gui = FindDirectory("src", "UnityGameTranslator.Manager.Gui");
        Program.Check(gui is not null, "the window's sources are found", "this check reads them; without them, it proves nothing");
        if (gui is null) return;

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(gui, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)) continue;

                if (code.Contains("Func<LoaderDescriptor", StringComparison.Ordinal)
                    || code.Contains("Func<LoaderBuild", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{number}");
                }
            }
        }

        Program.Check(offenders.Count == 0,
            "no loader or build read back from a control: the pick goes through LoaderPicks",
            offenders.Count == 0 ? "the steps, the confirmation and the plan read the same report"
                                 : "found at " + string.Join(", ", offenders));
    }

    private static string? FindDirectory(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
