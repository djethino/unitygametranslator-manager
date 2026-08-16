using UnityGameTranslator.Manager.Core.Api;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Update;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// The one entry point that turns "a machine" into "here are your games and what I would do
/// with each of them".
///
/// Everything above this — CLI today, GUI tomorrow — only renders what this produces. Keeping
/// the decisions here rather than in the front-end is what lets the CLI be a real test harness
/// for the GUI instead of a separate, slightly different implementation.
/// </summary>
public sealed class GameInventory
{
    private readonly IPlatform _platform;
    private readonly LoaderCatalogDocument _catalog;
    private readonly CatalogApiClient? _api;

    /// <summary>
    /// The account this tool is signed in as, when it is — sent with the community search ONLY so
    /// the answer can carry this account's own vote on each translation.
    ///
    /// ⚠ Not permission: the listing is public and identical without it. Without it the arrows on
    /// the game card cannot show what somebody already chose, so a second click withdraws the vote
    /// they meant to confirm.
    /// </summary>
    private readonly string? _apiToken;

    public GameInventory(IPlatform platform, LoaderCatalogDocument catalog, CatalogApiClient? api = null,
                         string? apiToken = null)
    {
        _platform = platform;
        _catalog = catalog;
        _api = api;
        _apiToken = apiToken;
        Folders = new CustomFolders(platform);
        Overrides = new GameOverrides(platform);
    }

    /// <summary>What the user told us about games we could not read on our own.</summary>
    public GameOverrides Overrides { get; } = null!;

    /// <summary>The folders the user added by hand. Exposed so they can be shown and removed.</summary>
    public CustomFolders Folders { get; } = null!;

    /// <summary>
    /// Where the signed-in account stands in the lineages it takes part in.
    ///
    /// Set by whoever owns the session — the window — and left null everywhere else: the CLI and
    /// the install engine have no account, and a report simply says nothing about roles rather
    /// than carrying a second, quieter way of being signed in.
    /// </summary>
    public AccountLineages? Lineages { get; set; }

    /// <summary>
    /// Where the newest published plugin build is looked up. Null leaves every report silent
    /// about plugin versions rather than guessing — the CLI's offline paths and the install
    /// engine's own throwaway inventory have no business reaching the network.
    ///
    /// Shared on purpose: one lookup answers for every game on the machine.
    /// </summary>
    public PluginReleases? Releases { get; set; }

    /// <summary>
    /// Which channel the version comparison is made against. It is a setting about the person,
    /// held by whoever owns the session — asking for betas and then being told a stable build is
    /// the newest one would contradict the choice on the settings screen.
    /// </summary>
    public ReleaseChannel Channel { get; set; } = ReleaseChannel.Stable;

    /// <summary>
    /// Every Unity game we can find: Steam first because it is the richest source, then the
    /// launcher defaults, then the folders the user added themselves.
    /// </summary>
    public List<GameInstall> ScanAll()
    {
        var games = new List<GameInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(GameInstall game)
        {
            if (seen.Add(Path.GetFullPath(game.Path))) games.Add(game);
        }

        foreach (var game in new SteamScanner(_platform).Scan()) Add(game);
        foreach (var game in new StoreScanner(_platform).Scan()) Add(game);

        // Applied after detection, never instead of it: we always read the files first, so a
        // stale answer about a game that has since been updated gets replaced by the truth.
        foreach (var game in games) Overrides.Apply(game);

        foreach (var folder in Folders.All)
        {
            if (Folders.IsMissing(folder)) continue; // reported elsewhere, not silently dropped
            foreach (var game in StoreScanner.ScanFolder(folder, GameStore.Manual, maxDepth: 2))
                Add(game);
        }

        return games;
    }

    /// <summary>Probes one folder the user pointed at directly.</summary>
    public GameInstall? ScanFolder(string folder)
    {
        var game = UnityGameProbe.Probe(folder, null, GameStore.Manual);
        if (game is null) return null;

        ModdabilityProbe.Evaluate(game);
        Overrides.Apply(game);
        return game;
    }

    /// <summary>
    /// Everything known about one game: installed loader, local translation, online catalog,
    /// what we would install and why, and what stands in the way.
    /// </summary>
    public async Task<GameReport> BuildReportAsync(GameInstall game, bool offline = false,
                                                   CancellationToken ct = default)
    {
        var report = new GameReport { Game = game };

        report.InstalledLoader = LoaderProbe.Detect(game.Path, _catalog);

        // ⚠ Read HERE rather than by each caller. Four places build a report, and a per-game
        // permission that one of them forgot to attach would be a game where the loader silently
        // goes back to being untouchable — the kind of gap nobody notices because the safe answer
        // is the one that stays.
        report.LoaderAdopted = new Settings.GamePreferences(_platform).Read(game.Path).AdoptLoader;

        // ⚠ The probe looks at files; only the receipt knows who put them there. Nothing was
        // filling this in, so DetectedLoader.InstalledByUs was false for EVERY game — including
        // the ones this tool had just set up. Two consequences, both reported as bugs: the card
        // said "it was here before you started using this tool" about a loader we had installed
        // ourselves minutes earlier, and ReadLoaderStanding refused to compare its version, so no
        // loader update was ever offered to anybody.
        //
        // The id is compared too: a receipt describing BepInEx says nothing about a MelonLoader
        // found on disk today.
        if (report.InstalledLoader is { } detected)
        {
            var receipt = ReceiptStore.Read(game.Path);

            detected.InstalledByUs = receipt?.Loader is { InstalledByUs: true } ours
                                     && string.Equals(ours.Id, detected.Id, StringComparison.OrdinalIgnoreCase);
        }

        var descriptor = ResolveDescriptor(report, game);
        if (descriptor is not null)
        {
            report.LocalTranslation = LocalTranslationProbe.Read(game.Path, descriptor);
            report.InstalledPluginVersion = LocalTranslationProbe.ReadInstalledPluginVersion(game.Path, descriptor);
            report.PluginDirectory = descriptor.PluginDir;
            report.PluginInPlace = LocalTranslationProbe.IsPluginInPlace(game.Path, descriptor);
            report.StrayPluginDirectories = LocalTranslationProbe.FindStrayPlugins(game.Path, descriptor);
            report.PluginBuildId = ResolvePluginBuild(descriptor, game.Runtime);
            CollectRequirements(report, descriptor, game);

            report.SiteAccount = LocalTranslationProbe.ReadSiteAccount(game.Path, descriptor);
            report.LoaderStanding = ReadLoaderStanding(report);
            report.PluginStanding = await ReadPluginStandingAsync(report, offline, ct).ConfigureAwait(false);
        }

        if (!game.IsModdable)
        {
            report.Blockers.Add(ModdabilityProbe.Explain(game));
        }

        // An override is a standing decision, not a one-off click. It keeps saying so on every
        // report: once forced, the game would otherwise read as perfectly ordinary, and nobody
        // would connect "the mod does nothing" back to the refusal they overruled weeks ago.
        if (game is { VerdictOverridden: true, OverriddenVerdict: { } overruled })
        {
            report.Warnings.Add(
                $"You chose to proceed here despite a refusal. {ModdabilityProbe.OverrideCaveat(overruled)}");
        }

        if (game.RuntimeIsAssumed)
            report.Warnings.Add($"The runtime ({Describe(game.Runtime)}) is what you told us, not what we read.");

        if (game.ArchitectureIsAssumed)
            report.Warnings.Add($"The architecture ({game.Architecture}) is what you told us, not what we read.");

        // Steam id when we have one, the game's name otherwise — the same order the mod uses, and
        // for the same reason: a game bought outside Steam, or installed by hand, has a published
        // translation just as often. Looking up only Steam games reported "none found" for games
        // whose translation was sitting on the site.
        if (!offline && _api is not null && (game.SteamAppId is not null || !string.IsNullOrWhiteSpace(game.Name)))
        {
            report.OnlineTranslations = game.SteamAppId is not null
                ? await _api.SearchBySteamIdAsync(game.SteamAppId, apiToken: _apiToken, ct: ct).ConfigureAwait(false)
                : await _api.SearchByNameAsync(game.Name, apiToken: _apiToken, ct: ct).ConfigureAwait(false);

            // "No translation exists" and "the search failed" look identical to a user, and
            // only one of them is our problem. Keep them apart.
            report.OnlineSearchError = _api.LastError;

            // And "we never asked" is a third thing again. Only a search that ran and came back is
            // evidence that nobody has published anything.
            report.OnlineChecked = _api.LastError is null;

            // Same lineage as the local file? Then it is not something to offer, it is the
            // thing the user already runs — and the only question is whether it moved online.
            if (report.LocalTranslation?.Uuid is { Length: > 0 } localUuid)
            {
                report.MatchingOnline = report.OnlineTranslations.FirstOrDefault(
                    t => string.Equals(t.Uuid, localUuid, StringComparison.OrdinalIgnoreCase));
            }
        }

        // ⚠ Only once we hold the published entry of THIS file, which is what makes the hash worth
        // computing: without something to compare it to, walking a 6000-line file would answer a
        // question nobody asked. With it, the manager reaches the same verdict a running game
        // would, and reaches it before anybody launches anything.
        if (report.MatchingOnline is { FileHash.Length: > 0 } published
            && report.LocalTranslation is { } local
            && ResolveDescriptor(report, game) is { } loader)
        {
            // ⚠ Measured against the ancestor when there is one, and only otherwise taken from the
            // counter the mod keeps. That counter describes what the MOD did: a file edited from
            // the browser editor, or by hand, carries a number that stopped describing it — and
            // reading it as "nothing changed here" is what turns a merge into an offer to download
            // over somebody's work.
            //
            // Null — no ancestor AND no counter to trust — is read as "there is work here": the
            // safe direction is the one that refuses to overwrite.
            report.Sync = Common.Sync.Decide(
                LocalTranslationProbe.ComputeContentHash(game.Path, loader),
                published.FileHash,
                local.SourceHash,
                local.HasLocalWork ?? true);
        }

        // Deliberately outside the community lookup: whether this account leads or contributes to
        // the lineage of the local file is a fact about the account, and it holds even when the
        // catalog search failed or the game is not published anywhere.
        report.MyPosition = Lineages?.For(report.LocalTranslation?.Uuid);

        return report;
    }

    /// <summary>
    /// The installed loader against the catalog's entry for it — WHOEVER installed it.
    ///
    /// ⚠ This used to answer null for a loader that was already there, so that no update could be
    /// offered on somebody else's files. It withheld the FACT along with the offer: a card could
    /// show "BepInEx 6.0.0" beside a catalog that had known about 6.0.2 for months and say
    /// nothing, on a screen whose whole purpose is to tell you where you stand.
    ///
    /// ⚠ Knowing is not acting, and the second half of that rule still holds without reservation:
    /// a loader we did not install is never updated or replaced from here — other mods may need
    /// that exact version. The refusal now lives where the ACTION is decided
    /// (<see cref="GameReport.LoaderUpdateOffered"/>, LoaderVerb, the install plan), which is
    /// where it belonged, and the screen says both things at once: what exists, and why we leave
    /// it alone.
    /// </summary>
    private VersionStanding? ReadLoaderStanding(GameReport report)
    {
        if (report.InstalledLoader is not { } installed) return null;

        var known = _catalog.Loaders.FirstOrDefault(l => l.Id == installed.Id);
        if (known is null) return null;

        return new VersionStanding(installed.Version, known.Version);
    }

    /// <summary>
    /// The plugin here against the newest published build.
    ///
    /// Answers with the available version even when nothing is installed: "0.12.0 available" is
    /// what makes an install offer concrete, and the same row then serves the update.
    /// </summary>
    private async Task<VersionStanding?> ReadPluginStandingAsync(GameReport report, bool offline,
                                                                 CancellationToken ct)
    {
        var installed = report.InstalledPluginVersion;

        // Offline is a promise, so no call is made — and the screen is told it does not know,
        // rather than being left to imply that whatever is installed is the newest there is.
        if (offline || Releases is null)
            return new VersionStanding(installed, null, offline ? "not checked while offline" : null);

        var release = await Releases.LatestAsync(Channel, ct).ConfigureAwait(false);

        return new VersionStanding(installed, release?.Version, Releases.LastError);
    }

    /// <summary>
    /// Which loader we work with: the one already installed, or the recommendation.
    /// An installed loader always wins — it is not ours to replace, and swapping it would break
    /// every other mod the player has.
    /// </summary>
    private LoaderDescriptor? ResolveDescriptor(GameReport report, GameInstall game)
    {
        // A game we have refused gets no recommendation. Naming a loader next to "no loader can
        // start here" invites the reader to try it anyway, and reads as if the tool disagrees
        // with itself.
        if (!game.IsModdable)
        {
            report.RecommendationReason = null;
            return null;
        }

        // ⚠ The person's answer first, the catalog's order after. Filtering comes before both:
        // a preference reorders what fits, it never makes something fit.
        var preferred = new Settings.SettingsStore(_platform).Current.PreferredLoaderFor(game.Runtime);

        var candidates = _catalog.Loaders
            .Where(l => l.SupportsRuntime(game.Runtime))
            .Where(l => FindAsset(l, game) is not null)
            .OrderByDescending(l => string.Equals(l.Id, preferred, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(l => l.Preference)
            .ToList();


        report.EligibleLoaders = candidates;

        if (report.InstalledLoader is not null)
        {
            var installed = _catalog.Loaders.FirstOrDefault(l => l.Id == report.InstalledLoader.Id);
            if (installed is not null)
            {
                report.RecommendedLoader = installed;
                report.RecommendationReason =
                    $"{installed.Display} is already installed — the plugin is simply added to it.";
                return installed;
            }
        }

        if (candidates.Count == 0)
        {
            report.RecommendationReason = game.Runtime == UnityRuntime.Unknown
                ? "No recommendation: the scripting backend could not be identified."
                : $"No loader in the catalog supports {Describe(game.Runtime)} on this system.";
            return null;
        }

        var best = candidates[0];
        report.RecommendedLoader = best;

        var alternatives = candidates.Skip(1).Select(l => l.Display).ToList();
        report.RecommendationReason = alternatives.Count > 0
            // 🔴 Not "recommended". The order comes from an integer in the catalog whose only
            // documentation is "higher wins" — calling that a recommendation claims a judgement
            // nobody has made. What is true is that this one is used if nothing is changed.
            ? $"This game is {Describe(game.Runtime)}. {best.Display} unless you choose otherwise — {string.Join(" and ", alternatives)} also fit."
            : $"This game is {Describe(game.Runtime)} — {best.Display} is the only option.";

        return best;
    }

    /// <summary>
    /// The archive matching this OS and architecture. Under Proton we deliberately pick the
    /// Windows build: the game is a Windows binary, so the loader must be one too.
    /// </summary>
    public LoaderAsset? FindAsset(LoaderDescriptor descriptor, GameInstall game) =>
        FindAsset(descriptor.Assets, game);

    /// <summary>
    /// The same rule, applied to any list of archives.
    ///
    /// ⚠ Split out so a build resolved from the publisher and the one pinned in the catalog are
    /// picked identically. Two copies of "which file fits this machine" would be two chances to
    /// disagree, and the disagreement would be an x86 game handed an x64 loader — the exact
    /// failure the refusal below was written to stop.
    /// </summary>
    public LoaderAsset? FindAsset(IReadOnlyList<LoaderAsset> assets, GameInstall game)
    {
        var os = game.RunsUnderProton ? "windows" : _platform.OsId;

        // No fallback to the host architecture. It used to default to x64 on a 64-bit machine,
        // which quietly installed the wrong loader into a 32-bit game; an unread architecture is
        // now a refusal the user can answer (see ModdabilityProbe.CanBeOverridden).
        var wanted = game.Architecture switch
        {
            GameArchitecture.X86 => "x86",
            GameArchitecture.X64 => "x64",
            GameArchitecture.Arm64 => "arm64",
            _ => null,
        };

        if (wanted is null)
        {
            return assets.FirstOrDefault(a => a.Os == os && a.Arch == "universal");
        }

        return assets.FirstOrDefault(a =>
                   a.Os == os && (a.Arch == wanted || a.Arch == "universal"))
               ?? assets.FirstOrDefault(a => a.Os == os && a.Arch == "universal");
    }

    private string? ResolvePluginBuild(LoaderDescriptor descriptor, UnityRuntime runtime)
    {
        var key = $"{descriptor.Id}:{(runtime == UnityRuntime.Il2Cpp ? "il2cpp" : "mono")}";
        return _catalog.PluginBuilds.TryGetValue(key, out var asset) ? asset : null;
    }

    /// <summary>
    /// The notes that are actually true for this loader in this situation.
    ///
    /// Public because the loader can be overridden after the report was built: showing the
    /// recommended loader's notes next to a different chosen loader would be worse than showing
    /// none, since the reader has no way to tell they no longer apply.
    /// </summary>
    /// <param name="freshInstall">
    /// False when the loader is already installed: it has already been through its first launch
    /// and will not be replaced, so notes about installing it are not true here.
    /// </param>
    public IEnumerable<string> WarningsFor(LoaderDescriptor loader, GameInstall game, bool freshInstall)
    {
        foreach (var warning in loader.Warnings)
        {
            if (warning.AppliesTo(_platform.OsId, game.Runtime, freshInstall))
                yield return warning.Text;
        }
    }

    private void CollectRequirements(GameReport report, LoaderDescriptor descriptor, GameInstall game)
    {
        foreach (var warning in WarningsFor(descriptor, game, report.InstalledLoader is null))
            report.Warnings.Add(warning);

        // A required desktop runtime we can prove is missing is a blocker, not a warning: the
        // game would fail at launch and the user would blame the mod.
        if (!string.IsNullOrEmpty(descriptor.Requires.DotnetDesktop)
            && game.Runtime == UnityRuntime.Il2Cpp)
        {
            var present = _platform.HasDotnetDesktopRuntime(descriptor.Requires.DotnetDesktop);
            if (present == false)
            {
                report.Blockers.Add(
                    $"{descriptor.Display} needs the .NET {descriptor.Requires.DotnetDesktop} " +
                    "Desktop Runtime, which is not installed. The game would fail to start.");
            }
            else if (present is null)
            {
                report.Warnings.Add(
                    $"{descriptor.Display} needs the .NET {descriptor.Requires.DotnetDesktop} " +
                    "Desktop Runtime; we could not verify it on this system.");
            }
        }

        if (_platform.NeedsDllOverride(game) && descriptor.ProtonDllOverride is not null)
        {
            report.Warnings.Add(
                $"Runs through Proton: Steam launch options must contain " +
                $"WINEDLLOVERRIDES=\"{descriptor.ProtonDllOverride}=n,b\" %command% " +
                "or the loader is never injected.");
        }

        // A missing download for this OS/architecture IS blocking: there is nothing to install.
        // The checksum is not checked here — it is resolved from the publisher's release at
        // download time, and its absence degrades to a warning rather than blocking the install.
        if (report.InstalledLoader is null && FindAsset(descriptor, game) is null)
        {
            report.Blockers.Add(
                $"{descriptor.Display} has no build for {_platform.OsId}/{game.Architecture}.");
        }
    }

    private static string Describe(UnityRuntime runtime) => runtime switch
    {
        UnityRuntime.Mono => "Mono",
        UnityRuntime.Il2Cpp => "IL2CPP",
        _ => "of an unknown type",
    };
}
