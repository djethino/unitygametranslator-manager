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
}

/// <summary>A folder to scan, with what we already know about where it came from.</summary>
public readonly record struct GameRootHint(string Path, GameStore Store)
{
    public override string ToString() => $"{Path} ({Store})";
}
