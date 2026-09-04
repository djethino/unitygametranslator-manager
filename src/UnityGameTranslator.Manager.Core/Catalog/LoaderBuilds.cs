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

    // 🔴 **No `repo`, no `url` here, and their absence is the point.** A source says WHICH channel
    // this is and how to recognise its files; WHERE that channel lives is in LoaderOrigins,
    // compiled in. The catalog is fetched at every launch, so an address read from it reaches every
    // installation at the next one, with no release in between — and what it points at is unpacked
    // into a game folder. Removing the fields from the model means a catalog still carrying them
    // cannot be obeyed, rather than merely not being read today.

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
/// ⚠ Results are cached keyed by loader and channel. Unauthenticated GitHub allows 60 requests an
/// hour per IP: resolving on every card that gets displayed would exhaust that on a machine with a
/// large library, and the answer changes a few times a year.
///
/// ⚠ **Two questions, and keeping them apart is what this class got wrong once.** How long an
/// answer is trusted before being asked AGAIN is <see cref="Freshness"/>; what the tool will admit
/// to knowing is the last answer received, whatever its age (<see cref="Known"/>). Merging the two
/// meant an expired answer was deleted, so screens fell back to the catalogue's pin and silently
/// un-said what they had been saying all morning.
/// </summary>
public sealed class LoaderBuildResolver
{
    private static readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<LoaderBuild> Builds)> Cache = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// What each ADDRESS answered, so several loaders reading the same page pay for it once.
    ///
    /// 🔴 **Three catalogue entries share one repository.** `bepinex5`, `bepinex6-mono` and
    /// `bepinex6-il2cpp` all resolve to `BepInEx/BepInEx` (see LoaderOrigins), and the two BepInEx 6
    /// entries share the Bleeding Edge page as well. The cache above is keyed by LOADER, so a
    /// warm-up fetched the same URL up to three times — two requests spent on an answer already in
    /// hand, out of sixty an hour for the whole machine.
    ///
    /// ⚠ **The result cannot be shared, only the document.** Which files in a release belong to a
    /// loader is decided by the catalogue's own rules (`RuleFor`), and mono and il2cpp pick
    /// different assets out of the very same release. So this holds the raw body and each loader
    /// still reads it with its own rules.
    ///
    /// ⚠ A failure is remembered too, briefly. Without that, one unreachable page is requested once
    /// per loader in the same second; with it for six hours, a hiccup would blind the tool for the
    /// afternoon. A minute covers the burst and nothing more.
    /// </summary>
    private static readonly Dictionary<string, (DateTimeOffset At, string? Body)> Documents = new();

    /// <summary>How long a failed fetch is remembered — long enough to cover one warm-up pass.</summary>
    private static readonly TimeSpan FailureHeld = TimeSpan.FromMinutes(1);

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
        lock (Documents) Documents.Clear();
    }

    /// <summary>
    /// One address, fetched at most once per <see cref="Freshness"/> however many loaders want it.
    ///
    /// ⚠ Throws what the fetch threw, exactly as before — the caller turns a failure into the
    /// pinned build and says so. What is remembered here is only that it failed, and only for
    /// <see cref="FailureHeld"/>, so the loaders behind it in the same pass do not each retry it.
    /// </summary>
    private async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        lock (Documents)
        {
            if (Documents.TryGetValue(url, out var held))
            {
                var age = DateTimeOffset.UtcNow - held.At;

                if (held.Body is { } body && age <= Freshness) return body;

                // A failure just met by the loader ahead of this one. Answered as the failure it
                // was rather than asked again — three requests for one dead address is what this
                // whole cache exists to stop.
                if (held.Body is null && age <= FailureHeld)
                    throw new HttpRequestException($"{url} did not answer a moment ago");
            }
        }

        try
        {
            var body = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            lock (Documents) Documents[url] = (DateTimeOffset.UtcNow, body);
            return body;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            lock (Documents) Documents[url] = (DateTimeOffset.UtcNow, null);
            throw;
        }
    }

    /// <summary>
    /// Forgets only what is older than this, so that "look again" really does — without a burst of
    /// them spending the budget.
    ///
    /// 🔴 **A rescan asks the publishers again, and a rescan is a button.** Four loaders is up to
    /// four requests against sixty an hour, shared with the mod's release check, this tool's own
    /// update check and every other program on this address. Pressed twenty times in an hour —
    /// which is a normal afternoon of testing — an unbounded Forget would exhaust the allowance and
    /// every later answer would be a rate-limit refusal, cached as the catalogue's pin.
    ///
    /// ⚠ The floor is deliberately far shorter than <see cref="Freshness"/>: the point is not to
    /// make somebody wait, it is that pressing the same button twice in ten seconds cannot cost
    /// twice.
    /// </summary>
    public static void Forget(TimeSpan olderThan)
    {
        var now = DateTimeOffset.UtcNow;

        lock (Cache)
        {
            foreach (var key in Cache.Where(e => now - e.Value.At > olderThan)
                                     .Select(e => e.Key).ToList())
            {
                Cache.Remove(key);
            }
        }

        // ⚠ The documents too, or the builds would be recomputed from a page held in memory and the
        // rescan would ask nobody anything — the exact fault this method exists to fix.
        lock (Documents)
        {
            foreach (var url in Documents.Where(e => now - e.Value.At > olderThan)
                                         .Select(e => e.Key).ToList())
            {
                Documents.Remove(url);
            }
        }
    }

    /// <summary>
    /// The cached answer while it is still young enough to be asked about again.
    ///
    /// ⚠ **Asking whether to re-ask, NOT what is known** — and the two were one method. It used to
    /// DELETE the entry on its way past, so the moment an answer turned six hours old the tool
    /// forgot it had ever had one. See <see cref="LastKnown"/> for what that cost.
    /// </summary>
    private static IReadOnlyList<LoaderBuild>? Fresh(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var hit)) return null;
            return DateTimeOffset.UtcNow - hit.At > Freshness ? null : hit.Builds;
        }
    }

    /// <summary>
    /// The last answer received, however old — what to SHOW, as opposed to whether to ask again.
    ///
    /// 🔴 **Written because forgetting made the window lie.** Expiring dropped the entry, so
    /// <see cref="Known"/> answered null, and ReadLoaderStanding falls back to the version PINNED in
    /// the catalogue when it gets null. On a window left open past six hours, clicking a game
    /// redrew its row from that pin — and "loader update available" simply vanished from a game
    /// that still had one, with nothing said and no way back short of a rescan.
    ///
    /// ⚠ A six-hour-old answer from the publisher is not stale data to be hidden: it is enormously
    /// closer to the truth than a version pinned in a catalogue months ago. What expiry decides is
    /// when to go and ask — never what to admit to knowing.
    /// </summary>
    private static IReadOnlyList<LoaderBuild>? LastKnown(string key)
    {
        lock (Cache) return Cache.TryGetValue(key, out var hit) ? hit.Builds : null;
    }

    /// <summary>
    /// Whether anything held about this catalogue has aged past <see cref="Freshness"/> and is
    /// worth asking about again.
    ///
    /// 🔴 **Nothing was watching for this, which is what turned a cache into a leak.** Expiry was
    /// added for "a tool left running for days", and <see cref="WarmAsync"/>'s own summary claims it
    /// is called "again when what it holds has gone stale" — but the only caller runs after a scan.
    /// So the answers aged out and nobody ever asked again: the window forgot, and stayed forgotten
    /// until somebody rescanned.
    ///
    /// ⚠ Entries that were never fetched are NOT reported here. Absent means the warm-up has not
    /// run — at startup, or with online mode off — and that is a different question with a different
    /// answer; treating it as stale would have a window that is deliberately offline asking every
    /// few minutes for ever.
    /// </summary>
    public static bool AnythingStale(LoaderCatalogDocument catalog, string? bepinex6Channel,
                                     int count = 5)
    {
        foreach (var loader in catalog.Loaders)
        {
            var channel = loader.Id.StartsWith("bepinex6", StringComparison.OrdinalIgnoreCase)
                ? bepinex6Channel
                : null;

            if (Pick(loader, channel) is not { } source) continue;

            var key = $"{loader.Id}|{source.Channel}|{count}";

            lock (Cache)
            {
                if (!Cache.TryGetValue(key, out var hit)) continue;
                if (DateTimeOffset.UtcNow - hit.At > Freshness) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What is already known about this loader on this channel, WITHOUT asking anybody.
    ///
    /// 🔴 **The whole point of warming up.** A screen drawn fifty times a minute cannot resolve;
    /// but once the answer is in, showing it costs a dictionary lookup. Null means **nobody has
    /// ever asked** — never "the answer went stale" — and a caller getting null says nothing rather
    /// than guessing, because printing the pinned version beside a channel that would install
    /// something else is the fault this exists to end.
    /// </summary>
    public static LoaderBuild? Known(LoaderDescriptor loader, string? channel, int count = 5)
    {
        if (Pick(loader, channel) is not { } source) return null;

        return LastKnown($"{loader.Id}|{source.Channel}|{count}") is { Count: > 0 } builds
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
                // ⚠ The loader's id, not an address off the source: where each publisher lives is
                // compiled in (LoaderOrigins). An id that file does not know resolves to nothing,
                // and the pinned fallback below answers — never an address the catalog supplied.
                builds = source.Kind switch
                {
                    "github-release" => await FromGitHubAsync(loader.Id, source, count, ct)
                        .ConfigureAwait(false),
                    "bepinex-be" => await FromBleedingEdgeAsync(loader.Id, source, count, ct)
                        .ConfigureAwait(false),
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
        string loaderId, LoaderSource source, int count, CancellationToken ct)
    {
        var repo = LoaderOrigins.GitHubRepoFor(loaderId);
        if (repo is null) return Array.Empty<LoaderBuild>();

        // ⚠ Through FetchAsync: three catalogue entries resolve to this same repository, and each
        // reads the same releases with its own asset rules.
        var url = $"{_apiBase}/repos/{repo}/releases?per_page=30";
        var json = await FetchAsync(url, ct).ConfigureAwait(false);

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
        string loaderId, LoaderSource source, int count, CancellationToken ct)
    {
        var pageUrl = LoaderOrigins.BuildsPageFor(loaderId);
        if (pageUrl is null) return Array.Empty<LoaderBuild>();

        // ⚠ Same reason as the GitHub side: both BepInEx 6 entries read this one page.
        var page = await FetchAsync(pageUrl, ct).ConfigureAwait(false);
        var origin = new Uri(pageUrl);

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
