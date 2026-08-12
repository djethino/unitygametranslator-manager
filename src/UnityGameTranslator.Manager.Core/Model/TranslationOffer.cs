namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>
/// What taking a community translation would actually do to THIS game, which is not the same
/// question as whether one exists.
///
/// ⚠ The whole point is that a blank game and a game carrying work are different propositions, and
/// one checkbox answered both. "Also bring a translation down" is a gift on a game with nothing in
/// it and a demolition on a game somebody has been building for a month — offering them the same
/// ticked box, and remembering the "yes" from the first to apply it to the second, is how a tool
/// destroys work while doing exactly what it was told.
/// </summary>
public enum TranslationOffer
{
    /// <summary>Nothing published to take. No question to ask.</summary>
    None,

    /// <summary>
    /// The file here IS the one that would be taken, unchanged, and the server has not moved.
    /// Nothing to do — and a ticked box that re-downloads the same bytes reads as an action.
    /// </summary>
    AlreadyInPlace,

    /// <summary>
    /// Nothing stands to be lost: either no file at all, or the published one sitting here
    /// untouched while a newer version exists. Taking it is a gain with no cost.
    /// </summary>
    FreeToTake,

    /// <summary>
    /// A file here carries entries that exist nowhere else — never uploaded. Replacing it puts
    /// them out of the game's reach, and no download brings them back.
    /// </summary>
    ReplacesWork,

    /// <summary>
    /// A file here is a DIFFERENT published translation, whole and re-downloadable. Nothing is
    /// permanently lost, but somebody chose that one and swapping it is not housekeeping.
    /// </summary>
    ReplacesChoice,
}

public static class TranslationOffers
{
    /// <summary>
    /// Reads the situation for one game and one candidate translation.
    /// </summary>
    /// <param name="picked">
    /// What would be taken, or null when nothing would be. Decided by the caller, since the rule
    /// for picking — what the person chose, else the best ranked in their language — is about
    /// preference and ranking rather than about risk.
    /// </param>
    public static TranslationOffer For(GameReport report, OnlineTranslation? picked)
    {
        if (picked is null) return TranslationOffer.None;

        var local = report.LocalTranslation;
        if (local is null) return TranslationOffer.FreeToTake;

        // ⚠ Asked first, and it outranks everything below. A file that has never been uploaded
        // reports every one of its entries as a local change, so this covers both "I have edited
        // the download" and "this is entirely my own work" — the two cases where a replacement is
        // not recoverable from anywhere.
        if (local.LocalChanges > 0) return TranslationOffer.ReplacesWork;

        // A file we cannot even read is not one to overwrite on a default: whatever is in it, we
        // are not the ones who can say it is worthless.
        if (local.EntryCount < 0) return TranslationOffer.ReplacesWork;

        var here = report.MatchingOnline;

        // A different lineage: they are using somebody's translation and we would hand them
        // another. Recoverable, so not "work", but still a choice being taken back.
        if (here is null || here.Id != picked.Id)
        {
            return local.EntryCount > 0
                ? TranslationOffer.ReplacesChoice
                : TranslationOffer.FreeToTake;
        }

        // The same translation. Whether there is anything to do depends on the server having moved
        // since this copy was taken — which is exactly what the hashes answer.
        return Install.TranslationInstaller.LooksRecoverableOnline(local, picked)
            ? TranslationOffer.AlreadyInPlace
            : TranslationOffer.FreeToTake;
    }

    /// <summary>
    /// Whether the box may start ticked. ONLY where nothing is at stake.
    ///
    /// ⚠ A stored "yes" is never honoured outside this case, and that is the safety property: the
    /// answer was given about the game as it was, and a game that has since acquired unpublished
    /// work is a different question. Ticking it there has to be a fresh, deliberate act.
    /// </summary>
    public static bool MayDefaultToYes(TranslationOffer offer) => offer == TranslationOffer.FreeToTake;

    /// <summary>Why the box is not ticked for them, in one clause, or null when it is.</summary>
    public static string? Caution(TranslationOffer offer) => offer switch
    {
        TranslationOffer.ReplacesWork =>
            "there is work here that has never been uploaded",
        TranslationOffer.ReplacesChoice =>
            "this game is already using a different translation",
        _ => null,
    };
}
