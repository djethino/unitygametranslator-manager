namespace UnityGameTranslator.Manager.Core.Catalog;

/// <summary>
/// Where this tool agrees to download from — every download, not only the loaders.
///
/// 🔴 **One rule for the four things this tool fetches and unpacks or runs.** <see cref="LoaderOrigins"/>
/// carried the check for loader archives, and nothing else went through it: the mod's plugin was
/// fetched from whatever `browser_download_url` GitHub's API answered, the tool's own update the
/// same way, and the Ollama installer — which is then EXECUTED — likewise. Each of those addresses
/// comes from a TLS answer of api.github.com, so none was open to a passer-by; but a redirect
/// written into one of those answers, or a field added to one of those APIs, was followed without
/// a question, into a game folder or into a running process. The address is now checked at the one
/// place every download goes through (<see cref="Net.Download"/>), whoever produced it.
///
/// ⚠ Two questions, because a download has two addresses. Where it STARTS — the URL we were handed
/// — must be one of ours: HTTPS, and on github.com a repository from the list below. Where it LANDS
/// after redirects must belong to the same publisher: GitHub hands release files to its own content
/// hosts, and that host has already changed name once (measured 2026-09-04:
/// `objects.githubusercontent.com` is now `release-assets.githubusercontent.com`). A list of exact
/// hosts would have broken every download on that day, silently, so the landing rule is the
/// publisher's domain, not a host name.
/// </summary>
public static class DownloadOrigins
{
    /// <summary>Ollama's repository, where its installer is published. Compiled in, like the rest.</summary>
    public const string OllamaRepository = "ollama/ollama";

    /// <summary>
    /// GitHub repositories a download may start from: the loaders' publishers, the mod, this tool,
    /// and Ollama. Compiled in — adding one means a release, which is the point.
    /// </summary>
    public static IReadOnlyCollection<string> TrustedRepositories { get; } = Build();

    private static string[] Build()
    {
        var repos = new List<string>(LoaderOrigins.KnownRepositories)
        {
            BuildInfo.ToolRepo,
            OllamaRepository,
        };

        // The mod's repository is compiled in as its releases API; the name is inside it.
        var mod = RepositoryOf(BuildInfo.ModReleasesApi);
        if (mod is not null) repos.Add(mod);

        return repos.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>"owner/name" out of "https://api.github.com/repos/owner/name/releases", or null.</summary>
    public static string? RepositoryOf(string releasesApi)
    {
        if (!Uri.TryCreate(releasesApi, UriKind.Absolute, out var uri)) return null;

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length >= 3 && parts[0].Equals("repos", StringComparison.OrdinalIgnoreCase)
            ? $"{parts[1]}/{parts[2]}"
            : null;
    }

    /// <summary>
    /// Whether an address is one this tool agrees to start a download from, whoever produced it.
    ///
    /// ⚠ Applied to the address rather than to its provenance, and that is deliberate. A URL can
    /// legitimately come from the publisher's own API — GitHub returns `browser_download_url`, the
    /// Bleeding Edge page carries hrefs — so it travels through the same field as anything a
    /// catalog or an answer might have named. Checking where a string came from is a thing to get
    /// wrong once; checking the address itself cannot be bypassed by adding a path.
    ///
    /// ⚠ Host, not prefix: "https://github.com.attacker.net/..." starts with the right characters
    /// and is a different machine. Uri parses the authority, string comparison does not.
    /// </summary>
    public static bool IsAllowedDownload(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        // Plain HTTP would let anyone on the path swap the file, and every publisher here serves
        // TLS. And a user name in the address is the classic disguise: the host after the `@` is
        // the real one, and nothing we fetch is ever addressed that way.
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        // On GitHub, the host is shared by everybody. Being on github.com says nothing — the
        // repository does, and it must be one of ours.
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return IsTrustedRepositoryPath(uri.AbsolutePath);

        return LoaderOrigins.BuildsHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a download that started at <paramref name="requested"/> may have landed at
    /// <paramref name="landed"/> once the redirects were followed.
    ///
    /// The same host is always fine. From github.com, GitHub's own content domain is fine — any
    /// host under `githubusercontent.com`, because the exact name is theirs to change and has
    /// changed. Anything else is a redirect out of the publisher's hands, and the file is refused
    /// after it was fetched rather than trusted because the first address looked right.
    /// </summary>
    public static bool IsAllowedLanding(Uri requested, Uri landed)
    {
        if (landed.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(landed.UserInfo)) return false;

        if (landed.Host.Equals(requested.Host, StringComparison.OrdinalIgnoreCase)) return true;

        return requested.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && IsUnder(landed.Host, "githubusercontent.com");
    }

    private static bool IsTrustedRepositoryPath(string absolutePath)
    {
        foreach (var repo in TrustedRepositories)
        {
            if (absolutePath.StartsWith($"/{repo}/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>A subdomain of the domain, never a look-alike: the dot before it is required.</summary>
    private static bool IsUnder(string host, string domain) =>
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
