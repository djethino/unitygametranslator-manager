namespace UnityGameTranslator.Installer.Core.Model;

/// <summary>
/// What a game's row says, in the player's terms.
///
/// The list used to show "Mono · Unity 2021.3.16f1". True, and useless to someone who wants to
/// play in their language: it answers "what is this technically" instead of "what can I do
/// here". The technical facts moved to the details of the card; a row now states a situation
/// and offers the verb that goes with it.
/// </summary>
public enum Situation
{
    /// <summary>Anti-cheat, store-locked, or a runtime we could not identify.</summary>
    Blocked,

    /// <summary>Nothing installed, and someone has translated it into the target language.</summary>
    TranslationAvailable,

    /// <summary>Nothing installed, and nobody has translated it into the target language yet.</summary>
    NotTranslatedYet,

    /// <summary>Set up and current.</summary>
    Ready,

    /// <summary>Set up, but a newer plugin or a newer translation exists.</summary>
    UpdateAvailable,

    /// <summary>Set up, with local work that has not been published.</summary>
    UnpublishedWork,

    /// <summary>
    /// Online mode is off, or the catalog could not be reached: we know what is installed, and
    /// nothing about what exists elsewhere. Said plainly rather than shown as "no translation",
    /// which would be a claim we cannot make.
    /// </summary>
    Unknown,
}

/// <summary>The situation of one game, with the words to say it.</summary>
public sealed record GameSituationInfo(
    Situation Situation,
    string Headline,
    string? Detail,
    string PrimaryAction)
{
    /// <summary>Which status colour the row should carry, if any.</summary>
    public string? StatusKey => Situation switch
    {
        Situation.Blocked => "StatusWarning",
        Situation.TranslationAvailable => "StatusSuccess",
        Situation.Ready => "StatusSuccess",
        Situation.UpdateAvailable => "StatusInfo",
        Situation.UnpublishedWork => "StatusInfo",
        _ => null,
    };

    public bool CanAct => Situation != Situation.Blocked;
}
