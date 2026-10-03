using UnityGameTranslator.Manager.Core.Settings;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// A key changed inside the game wins over a key decision taken here before it
/// (<see cref="GamePreference.SettleHotkey"/>) — and a key Mod defaults moved is still offered.
/// </summary>
internal static class HotkeySettleChecks
{
    internal static void WhenTheKeyMovesInTheGame()
    {
        Program.Section("A key changed inside the game, against the key decisions taken here");

        GamePreference Ticked() => new()
        {
            ReplaceHotkey = true,
            HotkeyAtLastWrite = "Shift+F10",
            Mod = new GameModOverrides { TargetLanguage = "fr" },
        };

        var untouched = Ticked();
        Program.Check(!untouched.SettleHotkey("Shift+F10", "Shift+F10") && untouched.ReplaceHotkey,
            "the game still holds the key last written: nothing moves",
            "the box would untick itself on every game it was ticked on");

        var defaultsMoved = Ticked();
        Program.Check(!defaultsMoved.SettleHotkey("Shift+F10", "F9") && defaultsMoved.ReplaceHotkey,
            "Mod defaults changed its key since: the box stays, so the new key is offered",
            "a game set up from Mod defaults would stop following them");

        var changedInGame = Ticked();
        var moved = changedInGame.SettleHotkey("Ctrl+F8", "Shift+F10");
        Program.Check(moved && !changedInGame.ReplaceHotkey && changedInGame.HotkeyAtLastWrite is null,
            "the key was changed in the game: the box is given back to the game",
            "the one-click offered to put the old key back over the one just chosen in the game");
        Program.Check(changedInGame.Mod is { TargetLanguage: "fr" },
            "and nothing else this game answered is touched", "only the key moved");

        var ownKey = new GamePreference
        {
            HotkeyAtLastWrite = "F7",
            Mod = new GameModOverrides { SettingsHotkey = "F7" },
        };
        Program.Check(ownKey.SettleHotkey("Ctrl+F8", "Shift+F10") && ownKey.Mod is null,
            "a key chosen here for this game goes the same way",
            "it would be written back over the game's newer key at the next install");

        var catchUp = Ticked();
        catchUp.HotkeyAtLastWrite = "F2";
        Program.Check(catchUp.SettleHotkey("Shift+F10", "Shift+F10")
                      && catchUp.ReplaceHotkey && catchUp.HotkeyAtLastWrite == "Shift+F10",
            "the game holds the key the decision calls for: the record catches up",
            "a write by a path that does not record (the command line) read as a change made in the game");

        var unread = Ticked();
        Program.Check(!unread.SettleHotkey(null, "Shift+F10") && unread.ReplaceHotkey,
            "a key that cannot be read decides nothing", "not reading it is not it having changed");

        var neverWritten = new GamePreference { ReplaceHotkey = true };
        Program.Check(!neverWritten.SettleHotkey("Ctrl+F8", "Shift+F10") && neverWritten.ReplaceHotkey,
            "with no record of a write, a ticked box is left alone",
            "somebody ticked it against the key the game holds — that is the question it answers");
    }
}
