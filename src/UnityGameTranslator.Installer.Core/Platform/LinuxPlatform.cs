using System.Runtime.InteropServices;
using UnityGameTranslator.Installer.Core.Model;

namespace UnityGameTranslator.Installer.Core.Platform;

/// <summary>
/// Linux, including SteamOS / Steam Deck.
///
/// Two things make Linux different from Windows and both are handled here rather than leaking
/// into Core: Steam lives in several possible places (native, Flatpak, SD card), and most games
/// are Windows builds running through Proton — which need a Wine DLL override to load anything.
/// </summary>
public sealed class LinuxPlatform : IPlatform
{
    public string OsId => "linux";

    public GameArchitecture HostArchitecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => GameArchitecture.X64,
        System.Runtime.InteropServices.Architecture.X86 => GameArchitecture.X86,
        System.Runtime.InteropServices.Architecture.Arm64 => GameArchitecture.Arm64,
        _ => GameArchitecture.Unknown,
    };

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public IEnumerable<string> SteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var candidates = new List<string>
        {
            Path.Combine(Home, ".steam", "steam"),
            Path.Combine(Home, ".steam", "root"),
            Path.Combine(Home, ".local", "share", "Steam"),
            // Flatpak Steam keeps its own tree.
            Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
        };

        foreach (var path in candidates)
        {
            if (!seen.Add(path)) continue;
            if (Directory.Exists(Path.Combine(path, "steamapps"))) yield return path;
        }

        // Steam Deck: SD cards and external drives are mounted here and hold a full library.
        foreach (var mountRoot in new[] { "/run/media", "/media" })
        {
            if (!Directory.Exists(mountRoot)) continue;

            IEnumerable<string> mounts;
            try { mounts = Directory.EnumerateDirectories(mountRoot); }
            catch { continue; }

            foreach (var mount in mounts)
            {
                // /run/media/<user>/<card> on some builds, /run/media/<card> on others.
                foreach (var candidate in new[] { mount }.Concat(SafeDirectories(mount)))
                {
                    if (!seen.Add(candidate)) continue;
                    if (Directory.Exists(Path.Combine(candidate, "steamapps"))) yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    public IEnumerable<GameRootHint> ExtraGameRoots()
    {
        // Heroic is how Epic and GOG games usually land on Linux. Its config lists the installs.
        var heroic = Path.Combine(Home, ".config", "heroic");
        if (Directory.Exists(heroic))
            yield return new GameRootHint(heroic, GameStore.Unknown);

        var games = Path.Combine(Home, "Games");
        if (Directory.Exists(games))
            yield return new GameRootHint(games, GameStore.Manual);
    }

    public string UserDataDirectory
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var baseDir = string.IsNullOrEmpty(xdg) ? Path.Combine(Home, ".local", "share") : xdg;
            return Path.Combine(baseDir, "unitygametranslator-installer");
        }
    }

    /// <summary>
    /// SteamOS keeps / read-only, so the tool can only ever live in the user's home. That is
    /// also the right place on any other distribution for a single-user tool.
    /// </summary>
    public string SelfInstallDirectory =>
        Path.Combine(Home, ".local", "share", "unitygametranslator-installer", "bin");

    public string ExecutableFileName => "unitygametranslator-installer";

    /// <summary>
    /// The .NET *Desktop* runtime is a Windows-only product. For a Proton game the runtime that
    /// matters lives inside the prefix, which we cannot inspect reliably — so we answer "unknown"
    /// and let the caller warn instead of blocking on a check it cannot make.
    /// </summary>
    public bool? HasDotnetDesktopRuntime(string majorVersion) => null;

    public bool NeedsDllOverride(GameInstall game) => game.RunsUnderProton;

    public bool IsGameRunning(GameInstall game)
    {
        if (string.IsNullOrEmpty(game.Path) || !Directory.Exists("/proc")) return false;

        var root = Path.GetFullPath(game.Path).TrimEnd('/') + "/";

        foreach (var dir in SafeDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out _)) continue;

            try
            {
                var exeLink = Path.Combine(dir, "exe");
                var target = File.ResolveLinkTarget(exeLink, returnFinalTarget: true)?.FullName;
                if (target is not null && target.StartsWith(root, StringComparison.Ordinal)) return true;
            }
            catch
            {
                // Other users' processes are unreadable; that is expected.
            }
        }
        return false;
    }
}
