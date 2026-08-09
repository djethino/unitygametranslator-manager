using System.Text.Json.Serialization;

namespace UnityGameTranslator.Installer.Core.Model;

/// <summary>
/// What the player does with what the mod captures, for one game.
///
/// The situation suggests a default — a translation exists, or none does — but it never decides:
/// "complete" is declared by an author at a point in time, and the total number of lines in a
/// game is unknowable, so a game marked complete can still be missing whatever its author never
/// walked past. All four remain offered, including on a complete translation.
/// </summary>
public enum Posture
{
    /// <summary>Play with what exists. Captures stay local.</summary>
    Use,

    /// <summary>Play and give back: a branch of the existing translation.</summary>
    Contribute,

    /// <summary>Take it as a starting point and carry it as my own lineage.</summary>
    Fork,

    /// <summary>Start a translation for this game.</summary>
    Start,
}

/// <summary>
/// The defaults applied to every game.
///
/// The target language especially is not a per-game setting: it is a fact about the person. It
/// is what turns "3 translations available" into "this game is playable in your language", and
/// it drives what every row of the list says.
/// </summary>
public sealed class InstallerSettings
{
    public const string FileName = "settings.json";

    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;

    /// <summary>
    /// Language code, or "auto" to follow the system. Same values the mod accepts, so it can be
    /// written into config.json unchanged.
    /// </summary>
    [JsonPropertyName("target_language")] public string TargetLanguage { get; set; } = "auto";

    /// <summary>"none", "ai", "google", "deepl" — mirrors the mod's translation_backend.</summary>
    [JsonPropertyName("translation_backend")] public string TranslationBackend { get; set; } = "none";

    /// <summary>OpenAI-compatible endpoint. Empty means "not configured yet".</summary>
    [JsonPropertyName("ai_url")] public string AiUrl { get; set; } = "";

    [JsonPropertyName("ai_model")] public string AiModel { get; set; } = "";

    [JsonPropertyName("enable_ai")] public bool EnableAi { get; set; }

    /// <summary>Community features. Off means the catalog is never queried.</summary>
    [JsonPropertyName("online_mode")] public bool OnlineMode { get; set; } = true;

    /// <summary>
    /// The in-game hotkey. Part of the settings because the mod's first-run wizard asks for it,
    /// and we can only skip that wizard honestly once every one of its questions is answered.
    /// </summary>
    [JsonPropertyName("settings_hotkey")] public string SettingsHotkey { get; set; } = "Ctrl+F10";

    /// <summary>"stable" or "beta".</summary>
    [JsonPropertyName("channel")] public string Channel { get; set; } = "stable";

    /// <summary>
    /// What to do when a game could go either way: a translation exists AND could be improved.
    /// Only a default — the choice stays on the game itself.
    /// </summary>
    [JsonPropertyName("default_posture")] public Posture DefaultPosture { get; set; } = Posture.Use;

    /// <summary>
    /// True once the user has been through the settings at least once. Until then we must not
    /// pretend to have answered the mod's wizard on their behalf.
    /// </summary>
    [JsonPropertyName("reviewed")] public bool Reviewed { get; set; }

    /// <summary>
    /// Whether these settings answer every question the mod's first-run wizard asks. Only then
    /// may the wizard be skipped: writing first_run_completed on a partial configuration would
    /// leave someone with a mod set to the wrong language and no screen to notice it on.
    /// </summary>
    [JsonIgnore]
    public bool AnswersTheWizard =>
        Reviewed
        && !string.IsNullOrWhiteSpace(TargetLanguage)
        && !string.IsNullOrWhiteSpace(SettingsHotkey)
        && (TranslationBackend != "ai" || !string.IsNullOrWhiteSpace(AiUrl));
}
