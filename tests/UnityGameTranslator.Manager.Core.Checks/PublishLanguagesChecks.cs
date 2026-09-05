using UnityGameTranslator.Manager.Core.Api;
using static UnityGameTranslator.Manager.Core.Api.PublishLanguages;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Which languages a publication travels under, and which of them is asked.
///
/// 🔴 **The defect these cases hold the door on: the tool refused to publish a translation whose
/// source read "auto".** That is the ordinary state of every file nobody has published — the mod
/// detects the source line by line and only fixes it at the first publication, when it ASKS. So
/// the door said "set it in the game" about a question the tool should have put itself, and it
/// said it on updates too, where the server does not even read what is sent.
///
/// The rule is the mod's own (UploadSetupPanel, TranslatorCore.SettleTargetLanguageOnFirstLine):
/// target fixed by the file, source asked once, nothing asked on an update or a contribution.
/// </summary>
internal static class PublishLanguagesChecks
{
    private static readonly Pair Nothing = new(null, null);

    internal static void WhichLanguagesAPublicationTravelsUnder()
    {
        Program.Section("Which languages a publication travels under");

        // 🔴 The case that was refused. A first publication with a source still detected: the
        // target is what the file settled on, the source is asked, nothing suggested.
        var first = Decide(PublishOutcome.NewTranslation, Nothing, new Pair(null, "French"), Nothing);
        Program.Check(first is { CanProceed: true, Target: "French", SourceIsAsked: true, Source: null },
            "a first publication asks the source", "the file's target is fixed, nothing suggested");

        // Somebody who set the source beforehand — strict source language needs it — gets it
        // prefilled, and is still asked: prefilled is not decided.
        var strict = Decide(PublishOutcome.NewTranslation, Nothing, new Pair(null, "French"),
                            new Pair("English", "French"));
        Program.Check(strict is { SourceIsAsked: true, Source: "English", Target: "French" },
            "a source the game names is suggested, still asked", "strict source users set it first");

        // ⚠ The FILE outranks the config: the mod keeps the config following the file, so a config
        // that disagrees is one that has not caught up.
        var stale = Decide(PublishOutcome.NewTranslation, Nothing, new Pair("English", "Thai"),
                           new Pair("English", "French"));
        Program.Check(stale.Target == "Thai",
            "the file's target wins over the config's", "the file is what the translation IS");

        // A file that says nothing falls back on the config, which the mod also settles.
        var older = Decide(PublishOutcome.NewTranslation, Nothing, Nothing, new Pair(null, "French"));
        Program.Check(older is { CanProceed: true, Target: "French" },
            "a file written before it said so falls back on the config", "older mods wrote no _target_language");

        // No target anywhere: nothing to publish into, refused before any window.
        var empty = Decide(PublishOutcome.NewTranslation, Nothing, Nothing, Nothing);
        Program.Check(empty is { CanProceed: false, Refusal: NoTargetYet },
            "no target at all is refused, and says why", "the mod sets it with the first line");

        // 🔴 An update or a contribution is never asked: the server keeps the lineage's pair and
        // ignores what is sent. What the server said is what is shown, whatever this machine says.
        // ⚠ The CONFIG may disagree freely — it is a preference, and the mod rewrites it from the
        // server at every launch. Only the file's own stamp can conflict (see further down).
        var update = Decide(PublishOutcome.UpdateMine, new Pair("English", "French"),
                            new Pair(null, "French"), new Pair("Japanese", "German"));
        Program.Check(update is { CanProceed: true, SourceIsAsked: false, Source: "English", Target: "French" },
            "an update shows the lineage's pair and asks nothing", "the server keeps what it was published with");

        var contribute = Decide(PublishOutcome.ContributeToTheirs, new Pair("English", "French"),
                                Nothing, Nothing);
        Program.Check(contribute is { CanProceed: true, SourceIsAsked: false, Source: "English", Target: "French" },
            "a contribution inherits the Main's pair", "and is told so before sending");

        // ⚠ "auto" on this machine is not a disagreement and not a refusal on an update: it is the
        // ordinary state of a copy that was never written back, resolved from the server.
        var autoHere = Decide(PublishOutcome.UpdateMine, new Pair("English", "French"),
                              Nothing, new Pair("auto", "auto"));
        Program.Check(autoHere is { CanProceed: true, Source: "English" },
            "\"auto\" on this machine never blocks an update", "this was the refusal on updates");

        // An older site that did not say: this machine's own answer stands in, file first.
        var silent = Decide(PublishOutcome.UpdateMine, Nothing, new Pair("English", "French"), Nothing);
        Program.Check(silent is { CanProceed: true, Source: "English", Target: "French" },
            "a site that did not say falls back on this machine", "field by field, file before config");

        var nobody = Decide(PublishOutcome.UpdateMine, Nothing, Nothing, new Pair(null, "French"));
        Program.Check(nobody is { CanProceed: false, Refusal: LineagePairUnknown },
            "nothing known anywhere on an update is refused", "the pair cannot be invented here");

        // 🔴 A file that STATES another pair is a different translation, not an update — the
        // restored-backup case. The mod refuses it; this tool used to send it under the lineage's
        // pair without a word. The refusal is the socle's sentence: both languages named, and Fork.
        var backup = Decide(PublishOutcome.UpdateMine, new Pair("English", "French"),
                            new Pair("English", "Thai"), Nothing);
        Program.Check(backup is { CanProceed: false, Refusal: { } why }
                      && why.Contains("Thai") && why.Contains("French") && why.Contains("Fork"),
            "a file stating another target is refused as an update", "the socle's rule, the mod's wording");

        // ⚠ And only a STATED disagreement: a file that says nothing keeps publishing fine.
        var silentFile = Decide(PublishOutcome.UpdateMine, new Pair("English", "French"),
                                new Pair("auto", null), Nothing);
        Program.Check(silentFile is { CanProceed: true, Target: "French" },
            "a file that states nothing is not a conflict", "most files were written before the stamp");

        // A code against a name is the same language, not a conflict.
        var spelled = Decide(PublishOutcome.UpdateMine, new Pair("English", "French"),
                             new Pair("en", "fr"), Nothing);
        Program.Check(spelled is { CanProceed: true, Source: "English", Target: "French" },
            "a code against a name is not a conflict", "compared through the catalogue, and the lineage's names win");
    }

    internal static void WhatASourceMayBe()
    {
        Program.Section("What a declared source may be");

        Program.Check(Complaint(null, "French") == ChooseSource && Complaint("auto", "French") == ChooseSource,
            "no source, or \"auto\", is not an answer", "the mod's own sentence");

        Program.Check(Complaint("French", "French") == SameLanguage,
            "the same language both ways is refused", "the site refuses it too");

        // ⚠ Through the catalogue: a code the catalogue lists as an alias ("zh-cn") and the name
        // it stands for are one language. A config written by hand can carry either.
        Program.Check(Complaint("zh-cn", "Simplified Chinese") == SameLanguage,
            "an alias and its name are the same language", "compared by code, not as text");

        Program.Check(Complaint("English", "French") is null,
            "a real pair is accepted", "nothing else is judged here");

        // A name the catalogue does not carry is still somebody's answer, compared as written.
        Program.Check(Complaint("Klingon", "Klingon") == SameLanguage && Complaint("Klingon", "French") is null,
            "an unknown language is compared as written", "IsSettled does not require the catalogue");
    }
}
