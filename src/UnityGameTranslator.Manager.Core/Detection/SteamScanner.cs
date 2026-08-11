using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Steam is the only store that hands us the app id for free, in appmanifest_*.acf — and the
/// app id is the key into our online translation catalog. That makes this scanner the one that
/// unlocks "this game already has a community translation", which no other tool can offer.
/// </summary>
public sealed class SteamScanner
{
    private readonly IPlatform _platform;

    public SteamScanner(IPlatform platform) => _platform = platform;

    /// <summary>Every Steam library folder found across every Steam installation.</summary>
    public IEnumerable<string> EnumerateLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in _platform.SteamRoots())
        {
            var steamApps = Path.Combine(steamRoot, "steamapps");
            if (Directory.Exists(steamApps) && seen.Add(steamApps)) yield return steamApps;

            // libraryfolders.vdf lists every other drive/partition the user added. Steam has
            // kept it in two places across versions, so both are tried.
            var manifests = new[]
            {
                Path.Combine(steamApps, "libraryfolders.vdf"),
                Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
            };

            foreach (var manifest in manifests)
            {
                var root = VdfParser.ParseFile(manifest);
                var folders = root?["libraryfolders"];
                if (folders is null) continue;

                foreach (var entry in folders.Children.Values)
                {
                    // Modern format: { "path" "D:\\SteamLibrary" ... }. Older: "1" "D:\\SteamLibrary".
                    var path = entry.IsLeaf ? entry.Value : entry.GetString("path");
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    var candidate = Path.Combine(path, "steamapps");
                    if (Directory.Exists(candidate) && seen.Add(candidate)) yield return candidate;
                }
            }
        }
    }

    /// <summary>Every installed app, whether or not it is a Unity game.</summary>
    public IEnumerable<SteamApp> EnumerateApps()
    {
        foreach (var library in EnumerateLibraries())
        {
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(library, "appmanifest_*.acf"); }
            catch { continue; }

            foreach (var manifestPath in manifests)
            {
                var root = VdfParser.ParseFile(manifestPath);
                var state = root?["AppState"];
                if (state is null) continue;

                var appId = state.GetString("appid");
                var name = state.GetString("name");
                var installDir = state.GetString("installdir");
                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(installDir)) continue;

                var gamePath = Path.Combine(library, "common", installDir);
                if (!Directory.Exists(gamePath)) continue;

                var compatData = Path.Combine(library, "compatdata", appId);

                yield return new SteamApp(
                    AppId: appId,
                    Name: string.IsNullOrWhiteSpace(name) ? installDir : name,
                    Path: gamePath,
                    LibraryPath: library,
                    ProtonPrefix: Directory.Exists(compatData) ? compatData : null);
            }
        }
    }

    /// <summary>Installed Steam apps that turn out to be Unity games.</summary>
    public IEnumerable<GameInstall> Scan()
    {
        foreach (var app in EnumerateApps())
        {
            var game = UnityGameProbe.Probe(app.Path, app.Name, GameStore.Steam, app.AppId);
            if (game is null) continue;

            if (app.ProtonPrefix is not null)
            {
                game.ProtonPrefix = app.ProtonPrefix;
                // A prefix exists for every app Steam ever launched through Proton, including
                // native Linux ones in some setups. The deciding evidence is the game itself
                // being a Windows build.
                game.RunsUnderProton = _platform.OsId != "windows" && IsWindowsBuild(game);
            }

            ModdabilityProbe.Evaluate(game);
            yield return game;
        }
    }

    private static bool IsWindowsBuild(GameInstall game) =>
        game.ExecutablePath?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
        || File.Exists(Path.Combine(game.Path, "UnityPlayer.dll"));
}

public readonly record struct SteamApp(
    string AppId,
    string Name,
    string Path,
    string LibraryPath,
    string? ProtonPrefix);
