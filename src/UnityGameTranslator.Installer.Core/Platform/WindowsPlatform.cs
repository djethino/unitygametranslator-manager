using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using UnityGameTranslator.Installer.Core.Model;

namespace UnityGameTranslator.Installer.Core.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatform
{
    public string OsId => "windows";

    public GameArchitecture HostArchitecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => GameArchitecture.X64,
        System.Runtime.InteropServices.Architecture.X86 => GameArchitecture.X86,
        System.Runtime.InteropServices.Architecture.Arm64 => GameArchitecture.Arm64,
        _ => GameArchitecture.Unknown,
    };

    public IEnumerable<string> SteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The registry is authoritative: it survives a Steam installed off the default drive.
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
        {
            var path = ReadRegistry(RegistryHive.LocalMachine, key, "InstallPath");
            if (path is not null && seen.Add(path) && Directory.Exists(path)) yield return path;
        }

        var userPath = ReadRegistry(RegistryHive.CurrentUser, @"SOFTWARE\Valve\Steam", "SteamPath");
        if (userPath is not null && seen.Add(userPath) && Directory.Exists(userPath)) yield return userPath;

        // Fallbacks for a broken or absent registry entry.
        foreach (var guess in new[]
                 {
                     Path.Combine(Env(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                     Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), "Steam"),
                 })
        {
            if (seen.Add(guess) && Directory.Exists(guess)) yield return guess;
        }
    }

    public IEnumerable<GameRootHint> ExtraGameRoots()
    {
        // Epic keeps one JSON manifest per installed game; the scanner reads them, we only
        // point at the folder.
        var epicManifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(epicManifests))
            yield return new GameRootHint(epicManifests, GameStore.Epic);

        foreach (var gog in GogRoots())
            yield return new GameRootHint(gog, GameStore.Gog);

        // Launchers' own default install locations. These hold games even when the launcher's
        // manifests are missing or the launcher itself has been uninstalled.
        //
        // Nothing beyond these is guessed. Scanning every drive for folders named "Games" was
        // tried and removed: such a folder is someone's personal way of organising a library,
        // not a convention, and a tool that assumes it is right on one machine and wrong on the
        // next. Anything else the user adds explicitly, and it is remembered (see CustomFolders).
        foreach (var baseFolder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            foreach (var name in new[] { "Epic Games", @"GOG Galaxy\Games" })
            {
                var path = Path.Combine(Env(baseFolder), name);
                if (Directory.Exists(path)) yield return new GameRootHint(path, GameStore.Manual);
            }
        }
    }

    private static IEnumerable<string> GogRoots()
    {
        // GOG Galaxy records each game under its own key with a "path" value.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            RegistryKey? games = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                games = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                if (games is null) continue;

                foreach (var id in games.GetSubKeyNames())
                {
                    string? path = null;
                    try
                    {
                        using var game = games.OpenSubKey(id);
                        path = game?.GetValue("path") as string;
                    }
                    catch
                    {
                        // A single unreadable key must not stop the enumeration.
                    }
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) yield return path;
                }
            }
            finally
            {
                games?.Dispose();
            }
        }
    }

    public string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnityGameTranslator", "Installer");

    public string SelfInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "UnityGameTranslator Installer");

    public string ExecutableFileName => "UnityGameTranslatorInstaller.exe";

    public bool? HasDotnetDesktopRuntime(string majorVersion)
    {
        // The shared framework folder is the ground truth; `dotnet --list-runtimes` needs the
        // CLI on PATH, which a player machine may not have.
        foreach (var root in new[]
                 {
                     Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
                     Path.Combine(Env(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(majorVersion + ".", StringComparison.Ordinal)) return true;
            }
        }

        // Absent folder is weak evidence, not proof: report "no" only if dotnet exists at all.
        var dotnetPresent =
            Directory.Exists(Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), "dotnet")) ||
            Directory.Exists(Path.Combine(Env(Environment.SpecialFolder.ProgramFilesX86), "dotnet"));
        return dotnetPresent ? false : null;
    }

    /// <summary>Windows loads the proxy DLL natively — no override needed, ever.</summary>
    public bool NeedsDllOverride(GameInstall game) => false;

    public bool IsGameRunning(GameInstall game)
    {
        if (string.IsNullOrEmpty(game.Path)) return false;
        var root = NormalizeRoot(game.Path);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var file = process.MainModule?.FileName;
                if (file is null) continue;
                if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch
            {
                // Access denied on system processes is expected and irrelevant here.
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    private static string NormalizeRoot(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetUserDefaultLocaleName(
        System.Text.StringBuilder localeName, int capacity);

    public string? SystemLanguage()
    {
        try
        {
            // LOCALE_NAME_MAX_LENGTH is 85; the value looks like "fr-FR".
            var buffer = new System.Text.StringBuilder(85);
            if (GetUserDefaultLocaleName(buffer, buffer.Capacity) == 0) return null;

            var locale = buffer.ToString();
            var dash = locale.IndexOf('-');
            var language = dash > 0 ? locale[..dash] : locale;
            return language.Length >= 2 ? language[..2].ToLowerInvariant() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read from the display adapter keys in the registry.
    ///
    /// qwMemorySize rather than WMI's Win32_VideoController.AdapterRAM: the WMI value is a 32-bit
    /// field and caps at 4 GB, so every card above that reports 4 GB — which would push someone
    /// with a 16 GB card towards a tiny model for no reason.
    ///
    /// The largest adapter wins. Laptops routinely expose both an integrated chip sharing system
    /// memory and a discrete card; the integrated one comes first in the enumeration and is not
    /// the one that will run the model.
    /// </summary>
    public long? VideoMemoryBytes()
    {
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (adapters is null) return null;

            long largest = 0;

            foreach (var name in adapters.GetSubKeyNames())
            {
                // Only the numbered device keys hold adapters; siblings like "Configuration" do not.
                if (name.Length != 4 || !name.All(char.IsDigit)) continue;

                using var adapter = adapters.OpenSubKey(name);
                if (adapter?.GetValue("HardwareInformation.qwMemorySize") is not { } raw) continue;

                var bytes = raw switch
                {
                    long value => value,
                    int value => (long)value,
                    byte[] buffer when buffer.Length >= 8 => BitConverter.ToInt64(buffer, 0),
                    _ => 0L,
                };

                if (bytes > largest) largest = bytes;
            }

            return largest > 0 ? largest : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Env(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder);

    private static string? ReadRegistry(RegistryHive hive, string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }
}
