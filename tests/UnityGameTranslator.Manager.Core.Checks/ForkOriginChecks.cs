using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Where the translation a game holds was forked from — <see cref="GameReport.ForkOrigin"/>.
///
/// 🔴 **Here because the rule was three conditions inlined in a badge strip**, which is a place no
/// check can reach and where the wrong one had already been written twice: the Main's origin worn
/// by a branch (2026-09-18), and a fork nobody had published yet crediting nobody at all
/// (2026-09-20).
/// </summary>
internal static class ForkOriginChecks
{
    internal static void WhereTheFileCameFrom()
    {
        Program.Section("Where a forked translation came from");

        // ── Not a fork at all ─────────────────────────────────────────────
        Program.Check(Report(local: File()).ForkOrigin is null,
            "a translation forked from nothing credits nobody",
            "silence is the answer, not \"forked from nowhere\"");

        // ── A branch answers nothing ──────────────────────────────────────
        //
        // Its published entry is the MAIN, so the Main's origin sits right there and is not this
        // file's. Crediting it put somebody else's source under the work held on this machine.
        var branch = Report(local: File(forkedFrom: 12, lines: 540),
                            mine: new LineagePosition { Uuid = "abc", IsMain = false },
                            online: Published(7, "alice", origin: new OnlineOrigin { Author = "zoe", Lines = 900 }));
        Program.Check(branch.ForkOrigin is null,
            "a branch wears no origin, not even its Main's",
            "the published entry beside a branch is the Main, whose provenance is not the branch's");

        // ── The site's own record wins ────────────────────────────────────
        var published = Report(local: File(forkedFrom: 12, lines: 540),
                               mine: new LineagePosition { Uuid = "abc", IsMain = true },
                               online: Published(7, "alice", origin: new OnlineOrigin { Author = "zoe", Lines = 900 }),
                               others: new[] { Published(12, "someone-else") });
        Program.Check(published.ForkOrigin is { Author: "zoe", Lines: 900 },
            "a published fork is credited as the site records it",
            "the database holds the inscription, with the account read live so a rename follows");

        // ── The file's own block, and the name found among the game's rows ─
        //
        // 🔴 The case that was missing everywhere: a fork lives in a game folder until somebody
        // publishes it, and it carried _forked_from all along.
        var local = Report(local: File(forkedFrom: 12, lines: 540),
                           online: null,
                           others: new[] { Published(12, "alice") });
        Program.Check(local.ForkOrigin is { Author: "alice", Lines: 540 },
            "an unpublished fork is credited from its own file",
            "the file states the row it came from; the name is one lookup away");

        Program.Check(local.ForkOrigin is { } named && Origins.Name(named).Contains("@alice"),
            "and it reads the same as a published fork's chip",
            "one fact, one wording: the socle words it, both products show it");

        // ── The row is not among what we can see ──────────────────────────
        var unnamed = Report(local: File(forkedFrom: 12, lines: 540));
        Program.Check(unnamed.ForkOrigin is { AuthorAsked: false, Lines: 540 },
            "a source nobody could name stays unnamed rather than guessed",
            "offline, or a row that is gone — neither is \"the account was removed\"");

        Program.Check(unnamed.ForkOrigin is { } bare && !Origins.Name(bare).Contains("removed"),
            "and it never says the account was removed",
            "saying so invents a fact about somebody who may be perfectly present");

        // ── The count is the file's, never re-measured ────────────────────
        var noCount = Report(local: File(forkedFrom: 12, lines: null),
                             others: new[] { Published(12, "alice") });
        Program.Check(noCount.ForkOrigin is { Author: "alice", Lines: null },
            "a file that does not say how many lines it took says nothing",
            "zero would claim it started from an empty translation");
    }

    private static LocalTranslation File(int? forkedFrom = null, int? lines = null) => new()
    {
        Path = @"C:\game\translations.json",
        Uuid = "abc",
        EntryCount = 600,
        ForkedFromSiteId = forkedFrom,
        ForkedFromLines = lines,
    };

    private static OnlineTranslation Published(int id, string? author, OnlineOrigin? origin = null) => new()
    {
        Id = id,
        Author = author,
        Origin = origin,
    };

    private static GameReport Report(LocalTranslation? local,
                                     LineagePosition? mine = null,
                                     OnlineTranslation? online = null,
                                     OnlineTranslation[]? others = null)
    {
        var report = new GameReport
        {
            Game = new GameInstall { Name = "A game", Path = @"C:\games\a-game" },
        };

        report.LocalTranslation = local;
        report.MyPosition = mine;
        report.MatchingOnline = online;

        var listing = new List<OnlineTranslation>();
        if (online is not null) listing.Add(online);
        if (others is not null) listing.AddRange(others);
        report.OnlineTranslations = listing;

        return report;
    }
}
