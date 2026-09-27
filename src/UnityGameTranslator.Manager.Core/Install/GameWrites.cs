using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// Whether this tool may write into a game's folder right now — the ONE answer every writer asks.
///
/// 🔴 **Not a locked file — a lost one.** UGT Mod holds the translation and its configuration in
/// memory and rewrites each file WHOLE on its own timer. A file written here while a game runs is
/// in conflict with nothing: it is overwritten at the mod's next save, silently, and the person
/// sees the change vanish minutes later with nothing said. That is worse than a refusal, which is
/// why this is a refusal.
///
/// ⚠ It used to live in <see cref="TranslationInstaller"/> alone, beside the install and uninstall
/// engines' own copies — and the configuration writer and the backups store asked nobody at all:
/// a game's settings applied, or a backup restored, while it ran were lost the same way.
///
/// ⚠ The check is <see cref="IPlatform.IsGameRunning"/> — the precise one, which opens each
/// candidate process — and not the cheap sweep the game list uses. That one answers "not running"
/// for a game belonging to another operating-system account, and this is exactly the machine where
/// several accounts share one game folder. The interface greys its buttons from the sweep; this is
/// what actually refuses.
/// </summary>
public static class GameWrites
{
    public const string RunningRefusal =
        "This game is running. Close it first: UGT Mod saves its files while it runs "
        + "and would overwrite the change.";

    /// <summary>
    /// Null when writing into this game is allowed now; the reason otherwise.
    ///
    /// ⚠ Fails towards refusing: an answer we cannot get is not permission — the cost of a
    /// needless refusal is a second attempt, the cost of a wrong permission is somebody's work.
    /// A null platform is a caller holding a game that provably cannot be running (none today).
    /// </summary>
    public static string? WhyNotNow(IPlatform? platform, GameInstall game)
    {
        if (platform is null) return null;

        try
        {
            return platform.IsGameRunning(game) ? RunningRefusal : null;
        }
        catch
        {
            return RunningRefusal;
        }
    }

    /// <summary>The same answer for a writer that only knows the game's folder.</summary>
    public static string? WhyNotNow(IPlatform? platform, string gamePath) =>
        WhyNotNow(platform, new GameInstall { Name = System.IO.Path.GetFileName(gamePath), Path = gamePath });
}
