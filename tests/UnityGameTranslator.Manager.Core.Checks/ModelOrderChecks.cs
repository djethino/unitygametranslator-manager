using UnityGameTranslator.Manager.Core.Catalog;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// The order the model table is presented in, and the two marks it carries.
///
/// 🔴 **Why this needs checking rather than reading.** The mark this replaced — awarded to any
/// model that followed every instruction — was correct when it was written and became meaningless
/// without a single line changing: models improved until nine rows out of ten met its condition.
/// Nothing failed, nothing warned, and the table quietly stopped helping anyone choose. A rule
/// whose usefulness depends on the data it is applied to has to be asked the question again.
///
/// ⚠ Every case below is SYNTHETIC on purpose. Pinning the checks to the live catalogue would make
/// them fail the day a model is added — which is not a defect — while telling us nothing about the
/// rule. What is checked here is the rule; what the catalogue currently contains is the reader's
/// business, not ours.
///
/// 🔸 THE SAME RULE IS APPLIED BY THE WEBSITE — `App\Services\ModelCatalog::order()`, checked by
/// `CatalogMirrorTest`. Both must be changed together, and both carry the same cases for that
/// reason: the same catalogue coming out in two orders, in two of our own tools, reads to a user
/// as one of them being wrong.
/// </summary>
internal static class ModelOrderChecks
{
    /// <summary>A model with every figure spelled out, so each case says only what it varies.</summary>
    private static ModelNote Model(string pull, double held, double needs,
                                   int suite = 16, int suiteOf = 16,
                                   int retried = 0, int refused = 0,
                                   bool strict = false, double load = 10,
                                   string role = "tested") => new()
    {
        Match = pull,
        Pull = pull,
        Role = role,
        MinVramGb = needs,
        Measured = new ModelMeasurements
        {
            VramGb = held,
            Suite = suite,
            SuiteOf = suiteOf,
            Retried = retried,
            Refused = refused,
            Lines = 20,
            StrictSource = strict,
            LoadSeconds = load,
        },
    };

    private static IReadOnlyList<string> Order(long? vram, params ModelNote[] models) =>
        ModelNotesProvider
            .Installable(new ModelNotesDocument { Models = models.ToList() }, vram)
            .Select(note => note.Pull!)
            .ToList();

    /// <summary>No card reading at all — the case a web page is permanently in.</summary>
    private static readonly long? Unknown = null;

    internal static void HowModelsAreRanked()
    {
        Program.Section("Model table — the order it is presented in");

        // 🔴 The top rung, and the one that must never be traded away. A line the model gives up on
        // stays in its original language on screen while somebody plays; everything else on this
        // ladder is a wait. Half the memory does not buy that back.
        Program.Check(
            Order(Unknown,
                  Model("gives-up", held: 1.0, needs: 4, suite: 16, refused: 1),
                  Model("heavy-but-complete", held: 20.0, needs: 24))
                is ["heavy-but-complete", "gives-up"],
            "a lost line outranks any amount of memory",
            "untranslated text is not a kind of slowness");

        // Same shape, one rung down: following every instruction beats being small.
        Program.Check(
            Order(Unknown,
                  Model("misses-one", held: 1.0, needs: 4, suite: 15),
                  Model("heavy-but-complete", held: 20.0, needs: 24))
                is ["heavy-but-complete", "misses-one"],
            "an unfollowed instruction outranks memory",
            "the suite is what the mod actually asks of a model");

        // ⚠ A THRESHOLD, never a count. Four retries out of twenty against five is not a difference
        // anybody can act on, and ranking on it would seat a 7.8 GB model above a 2.8 GB one over a
        // single line. Once both are past the threshold, what decides is the memory left to play in.
        Program.Check(
            Order(Unknown,
                  Model("five-retries-light", held: 2.8, needs: 4, retried: 5),
                  Model("four-retries-heavy", held: 7.8, needs: 10, retried: 4))
                is ["five-retries-light", "four-retries-heavy"],
            "retries are a threshold, not a score",
            "four out of twenty and five is not a choice");

        // ...but the threshold itself is real: needing no second go at all is worth more than a
        // gigabyte, because the retry is paid on the line somebody is waiting to read.
        Program.Check(
            Order(Unknown,
                  Model("retries-lighter", held: 1.7, needs: 4, retried: 4),
                  Model("clean-heavier", held: 3.1, needs: 4, retried: 0))
                is ["clean-heavier", "retries-lighter"],
            "never being asked twice outranks memory",
            "a retry is the same line paid for twice, while playing");

        // 🔴 The measured figure, NOT the requirement. min_vram_gb is rounded up to real card sizes,
        // so models holding 1.7 and 3.1 GB both read "4 GB" and used to sort as equals — collapsing
        // the one difference this rung exists to expose.
        Program.Check(
            Order(Unknown,
                  Model("holds-more", held: 3.1, needs: 4),
                  Model("holds-less", held: 1.7, needs: 4))
                is ["holds-less", "holds-more"],
            "memory is compared as measured, not as rounded",
            "both ask for a 4 GB card and one leaves twice as much to the game");

        // An extra capability at equal cost, so it settles a tie and never creates one.
        Program.Check(
            Order(Unknown,
                  Model("plain", held: 5.0, needs: 8),
                  Model("strict", held: 5.0, needs: 8, strict: true))
                is ["strict", "plain"],
            "strict source settles a tie",
            "same cost, one more thing it can be asked to do");

        // The last word, and only ever the last: paid once, while a game is starting.
        Program.Check(
            Order(Unknown,
                  Model("slow-start", held: 5.0, needs: 8, load: 30),
                  Model("quick-start", held: 5.0, needs: 8, load: 6))
                is ["quick-start", "slow-start"],
            "load time breaks what is otherwise identical",
            "paid once a session, not once a line");

        // 🔴 Being what this project develops against is a fact about US, not a measurement. Ranking
        // it first put a 16 GB model at the top of a table people read to find one that fits.
        Program.Check(
            Order(Unknown,
                  Model("reference-heavy", held: 16.1, needs: 24, role: "reference"),
                  Model("light", held: 3.1, needs: 4))
                is ["light", "reference-heavy"],
            "the reference model is not forced first",
            "it carries a mark saying what it is; it does not get a rank for it");

        // An unknown score is not a zero, and is no reason to lead with it either.
        Program.Check(
            Order(Unknown,
                  new ModelNote { Match = "never-run", Pull = "never-run", MinVramGb = 4 },
                  Model("measured", held: 20.0, needs: 24))
                is ["measured", "never-run"],
            "an unmeasured model sorts last",
            "nothing is claimed about it, so nothing puts it in front");

        // ⚠ The ONE key the website legitimately does not have: a web page has no idea what card
        // the reader owns, so it never demotes anything. Here, a model that spills out of the card
        // falls back to the processor — minutes a line rather than seconds.
        Program.Check(
            Order(6L * 1024 * 1024 * 1024,
                  Model("perfect-too-big", held: 16.1, needs: 24),
                  Model("retries-but-fits", held: 1.7, needs: 4, retried: 4))
                is ["retries-but-fits", "perfect-too-big"],
            "what fits this card comes first",
            "a model that spills to the processor is a trap, not a recommendation");

        // ...and an unread card demotes nobody. Offering nothing because a number could not be read
        // is worse than listing everything with its requirement stated.
        Program.Check(
            Order(Unknown,
                  Model("big", held: 16.1, needs: 24),
                  Model("small", held: 1.7, needs: 4, retried: 4))
                is ["big", "small"],
            "an unknown card size demotes nothing",
            "unknown is not small");
    }

    internal static void WhichRowsCarryAMark()
    {
        Program.Section("Model table — the marks");

        var reference = Model("reference", held: 16.1, needs: 24, role: "reference", strict: true);
        var lightest = Model("tiny", held: 1.7, needs: 4, retried: 4);
        var middling = Model("middling", held: 3.1, needs: 4);
        var broken = Model("broken", held: 0.9, needs: 4, refused: 1);

        var rows = new[] { reference, lightest, middling, broken };

        string? Mark(ModelNote note) => ModelNotesProvider.Standout(note, rows);

        Program.Check(Mark(reference) == "Used in development",
            "the reference says what it is",
            "answers 'what do you run yourselves'");

        // 🔴 The point of the whole revision: this row is sixth in the order, because four retries
        // out of twenty is a real cost. It is still the answer to 'I have a small card', and the
        // order alone buries it.
        Program.Check(Mark(lightest) == "Lightest that missed nothing",
            "the lightest complete model is marked",
            "answers 'I have a small card', which the order cannot");

        Program.Check(Mark(middling) is null && Mark(broken) is null,
            "nothing else carries a mark",
            "a mark on every row is a mark on none");

        // ⚠ LIGHTEST, not smallest. A model that leaves a line untranslated is not in the running,
        // however little memory it holds.
        Program.Check(Mark(broken) is null,
            "a model that gave up on a line is never the lightest",
            "0.9 GB, and it is not a candidate");

        // The condition the previous mark used — followed everything, gave up on nothing — is met
        // by almost every model now. It has to stay a floor, and never become the distinction.
        Program.Check(
            new[] { reference, lightest, middling }.Count(m => m.Measured!.Flawless) == 3
            && new[] { reference, lightest, middling }.Count(m => Mark(m) is not null) == 2,
            "'followed everything' is a floor, not a distinction",
            "three rows meet it; two are marked, on other grounds");

        // If the reference were ever the lightest too, the honest outcome is that nothing else
        // carries the mark: handing it to the next one up would name the wrong model.
        var tinyReference = Model("tiny-reference", held: 1.0, needs: 4, role: "reference");
        var others = new[] { tinyReference, middling };

        Program.Check(
            ModelNotesProvider.Standout(tinyReference, others) == "Used in development"
            && ModelNotesProvider.Standout(middling, others) is null,
            "the light mark is never passed down",
            "the second lightest is not the lightest");
    }
}
