using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Api;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// The model bench runs the game's own line through the game's own loop, and judges what comes
/// back as the game would.
///
/// 🔴 **Why this exists.** The bench is only worth something if it tests what a game does. It had
/// drifted three ways without a single check noticing (2026-09-26): its own prompt builder, which
/// never announced a pair of tags or a bracketed label; fixtures already in token form, so the
/// socle never knew which tags paired; and verdicts counted on the RESTORED answer, where every
/// [!nl] is a line break again — so each case built on line breaks failed a perfect answer.
///
/// ⚠ No model is asked. The one used here gives back exactly what it was sent — a perfect answer
/// by construction — so every structural case must pass on it, in every source language. A case
/// that fails here fails the bench, not a model.
/// </summary>
internal static class BenchChecks
{
    private static string? Echo(IReadOnlyList<ChatMessage> messages, double temperature, int maxTokens, int? seed) =>
        messages[messages.Count - 1].Content;

    public static void APerfectAnswerPassesEveryCase()
    {
        Program.Section("The model bench, on a perfect answer");

        foreach (var fixtures in Fixtures.All)
        {
            // A target other than the source, so each set is asked as a real run would ask it.
            var target = fixtures.Code == "en" ? "fr" : "en";

            foreach (var test in ModelTestSuite.Build(target, sourceCode: fixtures.Code))
            {
                if (test.ForReading || test.ExpectsRefusal) continue;

                var answer = LineTranslation.AskModel(test.Source,
                    new ModelJob { Instructions = test.Instructions, Attempts = Placeholders.MaxAttempts }, Echo);

                Program.Check(answer.Outcome == LineOutcome.Translated && ModelTestSuite.Judge(test, answer.Text!) == true,
                    $"{fixtures.Code}: {test.Name}",
                    "sent through the game's loop and given back untouched, it passes");
            }
        }

        var markup = ModelTestSuite.Build("fr", sourceCode: "en").First(t => t.Name == "markup markers kept");
        Program.Check(markup.Rule.Contains("They come in pairs around words", StringComparison.Ordinal),
            "the bench's prompt is the game's, pairs of tags included",
            "its own builder never announced what the game had started to");
    }
}
