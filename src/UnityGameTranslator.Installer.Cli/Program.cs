using UnityGameTranslator.Installer.Core.Api;
using UnityGameTranslator.Installer.Core.Catalog;
using UnityGameTranslator.Installer.Core.Detection;
using UnityGameTranslator.Installer.Core.Diagnostics;
using UnityGameTranslator.Installer.Core.Install;
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
                "install" or "update" => await InstallAsync(args),
                "uninstall" => await UninstallAsync(args),
                "forget" => await ForgetAsync(args),
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
              install <path or name>       Set up the loader and the plugin
              update <path or name>        Same thing: reinstalls the current release
              uninstall <path or name>     Remove what was installed
              forget <path or name>        Undo what you told us about a game
              catalog [--offline]          Show the loader catalog and where it came from
              diagnose                     Printable report, safe to paste in a public issue
              help                         This text

            --offline skips every network call (catalog and community translations).
            --all also lists games that cannot be modded, with the reason.
            --beta uses pre-release plugin builds.
            --runtime mono|il2cpp   tell us what we could not read
            --arch x86|x64          tell us what we could not read
            --force                 proceed despite a refusal (never for an anti-cheat)
            --yes skips the confirmation prompt.
            --loader, --settings  (uninstall) also remove the mod loader / your settings
                                  and translations. Both are off by default; settings and
                                  translations are copied aside before being deleted.
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

    private static async Task<int> InstallAsync(string[] args)
    {
        var context = await ResolveGameAsync(args, offline: false);
        if (context is null) return 1;

        var (platform, catalog, report, inventory) = context.Value;

        var channel = args.Contains("--beta", StringComparer.OrdinalIgnoreCase)
            ? ReleaseChannel.Beta
            : ReleaseChannel.Stable;

        // The recommendation is a default. --loader lets the user override it, because some
        // games work with one loader and not another for reasons no probe can see.
        var wanted = ValueOf(args, "--loader");
        LoaderDescriptor? chosen = null;
        if (wanted is not null)
        {
            chosen = report.EligibleLoaders.FirstOrDefault(
                l => string.Equals(l.Id, wanted, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
            {
                Console.Error.WriteLine($"'{wanted}' is not usable for this game. Available:");
                foreach (var loader in report.EligibleLoaders)
                    Console.Error.WriteLine($"  {loader.Id,-18} {loader.Display} {loader.Version}");
                return 1;
            }
        }

        // Answers for a game we could not read. Recorded, so the next run does not ask again.
        var runtimeArg = ValueOf(args, "--runtime");
        var archArg = ValueOf(args, "--arch");
        var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);

        if (runtimeArg is not null || archArg is not null || force)
        {
            var value = new GameOverride
            {
                Runtime = runtimeArg?.ToLowerInvariant() switch
                {
                    "mono" => UnityRuntime.Mono,
                    "il2cpp" => UnityRuntime.Il2Cpp,
                    _ => null,
                },
                Architecture = archArg?.ToLowerInvariant() switch
                {
                    "x86" => GameArchitecture.X86,
                    "x64" => GameArchitecture.X64,
                    _ => null,
                },
                IgnoreVerdict = force,
            };

            if (force && !ModdabilityProbe.CanBeOverridden(report.Game.Verdict)
                      && report.Game.Verdict != ModdabilityVerdict.Ok)
            {
                Console.Error.WriteLine(
                    $"--force does not apply here: {ModdabilityProbe.Explain(report.Game)}");
                return 3;
            }

            inventory.Overrides.Set(report.Game.Path, value);
            inventory.Overrides.Apply(report.Game);
            report = await inventory.BuildReportAsync(report.Game, offline: false);

            if (report.Game is { VerdictOverridden: true, OverriddenVerdict: { } overruled })
            {
                Console.WriteLine($"Proceeding despite: {ModdabilityProbe.Explain(
                    new GameInstall { Name = report.Game.Name, Path = report.Game.Path,
                                      Verdict = overruled, VerdictDetail = report.Game.VerdictDetail })}");
                Console.WriteLine(ModdabilityProbe.OverrideCaveat(overruled));
            }
            Console.WriteLine();
        }

        var engine = new InstallEngine(platform, catalog, new ModReleaseClient());
        var plan = engine.Plan(report, channel, chosen);

        if (plan is null)
        {
            Console.Error.WriteLine($"Cannot install into {report.Game.Name}:");
            foreach (var blocker in report.Blockers) Console.Error.WriteLine($"  ! {blocker}");
            if (report.Blockers.Count == 0)
                Console.Error.WriteLine($"  ! {report.RecommendationReason ?? "no suitable loader"}");
            return 3;
        }

        if (report.InstalledLoader is null && report.EligibleLoaders.Count > 1 && chosen is null)
        {
            var others = report.EligibleLoaders.Where(l => l != plan.Loader).Select(l => l.Id);
            Console.WriteLine($"Using {plan.Loader.Display} (recommended). " +
                              $"Other options: --loader {string.Join(" / --loader ", others)}");
            Console.WriteLine();
        }

        // Nothing is written before this is shown and accepted.
        Console.WriteLine("This will:");
        foreach (var line in plan.Describe()) Console.WriteLine($"  - {line}");

        // Recomputed for the loader actually chosen, which may not be the recommended one.
        foreach (var warning in inventory.WarningsFor(plan.Loader, report.Game, plan.InstallLoader))
            Console.WriteLine($"  ! {warning}");
        Console.WriteLine();

        if (!Confirm(args, "Proceed?")) { Console.WriteLine("Cancelled. Nothing was written."); return 0; }

        engine.Status += message => Console.WriteLine($"  {message}");
        var outcome = await engine.ApplyAsync(plan);

        Console.WriteLine();
        Console.WriteLine(outcome.Message);
        return outcome.Success ? 0 : 4;
    }

    /// <summary>
    /// Drops the answers the user gave for a game — a forced runtime, a forced architecture, a
    /// refusal they overruled. The way back has to exist, or the first answer is permanent.
    /// </summary>
    private static async Task<int> ForgetAsync(string[] args)
    {
        var context = await ResolveGameAsync(args, offline: true);
        if (context is null) return 1;

        var (_, _, report, inventory) = context.Value;

        if (inventory.Overrides.For(report.Game.Path) is null)
        {
            Console.WriteLine("Nothing was overridden for this game.");
            return 0;
        }

        if (ReceiptStore.Read(report.Game.Path) is not null)
        {
            Console.WriteLine("Something is still installed here. Uninstall it first, or the " +
                              "files stay behind and this tool will no longer offer to remove them.");
        }

        if (!Confirm(args, "Forget what you told us about this game?")) return 0;

        inventory.Overrides.Clear(report.Game.Path);
        Console.WriteLine("Done. It will be judged from its files again.");
        return 0;
    }

    private static async Task<int> UninstallAsync(string[] args)
    {
        var context = await ResolveGameAsync(args, offline: true);
        if (context is null) return 1;

        var (platform, catalog, report, _) = context.Value;
        var engine = new UninstallEngine(platform, catalog);

        var available = engine.Available(report.Game);
        if (!available.RemovePlugin && !available.RemoveLoader)
        {
            Console.Error.WriteLine(
                $"{report.Game.Name} has no install receipt — this tool did not set it up, so " +
                "it will not remove anything.");
            return 3;
        }

        var choice = new UninstallChoice(
            RemovePlugin: true,
            RemoveLoader: args.Contains("--loader", StringComparer.OrdinalIgnoreCase) && available.RemoveLoader,
            RemoveUserData: args.Contains("--settings", StringComparer.OrdinalIgnoreCase));

        Console.WriteLine("This will remove:");
        Console.WriteLine("  - the plugin");
        if (choice.RemoveLoader) Console.WriteLine("  - the mod loader (we installed it, nothing else uses it)");
        else if (!available.RemoveLoader) Console.WriteLine("  (the loader stays: it was already there, or other mods use it)");

        Console.WriteLine(choice.RemoveUserData
            ? "  - your settings and translations, copied aside first"
            : "  (your settings and translations stay — add --settings to remove them)");
        Console.WriteLine();

        if (!Confirm(args, "Proceed?")) { Console.WriteLine("Cancelled. Nothing was removed."); return 0; }

        var outcome = engine.Apply(report.Game, choice);

        Console.WriteLine();
        Console.WriteLine(outcome.Message);
        foreach (var item in outcome.Kept) Console.WriteLine($"  kept: {item}");
        if (outcome.BackupPath is not null)
            Console.WriteLine($"Your translations were copied to: {outcome.BackupPath}");

        return outcome.Success ? 0 : 4;
    }

    /// <summary>Shared front half of install/uninstall: find the game and describe it.</summary>
    private static async Task<(IPlatform, LoaderCatalogDocument, GameReport, GameInventory)?> ResolveGameAsync(
        string[] args, bool offline)
    {
        var target = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (target is null)
        {
            Console.Error.WriteLine("Usage: <command> <game folder or name fragment>");
            return null;
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
            return null;
        }

        Console.WriteLine($"{game.Name}  ({game.Runtime}, Unity {game.UnityVersion ?? "unknown"})");
        Console.WriteLine($"{game.Path}");
        Console.WriteLine();

        var report = await inventory.BuildReportAsync(game, offline);
        return (platform, catalog.Document, report, inventory);
    }

    /// <summary>Reads "--name value" from the arguments, or null when absent.</summary>
    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.FindIndex(args,
            a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        return index >= 0 && index + 1 < args.Length
               && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : null;
    }

    private static bool Confirm(string[] args, string question)
    {
        if (args.Contains("--yes", StringComparer.OrdinalIgnoreCase)) return true;

        // A redirected stdin cannot answer. Refusing is the safe reading of silence.
        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"{question} (no console to ask on — pass --yes to confirm)");
            return false;
        }

        Console.Write($"{question} [y/N] ");
        var answer = Console.ReadLine();
        return answer?.Trim().StartsWith('y') == true
               || answer?.Trim().StartsWith('Y') == true;
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
