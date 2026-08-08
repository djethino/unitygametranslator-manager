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

        foreach (var folder in ConventionalGameFolders())
            yield return new GameRootHint(folder, GameStore.Manual);
    }

    /// <summary>
    /// Games installed outside any launcher. Found by walking the drives that exist on this
    /// machine and looking for the handful of folder names people actually use — never from a
    /// list of hardcoded paths, which would work on one machine and no other.
    /// </summary>
    private static IEnumerable<string> ConventionalGameFolders()
    {
        string[] names = { "Games", "Jeux", "GOG Games", "Epic Games" };

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (var drive in drives)
        {
            bool usable;
            try { usable = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable; }
            catch { continue; }
            if (!usable) continue;

            foreach (var name in names)
            {
                var path = Path.Combine(drive.RootDirectory.FullName, name);
                if (Directory.Exists(path)) yield return path;
            }
        }

        // Launchers' own default locations, which hold games even when their manifests are gone.
        foreach (var baseFolder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            foreach (var name in new[] { "Epic Games", "GOG Galaxy\\Games" })
            {
                var path = Path.Combine(Env(baseFolder), name);
                if (Directory.Exists(path)) yield return path;
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
