namespace UnityGameTranslator.Manager.Core.Catalog;

/// <summary>
/// Who each loader is published by — compiled in, never read from the catalog.
///
/// 🔴 **The catalog says WHAT, this file says WHERE.** The catalog is fetched at every launch
/// (GitHub, then the site mirror, then a cache; the embedded copy is the fourth rung and is not
/// reached in normal use). So anything it names is a remote string that reaches every installation
/// at the next launch, with no release, no version and no act of publication in between — unlike a
/// binary, which somebody has to decide to update.
///
/// It used to name the addresses too: `github.repo`, `sources[].repo`, `sources[].url`. Whoever
/// could edit the catalog could therefore redirect the download, and the checksum was no help —
/// it is published in the same file, so it comes out consistent with whatever the URL points at,
/// and an absent checksum is not fatal by design (Bleeding Edge publishes none). What lands there
/// is written into a game folder, which is to say into code the game loads at startup.
///
/// With the addresses here, a tampered catalog can no longer redirect anything. The worst it can
/// still do is name a different FILE at the right publisher — installing a wrong version of
/// BepInEx rather than running arbitrary code. That is a change of nature, not of degree.
///
/// ⚠ Adding a loader means editing this file, so it means a release. That is the point: which
/// publishers we trust is not the kind of decision that should be able to travel silently.
/// </summary>
public static class LoaderOrigins
{
    private sealed record Origin(string? GitHubRepo, string? BuildsPage);

    /// <summary>
    /// ⚠ Keyed by the catalog's loader id. An id it does not know resolves to nothing, and every
    /// caller then falls back to what the catalog pinned — never to an address it supplied.
    /// </summary>
    private static readonly Dictionary<string, Origin> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bepinex5"] = new("BepInEx/BepInEx", null),

        // BepInEx 6 has no stable release at all: its GitHub page stopped at a pre-release in
        // August 2024 while development continued in Bleeding Edge builds. Both are BepInEx's own
        // infrastructure — builds.bepinex.dev is their CI, not a third party.
        ["bepinex6-mono"] = new("BepInEx/BepInEx", "https://builds.bepinex.dev/projects/bepinex_be"),
        ["bepinex6-il2cpp"] = new("BepInEx/BepInEx", "https://builds.bepinex.dev/projects/bepinex_be"),

        ["melonloader"] = new("LavaGang/MelonLoader", null),
    };

    /// <summary>Hosts a loader archive may come from. Nothing else is downloaded and unpacked.</summary>
    private static readonly string[] AllowedHosts =
    {
        "github.com",
        // Where a GitHub release download redirects. HttpClient follows it on its own, so the
        // address we check is the github.com one — this is here for a publisher that hands out the
        // final address directly.
        "objects.githubusercontent.com",
        "builds.bepinex.dev",
    };

    public static string? GitHubRepoFor(string loaderId) =>
        Known.TryGetValue(loaderId, out var origin) ? origin.GitHubRepo : null;

    public static string? BuildsPageFor(string loaderId) =>
        Known.TryGetValue(loaderId, out var origin) ? origin.BuildsPage : null;

    /// <summary>
    /// Whether an archive address is one we accept, whoever produced it.
    ///
    /// ⚠ Applied to the FINAL address rather than to its provenance, and that is deliberate. An
    /// asset URL can legitimately come from the publisher's own API — GitHub returns
    /// `browser_download_url`, the Bleeding Edge page carries hrefs — so the two travel through the
    /// same field as anything the catalog might have named. Checking where a string came from is a
    /// thing to get wrong once; checking the address itself cannot be bypassed by adding a path.
    ///
    /// ⚠ Host, not prefix: "https://github.com.attacker.net/..." starts with the right characters
    /// and is a different machine. Uri parses the authority, string comparison does not.
    /// </summary>
    public static bool IsAllowedDownload(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        // Plain HTTP would let anyone on the path swap the archive, and every publisher here
        // serves TLS.
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return false;

        // On GitHub, the host is shared by everybody. Being on github.com says nothing — the
        // repository does, and it must be one of ours.
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return IsKnownRepositoryPath(uri.AbsolutePath);

        return true;
    }

    private static bool IsKnownRepositoryPath(string absolutePath)
    {
        foreach (var origin in Known.Values)
        {
            if (origin.GitHubRepo is null) continue;

            if (absolutePath.StartsWith($"/{origin.GitHubRepo}/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
