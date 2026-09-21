using UnityGameTranslator.Manager.Core.Install;

namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>Where a game stands on what it lacks for the mod.</summary>
public enum RuntimeLibrariesStatus
{
    /// <summary>It lacks nothing, and nothing of ours is in place.</summary>
    NotNeeded,

    /// <summary>
    /// It lacks something, and ours is not in place — never added, removed, only one of the two
    /// batches added, or the loader's configuration lost our entry (a loader update rewrites it).
    /// </summary>
    Missing,

    /// <summary>Ours are in place, chosen for another Unity version than the game's now — a game update.</summary>
    WrongVersion,

    /// <summary>Ours are in place and the loader is told where they are.</summary>
    InPlace,

    /// <summary>Ours are still here, but the game no longer lacks anything.</summary>
    NoLongerNeeded,
}

/// <summary>
/// What a game lacks — .NET libraries, engine modules its build stripped, or both — whether what
/// this tool put beside it still stands, and where the modules would come from.
///
/// 🔴 **Reconciled from the files at every report, never remembered as "done".** Three things undo
/// an install without a word: a loader update rewrites its configuration and drops our entry; a
/// game update changes the Unity version the copies were chosen for; Steam's "verify files" can
/// delete our folder. Each one leaves a mod that stops at load, and the only way to notice is to
/// look again.
/// </summary>
/// <param name="Detail">What is wrong, in a few words, when something is — for the card and the report.</param>
public sealed record RuntimeLibrariesState(RuntimeLibrariesStatus Status, RuntimeLibraryNeed? Need,
                                           ReceiptRuntimeLibraries? Installed, string? Detail = null)
{
    public static RuntimeLibrariesState None { get; } = new(RuntimeLibrariesStatus.NotNeeded, null, null);

    /// <summary>Where each batch could come from, and which source an install would use.</summary>
    public sealed record SourceChoice(IReadOnlyList<ClassLibraryCandidate> Libraries, ClassLibraryCandidate? Library,
                                      IReadOnlyList<EngineModuleCandidate> Modules, EngineModuleCandidate? Module,
                                      bool ChosenGone);

    private static readonly SourceChoice NoSources =
        new(Array.Empty<ClassLibraryCandidate>(), null, Array.Empty<EngineModuleCandidate>(), null, false);

    /// <summary>
    /// The sources, worked out the first time something asks — and only then.
    ///
    /// 🔴 **Lazy because the game LIST builds a report per game, on the interface thread**, and a row
    /// never asks where the files would come from: only whether the mod can run. Finding the sources
    /// reads editors, other games' libraries and players, and checks signatures — seconds per game
    /// that lacks something, and the window froze at start for eight of them (2026-09-21). The card
    /// asks for them off that thread (<see cref="WarmSources"/>).
    /// </summary>
    public Lazy<SourceChoice>? Sources { get; init; }

    private SourceChoice Choice => Sources?.Value ?? NoSources;

    /// <summary>Works the sources out now, on the calling thread — what the card does before drawing.</summary>
    public void WarmSources() => _ = Choice;

    /// <summary>
    /// Every place the missing engine modules could come from, in the order they are preferred, each
    /// with what stands against it. Empty when no module is lacking.
    /// </summary>
    public IReadOnlyList<EngineModuleCandidate> ModuleSources => Choice.Modules;

    /// <summary>The source an install would use: the one chosen for this game while usable, else the first usable one.</summary>
    public EngineModuleCandidate? ModuleSource => Choice.Module;

    /// <summary>Every place the missing .NET libraries could come from, in the order they are preferred, each with its verdict.</summary>
    public IReadOnlyList<ClassLibraryCandidate> ClassLibrarySources => Choice.Libraries;

    /// <summary>The .NET libraries' source an install would use: the one chosen for this game while usable, else the first usable.</summary>
    public ClassLibraryCandidate? ClassLibrarySource => Choice.Library;

    /// <summary>Whether this computer could reach Unity's server when this was read — Unity's download is offered only then.</summary>
    public bool Online { get; init; } = true;

    /// <summary>A source was chosen for this game and is no longer usable — the card says the default took its place.</summary>
    public bool ChosenSourceGone => Choice.ChosenGone;

    /// <summary>
    /// The copies in place came from another source than the one now chosen for this game.
    ///
    /// ⚠ Without it a new choice made on the card, with the libraries already in place, had no verb
    /// to act on it (2026-09-21): the installer replaces a batch whose source changed, but nothing
    /// offered to run it. Only while in place — lacking ones are offered anyway.
    /// </summary>
    public bool SourceChanged =>
        Status == RuntimeLibrariesStatus.InPlace && Installed is { } installed && Need is not null
        && ((Need.Missing.Count > 0 && installed.SourceId is { Length: > 0 } libraries
             && ClassLibrarySource is { } chosenLibraries && chosenLibraries.Source.Id != libraries)
            || (Need.Modules is not null && installed.Modules is { SourceId: { Length: > 0 } modules }
                && ModuleSource is { } chosenModules && chosenModules.Source.Id != modules));

    /// <summary>The mod cannot start until this is settled.</summary>
    public bool BlocksTheMod => Need is not null && Status is RuntimeLibrariesStatus.Missing or RuntimeLibrariesStatus.WrongVersion;

    /// <summary>
    /// Adding them would change something and can be done — read by the promise and by the act, like
    /// <see cref="GameReport.PluginWriteOffered"/>. Each lacking batch needs a source.
    /// </summary>
    public bool WriteOffered => (BlocksTheMod || SourceChanged) && Need is { CanSupply: true }
                                && (Need.Missing.Count == 0 || ClassLibrarySource is not null)
                                && (Need.Modules is null || ModuleSource is not null);

    /// <summary>Adding them means downloading from Unity — what the person has to be told, and agree to, first.</summary>
    public bool NeedsUnityDownload => WriteOffered
                                      && ((Need!.Missing.Count > 0 && ClassLibrarySource?.Source.Kind == ClassLibrarySourceKind.UnityDownload)
                                          || (Need.Modules is not null && ModuleSource?.Source.Kind == EngineModuleSourceKind.UnityDownload));

    /// <summary>Copies would be taken from another game — what the person must be warned about first.</summary>
    public bool CopiesFromAnotherGame => WriteOffered
                                         && ((Need!.Missing.Count > 0 && ClassLibrarySource?.Source.Kind == ClassLibrarySourceKind.Game)
                                             || (Need.Modules is not null && ModuleSource?.Source.Kind == EngineModuleSourceKind.Game));

    /// <summary>Something of ours is in place and may be taken out.</summary>
    public bool RemoveOffered => Installed is not null;

    /// <summary>Whether a card should be shown for it at all: something is lacking, or ours is here.</summary>
    public bool Relevant => Need is not null || Installed is not null;

    /// <summary>
    /// Why what the game lacks cannot be added although the game can be modded: sources were looked
    /// for and none holds. Null otherwise. Offline, the answer is to go online (user's decision).
    /// </summary>
    public string? NoSource
    {
        get
        {
            var lacksLibraries = Need is { Missing.Count: > 0, CannotSupply: null } && ClassLibrarySource is null;
            var lacksModules = Need?.Modules is { CannotSupply: null } && ModuleSource is null;
            if (!lacksLibraries && !lacksModules) return null;

            if (!Online) return LocalCopies.GoOnline;

            return lacksModules
                ? ModuleSources.FirstOrDefault()?.Problems.FirstOrDefault()
                  ?? "no copy of the same Unity release was found on this computer, and Unity's download is not available for it"
                : ClassLibrarySources.FirstOrDefault()?.Problems.FirstOrDefault()
                  ?? "no copy was found on this computer, and Unity's download is not available for this build";
        }
    }

    /// <summary>
    /// One line: what the game lacks, and where that stands — the same words for the report and the
    /// card. Null when there is nothing to say.
    ///
    /// ⚠ ASCII only: `report` is pasted into issues from consoles that mangle anything else.
    /// </summary>
    public string? Headline => Status switch
    {
        RuntimeLibrariesStatus.NotNeeded => null,

        RuntimeLibrariesStatus.Missing when Need is { CanSupply: false } =>
            $"lacks {Need.Lacking} - it cannot be added: {Need.WhyNot}",

        RuntimeLibrariesStatus.Missing when NoSource is { } none =>
            Online ? $"lacks {Need!.Lacking} - it cannot be added: {none}" : $"lacks {Need!.Lacking} - {none}",

        RuntimeLibrariesStatus.Missing =>
            $"lacks {Need!.Lacking} - not added, so the mod will not start"
            + (Detail is null ? "" : $" ({Detail})"),

        RuntimeLibrariesStatus.WrongVersion =>
            $"lacks {Need!.Lacking} - the copies added no longer fit ({Detail}), so the mod will not start",

        RuntimeLibrariesStatus.InPlace =>
            $"lacks {Need!.Lacking} - added ({InstalledSummary})",

        RuntimeLibrariesStatus.NoLongerNeeded =>
            $"lacks nothing any more - {InstalledSummary} can be removed",

        _ => null,
    };

    /// <summary>
    /// What of ours is in place, and from where: "4 .NET libraries from the Unity 2021.3.6f1 editor;
    /// 36 engine modules of Unity 2021.3.6 from ...". ⚠ No Unity version on the .NET half: the
    /// source names its own, and what they must match is the game's Mono runtime, not a release.
    /// </summary>
    public string InstalledSummary
    {
        get
        {
            if (Installed is null) return "";

            var parts = new List<string>();
            if (Installed.Files.Count > 0)
            {
                // An address is what the copies of an earlier release recorded (BepInEx's archive).
                var from = Installed.Source.Contains("://", StringComparison.Ordinal) ? "" : $" from {Installed.Source}";
                parts.Add($"{Installed.Files.Count} .NET libraries{from}");
            }
            if (Installed.Modules is { } modules)
                parts.Add($"{modules.Files.Count} engine modules of Unity {modules.Unity} from {modules.Source}");

            return string.Join("; ", parts);
        }
    }
}
