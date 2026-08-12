using System.Text.Json;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Stores other than Steam. They give us a name and a path but no usable game id, so games found
/// here can still be installed — they just cannot be matched against the online catalog until
/// the user tells us which game it is.
/// </summary>
public sealed class StoreScanner
{
    private readonly IPlatform _platform;

    public StoreScanner(IPlatform platform) => _platform = platform;

    public IEnumerable<GameInstall> Scan()
    {
        foreach (var hint in _platform.ExtraGameRoots())
        {
            var games = hint.Store switch
            {
                GameStore.Epic => ScanEpicManifests(hint.Path),
                GameStore.Gog => ScanFolder(hint.Path, GameStore.Gog, maxDepth: 1),
                _ => ScanFolder(hint.Path, hint.Store, maxDepth: 2),
            };

            foreach (var game in games) yield return game;
        }
    }

    /// <summary>Epic writes one JSON manifest per installed game.</summary>
    private static IEnumerable<GameInstall> ScanEpicManifests(string manifestDir)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(manifestDir, "*.item"); }
        catch { yield break; }

        foreach (var file in files)
        {
            string? location = null;
            string? name = null;
            string? appName = null;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var rootElement = doc.RootElement;
                if (rootElement.TryGetProperty("InstallLocation", out var loc)) location = loc.GetString();
                if (rootElement.TryGetProperty("DisplayName", out var dn)) name = dn.GetString();

                // The launcher's own id for this title. Read while the manifest is open: it is
                // what lets the game be started through Epic, which some titles insist on.
                if (rootElement.TryGetProperty("AppName", out var an)) appName = an.GetString();
            }
            catch
            {
                continue; // one malformed manifest must not stop the scan
            }

            if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location)) continue;

            var game = UnityGameProbe.Probe(location, name, GameStore.Epic);
            if (game is null) continue;

            game.StoreAppId = appName;

            ModdabilityProbe.Evaluate(game);
            yield return game;
        }
    }

    /// <summary>
    /// A plain folder of games, walked to a bounded depth.
    ///
    /// Depth 2 is deliberate and empirical: repacked releases routinely nest the real game one
    /// level down (Some.Game.v1.0/game/), so depth 1 misses them entirely. Going deeper turns a
    /// half-second scan into a walk of the whole drive, which is how a scanner becomes a
    /// five-minute wait — so the limit is enforced, not advisory.
    /// </summary>
    public static IEnumerable<GameInstall> ScanFolder(string root, GameStore store, int maxDepth)
    {
        var direct = UnityGameProbe.Probe(root, null, store);
        if (direct is not null)
        {
            ModdabilityProbe.Evaluate(direct);
            yield return direct;
            yield break; // a game folder is a leaf: never look inside it
        }

        if (maxDepth <= 0) yield break;

        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(root); }
        catch { yield break; }

        foreach (var child in children)
        {
            foreach (var game in ScanFolder(child, store, maxDepth - 1)) yield return game;
        }
    }
}
