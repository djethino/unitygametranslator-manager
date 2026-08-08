using UnityGameTranslator.Installer.Core.Api;
using UnityGameTranslator.Installer.Core.Catalog;
using UnityGameTranslator.Installer.Core.Detection;
using UnityGameTranslator.Installer.Core.Diagnostics;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;

namespace UnityGameTranslator.Installer.Cli;

/// <summary>
/// The command line front-end.
///
/// Not a lesser version of the GUI: it is how the detection logic gets tested against real game
/// folders, and how a user sends a usable report when something goes wrong. Every decision it
/// prints comes from Core, so what the GUI will show is what this shows.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Game names are routinely Chinese, Japanese or Cyrillic. A console left on the legacy
        // code page turns them into mojibake, which makes the tool look broken on exactly the
        // games that most need translating.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected output */ }

        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "scan";
        var offline = args.Contains("--offline", StringComparer.OrdinalIgnoreCase);

        try
        {
            return command switch
            {
                "scan" => await ScanAsync(args, offline),
                "report" => await ReportAsync(args, offline),
                "catalog" => Catalog(offline),
                "diagnose" => await DiagnoseAsync(offline),
                "-h" or "--help" or "help" => Help(),
                _ => Unknown(command),
            };
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine($"""
            UnityGameTranslator Installer {BuildInfo.Version} (command line)

              scan [--offline] [--all]     List Unity games found on this machine
              report <path or name>        Everything known about one game
              catalog [--offline]          Show the loader catalog and where it came from
              diagnose                     Printable report, safe to paste in a public issue
              help                         This text

            --offline skips every network call (catalog and community translations).
            --all also lists games that cannot be modded, with the reason.
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Help();
        return 1;
    }

    private static async Task<int> ScanAsync(string[] args, bool offline)
    {
        var showAll = args.Contains("--all", StringComparer.OrdinalIgnoreCase);
        var platform = PlatformFactory.Create();
        var catalog = new CatalogProvider(platform).Get(offline);

        Console.WriteLine($"Platform: {platform.OsId} / {platform.HostArchitecture}");
        Console.WriteLine($"Catalog : {catalog.Document.Loaders.Count} loaders (source: {catalog.Source})");
        if (catalog.Error is not null) Console.WriteLine($"          note: {catalog.Error}");
        Console.WriteLine();

        var inventory = new GameInventory(platform, catalog.Document,
                                          offline ? null : new CatalogApiClient());

        var started = DateTimeOffset.UtcNow;
        var games = inventory.ScanAll();
        var elapsed = DateTimeOffset.UtcNow - started;

        var shown = 0;
        foreach (var game in games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!game.IsModdable && !showAll) continue;
            shown++;

            var loader = LoaderProbe.Detect(game.Path, catalog.Document);
            Console.WriteLine(FormatGameLine(game, loader));
        }

        Console.WriteLine();
        Console.WriteLine($"{shown} game(s) shown out of {games.Count} Unity game(s) found in {elapsed.TotalSeconds:F1}s.");

        var hidden = games.Count - shown;
        if (hidden > 0 && !showAll)
        {
            // Never truncate silently: a user who cannot find their game must be told why.
            Console.WriteLine($"{hidden} hidden because they cannot be modded — use --all to see them and the reason.");
        }

        await Task.CompletedTask;
        return 0;
    }

    private static string FormatGameLine(GameInstall game, DetectedLoader? loader)
    {
        var runtime = game.Runtime switch
        {
            UnityRuntime.Mono => "Mono  ",
            UnityRuntime.Il2Cpp => "IL2CPP",
            _ => "?     ",
        };

        var unity = game.UnityVersion ?? "unknown";
        var loaderText = loader is null
            ? "no loader"
            : $"{loader.Display}{(loader.Version is null ? "" : " " + loader.Version)}";

        var flags = new List<string>();
        if (game.SteamAppId is not null) flags.Add($"steam:{game.SteamAppId}");
        if (game.RunsUnderProton) flags.Add("proton");
        if (!game.IsModdable) flags.Add($"BLOCKED: {game.VerdictDetail ?? game.Verdict.ToString()}");

        var suffix = flags.Count > 0 ? "  [" + string.Join(", ", flags) + "]" : "";

        return $"  {runtime}  {unity,-14} {loaderText,-26} {game.Name}{suffix}";
    }

    private static async Task<int> ReportAsync(string[] args, bool offline)
    {
        var target = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (target is null)
        {
            Console.Error.WriteLine("Usage: report <game folder or name fragment>");
            return 1;
        }

        var platform = PlatformFactory.Create();
        var catalog = new CatalogProvider(platform).Get(offline);
        var inventory = new GameInventory(platform, catalog.Document,
                                          offline ? null : new CatalogApiClient());

        var game = Directory.Exists(target)
            ? inventory.ScanFolder(target)
            : inventory.ScanAll().FirstOrDefault(g =>
                g.Name.Contains(target, StringComparison.OrdinalIgnoreCase));

        if (game is null)
        {
            Console.Error.WriteLine($"No Unity game found for '{target}'.");
            return 1;
        }

        var report = await inventory.BuildReportAsync(game, offline);
        PrintReport(report);
        return report.Blockers.Count > 0 ? 3 : 0;
    }

    private static void PrintReport(GameReport report)
    {
        var game = report.Game;

        Console.WriteLine($"Game        : {game.Name}");
        Console.WriteLine($"Path        : {game.Path}");
        Console.WriteLine($"Store       : {game.Store}{(game.SteamAppId is null ? "" : $" (app id {game.SteamAppId})")}");
        Console.WriteLine($"Runtime     : {game.Runtime}");
        Console.WriteLine($"Unity       : {game.UnityVersion ?? "unknown"}");
        Console.WriteLine($"Architecture: {game.Architecture}");
        if (game.RunsUnderProton) Console.WriteLine($"Proton      : yes ({game.ProtonPrefix})");
        Console.WriteLine();

        Console.WriteLine($"Loader      : {(report.InstalledLoader is null
            ? "none installed"
            : $"{report.InstalledLoader.Display} {report.InstalledLoader.Version ?? ""}".Trim())}");

        if (report.InstalledLoader is { ForeignPluginCount: > 0 })
        {
            Console.WriteLine($"              {report.InstalledLoader.ForeignPluginCount} other mod(s) alongside — the loader will never be removed.");
        }

        Console.WriteLine($"Plugin      : {report.InstalledPluginVersion ?? "not installed"}");
        Console.WriteLine($"Recommends  : {report.RecommendedLoader?.Display ?? "nothing"}");
        if (report.RecommendationReason is not null) Console.WriteLine($"              {report.RecommendationReason}");
        if (report.PluginBuildId is not null) Console.WriteLine($"Build       : {report.PluginBuildId}");
        Console.WriteLine();

        if (report.LocalTranslation is { } local)
        {
            var count = local.EntryCount < 0 ? "unreadable file" : $"{local.EntryCount} entries";
            Console.WriteLine($"Local trans.: {count}"
                              + (local.LocalChanges > 0 ? $", {local.LocalChanges} unsynced change(s)" : "")
                              + (local.Uuid is null ? "" : $"  [{local.Uuid}]"));
        }
        else
        {
            Console.WriteLine("Local trans.: none");
        }

        if (report.OnlineTranslations.Count > 0)
        {
            Console.WriteLine($"Online      : {report.OnlineTranslations.Count} community translation(s)");

            if (report.MatchingOnline is { } mine)
            {
                Console.WriteLine($"              * you already have this one: {mine}");
            }

            var alternatives = report.AlternativeOnline.ToList();
            foreach (var t in alternatives.Take(10)) Console.WriteLine($"              - {t}");
            if (alternatives.Count > 10)
                Console.WriteLine($"              ... and {alternatives.Count - 10} more");
        }
        else if (report.OnlineSearchError is not null)
        {
            Console.WriteLine($"Online      : search failed — {report.OnlineSearchError}");
            Console.WriteLine("              (a firewall or proxy blocking the tool looks exactly like this)");
        }
        else
        {
            Console.WriteLine("Online      : none found");
        }

        if (report.Blockers.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Blocked:");
            foreach (var blocker in report.Blockers) Console.WriteLine($"  ! {blocker}");
        }

        if (report.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Before you install:");
            foreach (var warning in report.Warnings) Console.WriteLine($"  - {warning}");
        }
    }

    private static int Catalog(bool offline)
    {
        var platform = PlatformFactory.Create();
        var result = new CatalogProvider(platform).Get(offline);

        Console.WriteLine($"Source: {result.Source}");
        if (result.Error is not null) Console.WriteLine($"Note  : {result.Error}");
        Console.WriteLine($"Built : {result.Document.GeneratedAt ?? "unknown"}");
        Console.WriteLine();

        foreach (var loader in result.Document.Loaders)
        {
            Console.WriteLine($"  {loader.Id,-18} {loader.Display,-20} v{loader.Version,-14} "
                              + $"runtimes: {string.Join('+', loader.Runtimes)}");
            Console.WriteLine($"    plugin   -> {loader.PluginDir}{(loader.PluginDirShared ? "   (shared with other mods)" : "")}");
            Console.WriteLine($"    userdata -> {loader.UserDataDir}");

            var unverified = loader.Assets.Count(a => string.IsNullOrEmpty(a.Sha256));
            if (unverified > 0)
                Console.WriteLine($"    {unverified}/{loader.Assets.Count} asset(s) without a checksum — install refused until filled.");
        }

        return 0;
    }

    private static async Task<int> DiagnoseAsync(bool offline)
    {
        var platform = PlatformFactory.Create();
        var catalog = new CatalogProvider(platform).Get(offline);
        var inventory = new GameInventory(platform, catalog.Document, api: null);
        var games = inventory.ScanAll();

        Console.WriteLine("```");
        Console.WriteLine($"tool     : UnityGameTranslator Installer {BuildInfo.Version}");
        Console.WriteLine($"platform : {platform.OsId} / {platform.HostArchitecture}");
        Console.WriteLine($"os       : {Sanitize.Text(Environment.OSVersion.VersionString)}");
        Console.WriteLine($"catalog  : {catalog.Source}, {catalog.Document.Loaders.Count} loaders, built {catalog.Document.GeneratedAt}");
        if (catalog.Error is not null) Console.WriteLine($"catalog! : {catalog.Error}");
        Console.WriteLine($"games    : {games.Count}");
        Console.WriteLine();

        foreach (var game in games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            var loader = LoaderProbe.Detect(game.Path, catalog.Document);
            Console.WriteLine($"- {game.Name}");
            Console.WriteLine($"    runtime={game.Runtime} unity={game.UnityVersion ?? "?"} arch={game.Architecture}"
                              + $" store={game.Store} proton={game.RunsUnderProton}");
            Console.WriteLine($"    loader={(loader is null ? "none" : loader.Id + " " + (loader.Version ?? "?"))}"
                              + $" verdict={game.Verdict}{(game.VerdictDetail is null ? "" : " (" + game.VerdictDetail + ")")}");
            Console.WriteLine($"    path={Sanitize.Path(game.Path)}");
        }
        Console.WriteLine("```");
        Console.WriteLine();
        Console.WriteLine("The block above holds no user name, no home directory and no game library listing.");
        Console.WriteLine("Nothing was sent anywhere: copy it into an issue only if you want to.");

        await Task.CompletedTask;
        return 0;
    }
}
