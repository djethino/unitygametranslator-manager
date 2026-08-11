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
    public static GameSituationInfo Read(GameReport report, string targetLanguage, bool onlineChecked)
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
            if (local is { LocalChanges: > 0 })
            {
                return new GameSituationInfo(
                    Situation.UnpublishedWork,
                    $"{local.LocalChanges} change(s) not published",
                    local.EntryCount > 0 ? $"{local.EntryCount} lines translated" : null,
                    "Manage");
            }

            // A newer version of the very translation in use is worth surfacing; a different
            // translation existing is not an update, it is an alternative.
            var mine = report.MatchingOnline;
            if (mine is not null && local is not null
                && mine.ContentUpdatedAt is { } remoteDate
                && local.LastWrite is { } localDate
                && remoteDate > localDate)
            {
                return new GameSituationInfo(
                    Situation.UpdateAvailable,
                    "A newer version of this translation is available",
                    Freshness(mine),
                    "Update");
            }

            // What we put there ourselves being out of date, which nothing used to notice: this
            // enum has always promised "a newer plugin OR a newer translation" and only ever
            // delivered the second, so a game could sit on a plugin four versions old and read
            // "Ready to play". Deliberately after the translation, which is what the player sees
            // on screen, and after unpublished work, which is the only one of the three where
            // waiting costs something that cannot be recovered.
            if (Behind(report) is { } behind) return behind;

            var readyDetail = local is { EntryCount: > 0 }
                ? $"{local.EntryCount} lines"
                : "no translation file yet — it fills up as you play";

            return new GameSituationInfo(Situation.Ready, "Ready to play", readyDetail, "Manage");
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
    /// The mod, or the loader we installed, being older than what is published. Null when both
    /// are current — or when we could not find out, which is not the same thing and must not
    /// produce a claim either way.
    ///
    /// Both are named when both apply. Reporting one and staying silent on the other would have
    /// somebody update, look again, and be told there is another update — twice, for something
    /// that was known in one go.
    /// </summary>
    private static GameSituationInfo? Behind(GameReport report)
    {
        var plugin = report.PluginStanding is { UpdateAvailable: true } p ? p : null;
        var loader = report.LoaderStanding is { UpdateAvailable: true } l ? l : null;

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
    /// Postures offered for a game, best-first. All four stay reachable whenever a translation
    /// exists — including a complete one, which is exactly the case where assuming would shut
    /// the door on someone willing to finish what an author could not reach.
    /// </summary>
    public static IReadOnlyList<Posture> PosturesFor(GameReport report, string targetLanguage)
    {
        var hasTranslation = report.OnlineTranslations
            .Any(t => Languages.Matches(t.TargetLanguage, targetLanguage));

        return hasTranslation
            ? new[] { Posture.Use, Posture.Contribute, Posture.Fork, Posture.Start }
            : new[] { Posture.Start };
    }

    public static string Describe(Posture posture) => posture switch
    {
        Posture.Use => "Use it as it is",
        Posture.Contribute => "Use it and offer my corrections back",
        Posture.Fork => "Take it as a starting point, as my own version",
        Posture.Start => "Start a translation for this game",
        _ => posture.ToString(),
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
