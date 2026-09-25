using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// What one row in the game list is allowed to say.
///
/// 🔴 **This is the rule the list and the card BOTH answer with, and that is why it is checked
/// first.** SituationReader is handed a GameReport and returns the sentence a row prints. It has
/// no idea which of the two paths built that report — so a field left unfilled by one of them is
/// not an error it can raise, it is a sentence that silently never appears.
///
/// Three defects have been exactly that, all three found by somebody clicking every game in their
/// library to make the list tell the truth. The cases below pin the answers so that a report
/// carrying the fact produces the sentence; whether a given builder fills the field is the
/// separate question <see cref="ReportCompletenessChecks"/> asks.
/// </summary>
internal static class SituationChecks
{
    private static GameInstall Game(bool moddable = true) => new()
    {
        Name = "A game",
        Path = @"C:\games\a-game",
        Verdict = moddable ? ModdabilityVerdict.Ok : ModdabilityVerdict.AntiCheat,
    };

    private static LocalTranslation Local(int localChanges = 0, string? uuid = "u-1", string? mergedMainHash = null) => new()
    {
        Path = @"C:\games\a-game\translations.json",
        Uuid = uuid,
        EntryCount = 400,
        LocalChanges = localChanges,
        MergedMainHash = mergedMainHash,
    };

    /// <summary>
    /// A loader in place — what every real set-up game has.
    ///
    /// 🔴 **Added 2026-09-04, and its absence was the whole point.** These fixtures described games
    /// as "installed" with no loader at all, which is not a state any working game is in — so every
    /// case here silently agreed that a plugin alone means ready. That is exactly the answer a real
    /// game gave while translating nothing (see GameReport.SetupIncomplete).
    /// </summary>
    private static DetectedLoader Loader() => new()
    {
        Id = "bepinex6-il2cpp",
        Display = "BepInEx 6 (IL2CPP)",
        Version = "6.0.0.0",
        PluginDir = @"BepInEx\plugins\UnityGameTranslator",
    };

    internal static void SituationsAGameCanBeIn()
    {
        Program.Section("Situations a game can be in");

        // A refusal outranks everything: there is nothing to offer on a game we will not touch.
        var blocked = SituationReader.Read(
            new GameReport { Game = Game(moddable: false) }, "fr", onlineChecked: true);
        Program.Check(blocked.Situation == Situation.Blocked && !blocked.CanAct,
            "a game we refuse is Blocked", "and offers no verb");

        // The four words of the ecosystem, fixed 2026-08-14. They come from the shared verdict and
        // are the same ones the mod and the site print — a row inventing a fifth is a defect even
        // when the sentence is true.
        foreach (var (direction, headline, situation) in new[]
        {
            (SyncDirection.Download, "Update available", Situation.UpdateAvailable),
            (SyncDirection.Upload, "Unpublished changes", Situation.UnpublishedWork),
            (SyncDirection.Merge, "Conflict", Situation.Conflict),
        })
        {
            var report = new GameReport
            {
                Game = Game(),
                InstalledLoader = Loader(),
                LocalTranslation = Local(),
                InstalledPluginVersion = "0.12.0",
                Sync = direction,
            };

            var read = SituationReader.Read(report, "fr", onlineChecked: true);
            Program.Check(read.Headline == headline && read.Situation == situation,
                $"{direction} reads \"{headline}\"", "the ecosystem's own four words");
        }

        // Nothing published to compare against: the shared verdict cannot speak, and work that
        // exists in this game and nowhere else still deserves naming.
        var only = SituationReader.Read(
            new GameReport
            {
                Game = Game(),
                InstalledLoader = Loader(),
                LocalTranslation = Local(localChanges: 12),
                InstalledPluginVersion = "0.12.0",
            }, "fr", onlineChecked: true);
        Program.Check(only.Situation == Situation.UnpublishedWork,
            "unpublished work with no online side is still named", "Sync is null, the work is not");

        // A translation file is proof the mod ran here, whatever is on disk beside it — the row
        // must not offer to install a mod that is already working.
        //
        // ⚠ **With the loader present, which is what "already working" means.** This case used to
        // pass with no loader at all, and that is precisely the answer that turned out to be wrong
        // on a real game: it accepted a translation as proof of a working install even when nothing
        // could start. The rescue it was written for — an assembly whose version will not read — is
        // unchanged and still checked here.
        var noAssembly = SituationReader.Read(
            new GameReport { Game = Game(), InstalledLoader = Loader(), LocalTranslation = Local() },
            "fr", onlineChecked: true);
        Program.Check(noAssembly.Situation == Situation.Ready,
            "a translation file alone means set up", "no assembly found is not \"not set up\"");

        // "Nobody has published anything" and "we never asked" are different answers, and only one
        // of them is ours to state.
        var unasked = SituationReader.Read(
            new GameReport { Game = Game() }, "fr", onlineChecked: false);
        Program.Check(unasked.Situation == Situation.Unknown,
            "not having asked is its own situation", "never \"no translation exists\"");
    }

    /// <summary>
    /// Something of ours is here and nothing will run it.
    ///
    /// 🔴 **Every case below was read off a real game on 2026-09-04**, which said "Ready to play"
    /// in the list while Set up said "no loader installed" — on the same report. What produced it
    /// needs no mistake from anybody: Steam's "verify integrity of game files" deletes winhttp.dll
    /// and leaves BepInEx\plugins\ alone.
    /// </summary>
    internal static void SomethingHereThatCannotRun()
    {
        Program.Section("Something here that cannot run");

        // The exact shape observed: the plugin is on disk, no loader anywhere.
        var inert = SituationReader.Read(
            new GameReport { Game = Game(), InstalledPluginVersion = "0.12.1" },
            "fr", onlineChecked: true);
        Program.Check(inert.Situation == Situation.SetupIncomplete && inert.Headline == "Setup incomplete",
            "a plugin with no loader is not \"Ready to play\"", "the state that shipped saying it was");
        Program.Check(inert.Detail is not null && inert.Detail.Contains("mod loader", StringComparison.Ordinal),
            "and the row names the missing piece", "never a diagnosis to work out");
        Program.Check(inert.PrimaryAction == "Set up",
            "the verb is the one that fixes it", "a verb, not a sentence");

        // 🔴 And it must not promise what it has just said will not happen. The standing's fallback
        // line is "no translation file yet — it fills up as you play"; on the first build of this
        // state it was printed right after "The mod will not run."
        Program.Check(inert.Detail is not null && !inert.Detail.Contains("as you play", StringComparison.Ordinal),
            "and promises nothing about playing", "the row must not contradict itself");

        // ⚠ A translation does NOT rescue it — the decision taken on 2026-09-04. It proves the mod
        // ran here once, which is exactly what deleting the loader invalidates.
        var withWork = SituationReader.Read(
            new GameReport { Game = Game(), InstalledPluginVersion = "0.12.1", LocalTranslation = Local() },
            "fr", onlineChecked: true);
        Program.Check(withWork.Situation == Situation.SetupIncomplete,
            "a translation beside an inert plugin is still not ready", "it ran once, not now");
        Program.Check(withWork.Detail is not null && withWork.Detail.Contains("400", StringComparison.Ordinal),
            "and the work here is still named", "\"incomplete\" must not read as \"lost\"");

        // ⚠ Ahead of the sync verdicts on purpose: a conflict about a translation nothing reads is
        // not the headline. Doing nothing about a mod that cannot start costs more.
        var conflicted = SituationReader.Read(
            new GameReport
            {
                Game = Game(),
                InstalledPluginVersion = "0.12.1",
                LocalTranslation = Local(localChanges: 5),
                Sync = SyncDirection.Merge,
            }, "fr", onlineChecked: true);
        Program.Check(conflicted.Situation == Situation.SetupIncomplete,
            "and it outranks a sync verdict", "nothing reads that translation yet");

        // ⚠ The colour matters as much as the words: without its own entry this fell to the default
        // and drew with no status colour at all.
        Program.Check(inert.StatusKey == "StatusWarning",
            "the row carries a warning colour", "the loudest fact must not render quietest");

        // A game nobody has ever touched is NOT this state — there is nothing here to be inert.
        var untouched = SituationReader.Read(
            new GameReport { Game = Game() }, "fr", onlineChecked: true);
        Program.Check(untouched.Situation != Situation.SetupIncomplete,
            "an untouched game is not \"Setup incomplete\"", "nothing of ours is here to be broken");

        // 🔴 And the second screen answers the same way, which is the point of putting the rule on
        // the report. This one used to be right by accident: with a translation present it would
        // have promised "Play translated" over a game that shows nothing.
        var promise = PlayPromises.For(
            new GameReport { Game = Game(), InstalledPluginVersion = "0.12.1", LocalTranslation = Local() },
            new Settings.GameConfigSnapshot(
                Exists: true, FirstRunCompleted: true, InGameHotkey: null,
                Values: new Settings.GameModOverrides()));
        Program.Check(promise == PlayPromise.Plain,
            "and Play promises nothing", "one rule, both screens");

        // 🔴 The game's translation switch outranks the file it holds: off, the mod shows the
        // original text, and "Play translated" would be found out on the first screen.
        var ready = new GameReport
        {
            Game = Game(), InstalledLoader = Loader(), InstalledPluginVersion = "0.12.1", LocalTranslation = Local(),
        };
        var config = new Settings.GameConfigSnapshot(
            Exists: true, FirstRunCompleted: true, InGameHotkey: null, Values: new Settings.GameModOverrides());

        Program.Check(PlayPromises.For(ready, config) == PlayPromise.Translated
                      && PlayPromises.For(ready, config with { TranslationsShown = false }) == PlayPromise.TranslationOff
                      && PlayPromises.Label(PlayPromise.TranslationOff) == "Play",
            "translations switched off in the game: Play promises the original text",
            "the switch outranks the file, as it does in the mod");
    }

    /// <summary>
    /// The secondary line — and the one that proved the list was fed a poorer report than the card.
    /// </summary>
    internal static void WhatASecondLineSays()
    {
        Program.Section("The second line on a row");

        // 🔴 These three come from report.MyPosition, which for a long time only ONE of the two
        // report builders filled in — so the row could not print them at all and the card could.
        // The fact reaching the sentence is checked here; the builder filling it is checked in
        // ReportCompletenessChecks.
        foreach (var (position, fragment) in new[]
        {
            (new LineagePosition { Uuid = "u-1", IsMain = false, BranchFrozen = true }, "frozen"),
            (new LineagePosition { Uuid = "u-1", IsMain = false, MainMissing = true }, "removed"),
            (new LineagePosition { Uuid = "u-1", IsMain = false, MainAbandoned = true }, "account is gone"),
        })
        {
            var signals = SituationReader.Signals(
                new GameReport { Game = Game(), MyPosition = position }, null);

            Program.Check(signals is not null && signals.Contains(fragment, StringComparison.Ordinal),
                $"a stranded contribution says \"{fragment}\"", "reached through MyPosition");
        }

        // The judgement: said while the person has not put it away in the game, and never over a
        // wall — a closed road is the whole story.
        var ignored = SituationReader.Signals(
            new GameReport { Game = Game(), MyPosition = new LineagePosition { Uuid = "u-1", IsMain = false, MainIgnoring = true } }, null);
        Program.Check(ignored is not null && ignored.Contains("not taken in", StringComparison.Ordinal),
            "a contribution the Main moved on without says so", "reached through MyPosition");

        var putAway = SituationReader.Signals(
            new GameReport
            {
                Game = Game(),
                LocalTranslation = Local(uuid: "u-1"),
                MyPosition = new LineagePosition { Uuid = "u-1", IsMain = false, MainIgnoring = true },
                DismissedNotices = new[] { "main-ignoring:u-1" },
            }, null);
        Program.Check(putAway is null,
            "and nothing once the game's config says it was put away", "the game records the dismissal, this tool reads it");

        var walled = SituationReader.Signals(
            new GameReport { Game = Game(), MyPosition = new LineagePosition { Uuid = "u-1", IsMain = false, MainIgnoring = true, BranchFrozen = true } }, null);
        Program.Check(walled is not null && walled.Contains("frozen", StringComparison.Ordinal) && !walled.Contains("not taken in", StringComparison.Ordinal),
            "a wall outranks the judgement", "a closed road is the whole story");

        // 🔴 The Main moved since the last merge: the mod's rule, hash against hash, never content.
        var mainNow = new OnlineTranslation { FileHash = "b2", Author = "alice" };
        var moved = SituationReader.Signals(
            new GameReport
            {
                Game = Game(),
                LocalTranslation = Local(uuid: "u-1", mergedMainHash: "a1"),
                OnlineTranslations = new[] { mainNow },
                MatchingOnline = mainNow,
                MyPosition = new LineagePosition { Uuid = "u-1", IsMain = false },
            }, null);
        Program.Check(moved is not null && moved.Contains("the Main was updated", StringComparison.Ordinal),
            "a branch whose Main published since its last merge says so", "the mod's corner notification, on the row");

        var inStep = SituationReader.Signals(
            new GameReport
            {
                Game = Game(),
                LocalTranslation = Local(uuid: "u-1", mergedMainHash: "b2"),
                OnlineTranslations = new[] { mainNow },
                MatchingOnline = mainNow,
                MyPosition = new LineagePosition { Uuid = "u-1", IsMain = false },
            }, null);
        Program.Check(inStep is null, "and nothing when the Main is where the branch last merged it",
            "an act with no effect is not offered");

        var neverMerged = SituationReader.Signals(
            new GameReport
            {
                Game = Game(),
                LocalTranslation = Local(uuid: "u-1"),
                OnlineTranslations = new[] { mainNow },
                MatchingOnline = mainNow,
                MyPosition = new LineagePosition { Uuid = "u-1", IsMain = false },
            }, null);
        Program.Check(neverMerged is null, "nor when the branch never merged: unknown is not moved",
            "quiet rather than crying wolf, as the mod");

        var onAMain = SituationReader.Signals(
            new GameReport
            {
                Game = Game(),
                LocalTranslation = Local(uuid: "u-1", mergedMainHash: "a1"),
                OnlineTranslations = new[] { mainNow },
                MatchingOnline = mainNow,
                MyPosition = new LineagePosition { Uuid = "u-1", IsMain = true },
            }, null);
        Program.Check(onAMain is null, "and never on a Main, whatever the file carries",
            "a Main has no upstream to merge from");

        // ⚠ Not knowing is not the same as none — announcing "nobody is waiting" on the strength of
        // an unasked question is a guess dressed as a fact.
        var unknown = SituationReader.Signals(new GameReport { Game = Game() }, null);
        Program.Check(unknown is null,
            "an unasked lineage says nothing", "null is not zero");

        var none = SituationReader.Signals(new GameReport { Game = Game() }, 0);
        Program.Check(none is null,
            "nobody waiting says nothing", "a count of zero is not a signal");

        var waiting = SituationReader.Signals(new GameReport { Game = Game() }, 1);
        Program.Check(waiting == "1 contribution waiting",
            "one contribution is singular", "counted, not pluralised blindly");

        // ⚠ ONE line for every secondary signal, joined — a game whose contribution is stranded
        // still deserves to say its mod is old. Ranking them and dropping the loser was the bug.
        var both = SituationReader.Signals(
            new GameReport
            {
                Game = Game(),
                MyPosition = new LineagePosition { Uuid = "u-1", IsMain = false, BranchFrozen = true },
                PluginStanding = new VersionStanding("0.11.0", "0.12.0"),
            }, 2);

        Program.Check(both is not null
                      && both.Contains("frozen", StringComparison.Ordinal)
                      && both.Contains("2 contributions waiting", StringComparison.Ordinal),
            "signals are joined, never ranked", "one line, every signal");
    }

    /// <summary>The sentence a branch's card says about where it stands.</summary>
    internal static void WhatABranchSays()
    {
        Program.Section("The sentence on a branch");

        // 🔴 Each wall changes what the sentence promises: "reviewed by X" once the Main is gone,
        // its owner's account is, or contributions are closed, kept somebody working towards a
        // review that would never come. The abandoned one said exactly that (2026-09-18).
        foreach (var (position, fragment, never) in new[]
        {
            (new LineagePosition { Uuid = "u-1", IsMain = false, MainMissing = true }, "Main was removed", "reviewed by"),
            (new LineagePosition { Uuid = "u-1", IsMain = false, MainAbandoned = true }, "Main's account was deleted", "reviewed by"),
            (new LineagePosition { Uuid = "u-1", IsMain = false, BranchFrozen = true }, "no longer takes contributions", "reviewed by"),
        })
        {
            var said = position.Describe("alice");
            Program.Check(said.Contains(fragment, StringComparison.Ordinal) && !said.Contains(never, StringComparison.Ordinal),
                $"a walled branch says \"{fragment}\" and never \"{never}\"", "the wall changes the promise");
        }

        var open = new LineagePosition { Uuid = "u-1", IsMain = false }.Describe("alice");
        Program.Check(open.Contains("reviewed by alice", StringComparison.Ordinal),
            "an open branch names its reviewer", "the ordinary case");
    }
}
