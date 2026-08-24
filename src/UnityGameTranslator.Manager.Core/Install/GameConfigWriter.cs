using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Settings;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>What was written into a game, in the words used on screen.</summary>
public sealed record ConfigWriteResult(
    bool Written,
    IReadOnlyList<string> Applied,
    bool WizardSkipped,
    string? Failure);

/// <summary>
/// One setting where the game disagrees with what we would put there.
///
/// Both sides are already rendered for reading: a difference is shown to somebody deciding
/// whether to overwrite their own choice, and "true / false" answers nothing.
/// </summary>
/// <param name="Key">
/// The config.json key this line is about — a stable identifier, unlike the label.
///
/// ⚠ Exists so a screen can attach something to ONE line (the hotkey carries a checkbox) without
/// recognising it by its wording. The <paramref name="Label"/> is written for a reader and will be
/// reworded; anything hanging off it silently stops working the day it is. Nested keys are spelled
/// "parent.key", as the file has them.
/// </param>
/// <param name="Label">The setting in the words the screens use.</param>
/// <param name="InGame">What the game says now.</param>
/// <param name="Ours">What applying the defaults would put there.</param>
/// <param name="Note">
/// What is worth knowing before overwriting this one, or null when the value speaks for itself.
/// Carried here rather than reconstructed on the screen: the reason a setting deserves a caveat is
/// known where the setting is declared, and a screen matching on labels would lose it at the first
/// rewording.
/// </param>
/// <param name="Writes">
/// Whether applying would actually change this one, or merely shows where the two stand.
///
/// 🔴 **False is not a lesser difference, it is a different sentence**: "this game keeps its own
/// key, and here is yours for comparison". Only the hotkey produces it today — the one setting a
/// game may legitimately know better than we do — and it is the reason the line exists at all,
/// since deciding whether to replace a key is impossible without seeing both.
///
/// ⚠ Anything counting "what would one click do" must count only the lines that write. A list that
/// mixes the two makes the button offer to apply something it will deliberately leave alone.
/// </param>
public sealed record ConfigDifference(string Key, string Label, string InGame, string Ours,
                                      string? Note = null, bool Writes = true);

/// <summary>
/// Puts the installer's settings into a game's config.json, and nothing else.
///
/// ⚠ **Merge, never rewrite.** The file belongs to the mod, not to us. It holds an api_token we
/// must never touch, secrets encrypted for this machine, and keys this tool has never heard of —
/// the mod itself preserves those through its _extraData mechanism precisely because it expects
/// them to survive. Serialising a C# model over the file would silently delete every one of them,
/// and the user would discover it as "the mod logged me out and lost my settings".
///
/// So the file is loaded as a JSON tree, the handful of keys we own are set, and everything else
/// is written back untouched — including keys we cannot interpret.
///
/// ⚠ **api_token is never read and never written.** Site authentication belongs to the mod, and
/// the installer's own token is deliberately separate: revoking one must not disconnect the other.
/// Copying a token between them would quietly break that.
///
/// ⚠ **The keys we own are declared ONCE, in <see cref="Intended"/>.** Writing and comparing both
/// read that list, because they are the same question asked in two directions — and a second
/// hand-written inventory would answer it differently within a release or two. A key missing from
/// the comparison shows up as "your game matches your settings" about a setting nobody checked,
/// which is the kind of wrong that never gets reported.
/// </summary>
public sealed class GameConfigWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The mod's key for the panel shortcut.</summary>
    /// <remarks>
    /// Named here, like <see cref="GameContextKey"/>, because these are the keys read back from
    /// outside <see cref="Intended"/> by <see cref="InGameValue"/>. A second literal spelling of
    /// one of them would be a second declaration, which this class exists to avoid.
    /// </remarks>
    public const string HotkeyKey = "settings_hotkey";

    /// <summary>The mod's key for what a game is about, in the words sent to the AI.</summary>
    public const string GameContextKey = "game_context";

    /// <summary>
    /// The mod's key for the language it works towards. ⚠ A language NAME, never an ISO code —
    /// see the intent below.
    /// </summary>
    public const string TargetLanguageKey = "target_language";

    /// <summary>
    /// Who the MOD is signed in as, in this game.
    ///
    /// ⚠ Read only, and never written — like api_token, which this class does not touch at all.
    /// It is read for one reason: telling "this translation is mine" from "somebody else's"
    /// needs the account the GAME uses, and one machine can carry several. A household where one
    /// game is signed in as one person and the next as another is ordinary, and judging with this
    /// tool's own account would award somebody a Main they do not own — the very mistake
    /// AccountLineages was written to avoid.
    /// </summary>
    public const string ApiUserKey = "api_user";

    /// <summary>
    /// Where this game's config.json lives. One composition, because three callers needing the
    /// same path is exactly how two of them end up disagreeing about it.
    /// </summary>
    private static string ConfigPath(string gamePath, LoaderDescriptor descriptor) =>
        Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            LocalTranslationProbe.ConfigFileName);

    /// <summary>
    /// What this game carries right now under one of our own keys, or null when there is nothing
    /// to speak of — no config yet, one we cannot read, or one that never learned that key.
    ///
    /// ⚠ Exists because <see cref="Compare"/> deliberately cannot answer this, and for two
    /// different reasons. A key written only on demand is skipped there the moment the game has
    /// one of its own; and a key the game answered on its own is exactly what a screen needs to
    /// show BEFORE offering to replace it. Both cases are a question asked next to the game's own
    /// value, and a list of differences is the wrong shape for that.
    ///
    /// ⚠ Strings only, by design: the two keys read this way are both text a person wrote. A
    /// value stored as anything else comes back null rather than guessed at.
    /// </summary>
    public static string? InGameValue(string gamePath, LoaderDescriptor? descriptor, string key)
    {
        if (descriptor is null) return null;

        var path = ConfigPath(gamePath, descriptor);
        if (!File.Exists(path)) return null;

        try
        {
            var node = Load(path)[key];
            if (node is null) return null;

            var text = node.GetValue<JsonElement>().ValueKind == JsonValueKind.String
                ? node.GetValue<string>()
                : null;

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            // Same silence as Compare: a config we cannot read is reported by the install path,
            // which refuses to touch it. Saying it twice, less accurately, helps nobody.
            return null;
        }
    }

    /// <summary>
    /// Everything this game holds under a key we own, read back into the terms this tool reasons in.
    ///
    /// ⚠ **The mirror of <see cref="Intended"/>, and it has to stay one.** A key written there and
    /// not read here comes back as "this game says nothing about it", so a screen offering to
    /// override it starts from the defaults instead of from what the player set inside the game —
    /// which is the exact opposite of what pre-filling is for. The two lists are checked against
    /// each other by the checks project.
    ///
    /// ⚠ Never throws. A config.json somebody hand-edited and broke comes back as
    /// <see cref="GameConfigSnapshot.Unknown"/>; the install path is what reports it, and refuses
    /// to touch it. Saying it twice, less accurately, helps nobody — the same silence
    /// <see cref="Compare"/> keeps.
    /// </summary>
    public static GameConfigSnapshot Read(string gamePath, LoaderDescriptor? descriptor)
    {
        if (descriptor is null) return GameConfigSnapshot.Unknown;

        var path = ConfigPath(gamePath, descriptor);
        if (!File.Exists(path)) return GameConfigSnapshot.Unknown;

        try
        {
            var root = Load(path);
            var values = new GameModOverrides
            {
                TargetLanguage = Text(root, null, TargetLanguageKey),
                TranslationBackend = Text(root, null, "translation_backend"),
                AiUrl = Text(root, null, "ai_url"),
                AiModel = Text(root, null, "ai_model"),

                // Read into the CLEAR fields, then handed straight back to ProtectSecrets when
                // something is stored. The stored halves stay empty on purpose: what a game holds
                // is encrypted for this machine already, and copying one ciphertext into another
                // file would be a second copy of a secret nobody asked us to keep.
                AiApiKey = Secret(root, "ai_api_key"),
                GoogleApiKey = Secret(root, "google_api_key"),
                DeeplApiKey = Secret(root, "deepl_api_key"),
                DeeplUseFree = Flag(root, null, "deepl_use_free"),
                ModOnlineMode = Flag(root, null, "online_mode"),
                AutoDownload = Flag(root, "sync", "auto_download"),
                NotifyUpdates = Flag(root, "sync", "notify_updates"),
                CheckModUpdates = Flag(root, "sync", "check_mod_updates"),
                MergeStrategy = Text(root, "sync", "merge_strategy"),
                NotificationsEnabled = Flag(root, "sync", "notifications_enabled"),
                NotificationPosition = Text(root, "sync", "notification_position"),

                // ⚠ The channel is not stored in a game; what IS stored is what the mod announces
                // from inside it, which Intended derives from the channel. Read back the same way
                // round, and absent stays absent: reading "no pre-release notices" as "stable"
                // would invent an answer for every game written before this key existed.
                Channel = Flag(root, "sync", "notify_prereleases") switch
                {
                    true => "beta",
                    false => "stable",
                    null => null,
                },
            };

            // ⚠ The hotkey and enable_ai are carried BESIDE the settings, never among them — see
            // GameConfigSnapshot.InGameHotkey. They are measurements taken inside the game, not
            // values this tool may be told to write.
            //
            // ⚠ enable_ai is WRITTEN from GamePreference.StartTranslation and, until now, never
            // read back. That is fine for writing and wrong for describing: somebody who switches
            // translation off inside the mod leaves our stored preference saying it is on, and
            // anything reporting from that preference states the opposite of what the game does.
            return new GameConfigSnapshot(
                true,
                Flag(root, null, "first_run_completed") == true,
                Text(root, null, HotkeyKey),
                values,
                Flag(root, null, "enable_ai"));
        }
        catch
        {
            return GameConfigSnapshot.Unknown;
        }
    }

    /// <summary>A string under a key, or null when the game says nothing usable there.</summary>
    private static string? Text(JsonObject root, string? parent, string key)
    {
        var node = At(root, parent, key);
        if (node is null || node.GetValueKind() != JsonValueKind.String) return null;

        var text = node.GetValue<string>();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>A true/false under a key, or null when it is absent or is something else.</summary>
    private static bool? Flag(JsonObject root, string? parent, string key) =>
        At(root, parent, key)?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    /// <summary>
    /// A secret in clear, or null when there is none — or when it was sealed for another machine.
    ///
    /// That last case is deliberate and it is why this cannot simply carry the stored form across:
    /// a key that cannot be read here must come back as "no key", not as ciphertext we would then
    /// hand to a provider.
    /// </summary>
    private static string? Secret(JsonObject root, string key)
    {
        var stored = Text(root, null, key);
        return stored is null ? null : Secrets.Unprotect(stored);
    }

    private static JsonNode? At(JsonObject root, string? parent, string key) =>
        parent is null ? root[key] : (root[parent] as JsonObject)?[key];

    /// <summary>
    /// One key this tool owns, and the value it would give it.
    ///
    /// <paramref name="Value"/> holds a bool, a string, or null to mean "remove this key".
    /// Secrets are held here in CLEAR and protected on the way to disk — one place where the
    /// plaintext can reach a file, and it does not.
    /// </summary>
    /// <param name="OnlyIfAbsent">
    /// Write this key when the game has none, and leave it alone otherwise — for the settings a
    /// game may legitimately know better than we do. Such a key is also never reported as a
    /// difference: we are not offering to change it, so listing it would be an offer we do not make.
    /// </param>
    /// <param name="AskedSeparately">
    /// This key carries its OWN answer, rendered on its own line — a checkbox beside the values,
    /// rather than being accepted with the block.
    ///
    /// ⚠ **This used to mean "keep it out of <see cref="Compare"/> entirely", and that was wrong
    /// twice over.** It hid the hotkey from the list whatever the box said — so a screen claiming
    /// to list what differs never mentioned the one setting somebody was being asked to decide —
    /// and it forced that screen to render the same fact in a shape of its own, which looked
    /// nothing like the differences it sat next to. The line is now in the list like any other; it
    /// simply carries its own answer, and says so through <see cref="ConfigDifference.Writes"/>.
    ///
    /// ⚠ Still not the same statement as OnlyIfAbsent. That one means "we are not offering to
    /// change this"; this one means "the offer is attached to this very line".
    /// </param>
    /// <param name="AnsweredOnTheCard">
    /// This key already has a control of its own on the game's card, with its own Apply.
    ///
    /// 🔴 **Written as always, and never LISTED as a difference.** "Use Mod defaults in this game"
    /// answers exactly one question — what would applying Mod defaults change here — and these two
    /// do not come from Mod defaults at all: they are per-game answers with their own switch a few
    /// lines above. Listing them counted the same decision twice, and offered to settle with one
    /// button something the reader had just settled with another.
    ///
    /// ⚠ The opposite of <see cref="AskedSeparately"/>, which means "the offer IS this line".
    /// Here the offer is elsewhere, so the line has nothing to add.
    /// </param>
    private readonly record struct Intent(
        string? Parent,
        string Key,
        object? Value,
        string? Label,
        bool Secret = false,
        string? Note = null,
        bool OnlyIfAbsent = false,
        bool AskedSeparately = false,
        bool AnsweredOnTheCard = false);

    /// <summary>
    /// Everything we would write, and nothing else.
    ///
    /// Conditional by design: a key absent from this list is a key we do not own in that
    /// situation, so it is neither written nor compared. An AI server address means nothing to a
    /// game set to DeepL, and comparing it would report a difference about a setting that has no
    /// effect there.
    /// </summary>
    private static List<Intent> Intended(InstallerSettings settings, GamePreference? perGame,
                                         string targetLanguage, bool skipWizard,
                                         out bool wizardSkipped)
    {
        var intents = new List<Intent>();

        // ⚠ The mod stores a language NAME here, never an ISO code: GetSystemLanguageName
        // returns "French", its dropdown lists names, and GetTargetLanguage hands the value
        // straight to the API as ?lang= and to the AI prompt as "translate to ...".
        //
        // Writing "fr" produced a game that searched the catalogue for a language nobody
        // publishes under and asked a model to "translate to fr". The tool keeps ISO codes
        // internally — they are what a system reports and what makes a picker sane — and
        // converts on the way out.
        //
        // ⚠ And it is NEVER "auto" — see GameLanguages, which decides this pair. The tool knows
        // the answer at the moment it writes; leaving the mod to resolve it at launch made the
        // same install mean different things on different machines.
        intents.Add(new Intent(null, "target_language", targetLanguage, "language"));

        // ⚠ source_language is NOT here and must not be added — see GameLanguages. We cannot read
        // what language a game's own text is in, and writing a guess is an instruction to the
        // model, not a label: with strict_source_language it makes the model skip every line it
        // judges to be in another language, and a skipped line is cached "S", which the merge
        // treats as immutable. One wrong guess retires those lines for good.

        intents.Add(new Intent(null, "translation_backend", settings.TranslationBackend, "translation backend"));

        // ⚠ Written for EVERY backend, not just the AI one.
        //
        // In the mod, translation_backend says which service and enable_ai says whether it runs —
        // two questions the mod itself used to fold into one, which is why this only ever wrote
        // the flag alongside an AI setup. A Google or DeepL setup written without it left the game
        // with a backend and no answer about whether to use it.
        //
        // Named in the report either way: "we switched auto-translation off in your game" is
        // precisely the sentence somebody needs to have read before they wonder why nothing is
        // being translated.
        // 🔴 **"Captures only" settles this on its own, whatever else was answered.** The whole
        // point of that choice is a game that collects its text and translates none of it, so
        // leaving enable_ai to a flag set while another backend was selected would write a setup
        // contradicting the one thing somebody asked for. It is the only backend that decides this
        // rather than merely being told about it.
        var startsTranslating = settings.TranslationBackend == "capture"
            ? false
            : perGame?.StartTranslation ?? settings.EnableAi;
        intents.Add(new Intent(null, "enable_ai", startsTranslating,
            startsTranslating ? "auto-translation on" : "auto-translation off",
            AnsweredOnTheCard: true));

        // Only when this game has one, and never cleared by omission: a description written inside
        // the game, in the mod's own options, must survive an install from here.
        if (perGame?.GameContext is { } context)
            intents.Add(new Intent(null, GameContextKey, context, "what this game is about",
                                   AnsweredOnTheCard: true));

        // ⚠ Three conditions, and each one closed a real way of locking somebody out of the panel.
        //
        // 1. ASKED PER GAME (GamePreference.ReplaceHotkey), never globally. A hotkey belongs to one
        //    GAME: the mod captures it against the real keyboard there, which is the only
        //    measurement that exists. This tool only ever holds a global preference, and it cannot
        //    know what a given game already uses — so the question can only be answered on that
        //    game's card, next to both values. It used to be a single box on the defaults screen,
        //    which asked somebody to decide for every game at once, out of sight of every one of
        //    them, and showed nothing afterwards.
        // 2. UNIVERSAL ONLY. What a KeyCode designates depends on a per-project Unity setting
        //    ("Use Physical Keys") that no runtime API reports — so a key that prints a character
        //    means different things in different games. Six of the thirteen test games disagreed
        //    with the other five about the very same physical key.
        // 3. PARSEABLE at all, as before.
        //
        // Get it wrong and the mod says nothing: the panel simply stops opening, in a game where it
        // used to, and the settings screen that could fix it is the one behind that key.
        // See analyse/hotkey-keycode-divergence.md.
        // 🔴 **WHICH key reaches a game is decided HERE, once, and nowhere else.** Three things can
        // answer it and they have a strict order — a fallback chain spread over a resolver and a
        // screen is how the question ends up answered twice, differently.
        //
        //   1. the box ticked  → the key from Mod defaults, replacing what the game has;
        //   2. a key set for THIS game → that one (it is an explicit act about this very game);
        //   3. otherwise → the game keeps its own, and gets Mod defaults' key only if it has none.
        //
        // ⚠ Only universal keys ever travel from here, whichever of the three it is. What a Unity
        // KeyCode designates depends on a per-project setting no runtime API reports, so a key that
        // prints a character means different things in different games — six of thirteen test games
        // disagreed with the other five about the same physical key. Get it wrong and the mod says
        // nothing: its panel simply stops opening, in a game where it used to, and the screen that
        // could fix it is the one behind that key. See analyse/hotkey-keycode-divergence.md.
        var chosenKey = (perGame?.ReplaceHotkey ?? false)
            ? settings.SettingsHotkey
            : perGame?.Mod?.SettingsHotkey ?? settings.SettingsHotkey;

        if (BindableKeys.IsUniversal(chosenKey) && BindableKeys.IsValid(chosenKey))
        {
            // Whether it overwrites a key the game already carries, or only fills an empty slot.
            var replaces = (perGame?.ReplaceHotkey ?? false)
                           || perGame?.Mod?.SettingsHotkey is not null;

            // ⚠ Unticked does NOT mean "never write it": a game that has no hotkey yet has nothing
            // to overwrite, and skipping it there would leave the mod on its own default while
            // first_run_completed claims the question was answered — AnswersTheWizard counts this
            // setting among the answers. So: always on a fresh config, only on demand afterwards.
            intents.Add(new Intent(null, HotkeyKey, chosenKey, "in-game hotkey",
                Note: "set in the game itself — applying replaces the key you chose there",
                OnlyIfAbsent: !replaces,
                AskedSeparately: true));
        }

        // The mod's own setting, not this tool's. Someone who installed everything from here,
        // translation included, has what they need before the game starts and may not want the mod
        // reaching the network while they play.
        intents.Add(new Intent(null, "online_mode", settings.ModOnlineMode, "community features in game"));

        if (settings.TranslationBackend == "llm")
        {
            intents.Add(new Intent(null, "ai_url", settings.AiUrl, "AI server"));
            intents.Add(new Intent(null, "ai_model", settings.AiModel, "AI model"));

            // Encrypted with the same scheme, the same constants and the same machine identity as
            // the mod's own TokenProtection — that mirror exists exactly so this line can work.
            if (!string.IsNullOrWhiteSpace(settings.AiApiKey))
                intents.Add(new Intent(null, "ai_api_key", settings.AiApiKey, "API key", Secret: true));
        }
        else if (settings.TranslationBackend == "google")
        {
            // Without the key the backend is written but cannot translate a line, and the failure
            // appears in the game with nothing to explain it.
            if (!string.IsNullOrWhiteSpace(settings.GoogleApiKey))
                intents.Add(new Intent(null, "google_api_key", settings.GoogleApiKey, "Google key", Secret: true));
        }
        else if (settings.TranslationBackend == "deepl")
        {
            if (!string.IsNullOrWhiteSpace(settings.DeeplApiKey))
                intents.Add(new Intent(null, "deepl_api_key", settings.DeeplApiKey, "DeepL key", Secret: true));

            // Free and paid DeepL are different hosts; guessing wrong fails every request.
            intents.Add(new Intent(null, "deepl_use_free", settings.DeeplUseFree, "DeepL plan"));
        }

        // Carried across only when asked to. It usually IS the same network on both sides, which is
        // why the box is ticked by default — but it is a decision, and a game that never needed a
        // proxy should not inherit one because the installer did.
        if (settings.ProxyInGames && settings.ProxyMode != "default")
        {
            intents.Add(new Intent(null, "proxy_mode", settings.ProxyMode, "proxy"));
            intents.Add(new Intent(null, "proxy_url", settings.ProxyUrl, null));
            intents.Add(new Intent(null, "proxy_username", settings.ProxyUsername, null));
            intents.Add(new Intent(null, "proxy_bypass_local", settings.ProxyBypassLocal, null));

            if (!string.IsNullOrWhiteSpace(settings.ProxyPassword))
                intents.Add(new Intent(null, "proxy_password", settings.ProxyPassword, null, Secret: true));
        }

        // The channel picked here decides which plugin build is installed; the mod has its own
        // switch for which releases it announces. Leaving them apart meant choosing beta and still
        // being told only about stable ones.
        if (settings.Channel == "beta")
            intents.Add(new Intent("sync", "notify_prereleases", true, "beta update notices"));

        // All inside the mod's own "sync" block, set one key at a time so everything else it holds
        // — ignored_uuids, last_seen_mod_version, per-game state — survives untouched.
        intents.Add(new Intent("sync", "auto_download", settings.AutoDownload, null));
        intents.Add(new Intent("sync", "notify_updates", settings.NotifyUpdates, null));
        intents.Add(new Intent("sync", "check_mod_updates", settings.CheckModUpdates, null));
        intents.Add(new Intent("sync", "merge_strategy", settings.MergeStrategy, "update preferences"));
        intents.Add(new Intent("sync", "notifications_enabled", settings.NotificationsEnabled, null));
        intents.Add(new Intent("sync", "notification_position", settings.NotificationPosition, "notifications"));

        wizardSkipped = skipWizard && settings.AnswersTheWizard;
        if (wizardSkipped)
            intents.Add(new Intent(null, "first_run_completed", true, "first-run wizard skipped"));

        return intents;
    }

    /// <summary>
    /// Applies the settings to one game.
    ///
    /// <paramref name="skipWizard"/> only takes effect when the settings answer every question the
    /// mod's first-run wizard asks. Writing first_run_completed on a partial configuration would
    /// leave someone with a mod set to the wrong language and no screen on which to notice it —
    /// the wizard is the safety net, and we only remove it once we have genuinely replaced it.
    /// </summary>
    /// <param name="perGame">
    /// What was decided for this game in particular, which wins over the defaults wherever it
    /// says anything. Null means nothing was decided here and the defaults stand alone.
    /// </param>
    /// <param name="targetLanguage">
    /// The language this game is to be set to, decided by <see cref="GameLanguages.TargetFor"/>.
    /// Passed in rather than derived here, because deciding it needs what is published for this
    /// game — which this class has no business knowing about — and because the same decision has
    /// to be made identically by the screen that only shows it.
    /// </param>
    public ConfigWriteResult Apply(string gamePath, LoaderDescriptor descriptor,
                                   InstallerSettings settings, string targetLanguage,
                                   bool skipWizard = true, GamePreference? perGame = null)
    {
        var folder = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar));
        var path = ConfigPath(gamePath, descriptor);

        try
        {
            var root = Load(path);
            var applied = new List<string>();

            var intents = Intended(settings, perGame, targetLanguage, skipWizard, out var wizardSkipped);

            foreach (var intent in intents)
            {
                // Left exactly as the game has it — see Intent.OnlyIfAbsent.
                if (intent.OnlyIfAbsent && Existing(root, intent) is not null) continue;

                var value = intent.Secret && intent.Value is string secret
                    ? Secrets.Protect(secret)
                    : intent.Value;

                if (intent.Parent is null) Set(root, applied, intent.Key, value, intent.Label);
                else SetNested(root, applied, intent.Parent, intent.Key, value, intent.Label);
            }

            Directory.CreateDirectory(folder);

            // Written beside the target then moved into place. A config half-written by a crash or
            // a full disk is worse than no config at all: the mod would fall back to defaults and
            // the player would lose settings they had spent time on.
            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);

            return new ConfigWriteResult(true, applied, wizardSkipped, null);
        }
        catch (Exception ex)
        {
            return new ConfigWriteResult(false, Array.Empty<string>(), false,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes ONE key we own, leaving every other setting in the file exactly as the game has it.
    ///
    /// ⚠ Exists for the settings that belong to the GAME rather than to the defaults — what a game
    /// is about, first of all. Sending those through <see cref="Apply"/> would write the language,
    /// the backend and the update preferences in the same breath, which is not what a button that
    /// saves one sentence may do. It also has to work while "use my mod defaults here" is off:
    /// that switch governs the defaults, and this value was never one of them.
    ///
    /// Null removes the key — that is how "I emptied the box" reaches the file.
    /// </summary>
    public ConfigWriteResult ApplyOne(string gamePath, LoaderDescriptor descriptor,
                                      string key, string? value, string label)
    {
        var folder = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar));
        var path = ConfigPath(gamePath, descriptor);

        try
        {
            var root = Load(path);
            var applied = new List<string>();

            Set(root, applied, key, value, label);

            Directory.CreateDirectory(folder);

            // Same write-beside-then-move as Apply: a config half-written by a crash is worse than
            // no config at all.
            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);

            return new ConfigWriteResult(true, applied, false, null);
        }
        catch (Exception ex)
        {
            return new ConfigWriteResult(false, Array.Empty<string>(), false,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The key a difference is filed under, spelled as the file has it: "sync.merge_strategy" for
    /// a nested one, the bare name otherwise. One composition, so a screen and this class cannot
    /// disagree about how a nested key is written.
    /// </summary>
    private static string KeyOf(Intent intent) =>
        intent.Parent is null ? intent.Key : $"{intent.Parent}.{intent.Key}";

    /// <summary>What the game holds for this key today, or null when it holds nothing.</summary>
    private static JsonNode? Existing(JsonObject root, Intent intent) =>
        intent.Parent is null
            ? root[intent.Key]
            : (root[intent.Parent] as JsonObject)?[intent.Key];

    /// <summary>
    /// Where this game's configuration differs from what applying the defaults would put there.
    ///
    /// Empty means they agree — or that there is no config at all yet, which is the same silence:
    /// a game the mod has never run in has nothing to disagree about, and announcing every setting
    /// as "different" before the first launch would turn a fresh install into a wall of warnings.
    ///
    /// ⚠ A difference is NOT a fault, and nothing here should be worded as one. Somebody may have
    /// set one game to another language on purpose, or turned its AI off for the evening. This
    /// exists so the screen can offer, not so it can insist.
    /// </summary>
    public IReadOnlyList<ConfigDifference> Compare(string gamePath, LoaderDescriptor descriptor,
                                                   InstallerSettings settings, string targetLanguage,
                                                   GamePreference? perGame = null)
    {
        var path = ConfigPath(gamePath, descriptor);

        if (!File.Exists(path)) return Array.Empty<ConfigDifference>();

        JsonObject root;
        try
        {
            root = Load(path);
        }
        catch
        {
            // A config we cannot read is reported by the install path, which refuses to touch it.
            // Repeating it here as a list of differences would be a second, less accurate way of
            // saying the same thing.
            return Array.Empty<ConfigDifference>();
        }

        var differences = new List<ConfigDifference>();

        // ⚠ skipWizard: false. first_run_completed is not a preference, it is a latch — a game
        // that has been through the wizard carries true, and comparing it would report "the
        // first-run wizard differs" on every game somebody has actually played.
        foreach (var intent in Intended(settings, perGame, targetLanguage, skipWizard: false, out _))
        {
            // A key we would only remove has nothing to compare: its absence is our intent.
            if (intent.Value is null) continue;

            // Answered by its own control, with its own Apply — see Intent.AnsweredOnTheCard.
            if (intent.AnsweredOnTheCard) continue;

            // Unnamed keys travel with a named one — proxy_url with proxy_mode, auto_download with
            // the update preferences. Reporting them separately would list six differences for one
            // decision, and none of the six in words anybody chose.
            if (intent.Label is null) continue;

            var node = Existing(root, intent);

            // Nothing is being offered about this one while the game already answers it, so there
            // is nothing to report either.
            //
            // ⚠ Except when the line carries its own answer. Then the whole point is to show both
            // values — "replace this game's key?" cannot be answered without them — so it is
            // reported as a difference that does NOT write, rather than not reported at all. That
            // silence is what left the hotkey rendered in a shape of its own, beside a list it
            // belonged in.
            if (intent.OnlyIfAbsent && node is not null && !intent.AskedSeparately) continue;

            // Absent in the game means the mod is on its default there. That IS a difference worth
            // offering — it is how "your game never learned your hotkey" shows up — but it is
            // worded as absence rather than as a wrong value.
            var inGame = node is null ? null : Read(node, intent.Secret);
            var ours = Render(intent.Value, intent.Secret);

            if (inGame is not null && string.Equals(inGame, ours, StringComparison.Ordinal)) continue;

            differences.Add(new ConfigDifference(
                KeyOf(intent), intent.Label, inGame ?? "not set", ours, intent.Note,

                // The one case where a difference is shown and deliberately left alone: this line
                // asks its own question, and the answer today is no.
                Writes: !(intent.OnlyIfAbsent && node is not null)));
        }

        return differences;
    }

    /// <summary>
    /// A stored value as it should be compared: secrets decrypted, everything else as written.
    ///
    /// ⚠ Decrypting is not optional. Every Protect() of the same secret produces a different
    /// ciphertext, so comparing the stored forms would report the key as different every single
    /// time — and offer to rewrite a key that already matches.
    /// </summary>
    private static string Read(JsonNode node, bool secret)
    {
        var raw = node.GetValueKind() switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => node.ToString(),
        };

        return secret ? Render(Secrets.Unprotect(raw), secret: true) : raw;
    }

    /// <summary>
    /// A value in the form the comparison uses — and, for a secret, a form fit to be shown.
    ///
    /// ⚠ A secret never renders as itself. This string can reach a screen, and an API key on a
    /// screen is an API key in a screenshot. "set" against "set" also means two different keys
    /// compare equal, which is deliberate: the alternative is offering to overwrite a working key
    /// on the strength of something nobody can be shown to verify.
    /// </summary>
    private static string Render(object? value, bool secret)
    {
        if (secret) return string.IsNullOrEmpty(value as string) ? "not set" : "set";

        return value switch
        {
            null => "not set",
            bool flag => flag ? "true" : "false",
            string text => text,
            _ => value.ToString() ?? "",
        };
    }

    /// <summary>
    /// The existing file as a tree, or a new one.
    ///
    /// A file we cannot parse is NOT overwritten: it is left exactly where it is and reported.
    /// It may be a config someone hand-edited and broke, and replacing it would destroy the only
    /// copy of settings they still want — including an api_token they would then have to redo.
    /// </summary>
    private static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return new JsonObject();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();

        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        return node as JsonObject
            ?? throw new InvalidOperationException(
                "This game's config.json is not a JSON object. It was left untouched — "
                + "opening it and fixing it by hand is safer than us guessing.");
    }

    /// <summary>
    /// Sets one key inside a nested object, keeping everything else that object holds.
    ///
    /// The mod's "sync" block carries auto_download, notify_updates, merge_strategy and more. We
    /// only own one of them, so the object is edited in place rather than replaced — assigning a
    /// fresh object would wipe the player's merge strategy to change an update preference, which
    /// is exactly the kind of collateral damage the merge rule exists to prevent.
    /// </summary>
    private static void SetNested(JsonObject root, List<string> applied, string parent, string key,
                                  object? value, string? label)
    {
        if (root[parent] is not JsonObject nested)
        {
            nested = new JsonObject();
            root[parent] = nested;
        }

        nested[key] = value switch
        {
            null => null,
            bool flag => JsonValue.Create(flag),
            string text => JsonValue.Create(text),
            _ => JsonValue.Create(value.ToString()),
        };

        if (label is not null) applied.Add(label);
    }

    /// <summary>
    /// Sets one key, and records it for the report when it carries a name.
    ///
    /// A null value clears the key rather than writing "null" as a string, so unsetting a proxy
    /// username actually unsets it.
    /// </summary>
    private static void Set(JsonObject root, List<string> applied, string key, object? value,
                            string? label)
    {
        if (value is null)
        {
            root.Remove(key);
            return;
        }

        root[key] = value switch
        {
            bool flag => JsonValue.Create(flag),
            string text => JsonValue.Create(text),
            _ => JsonValue.Create(value.ToString()),
        };

        if (label is not null) applied.Add(label);
    }
}
