using System.Text.Json;
using UnityGameTranslator.Installer.Core.Net;

namespace UnityGameTranslator.Installer.Core.Install;

public enum ReleaseChannel
{
    /// <summary>Published releases only — what everyone gets.</summary>
    Stable,

    /// <summary>Includes pre-releases, for users who opted into testing.</summary>
    Beta,
}

public sealed record PublishedRelease(
    string Version,
    string TagName,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt,
    IReadOnlyDictionary<string, string> Assets)
{
    /// <summary>Size in bytes of each asset, so a download can be priced before it starts.</summary>
    public IReadOnlyDictionary<string, long> AssetSizes { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Finds a release to install, and the checksum that proves the download is intact.
///
/// Serves two repositories with one implementation: the mod's, and this tool's own. They are read
/// exactly the same way, and writing it twice would mean fixing GitHub's next change twice —
/// which is the shape of bug that only shows up on one of the two paths, months later.
///
/// Both publish a .sha256 next to every archive, so verification comes for free: we fetch the
/// small checksum file and hold the archive to it. Nothing is trusted because it came from the
/// right domain.
/// </summary>
public sealed class GitHubReleaseClient
{
    private readonly HttpClient _http;
    private readonly string _releasesApi;

    public GitHubReleaseClient(string releasesApi, HttpClient? http = null)
    {
        _releasesApi = releasesApi;
        _http = http ?? Http.Create(TimeSpan.FromSeconds(30));
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"UnityGameTranslatorInstaller/{BuildInfo.Version}");
        }
    }

    /// <summary>Releases of the mod — the plugin installed into games.</summary>
    public static GitHubReleaseClient ForMod(HttpClient? http = null) =>
        new(BuildInfo.ModReleasesApi, http);

    /// <summary>Releases of this tool itself.</summary>
    public static GitHubReleaseClient ForTool(HttpClient? http = null) =>
        new(BuildInfo.ToolReleasesApi, http);

    public async Task<PublishedRelease?> GetLatestAsync(ReleaseChannel channel = ReleaseChannel.Stable,
                                                     CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(_releasesApi, ct).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        PublishedRelease? best = null;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var draft = element.TryGetProperty("draft", out var d) && d.GetBoolean();
            if (draft) continue;

            var prerelease = element.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            if (prerelease && channel == ReleaseChannel.Stable) continue;

            var tag = element.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag is null) continue;

            var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (element.TryGetProperty("assets", out var assetArray)
                && assetArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetArray.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name is null || url is null) continue;

                    assets[name] = url;
                    if (asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes))
                        sizes[name] = bytes;
                }
            }

            DateTimeOffset? published = element.TryGetProperty("published_at", out var pa)
                                        && pa.ValueKind == JsonValueKind.String
                                        && DateTimeOffset.TryParse(pa.GetString(), out var parsed)
                ? parsed
                : null;

            var release = new PublishedRelease(
                Version: tag.TrimStart('v', 'V'),
                TagName: tag,
                IsPrerelease: prerelease,
                PublishedAt: published,
                Assets: assets)
            {
                AssetSizes = sizes,
            };

            // GitHub returns releases newest first, but the order is not contractual, so the
            // publication date decides rather than the position in the array.
            if (best is null || (release.PublishedAt > best.PublishedAt)) best = release;
        }

        return best;
    }

    /// <summary>
    /// Resolves the asset name pattern from the catalog against a concrete release.
    /// Returns the download URL and the expected SHA-256, or null when this release has no
    /// build for that loader — which is a real case worth reporting rather than crashing on.
    /// </summary>
    public async Task<(string Url, string Sha256)?> ResolveAssetAsync(
        PublishedRelease release, string assetPattern, CancellationToken ct = default)
    {
        var assetName = assetPattern.Replace("{version}", release.Version, StringComparison.Ordinal);

        if (!release.Assets.TryGetValue(assetName, out var url)) return null;

        var sha = await ReadChecksumAsync(release, assetName, ct).ConfigureAwait(false);
        if (sha is null)
        {
            throw new InvalidOperationException(
                $"Release {release.TagName} publishes {assetName} without its .sha256 checksum. " +
                "Refusing to install an archive that cannot be verified.");
        }

        return (url, sha);
    }

    /// <summary>
    /// Reads the sidecar checksum file. Format is sha256sum-compatible: "&lt;hash&gt;  &lt;filename&gt;".
    /// </summary>
    public async Task<string?> ReadChecksumAsync(PublishedRelease release, string assetName,
                                                 CancellationToken ct = default)
    {
        if (!release.Assets.TryGetValue(assetName + ".sha256", out var url)) return null;

        try
        {
            var content = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var first = content.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                               .FirstOrDefault();
            var hash = first?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            return hash is { Length: 64 } ? hash.ToLowerInvariant() : null;
        }
        catch
        {
            return null;
        }
    }
}
