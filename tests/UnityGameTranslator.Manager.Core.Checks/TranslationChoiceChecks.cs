using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Which translation a game would receive, and — far more important — when it would receive none.
///
/// 🔴 **Every case here is one this program got wrong.** The rule lived in two methods of an
/// eleven-thousand-line window and read one stored field for two different questions: "which one
/// did somebody name" and "which one is already in this game". So a translation chosen in the list
/// one evening went on being offered for ever, with Apply lit, over a hundred lines of unpublished
/// work done in the game since — and the guard written to stop exactly that sat in a branch the
/// stored id never reached.
///
/// The rule the owner stated, and the one these cases hold to: **a translation is chosen for
/// somebody only when they have none at all, including a purely local one nobody else has seen.**
/// </summary>
internal static class TranslationChoiceChecks
{
    private static GameInstall Game() => new()
    {
        Name = "A game",
        Path = @"C:\games\a-game",
    };

    private static OnlineTranslation Published(int id, string language = "fr") =>
        new() { Id = id, TargetLanguage = language, Uuid = $"uuid-{id}" };

    private static LocalTranslation Local(string uuid = "local-only") => new()
    {
        Path = @"C:\games\a-game\translations.json",
        Uuid = uuid,
        EntryCount = 123,
        LocalChanges = 123,
    };

    private static GameReport Report(LocalTranslation? local, params OnlineTranslation[] online) =>
        new() { Game = Game(), LocalTranslation = local, OnlineTranslations = online };

    internal static void WhichTranslationAGameWouldGet()
    {
        Program.Section("Which translation a game would get");

        // Nothing published: there is nothing to offer, whatever else is true.
        Program.Check(
            TranslationChoice.Waiting(Report(null), "fr", chosen: null, installed: null) is null,
            "nothing published means nothing offered", "no catalogue, no offer");

        // A blank game is the case the whole feature exists for.
        Program.Check(
            TranslationChoice.Waiting(Report(null, Published(7)), "fr", null, null)?.Id == 7,
            "a game with nothing gets the ranked pick", "the case one click is for");

        // ⚠ The server's order, not ours: it is ranking_score, normalised per game and already
        // without branches. Re-sorting here would give a different best from the website's.
        Program.Check(
            TranslationChoice.Waiting(Report(null, Published(7), Published(9)), "fr", null, null)?.Id == 7,
            "the first the server sent wins", "never re-ranked here");

        // Only in the language actually wanted.
        Program.Check(
            TranslationChoice.Waiting(Report(null, Published(7, "de")), "fr", null, null) is null,
            "another language is not a pick", "the target language decides");

        // 🔴 **The owner's rule, stated twice: choosing FOR somebody happens only when they have
        // nothing at all.** A translation started in the game and never uploaded has no lineage
        // online, so nothing downstream could see it — and the card offered a stranger's file.
        Program.Check(
            TranslationChoice.Waiting(Report(Local(), Published(7)), "fr", null, null) is null,
            "a purely local translation stops the ranked pick", "unpublished work has the most to lose");

        // ⚠ And the other half of that rule: a translation somebody NAMED is their decision, and it
        // keeps being honoured over a local file. What that produces is a replacement, which is
        // asked about — not a silent refusal.
        Program.Check(
            TranslationChoice.Waiting(Report(Local(), Published(7)), "fr", chosen: 7, installed: null)?.Id == 7,
            "a named choice is honoured over local work", "refusing it would make the list advisory");

        // ⚠ **A named choice that is gone from the catalogue offers NOTHING — and the two entry
        // points disagree about that.** Pick() says it falls through to the ranking ("the one they
        // named no longer being there is not a reason to leave them without one"); Waiting() never
        // lets it, because a choice that resolves to nothing ends the answer.
        //
        // The behaviour below is what ships, and it is pinned rather than corrected: substituting a
        // DIFFERENT translation for the one somebody named is the silent swap this whole area exists
        // to prevent. 🔸 Not decided — see analyse/manager-refonte-rafraichissement.md.
        Program.Check(
            TranslationChoice.Waiting(Report(null, Published(7)), "fr", chosen: 999, installed: null) is null,
            "a choice gone from the catalogue offers nothing", "no silent substitute (see Pick's note)");
    }

    internal static void WhenNothingIsWaiting()
    {
        Program.Section("When a translation is already there");

        var online = Published(12);

        // The same lineage: this game runs that very translation. Bringing a newer copy of the file
        // already here is the workbench's act, weighed against what was never uploaded.
        var sameLineage = new GameReport
        {
            Game = Game(),
            LocalTranslation = Local("uuid-12"),
            OnlineTranslations = new[] { online },
            MatchingOnline = online,
        };

        Program.Check(
            TranslationChoice.Waiting(sameLineage, "fr", chosen: 12, installed: null) is null,
            "the translation this game runs is not offered", "that is the workbench's act");

        // 🔴 **The case that shipped.** A file put here by a past install and edited since matches
        // no lineage online — MatchingOnline is null — so the test above says nothing about it,
        // while the translation is plainly already in the game. Apply lit up on a game whose owner
        // had asked for nothing.
        Program.Check(
            TranslationChoice.Waiting(Report(Local(), online), "fr", chosen: null, installed: 12) is null,
            "the one we installed is not re-offered", "even once the local file has diverged");

        // ⚠ And it holds when somebody names it again: it is still already there.
        Program.Check(
            TranslationChoice.Waiting(Report(Local(), online), "fr", chosen: 12, installed: 12) is null,
            "naming what is already installed offers nothing", "there is nothing to bring");

        // ⚠ But a DIFFERENT one, named deliberately, is still offered — the guard is about this
        // translation, not about the game having one.
        var other = Published(30);
        Program.Check(
            TranslationChoice.Waiting(Report(Local(), online, other), "fr", chosen: 30, installed: 12)?.Id == 30,
            "another one, named, is still offered", "the guard is per translation");

        // 🔴 **A leftover id on a game holding NOTHING must not withhold anything.** The same field
        // was once written by merely selecting a card, so entries from before carry an id that never
        // meant "installed". Read as such on an empty game it would quietly refuse the one
        // translation somebody was owed — a defect with no symptom at all.
        Program.Check(
            TranslationChoice.Waiting(Report(null, online), "fr", chosen: null, installed: 12)?.Id == 12,
            "a stale id on an empty game withholds nothing", "nothing is installed if nothing is there");
    }
}
