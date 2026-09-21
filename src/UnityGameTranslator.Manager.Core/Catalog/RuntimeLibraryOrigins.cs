namespace UnityGameTranslator.Manager.Core.Catalog;

/// <summary>
/// Where the libraries a game lacks may be downloaded from — compiled in, for the reason
/// <see cref="LoaderOrigins"/> gives: what is fetched here is put in a game folder and loaded by
/// the game at start, so which publishers are trusted must not travel with a catalogue fetch.
///
/// Three origins, each for what the others cannot supply (analyse: manager-runtime-libraries.md):
///   · BepInEx's archive of Unity's .NET class libraries, one per Unity version — complete before
///     Unity 2021.2; from 2021.2 its copies of `mscorlib`, `System` and `System.Core` are Linux
///     builds, unusable in a Windows game;
///   · our own lots of the Windows build of those libraries, one per generation of Unity's Mono
///     (<see cref="ClassLibraryLots"/>), for exactly that gap;
///   · Unity's own download server, for the engine modules a game's build stripped — never hosted
///     by us: they are Unity's, under Unity's terms.
/// </summary>
public static class RuntimeLibraryOrigins
{
    /// <summary>BepInEx's archive of the class libraries each Unity version ships.</summary>
    public const string ClassLibrariesHost = "unity.bepinex.dev";

    /// <summary>
    /// The archive for one Unity version, as the archive names it ("2018.4.36").
    ///
    /// ⚠ No checksum is published there; what is put in a game is instead held to the game's own
    /// libraries before anything is written (same family, right platform).
    /// </summary>
    public static string ClassLibrariesUrl(string archiveName) =>
        $"https://{ClassLibrariesHost}/corlibs/{Uri.EscapeDataString(archiveName)}.zip";

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

    /// <summary>Unity's terms, which the person accepts before anything is downloaded from Unity.</summary>
    public const string UnityTermsUrl = "https://unity.com/legal/terms-of-service";

    /// <summary>Hosts, outside GitHub, that a download for these libraries may start from and land on.</summary>
    public static IReadOnlyCollection<string> Hosts { get; } = [ClassLibrariesHost, UnityDownloadHost];
}
