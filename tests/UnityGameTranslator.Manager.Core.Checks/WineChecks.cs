using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>A Windows build run through Wine off Windows — by Steam's Proton, or by another launcher.</summary>
internal static class WineChecks
{
    internal static void WhereTheOverrideGoes()
    {
        Program.Section("A Windows build through Wine: where the DLL override goes");

        var proton = new GameInstall { Name = "Steam game", Path = "/games/a", RunsUnderProton = true };
        var heroic = new GameInstall { Name = "Heroic game", Path = "/games/b" };

        var (steamWhere, steamSetting) = GameLaunch.DllOverrideAdvice(proton, "winhttp");
        var (otherWhere, otherSetting) = GameLaunch.DllOverrideAdvice(heroic, "winhttp");

        Program.Check(steamWhere.Contains("Steam launch options") && steamSetting == "WINEDLLOVERRIDES=\"winhttp=n,b\" %command%"
                      && otherWhere.Contains("Heroic") && !otherWhere.Contains("Steam") && otherSetting == "WINEDLLOVERRIDES=winhttp=n,b",
            "Proton gets Steam's launch option, another launcher an environment variable",
            "told 'Steam launch options' for a Heroic game, somebody had nowhere to put it (2026-09-28)");
    }
}

/// <summary>A native Linux game: nothing starts the loader unless the game is launched through it.</summary>
internal static class NativeLaunchChecks
{
    internal static void HowALinuxGameStartsItsLoader()
    {
        Program.Section("A native Linux game: how the loader is started");

        var bepinex = new LoaderDescriptor { Id = "bepinex5", Display = "BepInEx 5" };
        var melon = new LoaderDescriptor { Id = "melonloader", Display = "MelonLoader" };
        var steamGame = new GameInstall { Name = "Native", Path = "/games/native", SteamAppId = "1", ExecutablePath = "/games/native/Game.x86_64" };
        var windowsBuild = new GameInstall { Name = "Wine", Path = "/games/wine", ExecutablePath = "/games/wine/Game.exe" };

        Program.Check(NativeLaunch.Applies(steamGame, "linux") && !NativeLaunch.Applies(windowsBuild, "linux")
                      && !NativeLaunch.Applies(steamGame, "windows"),
            "only a native build on Linux",
            "a Windows build goes through Wine and its DLL override instead");

        Program.Check(NativeLaunch.Advice(steamGame, bepinex) is { Setting: "./run_bepinex.sh %command%" },
            "BepInEx on Steam: run_bepinex.sh as the launch option",
            "installed without it, the game started with no loader and nothing said why (2026-09-28)");

        Program.Check(NativeLaunch.Advice(steamGame, melon) is { } ml
                      && ml.Setting.Contains("LD_PRELOAD=\"MelonLoader.Bootstrap.so:$LD_PRELOAD\"")
                      && ml.Setting.Contains("LD_LIBRARY_PATH=\"/games/native:"),
            "MelonLoader: preloaded by name, its folder on the library path",
            "a full path in LD_PRELOAD is ignored by the loader (MelonLoader's own guidance)");

        var staging = Path.Combine(Path.GetTempPath(), "ugt-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllText(Path.Combine(staging, NativeLaunch.BepInExScript), "#!/bin/sh\nexecutable_name=\"\"\nexit 0\n");
            NativeLaunch.PrepareExtracted(staging, bepinex, steamGame);
            var text = File.ReadAllText(Path.Combine(staging, NativeLaunch.BepInExScript));
            Program.Check(text.Contains("executable_name=\"Game.ugt\"") && text.StartsWith("#!/bin/sh\n"),
                "the script is given the game's renamed executable, nothing else changed",
                "started directly, run_bepinex.sh stops on an empty executable_name");
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }

        Program.Check(NativeLaunch.MovedName("Bioprototype.x86_64") == "Bioprototype.ugt"
                      && NativeLaunch.MovedName("Tap Ninja.x86_64") == "Tap Ninja.ugt"
                      && NativeLaunch.MovedName("Game") == "Game.ugt",
            "the renamed executable keeps the stem Unity finds its data by",
            "Unity looks for <name>_Data under the executable's name without its last extension");
    }

    /// <summary>
    /// The start file, as a sequence on real files — the moment matters: a game update comes
    /// between the install and the uninstall, and each step must read what the previous one left.
    /// </summary>
    internal static void TheStartFileThroughAGameUpdate()
    {
        Program.Section("A native Linux game: the start file, through a game update");

        var bepinex = new LoaderDescriptor { Id = "bepinex5", Display = "BepInEx 5" };
        var root = Path.Combine(Path.GetTempPath(), "ugt-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "My Game.x86_64");
            var moved = Path.Combine(root, "My Game.ugt");
            File.WriteAllText(executable, "ELF version 1");
            var game = new GameInstall { Name = "My Game", Path = root, ExecutablePath = executable };

            var recorded = NativeLaunch.Install(game, bepinex, existing: null);
            var script = File.ReadAllText(executable);
            Program.Check(recorded is { Executable: "My Game.x86_64", MovedTo: "My Game.ugt" }
                          && File.ReadAllText(moved) == "ELF version 1"
                          && NativeLaunch.IsOurStartFile(executable)
                          && script.Contains("exec ./run_bepinex.sh './My Game.ugt' \"$@\"")
                          && !script.Contains('\r'),
                "install: the game renamed, the start file under its name",
                "every launcher starts the game through the loader, with nothing typed (2026-09-28)");

            Program.Check(NativeLaunch.Install(game, bepinex, recorded) is not null
                          && File.ReadAllText(moved) == "ELF version 1",
                "installing again leaves the renamed game as it is",
                "moving the start file onto the game would destroy the game");

            // Steam updates the game: the real executable comes back over the start file.
            File.WriteAllText(executable, "ELF version 2");
            Program.Check(NativeLaunch.IsBroken(root, recorded!),
                "a game update over the start file is seen",
                "the game then runs without the mod, and nothing on screen would say why");

            NativeLaunch.Install(game, bepinex, recorded);
            Program.Check(File.ReadAllText(moved) == "ELF version 2" && NativeLaunch.IsOurStartFile(executable),
                "the next update takes the new executable and puts the start file back",
                "keeping the old copy would start the game from before its update");

            Program.Check(script.Contains("[ -x ./run_bepinex.sh ] || exec './My Game.ugt' \"$@\""),
                "without its loader, the start file still starts the game",
                "a loader deleted by hand would otherwise leave a game that no longer launches");

            var removed = new List<string>();
            NativeLaunch.Remove(root, recorded!, removed);
            Program.Check(File.ReadAllText(executable) == "ELF version 2" && !File.Exists(moved),
                "uninstall gives the game its executable back",
                "the game folder must end as the game left it");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        // Not a Linux program where the game should be: left alone, the launch option given instead.
        var other = Path.Combine(Path.GetTempPath(), "ugt-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            var launcher = Path.Combine(other, "Game.x86_64");
            const string script = "#!/bin/sh\nexec ./something-else\n";
            File.WriteAllText(launcher, script);
            var game = new GameInstall { Name = "Game", Path = other, ExecutablePath = launcher };

            Program.Check(NativeLaunch.Install(game, bepinex, existing: null) is null
                          && File.ReadAllText(launcher) == script
                          && !File.Exists(Path.Combine(other, "Game.ugt")),
                "a file that is not a Linux program is never renamed",
                "renaming something that is not the game Unity built could leave it unable to start");
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }
}

/// <summary>The loader's DLL override, written into the game's Wine prefix instead of typed into a launcher.</summary>
internal static class WinePrefixChecks
{
    private const string Header = "WINE REGISTRY Version 2\n;; All keys relative to \\User\n\n#arch=win64\n\n";

    internal static void WhatThePrefixHolds()
    {
        Program.Section("The Wine prefix: the override written, and taken back");

        var folder = Path.Combine(Path.GetTempPath(), "ugt-prefix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // No section yet: the common case on a fresh Proton prefix.
            var fresh = Path.Combine(folder, "fresh.reg");
            // Verbatim: user.reg doubles the backslashes of a key, and so must this.
            var freshText = Header + @"[Software\\Wine\\Fonts] 1790000000" + "\n\"LogPixels\"=dword:00000060\n";
            File.WriteAllText(fresh, freshText);

            var recorded = WinePrefixOverride.Set(fresh, "winhttp");
            Program.Check(WinePrefixOverride.IsSet(fresh, "winhttp") && recorded.Previous is null
                          && File.ReadAllText(fresh).Contains(@"[Software\\Wine\\DllOverrides] ")
                          && File.ReadAllText(fresh).Contains("\"LogPixels\"=dword:00000060"),
                "a missing section is added, the rest left as it was",
                "the loader never started under Proton without a launch option somebody had to type");

            WinePrefixOverride.Restore(recorded);
            Program.Check(!WinePrefixOverride.IsSet(fresh, "winhttp"),
                "uninstall removes an entry that was not there before",
                "leaving it would keep Wine preferring a DLL the uninstall deleted");

            // A value of the person's own: replaced, remembered, and put back.
            var own = Path.Combine(folder, "own.reg");
            var ownText = Header + @"[Software\\Wine\\DllOverrides] 1790000000"
                          + "\n\"*d3dcompiler_47\"=\"native\"\n\"winhttp\"=\"builtin\"\n";
            File.WriteAllText(own, ownText);

            var mine = WinePrefixOverride.Set(own, "winhttp");
            Program.Check(mine.Previous == "builtin" && WinePrefixOverride.IsSet(own, "winhttp")
                          && File.ReadAllText(own).Contains("\"*d3dcompiler_47\"=\"native\""),
                "an existing value is replaced and remembered",
                "the uninstall must be able to put back what the prefix held");

            WinePrefixOverride.Restore(mine);
            Program.Check(File.ReadAllText(own) == ownText,
                "uninstall puts the prefix back exactly",
                "a registry left different from how it was found is a change nobody asked for");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
