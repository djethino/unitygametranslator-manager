namespace UnityGameTranslator.Installer.Core.Model;

/// <summary>Where the game was found. Not cosmetic: it decides how we get the app id.</summary>
public enum GameStore
{
    Unknown,
    Steam,
    Epic,
    Gog,
    Manual,
}

/// <summary>Unity scripting backend. Decides which mod loader builds can work at all.</summary>
public enum UnityRuntime
{
    /// <summary>Probed and inconclusive — never guess, say so.</summary>
    Unknown,
    Mono,
    Il2Cpp,
}

public enum GameArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64,
}

/// <summary>
/// Why a game cannot be modded. A refusal must always carry its reason: "it does not work"
/// sends the user to our issue tracker, "this game ships an anti-cheat" does not.
/// </summary>
public enum ModdabilityVerdict
{
    /// <summary>Nothing blocking found.</summary>
    Ok,

    /// <summary>Anti-cheat present. Modding it can get the player's account banned.</summary>
    AntiCheat,

    /// <summary>Microsoft Store / Game Pass: encrypted binaries under a locked-down ACL.</summary>
    StoreProtected,

    /// <summary>Unity game, but we could not tell Mono from IL2CPP — refuse rather than coin-flip.</summary>
    RuntimeUnknown,

    /// <summary>
    /// We could not read whether the game is 32 or 64 bit. Refused rather than guessed: falling
    /// back to the machine's architecture installs a 64-bit loader into a 32-bit game, which does
    /// not crash — it simply never loads, and reads as "the mod does not work".
    /// </summary>
    ArchitectureUnknown,

    /// <summary>
    /// The game ships a stripped runtime library: members every mod loader calls were removed at
    /// build time. No loader can start, and this is a property of the game, not a bug in ours.
    /// </summary>
    StrippedRuntime,

    /// <summary>Not a Unity game.</summary>
    NotUnity,
}

/// <summary>
/// One Unity game found on disk, with everything we could establish about it.
/// Every field that could not be established stays Unknown/null — this type never guesses,
/// because a wrong guess here ends with a game that will not start.
/// </summary>
public sealed class GameInstall
{
    /// <summary>Display name. From the store manifest when we have one, folder name otherwise.</summary>
    public required string Name { get; init; }

    /// <summary>Root folder holding the executable and the *_Data directory.</summary>
    public required string Path { get; init; }

    public GameStore Store { get; init; } = GameStore.Unknown;

    /// <summary>Steam app id when known. This is the key into our online translation catalog.</summary>
    public string? SteamAppId { get; init; }

    public UnityRuntime Runtime { get; set; } = UnityRuntime.Unknown;

    /// <summary>Full Unity version including the release suffix (e.g. "2021.3.16f1"), or null.</summary>
    public string? UnityVersion { get; set; }

    public GameArchitecture Architecture { get; set; } = GameArchitecture.Unknown;

    /// <summary>The &lt;Game&gt;_Data folder, when found.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Main executable. On macOS this is the .app bundle.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// True when a Windows build runs through Proton on Linux. Changes everything: we install
    /// the *Windows* loader build and the user needs a Steam launch option.
    /// </summary>
    public bool RunsUnderProton { get; set; }

    /// <summary>Proton prefix path (steamapps/compatdata/&lt;appid&gt;), when applicable.</summary>
    public string? ProtonPrefix { get; set; }

    public ModdabilityVerdict Verdict { get; set; } = ModdabilityVerdict.Ok;

    /// <summary>Human-readable detail behind <see cref="Verdict"/>, e.g. "EasyAntiCheat".</summary>
    public string? VerdictDetail { get; set; }

    /// <summary>True when the runtime came from the user rather than from the files.</summary>
    public bool RuntimeIsAssumed { get; set; }

    /// <summary>True when the architecture came from the user rather than from the files.</summary>
    public bool ArchitectureIsAssumed { get; set; }

    /// <summary>
    /// True when the user chose to proceed despite a refusal. Kept visible so nothing downstream
    /// mistakes an override for a clean verdict.
    /// </summary>
    public bool VerdictOverridden { get; set; }

    /// <summary>
    /// The refusal the user overruled. Kept because the caveat that matters describes what was
    /// refused, and reading it off the current verdict — now Ok — returned an empty warning at
    /// the one moment it needed to be read.
    /// </summary>
    public ModdabilityVerdict? OverriddenVerdict { get; set; }

    /// <summary>
    /// Loader families this game's stripped runtime cannot host, with the reason. Empty for
    /// almost every game. When it holds every known family, the game is refused outright.
    /// </summary>
    public List<Detection.CorlibProbe.BrokenFamily> BrokenLoaderFamilies { get; } = new();

    public bool IsUnity => DataDirectory is not null || ExecutablePath is not null;

    public bool IsModdable => Verdict == ModdabilityVerdict.Ok;

    public override string ToString() => $"{Name} [{Runtime}, {UnityVersion ?? "unknown Unity"}]";
}
