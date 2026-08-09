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

public sealed record ModRelease(
    string Version,
    string TagName,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt,
    IReadOnlyDictionary<string, string> Assets);

/// <summary>
/// Finds the mod release to install, and the checksum that proves the download is intact.
///
/// The mod already publishes a .sha256 next to every archive, so verification comes for free:
/// we fetch the small checksum file and hold the archive to it. Nothing is trusted because it
/// came from the right domain.
/// </summary>
public sealed class ModReleaseClient
{
    private readonly HttpClient _http;

    public ModReleaseClient(HttpClient? http = null)
    {
        _http = http ?? Http.Create(TimeSpan.FromSeconds(30));
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"UnityGameTranslatorInstaller/{BuildInfo.Version}");
        }
    }

    public async Task<ModRelease?> GetLatestAsync(ReleaseChannel channel = ReleaseChannel.Stable,
                                                  CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(BuildInfo.ModReleasesApi, ct).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

        ModRelease? best = null;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var draft = element.TryGetProperty("draft", out var d) && d.GetBoolean();
            if (draft) continue;

            var prerelease = element.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            if (prerelease && channel == ReleaseChannel.Stable) continue;

            var tag = element.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag is null) continue;

            var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (element.TryGetProperty("assets", out var assetArray)
                && assetArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetArray.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name is not null && url is not null) assets[name] = url;
                }
            }

            DateTimeOffset? published = element.TryGetProperty("published_at", out var pa)
                                        && pa.ValueKind == JsonValueKind.String
                                        && DateTimeOffset.TryParse(pa.GetString(), out var parsed)
                ? parsed
                : null;

            var release = new ModRelease(
                Version: tag.TrimStart('v', 'V'),
                TagName: tag,
                IsPrerelease: prerelease,
                PublishedAt: published,
                Assets: assets);

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
        ModRelease release, string assetPattern, CancellationToken ct = default)
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
    private async Task<string?> ReadChecksumAsync(ModRelease release, string assetName,
                                                  CancellationToken ct)
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
