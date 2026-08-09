using System.Text.Json.Serialization;

namespace UnityGameTranslator.Installer.Core.Model;

/// <summary>
/// The loader catalog. Nothing about a mod loader's on-disk layout is hardcoded in this tool:
/// it all lives here, fetched at runtime. When BepInEx or MelonLoader changes its structure
/// (it has, more than once), we edit this file and every installed copy of the tool is fixed
/// without publishing a new binary — which matters a lot for a tool that cannot self-update.
/// </summary>
public sealed class LoaderCatalogDocument
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("generated_at")] public string? GeneratedAt { get; set; }
    [JsonPropertyName("loaders")] public List<LoaderDescriptor> Loaders { get; set; } = new();

    /// <summary>Mod release asset name per loader id, with a {version} placeholder.</summary>
    [JsonPropertyName("plugin_builds")] public Dictionary<string, string> PluginBuilds { get; set; } = new();
}

public sealed class LoaderDescriptor
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("display")] public string Display { get; set; } = "";

    /// <summary>"mono", "il2cpp" — which scripting backends this loader can host.</summary>
    [JsonPropertyName("runtimes")] public List<string> Runtimes { get; set; } = new();

    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>
    /// GitHub release the archives come from. When set, download URLs and checksums are both
    /// derived from it — so bumping a loader version means changing the tag, and nothing else.
    /// </summary>
    [JsonPropertyName("github")] public GitHubRelease? GitHub { get; set; }

    /// <summary>Downloadable archives, one per OS/architecture combination.</summary>
    [JsonPropertyName("assets")] public List<LoaderAsset> Assets { get; set; } = new();

    [JsonPropertyName("detect")] public LoaderDetect Detect { get; set; } = new();

    /// <summary>
    /// Where our plugin DLL goes, relative to the game root.
    /// Not always a dedicated folder: MelonLoader only scans the root of Mods/ and ignores
    /// subdirectories, so the DLL sits directly among other mods. Uninstall therefore has to
    /// work file by file from the receipt, never by removing a folder.
    /// </summary>
    [JsonPropertyName("plugin_dir")] public string PluginDir { get; set; } = "";

    /// <summary>
    /// Where the mod keeps config.json, translations.json, fonts and images — which is NOT the
    /// plugin folder for every loader. BepInEx: next to the DLL. MelonLoader: UserData/.
    /// This is what the "keep or delete my settings and translations" choice acts on, and what
    /// tells us a game already has a local translation.
    /// </summary>
    [JsonPropertyName("userdata_dir")] public string UserDataDir { get; set; } = "";

    /// <summary>True when the plugin folder is shared with other mods (MelonLoader's Mods/).</summary>
    [JsonPropertyName("plugin_dir_shared")] public bool PluginDirShared { get; set; }

    [JsonPropertyName("requires")] public LoaderRequirements Requires { get; set; } = new();

    /// <summary>Wine/Proton DLL override needed to inject, e.g. "winhttp" or "version".</summary>
    [JsonPropertyName("proton_dll_override")] public string? ProtonDllOverride { get; set; }

    /// <summary>Notes shown before installing, each with the situation it actually applies to.</summary>
    [JsonPropertyName("warnings")] public List<LoaderWarning> Warnings { get; set; } = new();

    /// <summary>
    /// Preference when several loaders fit. Higher wins. Lets us change the recommendation
    /// server-side the day a loader becomes the better default.
    /// </summary>
    [JsonPropertyName("preference")] public int Preference { get; set; }

    public bool SupportsRuntime(UnityRuntime runtime) => runtime switch
    {
        UnityRuntime.Mono => Runtimes.Contains("mono"),
        UnityRuntime.Il2Cpp => Runtimes.Contains("il2cpp"),
        _ => false,
    };
}

/// <summary>
/// A note shown before installing, together with when it is actually true.
///
/// Conditions are not decoration. A warning that appears when it does not apply teaches the
/// reader to skip warnings — which is exactly the habit we need them not to have on the one
/// that matters. "First launch will be slow" is false once the loader has already run, and a
/// note about macOS shown on Windows is noise.
/// </summary>
public sealed class LoaderWarning
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>Operating systems this applies to. Empty means all.</summary>
    [JsonPropertyName("os")] public List<string> Os { get; set; } = new();

    /// <summary>Scripting backends this applies to ("mono", "il2cpp"). Empty means all.</summary>
    [JsonPropertyName("runtimes")] public List<string> Runtimes { get; set; } = new();

    /// <summary>
    /// Only when we are about to install the loader ourselves — not when it is already there
    /// and has been running for months.
    /// </summary>
    [JsonPropertyName("on_fresh_install")] public bool OnFreshInstallOnly { get; set; }

    public bool AppliesTo(string osId, UnityRuntime runtime, bool freshInstall)
    {
        if (OnFreshInstallOnly && !freshInstall) return false;

        if (Os.Count > 0 && !Os.Contains(osId, StringComparer.OrdinalIgnoreCase)) return false;

        if (Runtimes.Count > 0)
        {
            var name = runtime == UnityRuntime.Il2Cpp ? "il2cpp" : "mono";
            if (!Runtimes.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        }

        return true;
    }
}

public sealed class GitHubRelease
{
    /// <summary>"owner/name", e.g. "BepInEx/BepInEx".</summary>
    [JsonPropertyName("repo")] public string Repo { get; set; } = "";

    /// <summary>Release tag, e.g. "v5.4.23.5".</summary>
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
}

public sealed class LoaderAsset
{
    /// <summary>"windows", "linux", "macos".</summary>
    [JsonPropertyName("os")] public string Os { get; set; } = "";

    /// <summary>"x64", "x86", "universal".</summary>
    [JsonPropertyName("arch")] public string Arch { get; set; } = "";

    /// <summary>Asset file name inside the GitHub release. Preferred over a raw URL.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Direct URL, for anything not hosted as a GitHub release asset.</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    /// <summary>
    /// Optional pinned SHA-256. Takes precedence over the digest GitHub publishes, for the case
    /// where we want to guarantee a specific file rather than "whatever is behind that name".
    /// </summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>How to recognise this loader already sitting in a game folder.</summary>
public sealed class LoaderDetect
{
    /// <summary>At least one of these files must exist (proxy DLL variants).</summary>
    [JsonPropertyName("any")] public List<string> Any { get; set; } = new();

    /// <summary>All of these must exist.</summary>
    [JsonPropertyName("all")] public List<string> All { get; set; } = new();

    /// <summary>
    /// Files that must NOT exist. This is what separates BepInEx 5 from BepInEx 6, which share
    /// winhttp.dll and doorstop_config.ini and differ by a hidden marker and a dotnet folder.
    /// </summary>
    [JsonPropertyName("none")] public List<string> None { get; set; } = new();

    /// <summary>File whose assembly/file version identifies the installed loader version.</summary>
    [JsonPropertyName("version_file")] public string? VersionFile { get; set; }
}

public sealed class LoaderRequirements
{
    /// <summary>Minimum Unity version, e.g. "5.0.0". Null when there is no known floor.</summary>
    [JsonPropertyName("unity_min")] public string? UnityMin { get; set; }

    /// <summary>
    /// .NET Desktop Runtime the *user's machine* needs, e.g. "6.0". MelonLoader IL2CPP needs it
    /// and fails at game launch without it — so we check before promising anything.
    /// </summary>
    [JsonPropertyName("dotnet_desktop")] public string? DotnetDesktop { get; set; }
}
