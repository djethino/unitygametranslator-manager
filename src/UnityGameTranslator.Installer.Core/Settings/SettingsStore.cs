using System.Text.Json;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;
using UnityGameTranslator.Installer.Core.Security;

namespace UnityGameTranslator.Installer.Core.Settings;

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
                    loaded.AiApiKey = SecretProtection.Unprotect(loaded.AiApiKeyStored);
                    loaded.ProxyPassword = SecretProtection.Unprotect(loaded.ProxyPasswordStored);
                    loaded.GoogleApiKey = SecretProtection.Unprotect(loaded.GoogleApiKeyStored);
                    loaded.DeeplApiKey = SecretProtection.Unprotect(loaded.DeeplApiKeyStored);

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
            settings.AiApiKeyStored = SecretProtection.Protect(settings.AiApiKey);
            settings.ProxyPasswordStored = SecretProtection.Protect(settings.ProxyPassword);
            settings.GoogleApiKeyStored = SecretProtection.Protect(settings.GoogleApiKey);
            settings.DeeplApiKeyStored = SecretProtection.Protect(settings.DeeplApiKey);

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
    /// Returns a two-letter code, lowercase.
    /// </summary>
    public string ResolveTargetLanguage()
    {
        var configured = Current.TargetLanguage;

        if (!string.IsNullOrWhiteSpace(configured)
            && !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return Normalise(configured);
        }

        // Asked of the OS, not of CultureInfo: invariant globalization makes the latter answer
        // "iv", which showed up as "No iv translation yet" on every row.
        var system = _platform.SystemLanguage();
        return system is not null ? Normalise(system) : "en";
    }

    private static string Normalise(string language) =>
        language.Trim().ToLowerInvariant() is { Length: >= 2 } value ? value[..2] : "en";
}
