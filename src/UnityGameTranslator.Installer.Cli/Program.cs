using UnityGameTranslator.Installer.Core.Ai;
using UnityGameTranslator.Installer.Core.Api;
using UnityGameTranslator.Installer.Core.Catalog;
using UnityGameTranslator.Installer.Core.Detection;
using UnityGameTranslator.Installer.Core.Diagnostics;
using UnityGameTranslator.Installer.Core.Install;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;
using UnityGameTranslator.Installer.Core.Settings;

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
                "ai" => await AiAsync(args),
                "urls" => Urls(args),
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
              ai [--test] [--model M]      Find a local AI server, optionally translate one line
              ai --compare a,b,c            Score several models on the job the mod asks of them
              ai --suite --model M          Put one model through the mod's instructions, hardest last
                  [--context "..."]         ...with the game description the mod would send
              ai --ollama [--yes]           Start an installed Ollama, or price installing one
              urls <address>                Show which endpoints an address resolves to
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

        // A path is looked up in the full scan FIRST, and only probed on its own when that finds
        // nothing.
        //
        // Probing a folder in isolation skips the store scanners, so a Steam game came back as
        // "Manual" with no app id — which sent the community lookup to search by name instead of
        // by id, and reported "none found" for a game whose translation was right there. This
        // command is what a user pastes into an issue: a diagnosis that does not reproduce the
        // real detection path sends whoever reads it after the wrong thing. It sent me.
        var game = Directory.Exists(target)
            ? inventory.ScanAll().FirstOrDefault(g =>
                  string.Equals(Path.GetFullPath(g.Path).TrimEnd(Path.DirectorySeparatorChar),
                                Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                                StringComparison.OrdinalIgnoreCase))
              ?? inventory.ScanFolder(target)
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
        // Reviewed settings only: until someone has been through the settings screen, we have
        // nothing to say about their language or their backend, and writing defaults into their
        // game would look like we decided for them.
        var configured = new SettingsStore(platform).Current;
        var plan = engine.Plan(report, channel, chosen,
            configured.Reviewed ? configured : null);

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
    /// <summary>
    /// Looks for a local AI server and, with --test, actually translates a line through it.
    ///
    /// The mod only ever needs an OpenAI-compatible URL, so this asks "what answers" rather than
    /// "what is installed" — which also finds servers nobody thought to look for.
    /// </summary>
    private static async Task<int> AiAsync(string[] args)
    {
        var probe = new AiServerProbe();

        if (args.Contains("--suite", StringComparer.OrdinalIgnoreCase))
            return await SuiteAsync(probe, args);

        if (args.Contains("--ollama", StringComparer.OrdinalIgnoreCase))
            return await OllamaAsync(args);

        if (ValueOf(args, "--compare") is { } list)
            return await CompareAsync(probe, list.Split(',', StringSplitOptions.RemoveEmptyEntries));

        Console.WriteLine("Looking for a local AI server...");
        var servers = await probe.DiscoverAsync();

        if (servers.Count == 0)
        {
            Console.WriteLine("None found on the usual ports.");
            Console.WriteLine("A server on another port or another machine still works: enter its URL in the settings.");

            // Only when nothing answered, and only as a suggestion: the command that would set
            // one up is printed, never run behind their back.
            var status = await new OllamaProbe(PlatformFactory.Create()).InspectAsync();
            Console.WriteLine(status.State == OllamaState.InstalledButStopped
                ? "Ollama is installed here but stopped — 'ai --ollama' offers to start it."
                : "Nothing installed either — 'ai --ollama' says what it would take.");
            return 0;
        }

        foreach (var server in servers)
        {
            Console.WriteLine($"  {server.Product,-22} {server.Url}");
            foreach (var model in server.Models.Take(8)) Console.WriteLine($"      {model}");
            if (server.Models.Count > 8) Console.WriteLine($"      ... and {server.Models.Count - 8} more");
        }

        if (!args.Contains("--test", StringComparer.OrdinalIgnoreCase)) return 0;

        var chosen = servers.FirstOrDefault(s => s.Models.Count > 0);
        if (chosen is null)
        {
            Console.WriteLine("No model to test with: pull one first.");
            return 0;
        }

        var chosenModel = ValueOf(args, "--model") ?? chosen.Models[0];
        Console.WriteLine();
        Console.WriteLine($"Translating one line with {chosenModel}...");

        var trial = await probe.MeasureAsync(chosen.Url, chosenModel);

        if (!trial.Succeeded)
        {
            Console.WriteLine($"Failed after {trial.Elapsed.TotalSeconds:F1}s ({trial.Detail}).");
            return 4;
        }

        // Shown on one line: the answer legitimately contains a [!nl] placeholder and may carry
        // real line breaks, and a multi-line answer in the middle of a report is unreadable.
        Console.WriteLine($"  answer : {trial.Output?.ReplaceLineEndings(" / ")}");
        Console.WriteLine($"  first  : {trial.Elapsed.TotalSeconds:F1}s"
                          + (trial.FirstRunWasCold ? " (model had to be loaded)" : " (model was already loaded)"));
        Console.WriteLine(trial.WarmElapsed is { } warm
            ? $"  then   : {warm.TotalSeconds:F1}s per line"
            : "  then   : not measured");
        Console.WriteLine($"  on GPU : {trial.OnGpu switch { true => "yes", false => "NO - running on the processor", _ => "unknown (this server does not say)" }}");
        Console.WriteLine($"  VRAM   : {trial.VramText}");
        Console.WriteLine($"  keeps placeholders : {Yes(trial.KeptPlaceholders)}");
        Console.WriteLine($"  answers with the translation only : {Yes(trial.AnsweredWithTranslationOnly)}");
        Console.WriteLine();
        Console.WriteLine("This figure is optimistic: measured with no game running. In play the model");
        Console.WriteLine("shares the graphics card with the game, so expect it to be slower.");
        return 0;
    }

    /// <summary>
    /// Scores several models on the job the mod actually asks of them.
    ///
    /// Speed alone would rank a fast model that mangles placeholders above a slower one that
    /// does not, and the first is unusable: the mod displays what comes back, verbatim, into a
    /// running game. So the table carries fidelity first, then the cost of using it.
    /// </summary>
    private static async Task<int> CompareAsync(AiServerProbe probe, string[] models)
    {
        var servers = await probe.DiscoverAsync();
        var server = servers.FirstOrDefault(s => s.Models.Count > 0);

        if (server is null)
        {
            Console.Error.WriteLine("No local AI server answered.");
            return 4;
        }

        Console.WriteLine($"Testing on {server.Product} at {server.Url}");
        Console.WriteLine("Each model translates a line carrying the placeholders the mod uses.");
        Console.WriteLine();
        Console.WriteLine($"{"model",-24} {"keeps",-6} {"only",-6} {"per line",-9} {"VRAM",-8} {"GPU",-8}");
        Console.WriteLine(new string('-', 66));

        foreach (var model in models.Select(m => m.Trim()))
        {
            if (!server.Models.Any(m => m.StartsWith(model, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"{model,-24} not installed on this server");
                continue;
            }

            var trial = await probe.MeasureAsync(server.Url, model);

            if (!trial.Succeeded)
            {
                Console.WriteLine($"{model,-24} failed ({trial.Detail})");
                continue;
            }

            var perLine = trial.WarmElapsed ?? trial.Elapsed;

            // Shown as a tally, not a tick: these models are sampled, and "3 out of 4" is the
            // difference between a model that does the job and one that will corrupt a line now
            // and then — which no single draw can tell you.
            Console.WriteLine($"{model,-24} {trial.RunsKeepingPlaceholders}/{trial.Runs,-4} "
                              + $"{trial.RunsAnsweringOnly}/{trial.Runs,-4} "
                              + $"{perLine.TotalSeconds,6:F1}s   {trial.VramText,-8} "
                              + $"{trial.OnGpu switch { true => "yes", false => "NO", _ => "?" },-8}");
        }

        Console.WriteLine();
        Console.WriteLine("keeps = of how many answers the placeholders came back untouched, in order.");
        Console.WriteLine("        The mod checks this itself and retries up to three times, so a model");
        Console.WriteLine("        that misses costs you those extra calls — and when it keeps missing,");
        Console.WriteLine("        the line is left untranslated rather than shown mangled.");
        Console.WriteLine("only  = answered with the translation and nothing else.");
        Console.WriteLine("Times are measured with no game running; in play the model shares the GPU.");
        return 0;
    }

    /// <summary>
    /// Runs the instruction suite and prints, for every case, the verdict AND what the model
    /// answered.
    ///
    /// Showing the answer is not decoration. The checks are heuristics on free text and will be
    /// wrong in both directions; a reader who can see the answer catches that and decides for
    /// themselves. This tool reports, it does not certify.
    /// </summary>
    /// <summary>
    /// Start what is there, or price what is not. Never both, and never silently.
    ///
    /// --yes exists for our own end-to-end runs; without it nothing is downloaded, which matters
    /// for a 1.5 GB installer that someone may have typed this command out of curiosity about.
    /// </summary>
    private static async Task<int> OllamaAsync(string[] args)
    {
        var platform = PlatformFactory.Create();
        var probe = new OllamaProbe(platform);
        var status = await probe.InspectAsync();

        switch (status.State)
        {
            case OllamaState.Running:
                Console.WriteLine($"Ollama is already serving ({status.Detail}). Nothing to do.");
                return 0;

            case OllamaState.InstalledButStopped:
                Console.WriteLine($"Ollama is installed: {Sanitize.Path(status.ExecutablePath!)}");
                Console.WriteLine("Starting it. Nothing is downloaded.");

                var outcome = await probe.StartAsync(status.ExecutablePath!);
                if (outcome.Started)
                {
                    Console.WriteLine("It answers now.");
                    if (outcome.HowToStop is not null)
                        Console.WriteLine($"To stop it later: {outcome.HowToStop}");
                    return 0;
                }

                if (outcome.Command is not null)
                {
                    Console.Error.WriteLine("This needs administrator rights, which we will not ask for. Run:");
                    Console.Error.WriteLine($"    {outcome.Command}");
                }
                else
                {
                    Console.Error.WriteLine(outcome.Failure ?? "It would not start from here.");
                }
                return 4;
        }

        var installer = new OllamaInstaller(platform);
        var offer = await installer.PrepareAsync();

        if (!offer.CanInstall)
        {
            Console.WriteLine(offer.Refusal);
            return 4;
        }

        Console.WriteLine($"Would install {offer.AssetName} ({offer.SizeText}), checksum {offer.Sha256}");
        Console.WriteLine("A model has to be downloaded on top of that before anything can be translated.");

        if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Nothing downloaded. Add --yes to go ahead.");
            return 0;
        }

        installer.Progress += (done, total) =>
        {
            if (total is { } t && done % (20 * 1024 * 1024) < 81920)
                Console.WriteLine($"  {done / 1024 / 1024} / {t / 1024 / 1024} MB");
        };

        var failure = await installer.InstallAsync(offer);
        if (failure is null)
        {
            Console.WriteLine("Installed and answering.");
            return 0;
        }

        Console.Error.WriteLine(failure);
        return 4;
    }

    private static async Task<int> SuiteAsync(AiServerProbe probe, string[] args)
    {
        var servers = await probe.DiscoverAsync();
        var server = servers.FirstOrDefault(s => s.Models.Count > 0);
        if (server is null)
        {
            Console.Error.WriteLine("No local AI server answered.");
            return 4;
        }

        var model = ValueOf(args, "--model") ?? server.Models[0];

        // The language the user actually plays in, not a hardcoded one: an instruction suite run
        // toward a language nobody uses says nothing about the model they will live with.
        var settings = new SettingsStore(PlatformFactory.Create());
        var language = ValueOf(args, "--lang") ?? settings.ResolveTargetLanguage();

        // The mod's "describe this game" setting, which goes into the very first line of the
        // prompt. Offered here because nobody could say whether filling it in helps or hurts, and
        // running the suite both ways settles it for a given model.
        var gameContext = ValueOf(args, "--context");

        Console.WriteLine($"{model} on {server.Product}, translating to {Languages.NameOf(language)}");
        Console.WriteLine(gameContext is null
            ? "Game context: none (the mod's default wording)"
            : $"Game context: {gameContext}");

        // Said before the results, not after: someone reading marks for a job that makes no sense
        // will have drawn their conclusion long before a footnote.
        if (ModelTestSuite.IsDegenerate(language))
        {
            Console.WriteLine();
            Write("note   : ", ConsoleColor.Yellow);
            Console.WriteLine(ModelTestSuite.DegenerateCaveat);
        }

        // What we have run ourselves against this model, if anything — said before the suite so
        // it reads as background, not as a conclusion drawn from the marks below.
        var notes = await new ModelNotesProvider(PlatformFactory.Create())
            .GetAsync(offline: !settings.Current.OnlineMode);
        var note = ModelNotesProvider.Describe(notes, model);
        if (note is not null) Console.WriteLine(note);

        Console.WriteLine();

        var passed = 0;
        var total = 0;
        var echoed = 0;
        var unlocked = new List<(ModelTest Test, bool Supported)>();

        await probe.RunSuiteAsync(server.Url, model, language, gameContext: gameContext, onResult: result =>
        {
            if (result.Test.UnlocksOption is not null)
            {
                unlocked.Add((result.Test, result.Passed));
            }
            else
            {
                total++;
                if (result.Passed) passed++;
            }

            // Same vocabulary as the window, and for the same reason: an experimental test that
            // does not pass is not a KO. Amber says "this door stays closed", green says "this
            // model opens one almost none do" — the outcome that is easy to miss.
            var experimental = result.Test.UnlocksOption is not null;
            var mark = experimental
                ? (result.Passed ? "can" : "cannot")
                : (result.Passed ? "ok" : "KO");

            Write($"[{mark}] ", result.Passed
                ? ConsoleColor.Green
                : experimental ? ConsoleColor.Yellow : ConsoleColor.Red);
            Console.WriteLine($"{result.Test.Difficulty,-7} {result.Test.Name}");
            Console.WriteLine($"       asked  : {result.Test.Source.ReplaceLineEndings(" / ")}");
            Console.WriteLine($"       expect : {result.Test.Expectation}");
            Console.WriteLine($"       answer : {result.Answer?.ReplaceLineEndings(" / ") ?? "(nothing)"}");

            if (result.EchoedInstructions)
            {
                echoed++;
                Console.WriteLine($"       !!     : the model repeated the instructions. That alone makes it");
                Console.WriteLine($"                unusable as is — the mod prints this verbatim into the game.");
                Console.WriteLine($"       judged : {result.Translation}");
            }

            Console.WriteLine();
        });

        Console.WriteLine($"{passed}/{total} required instructions followed.");

        // Experimental capabilities are listed apart, and phrased as what they unlock rather
        // than as a failure: the mod ships these options disabled, so a model that cannot do one
        // is not a worse model — it just means that option stays off. Models are getting better
        // at this, and the same test will start passing on its own.
        foreach (var (test, supported) in unlocked)
        {
            Write(supported
                    ? $"Experimental option '{test.UnlocksOption}': this model can do it — you may switch it on."
                    : $"Experimental option '{test.UnlocksOption}': not followed by this model — leave it off.",
                supported ? ConsoleColor.Green : ConsoleColor.Yellow);
            Console.WriteLine();

            // Printed on success too, in amber: passing says the model is capable, not that the
            // option is safe. A green line on its own would read as a recommendation, and the
            // mod ships this option disabled precisely because it fails silently.
            if (test.Caveat is not null)
            {
                Write($"  {test.Caveat}", ConsoleColor.Yellow);
                Console.WriteLine();
            }
        }
        if (echoed > 0)
        {
            Console.WriteLine($"{echoed}/{total} answers repeated the instructions back — on its own, a reason");
            Console.WriteLine("not to use this model, whatever the marks above say.");
        }
        Console.WriteLine();
        Console.WriteLine("Read the answers, not just the marks: these checks are heuristics on free");
        Console.WriteLine("text and can be wrong either way. Whether this model is good enough is");
        Console.WriteLine("your call, not the tool's.");
        return 0;
    }

    /// <summary>
    /// Shows what an address resolves to, so a divergence from the mod is visible rather than
    /// discovered in a game. Same five rules, same order, mirrored from ResolveAIEndpoint.
    /// </summary>
    private static int Urls(string[] args)
    {
        var inputs = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

        if (inputs.Length == 0)
        {
            inputs = new[]
            {
                "http://localhost:11434",
                "https://api.openai.com/v1/chat/completions",
                "https://api.deepseek.com/chat/completions",
                "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
                "https://api.groq.com/openai/v1",
            };
            Console.WriteLine("No address given — showing the cases the mod documents.");
            Console.WriteLine();
        }

        foreach (var input in inputs)
        {
            Console.WriteLine(input);
            Console.WriteLine($"    chat   {AiEndpoint.Chat(input)}");
            Console.WriteLine($"    models {AiEndpoint.Models(input)}");
            Console.WriteLine();
        }

        return 0;
    }

    private static string Yes(bool? value) =>
        value switch { true => "yes", false => "NO", _ => "not checked" };

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

        // A path is looked up in the full scan FIRST, and only probed on its own when that finds
        // nothing.
        //
        // Probing a folder in isolation skips the store scanners, so a Steam game came back as
        // "Manual" with no app id — which sent the community lookup to search by name instead of
        // by id, and reported "none found" for a game whose translation was right there. This
        // command is what a user pastes into an issue: a diagnosis that does not reproduce the
        // real detection path sends whoever reads it after the wrong thing. It sent me.
        var game = Directory.Exists(target)
            ? inventory.ScanAll().FirstOrDefault(g =>
                  string.Equals(Path.GetFullPath(g.Path).TrimEnd(Path.DirectorySeparatorChar),
                                Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                                StringComparison.OrdinalIgnoreCase))
              ?? inventory.ScanFolder(target)
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

    /// <summary>
    /// Writes a fragment in colour and restores what was there.
    ///
    /// Restoring rather than calling ResetColor: the terminal may have been set to something the
    /// user chose, and a tool has no business flattening it on the way out. Redirected output
    /// carries no colour at all, which is exactly what a pipe wants.
    /// </summary>
    private static void Write(string text, ConsoleColor colour)
    {
        if (Console.IsOutputRedirected)
        {
            Console.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}
