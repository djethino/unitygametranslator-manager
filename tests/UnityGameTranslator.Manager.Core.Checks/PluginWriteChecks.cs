using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Whether putting the mod into a game would write anything — the question a one-click both
/// PROMISES and PERFORMS.
///
/// 🔴 **It had no answer at all, and the two halves disagreed in silence.** The confirmation
/// dialog lists what is about to happen, built from the report; the plan that then runs had
/// `InstallPlugin = plugin`, a pass-through. So on a game whose mod was already current the
/// dialog said nothing about the mod — correctly — and the mod was rewritten anyway.
///
/// Measured 2026-09-09: somebody ticked "also bring a translation down" on a game running a
/// locally built plugin, and the click put the published release back over it. Nothing warned
/// them, because from the dialog's point of view nothing about the mod was happening.
///
/// ⚠ **The dangerous direction is the one that reads as harmless.** Rewriting an identical file
/// changes nothing on most machines, which is exactly why it survived: it is only visible when
/// what is installed is NOT what the site publishes.
/// </summary>
internal static class PluginWriteChecks
{
    private static GameReport Report(string? installed, string? available) => new()
    {
        Game = new GameInstall
        {
            Name = "A game",
            Path = @"C:\games-game",
            Verdict = ModdabilityVerdict.Ok,
        },
        InstalledPluginVersion = installed,
        PluginStanding = installed is null && available is null
            ? null
            : new VersionStanding(installed, available),
    };

    internal static void WhenTheModWouldBeWritten()
    {
        Program.Section("When putting the mod in would write something");

        Program.Check(Report(installed: null, available: "0.13.1").PluginWriteOffered,
            "nothing here yet: it is written",
            "this is the ordinary first install, and the one-click promises it");

        Program.Check(Report(installed: "0.13.0", available: "0.13.1").PluginWriteOffered,
            "something newer published: it is written",
            "the dialog says 'update the mod to 0.13.1' and the plan must then do it");

        Program.Check(!Report(installed: "0.13.1", available: "0.13.1").PluginWriteOffered,
            "already current: it is NOT written",
            "this is the case that cost a locally built plugin — the dialog said nothing about the mod and the mod was replaced");

        // 🔴 Strictly newer, never merely different — VersionStanding's own rule, and this is the
        // shape that made it matter here: a build that is AHEAD of what the site publishes.
        Program.Check(!Report(installed: "0.13.2", available: "0.13.1").PluginWriteOffered,
            "and something ahead of the published one is left alone",
            "replacing it would be a downgrade described as an update");

        Program.Check(!Report(installed: "0.13.1", available: null).PluginWriteOffered,
            "nothing known about what is published: it is left alone",
            "the catalogue being silent is not a reason to write over what runs");

        TheActObeysThePromise();
    }

    /// <summary>
    /// The plan that runs is built from the same answer the dialog was built from.
    ///
    /// 🔴 **Pinning the rule alone would have caught nothing.** The rule was never wrong — there
    /// simply was none on the acting side: `InstallPlugin = plugin`, a parameter passed straight
    /// through. So the defect lives in the WIRING, and the wiring is in a window this project
    /// cannot instantiate here.
    ///
    /// ⚠ Lexical, therefore, and narrow on purpose: it asks only that the plan's plugin decision
    /// mentions the report's answer at all. That is exactly what was missing, and it is what a
    /// fourth screen added later would miss again.
    /// </summary>
    private static void TheActObeysThePromise()
    {
        var window = Find("src", "UnityGameTranslator.Manager.Gui", "MainWindow.axaml.cs");

        Program.Check(window is not null, "the window's source is found",
            "this check reads it; without it, it proves nothing");
        if (window is null) return;

        var text = File.ReadAllText(window);
        var at = text.IndexOf("InstallPlugin =", StringComparison.Ordinal);

        Program.Check(at >= 0, "the plan still decides InstallPlugin",
            "the check is anchored on it; renamed, it must say so rather than pass quietly");
        if (at < 0) return;

        var line = text.Substring(at, Math.Min(220, text.Length - at));
        var decision = line.Substring(0, line.IndexOf(',') < 0 ? line.Length : line.IndexOf(','));

        Program.Check(decision.Contains("PluginWriteOffered", StringComparison.Ordinal),
            "and decides it from PluginWriteOffered",
            "the dialog is built from that answer; a plan that ignores it does what nobody was told about");

        // ⚠ And "asked for by name" must still get through, or the mod's own button would confirm,
        // run, report success and replace nothing — the exact trap the loader's comment records.
        Program.Check(decision.Contains("force", StringComparison.Ordinal),
            "while a repair asked for by name still writes",
            "a reinstall on a current game is a legitimate act, and it is what force is for");
    }

    private static string? Find(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
