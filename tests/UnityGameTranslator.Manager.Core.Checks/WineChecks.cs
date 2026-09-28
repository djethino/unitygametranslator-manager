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
