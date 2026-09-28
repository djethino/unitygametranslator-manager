using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Model;

/// <summary>
/// Which game a pack opened from the file explorer goes to: one found without doubt, or the games
/// to offer first when it is not.
/// </summary>
/// <param name="Found">The one game the pack names, when exactly one does.</param>
/// <param name="Likely">When none is certain: those the pack could mean, to list first.</param>
public sealed record PackTarget(GameInstall? Found, IReadOnlyList<GameInstall> Likely);

public static class PackTargets
{
    /// <summary>
    /// Reads the manifest against the games on this computer.
    ///
    /// ⚠ **"The same game" is AssetPlanner.OtherGame's answer, not a second one.** The Assets tab
    /// warns "Made for X" on that rule; a double-click deciding with another would open a pack on a
    /// game the tab then calls someone else's. So: the Steam id decides when both sides have one,
    /// the name (or the product name) otherwise.
    ///
    /// ⚠ Found only when exactly ONE game matches — two installs of the same game are two choices,
    /// not one. And never a refusal: no identity is reliable enough to refuse on (user, 2026-09-27),
    /// so when nothing matches every game is still offered, similar names first.
    /// </summary>
    public static PackTarget For(PackManifest manifest, IReadOnlyList<GameInstall> games)
    {
        var named = !string.IsNullOrWhiteSpace(manifest.GameName) || !string.IsNullOrWhiteSpace(manifest.SteamId);
        if (!named) return new PackTarget(null, []);

        // OtherGame's null also means "nothing to compare" (a pack naming only a Steam id, a game
        // without one): that is not a match.
        var same = games.Where(game =>
                AssetPlanner.OtherGame(SideOf(game), manifest) is null
                && ((!string.IsNullOrWhiteSpace(manifest.SteamId) && !string.IsNullOrWhiteSpace(game.SteamAppId))
                    || !string.IsNullOrWhiteSpace(manifest.GameName)))
            .ToList();
        if (same.Count == 1) return new PackTarget(same[0], []);
        if (same.Count > 1) return new PackTarget(null, same);

        var wanted = Letters(manifest.GameName);
        if (wanted.Length == 0) return new PackTarget(null, []);

        // Loose: one name inside the other once reduced to letters and digits — "Foo" against
        // "Foo: Deluxe Edition". A suggestion, listed first; the choice stays the person's.
        var similar = games.Where(game => Similar(Letters(game.Name), wanted) || Similar(Letters(game.ProductName), wanted))
                           .ToList();
        return new PackTarget(null, similar);
    }

    private static GameAssetSide SideOf(GameInstall game) => new()
    {
        GameName = game.Name,
        ProductName = game.ProductName,
        SteamId = game.SteamAppId,
    };

    private static bool Similar(string a, string b) =>
        a.Length > 0 && b.Length > 0 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));

    private static string Letters(string? name) =>
        name is null ? "" : new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
