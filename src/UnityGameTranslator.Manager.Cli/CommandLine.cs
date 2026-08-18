using UnityGameTranslator.Manager.Core.Ai;
using UnityGameTranslator.Manager.Core.Api;
using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Diagnostics;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Settings;
using UnityGameTranslator.Manager.Core.Update;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Cli;

/// <summary>
/// The command line front-end.
///
/// Not a lesser version of the GUI: it is how the detection logic gets tested against real game
/// folders, and how a user sends a usable report when something goes wrong. Every decision it
/// prints comes from Core, so what the GUI will show is what this shows.
///
/// ⚠ This is a library, not a program of its own. The tool ships as ONE executable with two
/// faces: run with a known command it behaves as this front-end, run with nothing it opens the
/// window. Two binaries would mean two files to replace on every update and two things to sign,
/// for one product.
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Every verb this front-end answers to.
    ///
    /// Held in one place because two callers need it: the dispatch below, and the decision of
    /// whether the arguments are a command at all. A folder dropped onto the executable is not a
    /// command — sending it here would print "Unknown command" into a console that vanishes,
    /// which reads as "the tool did nothing".
    /// </summary>
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "scan", "report", "catalog", "diagnose", "install", "update", "uninstall",
        "restore", "forget", "ai", "urls", "self-update", "help", "-h", "--help",
    };

    /// <summary>
    /// True when these arguments ask for the command line rather than the window.
    ///
    /// Anything unrecognised opens the window on purpose: a person who double-clicks the
    /// executable, or drops a game folder on it, wants the tool — not an error message.
    /// </summary>
    public static bool Handles(string[] args) =>
        args.Length > 0 && (Commands.Contains(args[0]) || args[0].StartsWith('-'));

    public static async Task<int> RunAsync(string[] args)
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
                "catalog" => await CatalogAsync(offline),
                "diagnose" => await DiagnoseAsync(offline),
                "install" or "update" => await InstallAsync(args),
                "uninstall" => await UninstallAsync(args),
                "restore" => await RestoreAsync(args),
                "forget" => await ForgetAsync(args),
                "ai" => await AiAsync(args),
                "urls" => Urls(args),
                "self-update" => await SelfUpdateAsync(args),
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
            UnityGameTranslator Manager {BuildInfo.Version} (command line)

              scan [--offline] [--all]     List Unity games found on this machine
              report <path or name>        Everything known about one game
              install <path or name>       Set up the loader and the plugin
              update <path or name>        Same thing: reinstalls the current release
              uninstall <path or name>     Remove what was installed
              restore <path or name>       Put back the files this game had before we replaced them
              forget <path or name>        Undo what you told us about a game
              ai [--test] [--model M]      Find a local AI server, optionally translate one line
              ai --compare a,b,c            Score several models on the job the mod asks of them
              ai --suite --model M          Put one model through the mod's instructions, hardest last
                  [--context "..."]         ...with the game description the mod would send
                  [--game "..."]            ...and the game name, as the mod sends it
                  [--no-rate]               skip the marks it gives its own answers
                  [--judge M]               ...or have another model give them
              ai --ollama [--yes]           Start an installed Ollama, or price installing one
              urls <address>                Show which endpoints an address resolves to
              self-update [--check]        Update this tool itself (--check only looks)
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
                                  and translations. Both are off by default. The translation
                                  is backed up one last time first, and the backups stay.
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

        // Timed and printed because the window repeats it on a clock: a sweep whose cost nobody
        // measured is a promise that it is cheap, and this is the number that keeps the promise.
        var sweepStarted = DateTimeOffset.UtcNow;
        var running = RunningGames.Sweep(games);
        var sweepTook = DateTimeOffset.UtcNow - sweepStarted;

        Console.WriteLine($"Running now: {running.Paths.Count} of them "
                          + $"(looked in {sweepTook.TotalMilliseconds:F0} ms).");

        foreach (var path in running.Paths) Console.WriteLine($"  {path}");

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
        var settings = new SettingsStore(platform).Current;

        // 🔴 **The token and the lineages, exactly as the window passes them.** Without them this
        // command answered a strictly smaller question than the screen it is meant to describe: no
        // role, so "your Main" and "somebody else's lineage" printed identically, and no vote on
        // the community entries. One binary with two faces has to reach one answer.
        var lineages = new AccountLineages();
        if (!offline) await lineages.EnsureAsync(settings.ApiToken);

        var inventory = new GameInventory(platform, catalog.Document,
                                          offline ? null : new CatalogApiClient(),
                                          offline ? null : settings.ApiToken)
        {
            Lineages = offline ? null : lineages,

            // The same version comparison the window makes. This command is what somebody pastes
            // into an issue, describing the state the window described to them — a CLI that
            // quietly knew less would send whoever reads it after a difference that is not there.
            //
            // Wired here and not on the install path: there it would spend a request on the rate
            // limited API to print nothing.
            Releases = offline ? null : new PluginReleases(),

            // 🔴 Which BepInEx 6 stream to measure "up to date" against, warmed just below. This
            // command is the one somebody pastes into an issue; comparing against the catalogue's
            // pinned version here while the window compares against the resolved build would put
            // two different verdicts about one game into one conversation.
            BepInEx6Channel = settings.BepInEx6Channel,
        };

        // ⚠ The resolver's cache lives in the process, so a command that just started knows
        // nothing until it asks. Without this the comparison silently used the catalogue's pin —
        // which is exactly how "up to date" appeared beside a newer published build.
        if (!offline)
        {
            await new LoaderBuildResolver()
                .WarmAsync(catalog.Document, settings.BepInEx6Channel)
                .ConfigureAwait(false);
        }

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

        // Read here rather than inside PrintReport: the catalog is what turns an installed loader
        // id into the descriptor that knows where its config.json lives.
        var installed = report.InstalledLoader is null
            ? null
            : catalog.Document.Loaders.FirstOrDefault(l => l.Id == report.InstalledLoader.Id);

        PrintReport(report, GameConfigWriter.Read(report.Game.Path, installed));
        PrintSituation(platform, report, offline);
        PrintModSettings(platform, catalog.Document, report);
        return report.Blockers.Count > 0 ? 3 : 0;
    }

    /// <summary>
    /// The two lines the game list shows for this game, in the words it shows them.
    ///
    /// 🔴 Printed because otherwise they are **unverifiable without opening the window** — and the
    /// list is precisely where a wrong sync verdict or a missing role hides, since nothing else
    /// renders them. SituationReader had been re-deriving both from a line count and a file
    /// timestamp; that survived a long time because no command ever said what it produced.
    /// </summary>
    private static void PrintSituation(IPlatform platform, GameReport report, bool offline)
    {
        var settings = new SettingsStore(platform).Current;
        var language = new SettingsStore(platform).ResolveTargetLanguage();
        var situation = SituationReader.Read(report, language, onlineChecked: !offline,
                                             signedInAs: settings.ApiUser);

        Console.WriteLine();
        Console.WriteLine($"In the list : {situation.Headline}");

        if (situation.Detail is { Length: > 0 } detail)
            Console.WriteLine($"              {detail}");

        if (report.Sync is { } sync) Console.WriteLine($"              (sync verdict: {sync})");
    }

    /// <summary>
    /// What this game would be configured with, and where that comes from.
    ///
    /// 🔴 **The only way to check any of this without a window.** Whether a game follows the
    /// defaults or holds answers of its own, and which settings would move, is now a real question
    /// with three possible sources — and this project's own rule is that the card gets judged by
    /// clicking it, which nobody has done yet. A report that stops short of the answer leaves the
    /// behaviour unverifiable until somebody does.
    ///
    /// ⚠ Same Core calls as the window, deliberately. A second way of working it out here would be
    /// a second answer, and this text is what gets pasted into an issue.
    /// </summary>
    private static void PrintModSettings(IPlatform platform, LoaderCatalogDocument catalog,
                                         GameReport report)
    {
        var defaults = new SettingsStore(platform).Current;
        if (!defaults.Reviewed) return;

        var preference = new GamePreferences(platform).Read(report.Game.Path);

        var descriptor = report.InstalledLoader is null
            ? null
            : catalog.Loaders.FirstOrDefault(l => l.Id == report.InstalledLoader.Id);

        var snapshot = GameConfigWriter.Read(report.Game.Path, descriptor);
        var mine = preference.UsesModDefaults(snapshot);

        Console.WriteLine();
        Console.WriteLine($"Mod settings: {(mine
            ? "Mod defaults"
            : $"this game's own ({preference.Mod?.Count ?? 0} set for this game)")}");

        // ⚠ Said whichever way the box went. "Nobody has decided, and this game is already
        // configured, so it keeps what it has" is the whole rule, and it is invisible in the stored
        // file — apply_mod_defaults is simply absent there.
        if (preference.ApplyModDefaults is null)
        {
            Console.WriteLine(snapshot.IsConfigured
                ? "              nobody has chosen for this game - it is already configured, so it keeps its own settings"
                : "              nobody has chosen for this game - it is not configured yet, so it follows Mod defaults");
        }

        if (descriptor is null)
        {
            Console.WriteLine("              no loader installed, so nothing has been written yet");
            return;
        }

        if (!snapshot.Exists)
        {
            Console.WriteLine("              this game has no configuration yet - one would be created");
            return;
        }

        var settings = ModSettingsResolver.Resolve(defaults, preference, snapshot);

        var picked = GameLanguages.Resolve(settings.TargetLanguage, platform.SystemLanguage());
        var target = GameLanguages.TargetFor(report, descriptor, picked);

        // ⚠ Said out loud, because otherwise the language setting looks broken. A game already
        // holding a translation keeps that translation's language — its target is what the file IS,
        // not a preference — so a language chosen here produces nothing, and nothing explains why.
        if (!string.Equals(target, Languages.NameOf(picked), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"              stays on {target}: it already holds a translation in "
                              + $"that language, so {Languages.NameOf(picked)} is not written");
        }

        var differences = new GameConfigWriter()
            .Compare(report.Game.Path, descriptor, settings, target, preference);

        if (differences.Count == 0)
        {
            Console.WriteLine("              this game already matches - nothing would be written");
            return;
        }

        foreach (var difference in differences)
        {
            // ASCII only, like the stray-plugin lines above: this ends up pasted into an issue,
            // through a console that mangles the arrows the rest of this file uses.
            // ⚠ The kept line names Mod defaults rather than saying "yours". Only the hotkey
            // produces a non-writing difference, and its replacement can only ever come from Mod
            // defaults — there is deliberately no per-game hotkey setting. See GameModOverrides.
            Console.WriteLine(difference.Writes
                ? $"              - {difference.Label}: {difference.InGame} -> {difference.Ours}"
                : $"              . {difference.Label}: {difference.InGame} (kept; Mod defaults uses {difference.Ours})");
        }
    }

    /// <summary>
    /// One version against what is published, in the words the window uses.
    ///
    /// ⚠ "up to date" is never printed on a lookup that failed. This text ends up in issues, and
    /// a report that claims a plugin is current when nothing could be reached would have whoever
    /// reads it looking for a bug in a version that was never checked.
    /// </summary>
    /// <summary>The catalog entry for the loader this game has, or null when it has none we know.</summary>
    private static LoaderDescriptor? InstalledDescriptorFor(GameReport report) =>
        report.InstalledLoader is null
            ? null
            : new CatalogProvider(PlatformFactory.Create()).Get(offline: true)
                  .Document.Loaders.FirstOrDefault(l => l.Id == report.InstalledLoader.Id);

    private static string Standing(VersionStanding standing) => standing switch
    {
        { CheckFailed: { } why } => $"could not check for a newer version ({why})",
        { UpdateAvailable: true } => $"{standing.Available} is available",
        { UpToDate: true } => "up to date",

        // Two publication lines of the same version, which nobody can rank — see VersionStanding.
        // Named rather than left to "no version information": both numbers ARE known, and the
        // reader can decide for themselves once they are told what is on offer.
        { NotComparable: true } =>
            $"{standing.Available} is published on the channel you chose — a different line from "
            + $"{standing.Installed}, so neither is newer than the other",
        { IsInstalled: false, Available: { } offered } => $"{offered} would be installed",
        _ => "no version information",
    };

    private static void PrintReport(GameReport report, GameConfigSnapshot config)
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

        if (report.LoaderStanding is { } loaderStanding) Console.WriteLine($"              {Standing(loaderStanding)}");

        // ⚠ Right under the version it qualifies. The standing is now reported for a loader
        // whoever installed it - withholding the fact was worse than stating it - but a bare
        // "6.0.2 available" on somebody else's loader reads as a job this tool is failing to do.
        if (report.InstalledLoader is { InstalledByUs: false })
        {
            Console.WriteLine("              not installed by this tool - other mods may need this "
                              + "exact version, so it is never updated or removed from here");
        }

        Console.WriteLine($"Plugin      : {report.InstalledPluginVersion ?? "not installed"}");

        if (report.PluginStanding is { } pluginStanding) Console.WriteLine($"              {Standing(pluginStanding)}");

        // ⚠ Printed right under the version, because it changes what that version MEANS. This
        // command is what gets pasted into an issue, and it reported "0.11 , up to date" about a
        // game running an assembly the loader reads first from somewhere else — or possibly not
        // at all. The window said so; this did not, and the two must not disagree.
        if (report.StrayPluginDirectories.Count > 0)
        {
            // ASCII only on this line: the console this ends up in mangles the em dash the rest
            // of this file uses, and a report pasted into an issue must stay readable.
            Console.WriteLine(report.PluginInPlace
                ? $"              ALSO installed in {string.Join(", ", report.StrayPluginDirectories)}"
                  + $" - only {report.PluginDirectory}/ is updated, so the game may keep running the other"
                : $"              installed in {report.StrayPluginDirectories[0]}/, not "
                  + $"{report.PluginDirectory}/ - it may load late or not at all");
        }
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

        // ⚠ The same history the window lists, printed here for the same reason every other line
        // is: this command is what somebody pastes into an issue, and a copy they can no longer
        // find is exactly the kind of thing they paste it about.
        if (InstalledDescriptorFor(report) is { } backupsLoader)
        {
            var kept = TranslationBackupStore.List(report.Game.Path, backupsLoader);
            var saved = Backups.SavedCount(kept);

            Console.WriteLine($"Backups     : {saved} saved by you, {kept.Count - saved} automatic");

            foreach (var entry in kept)
            {
                var what = string.IsNullOrEmpty(entry.Label)
                    ? Backups.Describe(entry.Reason, entry.By)
                    : $"\"{entry.Label}\"";

                Console.WriteLine($"              {entry.At:dd MMM HH:mm}  {entry.Lines,6} lines  {what}");
            }
        }

        // What starting this game would produce, from the game's own configuration. The window
        // prints the same answer on the Play button, from the same Core call — a screen and a
        // command disagreeing about what a game does is the failure that rule exists to prevent.
        var promise = PlayPromises.For(report, config);
        Console.WriteLine($"Pressing Play: {PlayPromises.Label(promise)} — {PlayPromises.Explain(promise)}");

        // ⚠ The verdict a running game would reach, reached without one. Printed here because this
        // command is what somebody pastes into an issue about sync, and "the mod says X, the
        // manager says Y" is the report that matters.
        if (report.Sync is { } sync)
        {
            Console.WriteLine($"Sync        : {sync switch
            {
                SyncDirection.InSync => "in sync with the published version",
                SyncDirection.Download => "the published version has moved — nothing of yours is at risk",
                SyncDirection.Upload => "you have changes the server does not",
                SyncDirection.Merge => "both moved — the mod has the screens to settle it line by line",
                _ => sync.ToString(),
            }}");
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

        var engine = new InstallEngine(platform, catalog, GitHubReleaseClient.ForMod())
        {
            // Same stream the report named, so what is installed is what was announced.
            BepInEx6Channel = new SettingsStore(platform).Current.BepInEx6Channel,
        };
        // Reviewed settings only: until someone has been through the settings screen, we have
        // nothing to say about their language or their backend, and writing defaults into their
        // game would look like we decided for them.
        var configured = new SettingsStore(platform).Current;

        // 🔴 The SAME settings the window would write, resolved by the same Core call. This command
        // used to pass the bare defaults and no preference at all, so a game the card protects —
        // one carrying a configuration somebody set up inside the mod — was quietly overwritten the
        // moment it was installed into from a terminal. One binary with two faces has to mean one
        // answer: what `manager install` writes is what the window would have written.
        var preference = new GamePreferences(platform).Read(report.Game.Path);

        var settings = ModSettingsResolver.Resolve(
            configured, preference,
            GameConfigWriter.Read(report.Game.Path, report.InstalledLoader is null
                ? null
                : catalog.Loaders.FirstOrDefault(l => l.Id == report.InstalledLoader.Id)));

        // ⚠ --beta still wins: it is this run's explicit instruction, and an option typed on the
        // line must not be overruled by something remembered. Without it, the game's own channel
        // answers — which is the whole point of being able to test a pre-release in one game.
        if (channel == ReleaseChannel.Stable && settings.Channel == "beta")
            channel = ReleaseChannel.Beta;

        var plan = engine.Plan(report, channel, chosen,
            configured.Reviewed ? settings : null,
            configured.Reviewed ? preference : null);

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
            // 🔴 Not "recommended" — see GameInventory.ResolveDescriptor. Saying which one is
            // being used, and how to say otherwise, is the whole of what we know.
            Console.WriteLine($"Using {plan.Loader.Display}. " +
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
        Console.WriteLine($"  on GPU : {trial.GpuText}");
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
                              + $"{trial.OnCardShare switch { null => "?", var share => $"{(int)Math.Round(share.Value * 100)}%" },-8}");
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

        // The game's own name, which the mod sends whenever the game states one. Offered for the
        // same reason and it settles a real doubt: a model that knows the game should gain its
        // vocabulary, one that has never heard of it may invent an universe instead.
        var gameName = ValueOf(args, "--game");

        // The second pass, on by default here: this command measures ONE model deliberately, and
        // the marks are the only thing said about the cases no check can judge. It costs one extra
        // request per case, so --no-rate is there for whoever is paying per request.
        var rate = !args.Contains("--no-rate", StringComparer.OrdinalIgnoreCase);

        // Marking with another model measures the bias rather than removing it: a model prefers
        // text that reads like its own, and the gap between the two runs is the only way to see
        // how far that goes.
        var judge = ValueOf(args, "--judge");

        Console.WriteLine($"{model} on {server.Product}, translating to {Languages.NameOf(language)}");
        Console.WriteLine(gameContext is null
            ? "Game context: none (the mod's default wording)"
            : $"Game context: {gameContext}");
        Console.WriteLine(gameName is null
            ? "Game name: none (as in a game that does not state one)"
            : $"Game name: {gameName}");

        // Which language it is translating FROM, named rather than assumed. It is chosen so it is
        // never the target — asking for English from English is a job the mod never gives a model
        // — and someone whose games are written in another language can say so with --source.
        var from = ModelTestSuite.SourceFor(language, ValueOf(args, "--source"));
        Console.WriteLine($"Translating from: {from.Language}");

        // What we have run ourselves against this model, if anything — said before the suite so
        // it reads as background, not as a conclusion drawn from the marks below.
        var notes = await new ModelNotesProvider(PlatformFactory.Create())
            .GetAsync(offline: !settings.Current.OnlineMode);
        var note = ModelNotesProvider.Describe(notes, model);
        if (note is not null) Console.WriteLine(note);

        // What it is about to spend, before spending it. Free on a local server, billed per
        // request and per token on a paid endpoint — and this run is not small.
        var cost = ModelTestSuite.Cost(language, gameContext, ValueOf(args, "--source"),
                                       gameName, rate);

        Write($"{cost.Cases} tests, {cost.Requests} requests"
              + (cost.MostRequests > cost.Requests ? $" (up to {cost.MostRequests} with retries)" : "")
              + $", about {cost.AboutTokens:N0} tokens sent."
              + Environment.NewLine, ConsoleColor.DarkGray);
        Write("A paid service charges for every one of them." + Environment.NewLine,
              ConsoleColor.DarkGray);

        Console.WriteLine();

        var passed = 0;
        var total = 0;
        var echoed = 0;
        var unlocked = new List<(ModelTest Test, bool Supported)>();
        var outcomes = new List<ModelTestResult>();

        await probe.RunSuiteAsync(server.Url, model, language, gameContext: gameContext,
                                  sourceCode: ValueOf(args, "--source"), gameName: gameName,
                                  rate: rate, judge: judge,
                                  onResult: result =>
        {
            outcomes.Add(result);

            if (result.Test.UnlocksOption is not null)
            {
                unlocked.Add((result.Test, result.Passed));
            }
            else
            {
                // ⚠ A case nobody judged is not a case that failed. Counted here, four unjudged
                // cases turned a perfect 16/16 into "16/20" — a model marked down for work the
                // tool deliberately declined to grade.
                if (!result.Test.ForReading)
                {
                    total++;
                    if (result.Passed) passed++;
                }
            }

            // Same vocabulary as the window, and for the same reason: an experimental test that
            // does not pass is not a KO. Amber says "this door stays closed", green says "this
            // model opens one almost none do" — the outcome that is easy to miss.
            //
            // A case with no verdict is a third thing again: nobody judged it, so it is neither.
            // Counting it as a pass would inflate the score with work nobody checked.
            var experimental = result.Test.UnlocksOption is not null;
            // The same word the window shows. "--" said nothing, and the two front ends have to
            // name the same fact the same way.
            var mark = result.Test.ForReading
                ? "read"
                : experimental
                    ? (result.Passed ? "can" : "cannot")
                    : (result.Passed ? "ok" : "KO");

            Write($"[{mark}] ", result.Test.ForReading
                ? ConsoleColor.Cyan
                : result.Passed
                    ? ConsoleColor.Green
                    : experimental ? ConsoleColor.Yellow : ConsoleColor.Red);
            // What the line cost, on the same row as its verdict. A model that passes every case
            // on the third try is not the same model as one that passes on the first, and the
            // difference is the wait a player sits through.
            // Always the count, and always over the ceiling: three of three is a line that came
            // within one attempt of being abandoned, which a bare "3" does not say.
            var cost = $"   {result.Elapsed.TotalSeconds:F1}s over "
                     + (result.Test.CanBeAskedAgain
                        ? $"{result.Attempts} of {Placeholders.MaxAttempts} requests"
                        : $"{result.Attempts} request — nothing to check, so no second one");

            if (!result.Accepted) cost += ", refused — left untranslated";
            else if (result.Repaired) cost += ", repaired by the mod";
            // Not a failure: the mod takes this off before a player sees it. Said
            // because it is a habit rather than an accident — a model that wraps one
            // answer wraps them all, and that separates two models that both pass.
            if (result.NeededCleaning) cost += ", wrapped, cleaned by the mod";

            Console.Write($"{result.Test.Difficulty,-7} {result.Test.Name}");
            Write(cost + Environment.NewLine,
                  result.Accepted ? ConsoleColor.DarkGray : ConsoleColor.Red);
            Console.WriteLine($"       asked  : {result.Test.Source.ReplaceLineEndings(" / ")}");
            Console.WriteLine(result.Test.ForReading
                ? $"       read   : {result.Test.ReadThisFor}"
                : $"       expect : {result.Test.Expectation}");
            Console.WriteLine($"       answer : {result.Answer?.ReplaceLineEndings(" / ") ?? "(nothing)"}");

            // The mark, when one was asked for. Never presented as a verdict — the word is there
            // to say who produced it, and the caveat under the run says how far it goes.
            if (result.SelfAssessment is { } assessment)
                Console.WriteLine($"       self   : {assessment}/10 (self-assessment)");

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

        // The marks, gathered into one figure. Kept apart from the score above and named for what
        // it is: the model grading itself. See ModelTestResult.SelfAssessment for how far it goes.
        var marks = outcomes.Where(o => o.SelfAssessment is not null)
                            .Select(o => o.SelfAssessment!.Value)
                            .ToList();

        if (marks.Count > 0)
        {
            Console.WriteLine($"Self-assessment: {marks.Average():F1}/10 on average, over "
                              + $"{marks.Count} answer(s) — the model grading its own work, not a verdict.");
        }

        var helped = outcomes.Count(r => r.Test.UnlocksOption is null && r.PassedWithHelp);
        if (helped > 0)
        {
            // Said plainly: a mark obtained after the mod corrected something is not the same as
            // one obtained first time. Both lines work in a game; only one model got them right.
            Console.WriteLine(helped == 1
                ? "1 of those was wrong at first and passed only after the mod corrected it."
                : $"{helped} of those were wrong at first and passed only after the mod corrected them.");
        }

        if (ModelTestSuite.Summarise(outcomes) is { Length: > 0 } summary)
            Console.WriteLine(summary);

        if (probe.LastPlacement is { } placement)
            Console.WriteLine($"This model holds {placement}");

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
                Endpoints.OllamaDefault,
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
            Console.WriteLine($"    chat   {Endpoints.Chat(input)}");
            Console.WriteLine($"    models {Endpoints.Models(input)}");
            Console.WriteLine();
        }

        return 0;
    }

    private static string Yes(bool? value) =>
        value switch { true => "yes", false => "NO", _ => "not checked" };

    /// <summary>
    /// Writes back what this game had before we replaced it — its own mod loader, most often.
    ///
    /// 🔴 A verb of its own, never a step of uninstall. Removing our files and writing somebody
    /// else's back are opposite motions, and doing both under one word left a person who asked
    /// for a clean game staring at a loader, reading it as a failure.
    /// </summary>
    private static async Task<int> RestoreAsync(string[] args)
    {
        if (await ResolveGameAsync(args, offline: true) is not var (platform, catalog, report, _)
            || report is null)
        {
            return 3;
        }

        // ⚠ What is MISSING, never what is stored — the same figure the window shows. An
        // installed game holds every one of those paths, so there is nothing to write.
        var aside = UninstallEngine.RestorableFiles(report.Game);
        if (aside.Count == 0)
        {
            Console.WriteLine($"Every file {report.Game.Name} had before is already in place. "
                              + "There is nothing to put back.");
            return 0;
        }

        Console.WriteLine($"{report.Game.Name}");
        Console.WriteLine($"{report.Game.Path}");
        Console.WriteLine();
        Console.WriteLine("These files were here before UnityGameTranslator Manager replaced them:");
        foreach (var file in aside) Console.WriteLine($"  {file}");
        Console.WriteLine();
        Console.WriteLine("Putting them back restores the mod loader this game came with, so it "
                          + "will be detected again. Anything sitting at one of those paths right "
                          + "now is left alone.");
        Console.WriteLine();

        if (!Confirm(args, "Put them back?"))
        {
            Console.WriteLine("Cancelled. Nothing was written.");
            return 0;
        }

        var outcome = new UninstallEngine(platform, catalog).PutBackWhatWasHere(report.Game);
        Console.WriteLine(outcome.Message);
        foreach (var file in outcome.PutBack) Console.WriteLine($"  {file}");

        return outcome.Success ? 0 : 4;
    }

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

        // ⚠ Says which of the two things survives, because they do not survive alike: the
        // backups stay with the game, the settings do not come back.
        Console.WriteLine(choice.RemoveUserData
            ? "  - your settings and this game's translation (backed up one last time first,"
              + " and the backups themselves stay)"
            : "  (your settings and translations stay — add --settings to remove them)");
        Console.WriteLine();

        if (!Confirm(args, "Proceed?")) { Console.WriteLine("Cancelled. Nothing was removed."); return 0; }

        var outcome = engine.Apply(report.Game, choice);

        Console.WriteLine();
        Console.WriteLine(outcome.Message);
        foreach (var item in outcome.Kept) Console.WriteLine($"  kept: {item}");
        if (outcome.LastBackupTaken)
            Console.WriteLine("The translation was backed up one last time, with the fonts and "
                              + "images it used.");

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

        // ⚠ Which BepInEx 6 stream to measure against, and the answers to measure with. The
        // resolver's cache lives in the process, so a command that starts fresh knows nothing
        // until it asks — and without asking, every report would compare against the catalogue's
        // pinned version and announce "up to date" beside a newer published build.
        var bepinex6Channel = new SettingsStore(platform).Current.BepInEx6Channel;
        if (!offline)
        {
            await new LoaderBuildResolver()
                .WarmAsync(catalog.Document, bepinex6Channel)
                .ConfigureAwait(false);
        }

        var inventory = new GameInventory(platform, catalog.Document,
                                          offline ? null : new CatalogApiClient())
        {
            BepInEx6Channel = bepinex6Channel,
        };

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

    /// <summary>
    /// Updates the tool itself.
    ///
    /// The same two steps the window will offer: look, then say yes. Nothing is downloaded before
    /// the answer, and the previous binary stays under its own name until the new one has started
    /// once — so a build that will not run leaves the working one right beside it.
    /// </summary>
    private static async Task<int> SelfUpdateAsync(string[] args)
    {
        var platform = PlatformFactory.Create();
        var settings = new SettingsStore(platform).Current;

        var channel = args.Contains("--beta", StringComparer.OrdinalIgnoreCase)
            ? ReleaseChannel.Beta
            : settings.ToolReleaseChannel;

        Console.WriteLine($"Running  : {SelfUpdater.CurrentVersion}");
        Console.WriteLine($"From     : {SelfUpdater.RunningExecutable ?? "unknown"}");
        Console.WriteLine($"Channel  : {(channel == ReleaseChannel.Beta ? "beta" : "stable")}");

        // Silent for a normal build. A build pointed elsewhere otherwise reports a network failure
        // that reads exactly like a firewall, whoever is looking at it.
        if (SelfUpdater.UnusualReleaseHost is { } host)
            Console.WriteLine($"Releases : {host} — not GitHub. Self-hosted or a test build.");

        Console.WriteLine();

        var updater = new SelfUpdater(platform);
        var check = await updater.CheckAsync(channel);

        switch (check.State)
        {
            case SelfUpdateState.UpToDate:
                Console.WriteLine(check.Message);
                return 0;

            case SelfUpdateState.CheckFailed:
                // Not the same as "up to date", and it must not be reported as one.
                Console.Error.WriteLine(check.Message);
                return 1;

            case SelfUpdateState.NoBuildForThisSystem:
            case SelfUpdateState.CannotBeVerified:
                Console.Error.WriteLine(check.Message);
                return 1;
        }

        var offer = check.Offer!;
        Console.WriteLine($"Available: {offer.NewVersion}{(offer.IsPrerelease ? " (beta)" : "")}");
        if (offer.PublishedAt is { } published)
            Console.WriteLine($"Published: {published:yyyy-MM-dd}");
        if (offer.SizeBytes is { } size)
            Console.WriteLine($"Download : {size / 1024d / 1024d:0.#} MB");
        Console.WriteLine($"Notes    : {offer.ReleasePageUrl}");
        Console.WriteLine();

        // Said before the download rather than after it: someone who keeps the tool somewhere they
        // cannot write to should not pay for fifty megabytes to find out.
        if (updater.WhyCannotApply() is { } blocked)
        {
            Console.Error.WriteLine(blocked);
            return 1;
        }

        if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Looking only. Run without --check to apply it.");
            return 0;
        }

        if (!Confirm(args, $"Replace {offer.CurrentVersion} with {offer.NewVersion}?"))
        {
            Console.WriteLine("Cancelled. Nothing was downloaded.");
            return 0;
        }

        var lastShown = -1;
        updater.Progress += (done, total) =>
        {
            if (total is not > 0) return;
            var percent = (int)(done * 100 / total.Value);
            if (percent == lastShown) return;
            lastShown = percent;
            Console.Write($"\r  downloading… {percent}%   ");
        };

        try
        {
            var result = await updater.ApplyAsync(offer);
            Console.WriteLine();
            Console.WriteLine($"Updated to {result.Version}.");
            Console.WriteLine($"The version you were running is beside it as "
                              + $"{Path.GetFileName(result.PreviousCopy)}; it is removed the next "
                              + "time the new one starts.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
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

    private static async Task<int> CatalogAsync(bool offline)
    {
        var platform = PlatformFactory.Create();
        var result = new CatalogProvider(platform).Get(offline);

        Console.WriteLine($"Source: {result.Source}");
        if (result.Error is not null) Console.WriteLine($"Note  : {result.Error}");
        Console.WriteLine($"Built : {result.Document.GeneratedAt ?? "unknown"}");
        Console.WriteLine();

        var resolver = offline ? null : new LoaderBuildResolver();

        foreach (var loader in result.Document.Loaders)
        {
            Console.WriteLine($"  {loader.Id,-18} {loader.Display,-20} pinned v{loader.Version,-14} "
                              + $"runtimes: {string.Join('+', loader.Runtimes)}");
            Console.WriteLine($"    plugin   -> {loader.PluginDir}{(loader.PluginDirShared ? "   (shared with other mods)" : "")}");
            Console.WriteLine($"    userdata -> {loader.UserDataDir}");

            var unverified = loader.Assets.Count(a => string.IsNullOrEmpty(a.Sha256));
            if (unverified > 0)
                Console.WriteLine($"    {unverified}/{loader.Assets.Count} pinned asset(s) without a checksum.");

            // What each source offers RIGHT NOW, beside what the catalog pins. This is the whole
            // point of schema 5, and printing it is also how the resolvers get exercised against
            // the real publishers without a game folder in sight.
            if (resolver is null || loader.Sources.Count == 0) continue;

            foreach (var source in loader.Sources)
            {
                var builds = await resolver.ResolveAsync(loader, source.Channel, count: 3);

                if (builds[0].IsPinnedFallback)
                {
                    Console.WriteLine($"    {source.Channel,-8} -> unreachable, would install the pinned v{loader.Version}");
                    continue;
                }

                Console.WriteLine($"    {source.Channel,-8} -> {builds[0].Describe()}  ({source.Label}, "
                                  + $"{builds[0].Assets.Count} archive(s), "
                                  + $"{builds[0].Assets.Count(a => !string.IsNullOrEmpty(a.Sha256))} with a checksum)");

                foreach (var older in builds.Skip(1))
                    Console.WriteLine($"                 also {older.Describe()}");
            }
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
        Console.WriteLine($"tool     : UnityGameTranslator Manager {BuildInfo.Version}");
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
