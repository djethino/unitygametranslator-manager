using System.Text.Json.Serialization;
using UnityGameTranslator.Manager.Core.Install;

namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>
/// What a game is set up to do about its translation.
///
/// ⚠ **Read from the game's state, never stored.** It was a stored preference for a day, and that
/// was the bug: a value kept in a file is a claim about a game somebody may have changed since —
/// and for every game that existed before the field did, a claim nobody ever made. So a game
/// already carrying its owner's translation reported itself as "use somebody else's". It is now
/// deduced from the file present, the selection, and the translate-while-playing switch, so it
/// cannot go stale. See MainWindow.DeducedPosture.
///
/// ⚠ **Three, not four, and the two that left were not dropped by accident.** "Contribute" and
/// "fork" are decisions taken when PUBLISHING, inside the mod: they choose what a lineage does with
/// what you wrote. This tool writes config.json, which has no key for either — so offering them
/// here would have produced exactly the same configuration as "complete it", three buttons for one
/// outcome. What this tool can honestly settle is whether a translation is taken, and whether one
/// is being made while you play.
/// </summary>
public enum Posture
{
    /// <summary>Take the published translation and read it. Nothing is generated while playing.</summary>
    Use,

    /// <summary>Take it, and let the translator fill in whatever it does not cover.</summary>
    Complete,

    /// <summary>Nobody else's file. The translation of this game starts here.</summary>
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

    /// <summary>
    /// "none", "llm", "google" or "deepl" — the mod's own values, written into config.json as-is.
    ///
    /// ⚠ It is "llm", not "ai". This tool used "ai" because that is the word its own screen uses,
    /// and the mod's switch matches on "llm": every AI setup written into a game was a backend the
    /// mod did not recognise, so it translated nothing and said nothing. A value that only has to
    /// travel between two programs must use the receiving program's spelling; the friendly wording
    /// belongs in the UI, not in the file.
    /// </summary>
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
    /// Google Translate and DeepL keys, kept apart from the AI one and from each other.
    ///
    /// One shared field would have looked simpler and been worse: switching backend to compare
    /// them would silently overwrite the key you were using, and you would only find out when a
    /// game stopped translating. Each service gets its own slot, so going back is free.
    /// </summary>
    [JsonPropertyName("google_api_key")] public string? GoogleApiKeyStored { get; set; }

    [JsonIgnore] public string? GoogleApiKey { get; set; }

    [JsonPropertyName("deepl_api_key")] public string? DeeplApiKeyStored { get; set; }

    [JsonIgnore] public string? DeeplApiKey { get; set; }

    /// <summary>DeepL's free tier uses a different host, so the mod needs telling which one.</summary>
    [JsonPropertyName("deepl_use_free")] public bool DeeplUseFree { get; set; } = true;

    /// <summary>
    /// Whether the MOD may reach the internet from inside a game. Separate from OnlineMode, which
    /// governs this tool.
    ///
    /// They looked like one setting and are two decisions. Someone who installs everything from
    /// here — translation included — has what they need before the game starts, and may well not
    /// want the mod talking to anything while they play. Folding the two together meant that
    /// choice could not be expressed at all.
    /// </summary>
    [JsonPropertyName("mod_online_mode")] public bool ModOnlineMode { get; set; } = true;

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

    /// <summary>
    /// Whether the proxy is also written into each game's config.json.
    ///
    /// Asked rather than assumed, because it is genuinely two questions. A proxy is usually a fact
    /// about the network, so the same one serves both — but not always: this tool runs on a work
    /// machine's network while a game may not need it at all, and pushing our setting into every
    /// game would change how the mod reaches the internet without anyone deciding it.
    ///
    /// On by default: when someone has bothered to configure a proxy, the common case is that
    /// nothing here reaches the network without it.
    /// </summary>
    [JsonPropertyName("proxy_in_games")] public bool ProxyInGames { get; set; } = true;

    /// <summary>
    /// The site token, for this tool alone.
    ///
    /// ⚠ Deliberately NOT the mod's. Revoking one must not disconnect the other, and a tool that
    /// installs things has no business holding the credential a game plays with. The installer may
    /// later offer to write its own into a game — asked, never by copying what the mod has.
    ///
    /// Encrypted on disk like every other secret here, with the plaintext kept apart so nothing
    /// can serialise it by accident.
    /// </summary>
    [JsonPropertyName("api_token")] public string? ApiTokenStored { get; set; }

    [JsonIgnore] public string? ApiToken { get; set; }

    /// <summary>Who we are signed in as, for display. Cleared together with the token.</summary>
    [JsonPropertyName("api_user")] public string? ApiUser { get; set; }

    /// <summary>
    /// Which server issued the token. A token is only valid for the site that made it, so pointing
    /// the tool at another instance must not send this one along — the mod holds the same guard.
    /// </summary>
    [JsonPropertyName("api_token_server")] public string? ApiTokenServer { get; set; }

    /// <summary>True when a usable token is on hand.</summary>
    [JsonIgnore] public bool SignedIn => !string.IsNullOrWhiteSpace(ApiToken);

    /// <summary>Community features. Off means the catalog is never queried.</summary>
    [JsonPropertyName("online_mode")] public bool OnlineMode { get; set; } = true;

    /// <summary>
    /// The in-game hotkey. Part of the settings because the mod's first-run wizard asks for it,
    /// and we can only skip that wizard honestly once every one of its questions is answered.
    /// </summary>
    [JsonPropertyName("settings_hotkey")] public string SettingsHotkey { get; set; } = "Ctrl+F10";

    // ⚠ There is deliberately no "write this hotkey into games" setting here, and adding one back
    // would undo a decision rather than fill a gap. Whether a game's own hotkey is replaced is
    // asked on that game's card (GamePreference.ReplaceHotkey), because this tool cannot know what
    // a given game already uses, nor how it reads keys: the mod captured that key against the real
    // keyboard, which is the only measurement anybody has, and this is a global preference.
    // Pushing a preference over a measurement — for every game at once, out of sight of all of
    // them — is how somebody ends up unable to open the panel in a game where it used to work,
    // with no screen left to fix it on. See analyse/hotkey-keycode-divergence.md.

    /// <summary>
    /// Sync and notification preferences, written into the game's own "sync" block.
    ///
    /// These are facts about a person, not about a game: someone with twenty games does not want
    /// to answer "should I download updates automatically" twenty times, and merge_strategy least
    /// of all. Managing updates is this tool's job anyway.
    /// </summary>
    [JsonPropertyName("auto_download")] public bool AutoDownload { get; set; }

    [JsonPropertyName("notify_updates")] public bool NotifyUpdates { get; set; } = true;

    [JsonPropertyName("check_mod_updates")] public bool CheckModUpdates { get; set; } = true;

    /// <summary>"ask", and the other values the mod accepts. Its default is to ask.</summary>
    [JsonPropertyName("merge_strategy")] public string MergeStrategy { get; set; } = "ask";

    [JsonPropertyName("notifications_enabled")] public bool NotificationsEnabled { get; set; } = true;

    /// <summary>"top-right" and the other corners the mod knows.</summary>
    [JsonPropertyName("notification_position")] public string NotificationPosition { get; set; } = "top-right";

    /// <summary>
    /// "stable" or "beta" — for the MOD. Which plugin builds are installed into games, and what
    /// the mod itself notifies about from inside them (its sync.notify_prereleases).
    /// </summary>
    [JsonPropertyName("channel")] public string Channel { get; set; } = "stable";

    /// <summary>
    /// "stable" or "beta" — for THIS TOOL, and deliberately not the same setting as the mod's.
    ///
    /// They look like one choice and are two risks. Trying a beta of the installer costs the
    /// person one program that might misbehave while they watch it; putting a beta of the mod into
    /// their games costs them a plugin loaded into every game they play, which they will meet
    /// mid-session. Someone can reasonably want the first and not the second.
    /// </summary>
    [JsonPropertyName("tool_channel")] public string ToolChannel { get; set; } = "stable";

    /// <summary>
    /// "be" or "github" — which BepInEx 6 builds are installed. A third channel, and a third risk.
    ///
    /// 🔴 **Neither of them is "stable", because BepInEx 6 has none.** Its GitHub page stopped at
    /// a pre-release in August 2024 while development moved to the Bleeding Edge builds, which
    /// ship several times a month. So the honest choice is between a two-year-old pre-release and
    /// an untested recent build — not between safe and adventurous — and the words on screen say
    /// exactly that. Calling the first one stable would promise something nobody is offering.
    ///
    /// Bleeding Edge by default: the plugin works with either, and pre.2 predates every Unity
    /// version released since. ⚠ Neither channel publishes a checksum.
    ///
    /// Applies to bepinex6-mono and bepinex6-il2cpp together — one project, one build server.
    /// BepInEx 5 and MelonLoader have a single source and ignore this.
    /// </summary>
    [JsonPropertyName("bepinex6_channel")] public string BepInEx6Channel { get; set; } = "be";

    /// <summary>
    /// Whether this tool looks for its own updates.
    ///
    /// On by default, like any other program that talks to the internet — and never applied
    /// without a yes, so "on" means "you will be told", not "it will change under you". Turning it
    /// off is honoured to the letter: no request is made at all. OnlineMode being off has the
    /// same effect, since that setting means the tool makes no calls whatsoever.
    /// </summary>
    [JsonPropertyName("check_tool_updates")] public bool CheckToolUpdates { get; set; } = true;

    /// <summary>The tool's channel as the release client wants it.</summary>
    [JsonIgnore]
    public ReleaseChannel ToolReleaseChannel =>
        string.Equals(ToolChannel, "beta", StringComparison.OrdinalIgnoreCase)
            ? ReleaseChannel.Beta
            : ReleaseChannel.Stable;


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

        // Not merely present: usable. The mod parses this into a Unity KeyCode and, when that
        // fails, its panel never opens and it says nothing. Skipping the first-run wizard on top
        // of that would leave someone locked out of the mod with no screen to fix it on — so an
        // unusable hotkey means the wizard runs, which is exactly the safety net it is for.
        && BindableKeys.IsValid(SettingsHotkey)

        && (TranslationBackend != "llm" || !string.IsNullOrWhiteSpace(AiUrl))

        // A paid backend with no key is a backend that cannot translate a single line. Letting
        // the mod's wizard run is better than writing a setup that fails on the first sentence
        // with nothing on screen to explain it.
        && (TranslationBackend != "google" || !string.IsNullOrWhiteSpace(GoogleApiKey))
        && (TranslationBackend != "deepl" || !string.IsNullOrWhiteSpace(DeeplApiKey));

    /// <summary>
    /// An independent copy of every field, for building the settings ONE game is written with
    /// (see ModSettingsResolver) without touching the ones that belong to everybody.
    ///
    /// 🔴 **A memberwise clone, deliberately, and never a field-by-field copy.** This class was
    /// copied field by field once, on the defaults screen, and each Apply silently erased what the
    /// copy had not thought to carry — the account token, the name behind it, the server that
    /// issued it — signing people out from a window that has nothing to do with accounts. Every
    /// field here is a string, a bool or an int, so a shallow clone IS a full copy, and it stays
    /// one as fields are added. That last part is the whole reason.
    ///
    /// ⚠ Never use it to SAVE: SettingsStore.Save says why, and says it at length. This produces a
    /// value to write into a game, which is a different act with a different lifetime.
    /// </summary>
    public InstallerSettings Copy() => (InstallerSettings)MemberwiseClone();
}
