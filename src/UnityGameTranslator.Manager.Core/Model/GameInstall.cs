namespace UnityGameTranslator.Manager.Core.Model;

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

    /// <summary>
    /// The loader starts, the mod cannot: the game lacks .NET libraries the mod needs, and no copy
    /// published for its Unity version can be put beside it (see <see cref="RuntimeLibraryNeed"/>).
    /// A property of how the game was built, like <see cref="StrippedRuntime"/>.
    /// </summary>
    MissingRuntimeLibraries,

    /// <summary>Not a Unity game.</summary>
    NotUnity,
}

/// <summary>
/// What a Mono game lacks for the mod — read from its own folder when it was scanned, without
/// downloading anything. Two batches, supplied from different places under different rules:
/// the .NET class libraries (<see cref="Missing"/>) and Unity's engine modules (<see cref="Modules"/>).
/// </summary>
/// <param name="Missing">
/// The .NET libraries where the mod's references stop, by name ("netstandard", "System"). What stops
/// FIRST: a game lacking netstandard names netstandard alone, and the install finds what lies
/// behind it on the real files. Empty when only engine modules are lacking.
/// </param>
/// <param name="LoaderCannotStart">
/// The game's mscorlib lacks what every loader calls (<see cref="Detection.CorlibProbe"/>), so the
/// libraries are needed before anything at all can run, not only the mod.
/// </param>
/// <param name="Archive">The Unity version the .NET copies are chosen for ("2018.4.36"), or null when unreadable.</param>
/// <param name="CannotSupply">
/// Why no copy of the .NET libraries can serve this game, or null when one can — to be confirmed on
/// the files.
/// </param>
/// <param name="Modules">The engine modules the game's build stripped of what the mod calls, or null.</param>
public sealed record RuntimeLibraryNeed(IReadOnlyList<string> Missing, bool LoaderCannotStart,
                                        string? Archive, string? CannotSupply,
                                        EngineModuleNeed? Modules = null)
{
    /// <summary>Both batches can be supplied, as far as can be said without the files.</summary>
    public bool CanSupply => WhyNot is null;

    /// <summary>The first reason nothing can serve, whichever batch it concerns — null when both can.</summary>
    public string? WhyNot => Missing.Count > 0 && CannotSupply is not null ? CannotSupply : Modules?.CannotSupply;

    /// <summary>
    /// What the game lacks, named — the same words in the refusal, the report and the card.
    /// ⚠ ASCII only: `report` is pasted into issues from consoles that mangle anything else.
    /// </summary>
    public string Lacking
    {
        get
        {
            var parts = new List<string>();
            if (Missing.Count > 0) parts.Add(".NET libraries " + string.Join(", ", Missing));
            if (Modules is { } modules) parts.Add("engine modules its build stripped (" + string.Join(", ", modules.Stripped) + ")");
            return string.Join(" and ", parts);
        }
    }
}

/// <summary>
/// Unity's engine modules a game's build stripped of what the mod calls (issue #28, measured on two
/// games 2026-09-21).
///
/// 🔴 **They are replaced as a whole set, or not at all.** A module carries the layout of the data
/// the game serialised; replacing some and not others left a game on a black screen, while the
/// complete set of the same version, or of an older one of the same branch, ran it perfectly. So
/// what is supplied is <see cref="Set"/>, every engine module the game ships.
/// </summary>
/// <param name="Stripped">The modules the mod's references stop in, and that the build stripped.</param>
/// <param name="Set">Every engine module the game ships ("UnityEngine", "UnityEngine.CoreModule"…) — what is replaced.</param>
/// <param name="Build">The game's Unity build as the engine states it ("2021.3.6f1"), or null.</param>
/// <param name="Changeset">The build's changeset ("7da38d85baf6"), which Unity's downloads are filed under, or null.</param>
/// <param name="CannotSupply">Why no copy can be verified for this game, or null when one may be.</param>
public sealed record EngineModuleNeed(IReadOnlyList<string> Stripped, IReadOnlyList<string> Set,
                                      string? Build, string? Changeset, string? CannotSupply);

/// <summary>
/// One Unity game found on disk, with everything we could establish about it.
/// Every field that could not be established stays Unknown/null — this type never guesses,
/// because a wrong guess here ends with a game that will not start.
/// </summary>
public sealed class GameInstall
{
    /// <summary>Display name. From the store manifest when we have one, folder name otherwise.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The product name Unity itself wrote, when the game states one — the second line of
    /// &lt;Game&gt;_Data/app.info.
    ///
    /// 🔴 **Kept apart from <see cref="Name"/>, which is what the reader SEES.** The display name
    /// can come from a store manifest or from a folder; this one is the string the site records as
    /// `unity_name` when somebody publishes, so it is the key two machines can agree on. Sending
    /// the display name instead is how a game ends up looked up by a repack's folder name.
    ///
    /// Null when app.info says nothing usable — the name then falls back exactly as before.
    /// </summary>
    public string? ProductName { get; set; }

    /// <summary>
    /// The company on the first line of the same file. Weak on its own, and the reason it is here:
    /// a product name like "Game" identifies nothing, and the pair identifies a great deal.
    /// </summary>
    public string? CompanyName { get; set; }

    /// <summary>Root folder holding the executable and the *_Data directory.</summary>
    public required string Path { get; init; }

    public GameStore Store { get; init; } = GameStore.Unknown;

    /// <summary>Steam app id when known. This is the key into our online translation catalog.</summary>
    public string? SteamAppId { get; init; }

    /// <summary>
    /// The id the game's own store knows it by, when that store is not Steam — today, Epic's
    /// AppName from its manifest.
    ///
    /// ⚠ Deliberately separate from SteamAppId, which is NOT a reliable statement about a store:
    /// it falls back to steam_appid.txt, and that file travels with any copy of a game. This one
    /// only ever comes from the store's own records, so it can be used to ask that store to launch.
    /// </summary>
    public string? StoreAppId { get; set; }

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

    /// <summary>
    /// The .NET libraries this game lacks for the mod, or null when it lacks none (and for every
    /// IL2CPP game, which has no class library to lack).
    /// </summary>
    public RuntimeLibraryNeed? RuntimeLibraries { get; set; }

    /// <summary>
    /// Whether the game is a Windows build, wherever it runs — on Windows, or through Proton.
    ///
    /// ⚠ One rule for two questions: whether a Linux Steam game runs through Proton, and which
    /// build of a class library may be put beside a game (a Linux build calls a native library no
    /// Windows build can load).
    /// </summary>
    public bool IsWindowsBuild =>
        ExecutablePath?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
        || File.Exists(System.IO.Path.Combine(Path, "UnityPlayer.dll"));

    public bool IsUnity => DataDirectory is not null || ExecutablePath is not null;

    public bool IsModdable => Verdict == ModdabilityVerdict.Ok;

    /// <summary>
    /// Whether the mod could ever run on this game — by any route, including one this tool
    /// declines to take for you.
    ///
    /// 🔴 **Not the same question as <see cref="IsModdable"/>, and confusing the two writes
    /// falsehoods on screen.** `IsModdable` answers *will this tool set it up*. This answers
    /// *can a translation for this game exist at all*, which is what anything about published
    /// translations has to be judged against.
    ///
    /// Three of the refusals are warnings, not walls. An anti-cheat does not stop the mod from
    /// working — it stops us from recommending it, because the account that gets banned is the
    /// player's; somebody may well install it by hand and accept that, and their translation is
    /// as real as any other. An unreadable runtime or architecture is our probe failing, not the
    /// game refusing, and `--runtime` / `--arch` exist precisely to override it.
    ///
    /// The other four are walls: a stripped runtime library no loader can start against, class
    /// libraries the mod needs and no published copy can supply, encrypted binaries under a
    /// locked-down ACL, and a game that is not Unity at all.
    ///
    /// ⚠ **NOT a duplicate of <see cref="Detection.ModdabilityProbe.CanBeOverridden"/>, and merging
    /// the two would break both.** That one asks *will this tool try anyway if you insist*, and the
    /// answers cross over precisely on the cases that matter:
    ///
    /// <list type="bullet">
    /// <item>`AntiCheat` — overridable: NO (we never install into one, whoever asks) ·
    ///       could run: YES (the mod works; it is the ban we will not risk on someone's behalf)</item>
    /// <item>`StrippedRuntime` — overridable: YES (trying is reversible and costs minutes) ·
    ///       could run: NO (three loaders and a runtime swap were tried; it is the game)</item>
    /// </list>
    ///
    /// Three questions, not two: *will we set it up* (IsModdable), *will we try if pushed*
    /// (CanBeOverridden), *can a translation for this game exist* (here).
    /// </summary>
    public bool ModCouldRun => Verdict is ModdabilityVerdict.Ok
                                       or ModdabilityVerdict.AntiCheat
                                       or ModdabilityVerdict.RuntimeUnknown
                                       or ModdabilityVerdict.ArchitectureUnknown;

    public override string ToString() => $"{Name} [{Runtime}, {UnityVersion ?? "unknown Unity"}]";
}
