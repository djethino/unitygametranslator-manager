using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Catalog;

/// <summary>
/// Where a loader's archives are published, and which files in that listing are ours.
///
/// 🔴 **We read the list the publisher put out; we never compose a file name.** Composing requires
/// knowing every case in advance — the losing bet described in read-before-replacing.md. The three
/// projects name their archives three different ways, each listing also holds files that are not
/// loaders at all (BepInEx_Patcher_*, BepInEx's NET.CoreCLR builds, MelonLoader.Installer.*), and
/// the day one of them ships a win-arm64 build a reader sees it while a template does not.
/// </summary>
public sealed class LoaderSource
{
    /// <summary>Which choice this is when a loader has several ("be", "github", "release").</summary>
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";

    /// <summary>"github-release" or "bepinex-be".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    /// <summary>What to call it on screen. ⚠ Never "stable" for BepInEx 6 — there is none.</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    /// <summary>"owner/name", for kind = github-release.</summary>
    [JsonPropertyName("repo")] public string? Repo { get; set; }

    /// <summary>Page to read, for kind = bepinex-be.</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }

    /// <summary>
    /// Skip pre-releases. True for BepInEx 5 and MelonLoader; false for BepInEx 6, whose only
    /// GitHub publication IS a pre-release — filtering them there would find nothing at all.
    /// </summary>
    [JsonPropertyName("stable_only")] public bool StableOnly { get; set; }

    [JsonPropertyName("match")] public List<LoaderAssetRule> Match { get; set; } = new();
}

/// <summary>One published file we accept, and what system it is for.</summary>
public sealed class LoaderAssetRule
{
    /// <summary>
    /// Literal start of the file name — not a pattern. BepInEx puts the version after it
    /// ("BepInEx_win_x64_"), MelonLoader does not ("MelonLoader.x64.zip"), and a literal handles
    /// both without a regex to get wrong.
    /// </summary>
    [JsonPropertyName("prefix")] public string Prefix { get; set; } = "";

    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("arch")] public string Arch { get; set; } = "";
}

/// <summary>One build of a loader, as the publisher currently offers it.</summary>
public sealed record LoaderBuild(
    string Version,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<LoaderAsset> Assets,
    string SourceLabel,
    bool IsPinnedFallback)
{
    /// <summary>"6.0.0-be.785 — 28 Jun 2026", or just the version when no date is known.</summary>
    public string Describe() => PublishedAt is { } when
        ? $"{Version} — {when.ToLocalTime():d MMM yyyy}"
        : Version;
}

/// <summary>
/// Asks each publisher what it currently offers, so the catalog stops carrying version numbers.
///
/// ⚠ **A failure falls back to the pinned entry and SAYS SO** (<see cref="LoaderBuild.IsPinnedFallback"/>).
/// Silently installing a two-year-old build because a page did not answer is the failure mode this
/// whole change exists to end; repeating it quietly one level down would be worse than before.
///
/// ⚠ Results are cached for the life of the process, keyed by loader and channel. Unauthenticated
/// GitHub allows 60 requests an hour per IP: resolving on every card that gets displayed would
/// exhaust that on a machine with a large library, and the answer changes a few times a year.
/// </summary>
public sealed class LoaderBuildResolver
{
    private static readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<LoaderBuild> Builds)> Cache = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// How long an answer is trusted before being asked again.
    ///
    /// ⚠ Kept for the life of the process was right for a tool opened and closed; it is wrong for
    /// one left running for days on a machine somebody plays on. Six hours: far longer than any
    /// session where the answer could change under the reader, far shorter than a working day.
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly string _apiBase;

    public LoaderBuildResolver(HttpClient? http = null, string? apiBase = null)
    {
        _http = http ?? Http.Create(TimeSpan.FromSeconds(20));
        _apiBase = (apiBase ?? "https://api.github.com").TrimEnd('/');
    }

    /// <summary>Forgets everything. For tests, and for a "check again" button.</summary>
    public static void Forget()
    {
        lock (Cache) Cache.Clear();
    }

    /// <summary>The cached answer while it is still young enough to be believed.</summary>
    private static IReadOnlyList<LoaderBuild>? Fresh(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var hit)) return null;
            if (DateTimeOffset.UtcNow - hit.At > Freshness) { Cache.Remove(key); return null; }
            return hit.Builds;
        }
    }

    /// <summary>
    /// What is already known about this loader on this channel, WITHOUT asking anybody.
    ///
    /// 🔴 **The whole point of warming up.** A screen drawn fifty times a minute cannot resolve;
    /// but once the answer is in, showing it costs a dictionary lookup. Null means "not asked yet
    /// or gone stale", and a caller getting null says nothing rather than guessing — printing the
    /// pinned version beside a channel that would install something else is the fault this exists
    /// to end.
    /// </summary>
    public static LoaderBuild? Known(LoaderDescriptor loader, string? channel, int count = 5)
    {
        if (Pick(loader, channel) is not { } source) return null;

        return Fresh($"{loader.Id}|{source.Channel}|{count}") is { Count: > 0 } builds
            ? builds[0]
            : null;
    }

    /// <summary>
    /// Resolves every loader in the catalog, quietly, so screens can read the answers later.
    ///
    /// ⚠ One pass over four entries — two or three requests in all, against an unauthenticated
    /// GitHub budget of sixty an hour. Called when the window opens and again when what it holds
    /// has gone stale; never on the drawing path, where it would cost a request per card.
    ///
    /// Never throws: a publisher that does not answer leaves the cache without that entry, and the
    /// screens fall back to saying nothing about a version, which is what they do today.
    /// </summary>
    public async Task WarmAsync(LoaderCatalogDocument catalog, string bepinex6Channel,
                                CancellationToken ct = default)
    {
        foreach (var loader in catalog.Loaders)
        {
            if (loader.Sources.Count == 0) continue;
            if (ct.IsCancellationRequested) return;

            var channel = loader.Id.StartsWith("bepinex6", StringComparison.OrdinalIgnoreCase)
                ? bepinex6Channel
                : null;

            await ResolveAsync(loader, channel, count: 5, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The most recent builds this loader offers on the given channel, newest first.
    ///
    /// Never empty and never throws: the last resort is the version pinned in the catalog, which
    /// is also what an offline machine and an older Manager use.
    /// </summary>
    public async Task<IReadOnlyList<LoaderBuild>> ResolveAsync(
        LoaderDescriptor loader, string? channel, int count = 5, CancellationToken ct = default)
    {
        var source = Pick(loader, channel);
        if (source is null) return new[] { Pinned(loader) };

        var key = $"{loader.Id}|{source.Channel}|{count}";
        if (Fresh(key) is { } cached) return cached;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Fresh(key) is { } second) return second;

            IReadOnlyList<LoaderBuild> builds;
            try
            {
                builds = source.Kind switch
                {
                    "github-release" => await FromGitHubAsync(source, count, ct).ConfigureAwait(false),
                    "bepinex-be" => await FromBleedingEdgeAsync(source, count, ct).ConfigureAwait(false),
                    _ => Array.Empty<LoaderBuild>(),
                };
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A process boundary: an outage, a rate limit, a page that changed shape. The
                // caller is told which it got through IsPinnedFallback rather than left guessing.
                builds = Array.Empty<LoaderBuild>();
            }

            if (builds.Count == 0) builds = new[] { Pinned(loader) };

            lock (Cache) Cache[key] = (DateTimeOffset.UtcNow, builds);
            return builds;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The channel asked for, then the loader's only source. Null when it has none.</summary>
    public static LoaderSource? Pick(LoaderDescriptor loader, string? channel)
    {
        if (loader.Sources.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(channel))
        {
            var wanted = loader.Sources.FirstOrDefault(
                s => string.Equals(s.Channel, channel, StringComparison.OrdinalIgnoreCase));
            if (wanted is not null) return wanted;
        }

        return loader.Sources[0];
    }

    /// <summary>
    /// What the catalog pins. Not a failure state on its own — it is what an offline install uses,
    /// and what every Manager built before this existed uses all the time.
    /// </summary>
    public static LoaderBuild Pinned(LoaderDescriptor loader) => new(
        loader.Version,
        null,
        loader.Assets,
        "pinned in the catalog",
        IsPinnedFallback: true);

    private async Task<IReadOnlyList<LoaderBuild>> FromGitHubAsync(
        LoaderSource source, int count, CancellationToken ct)
    {
        var url = $"{_apiBase}/repos/{source.Repo}/releases?per_page=30";
        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<LoaderBuild>();

        var builds = new List<LoaderBuild>();

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (builds.Count >= count) break;

            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (source.StableOnly
                && release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;

            if (!release.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array) continue;

            var matched = new List<LoaderAsset>();
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                if (name is null) continue;

                var rule = RuleFor(source, name);
                if (rule is null) continue;

                matched.Add(new LoaderAsset
                {
                    Os = rule.Os,
                    Arch = rule.Arch,
                    Name = name,
                    // Taken from the API rather than assembled: the publisher already says where
                    // the file is, and one place fewer to be wrong about.
                    Url = Text(asset, "browser_download_url") ?? "",
                    Sha256 = Digest(Text(asset, "digest")),
                });
            }

            // A release with none of our files is a release for something else — BepInEx 5's
            // repository carries the 6.x pre-releases too, and vice versa.
            if (matched.Count == 0) continue;

            builds.Add(new LoaderBuild(
                Version: (Text(release, "tag_name") ?? "").TrimStart('v', 'V'),
                PublishedAt: Moment(Text(release, "published_at")),
                Assets: matched,
                SourceLabel: source.Label,
                IsPinnedFallback: false));
        }

        return builds;
    }

    // The build server offers no API — /artifacts.json, /api/... and /latest all answer 404 — but
    // its page is regular. Each build opens with its number, then carries a date and its files.
    private static readonly Regex BuildNumber = new(
        @"artifact-id""\s*>\s*#(?<n>\d+)\s*<", RegexOptions.Compiled);

    private static readonly Regex BuildDate = new(
        @"build-date""\s*>(?<when>[^<]+)<", RegexOptions.Compiled);

    private static readonly Regex BuildLink = new(
        @"artifact-link""[^>]*href=""(?<href>[^""]+)""", RegexOptions.Compiled);

    private async Task<IReadOnlyList<LoaderBuild>> FromBleedingEdgeAsync(
        LoaderSource source, int count, CancellationToken ct)
    {
        var page = await _http.GetStringAsync(source.Url, ct).ConfigureAwait(false);
        var origin = new Uri(source.Url!);

        var builds = new List<LoaderBuild>();
        var starts = BuildNumber.Matches(page);

        for (var i = 0; i < starts.Count && builds.Count < count; i++)
        {
            // Everything from this build's number up to the next one belongs to it.
            var from = starts[i].Index;
            var to = i + 1 < starts.Count ? starts[i + 1].Index : page.Length;
            var block = page[from..to];

            var matched = new List<LoaderAsset>();
            string? version = null;

            foreach (Match link in BuildLink.Matches(block))
            {
                var href = link.Groups["href"].Value;

                // ⚠ The href is percent-encoded — the '+' before the commit hash arrives as %2B.
                // Match on the decoded name, download from the URL as published.
                var name = Uri.UnescapeDataString(href[(href.LastIndexOf('/') + 1)..]);

                var rule = RuleFor(source, name);
                if (rule is null) continue;

                version ??= VersionIn(name, rule.Prefix);

                matched.Add(new LoaderAsset
                {
                    Os = rule.Os,
                    Arch = rule.Arch,
                    Name = name,
                    Url = new Uri(origin, href).ToString(),
                    // The build server publishes no checksum, for any file. Saying so is the
                    // honest answer; inventing one is not available.
                    Sha256 = "",
                });
            }

            if (matched.Count == 0) continue;

            var date = BuildDate.Match(block);

            builds.Add(new LoaderBuild(
                Version: version ?? $"build {starts[i].Groups["n"].Value}",
                PublishedAt: date.Success ? Moment(date.Groups["when"].Value) : null,
                Assets: matched,
                SourceLabel: source.Label,
                IsPinnedFallback: false));
        }

        return builds;
    }

    /// <summary>The rule this file name answers to, or null when it is not one of ours.</summary>
    private static LoaderAssetRule? RuleFor(LoaderSource source, string name)
    {
        // Archives only. Every listing also holds installers and patchers, and the prefixes are
        // narrow enough already — this is the belt, not the braces.
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;

        return source.Match.FirstOrDefault(
            r => name.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// "BepInEx-Unity.Mono-win-x64-6.0.0-be.785+6abdba4.zip" → "6.0.0-be.785".
    ///
    /// The commit hash is dropped: it identifies the file, not the build, and showing it would
    /// put eight characters of noise in front of the one thing being compared — the number.
    /// </summary>
    private static string? VersionIn(string name, string prefix)
    {
        if (name.Length <= prefix.Length) return null;

        var rest = name[prefix.Length..];
        var end = rest.LastIndexOf(".zip", StringComparison.OrdinalIgnoreCase);
        if (end > 0) rest = rest[..end];

        var plus = rest.IndexOf('+');
        if (plus > 0) rest = rest[..plus];

        return rest.Length > 0 ? rest : null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Digest(string? raw)
    {
        const string prefix = "sha256:";
        return raw is not null && raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? raw[prefix.Length..].ToLowerInvariant()
            : "";
    }

    private static DateTimeOffset? Moment(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var when) ? when : null;
}
