using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Turns everything we found about a game into one sentence and one verb.
///
/// The rule that governs all of it: a translation marked "complete" is an author's declaration
/// at a point in time, not a measurement — the total number of lines in a game is unknowable.
/// So this never concludes "there is nothing left to do here"; it says what exists, when it last
/// moved, and leaves every posture reachable.
/// </summary>
public static class SituationReader
{
    /// <param name="branchesWaiting">
    /// Contributions sitting on a Main this account leads, from AccountLineages — null when the
    /// answer is not known, which is not the same as none. Passed in rather than read here: this
    /// class is given a game, and that figure is about a PERSON across their whole library.
    /// </param>
    public static GameSituationInfo Read(GameReport report, string targetLanguage, bool onlineChecked,
                                         int? branchesWaiting = null)
    {
        var game = report.Game;

        if (!game.IsModdable)
        {
            return new GameSituationInfo(
                Situation.Blocked,
                Explain(game),
                null,
                PrimaryAction: "");
        }

        var local = report.LocalTranslation;

        // ⚠ Worked out once and attached to every answer below, instead of being one situation
        // among others. See GameSituationInfo.Pending: the headline is a competition the
        // translation states deserve to win, and "your mod is old" losing it silently was the bug.
        var pending = Signals(report, branchesWaiting);

        // A translation file is proof the mod ran here, whatever we did or did not find on disk.
        // Deciding on the assembly alone made a game with a live translation read as "not set up
        // yet" and offer to install a mod that was already working — the row contradicted the
        // player's own game.
        var installed = report.InstalledPluginVersion is not null || local is not null;

        // What exists online in the language the player actually wants.
        var inLanguage = report.OnlineTranslations
            .Where(t => Languages.Matches(t.TargetLanguage, targetLanguage))
            .OrderByDescending(t => t.LineCount)
            .ToList();

        if (installed)
        {
            // 🔴 **The sync verdict comes from the shared rule, and this row used to invent its
            // own.** GameReport.Sync is the answer the MOD reaches inside the game, from `common`;
            // the card shows it. This list decided instead from two things that are not it:
            //
            //   · `LocalChanges > 0` alone → announced "not published" for a file whose server side
            //     had ALSO moved, which is a conflict and needs settling, not an upload;
            //   · the published date against the local file's LAST WRITE TIME → a filesystem
            //     stamp the mod bumps whenever it caches a line, so a real update went unmentioned
            //     for any game somebody had simply played.
            //
            // One verdict, one vocabulary. The four words are the ecosystem's own, fixed on
            // 2026-08-14: Up to date / Update available / Unpublished changes / Conflict.
            if (report.Sync is { } sync && sync != SyncDirection.InSync)
            {
                var (headline, verb, situation) = sync switch
                {
                    SyncDirection.Merge => ("Conflict", "Manage", Situation.Conflict),
                    SyncDirection.Upload => ("Unpublished changes", "Manage", Situation.UnpublishedWork),
                    SyncDirection.Download => ("Update available", "Update", Situation.UpdateAvailable),
                    _ => ("", "", Situation.Ready),
                };

                if (headline.Length > 0)
                    return new GameSituationInfo(situation, headline, Standing(report, local), verb, pending);
            }

            // Nothing published to compare against — see GameReport.Sync, whose null covers exactly
            // that. Work that exists in this game and nowhere else is still worth naming, and the
            // shared verdict cannot name it because there is no other side to the comparison.
            if (report.Sync is null && local is { LocalChanges: > 0 })
            {
                return new GameSituationInfo(
                    Situation.UnpublishedWork, "Unpublished changes",
                    Standing(report, local), "Manage", pending);
            }

            // What we put there ourselves being out of date, which nothing used to notice: this
            // enum has always promised "a newer plugin OR a newer translation" and only ever
            // delivered the second, so a game could sit on a plugin four versions old and read
            // "Ready to play". Deliberately after the translation, which is what the player sees
            // on screen, and after unpublished work, which is the only one of the three where
            // waiting costs something that cannot be recovered.
            if (Behind(report) is { } behind) return behind;

            return new GameSituationInfo(Situation.Ready, "Ready to play",
                                         Standing(report, local), "Manage", pending);
        }

        if (!onlineChecked)
        {
            return new GameSituationInfo(
                Situation.Unknown,
                "Not set up yet",
                "Community translations were not checked",
                "Set up");
        }

        if (inLanguage.Count > 0)
        {
            var best = inLanguage[0];
            var others = inLanguage.Count > 1 ? $", {inLanguage.Count - 1} other(s)" : "";

            // "complete" is repeated as the author's word, with the date next to it: complete
            // and last touched fourteen months ago is a different proposition from complete
            // last week, and only showing the label would hide that.
            var quality = string.Equals(best.Status, "complete", StringComparison.OrdinalIgnoreCase)
                ? "complete according to its author"
                : "in progress";

            return new GameSituationInfo(
                Situation.TranslationAvailable,
                $"Translation available in {Languages.NameOf(targetLanguage)}",
                $"{quality}{Freshness(best, prefix: " · ")}{others}",
                "Install and play");
        }

        // ⚠ "Be the first" rather than the bare absence. The row used to state a lack and stop
        // there, which reads as a door closed — nothing here for you — when it is the opposite:
        // this is the one situation where a person can do something nobody else has. Three words,
        // and they turn a dead end into an opening.
        //
        // Deliberately not a demand. Whether anyone takes it up depends on their machine, their
        // patience and their languages, and none of that is ours to press on.
        return new GameSituationInfo(
            Situation.NotTranslatedYet,
            $"No {Languages.NameOf(targetLanguage)} translation yet — be the first",
            report.OnlineTranslations.Count > 0
                ? $"{report.OnlineTranslations.Count} translation(s) in other languages"
                : null,
            "Install and translate");
    }

    /// <summary>
    /// The second line of a row: what this account IS to the translation here, and how big it is.
    ///
    /// 🔴 **The role was missing entirely, and it changes what the line above is worth.**
    /// "Unpublished changes" on a translation this account LEADS means work only they can publish;
    /// the same words on somebody else's lineage mean work that would go up as a branch, for its
    /// Main to take or leave. Two different situations, and the list said the same thing for both.
    ///
    /// ⚠ The vocabulary is the ecosystem's: Main and Branch, never "Contributor" on its own — see
    /// CLAUDE.md. A branch IS a contribution, so the two words travel together or not at all.
    ///
    /// ⚠ Silent when the role is unknown, which covers signed out, not read yet, and a lineage this
    /// account has no part in. Those three are not distinguished on purpose: telling them apart
    /// would let a row make a claim it cannot support.
    /// </summary>
    private static string? Standing(GameReport report, LocalTranslation? local)
    {
        var parts = new List<string>();

        if (report.MyPosition is { } position)
            parts.Add(position.IsMain ? "your Main" : "your branch (contributor)");

        if (local is { EntryCount: > 0 }) parts.Add($"{local.EntryCount} lines");

        return parts.Count > 0
            ? string.Join(" · ", parts)
            : local is null ? "no translation file yet — it fills up as you play" : null;
    }

    /// <summary>
    /// The mod, or the loader we installed, being older than what is published. Null when both
    /// are current — or when we could not find out, which is not the same thing and must not
    /// produce a claim either way.
    ///
    /// Both are named when both apply. Reporting one and staying silent on the other would have
    /// somebody update, look again, and be told there is another update — twice, for something
    /// that was known in one go.
    /// </summary>
    /// <summary>
    /// "mod", "loader" or "mod and loader" when something installed is behind, else null.
    ///
    /// ⚠ Two words, not a version pair: this rides on a list row beside a headline, and
    /// "mod 0.11.0 → 0.12.1" there would compete with the sentence that says what the game is FOR.
    /// The versions are on the game's own card, where there is room to act on them.
    /// </summary>
    /// <summary>
    /// The secondary line: what is behind, and what is waiting — joined, or null when neither.
    ///
    /// ⚠ Contributions come first. An update is available whenever somebody gets round to it;
    /// a contributor is waiting on a person, and that is the one thing on this row where the delay
    /// is felt by somebody else.
    /// </summary>
    public static string? Signals(GameReport report, int? branchesWaiting)
    {
        var parts = new List<string>();

        if (branchesWaiting is > 0 and var waiting)
            parts.Add(waiting == 1 ? "1 contribution waiting" : $"{waiting} contributions waiting");

        if (OutOfDate(report) is { } behind) parts.Add($"{behind} update available");

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    public static string? OutOfDate(GameReport report)
    {
        var plugin = report.PluginStanding is { UpdateAvailable: true };

        // 🔴 **LoaderStanding, not LoaderUpdateOffered — the row INFORMS, it does not offer.**
        //
        // Hiding a newer loader until somebody had taken the loader over meant the one thing that
        // would make them take it over was invisible: nothing said a newer version existed, so
        // nothing sent them to Set up to allow it. A row is where you learn a game needs looking
        // at; whether we may act is a question for the card.
        //
        // The wording carries the difference — "loader update available (not ours)" — so nobody
        // presses one-click expecting it to be taken care of.
        var loaderNewer = report.LoaderStanding is { UpdateAvailable: true };
        var loaderOurs = report.LoaderUpdateOffered;

        var loader = loaderNewer
            ? (loaderOurs ? "loader" : "loader (not ours)")
            : null;

        return (plugin, loader) switch
        {
            (true, not null) => $"mod and {loader}",
            (true, null) => "mod",
            (false, not null) => loader,
            _ => null,
        };
    }

    private static GameSituationInfo? Behind(GameReport report)
    {
        var plugin = report.PluginStanding is { UpdateAvailable: true } p ? p : null;

        // ⚠ LoaderUpdateOffered, not LoaderStanding: this situation carries the verb "Update", and
        // a card that offers to update a loader we may not touch would be promising something it
        // refuses to do. The newer version is still reported on the game's own card, as a fact
        // with the reason we leave it alone beside it.
        var loader = report.LoaderUpdateOffered ? report.LoaderStanding : null;

        if (plugin is null && loader is null) return null;

        var what = plugin is not null && loader is not null
            ? "Mod and loader updates available"
            : plugin is not null
                ? "Mod update available"
                : "Loader update available";

        var detail = new List<string>();
        if (plugin is not null) detail.Add($"mod {plugin.Installed} → {plugin.Available}");
        if (loader is not null) detail.Add($"loader {loader.Installed} → {loader.Available}");

        return new GameSituationInfo(Situation.UpdateAvailable, what,
                                     string.Join(" · ", detail), "Update");
    }

    /// <summary>
    /// Postures offered for a game, best-first. All of them stay reachable whenever a translation
    /// exists — including a complete one, which is exactly the case where assuming would shut
    /// the door on someone willing to finish what an author could not reach.
    /// </summary>
    public static IReadOnlyList<Posture> PosturesFor(GameReport report, string targetLanguage)
    {
        var hasTranslation = report.OnlineTranslations
            .Any(t => Languages.Matches(t.TargetLanguage, targetLanguage));

        // Nothing published means there is nothing to take, so the choice collapses to the only
        // honest one. Offering "use it" over an empty catalogue would be a button for a file that
        // does not exist.
        return hasTranslation
            ? new[] { Posture.Use, Posture.Complete, Posture.Start }
            : new[] { Posture.Start };
    }

    public static string Describe(Posture posture) => posture switch
    {
        Posture.Use => "Use the published translation as it is",
        Posture.Complete => "Use it, and translate what it does not cover",
        Posture.Start => "Start this game's translation from nothing",
        _ => posture.ToString(),
    };

    /// <summary>
    /// What the posture means for this game, in the consequence rather than the intention — the
    /// sentence somebody reads to check they picked the right one.
    /// </summary>
    public static string Consequence(Posture posture) => posture switch
    {
        Posture.Use => "Text the translation does not cover stays in the game's own language.",
        Posture.Complete => "Whatever it does not cover is translated as you meet it, and joins "
                          + "the file as machine work you can review later.",
        Posture.Start => "Nothing is downloaded. The mod captures the game's text as you play, "
                       + "for a translator to fill in or for you to write yourself.",
        _ => "",
    };

    /// <summary>
    /// Dates come from content_updated_at, never updated_at: a vote or a download bumps the
    /// latter, which would show an abandoned translation as freshly maintained.
    /// </summary>
    private static string Freshness(OnlineTranslation translation, string prefix = "")
    {
        if (translation.ContentUpdatedAt is not { } date) return "";

        var days = (int)(DateTimeOffset.UtcNow - date).TotalDays;
        var text = days switch
        {
            < 0 => date.ToString("yyyy-MM-dd"),
            0 => "updated today",
            1 => "updated yesterday",
            < 30 => $"updated {days} days ago",
            < 365 => $"updated {days / 30} month(s) ago",
            _ => $"updated {days / 365} year(s) ago",
        };
        return prefix + text;
    }

    private static string Explain(GameInstall game) => game.Verdict switch
    {
        ModdabilityVerdict.AntiCheat => $"Cannot be modded — {game.VerdictDetail}",
        ModdabilityVerdict.StoreProtected => "Cannot be modded — store-protected build",
        ModdabilityVerdict.RuntimeUnknown => "Not identified — Mono or IL2CPP could not be read",
        ModdabilityVerdict.ArchitectureUnknown => "Not identified — 32-bit or 64-bit could not be read",
        ModdabilityVerdict.StrippedRuntime => "Cannot be modded — the game ships a stripped runtime",
        ModdabilityVerdict.NotUnity => "Not a Unity game",
        _ => "Cannot be modded",
    };
}
