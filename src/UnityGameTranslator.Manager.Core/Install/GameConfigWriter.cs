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
/// <param name="Label">The setting in the words the screens use.</param>
/// <param name="InGame">What the game says now.</param>
/// <param name="Ours">What applying the defaults would put there.</param>
public sealed record ConfigDifference(string Label, string InGame, string Ours);

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

    /// <summary>
    /// One key this tool owns, and the value it would give it.
    ///
    /// <paramref name="Value"/> holds a bool, a string, or null to mean "remove this key".
    /// Secrets are held here in CLEAR and protected on the way to disk — one place where the
    /// plaintext can reach a file, and it does not.
    /// </summary>
    private readonly record struct Intent(
        string? Parent,
        string Key,
        object? Value,
        string? Label,
        bool Secret = false);

    /// <summary>
    /// Everything we would write, and nothing else.
    ///
    /// Conditional by design: a key absent from this list is a key we do not own in that
    /// situation, so it is neither written nor compared. An AI server address means nothing to a
    /// game set to DeepL, and comparing it would report a difference about a setting that has no
    /// effect there.
    /// </summary>
    private static List<Intent> Intended(InstallerSettings settings, GamePreference? perGame,
                                         bool skipWizard, out bool wizardSkipped)
    {
        var intents = new List<Intent>();

        // ⚠ The mod stores a language NAME here, never an ISO code: GetSystemLanguageName
        // returns "French", its dropdown lists names, and GetTargetLanguage hands the value
        // straight to the API as ?lang= and to the AI prompt as "translate to ...".
        //
        // Writing "fr" produced a game that searched the catalogue for a language nobody
        // publishes under and asked a model to "translate to fr". The tool keeps ISO codes
        // internally — they are what a system reports and what makes a picker sane — and
        // converts on the way out. "auto" is passed through: the mod resolves it itself.
        var language = string.Equals(settings.TargetLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : Languages.NameOf(settings.TargetLanguage);

        intents.Add(new Intent(null, "target_language", language, "language"));
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
        var startsTranslating = perGame?.StartTranslation ?? settings.EnableAi;
        intents.Add(new Intent(null, "enable_ai", startsTranslating,
            startsTranslating ? "auto-translation on" : "auto-translation off"));

        // Only when this game has one, and never cleared by omission: a description written inside
        // the game, in the mod's own options, must survive an install from here.
        if (perGame?.GameContext is { } context)
            intents.Add(new Intent(null, "game_context", context, "what this game is about"));

        // Only written when the mod could act on it. An unparseable hotkey would replace a working
        // one with something that never fires, and the mod reports nothing when that happens —
        // leaving the panel unreachable in a game where it used to open.
        if (BindableKeys.IsValid(settings.SettingsHotkey))
            intents.Add(new Intent(null, "settings_hotkey", settings.SettingsHotkey, "in-game hotkey"));

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
    public ConfigWriteResult Apply(string gamePath, LoaderDescriptor descriptor,
                                   InstallerSettings settings, bool skipWizard = true,
                                   GamePreference? perGame = null)
    {
        var folder = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar));
        var path = Path.Combine(folder, LocalTranslationProbe.ConfigFileName);

        try
        {
            var root = Load(path);
            var applied = new List<string>();

            var intents = Intended(settings, perGame, skipWizard, out var wizardSkipped);

            foreach (var intent in intents)
            {
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
                                                   InstallerSettings settings,
                                                   GamePreference? perGame = null)
    {
        var path = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            LocalTranslationProbe.ConfigFileName);

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
        foreach (var intent in Intended(settings, perGame, skipWizard: false, out _))
        {
            // A key we would only remove has nothing to compare: its absence is our intent.
            if (intent.Value is null) continue;

            // Unnamed keys travel with a named one — proxy_url with proxy_mode, auto_download with
            // the update preferences. Reporting them separately would list six differences for one
            // decision, and none of the six in words anybody chose.
            if (intent.Label is null) continue;

            var node = intent.Parent is null
                ? root[intent.Key]
                : (root[intent.Parent] as JsonObject)?[intent.Key];

            // Absent in the game means the mod is on its default there. That IS a difference worth
            // offering — it is how "your game never learned your hotkey" shows up — but it is
            // worded as absence rather than as a wrong value.
            var inGame = node is null ? null : Read(node, intent.Secret);
            var ours = Render(intent.Value, intent.Secret);

            if (inGame is not null && string.Equals(inGame, ours, StringComparison.Ordinal)) continue;

            differences.Add(new ConfigDifference(intent.Label, inGame ?? "not set", ours));
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
