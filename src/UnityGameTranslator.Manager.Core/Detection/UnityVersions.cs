using System.Text.RegularExpressions;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// A Unity version as games and Unity's own files write it — "2021.3.6f1", "6000.0.50f1", or the
/// bare "2021.3.6" a player's file version gives.
/// </summary>
/// <param name="Kind">'a', 'b', 'f' or 'p' — alpha, beta, final, patch — or null when not written.</param>
/// <param name="Build">The number after the kind ("1" in "f1"), or null.</param>
public sealed record UnityVersion(int Major, int Minor, int Patch, char? Kind, int? Build)
{
    /// <summary>"2021.3" — the branch, inside which Unity keeps its engine interfaces stable.</summary>
    public string Branch => $"{Major}.{Minor}";

    /// <summary>"2021.3.6" — what "the same version" means for engine modules (see <see cref="UnityVersions.SameRelease"/>).</summary>
    public string Release => $"{Major}.{Minor}.{Patch}";

    public override string ToString() => Kind is null ? Release : $"{Release}{Kind}{Build}";
}

public static class UnityVersions
{
    private static readonly Regex Pattern =
        new(@"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:(?<kind>[abfp])(?<build>\d+))?", RegexOptions.CultureInvariant);

    /// <summary>Null when the text is not a Unity version — nothing is guessed from it then.</summary>
    public static UnityVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = Pattern.Match(text.Trim());
        if (!match.Success) return null;

        return new UnityVersion(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
            match.Groups["kind"].Success ? match.Groups["kind"].Value[0] : null,
            match.Groups["build"].Success ? int.Parse(match.Groups["build"].Value) : null);
    }

    /// <summary>
    /// Same branch and same patch number: "2022.3.62f2" and "2022.3.62f3" are one release.
    ///
    /// ⚠ Measured, not assumed (2026-09-21): a game on 2022.3.62f2 ran with the complete engine
    /// modules of a 2022.3.62f3 game. The suffix is a re-issue of the same release; the patch
    /// number is what moves the engine's interfaces.
    /// </summary>
    public static bool SameRelease(UnityVersion a, UnityVersion b) =>
        a.Major == b.Major && a.Minor == b.Minor && a.Patch == b.Patch;

    /// <summary>Same branch, lower patch — the only other release whose engine modules may serve.</summary>
    public static bool OlderInBranch(UnityVersion candidate, UnityVersion game) =>
        candidate.Major == game.Major && candidate.Minor == game.Minor && candidate.Patch < game.Patch;
}
