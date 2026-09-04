using UnityGameTranslator.Manager.Core.Catalog;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Where this tool agrees to download from, and where a download may land.
///
/// ⚠ Both halves matter. The refusals are the point; the acceptances are what keeps the rule
/// from breaking every install the day a publisher renames a host — which GitHub did between
/// the rule being written and this check existing (`objects.` became `release-assets.`).
/// </summary>
internal static class DownloadOriginsChecks
{
    public static void WhereADownloadMayStart()
    {
        Program.Section("Download origins: where a download may start");

        Program.Check(DownloadOrigins.IsAllowedDownload(
                "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip"),
            "a loader from its publisher", "the case every install goes through");
        Program.Check(DownloadOrigins.IsAllowedDownload(
                "https://github.com/LavaGang/MelonLoader/releases/download/v0.7.0/MelonLoader.x64.zip"),
            "the other loader publisher", "");
        Program.Check(DownloadOrigins.IsAllowedDownload(
                "https://builds.bepinex.dev/projects/bepinex_be/740/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.740.zip"),
            "a Bleeding Edge build", "BepInEx's own CI, not a third party");
        Program.Check(DownloadOrigins.IsAllowedDownload(
                $"https://github.com/{BuildInfo.ToolRepo}/releases/download/v0.1.1/UnityGameTranslatorManager-v0.1.1-win-x64.zip"),
            "this tool's own update", "it used to go unchecked");
        Program.Check(DownloadOrigins.IsAllowedDownload(
                "https://github.com/ollama/ollama/releases/download/v0.33.3/OllamaSetup.exe"),
            "the Ollama installer", "executed afterwards — the one that matters most");

        var mod = DownloadOrigins.RepositoryOf(BuildInfo.ModReleasesApi);
        Program.Check(mod is not null && DownloadOrigins.IsAllowedDownload(
                $"https://github.com/{mod}/releases/download/v0.12.1/UnityGameTranslator-BepInEx5.zip"),
            $"the mod's plugin ({mod})", "read out of the compiled releases API");

        Program.Check(!DownloadOrigins.IsAllowedDownload(
                "https://github.com/someone-else/BepInEx/releases/download/v5/BepInEx.zip"),
            "refuses another repository on github.com", "the host is shared by everybody");
        Program.Check(!DownloadOrigins.IsAllowedDownload(
                "https://github.com.attacker.net/BepInEx/BepInEx/releases/download/v5/x.zip"),
            "refuses a host that merely starts right", "Uri parses the authority");
        Program.Check(!DownloadOrigins.IsAllowedDownload(
                "https://github.com@attacker.net/BepInEx/BepInEx/releases/download/v5/x.zip"),
            "refuses our host as a user name", "the host after the @ is the real one");
        Program.Check(!DownloadOrigins.IsAllowedDownload(
                "http://github.com/BepInEx/BepInEx/releases/download/v5/x.zip"),
            "refuses plain HTTP", "anyone on the path could swap the file");
        Program.Check(!DownloadOrigins.IsAllowedDownload(
                "https://release-assets.githubusercontent.com/github-production-release-asset/1/2"),
            "refuses starting at the content host", "a start names a publisher; the content host names nobody");
        Program.Check(!DownloadOrigins.IsAllowedDownload(""), "refuses nothing", "");
    }

    public static void WhereADownloadMayLand()
    {
        Program.Section("Download origins: where a download may land");

        var github = new Uri("https://github.com/BepInEx/BepInEx/releases/download/v5/x.zip");
        var bepinex = new Uri("https://builds.bepinex.dev/projects/bepinex_be/740/x.zip");

        Program.Check(DownloadOrigins.IsAllowedLanding(github,
                new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sp=r")),
            "GitHub's content host of today", "measured 2026-09-04 — a fixed name would have refused it");
        Program.Check(DownloadOrigins.IsAllowedLanding(github,
                new Uri("https://objects.githubusercontent.com/github-production-release-asset/1/2")),
            "GitHub's content host of yesterday", "still theirs");
        Program.Check(DownloadOrigins.IsAllowedLanding(github, github),
            "no redirect at all", "");
        Program.Check(DownloadOrigins.IsAllowedLanding(bepinex, bepinex),
            "Bleeding Edge served directly", "");

        Program.Check(!DownloadOrigins.IsAllowedLanding(github, new Uri("https://attacker.net/x.zip")),
            "refuses a redirect out of GitHub", "the first address looked right; the file is not");
        Program.Check(!DownloadOrigins.IsAllowedLanding(github, new Uri("https://evilgithubusercontent.com/x")),
            "refuses a look-alike of the content domain", "the dot before the domain is required");
        Program.Check(!DownloadOrigins.IsAllowedLanding(github, new Uri("http://release-assets.githubusercontent.com/x")),
            "refuses a downgrade to HTTP on the way", "");
        Program.Check(!DownloadOrigins.IsAllowedLanding(bepinex, new Uri("https://github.com/BepInEx/BepInEx/x.zip")),
            "refuses Bleeding Edge redirecting to GitHub", "a start on one host lands on that host");
    }
}
