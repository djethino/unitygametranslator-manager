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
            // ⚠ GOG used to be walked one level less than everything else, with nothing saying
            // why — so a GOG game shipped inside a subfolder was invisible where the same layout
            // was found anywhere else. One depth, decided in one place: UnityGameProbe.NestingDepth.
            var games = hint.Store switch
            {
                GameStore.Epic => ScanEpicManifests(hint.Path),
                _ => ScanFolder(hint.Path, hint.Store),
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

            // Same rule as Steam: the manifest points at a folder, and the game is not always at
            // the top of it. Several games found means the manifest names none of them, so the
            // launcher's own id is not attached either — starting the wrong title through Epic is
            // worse than offering no shortcut at all.
            var games = UnityGameProbe.ProbeDeclaredFolder(location, name, GameStore.Epic);

            foreach (var game in games)
            {
                if (games.Count == 1) game.StoreAppId = appName;

                ModdabilityProbe.Evaluate(game);
                yield return game;
            }
        }
    }

    /// <summary>
    /// A plain folder of games, walked to a bounded depth.
    ///
    /// ⚠ The walk itself lives in <see cref="UnityGameProbe.FindGameFolders"/> — it is the same
    /// walk Steam needs, and two copies of "how deep do we look" is how Steam came to have its own
    /// answer of zero.
    /// </summary>
    public static IEnumerable<GameInstall> ScanFolder(string root, GameStore store,
                                                      int maxDepth = UnityGameProbe.NestingDepth)
    {
        foreach (var folder in UnityGameProbe.FindGameFolders(root, maxDepth))
        {
            var game = UnityGameProbe.Probe(folder, null, store);
            if (game is null) continue;

            ModdabilityProbe.Evaluate(game);
            yield return game;
        }
    }
}
