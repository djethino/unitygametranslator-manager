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
                if (test.ForReading || test.ExpectsRefusal || test.CopyFails) continue;

                var answer = LineTranslation.AskModel(test.Source,
                    new ModelJob { Instructions = test.Instructions, Attempts = Placeholders.MaxAttempts }, Echo);

                Program.Check(answer.Outcome == LineOutcome.Translated && ModelTestSuite.Judge(test, answer.Text!) == true,
                    $"{fixtures.Code}: {test.Name}",
                    "sent through the game's loop and given back untouched, it passes");
            }
        }

        // The cases with tags INSIDE a sentence must also FAIL what a game would show wrongly —
        // a verdict a perfect answer passes is only half a verdict. French answers to the English
        // set, written as the game restores them.
        var suite = ModelTestSuite.Build("fr", sourceCode: "en");
        bool? Verdict(string name, string answer) => ModelTestSuite.Judge(suite.First(t => t.Name == name), answer);

        foreach (var (name, answer, why) in new[]
        {
            ("a coloured phrase in the middle of a sentence", "<color=#00FF00>Parlez au forgeron avant la tombée de la nuit</color>",
                "the whole sentence coloured"),
            ("a coloured phrase in the middle of a sentence", "Parlez au forgeron<color=#00FF00></color> avant la tombée de la nuit",
                "an empty colour"),
            ("two coloured phrases in one sentence", "Infligez <color=#FF4040>des dégâts doublés aux ennemis <color=#40A0FF>étourdis</color></color>",
                "one colour swallowed the other"),
            ("nested tags inside a sentence", "<b>Appuyez</b> sur <color=#FFCC00>E</color> pour ramasser l'objet",
                "the bold left the key for another word"),
            ("an icon beside its number", "Coûte [!v*0] par utilisation <sprite name=\"coin\">",
                "the icon drifted away from its number"),
            ("a number inside a coloured phrase", "Vous avez trouvé [!v*0] <color=#FFCC00>pièces d'or</color> dans le vieux coffre",
                "the number left the colour"),
            ("a line break inside a colour", "Rapportez la <color=#FFCC00>Couronne du Roi déchu</color>\nau temple",
                "the line break left the colour"),
        })
            Program.Check(Verdict(name, answer) == false, $"fails: {why}", "a game would show this wrongly");

        foreach (var (name, answer, why) in new[]
        {
            ("two coloured phrases in one sentence", "Infligez aux ennemis <color=#40A0FF>étourdis</color> <color=#FF4040>des dégâts doublés</color>",
                "the two colours in the other order"),
            ("nested tags inside a sentence", "Appuyez sur <color=#FFCC00><b>E</b></color> pour ramasser l'objet",
                "the colour outside the bold"),
            ("a line break inside a colour", "Rapportez au temple la <color=#FFCC00>Couronne du\nRoi déchu</color>",
                "the colour moved within the sentence"),
        })
            Program.Check(Verdict(name, answer) == true, $"passes: {why}", "it shows exactly as the source meant");

        var markup = ModelTestSuite.Build("fr", sourceCode: "en").First(t => t.Name == "markup markers kept");
        Program.Check(markup.Rule.Contains("They come in pairs", StringComparison.Ordinal),
            "the bench's prompt is the game's, pairs of tags included",
            "its own builder never announced what the game had started to");
    }
}
