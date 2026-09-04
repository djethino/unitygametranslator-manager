using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Settings;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Which two languages a game's card says its translation goes between.
///
/// 🔴 **The defect these cases hold the door on: the pair was shown only for a PUBLISHED
/// translation.** It was drawn inside the line naming the author, so a file somebody is building
/// and has never uploaded — the ordinary state — carried its size, its make-up and its standing,
/// and never said what it translates into. Nothing justified hiding it: the answer sits in the
/// game's own config.json whether or not any server has heard of the file.
///
/// The order of authority is the install path's (GameLanguages.TargetFor), and it is checked here
/// field by field, which is the part a reader would not guess: a published entry missing its source
/// must not erase the source the game names.
/// </summary>
internal static class LanguagePairChecks
{
    private static OnlineTranslation Published(string? source, string? target) =>
        new() { Id = 1, SourceLanguage = source, TargetLanguage = target };

    internal static void WhichLanguagesAGameShows()
    {
        Program.Section("Which languages a game's translation shows");

        // Nothing published: the game's own configuration is the answer, and it is a real one.
        var own = GameLanguages.PairFor(null, ("English", "Thai"));
        Program.Check(own is { Source: "English", Target: "Thai" },
            "an unpublished file shows the game's own pair", "this is the case that showed nothing");

        // What was published is what the file IS — it was uploaded under that pair and is listed
        // under it, so it wins over whatever the game happens to name.
        var moved = GameLanguages.PairFor(Published("English", "French"), ("English", "Thai"));
        Program.Check(moved.Target == "French",
            "a published translation decides its own target", "same authority as TargetFor");

        // ⚠ Field by field. An old entry with no source must fall back rather than blank the one
        // this game names — a wholesale choice would lose it.
        var half = GameLanguages.PairFor(Published(null, "French"), ("English", "Thai"));
        Program.Check(half is { Source: "English", Target: "French" },
            "a missing published field falls back on its own", "never wholesale");

        // "auto" and blanks are already null by the time they get here (ReadLanguages), and a blank
        // published field is not an answer either.
        var blank = GameLanguages.PairFor(Published("", "  "), (null, null));
        Program.Check(!blank.Known && blank is { Source: null, Target: null },
            "blanks are not answers", "nothing known stays nothing known");

        // 🔴 What is WRITTEN when nothing is known — the two words, from one place. A missing target
        // is worded as the gap it is, never as "auto": the mod resolves it from the machine's locale
        // at launch, so a game left that way means something different on every machine.
        Program.Check(blank.SourceLabel == GameLanguages.SourceUnstated
                      && blank.TargetLabel == GameLanguages.TargetUnstated,
            "an unstated language is named, not blank", "a gap on screen says nothing");

        // A source nobody declared is a real answer and stands beside a target that is settled.
        var detected = GameLanguages.PairFor(null, (null, "Thai"));
        Program.Check(detected.Known && detected.SourceLabel == GameLanguages.SourceUnstated
                      && detected.Target == "Thai",
            "a detected source shows beside a real target", "the mod detects it line by line");
    }
}
