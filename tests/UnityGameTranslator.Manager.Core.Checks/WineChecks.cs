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
            Program.Check(text.Contains("executable_name=\"Game.x86_64\"") && text.StartsWith("#!/bin/sh\n"),
                "the script is given the game's executable, nothing else changed",
                "started directly, run_bepinex.sh stops on an empty executable_name");
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }
}
