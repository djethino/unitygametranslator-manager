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

    public void Save(InstallerSettings settings)
    {
        Current = settings;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Written beside the target then moved: a file half-written by a crash would come
            // back as defaults, silently discarding what the user chose.
            // Encrypted on the way out, every time: the only path from memory to disk.
            settings.AiApiKeyStored = SecretProtection.Protect(settings.AiApiKey);

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
