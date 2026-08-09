using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityGameTranslator.Installer.Core.Detection;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Security;

namespace UnityGameTranslator.Installer.Core.Install;

/// <summary>What was written into a game, in the words used on screen.</summary>
public sealed record ConfigWriteResult(
    bool Written,
    IReadOnlyList<string> Applied,
    bool WizardSkipped,
    string? Failure);

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
/// </summary>
public sealed class GameConfigWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Applies the settings to one game.
    ///
    /// <paramref name="skipWizard"/> only takes effect when the settings answer every question the
    /// mod's first-run wizard asks. Writing first_run_completed on a partial configuration would
    /// leave someone with a mod set to the wrong language and no screen on which to notice it —
    /// the wizard is the safety net, and we only remove it once we have genuinely replaced it.
    /// </summary>
    public ConfigWriteResult Apply(string gamePath, LoaderDescriptor descriptor,
                                   InstallerSettings settings, bool skipWizard = true)
    {
        var folder = Path.Combine(gamePath,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar));
        var path = Path.Combine(folder, LocalTranslationProbe.ConfigFileName);

        try
        {
            var root = Load(path);
            var applied = new List<string>();

            Set(root, applied, "target_language", settings.TargetLanguage, "language");
            Set(root, applied, "translation_backend", settings.TranslationBackend, "translation backend");
            // Only written when the mod could act on it. An unparseable hotkey would replace a
            // working one with something that never fires, and the mod reports nothing when that
            // happens — leaving the panel unreachable in a game where it used to open.
            if (Hotkeys.IsValid(settings.SettingsHotkey))
                Set(root, applied, "settings_hotkey", settings.SettingsHotkey, "in-game hotkey");
            // The mod's own setting, not this tool's. Someone who installed everything from here,
            // translation included, has what they need before the game starts and may not want
            // the mod reaching the network while they play.
            Set(root, applied, "online_mode", settings.ModOnlineMode, "community features in game");

            if (settings.TranslationBackend == "llm")
            {
                Set(root, applied, "ai_url", settings.AiUrl, "AI server");
                Set(root, applied, "ai_model", settings.AiModel, "AI model");
                Set(root, applied, "enable_ai", true, "AI translation");

                // Encrypted with the same scheme, the same constants and the same machine
                // identity as the mod's own TokenProtection — that mirror exists exactly so this
                // line can work. Written in its protected form: the plaintext never reaches disk.
                if (!string.IsNullOrWhiteSpace(settings.AiApiKey))
                    Set(root, applied, "ai_api_key", SecretProtection.Protect(settings.AiApiKey), "API key");
            }
            else if (settings.TranslationBackend == "google")
            {
                // Without the key the backend is written but cannot translate a line, and the
                // failure appears in the game with nothing to explain it.
                if (!string.IsNullOrWhiteSpace(settings.GoogleApiKey))
                    Set(root, applied, "google_api_key", SecretProtection.Protect(settings.GoogleApiKey), "Google key");
            }
            else if (settings.TranslationBackend == "deepl")
            {
                if (!string.IsNullOrWhiteSpace(settings.DeeplApiKey))
                    Set(root, applied, "deepl_api_key", SecretProtection.Protect(settings.DeeplApiKey), "DeepL key");

                // Free and paid DeepL are different hosts; guessing wrong fails every request.
                Set(root, applied, "deepl_use_free", settings.DeeplUseFree, null);
            }
            else if (settings.TranslationBackend == "none")
            {
                // Turned off explicitly rather than left as it was: someone who chose community
                // translations only would otherwise keep an AI running that they thought they had
                // just switched off.
                Set(root, applied, "enable_ai", false, "AI translation (off)");
            }

            // Network settings carry across because the reason to set them is the same on both
            // sides: a proxy that this tool needs is a proxy the mod needs. Only written when
            // they differ from the default, so an untouched game keeps its own arrangement.
            if (settings.ProxyMode != "default")
            {
                Set(root, applied, "proxy_mode", settings.ProxyMode, "proxy");
                Set(root, applied, "proxy_url", settings.ProxyUrl, null);
                Set(root, applied, "proxy_username", settings.ProxyUsername, null);
                Set(root, applied, "proxy_bypass_local", settings.ProxyBypassLocal, null);

                if (!string.IsNullOrWhiteSpace(settings.ProxyPassword))
                    Set(root, applied, "proxy_password", SecretProtection.Protect(settings.ProxyPassword), null);
            }

            // The channel picked here decides which plugin build is installed; the mod has its own
            // switch for which releases it announces. Leaving them apart meant choosing beta and
            // still being told only about stable ones.
            if (settings.Channel == "beta")
                SetNested(root, applied, "sync", "notify_prereleases", true, "beta update notices");

            // All inside the mod's own "sync" block, set one key at a time so everything else it
            // holds — ignored_uuids, last_seen_mod_version, per-game state — survives untouched.
            SetNested(root, applied, "sync", "auto_download", settings.AutoDownload, null);
            SetNested(root, applied, "sync", "notify_updates", settings.NotifyUpdates, null);
            SetNested(root, applied, "sync", "check_mod_updates", settings.CheckModUpdates, null);
            SetNested(root, applied, "sync", "merge_strategy", settings.MergeStrategy, "update preferences");
            SetNested(root, applied, "sync", "notifications_enabled", settings.NotificationsEnabled, null);
            SetNested(root, applied, "sync", "notification_position", settings.NotificationPosition, "notifications");

            var wizardSkipped = skipWizard && settings.AnswersTheWizard;
            if (wizardSkipped) Set(root, applied, "first_run_completed", true, "first-run wizard skipped");

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
                                  object value, string? label)
    {
        if (root[parent] is not JsonObject nested)
        {
            nested = new JsonObject();
            root[parent] = nested;
        }

        nested[key] = value switch
        {
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
