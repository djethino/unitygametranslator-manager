using UnityGameTranslator.Installer.Core.Model;

namespace UnityGameTranslator.Installer.Core.Api;

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

    public static IReadOnlyList<ModelTest> Build(string targetLanguage)
    {
        var language = Languages.NameOf(targetLanguage);

        return new List<ModelTest>
        {
            new("plain line", "easy",
                "Start Game",
                Rules(language),
                (_, answer) => answer.Trim().Length > 0 && CountLines(answer) == 1)
            {
                Expectation = "translates, one line, nothing else",
            },

            new("no invented punctuation", "easy",
                "Loading",
                Rules(language) + "\n- Do not add punctuation if not in the source to translate",
                (_, answer) => !answer.TrimEnd().EndsWith('.') && !answer.TrimEnd().EndsWith('!'))
            {
                Expectation = "no full stop added where the source has none",
            },

            new("single word", "medium",
                "Save",
                Rules(language),
                (_, answer) => CountLines(answer) == 1 && answer.Trim().Split(' ').Length <= 4)
            {
                Expectation = "answers with a word, not a sentence about the word",
            },

            new("technical terms kept", "medium",
                "Your API key is stored in JSON",
                Rules(language) + "\n- Keep technical terms unchanged: API, URL, UUID, JSON, AI",
                (_, answer) => answer.Contains("API", StringComparison.Ordinal)
                               && answer.Contains("JSON", StringComparison.Ordinal))
            {
                Expectation = "API and JSON come back untranslated",
            },

            new("keyboard shortcut kept", "medium",
                "Press Ctrl+F10 to open settings",
                Rules(language) + "\n- Keep keyboard shortcuts as-is: Ctrl, Alt, Shift, F1-F12, Tab, Esc",
                (_, answer) => answer.Contains("Ctrl", StringComparison.Ordinal)
                               && answer.Contains("F10", StringComparison.Ordinal))
            {
                Expectation = "Ctrl and F10 survive untouched",
            },

            new("one placeholder", "hard",
                "Press [!v*0] to continue",
                Rules(language) + PlaceholderRules,
                (_, answer) => HasExactlyOnce(answer, "[!v*0]"))
            {
                Expectation = "[!v*0] comes back exactly once",
            },

            new("two placeholders in order", "hard",
                "Press [!v*0] to save[!nl]Your API key is required",
                Rules(language) + PlaceholderRules,
                (_, answer) => InOrder(answer, "[!v*0]", "[!nl]"))
            {
                Expectation = "[!v*0] then [!nl], both once, in that order",
            },

            new("placeholder at the very end", "hard",
                "Settings saved[!nl]",
                Rules(language) + PlaceholderRules,
                (_, answer) => HasExactlyOnce(answer, "[!nl]"))
            {
                Expectation = "a trailing [!nl] is not swallowed",
            },

            new("refuses to translate the wrong language", "hardest",
                "Bonjour tout le monde",
                $"""
                === CRITICAL RULE ===
                Source language: English
                - If text is NOT in English: reply ONLY with exactly: {SkipMarker}
                - If text IS in English: translate to {language}
                - Output the translation only, no explanation
                """,
                (_, answer) => answer.Trim() == SkipMarker)
            {
                Expectation = $"answers exactly {SkipMarker} and nothing else",
            },
        };
    }

    private const string PlaceholderRules = """

        - IMPORTANT: Keep [!nl] placeholders exactly where they are, do not remove or move them
        - IMPORTANT: Keep [!v*0], [!v*1], etc. placeholders exactly as-is, do not modify them
        """;

    private static string Rules(string language) => $"""
        === TRANSLATION RULES ===
        - Output the translation only, no explanation
        - Translation must be understandable and correct in target language
        - Keep it concise for UI

        Now, translate this to {language}:
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
    private static bool InOrder(string text, string first, string second)
    {
        if (!HasExactlyOnce(text, first) || !HasExactlyOnce(text, second)) return false;
        return text.IndexOf(first, StringComparison.Ordinal)
             < text.IndexOf(second, StringComparison.Ordinal);
    }
}
