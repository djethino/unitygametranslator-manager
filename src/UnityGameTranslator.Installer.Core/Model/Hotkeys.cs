namespace UnityGameTranslator.Installer.Core.Model;

/// <summary>
/// Which hotkey strings the mod can actually act on.
///
/// ⚠ This mirrors TranslatorUIManager.IsHotkeyPressed, which strips the modifiers and then does
/// Enum.TryParse&lt;KeyCode&gt; on what is left. A name that does not parse makes that method
/// **return false forever, without a word** — the panel simply never opens.
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
public static class Hotkeys
{
    /// <summary>The default the mod itself falls back to.</summary>
    public const string Default = "Ctrl+F10";

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

    /// <summary>The key left once the modifiers are stripped — exactly as the mod strips them.</summary>
    public static string BaseKeyOf(string hotkey) =>
        hotkey.Replace("Ctrl+", "", StringComparison.OrdinalIgnoreCase)
              .Replace("Alt+", "", StringComparison.OrdinalIgnoreCase)
              .Replace("Shift+", "", StringComparison.OrdinalIgnoreCase)
              .Trim();

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
}
