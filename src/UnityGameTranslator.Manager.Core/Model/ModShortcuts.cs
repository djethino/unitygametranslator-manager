namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>One of the mod's optional shortcuts: its config.json key, and the mod's own words for it.</summary>
public sealed record ModShortcut(string Key, string Label, string Hint);

/// <summary>
/// The mod's optional shortcuts — everything under "Additional Hotkeys" in its Options, the panel
/// key aside (that one has its own setting everywhere: <see cref="InstallerSettings.SettingsHotkey"/>).
///
/// 🔴 **One list, read by every screen and by GameConfigWriter.** The keys are the mod's config.json
/// names (common/spec/config/schema.json), and the labels and hints are the mod's own words
/// (common/spec/screens/options.json): the same shortcut reads the same in both products.
///
/// ⚠ All empty by default in the mod ("to avoid conflicts with game controls"), and the same here:
/// an empty value means "no shortcut", never "unknown".
/// </summary>
public static class ModShortcuts
{
    public static readonly IReadOnlyList<ModShortcut> All = new[]
    {
        new ModShortcut("toggle_translations_hotkey", "Toggle translations",
            "Turn all translations on/off (restores original text)"),
        new ModShortcut("toggle_ai_hotkey", "Toggle translation backend",
            "Pause/resume live translation - texts already translated stay translated"),
        new ModShortcut("toggle_images_hotkey", "Toggle image replacement",
            "Debug: show original images instead of replacements"),
        new ModShortcut("toggle_fonts_hotkey", "Toggle font replacement",
            "Debug: show the game's original fonts instead of the mod's replacement fonts"),
        new ModShortcut("toggle_overlay_hotkey", "Toggle notifications",
            "Show/hide the corner notification overlay (for clean screenshots)"),
        new ModShortcut("open_inspector_hotkey", "Toggle Inspector",
            "Open/close the element inspector panel"),
        new ModShortcut("open_upload_hotkey", "Toggle Upload",
            "Open/close the translation upload panel"),
        new ModShortcut("open_exclusion_mode_hotkey", "Toggle Exclusion mode",
            "Open/close the inspector in exclusion mode"),
        new ModShortcut("open_text_editor_hotkey", "Toggle Text editor",
            "Open/close the in-game text editor (click UI text to edit)"),
        new ModShortcut("force_scan_hotkey", "Force scene rescan",
            "Re-scan the current scene (useful after scene glitches)"),
    };

    /// <summary>
    /// Whether a value may be written into a game from UGT Manager: empty (no shortcut), or a
    /// universal, valid key — the same limit as the panel key (<see cref="BindableKeys"/>).
    /// </summary>
    public static bool MayTravel(string? value) =>
        string.IsNullOrEmpty(value) || (BindableKeys.IsUniversal(value) && BindableKeys.IsValid(value));

    /// <summary>A shortcut compared as the mod reads it: null and empty are both "none".</summary>
    public static string Norm(string? value) => value ?? "";

    /// <summary>A copy of a shortcut map, never the same instance — see the Copy() of its two holders.</summary>
    public static Dictionary<string, string>? CopyOf(Dictionary<string, string>? map) =>
        map is null ? null : new Dictionary<string, string>(map, StringComparer.Ordinal);
}
