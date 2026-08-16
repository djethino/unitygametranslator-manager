using System.Text.Json.Serialization;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Settings;

/// <summary>
/// Where a value shown on a screen came from. Three sources, and somebody editing one game has to
/// be able to tell them apart at a glance — otherwise "the language is Japanese" is a sentence with
/// three different meanings and no way to know which one is on screen.
/// </summary>
public enum ModValueOrigin
{
    /// <summary>Nothing was said about this game: the defaults answer, and keep answering when they change.</summary>
    Defaults,

    /// <summary>Read out of this game's own config.json. Shown, never frozen — see <see cref="GameModOverrides"/>.</summary>
    Game,

    /// <summary>Decided for this game and stored here. The only one of the three that is ours.</summary>
    ThisGame,
}

/// <summary>
/// The mod settings decided for ONE game, overriding the defaults where they say anything.
///
/// ⚠ **Every field is nullable, and null is a real value**: it means "nothing was decided here", so
/// the game's own configuration answers, and failing that the defaults do — and they keep answering
/// when they change. Writing the resolved value at creation time would freeze today's answer into
/// every game, and changing a default later would then change nothing. This is the same rule
/// <see cref="GamePreference"/> states at the top of its own file, applied one level down.
///
/// ⚠ **Pre-filling a form from the game does NOT create an override.** Showing what a game holds is
/// how somebody decides; storing it because it was displayed would turn merely opening a card into
/// twenty-five decisions nobody took. Only an edit lands here.
///
/// ⚠ **This carries no `enable_ai` and no `game_context`**, deliberately. Both are already per-game
/// and already answered — `GamePreference.StartTranslation` and `GamePreference.GameContext`. A
/// second field for the same key would be a second answer, and the two could disagree.
///
/// ⚠ **No proxy either**: it belongs to the tool's own settings, not to the mod defaults this
/// mirrors, and it describes a network rather than a game.
/// </summary>
public sealed class GameModOverrides
{
    /// <summary>Language code, or "auto". Same values the mod accepts — see InstallerSettings.</summary>
    [JsonPropertyName("target_language")] public string? TargetLanguage { get; set; }

    /// <summary>"none", "llm", "google" or "deepl" — the mod's own spellings, never "ai".</summary>
    [JsonPropertyName("translation_backend")] public string? TranslationBackend { get; set; }

    [JsonPropertyName("ai_url")] public string? AiUrl { get; set; }

    [JsonPropertyName("ai_model")] public string? AiModel { get; set; }

    /// <summary>
    /// The AI key for this game, encrypted on disk exactly as everywhere else in this tool.
    ///
    /// ⚠ Per game because the SERVER is per game: pointing one game at another endpoint and leaving
    /// it on the key of the first would send a credential to somewhere that never issued it. The
    /// two travel together or neither does.
    /// </summary>
    [JsonPropertyName("ai_api_key")] public string? AiApiKeyStored { get; set; }

    /// <summary>The key in clear, in memory only. Never serialised — the split is what makes that true.</summary>
    [JsonIgnore] public string? AiApiKey { get; set; }

    [JsonPropertyName("google_api_key")] public string? GoogleApiKeyStored { get; set; }

    [JsonIgnore] public string? GoogleApiKey { get; set; }

    [JsonPropertyName("deepl_api_key")] public string? DeeplApiKeyStored { get; set; }

    [JsonIgnore] public string? DeeplApiKey { get; set; }

    [JsonPropertyName("deepl_use_free")] public bool? DeeplUseFree { get; set; }

    /// <summary>
    /// A panel shortcut chosen for THIS game, or null when none was.
    ///
    /// 🔴 **It belongs to the hotkey's own brick, never to the settings form.** The card gives the
    /// key its own block: both keys on screen, a capture to set one for this game, the box that
    /// says whether Mod defaults' key wins, and its own verb. That block is the only place this is
    /// ever edited.
    ///
    /// ⚠ It was a row in the settings form for a day, and that broke the mechanism twice over:
    /// setting a key there bypassed the question and wrote it outright, and it hid the box that
    /// asks it. Worse, the resolver then read the GAME's key as a fallback source, so unticking
    /// "use Mod defaults" made the key we would write equal the key the game already had — the
    /// comparison found nothing and the question vanished from the screen. Measured on a real game.
    ///
    /// ⚠ Which key actually reaches a game is decided in ONE place — GameConfigWriter.Intended —
    /// and never by a fallback chain here. See <see cref="ModSettingsResolver.Resolve"/>, which
    /// deliberately leaves this field alone.
    /// </summary>
    [JsonPropertyName("settings_hotkey")] public string? SettingsHotkey { get; set; }

    /// <summary>Whether the MOD may reach the internet from inside this game.</summary>
    [JsonPropertyName("mod_online_mode")] public bool? ModOnlineMode { get; set; }

    [JsonPropertyName("auto_download")] public bool? AutoDownload { get; set; }

    [JsonPropertyName("notify_updates")] public bool? NotifyUpdates { get; set; }

    [JsonPropertyName("check_mod_updates")] public bool? CheckModUpdates { get; set; }

    [JsonPropertyName("merge_strategy")] public string? MergeStrategy { get; set; }

    [JsonPropertyName("notifications_enabled")] public bool? NotificationsEnabled { get; set; }

    [JsonPropertyName("notification_position")] public string? NotificationPosition { get; set; }

    /// <summary>
    /// "stable" or "beta", for the plugin build installed into THIS game.
    ///
    /// Per game because that is where the risk is taken: trying a pre-release plugin in one game is
    /// a different decision from putting it in all of them, and somebody testing a fix has exactly
    /// one game in mind.
    /// </summary>
    [JsonPropertyName("channel")] public string? Channel { get; set; }

    /// <summary>True when nothing at all was decided for this game.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        TargetLanguage is null && TranslationBackend is null && AiUrl is null && AiModel is null
        && AiApiKey is null && GoogleApiKey is null && DeeplApiKey is null && DeeplUseFree is null
        && SettingsHotkey is null && ModOnlineMode is null && AutoDownload is null
        && NotifyUpdates is null && CheckModUpdates is null && MergeStrategy is null
        && NotificationsEnabled is null && NotificationPosition is null && Channel is null;

    /// <summary>
    /// How many settings this game answers on its own — the figure the settings form shows.
    ///
    /// ⚠ **The hotkey is deliberately not counted, while <see cref="IsEmpty"/> does count it.** The
    /// two answer different questions: this one labels a form the key is not part of, so counting
    /// it would announce four answers above three rows. IsEmpty asks "is there anything to keep at
    /// all", and a key set on its own is certainly something.
    /// </summary>
    [JsonIgnore]
    public int Count =>
        (TargetLanguage is null ? 0 : 1) + (TranslationBackend is null ? 0 : 1)
        + (AiUrl is null ? 0 : 1) + (AiModel is null ? 0 : 1) + (AiApiKey is null ? 0 : 1)
        + (GoogleApiKey is null ? 0 : 1) + (DeeplApiKey is null ? 0 : 1)
        + (DeeplUseFree is null ? 0 : 1)
        + (ModOnlineMode is null ? 0 : 1) + (AutoDownload is null ? 0 : 1)
        + (NotifyUpdates is null ? 0 : 1) + (CheckModUpdates is null ? 0 : 1)
        + (MergeStrategy is null ? 0 : 1) + (NotificationsEnabled is null ? 0 : 1)
        + (NotificationPosition is null ? 0 : 1) + (Channel is null ? 0 : 1);

    /// <summary>
    /// Encrypts the secrets on the way to disk. The ONE path from plaintext to a file, exactly as
    /// SettingsStore.Save is for the defaults.
    /// </summary>
    public void ProtectSecrets()
    {
        AiApiKeyStored = Secrets.Protect(AiApiKey);
        GoogleApiKeyStored = Secrets.Protect(GoogleApiKey);
        DeeplApiKeyStored = Secrets.Protect(DeeplApiKey);
    }

    /// <summary>
    /// Decrypts them on the way in. A file written on another machine cannot be read here and comes
    /// back as "no key" rather than as garbage handed to a provider.
    /// </summary>
    public void UnprotectSecrets()
    {
        AiApiKey = Secrets.Unprotect(AiApiKeyStored);
        GoogleApiKey = Secrets.Unprotect(GoogleApiKeyStored);
        DeeplApiKey = Secrets.Unprotect(DeeplApiKeyStored);
    }

    /// <summary>An independent copy, for a screen that must be able to mean Cancel.</summary>
    public GameModOverrides Copy() => (GameModOverrides)MemberwiseClone();
}

/// <summary>
/// What one game's config.json holds today, in the terms this tool reasons in.
///
/// ⚠ <see cref="Exists"/> and <see cref="IsConfigured"/> are NOT the same question, and confusing
/// them is what decides wrongly whether a newly discovered game starts out following the defaults.
/// The mod writes a config.json the first time it loads, before anybody has answered anything — so
/// "there is a file" says nothing at all. "Somebody answered for this game" is the question, and
/// it is answered by the wizard's own latch or by a setting actually being there.
/// </summary>
/// <param name="Exists">Whether a config.json is present and readable at all.</param>
/// <param name="FirstRunCompleted">
/// The mod's own latch: true once its first-run wizard has been through. A latch, never a
/// preference — see GameConfigWriter.Compare, which refuses to compare it for that reason.
/// </param>
/// <param name="InGameHotkey">
/// The key this game opens the panel with, or null when it names none.
///
/// 🔴 **Here rather than in <see cref="Values"/>, and the distinction is the whole point.** Values
/// are settings: things this tool may be told to write. This is an OBSERVATION: a key captured
/// inside the game against the real keyboard, which is the only measurement of it that exists. It
/// is read so a screen can show both keys side by side — "replace this one?" is unanswerable
/// without them — and it is never a source anything resolves from. Putting it among the settings
/// made it settable, and made unticking the defaults box silently delete the question.
/// </param>
/// <param name="Values">Everything under a key this tool owns. Empty when the file answers none.</param>
/// <param name="AutoTranslate">
/// The mod's `enable_ai` as this game currently holds it — whether it translates while played.
///
/// 🔴 **An OBSERVATION, like the hotkey, and for the same reason.** It is written from
/// <see cref="GamePreference.StartTranslation"/>, but somebody can turn it off inside the mod, and
/// from that moment the stored preference says the opposite of what the game does. Anything that
/// DESCRIBES a game — starting with the Play button, which promises what pressing it will do —
/// reads this and never the preference.
///
/// Null means the game names no answer: written before the key existed, or never configured.
/// </param>
public sealed record GameConfigSnapshot(bool Exists, bool FirstRunCompleted, string? InGameHotkey,
                                        GameModOverrides Values, bool? AutoTranslate = null)
{
    /// <summary>
    /// Whether somebody has configured this game — as opposed to the mod having dropped a file.
    ///
    /// 🔴 This is what decides how "Use my mod defaults here" starts out on a game nobody has
    /// decided about: a game that is already configured keeps its own configuration, and the first
    /// one-click must not silently overwrite an answer somebody gave inside the game.
    /// </summary>
    /// <remarks>
    /// A key of its own counts: somebody sat in that game and captured it. It is one of the
    /// clearest signs there is that this game was set up by a person rather than merely visited by
    /// the mod.
    /// </remarks>
    public bool IsConfigured => Exists && (FirstRunCompleted || InGameHotkey is not null || !Values.IsEmpty);

    /// <summary>A game we have never been able to read anything from.</summary>
    public static readonly GameConfigSnapshot Unknown = new(false, false, null, new GameModOverrides());
}

/// <summary>
/// Turns the three sources into the one set of values a game is actually written with.
///
/// 🔴 **In the Core, never on a screen.** The CLI reports what a game will be configured with and
/// the window shows it; the two answering the same question differently is precisely the failure
/// this class exists to make impossible.
/// </summary>
public static class ModSettingsResolver
{
    /// <summary>
    /// What would be written into this game, from the defaults, this game's own answers, and what
    /// the game already holds.
    /// </summary>
    /// <param name="snapshot">
    /// What the game's config.json holds today, or null when nothing could be read.
    ///
    /// ⚠ It is a SOURCE, not merely a display: with "use my mod defaults here" unticked and no
    /// override for a setting, the game's own value is what gets written back — which is what makes
    /// unticking safe. Applying then rewrites the same bytes and changes nothing, so an untouched
    /// game stays untouched.
    ///
    /// It also settles whether the box starts out ticked at all — see
    /// <see cref="GamePreference.UsesModDefaults"/>.
    /// </param>
    public static InstallerSettings Resolve(InstallerSettings defaults, GamePreference preference,
                                            GameConfigSnapshot? snapshot)
    {
        // Ticked: the defaults answer everything, and there is nothing to merge. Returned as-is
        // rather than copied, because nothing downstream writes to it.
        if (preference.UsesModDefaults(snapshot)) return defaults;

        // ⚠ A memberwise clone rather than a field-by-field copy, and that is load-bearing: this
        // object is handed to GameConfigWriter, which reads settings this screen has never heard of
        // (the proxy, first of all). A copy that forgot one would quietly write a default into
        // somebody's game, and adding a field later would silently reopen the hole.
        var resolved = defaults.Copy();

        var own = preference.Mod;
        var inGame = snapshot?.Values;

        resolved.TargetLanguage = own?.TargetLanguage ?? inGame?.TargetLanguage ?? defaults.TargetLanguage;
        resolved.TranslationBackend = own?.TranslationBackend ?? inGame?.TranslationBackend ?? defaults.TranslationBackend;
        resolved.AiUrl = own?.AiUrl ?? inGame?.AiUrl ?? defaults.AiUrl;
        resolved.AiModel = own?.AiModel ?? inGame?.AiModel ?? defaults.AiModel;
        resolved.AiApiKey = own?.AiApiKey ?? inGame?.AiApiKey ?? defaults.AiApiKey;
        resolved.GoogleApiKey = own?.GoogleApiKey ?? inGame?.GoogleApiKey ?? defaults.GoogleApiKey;
        resolved.DeeplApiKey = own?.DeeplApiKey ?? inGame?.DeeplApiKey ?? defaults.DeeplApiKey;
        resolved.DeeplUseFree = own?.DeeplUseFree ?? inGame?.DeeplUseFree ?? defaults.DeeplUseFree;
        // 🔴 **SettingsHotkey is deliberately absent from this chain**, and the Copy() above leaves
        // the defaults' key in place. Which key reaches a game is decided in ONE place —
        // GameConfigWriter.Intended — from the box, the key set for this game, and what the game
        // holds, in that order.
        //
        // ⚠ Reading the GAME's key here as a fallback source would make the key we would write
        // equal the key the game already has: the comparison then finds nothing, and "replace this
        // game's key" vanishes from the screen the moment somebody unticks the box. That happened.
        resolved.ModOnlineMode = own?.ModOnlineMode ?? inGame?.ModOnlineMode ?? defaults.ModOnlineMode;
        resolved.AutoDownload = own?.AutoDownload ?? inGame?.AutoDownload ?? defaults.AutoDownload;
        resolved.NotifyUpdates = own?.NotifyUpdates ?? inGame?.NotifyUpdates ?? defaults.NotifyUpdates;
        resolved.CheckModUpdates = own?.CheckModUpdates ?? inGame?.CheckModUpdates ?? defaults.CheckModUpdates;
        resolved.MergeStrategy = own?.MergeStrategy ?? inGame?.MergeStrategy ?? defaults.MergeStrategy;
        resolved.NotificationsEnabled = own?.NotificationsEnabled ?? inGame?.NotificationsEnabled ?? defaults.NotificationsEnabled;
        resolved.NotificationPosition = own?.NotificationPosition ?? inGame?.NotificationPosition ?? defaults.NotificationPosition;
        resolved.Channel = own?.Channel ?? inGame?.Channel ?? defaults.Channel;

        return resolved;
    }

    /// <summary>
    /// Which of the three sources a value on screen came from.
    ///
    /// Asked field by field by the screen, with that field's own two candidates, rather than by
    /// name: a lookup keyed on a label loses its meaning at the first rewording, and one keyed on a
    /// string is a second inventory of the settings.
    /// </summary>
    public static ModValueOrigin OriginOf(object? own, object? inGame) =>
        own is not null ? ModValueOrigin.ThisGame
        : inGame is not null ? ModValueOrigin.Game
        : ModValueOrigin.Defaults;

    /// <summary>
    /// The words a screen puts beside a field to say where its value came from.
    ///
    /// 🔴 **The source is NAMED, never referred to by a possessive or by a position.** "from my
    /// defaults" has no referent — a machine owns nothing, and one machine carries games belonging
    /// to different people — and "the settings below" points at a block that is folded shut by
    /// default. Mod defaults is a screen with a title on it; that title is what this says.
    /// </summary>
    public static string Describe(ModValueOrigin origin) => origin switch
    {
        ModValueOrigin.ThisGame => "set for this game",

        // ⚠ The wording GameContextField already uses for exactly this state. Reused rather than
        // reinvented: it is the same fact, and two spellings of it would read as two facts.
        ModValueOrigin.Game => "read from this game",

        _ => "from Mod defaults",
    };
}
