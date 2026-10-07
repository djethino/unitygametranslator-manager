using System.Text.Json;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>Where a download comes from and how strongly we can vouch for it.</summary>
public enum IntegrityLevel
{
    /// <summary>Hash pinned in our catalog. Strongest: it survives the publisher changing a file.</summary>
    Pinned,

    /// <summary>Hash published by GitHub for that asset. Detects corruption and swapped files.</summary>
    Published,

    /// <summary>No hash available anywhere. HTTPS is all we have.</summary>
    None,

    /// <summary>
    /// GitHub could not be asked (its limit of 60 calls an hour, an outage): whether it publishes a
    /// hash is unknown. HTTPS is all we have — and the screen says why, rather than "none".
    /// </summary>
    Unasked,
}

/// <param name="Bytes">
/// The file's size as its publisher states it, when that answer was read (GitHub's `size`); the
/// download is held to it. Null for a publisher that states none.
/// </param>
public sealed record ResolvedDownload(string Url, string? Sha256, IntegrityLevel Integrity, long? Bytes = null)
{
    public string Describe() => Integrity switch
    {
        IntegrityLevel.Pinned => "checksum pinned in the catalog",
        IntegrityLevel.Published => "checksum published by GitHub",
        IntegrityLevel.Unasked => "GitHub did not answer, so no checksum could be read — HTTPS only",
        _ => "no checksum available — HTTPS only",
    };
}

/// <summary>
/// Resolves a release asset to a URL and a checksum.
///
/// Neither BepInEx nor MelonLoader publishes .sha256 files, but GitHub exposes a sha256 digest
/// for every release asset through its API. Reading it there is better than pinning hashes in
/// our catalog: a pinned hash goes stale the moment we bump a loader version, and a stale hash
/// blocks installs for everyone. Pinning stays possible and takes precedence when present.
/// </summary>
public sealed class GitHubAssets
{
    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly Dictionary<string, Dictionary<string, string>> _cache = new();

    /// <summary>
    /// <paramref name="apiBase"/> exists so this can be pointed at something other than GitHub —
    /// which is how the self-update path gets exercised end to end before a single release has
    /// been published. Left alone it is GitHub, as it is everywhere in the product.
    /// </summary>
    public GitHubAssets(HttpClient? http = null, string? apiBase = null)
    {
        _apiBase = (apiBase ?? "https://api.github.com").TrimEnd('/');
        _http = http ?? Http.Create(TimeSpan.FromSeconds(30));
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"UnityGameTranslatorManager/{BuildInfo.Version}");
        }
    }

    /// <summary>
    /// Digests for every asset of a release, keyed by file name — empty when the release publishes
    /// none, and **null when GitHub could not be asked**.
    ///
    /// 🔴 The two used to be one empty map (2026-10-07): a rate limit read as "this release has no
    /// checksum", the install went on unverified saying exactly that, and Ollama's offer claimed the
    /// publisher had none. And the empty answer was cached, so the question was never asked again in
    /// that session. Now a failure is said, kept apart, and not remembered — the next lookup asks.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> GetDigestsAsync(
        string repo, string tag, CancellationToken ct = default)
    {
        var key = $"{repo}@{tag}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var url = $"{_apiBase}/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    if (name is null || digest is null) continue;

                    // The field is prefixed with the algorithm: "sha256:<hex>".
                    const string prefix = "sha256:";
                    if (digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        digests[name] = digest[prefix.Length..].ToLowerInvariant();
                }
            }
        }
        // A cancellation the caller asked for goes on; a timeout (also a TaskCanceledException) is
        // GitHub not answering.
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Faults.Say("GitHubAssets.GetDigestsAsync", ex, key);
            return null;
        }

        _cache[key] = digests;
        return digests;
    }

    public static string BuildUrl(string repo, string tag, string assetName) =>
        $"https://github.com/{repo}/releases/download/{tag}/{assetName}";
}
