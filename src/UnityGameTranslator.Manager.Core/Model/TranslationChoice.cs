using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>
/// Which community translation a game would receive, and whether it would receive one at all.
///
/// 🔴 **Two questions that shared one answer, and that cost real work.** "Which one did somebody
/// NAME for this game" and "which one is ALREADY in it" were both read from a single stored field,
/// so a choice made in the translations window one evening went on offering itself for ever — over
/// a hundred lines of unpublished work done in the game since. They are two parameters here, and
/// the names say which is which.
///
/// ⚠ Pure on purpose: given a report and three answers it decides, touching no disk, no window and
/// no clock. That is what lets the cases in Manager.Core.Checks pin it — the rule was previously
/// spread across two methods of an eleven-thousand-line window and could not be checked at all.
/// </summary>
public static class TranslationChoice
{
    /// <summary>
    /// The translation this game would receive and has NOT got — null when there is nothing to put
    /// there, or when what would be put there is already in place.
    /// </summary>
    /// <param name="chosen">
    /// What somebody named and has not applied. An intention, held for the session by the window —
    /// never something read back off disk, which is the mistake this parameter exists to prevent.
    /// </param>
    /// <param name="installed">
    /// What a past install actually put in this game, when we were the ones who put it there.
    /// </param>
    public static OnlineTranslation? Waiting(GameReport report, string targetLanguage,
                                             int? chosen, int? installed)
    {
        // The named one first, including when it is this game's own Main — which is not in the list
        // of alternatives. Failing that, the pick the one-click reads, so the card and the bar at
        // the bottom cannot describe different intentions.
        var picked = chosen is { } id
            ? report.OnlineTranslations.FirstOrDefault(t => t.Id == id)
              ?? (report.MatchingOnline is { } main && main.Id == id ? main : null)
            : Pick(report, targetLanguage, chosen);

        if (picked is null) return null;

        // ⚠ This covers AlreadyInPlace and more: the same lineage with the server ahead reports
        // FreeToTake, and that is precisely the case the workbench owns — "Download what changed
        // online…", which weighs the merge and carries its own scope mark.
        if (report.MatchingOnline is { } here && here.Id == picked.Id) return null;

        // 🔴 **And the one we put here ourselves, which MatchingOnline cannot always see** — but
        // ONLY when nobody named it. A local file that has since diverged, or was never published,
        // matches no lineage online, so the test above says nothing about it while the translation
        // is plainly already there. That is how a game carrying a hundred lines of somebody's own
        // work was offered the very translation it had been set up from, with Apply lit and nothing
        // asked for.
        //
        // ⚠ **A named choice passes.** Refusing it would close the only door there is: with no
        // lineage in common the workbench has nothing to offer either, so somebody deciding to go
        // back to the published version over their own edits would be met with silence and no way
        // forward. Same principle as Pick states — what is refused is choosing on their BEHALF, and
        // a replacement they asked for is weighed and warned about where it happens.
        //
        // ⚠ **Only while a file is actually there**, and that condition is load-bearing rather than
        // cautious. Entries written before this field meant one thing are ambiguous: the same
        // `translation_id` was once saved by merely SELECTING a card. Read as "installed" on a game
        // holding nothing, such a leftover would silently withhold the one translation somebody was
        // owed. A game with no local translation has nothing installed, whatever the file says.
        if (chosen is null && report.LocalTranslation is not null
            && installed is { } already && already == picked.Id)
        {
            return null;
        }

        return picked;
    }

    /// <summary>
    /// Which translation one click would take, or null when it would take none.
    ///
    /// The rules, in order, and each of them is a decision rather than a convenience:
    ///  · what the person already chose wins — a pick made in the translations window is an answer,
    ///    and quietly preferring our own would make that window advisory;
    ///  · otherwise the FIRST one published in their language, in the order the SERVER sent. That
    ///    order is Translation::ranking_score, which normalises by the best score of the game and
    ///    already leaves branches out. Re-sorting here would produce a different best from the
    ///    website's for the same data, and neither could be called wrong;
    ///  · a file already in the game does NOT stop a NAMED choice, because a newer version of that
    ///    very translation is worth taking. What it does is turn the step into a replacement, which
    ///    is asked about.
    /// </summary>
    public static OnlineTranslation? Pick(GameReport report, string targetLanguage, int? chosen)
    {
        if (report.OnlineTranslations.Count == 0) return null;

        if (chosen is { } id)
        {
            var picked = report.OnlineTranslations.FirstOrDefault(t => t.Id == id);

            // Gone from the catalogue — taken down, or made private. Falling through to the ranking
            // rather than failing: the person asked for a translation, and the one they named no
            // longer being there is not a reason to leave them without one.
            if (picked is not null) return picked;
        }

        // 🔴 **Ranking one for somebody only happens when they have NOTHING.** Below this line the
        // choice is nobody's — it is the first community translation matching the target language —
        // and it was being made on a game that already holds work in progress. A translation started
        // locally and never uploaded has no MatchingOnline, so nothing downstream saw it: the card
        // offered a stranger's file, and the one-click listed replacing it as a step.
        //
        // ⚠ "Nothing" means nothing AT ALL, including a purely local file nobody else has ever seen.
        // That is the whole point: unpublished work is the case with the most to lose and the least
        // to show for itself.
        //
        // ⚠ Above this line is untouched, and must stay so: a translation somebody NAMED is their
        // decision, and it keeps being honoured whatever is on disk. What is refused here is
        // choosing on their behalf.
        if (report.LocalTranslation is not null) return null;

        return report.OnlineTranslations
            .FirstOrDefault(t => Languages.Matches(t.TargetLanguage, targetLanguage));
    }
}
