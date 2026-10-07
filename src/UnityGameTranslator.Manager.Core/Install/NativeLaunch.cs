using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// How a loader is started in a NATIVE Linux game — the counterpart of Proton's DLL override.
///
/// 🔴 **Nothing loads a loader into a native Linux game by itself** (Bazzite VM, 2026-09-28). On
/// Windows and under Wine the game picks up a DLL placed beside it; on Linux it must be started
/// through a preload — BepInEx ships <c>run_bepinex.sh</c>, MelonLoader a library for
/// <c>LD_PRELOAD</c>. The install used to end on "is ready" with neither said.
///
/// 🔴 **A start file, not a launch option** (user, same day: "a Bazzite or Steam Deck user is not
/// an advanced user"). The game's executable is renamed <c>&lt;name&gt;.ugt</c> and a small script
/// takes its name: every launcher — Steam, Heroic, a desktop shortcut — starts the script, which
/// starts the game through the loader. Measured: Unity finds <c>&lt;name&gt;_Data</c> from the
/// renamed file (it drops the last extension), BepInEx and UGT Mod load, nothing typed anywhere.
///
/// ⚠ **A game update or Steam's "Verify files" puts the real executable back** over the script.
/// The game then runs without the mod, beside a stale <c>.ugt</c>; <see cref="IsBroken"/> says so,
/// and the next install or update redoes it (<see cref="Install"/> takes the new executable).
///
/// ⚠ Behaviour, not data: decided here, where a release decides it, rather than in the catalogue
/// every installation fetches.
/// </summary>
public static class NativeLaunch
{
    public const string BepInExScript = "run_bepinex.sh";

    /// <summary>The line that makes a start file ours — the only file uninstall will delete by that name.</summary>
    public const string Marker = "# UnityGameTranslator Manager: starts the game through its mod loader.";

    private const string MovedExtension = ".ugt";

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
    /// The name the game's executable takes: its stem and <c>.ugt</c>. Unity looks for its data
    /// folder under the executable's name without the last extension, so the stem is kept.
    /// </summary>
    public static string MovedName(string executableName) =>
        Path.GetFileNameWithoutExtension(executableName) + MovedExtension;

    /// <summary>
    /// Readies the extracted loader BEFORE it is copied: BepInEx's script is given the game's
    /// renamed executable, so starting the script by hand works too.
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

        var named = $"executable_name=\"{MovedName(Path.GetFileName(executable))}\"";
        File.WriteAllText(script, text.Substring(0, index) + named + text.Substring(index + empty.Length));
    }

    /// <summary>
    /// After the copy: BepInEx's script made executable. A zip written on Windows carries no mode,
    /// so it lands as a text file the shell refuses to run.
    /// </summary>
    public static void MakeExecutables(string gameRoot, LoaderDescriptor loader)
    {
        if (OperatingSystem.IsWindows() || !IsBepInEx(loader)) return;

        var script = Path.Combine(gameRoot, BepInExScript);
        if (File.Exists(script)) MakeExecutable(script);
    }

    /// <summary>
    /// Puts the start file in place, or back in place after a game update, and returns what the
    /// receipt records. Null when this loader or game gives nothing to start through — the
    /// summary then gives the launch option (<see cref="Advice"/>).
    /// </summary>
    public static ReceiptStartFile? Install(GameInstall game, LoaderDescriptor loader, ReceiptStartFile? existing)
    {
        if (!IsBepInEx(loader) && !IsMelonLoader(loader)) return null;

        var name = existing?.Executable ?? (game.ExecutablePath is { } path ? Path.GetFileName(path) : null);
        if (string.IsNullOrEmpty(name)) return null;

        var moved = existing?.MovedTo ?? MovedName(name);
        var executable = Path.Combine(game.Path, name);
        var movedPath = Path.Combine(game.Path, moved);

        // The game's own file where the start file should be: the first install, or an update
        // that put it back. Either way it is the current game, and it replaces a stale copy.
        // ⚠ Only a Linux program (ELF): anything else there is not the game Unity built, and
        // renaming it could leave a game that no longer starts. The launch option is given instead.
        if (File.Exists(executable) && !IsOurStartFile(executable))
        {
            if (!IsElf(executable)) return null;
            File.Move(executable, movedPath, overwrite: true);
        }

        if (!File.Exists(movedPath)) return null;

        var temporary = executable + ".ugt-tmp";
        File.WriteAllText(temporary, Script(game, loader, moved));
        MakeExecutable(temporary);
        File.Move(temporary, executable, overwrite: true);

        return new ReceiptStartFile { Executable = name, MovedTo = moved };
    }

    /// <summary>
    /// Puts the game's executable back under its own name. After a game update the executable is
    /// already the real one and only the stale copy goes.
    /// </summary>
    public static void Remove(string gameRoot, ReceiptStartFile recorded, ICollection<string> removed)
    {
        var executable = Path.Combine(gameRoot, recorded.Executable);
        var movedPath = Path.Combine(gameRoot, recorded.MovedTo);

        if (IsOurStartFile(executable))
        {
            if (!File.Exists(movedPath)) return; // nothing to put back: keep what still starts something

            File.Delete(executable);
            File.Move(movedPath, executable);
            removed.Add($"the start file in place of {recorded.Executable} (the game's own is back)");
        }
        else if (File.Exists(executable) && File.Exists(movedPath))
        {
            File.Delete(movedPath);
            removed.Add($"{recorded.MovedTo} (an old copy of the game's executable)");
        }
    }

    /// <summary>True when the game's executable came back over the start file — a game update, or Steam's "Verify files".</summary>
    public static bool IsBroken(string gameRoot, ReceiptStartFile recorded)
    {
        var executable = Path.Combine(gameRoot, recorded.Executable);
        return File.Exists(executable) && !IsOurStartFile(executable);
    }

    private static bool IsElf(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            return file.Read(magic) == 4 && magic[0] == 0x7F && magic[1] == (byte)'E'
                   && magic[2] == (byte)'L' && magic[3] == (byte)'F';
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not known to be a Linux executable: no start file is put in front of it — said.
            Faults.Say("NativeLaunch.IsElf", e, Sanitize.Path(path));
            return false;
        }
    }

    public static bool IsOurStartFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length < 4096 && File.ReadAllText(path).Contains(Marker, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not known to be ours: read as a game update having replaced it — said.
            Faults.Say("NativeLaunch.IsOurStartFile", e, Sanitize.Path(path));
            return false;
        }
    }

    /// <summary>
    /// The launch option, for when there is no start file (the executable could not be read).
    /// Reads the game folder for MelonLoader, whose preload library changed name between versions.
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
            var variables = $"LD_LIBRARY_PATH=\"{game.Path}:$LD_LIBRARY_PATH\" LD_PRELOAD=\"{MelonPreload(game.Path)}:$LD_PRELOAD\"";

            return steam
                ? ("Set this as the game's Steam launch options:", variables + " %command%")
                : ("Start the game with these variables:",
                   variables + " ./" + Path.GetFileName(game.ExecutablePath ?? "game"));
        }

        return null;
    }

    private static string MelonPreload(string gameRoot) =>
        MelonPreloads.FirstOrDefault(name => File.Exists(Path.Combine(gameRoot, name))) ?? MelonPreloads[0];

    /// <summary>
    /// The start file. POSIX sh, LF, the game named in single quotes (a name may hold spaces).
    /// MelonLoader's library is preloaded by NAME with the folder on the library path — a full
    /// path in LD_PRELOAD is ignored under secure execution (MelonLoader's own guidance).
    /// </summary>
    internal static string Script(GameInstall game, LoaderDescriptor loader, string moved)
    {
        var quoted = "'./" + moved.Replace("'", "'\\''") + "'";
        var lines = new List<string>
        {
            "#!/bin/sh",
            Marker,
            $"# The game itself is {moved}. Uninstalling the mod loader with UGT Manager puts it back.",
            "cd \"$(dirname \"$(readlink -f \"$0\")\")\" || exit 1",
        };

        // 🔴 The loader gone (deleted by hand, say), the game still starts — without the mod. A
        // start file that could leave a game unable to launch would be the one thing worse than
        // no mod at all.
        if (IsBepInEx(loader))
        {
            lines.Add($"[ -x ./{BepInExScript} ] || exec {quoted} \"$@\"");
            lines.Add($"exec ./{BepInExScript} {quoted} \"$@\"");
        }
        else
        {
            var library = MelonPreload(game.Path);
            lines.Add($"[ -f ./{library} ] || exec {quoted} \"$@\"");
            lines.Add("export LD_LIBRARY_PATH=\"$PWD${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}\"");
            lines.Add($"export LD_PRELOAD=\"{library}${{LD_PRELOAD:+:$LD_PRELOAD}}\"");
            lines.Add($"exec {quoted} \"$@\"");
        }

        return string.Join("\n", lines) + "\n";
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path)
            | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
