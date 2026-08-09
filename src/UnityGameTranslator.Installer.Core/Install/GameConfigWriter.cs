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
            Set(root, applied, "online_mode", settings.OnlineMode, "community features");

            if (settings.TranslationBackend == "ai")
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
