using System.Text.Json;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Settings;

/// <summary>
/// Reads and writes the defaults applied to every game.
///
/// Deliberately dull: a missing or damaged file falls back to defaults rather than refusing to
/// start, because losing a language preference is an annoyance while failing to launch is not.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly IPlatform _platform;

    public SettingsStore(IPlatform platform)
    {
        _platform = platform;
        _path = Path.Combine(platform.UserDataDirectory, InstallerSettings.FileName);
        Current = Load();

        // Drawn on the first run and read on every one after, here because this is the first thing
        // built that knows where this machine's data lives — and because a client made before it is
        // a call that does not say which machine it comes from. See MachineIdentity: it is a random
        // number, and deliberately not anything measured about the machine.
        Net.Http.DeviceId = MachineIdentity.ReadOrCreate(platform);
    }

    public InstallerSettings Current { get; private set; }

    private InstallerSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<InstallerSettings>(
                    File.ReadAllText(_path), JsonOptions);

                if (loaded is not null)
                {
                    // Decrypted into memory, and the stored form left alone: a file written on
                    // another machine cannot be read here, and must come back as "no key"
                    // rather than as a crash or as garbage sent to a provider.
                    // ⚠ A blank here is not a choice, it is a field written before there was
                    // anything to write. Left blank, the channel is decided by whichever source
                    // happens to sit first in the catalogue — so reordering a JSON array would
                    // silently move everybody to another stream. The declared default answers it.
                    if (string.IsNullOrWhiteSpace(loaded.BepInEx6Channel))
                        loaded.BepInEx6Channel = new InstallerSettings().BepInEx6Channel;

                    loaded.AiApiKey = Secrets.Unprotect(loaded.AiApiKeyStored);
                    loaded.ProxyPassword = Secrets.Unprotect(loaded.ProxyPasswordStored);
                    loaded.GoogleApiKey = Secrets.Unprotect(loaded.GoogleApiKeyStored);
                    loaded.DeeplApiKey = Secrets.Unprotect(loaded.DeeplApiKeyStored);
                    loaded.ApiToken = Secrets.Unprotect(loaded.ApiTokenStored);

                    // A token belongs to the server that issued it. If the tool now points
                    // somewhere else, keeping it would send someone's credential to a site that
                    // never made it — the mod guards the same way.
                    if (loaded.ApiToken is not null
                        && !string.IsNullOrWhiteSpace(loaded.ApiTokenServer)
                        && !string.Equals(loaded.ApiTokenServer, BuildInfo.ApiBaseUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        loaded.ApiToken = null;
                        loaded.ApiTokenStored = null;
                        loaded.ApiUser = null;
                        loaded.ApiTokenServer = null;
                    }

                    // Applied before anything can make a request. Every HttpClient in the tool is
                    // built from these, so a proxy configured once holds everywhere — a partial
                    // application would produce "search works, install does not", which is close
                    // to undiagnosable from the outside.
                    // Settings written before the backend name was corrected say "ai" where the
                    // mod expects "llm". Left alone, they would keep producing game configs the
                    // mod cannot act on, silently — so they are moved on read rather than asking
                    // the user to notice and redo it.
                    if (loaded.TranslationBackend == "ai") loaded.TranslationBackend = "llm";

                    ApplyNetworkSettings(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // A damaged file must not stop the tool from running.
        }

        return new InstallerSettings();
    }

    /// <summary>
    /// Pushes the proxy configuration into the HTTP factory. Called on load and on save, because
    /// a proxy someone just configured has to work without restarting the tool — the situation
    /// they are in is precisely that nothing is reaching the network yet.
    /// </summary>
    public static void ApplyNetworkSettings(InstallerSettings settings) =>
        Net.Http.Proxy = new Net.ProxySettings(
            settings.ProxyMode,
            settings.ProxyUrl,
            settings.ProxyUsername,
            settings.ProxyPassword,
            settings.ProxyBypassLocal);

    /// <summary>
    /// ⚠ APPLY WHAT A SCREEN OWNS ONTO <see cref="Current"/> AND SAVE THAT — never a copy of your
    /// own making.
    ///
    /// The whole object is persisted, so saving a partial copy erases every field the copy did not
    /// think to carry. It happened: the defaults window built a copy of the twenty-two values it
    /// edits, and each Apply silently wiped the account token, the name behind it and the server
    /// that issued it — signing people out from a window that has nothing to do with accounts, at
    /// a moment they could not connect to anything they had done.
    ///
    /// Writing onto Current cannot lose a setting a screen has never heard of, which is the only
    /// version of this that stays correct as fields are added.
    /// </summary>
    public void Save(InstallerSettings settings)
    {
        Current = settings;
        ApplyNetworkSettings(settings);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Written beside the target then moved: a file half-written by a crash would come
            // back as defaults, silently discarding what the user chose.
            // Encrypted on the way out, every time: the only path from memory to disk.
            settings.AiApiKeyStored = Secrets.Protect(settings.AiApiKey);
            settings.ProxyPasswordStored = Secrets.Protect(settings.ProxyPassword);
            settings.GoogleApiKeyStored = Secrets.Protect(settings.GoogleApiKey);
            settings.DeeplApiKeyStored = Secrets.Protect(settings.DeeplApiKey);
            settings.ApiTokenStored = Secrets.Protect(settings.ApiToken);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Failing to persist must not lose the choice for this session.
        }
    }

    /// <summary>
    /// The language to reason with: the configured one, or the system's when set to "auto".
    /// Returns the canonical code for the language, lowercase — two letters for most, more
    /// where the language needs it ("zh-tw").
    /// </summary>
    public string ResolveTargetLanguage() =>
        GameLanguages.Resolve(Current.TargetLanguage, _platform.SystemLanguage());
}
