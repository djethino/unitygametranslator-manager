using System.Text;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Manager.Core.Api;

/// <summary>
/// The mod's own acceptance rules, copied so the tester can fail and retry exactly as a game will.
///
/// ⚠ THIS IS A COPY of TranslatorCore, and copies drift. It is here because the alternative is
/// worse: the tester used to judge an answer by its own checks and stop at one call, while a game
/// judges it by these rules and calls up to three times. Every figure we showed — pass, fail, and
/// above all the time — described something that never happens in play.
///
/// What a player waits for is not one request. It is this whole chain: a first answer, a check, a
/// corrective exchange, a reinforced retry, and then either an accepted line or a line left in its
/// original language. That is the number this exists to measure.
///
/// If the mod's prompt or validation changes, this file has to be re-read against it. Better a
/// copy that is checked than a measurement of the wrong thing.
/// </summary>
public static class ModPlaceholderRules
{
    /// <summary>
    /// How many times a game will ask before giving up on a line — the mod's own ceiling.
    ///
    /// Named here because two places need it and they must not drift: the loop that reproduces the
    /// chain, and the report that shows "2 of 3 requests". Without the ceiling, a line that took
    /// three requests looks like any other number rather than what it is — a line that came within
    /// one attempt of being left in its original language.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>Every frozen token the mod sends: [!v*N], [!t*N], [!STR*N], [!nl].</summary>
    private static readonly Regex TokenPattern = new(
        @"\[!(?:v\*\d+|t\*\d+|STR\*\d+|nl)\]", RegexOptions.Compiled);

    /// <summary>
    /// Each token plus the game's own delimiters immediately around it, e.g. "({[!v*0]})".
    ///
    /// Expanding left over opening characters only and right over closing ones cannot swallow a
    /// neighbour, since every token itself starts with '[' and ends with ']'. These sequences have
    /// to come back verbatim — a translation that keeps the token but loses the bracket the game
    /// wrapped it in still breaks the line.
    /// </summary>
    public static List<string> FrozenSequences(string source)
    {
        var sequences = new List<string>();
        if (string.IsNullOrEmpty(source)) return sequences;

        foreach (Match match in TokenPattern.Matches(source))
        {
            var start = match.Index;
            var end = match.Index + match.Length;

            while (start > 0 && (source[start - 1] == '{' || source[start - 1] == '(' || source[start - 1] == '['))
                start--;

            while (end < source.Length && (source[end] == '}' || source[end] == ')' || source[end] == ']'))
                end++;

            var sequence = source.Substring(start, end - start);
            if (!sequences.Contains(sequence)) sequences.Add(sequence);
        }

        return sequences;
    }

    /// <summary>
    /// Whether a game would accept this answer. Containment is not enough — "[{[!v*0]}]" contains
    /// "[!v*0]" and is still wrong — so this checks the frozen sequences verbatim, then that the
    /// tokens form the same multiset, then that no bracket was added or lost.
    /// </summary>
    public static bool Accepts(string source, string translation, List<string> frozen,
                               out List<string> errors)
    {
        errors = new List<string>();

        foreach (var sequence in frozen)
        {
            if (Occurrences(translation, sequence) < Occurrences(source, sequence))
                errors.Add($"the exact sequence \"{sequence}\" is missing or altered");
        }

        var inSource = Tally(source);
        var inAnswer = Tally(translation);

        foreach (var (token, expected) in inSource)
        {
            inAnswer.TryGetValue(token, out var found);
            if (found != expected)
                errors.Add($"token {token} appears {found} time(s) instead of {expected}");
        }

        foreach (var (token, _) in inAnswer)
        {
            if (!inSource.ContainsKey(token))
                errors.Add($"token {token} does not exist in the source");
        }

        foreach (var bracket in new[] { '{', '}', '[', ']' })
        {
            var expected = source.Count(c => c == bracket);
            var found = translation.Count(c => c == bracket);
            if (expected != found)
                errors.Add($"character '{bracket}' appears {found} time(s) instead of {expected}");
        }

        return errors.Count == 0;
    }

    /// <summary>
    /// The one repair a game makes for itself: line breaks trimmed from the very end, when that
    /// trailing deficit explains the whole difference. Anything else is still rejected.
    /// </summary>
    public static string? RepairTrailingBreaks(string source, string translation)
    {
        const string token = "[!nl]";
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translation)) return null;

        static int Trailing(string text)
        {
            var count = 0;
            var trimmed = text.TrimEnd();

            while (trimmed.EndsWith(token, StringComparison.Ordinal))
            {
                count++;
                trimmed = trimmed[..^token.Length].TrimEnd();
            }

            return count;
        }

        var deficit = Occurrences(source, token) - Occurrences(translation, token);
        if (deficit <= 0) return null;

        if (Trailing(source) - Trailing(translation) != deficit) return null;

        return new StringBuilder(translation.TrimEnd())
            .Insert(translation.TrimEnd().Length, token, deficit)
            .ToString();
    }

    /// <summary>
    /// The second attempt's user turn: what exactly was wrong, and what must reappear. Targeted
    /// feedback corrects far better than "try again", which is why the mod bothers.
    /// </summary>
    public static string Correction(List<string> errors, List<string> frozen)
    {
        var message = new StringBuilder();
        message.AppendLine("Your translation is INVALID:");

        foreach (var error in errors) message.AppendLine($"- {error}");

        if (frozen.Count > 0)
        {
            message.AppendLine("These exact character sequences from the source must appear unchanged in your translation:");
            message.AppendLine(string.Join(", ", frozen.Select(sequence => $"\"{sequence}\"")));
        }

        message.Append("Reply with ONLY the corrected translation, nothing else.");
        return message.ToString();
    }

    /// <summary>The third attempt's extra system section: a fresh start, with the sequences spelt out.</summary>
    public static string MandatorySequences(List<string> frozen)
    {
        var section = new StringBuilder();
        section.AppendLine("=== MANDATORY EXACT SEQUENCES ===");
        section.AppendLine("The text contains technical placeholders. Your output MUST contain these exact character sequences, copied character-for-character, unmodified:");

        foreach (var sequence in frozen) section.AppendLine($"\"{sequence}\"");

        return section.ToString();
    }

    private static Dictionary<string, int> Tally(string text)
    {
        var tally = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match match in TokenPattern.Matches(text))
        {
            tally.TryGetValue(match.Value, out var count);
            tally[match.Value] = count + 1;
        }

        return tally;
    }

    private static int Occurrences(string text, string token)
    {
        var count = 0;
        var at = 0;

        while ((at = text.IndexOf(token, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += token.Length;
        }

        return count;
    }
}
