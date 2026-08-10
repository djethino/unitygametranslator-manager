using UnityGameTranslator.Installer.Core.Model;

namespace UnityGameTranslator.Installer.Core.Platform;

/// <summary>
/// Everything that genuinely differs between operating systems, and nothing else.
///
/// Same shape as the mod's IModLoaderAdapter: one shared trunk (Core) that holds all the logic,
/// and thin adapters for the parts that cannot be shared. If a method here could be written once
/// for every OS, it does not belong in this interface — it belongs in Core.
/// </summary>
public interface IPlatform
{
    /// <summary>"windows", "linux" or "macos" — matches the "os" field of catalog assets.</summary>
    string OsId { get; }

    /// <summary>Architecture of the machine, not of a given game.</summary>
    GameArchitecture HostArchitecture { get; }

    /// <summary>Steam installation roots (the folder holding steamapps/), most likely first.</summary>
    IEnumerable<string> SteamRoots();

    /// <summary>
    /// Folders worth scanning for games outside Steam: Epic, GOG, and the usual manual
    /// locations. Returned even when empty so callers never special-case an OS.
    /// </summary>
    IEnumerable<GameRootHint> ExtraGameRoots();

    /// <summary>Where the tool keeps its own state (catalog cache, known installs).</summary>
    string UserDataDirectory { get; }

    /// <summary>Where the portable executable would copy itself if the user accepts.</summary>
    string SelfInstallDirectory { get; }

    /// <summary>File name of the tool's executable on this OS.</summary>
    string ExecutableFileName { get; }

    /// <summary>
    /// The places a launcher can be put here. Offered as choices at self-install, so a system with
    /// no desktop to speak of simply offers one fewer box rather than a box that does nothing.
    /// </summary>
    IReadOnlyList<LauncherKind> LauncherKinds { get; }

    /// <summary>
    /// Writes a launcher and returns the files it created, or an empty list when it could not.
    ///
    /// Failing to make a shortcut must never fail an installation: the tool is installed and
    /// runnable either way, and the person can be told which part did not happen.
    /// </summary>
    IReadOnlyList<string> CreateLauncher(LauncherKind kind, string executable);

    /// <summary>
    /// Puts the tool in the system's own list of installed applications, and returns how to find
    /// that entry again — or null when this system has no such list.
    ///
    /// ⚠ Must be idempotent: called again for the same installation it updates the entry rather
    /// than adding a second one. A tool that appears three times in the system's list after two
    /// updates is a tool people stop trusting.
    /// </summary>
    string? RegisterInstalled(ToolInstallation installation);

    /// <summary>Removes that entry. Silent when it is already gone.</summary>
    void UnregisterInstalled(string registration);

    /// <summary>
    /// Is the given .NET Desktop Runtime major version present? MelonLoader IL2CPP needs 6.0
    /// and fails at game launch without it, so we check instead of promising.
    /// Returns null when we cannot tell — which is not the same as "no".
    /// </summary>
    bool? HasDotnetDesktopRuntime(string majorVersion);

    /// <summary>
    /// True when this game needs a Wine/Proton DLL override to load a loader. Windows never
    /// does; Linux does for any game running through Proton.
    /// </summary>
    bool NeedsDllOverride(GameInstall game);

    /// <summary>Is a process currently running out of this folder? Blocks writing to it.</summary>
    bool IsGameRunning(GameInstall game);

    /// <summary>
    /// The language the operating system is set to, as an ISO 639-1 code, or null when it cannot
    /// be established.
    ///
    /// Asked of the OS rather than of CultureInfo on purpose: this tool builds with invariant
    /// globalization, so CurrentUICulture is the invariant culture and reports "iv" — which
    /// surfaced as "No iv translation yet" on every row. Dropping invariant mode would drag ICU
    /// into a single-file build for one string, so the string is fetched where it actually lives.
    /// </summary>
    string? SystemLanguage();

    /// <summary>
    /// Video memory on the largest graphics adapter, in bytes, or null when it cannot be read.
    ///
    /// Used to say which models are worth offering — a 12 GB model on a 6 GB card runs on the
    /// processor and takes minutes per line, which reads as "the mod is broken".
    ///
    /// ⚠ Null must never block anything. Cards, drivers and virtual machines report this in ways
    /// we will not all have seen; when we cannot tell, the whole list is shown with its
    /// requirements stated and the person decides. A tool that refuses to offer anything because
    /// it failed to read a number is worse than one that asks.
    /// </summary>
    long? VideoMemoryBytes();
}

/// <summary>A folder to scan, with what we already know about where it came from.</summary>
public readonly record struct GameRootHint(string Path, GameStore Store)
{
    public override string ToString() => $"{Path} ({Store})";
}
