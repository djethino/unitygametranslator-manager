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

    /// <summary>%TEMP%, which Windows already gives each account separately.</summary>
    public string RuntimeStateDirectory => Path.GetTempPath();

    public string SelfInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "UnityGameTranslator Installer");

    public string ExecutableFileName => "UnityGameTranslatorInstaller.exe";

    public IReadOnlyList<LauncherKind> LauncherKinds => [LauncherKind.Menu, LauncherKind.Desktop];

    /// <summary>
    /// Writes a .lnk through the shell's own shortcut object.
    ///
    /// A .lnk is a binary format nobody should be writing by hand, and the one component that has
    /// created them correctly on every Windows since forever is the shell itself. Reached by late
    /// binding rather than by a COM interop assembly: it is four calls, and adding a dependency to
    /// a single-file build for four calls is a poor trade.
    /// </summary>
    public IReadOnlyList<string> CreateLauncher(LauncherKind kind, string executable)
    {
        var folder = kind == LauncherKind.Desktop
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                           "Programs");

        if (string.IsNullOrEmpty(folder)) return [];

        var path = Path.Combine(folder, "UnityGameTranslator Installer.lnk");

        try
        {
            Directory.CreateDirectory(folder);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return [];

            var shell = Activator.CreateInstance(shellType);
            if (shell is null) return [];

            var shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
            if (shortcut is null) return [];

            var type = shortcut.GetType();
            void Set(string name, object value) => type.InvokeMember(name,
                System.Reflection.BindingFlags.SetProperty, null, shortcut, [value]);

            Set("TargetPath", executable);
            Set("WorkingDirectory", Path.GetDirectoryName(executable) ?? "");
            Set("Description", "Set up UnityGameTranslator in your Unity games");
            Set("IconLocation", executable + ",0");

            type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

            return File.Exists(path) ? [path] : [];
        }
        catch
        {
            // A locked shell, a policy, a folder we may not write to. The tool is installed and
            // runnable either way; the caller says which part did not happen.
            return [];
        }
    }

    /// <summary>
    /// The per-user uninstall key — the same place Windows reads its "Installed apps" list from.
    ///
    /// ⚠ Per-user (HKCU) because the installation is per-user: writing to the machine-wide list
    /// would need elevation and would claim an installation other accounts cannot actually run.
    ///
    /// One fixed key name, so calling this again updates the entry instead of adding another. That
    /// is not a detail: a tool that appears three times in Windows' list after two updates looks
    /// exactly like something that does not clean up after itself.
    /// </summary>
    public string? RegisterInstalled(ToolInstallation installation)
    {
        const string parent = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        const string name = "UnityGameTranslatorInstaller";

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{parent}\{name}", writable: true);
            if (key is null) return null;

            key.SetValue("DisplayName", "UnityGameTranslator Installer");
            key.SetValue("DisplayVersion", installation.Version);
            key.SetValue("Publisher", "ASymptOmatik Games");
            key.SetValue("InstallLocation", installation.Directory);
            key.SetValue("DisplayIcon", installation.Executable);

            // Opens the removal window rather than deleting behind the person's back: the button
            // in Windows' own settings has to lead to the same three questions as ours.
            key.SetValue("UninstallString", $"\"{installation.Executable}\" --remove");

            // No QuietUninstallString on purpose: there is no silent path, and advertising one we
            // do not have would let Windows remove the tool without a word.
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("URLInfoAbout", BuildInfo.WebsiteBaseUrl);

            var size = SizeInKilobytes(installation);
            if (size > 0) key.SetValue("EstimatedSize", size, RegistryValueKind.DWord);

            return $@"HKCU\{parent}\{name}";
        }
        catch
        {
            return null;
        }
    }

    public bool IsRegistered(string registration)
    {
        const string prefix = @"HKCU\";
        if (!registration.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registration[prefix.Length..]);
            return key is not null;
        }
        catch
        {
            // Unreadable is not the same as absent, and claiming an installation is broken on the
            // strength of a permissions error would send somebody repairing what is not wrong.
            return true;
        }
    }

    public void UnregisterInstalled(string registration)
    {
        const string prefix = @"HKCU\";
        if (!registration.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(registration[prefix.Length..], throwOnMissingSubKey: false);
        }
        catch
        {
            // Already gone, or not ours to delete. Either way there is nothing left to do.
        }
    }

    private static int SizeInKilobytes(ToolInstallation installation)
    {
        try
        {
            var total = installation.Files
                .Where(File.Exists)
                .Sum(file => new FileInfo(file).Length);

            return (int)Math.Min(total / 1024, int.MaxValue);
        }
        catch
        {
            return 0;
        }
    }

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
