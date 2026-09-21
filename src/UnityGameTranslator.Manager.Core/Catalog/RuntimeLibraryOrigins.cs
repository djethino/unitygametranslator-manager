namespace UnityGameTranslator.Manager.Core.Catalog;

/// <summary>
/// Where the libraries a game lacks may be downloaded from — compiled in, for the reason
/// <see cref="LoaderOrigins"/> gives: what is fetched here is put in a game folder and loaded by
/// the game at start, so which publishers are trusted must not travel with a catalogue fetch.
///
/// 🔴 **One origin: Unity's own download server** (user's decision, 2026-09-21), for both the .NET
/// class libraries (read from its editor package) and the engine modules (from its build support
/// package) — never hosted by us: they are Unity's, under Unity's terms.
///
/// ⚠ BepInEx's archive of those libraries (`unity.bepinex.dev/corlibs`) served the first version of
/// this and was dropped: a hundred times lighter, but a third party publishing no checksum, and from
/// Unity 2021.2 only the Linux build of `mscorlib`, `System` and `System.Core`.
/// </summary>
public static class RuntimeLibraryOrigins
{
    /// <summary>Unity's download server — the one that serves the Hub and the editor installers.</summary>
    public const string UnityDownloadHost = "download.unity3d.com";

    /// <summary>
    /// Unity's description of one build's downloads: sections per module, each with its file, size
    /// and md5. The macOS editor's file is the one listing BOTH platforms' build support as single
    /// packages — "Windows-Mono", and "Linux-Mono" (named "Linux" before 2019) — read for 2018.4 and
    /// 2021.3; the Linux editor's lists no Linux package, the Windows editor's no Windows one.
    /// </summary>
    public static string UnityBuildIndexUrl(string version, string changeset) =>
        $"https://{UnityDownloadHost}/download_unity/{Uri.EscapeDataString(changeset)}/unity-{Uri.EscapeDataString(version)}-osx.ini";

    /// <summary>A file named in that index, relative to the build's folder on the server.</summary>
    public static string UnityBuildFileUrl(string changeset, string relative) =>
        $"https://{UnityDownloadHost}/download_unity/{Uri.EscapeDataString(changeset)}/{relative.TrimStart('/')}";

    /// <summary>
    /// Unity's terms, shown to the person before anything is downloaded from Unity — who downloads
    /// is the person, from Unity's own server; this tool only fetches on their confirmation.
    ///
    /// ⚠ The Editor Software Terms, not the general Terms of Service (changed 2026-09-21): what is
    /// downloaded is part of the editor's package, and these are the terms that govern it; they
    /// supplement the general ones and link to them.
    /// </summary>
    public const string UnityTermsUrl = "https://unity.com/legal/editor-terms-of-service/software";

    /// <summary>Hosts, outside GitHub, that a download for these libraries may start from and land on.</summary>
    public static IReadOnlyCollection<string> Hosts { get; } = [UnityDownloadHost];
}
