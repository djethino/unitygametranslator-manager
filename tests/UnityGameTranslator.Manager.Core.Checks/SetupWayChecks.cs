using UnityGameTranslator.Manager.Core.Settings;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// "Set it up in the game" is a one-off: once the mod's Setup has been answered, the game keeps its
/// own settings and nothing is left to write (<see cref="GamePreference.SettleAfterSetup"/>).
/// </summary>
internal static class SetupWayChecks
{
    internal static void WhenTheSetupHasBeenAnswered()
    {
        Program.Section("\"Set it up in the game\", once the game has been set up in it");

        var open = new GameConfigSnapshot(true, FirstRunCompleted: false, null, new GameModOverrides());
        var closed = new GameConfigSnapshot(true, FirstRunCompleted: true, null, new GameModOverrides());

        GamePreference Asking() => new()
        {
            ApplyModDefaults = false,
            LetWizardAsk = true,
            Mod = new GameModOverrides { TargetLanguage = "fr" },
            StartTranslation = true,
            GameContext = "a ship game",
            ReplaceHotkey = true,
            InstalledTranslationId = 42,
            AdoptLoader = true,
        };

        var waiting = Asking();
        Program.Check(!waiting.SettleAfterSetup(open) && waiting.LetWizardAsk,
            "latch still open: the Setup has not run, nothing moves",
            "settling here would take back a choice before the game has asked anything");

        var answered = Asking();
        var moved = answered.SettleAfterSetup(closed);
        Program.Check(moved && !answered.LetWizardAsk && answered.ApplyModDefaults == false,
            "latch closed again: the game is on \"Set it up here\"",
            "kept, the answer asked for the latch open and the card offered the Setup again forever");

        Program.Check(answered.Mod is null && answered.StartTranslation is null
                      && answered.GameContext is null && !answered.ReplaceHotkey,
            "what the Setup answered is not contradicted by older answers",
            "an answer remembered from before would be offered back as a change to write over it");

        Program.Check(answered.InstalledTranslationId == 42 && answered.AdoptLoader,
            "what the config.json does not carry is kept",
            "the Setup says nothing about the loader or the installed translation");

        var other = new GamePreference { ApplyModDefaults = false, LetWizardAsk = false, GameContext = "kept" };
        Program.Check(!other.SettleAfterSetup(closed) && other.GameContext == "kept",
            "a game that never asked for the Setup is left alone",
            "a closed latch on its own means nothing — every game played through has one");
    }
}
