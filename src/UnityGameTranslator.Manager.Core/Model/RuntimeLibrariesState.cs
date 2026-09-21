namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>Where a game stands on the .NET libraries it lacks.</summary>
public enum RuntimeLibrariesStatus
{
    /// <summary>It lacks nothing, and nothing of ours is in place.</summary>
    NotNeeded,

    /// <summary>
    /// It lacks some, and they are not in place — never added, removed, or the loader's
    /// configuration lost our entry (a loader update rewrites it).
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
/// The .NET libraries a game lacks, and whether what this tool put beside it still stands.
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

    /// <summary>The mod cannot start until this is settled.</summary>
    public bool BlocksTheMod => Need is not null && Status is RuntimeLibrariesStatus.Missing or RuntimeLibrariesStatus.WrongVersion;

    /// <summary>
    /// Adding them would change something and can be done — read by the promise and by the act, like
    /// <see cref="GameReport.PluginWriteOffered"/>.
    /// </summary>
    public bool WriteOffered => BlocksTheMod && Need is { CanSupply: true };

    /// <summary>Something of ours is in place and may be taken out.</summary>
    public bool RemoveOffered => Installed is not null;

    /// <summary>Whether a card should be shown for it at all: something is lacking, or ours is here.</summary>
    public bool Relevant => Need is not null || Installed is not null;

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
            $"lacks {Named(Need.Missing)} - they cannot be added: {Need.CannotSupply}",

        RuntimeLibrariesStatus.Missing =>
            $"lacks {Named(Need!.Missing)} - not added, so the mod will not start"
            + (Detail is null ? "" : $" ({Detail})"),

        RuntimeLibrariesStatus.WrongVersion =>
            $"lacks {Named(Need!.Missing)} - the ones added were {Detail}, so the mod will not start",

        RuntimeLibrariesStatus.InPlace =>
            $"lacks {Named(Need!.Missing)} - added for Unity {Installed!.Unity} ({Installed.Files.Count} files)",

        RuntimeLibrariesStatus.NoLongerNeeded =>
            $"lacks nothing any more - the {Installed!.Files.Count} files added for Unity {Installed.Unity} can be removed",

        _ => null,
    };

    private static string Named(IReadOnlyList<string> libraries) => string.Join(", ", libraries);
}
