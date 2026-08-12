using UnityGameTranslator.Common;
namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>
/// Which KEYS this tool is willing to write into somebody's game.
///
/// ⚠ How a shortcut is SPELLED is no longer decided here: that is
/// <see cref="UnityGameTranslator.Common.Hotkeys"/>, shared with the mod's input loop and its
/// capture widget. This class answers a different question — which KEYS this tool is willing to
/// write into somebody's game — and the two were tangled together long enough to disagree about
/// case: "ctrl+F10" passed as valid here and never fired over there.
///
/// The mod's own answer on keys is Enum.TryParse&lt;KeyCode&gt;, i.e. Unity's enum, which is wider
/// than the list below. A name that does not parse makes its input loop **return false forever,
/// without a word** — the panel simply never opens.
///
/// That silence is why this class exists. Writing an unparseable hotkey into a game is worse than
/// writing none: combined with skipping the first-run wizard, it leaves someone with a mod they
/// cannot open and no screen on which to fix it. Measured for real on 2026-08-09, where a settings
/// field accepted "²" and would have shipped it into a game.
///
/// The names below are UnityEngine.KeyCode members. Unity is not referenced here — this tool
/// targets .NET 8 and has no business loading UnityEngine — so the list is carried explicitly,
/// restricted to keys a person would sensibly bind. Anything outside it is refused rather than
/// guessed at.
/// </summary>
public static class BindableKeys
{
    /// <summary>The default the mod itself falls back to. Stated once, in the shared library.</summary>
    public const string Default = Common.Hotkeys.Default;

    private static readonly HashSet<string> KeyNames = BuildKeyNames();

    private static HashSet<string> BuildKeyNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Named keys, spelled the way Unity spells them. The spelling is the whole point:
            // "Enter" and "Up" are what people say, and neither exists in KeyCode.
            "Space", "Tab", "Escape", "Return", "Backspace", "Delete", "Insert",
            "Home", "End", "PageUp", "PageDown",
            "UpArrow", "DownArrow", "LeftArrow", "RightArrow",
            "BackQuote", "Minus", "Equals", "LeftBracket", "RightBracket",
            "Semicolon", "Quote", "Backslash", "Comma", "Period", "Slash",
            "Print", "ScrollLock", "Pause", "Menu",
        };

        // F1 to F15: Unity stops there, and so do we rather than accepting an F16 that would
        // parse nowhere.
        for (var i = 1; i <= 15; i++) names.Add($"F{i}");

        for (var c = 'A'; c <= 'Z'; c++) names.Add(c.ToString());

        // Digits are Alpha0..Alpha9 in Unity, not "0".."9" — a hotkey saved as "5" never fires.
        for (var i = 0; i <= 9; i++)
        {
            names.Add($"Alpha{i}");
            names.Add($"Keypad{i}");
        }

        return names;
    }

    /// <summary>Every base key that can be bound, for a picker or an error message.</summary>
    public static IReadOnlyCollection<string> AvailableKeys => KeyNames;

    /// <summary>
    /// The keys this tool is willing to WRITE into somebody's game — a strictly smaller question
    /// than "what can the mod parse", and the two were the same list until 2026-08-12.
    ///
    /// ⚠ **A key that prints a character is not portable between games.** What a Unity `KeyCode`
    /// designates depends on a per-PROJECT setting the studio chose — Input Manager → "Use Physical
    /// Keys" — which is on by default since Unity 2022.1 but stays off in every project migrated
    /// from before. With it on, `KeyCode` is a position on a US keyboard; with it off, it is
    /// whatever the key produces under the current layout. **No runtime API reports which régime a
    /// game is in**, deducing it from the Unity version is wrong (migrated projects), and inferring
    /// it by comparing code and character breaks under Proton, where Wine can resolve virtual keys
    /// against one layout while text follows another.
    ///
    /// Measured on the 13 test games: the same physical key was written `Quote` in six of them and
    /// `BackQuote` in five — two of those on the SAME Unity build. See
    /// analyse/hotkey-keycode-divergence.md.
    ///
    /// So this tool stopped trying to transpose. The keys below carry NO character, therefore no
    /// layout can move them and both régimes agree on them — on AZERTY, QWERTZ, Dvorak, Cyrillic,
    /// JIS or Hangul alike. That is not a convention that a future Unity could change: there is
    /// simply nothing to translate.
    ///
    /// Anything else stays perfectly legitimate **in the game**, where the mod captures it against
    /// the real keyboard and is right by construction. It is only unfit to travel from here.
    /// </summary>
    private static readonly HashSet<string> UniversalKeyNames = BuildUniversalKeyNames();

    private static HashSet<string> BuildUniversalKeyNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // No character, no layout, no argument.
            "Escape", "Tab", "Space", "Return", "Backspace",
            "Delete", "Insert", "Home", "End", "PageUp", "PageDown",
            "UpArrow", "DownArrow", "LeftArrow", "RightArrow",
            "Print", "ScrollLock", "Pause",
        };

        for (var i = 1; i <= 15; i++) names.Add($"F{i}");

        // ⚠ The keypad, NOT the number row. Alpha0..Alpha9 sit on keys that print something and
        // are re-lettered by several layouts; Keypad0..Keypad9 have their own scan codes.
        for (var i = 0; i <= 9; i++) names.Add($"Keypad{i}");

        return names;
    }

    /// <summary>Keys this tool may write into a game, for a picker or an error message.</summary>
    public static IReadOnlyCollection<string> UniversalKeys => UniversalKeyNames;

    /// <summary>
    /// Whether this shortcut means the same thing in every game, on every keyboard.
    ///
    /// Empty counts as universal: it means "no hotkey", which travels fine.
    /// </summary>
    public static bool IsUniversal(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return true;

        return UniversalKeyNames.Contains(BaseKeyOf(hotkey));
    }

    /// <summary>
    /// Whether the mod would act on this string.
    ///
    /// Empty is valid on purpose: the mod treats an empty hotkey as "disabled", and that is a
    /// legitimate choice for the extra toggles. It is only the main settings hotkey that must not
    /// be empty, and that is enforced where it matters rather than here.
    /// </summary>
    public static bool IsValid(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return true;

        return KeyNames.Contains(BaseKeyOf(hotkey));
    }

    /// <summary>The key left once the modifiers are stripped — by the same code the mod uses.</summary>
    public static string BaseKeyOf(string hotkey) => Common.Hotkeys.BaseKeyOf(hotkey);

    /// <summary>
    /// Physical key position → the name Unity gives it **when the game reads positions**.
    ///
    /// 🔴 **This table is only true half the time, and that was found out the hard way.** It used
    /// to claim that "neither Unity nor this table cares which character the key prints", on the
    /// strength of one game checked on 2026-08-09 where the mod had indeed written `BackQuote` for
    /// the key left of "1". That game had Input Manager → "Use Physical Keys" ON. A game with it
    /// OFF — every project migrated from before Unity 2022.1 — writes `Quote` for the very same
    /// key on a French AZERTY, because Windows reports it as VK_OEM_7 there.
    ///
    /// One measurement, generalised into a rule, is what made writing hotkeys into games feel safe.
    /// Six of the thirteen test games disagree with it. See analyse/hotkey-keycode-divergence.md.
    ///
    /// ⚠ So this mapping is NOT a way to transpose a key across games. It is kept for the one thing
    /// it does honestly: turning what Avalonia reports under our own capture into a Unity name.
    /// Whether that name means the same thing inside a given game is decided by
    /// <see cref="IsUniversal"/> — and for anything that prints a character, the answer is no.
    ///
    /// Only keys Unity actually has are listed. IntlBackslash (the &lt;&gt; key on European
    /// keyboards) has no KeyCode at all, so it is absent and gets refused rather than approximated
    /// into something that would bind the wrong key.
    /// </summary>
    private static readonly Dictionary<string, string> PhysicalToUnity = BuildPhysicalMap();

    private static Dictionary<string, string> BuildPhysicalMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Backquote"] = "BackQuote",
            ["Minus"] = "Minus",
            ["Equal"] = "Equals",
            ["BracketLeft"] = "LeftBracket",
            ["BracketRight"] = "RightBracket",
            ["Backslash"] = "Backslash",
            ["Semicolon"] = "Semicolon",
            ["Quote"] = "Quote",
            ["Comma"] = "Comma",
            ["Period"] = "Period",
            ["Slash"] = "Slash",
            ["Space"] = "Space",
            ["Tab"] = "Tab",
            ["Escape"] = "Escape",
            ["Enter"] = "Return",
            ["Backspace"] = "Backspace",
            ["Delete"] = "Delete",
            ["Insert"] = "Insert",
            ["Home"] = "Home",
            ["End"] = "End",
            ["PageUp"] = "PageUp",
            ["PageDown"] = "PageDown",
            ["ArrowUp"] = "UpArrow",
            ["ArrowDown"] = "DownArrow",
            ["ArrowLeft"] = "LeftArrow",
            ["ArrowRight"] = "RightArrow",
            ["PrintScreen"] = "Print",
            ["ScrollLock"] = "ScrollLock",
            ["Pause"] = "Pause",
            ["ContextMenu"] = "Menu",
        };

        for (var i = 1; i <= 15; i++) map[$"F{i}"] = $"F{i}";
        for (var c = 'A'; c <= 'Z'; c++) map[c.ToString()] = c.ToString();

        // Number row and keypad are named differently on each side, and this is where a
        // hand-written hotkey most often goes wrong: Unity calls the "1" key Alpha1, never "1".
        for (var i = 0; i <= 9; i++)
        {
            map[$"Digit{i}"] = $"Alpha{i}";
            map[$"NumPad{i}"] = $"Keypad{i}";
            map[$"Numpad{i}"] = $"Keypad{i}";
        }

        return map;
    }

    /// <summary>
    /// The Unity name for a physical key position, or null when Unity has no equivalent.
    ///
    /// Null must be reported, never silently swapped for something else: someone who binds a key
    /// and finds a different one saved has been overruled without being told, and will conclude
    /// the mod is broken when it does not respond to the key they actually chose.
    /// </summary>
    public static string? FromPhysicalKey(string physicalKeyName) =>
        PhysicalToUnity.TryGetValue(physicalKeyName, out var unity) ? unity : null;

    /// <summary>
    /// The full hotkey string, modifiers first, in the order the mod writes and parses them.
    /// Null when the key itself has no Unity equivalent.
    /// </summary>
    public static string? Compose(string physicalKeyName, bool ctrl, bool alt, bool shift)
    {
        var key = FromPhysicalKey(physicalKeyName);
        if (key is null) return null;

        return Common.Hotkeys.Compose(key, ctrl, alt, shift);
    }

    /// <summary>
    /// Why a hotkey was refused, in words that say what to do about it. Null when it is fine.
    /// </summary>
    public static string? Explain(string? hotkey)
    {
        if (IsValid(hotkey)) return null;

        var key = BaseKeyOf(hotkey!);

        return $"The mod cannot use \"{key}\" — it would silently never open. "
             + "Function keys (F1 to F15), letters, and named keys like Space, Tab or Escape work; "
             + "digits are written Alpha1 to Alpha9. Modifiers go in front: Ctrl+, Alt+, Shift+.";
    }

    /// <summary>
    /// Why a key is fine in a game but not fit to be sent from here. Null when it travels.
    ///
    /// Worded as a limit of THIS tool, because that is what it is: the key works perfectly well
    /// where it was captured. Saying "unsupported" would be false and would send somebody looking
    /// for a fix that does not exist.
    /// </summary>
    public static string? ExplainNotUniversal(string? hotkey)
    {
        if (IsUniversal(hotkey)) return null;

        var key = BaseKeyOf(hotkey!);

        return $"\"{key}\" prints a character, so what it means changes from one game to the next — "
             + "the same physical key is read differently depending on a setting each studio chose. "
             + "This tool only sends keys that mean the same everywhere: F1 to F15, the keypad, "
             + "Insert/Delete/Home/End/Page keys, arrows, Escape, Tab, Space, Enter. "
             + "To use this key, set it in the game itself — there the mod reads your actual "
             + "keyboard, so it is right whatever your layout or language.";
    }
}
