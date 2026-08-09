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

    /// <summary>
    /// The API key as it sits on disk: always encrypted, never readable text.
    ///
    /// Two properties rather than one, on purpose. Callers need the key in clear to send a
    /// request; the file must never hold it that way. Keeping them apart means nobody can
    /// serialise the plaintext by accident — the only path to disk goes through the store.
    /// </summary>
    [JsonPropertyName("ai_api_key")] public string? AiApiKeyStored { get; set; }

    /// <summary>The key in clear, in memory only. Never serialised.</summary>
    [JsonIgnore] public string? AiApiKey { get; set; }

    /// <summary>
    /// How to reach the network: "default", "none", "system" or "custom".
    ///
    /// Same four values and the same key name as the mod's own proxy_mode, so someone who
    /// configured one does not have to learn a second vocabulary — and so these can be written
    /// into a game's config.json unchanged the day we do that.
    /// </summary>
    [JsonPropertyName("proxy_mode")] public string ProxyMode { get; set; } = "default";

    [JsonPropertyName("proxy_url")] public string? ProxyUrl { get; set; }

    [JsonPropertyName("proxy_username")] public string? ProxyUsername { get; set; }

    /// <summary>
    /// The proxy password as it sits on disk: encrypted, like every other secret here. Same
    /// two-property split as the API key — nothing can serialise the plaintext by accident.
    /// </summary>
    [JsonPropertyName("proxy_password")] public string? ProxyPasswordStored { get; set; }

    /// <summary>The proxy password in clear, in memory only. Never serialised.</summary>
    [JsonIgnore] public string? ProxyPassword { get; set; }

    [JsonPropertyName("proxy_bypass_local")] public bool ProxyBypassLocal { get; set; } = true;

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
