using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// How a loader is started in a NATIVE Linux game — the counterpart of Proton's DLL override.
///
/// 🔴 **Nothing loads a loader into a native Linux game by itself** (measured in a Bazzite VM,
/// 2026-09-28). On Windows and under Wine, the game picks up a DLL placed beside it; on Linux it
/// must be started through a preload: BepInEx ships <c>run_bepinex.sh</c>, MelonLoader a library
/// for <c>LD_PRELOAD</c>. The install used to end on "is ready" with neither said — and the script
/// not even executable, since a zip made on Windows carries no execute bit.
///
/// ⚠ Behaviour, not data: which file, which variable and what to prepare are decided here, where
/// a release decides them, rather than in the catalogue that every installation fetches.
/// </summary>
public static class NativeLaunch
{
    public const string BepInExScript = "run_bepinex.sh";

    /// <summary>MelonLoader 0.7 and later, then the name 0.6 used.</summary>
    private static readonly string[] MelonPreloads = ["MelonLoader.Bootstrap.so", "libversion.so"];

    /// <summary>True when this game on this system needs a loader started for it.</summary>
    public static bool Applies(GameInstall game, string osId) =>
        osId == "linux" && !game.IsWindowsBuild;

    private static bool IsBepInEx(LoaderDescriptor loader) =>
        loader.Id.StartsWith("bepinex", StringComparison.OrdinalIgnoreCase);

    private static bool IsMelonLoader(LoaderDescriptor loader) =>
        loader.Id.StartsWith("melonloader", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Readies the extracted loader BEFORE it is copied: BepInEx's script gets the game's
    /// executable, so starting the script directly works too (Steam passes it on its own).
    ///
    /// ⚠ Before the copy on purpose: the receipt records each file's hash as copied, and
    /// uninstall keeps a file whose hash no longer matches, taking it for the person's own edit.
    /// </summary>
    public static void PrepareExtracted(string extractedRoot, LoaderDescriptor loader, GameInstall game)
    {
        if (!IsBepInEx(loader)) return;

        var script = Path.Combine(extractedRoot, BepInExScript);
        if (!File.Exists(script) || game.ExecutablePath is not { } executable) return;

        var text = File.ReadAllText(script);
        const string empty = "executable_name=\"\"";
        var index = text.IndexOf(empty, StringComparison.Ordinal);
        if (index < 0) return;

        // The file name only: the script resolves it against its own folder, the game's.
        var named = $"executable_name=\"{Path.GetFileName(executable)}\"";
        File.WriteAllText(script, text.Substring(0, index) + named + text.Substring(index + empty.Length));
    }

    /// <summary>
    /// After the copy: the files that must be executable, made so. A zip written on Windows
    /// carries no mode, so the script lands as a plain text file the shell refuses to run.
    /// </summary>
    public static void MakeExecutables(string gameRoot, LoaderDescriptor loader)
    {
        if (OperatingSystem.IsWindows() || !IsBepInEx(loader)) return;

        var script = Path.Combine(gameRoot, BepInExScript);
        if (!File.Exists(script)) return;

        File.SetUnixFileMode(script, File.GetUnixFileMode(script)
            | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    /// <summary>
    /// Where the setting goes and what it is, or null when this loader needs none here.
    /// Reads the game folder for MelonLoader, whose preload library changed name between
    /// versions; before an install nothing is there yet, and the answer says so.
    /// </summary>
    public static (string Where, string Setting)? Advice(GameInstall game, LoaderDescriptor loader)
    {
        var steam = game.SteamAppId is not null;

        if (IsBepInEx(loader))
        {
            return steam
                ? ("Set this as the game's Steam launch options:", $"./{BepInExScript} %command%")
                : ($"Start the game with {BepInExScript}, in the game's folder:", $"./{BepInExScript}");
        }

        if (IsMelonLoader(loader))
        {
            var library = MelonPreloads.FirstOrDefault(name => File.Exists(Path.Combine(game.Path, name)))
                          ?? MelonPreloads[0];
            var variables = $"LD_LIBRARY_PATH=\"{game.Path}:$LD_LIBRARY_PATH\" LD_PRELOAD=\"{library}:$LD_PRELOAD\"";

            return steam
                ? ("Set this as the game's Steam launch options:", variables + " %command%")
                : ("Start the game with these variables:",
                   variables + " ./" + Path.GetFileName(game.ExecutablePath ?? "game"));
        }

        return null;
    }
}
