using UnityGameTranslator.Manager.Core.Settings;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// A settings draft is held as waiting only while it differs from what is decided for the game.
/// A control that fills late reported a change that changed nothing, and the card's Undo stayed —
/// and came back after Undo, at the next fill.
/// </summary>
internal static class SettingsDraftChecks
{
    internal static void WhenADraftIsWaiting()
    {
        Program.Section("When a settings draft is waiting");

        var decided = new GameModOverrides { SettingsHotkey = "Ctrl+F10" };
        Program.Check(decided.Copy().SameAs(decided), "a draft that changed nothing is the same",
            "a late fill must not leave an answer waiting");

        var changed = decided.Copy();
        changed.SettingsHotkey = "Ctrl+F11";
        Program.Check(!changed.SameAs(decided), "a changed field is a difference", "that one is waiting, and Undo is offered");

        Program.Check(new GameModOverrides().SameAs(null), "nothing answered is the same as nothing decided",
            "a game with no answers of its own has no preference object");
        Program.Check(!decided.SameAs(null), "an answer against nothing decided is a difference", "it is new");
    }
}
