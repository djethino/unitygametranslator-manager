namespace UnityGameTranslator.Manager.Core.Model;

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
    /// Set up, and BOTH sides moved: the published translation changed and so did the one here.
    ///
    /// ⚠ Its own state rather than a shade of <see cref="UnpublishedWork"/>, because nothing about
    /// it is the same. Unpublished work waits for a decision that costs nothing to postpone; a
    /// conflict has to be settled line by line, in the mod, and pressing the ordinary verb would
    /// pick a side. It also has to LOOK different — folded into the other it inherited the calm
    /// blue of "there is something to send".
    /// </summary>
    Conflict,

    /// <summary>
    /// Online mode is off, or the catalog could not be reached: we know what is installed, and
    /// nothing about what exists elsewhere. Said plainly rather than shown as "no translation",
    /// which would be a claim we cannot make.
    /// </summary>
    Unknown,
}

/// <summary>The situation of one game, with the words to say it.</summary>
/// <param name="Pending">
/// What is out of date in this game, whatever else its headline says — "mod", "loader", or both.
///
/// 🔴 **Separate from the headline because they are not in competition.** A row can only carry one
/// situation, and the translation states rightly win it: unpublished work can be lost, a stale
/// plugin cannot. But that ranking meant a game reading "Unpublished changes" hid the fact that
/// its mod was four versions behind, and the only way to find out was to open it.
///
/// Null when everything installed is current, so a row that says nothing is saying something.
/// </param>
public sealed record GameSituationInfo(
    Situation Situation,
    string Headline,
    string? Detail,
    string PrimaryAction,
    string? Pending = null)
{
    /// <summary>Which status colour the row should carry, if any.</summary>
    public string? StatusKey => Situation switch
    {
        Situation.Blocked => "StatusWarning",
        Situation.TranslationAvailable => "StatusSuccess",
        Situation.Ready => "StatusSuccess",
        Situation.UpdateAvailable => "StatusInfo",
        Situation.UnpublishedWork => "StatusInfo",

        // ⚠ Warning, not info. Both sides moved, and the row is asking for an arbitration rather
        // than announcing something pending — the one state here where doing nothing has a cost.
        Situation.Conflict => "StatusWarning",

        _ => null,
    };

    public bool CanAct => Situation != Situation.Blocked;
}
