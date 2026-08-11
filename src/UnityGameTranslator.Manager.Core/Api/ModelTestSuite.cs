using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>One thing we ask of a model, and how to tell whether it obeyed.</summary>
public sealed record ModelTest(
    string Name,
    string Difficulty,
    string Source,
    string Rule,
    Func<string, string, bool> Check)
{
    /// <summary>What the model was asked to do, in one line, for the report.</summary>
    public string Expectation { get; init; } = "";

    /// <summary>
    /// A capability the mod exposes as an experimental option, not something every model is
    /// expected to do today.
    ///
    /// Counting it in the main score would be misleading twice over: it would mark a perfectly
    /// usable model down for a feature the mod itself ships disabled, and it would flatten the
    /// difference between models when they all fail the same one. Reported on its own, it
    /// answers a question the user actually has — can I switch that option on with this model —
    /// and it will start passing as models get better at following instructions.
    /// </summary>
    public string? UnlocksOption { get; init; }

    /// <summary>
    /// Why the option stays experimental even when the model passes.
    ///
    /// Lives here rather than in each screen: the window and the CLI both report this, and a
    /// caveat written twice is a caveat that will end up saying two different things. Passing is
    /// a capability, never a clearance — one sentence in one language says nothing about what a
    /// whole game will do, and the failure mode of this particular option is silent.
    /// </summary>
    public string? Caveat { get; init; }
}

/// <summary>Outcome of one test: the verdict AND what the model actually said.</summary>
public sealed record ModelTestResult(
    ModelTest Test,
    string? Answer,
    bool Passed,
    string? Failure)
{
    /// <summary>
    /// The model repeated the instructions before answering.
    ///
    /// This is its own failure — the mod displays what comes back, verbatim, so echoed rules end
    /// up on the player's screen. It also poisons every other check: a placeholder appears twice
    /// (once in the echoed rule, once in the translation) and reads as "duplicated", while a
    /// technical term found inside an echoed rule reads as "kept". Both verdicts would be wrong,
    /// in opposite directions, which is why this is reported separately rather than folded in.
    /// </summary>
    public bool EchoedInstructions { get; init; }

    /// <summary>
    /// The part of the answer that is actually the translation, when the model prefixed it with
    /// the instructions. The structural checks run on this, and the report says so.
    /// </summary>
    public string? Translation { get; init; }

    /// <summary>
    /// How many requests this line cost, judged by the mod's own rules — one when it was accepted
    /// straight away, up to three when it had to be corrected.
    /// </summary>
    public int Attempts { get; init; } = 1;

    /// <summary>
    /// End to end, every attempt included. This is the delay a player lives with: the time between
    /// a line appearing in its original language and being replaced — or not being replaced at
    /// all, which is timed too, because waiting for nothing is the case worth seeing.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Whether a game would have used this answer. Deliberately separate from <see cref="Passed"/>:
    /// ours is a per-case check written for a report, this is the mod's own acceptance rule.
    /// </summary>
    public bool Accepted { get; init; } = true;

    /// <summary>The mod put back trailing line breaks the model had trimmed.</summary>
    public bool Repaired { get; init; }
}

/// <summary>
/// Puts a model through the instructions the mod really sends, hardest last.
///
/// Two rules govern this file.
///
/// First, the answer is always shown next to the verdict. Our checks are heuristics on free
/// text, so they will produce false positives and false negatives; whoever reads the report has
/// to be able to see that for themselves and overrule us. The tool measures, the user decides.
///
/// Second, nothing here is written for one particular language. The target comes from the
/// settings, and every check is about STRUCTURE — a placeholder still present, a technical term
/// untouched, no punctuation invented — never about a specific translation being "right", which
/// would only work for the languages we happen to speak.
/// </summary>
public static class ModelTestSuite
{
    /// <summary>The exact marker the mod expects for a line it must not translate.</summary>
    public const string SkipMarker = "AxNoTranslateXa";

    /// <summary>
    /// Which language a given run translates FROM, so a report can name it.
    ///
    /// It is never the target: that was the whole reason for holding these sentences in several
    /// languages. Asking a model to translate English into English is a job the mod never gives
    /// one, and it goes strangely — measured, a translation-specialised model answered in
    /// Portuguese and then in Russian, and a small one dropped markers it holds perfectly well
    /// when there is real work to do.
    /// </summary>
    public static Fixtures SourceFor(string targetLanguage, string? sourceCode = null) =>
        Fixtures.ByCode(sourceCode) ?? Fixtures.For(targetLanguage);

    /// <summary>
    /// The few figures worth printing under a run, and no others.
    ///
    /// Each answers a question somebody playing actually has: what a line usually costs them in
    /// waiting, how bad the worst one got — that is the one they notice — how often the model had
    /// to be asked again, which is time and graphics card taken from the game itself, and how many
    /// lines it gave up on, which is text staying in a language they do not read.
    ///
    /// Deliberately absent: how long the suite took. Nobody plays a suite. A total that grows with
    /// the number of tests measures our list, not their game, and it would invite comparing two
    /// runs that counted different things.
    /// </summary>
    public static string Summarise(IReadOnlyList<ModelTestResult> results)
    {
        var lines = results.Where(r => r.Test.UnlocksOption is null && r.Elapsed > TimeSpan.Zero)
                           .ToList();

        if (lines.Count == 0) return "";

        // The first line of a session pays for loading the model, once, and it dwarfs everything
        // after it. Averaging it in would describe a wait nobody has twice.
        var afterLoading = lines.Count > 1 ? lines.Skip(1).ToList() : lines;

        var ordered = afterLoading.Select(r => r.Elapsed.TotalSeconds).OrderBy(seconds => seconds)
                                  .ToList();

        var typical = ordered[ordered.Count / 2];
        var worst = ordered[^1];

        var corrected = lines.Count(r => r.Attempts > 1);
        var refused = lines.Count(r => !r.Accepted);

        var summary = new System.Text.StringBuilder();

        summary.Append($"A line usually takes {typical:F1}s and the slowest took {worst:F1}s — "
                     + "that is the wait before the original text is replaced on screen. ");

        summary.Append(corrected == 0
            ? $"Nothing had to be asked twice — the mod allows up to "
              + $"{ModPlaceholderRules.MaxAttempts} tries per line before giving up on it."
            : $"{corrected} line(s) of {lines.Count} had to be asked again, out of the "
              + $"{ModPlaceholderRules.MaxAttempts} tries the mod allows before leaving a line "
              + "alone. Each extra try spends that time and that graphics card over again, while "
              + "the game is running.");

        if (refused > 0)
        {
            summary.Append($" {refused} was refused even then, and would stay in its original "
                         + "language until a later session.");
        }

        return summary.ToString();
    }

    /// <param name="gameContext">
    /// What the mod's own "describe this game" setting holds, or null for the default.
    ///
    /// Worth being able to vary: it is the one line of the prompt a player writes themselves, and
    /// nobody could say whether filling it in helps or hurts. Running the suite twice answers that
    /// for a given model instead of leaving the field to folklore.
    /// </param>
    /// <param name="sourceCode">
    /// The language to translate FROM. Left null, it is chosen so it is never the target — which
    /// is the whole reason these sets exist. Someone whose games are written in Chinese can name
    /// Chinese here, because that is their real case and only they know it.
    /// </param>
    public static IReadOnlyList<ModelTest> Build(string targetLanguage, string? gameContext = null,
                                                 string? sourceCode = null)
    {
        var language = Languages.NameOf(targetLanguage);

        var from = Fixtures.ByCode(sourceCode) ?? Fixtures.For(targetLanguage);
        var foreign = Fixtures.ForeignTo(from, targetLanguage);

        // Built from the source of each case, exactly as the mod builds it — see Prompt. Writing
        // the rules by hand per test is what made this suite ask for things the mod never asks.
        string Rules(string source) => Prompt(language, source, gameContext);

        var tests = new List<ModelTest>
        {
            new("plain line", "easy",
                from.PlainLine,
                Rules(from.PlainLine),
                (_, answer) => answer.Trim().Length > 0 && CountLines(answer) == 1)
            {
                Expectation = "translates, one line, nothing else",
            },

            new("no invented punctuation", "easy",
                from.NoPunctuation,
                Rules(from.NoPunctuation),
                // Tested against what THIS language ends sentences with. A Japanese model that
                // adds 。 has done the very thing the check exists for, and looking only for a
                // full stop would have let it through.
                (_, answer) => !from.SentenceEnders.Any(
                    end => answer.TrimEnd().EndsWith(end, StringComparison.Ordinal)))
            {
                Expectation = "no sentence mark added where the source has none",
            },

            new("single word", "medium",
                from.SingleWord,
                Rules(from.SingleWord),
                (_, answer) => CountLines(answer) == 1 && answer.Trim().Split(' ').Length <= 4)
            {
                Expectation = "answers with a word, not a sentence about the word",
            },

            new("technical terms kept", "medium",
                from.TechnicalTerms,
                Rules(from.TechnicalTerms),
                (_, answer) => answer.Contains("API", StringComparison.Ordinal)
                               && answer.Contains("JSON", StringComparison.Ordinal))
            {
                Expectation = "API and JSON come back untranslated",
            },

            new("keyboard shortcut kept", "medium",
                from.Shortcut,
                Rules(from.Shortcut),
                (_, answer) => answer.Contains("Ctrl", StringComparison.Ordinal)
                               && answer.Contains("F10", StringComparison.Ordinal))
            {
                Expectation = "Ctrl and F10 survive untouched",
            },

            new("one placeholder", "hard",
                from.OnePlaceholder,
                Rules(from.OnePlaceholder),
                (_, answer) => HasExactlyOnce(answer, "[!v*0]"))
            {
                Expectation = "[!v*0] comes back exactly once",
            },

            new("two placeholders in order", "hard",
                from.TwoPlaceholders,
                Rules(from.TwoPlaceholders),
                (_, answer) => InOrder(answer, "[!v*0]", "[!nl]"))
            {
                Expectation = "[!v*0] then [!nl], both once, in that order",
            },

            // A name the game drops into a sentence. Its own rule in the mod, and never tested
            // until now: a model that translates the marker leaves the player reading a token.
            new("a name kept out of it", "hard",
                from.NameInjected,
                Rules(from.NameInjected),
                (_, answer) => HasExactlyOnce(answer, "[!STR*0]"))
            {
                Expectation = "[!STR*0] comes back untouched, not translated",
            },

            // A number wrapped in its own colour tag — the shape behind every "3 / 10" counter in
            // a coloured HUD. Nothing to translate at all, which is what makes it hard: models
            // answer such prompts with a sentence about the prompt.
            new("nothing but markers", "hard",
                Fixtures.MarkersOnly,
                Rules(Fixtures.MarkersOnly),
                (_, answer) => InOrder(answer.Trim(), "[!t*0]", "[!v*0]", "[!t*1]")
                               && answer.Trim().Length <= 24)
            {
                Expectation = "gives the markers back and adds nothing to them",
            },

            // A blank line inside a text. The mod repairs a trailing one; this one it cannot,
            // and a paragraph that loses its break arrives as a wall.
            new("a blank line survives", "hard",
                from.BlankLine,
                Rules(from.BlankLine),
                (_, answer) => Occurrences(answer, "[!nl]") == 2)
            {
                Expectation = "two consecutive [!nl] come back as two, not one",
            },

            new("placeholder at the very end", "hard",
                from.TrailingBreak,
                Rules(from.TrailingBreak),
                (_, answer) => HasExactlyOnce(answer, "[!nl]"))
            {
                // The one failure the mod repairs by itself: it puts back trailing line breaks a
                // model trimmed, rather than throwing the answer away. Kept as a test because a
                // model that needs the repair is a model that will need it constantly.
                Expectation = "a trailing [!nl] is not swallowed (the mod can repair this one)",
            },

            // The three below exist because the ones above were too kind, and a real translation
            // file said so: in one measured game the median line is 73 characters and the ninth
            // decile 220, two lines in five carry markup, and a single line can hold eight
            // markers. Passing a ten-word label proves nothing about that.

            new("markup markers kept", "hard",
                from.MarkupSpan,
                Rules(from.MarkupSpan),
                (_, answer) => InOrder(answer, "[!t*0]", "[!t*1]", "[!v*0]"))
            {
                // The mod lifts every <color>, <b> and <sprite> out of the text before sending it
                // and puts them back afterwards, so what a model actually meets is a pair of
                // markers wrapped around the words they used to colour. Swap them and the colour
                // starts and ends in the wrong place; drop one and the rest of the line takes it.
                Expectation = "the tag markers keep their place around the words they wrap",
            },

            new("a paragraph, not a label", "stress",
                from.Paragraph,
                Rules(from.Paragraph),
                (_, answer) => InOrder(answer,
                    "[!t*0]", "[!t*1]", "[!nl]", "[!v*0]", "[!v*1]", "[!v*2]"))
            {
                // Distance is the whole point. A marker is rarely lost on a short line; it is lost
                // in the middle of a long one, where the model has stopped tracking it — and long
                // lines are most of a game's dialogue.
                //
                // Checked on structure alone, never on length: a translation can legitimately be
                // half the characters of its source or twice them depending on the language, and
                // a size rule here would fail entire writing systems rather than bad models.
                Expectation = "all six markers survive, once each, in order, across a long text",
            },

            new("markers in a row", "stress",
                from.MarkersInRow,
                Rules(from.MarkersInRow),
                (_, answer) => InOrder(answer,
                    "[!t*0]", "[!t*1]", "[!v*0]", "[!v*1]", "[!v*2]", "[!v*3]", "[!v*4]", "[!v*5]"))
            {
                // Taken from the shape of a real line: a counter listing six values separated by
                // slashes. Models renumber these, or helpfully collapse them into a range.
                Expectation = "six numbered markers come back in their original order, none merged",
            },

            new("refuses another real language", "experimental",
                foreign.PlainLine,
                Strict(from, language) + Rules(foreign.PlainLine),
                (_, answer) => answer.Trim() == SkipMarker)
            {
                // The sentence is chosen so it is neither the source nor the target. A fixed one
                // is eventually somebody's own language: this case used to hand a French sentence
                // to every French player's model and ask it to refuse French while translating
                // into French, and had done so quietly since the day it was written.
                Expectation = $"answers exactly {SkipMarker} and nothing else",
                UnlocksOption = "strict_source",

                Caveat = "It stays experimental whatever this test says: one sentence is not a "
                       + "game, and when it goes wrong it goes wrong silently — text that was "
                       + "perfectly fine is dropped and nothing tells you. Switch it on knowing "
                       + "that, and keep an eye on what disappears.",
            },

            // Last of all, and the only case that can never collide with anybody: a constructed
            // language is neither a source nor a target here.
            //
            // Not a joke. Games carry invented languages, proper nouns and plain gibberish, and a
            // model that confidently "translates" those is the one that will quietly rewrite them
            // in a real game. Refusing what it cannot know is the behaviour being measured.
            new("refuses what it cannot know", "experimental",
                Fixtures.Klingon,
                Strict(from, language) + Rules(Fixtures.Klingon),
                (_, answer) => answer.Trim() == SkipMarker)
            {
                Expectation = $"answers exactly {SkipMarker}, rather than inventing a translation",
                UnlocksOption = "strict_source",

                Caveat = "A model that translates this anyway is telling you what it does with a "
                       + "game's invented words and proper nouns: it rewrites them.",
            },
        };

        // Removed rather than failed. Asking a script with no capitals to answer in capitals is
        // asking for something that does not exist, and a KO there would say nothing about the
        // model — only about the language it was handed.
        if (from.ShoutedLabel is { } shouted)
        {
            tests.Insert(3, new ModelTest("shouted label stays shouted", "medium",
                shouted,
                Rules(shouted),
                (_, answer) =>
                {
                    var letters = answer.Trim().Where(char.IsLetter).ToList();
                    return letters.Count > 0 && letters.All(char.IsUpper);
                })
            {
                // Case is not decoration in a menu: a row of shouted labels with one sentence-cased
                // entry among them looks broken, and the mod cannot put the case back.
                Expectation = "answers in capitals, as the source is",
            });
        }

        return tests;
    }

    /// <summary>
    /// The block the mod puts ABOVE everything else when strict_source is on. It does not replace
    /// the usual rules: the model still has to translate normally when the language does match.
    /// </summary>
    private static string Strict(Fixtures from, string targetLanguage) => $"""
        === CRITICAL RULE ===
        Source language: {from.Language}
        - If text is NOT in {from.Language}: reply ONLY with exactly: {SkipMarker}
        - If text IS in {from.Language}: translate to {targetLanguage}

        """;

    /// <summary>
    /// The system turn the mod would build for THIS text, rule for rule.
    ///
    /// The rules about markers are conditional in the mod, and that is not tidiness: its own code
    /// says announcing a placeholder the text does not contain invites the model to invent one,
    /// and small models were measured answering with a marker they had merely been told about.
    /// A suite that always sent the full block was therefore testing a prompt the mod never
    /// sends, and testing it in the direction that flatters.
    ///
    /// Mirrors TranslatorCore's game path. It is a copy, and copies drift — but the alternative
    /// is a tester that measures something else, which is worse than one that needs checking
    /// against the mod when the mod's prompt changes.
    /// </summary>
    private static string Prompt(string language, string source, string? gameContext = null,
                                 string? sourceLanguage = null)
    {
        var context = string.IsNullOrWhiteSpace(gameContext)
            ? "video game UI, menus and dialogues"
            : gameContext;

        var prompt = new System.Text.StringBuilder();

        prompt.AppendLine("=== CONTEXT ===");
        prompt.AppendLine(sourceLanguage is null
            ? $"Translating video game ({context}) to {language}."
            : $"Translating video game ({context}) from {sourceLanguage} to {language}.");
        prompt.AppendLine();

        prompt.AppendLine("=== TRANSLATION RULES ===");
        prompt.AppendLine("- Output the translation only, no explanation");
        prompt.AppendLine("- Translation must be correct in target language");
        prompt.AppendLine("- Keep it concise for UI");
        prompt.AppendLine("- Do not add punctuation if not in the source to translate");
        prompt.AppendLine("- Keep unchanged: keyboard keys (Tab, Esc, Space...), technical settings (VSync, Auto)");

        if (source.Contains("[!nl]", StringComparison.Ordinal))
            prompt.AppendLine("- IMPORTANT: Keep [!nl] placeholders exactly where they are, do not remove or move them");

        if (source.Contains("[!t*", StringComparison.Ordinal))
            prompt.AppendLine("- IMPORTANT: Keep [!t*0], [!t*1], etc. tag placeholders exactly as-is, do not modify or remove them");

        if (source.Contains("[!v*", StringComparison.Ordinal))
            prompt.AppendLine("- IMPORTANT: Keep [!v*0], [!v*1], etc. placeholders exactly as-is, do not modify them");

        if (source.Contains("[!STR*", StringComparison.Ordinal))
            prompt.AppendLine("- IMPORTANT: Keep [!STR*0], [!STR*1], etc. string variable placeholders exactly as-is, do not translate or modify them");

        // The mod says "word" rather than "text" for anything without a space, and that line
        // changes what a model answers — several return a definition when it is missing.
        if (IsSingleWord(source))
        {
            prompt.AppendLine();
            prompt.Append("Now, translate this word:");
        }

        return prompt.ToString();
    }

    /// <summary>
    /// The mod's own test, in its own terms: no line break, and no space. Scriptio continua is
    /// judged on length there; it is left out here rather than half-copied, because this suite
    /// must not carry rules written for particular writing systems.
    /// </summary>
    private static bool IsSingleWord(string text) =>
        !text.Contains('\n') && !text.Contains("[!nl]", StringComparison.Ordinal)
        && !text.Contains(' ');

    /// <summary>
    /// A line of the length games actually hold. In one measured translation file the median line
    /// is 73 characters and the ninth decile 220; the mod itself accepts up to 15000, so this is
    /// ordinary rather than extreme — the suite simply had nothing above forty.
    ///
    /// Every marker here is one the mod really produces: a tag pair around a coloured word, a line
    /// break, and numbers lifted out of the text so one cache entry can serve every value.
    /// </summary>
    private const string Paragraph =
        "[!t*0]Warning[!t*1][!nl]The reactor is running at [!v*0] percent of its rated output. "
        + "Vent the coolant before the next jump, or the crew will not survive it. Repairs cost "
        + "[!v*1] credits and take [!v*2] cycles, and nothing else can be built while they are "
        + "under way.";

    /// <summary>
    /// The system turn, in the shape the mod builds it: a context block naming the job and the
    /// target language, then the rules. No "now translate this" line — in the mod the text to
    /// translate arrives as the user turn and nothing announces it.
    /// </summary>
    private static string Rules(string language) => $"""
        === CONTEXT ===
        Translating video game (video game UI, menus and dialogues) to {language}.

        === TRANSLATION RULES ===
        - Output the translation only, no explanation
        - Translation must be correct in target language
        - Keep it concise for UI
        - Do not add punctuation if not in the source to translate
        - Keep unchanged: keyboard keys (Tab, Esc, Space...), technical settings (VSync, Auto)
        """;

    /// <summary>
    /// Whether the answer carries the instructions back. Recognised by shape, not by wording:
    /// the model translates the rules too, so matching the text we sent would never fire.
    /// A bullet line or a section header in the answer is the tell.
    /// </summary>
    public static bool LooksLikeEchoedInstructions(string answer)
    {
        var lines = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return false;

        return lines.Any(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("===", StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The translation inside an answer that repeated the instructions: the last line that is
    /// not a bullet or a header. A repair for reading, never for judging — the echo itself stays
    /// reported as a failure.
    /// </summary>
    public static string ExtractTranslation(string answer)
    {
        var lines = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(line => line.TrimEnd())
                          .Where(line =>
                          {
                              var trimmed = line.TrimStart();
                              return !trimmed.StartsWith("- ", StringComparison.Ordinal)
                                  && !trimmed.StartsWith("===", StringComparison.Ordinal);
                          })
                          .ToList();

        return lines.Count > 0 ? lines[^1].Trim() : answer.Trim();
    }

    private static int Occurrences(string text, string token)
    {
        var count = 0;
        var at = text.IndexOf(token, StringComparison.Ordinal);

        while (at >= 0)
        {
            count++;
            at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static int CountLines(string text) =>
        text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool HasExactlyOnce(string text, string token)
    {
        var first = text.IndexOf(token, StringComparison.Ordinal);
        return first >= 0 && text.IndexOf(token, first + 1, StringComparison.Ordinal) < 0;
    }

    /// <summary>
    /// Both tokens present once each, and in the order given. Order matters as much as presence:
    /// keeping both while swapping them puts the line break in the wrong place on screen.
    /// </summary>
    private static bool InOrder(string text, params string[] tokens)
    {
        var previous = -1;

        foreach (var token in tokens)
        {
            if (!HasExactlyOnce(text, token)) return false;

            var at = text.IndexOf(token, StringComparison.Ordinal);
            if (at < previous) return false;

            previous = at;
        }

        return true;
    }
}
