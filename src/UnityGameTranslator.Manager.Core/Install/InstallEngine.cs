using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Settings;

namespace UnityGameTranslator.Manager.Core.Install;

public sealed record InstallPlan(
    GameInstall Game,
    LoaderDescriptor Loader,
    bool InstallLoader,
    string PluginAssetPattern,
    ReleaseChannel Channel)
{
    /// <summary>
    /// Whether the plugin itself is (re)installed.
    ///
    /// ⚠ False is a real plan, not a no-op. The loader and the mod are published by different
    /// people on different days, and one button doing both meant a loader two versions behind
    /// could only be brought up to date by replacing a perfectly current plugin at the same time —
    /// a download, a health check and a receipt rewrite, all to move a file nobody asked about.
    /// The two are now asked for separately, and the one-click asks for both.
    /// </summary>
    public bool InstallPlugin { get; init; } = true;

    /// <summary>
    /// A plugin copy sitting outside the documented location, relative to the game. Reported so
    /// the user can remove it: we never delete files we did not install, and two copies of the
    /// assembly in one game leave the loader free to pick either.
    /// </summary>
    public IReadOnlyList<string> StrayPluginDirectories { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The settings to write into this game's config.json, or null to leave it alone entirely.
    ///
    /// Null is a real case, not a degenerate one: reinstalling over a game someone has already
    /// tuned by hand should not quietly reset their language or switch their AI back on.
    /// </summary>
    public InstallerSettings? Settings { get; init; }

    /// <summary>
    /// What was decided for this game in particular. Carried beside the defaults rather than
    /// folded into them, so nothing has to build a doctored copy of somebody's settings to
    /// express "in this game, do not start translating".
    /// </summary>
    public GamePreference? Preference { get; init; }

    /// <summary>
    /// The language this game is to be set to. Decided when the plan is made, from what is
    /// published for this game, so the confirmation shown to the user and the file written
    /// afterwards cannot say two different things.
    /// </summary>
    public string? TargetLanguage { get; init; }

    /// <summary>Human-readable summary shown before anything is written.</summary>
    public IEnumerable<string> Describe()
    {
        yield return InstallLoader
            ? $"Install {Loader.Display} {Loader.Version} into {Game.Name}"
            : $"Use the {Loader.Display} already installed in {Game.Name}";

        // Said either way. "The mod is left as it is" is the sentence that stops somebody
        // wondering, after a loader update, whether their plugin was quietly replaced too.
        yield return InstallPlugin
            ? $"Install the plugin into {Loader.PluginDir}/"
            : "The plugin already there is left exactly as it is";

        if (!string.Equals(Loader.UserDataDir, Loader.PluginDir, StringComparison.OrdinalIgnoreCase))
            yield return $"Settings and translations live in {Loader.UserDataDir}/";

        // Named before it happens, because it is the one setting that decides whether the game
        // reads the file it is about to be given. A pair that comes from a translation already in
        // place is called out as such: it is not a choice being made, it is one being respected.
        if (TargetLanguage is { Length: > 0 } target)
            yield return $"Set this game to translate into {target}";

        yield return "Existing settings and translations are left untouched";

        foreach (var stray in StrayPluginDirectories)
        {
            yield return $"! Remove the other copy of the mod in {stray}/ — with two of them, the "
                       + "loader reads the older one and updates appear to do nothing";
        }
    }
}

public sealed record InstallOutcome(bool Success, string Message, Receipt? Receipt);

/// <summary>
/// Installs and updates. Nothing is written before the plan is accepted, everything written is
/// recorded, and a failure halfway puts the folder back the way it was.
/// </summary>
public sealed class InstallEngine
{
    private readonly IPlatform _platform;
    private readonly LoaderCatalogDocument _catalog;
    private readonly GitHubReleaseClient _releases;
    private readonly GitHubAssets _assets;

    public InstallEngine(IPlatform platform, LoaderCatalogDocument catalog,
                         GitHubReleaseClient? releases = null, GitHubAssets? assets = null)
    {
        _platform = platform;
        _catalog = catalog;

        // The MOD's releases: this engine puts a plugin into a game. The tool's own updates go
        // through SelfUpdater, which reads the other repository.
        _releases = releases ?? GitHubReleaseClient.ForMod();
        _assets = assets ?? new GitHubAssets();
    }

    public event Action<string>? Status;

    /// <summary>
    /// Turns a report into a plan, or explains why there is none. Never partially applies:
    /// planning and doing are separate so the user can see the whole thing first.
    /// </summary>
    /// <param name="loaderOverride">
    /// A loader the user picked instead of the recommendation. Ignored when a loader is already
    /// installed: replacing someone's loader would break every other mod they have.
    /// </param>
    /// <param name="settings">
    /// What to write into the game's config.json once installed, or null to leave it untouched.
    /// Passed through the plan rather than read from disk here, so the caller stays in charge of
    /// whether someone's existing configuration gets changed at all.
    /// </param>
    public InstallPlan? Plan(GameReport report, ReleaseChannel channel = ReleaseChannel.Stable,
                             LoaderDescriptor? loaderOverride = null,
                             InstallerSettings? settings = null,
                             GamePreference? preference = null)
    {
        if (report.Blockers.Count > 0) return null;

        var loader = report.InstalledLoader is not null
            ? report.RecommendedLoader
            : loaderOverride ?? report.RecommendedLoader;

        if (loader is null) return null;

        var runtime = report.Game.Runtime == UnityRuntime.Il2Cpp ? "il2cpp" : "mono";
        if (!_catalog.PluginBuilds.TryGetValue($"{loader.Id}:{runtime}", out var pattern)) return null;

        // Every copy that is not where it belongs, whether or not a good one exists beside it.
        // Reading this from FindInstalledPlugin missed the case that matters: it stops at the
        // first hit, canonical first, so a game holding both reported no stray at all.
        var stray = LocalTranslationProbe.FindStrayPlugins(report.Game.Path, loader);

        return new InstallPlan(
            Game: report.Game,
            Loader: loader,
            InstallLoader: report.InstalledLoader is null,
            PluginAssetPattern: pattern,
            Channel: channel)
        {
            StrayPluginDirectories = stray,
            Settings = settings,
            Preference = preference,

            // Decided here because here is where the report is: what is published for this game
            // is what fixes its languages, and nothing further down the chain can see it.
            TargetLanguage = settings is null
                ? null
                : GameLanguages.TargetFor(report, loader,
                    GameLanguages.Resolve(settings.TargetLanguage, _platform.SystemLanguage())),
        };
    }

    public async Task<InstallOutcome> ApplyAsync(InstallPlan plan, CancellationToken ct = default)
    {
        if (_platform.IsGameRunning(plan.Game))
        {
            return new InstallOutcome(false,
                "The game is running. Its files are locked — close it and try again.", null);
        }

        var staging = Path.Combine(_platform.UserDataDirectory, "staging",
                                   Guid.NewGuid().ToString("N")[..8]);
        var files = new FileOperations(plan.Game.Path);

        // An install that fails must leave nothing behind, including a receipt claiming success.
        var existing = ReceiptStore.Read(plan.Game.Path);

        try
        {
            var receipt = new Receipt
            {
                ToolVersion = BuildInfo.Version,
                InstalledAt = existing?.InstalledAt ?? DateTimeOffset.UtcNow,
                UpdatedAt = existing is null ? null : DateTimeOffset.UtcNow,
                Game = new ReceiptGame
                {
                    Path = plan.Game.Path,
                    SteamId = plan.Game.SteamAppId,
                    Runtime = plan.Game.Runtime.ToString(),
                    Unity = plan.Game.UnityVersion,
                },
            };

            if (plan.InstallLoader)
            {
                Status?.Invoke($"Downloading {plan.Loader.Display} {plan.Loader.Version}...");
                await InstallLoaderAsync(plan, files, receipt, staging, existing, ct).ConfigureAwait(false);
            }
            else
            {
                // Record that the loader is not ours, so uninstall never touches it.
                receipt.Loader = existing?.Loader ?? new ReceiptLoader
                {
                    Id = plan.Loader.Id,
                    Version = "",
                    InstalledByUs = false,
                };
            }

            if (plan.InstallPlugin)
            {
                Status?.Invoke("Downloading the plugin...");
                await InstallPluginAsync(plan, files, receipt, staging, ct).ConfigureAwait(false);
            }
            else
            {
                // Carried over from the receipt we are replacing. Dropping it would leave an
                // installed plugin that nothing claims to have put there — and uninstall reads the
                // receipt, so it would then refuse to remove our own files.
                receipt.Plugin = existing?.Plugin;
            }

            var health = VerifyHealth(plan);
            if (health is not null)
            {
                files.Rollback();
                return new InstallOutcome(false, $"Install check failed: {health}. Nothing was kept.", null);
            }

            ReceiptStore.Write(plan.Game.Path, receipt);

            // Written after the health check, and deliberately outside the rollback: config.json
            // belongs to the mod and may predate us. Rolling it back would mean restoring a file
            // we did not create, and a failure to write settings is not a reason to undo a
            // perfectly good install — it is a reason to say so and let the mod's own wizard ask.
            ConfigWriteResult? configured = null;
            if (plan.Settings is not null && plan.TargetLanguage is not null)
            {
                Status?.Invoke("Applying your settings...");
                configured = new GameConfigWriter()
                    .Apply(plan.Game.Path, plan.Loader, plan.Settings, plan.TargetLanguage,
                           perGame: plan.Preference);
            }

            Status?.Invoke("Done.");

            return new InstallOutcome(true, BuildSuccessMessage(plan, configured), receipt);
        }
        catch (Exception ex)
        {
            files.Rollback();
            return new InstallOutcome(false, $"{ex.Message} Nothing was kept.", null);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <param name="existing">
    /// The receipt being replaced, when there is one. Its file list is kept alongside the new one:
    /// a newer version of a loader may ship fewer files than the one it replaces, and anything
    /// dropped from the receipt is a file we put there that uninstall would then walk past
    /// forever. Recording more than we need costs nothing — every removal checks the file is
    /// still ours before touching it.
    /// </param>
    private async Task InstallLoaderAsync(InstallPlan plan, FileOperations files, Receipt receipt,
                                          string staging, Receipt? existing, CancellationToken ct)
    {
        var inventory = new GameInventory(_platform, _catalog);
        var asset = inventory.FindAsset(plan.Loader, plan.Game)
            ?? throw new InvalidOperationException(
                $"No {plan.Loader.Display} build for this system and architecture.");

        var download = await ResolveAsync(plan.Loader, asset, ct).ConfigureAwait(false);
        Status?.Invoke($"Verifying: {download.Describe()}");

        var fetcher = new ArchiveFetcher(staging);
        var archive = await fetcher
            .FetchAsync(download.Url, download.Sha256, plan.Loader.Id, ct)
            .ConfigureAwait(false);

        Status?.Invoke($"Installing {plan.Loader.Display}...");

        var before = files.WrittenFiles.Count;
        var dirsBefore = files.CreatedDirectories.Count;

        CopyTree(archive.ExtractedPath, "", files);

        var wasOurs = existing?.Loader is { InstalledByUs: true } ? existing.Loader : null;

        var written = files.WrittenFiles.Skip(before).ToList();

        // The new entries win where they overlap: theirs is the hash that matches what is on disk
        // now, and uninstall compares against it to know whether the user has since edited a file.
        var carried = wasOurs is null
            ? Enumerable.Empty<ReceiptFile>()
            : wasOurs.Files.Where(old => !written.Any(
                  fresh => string.Equals(fresh.Path, old.Path, StringComparison.OrdinalIgnoreCase)));

        receipt.Loader = new ReceiptLoader
        {
            Id = plan.Loader.Id,
            Version = plan.Loader.Version,
            InstalledByUs = true,
            Files = written.Concat(carried).ToList(),
            DirsCreated = files.CreatedDirectories.Skip(dirsBefore)
                        .Union(wasOurs?.DirsCreated ?? Enumerable.Empty<string>(),
                               StringComparer.OrdinalIgnoreCase)
                        .ToList(),
        };
    }

    private async Task InstallPluginAsync(InstallPlan plan, FileOperations files, Receipt receipt,
                                          string staging, CancellationToken ct)
    {
        var release = await _releases.GetLatestAsync(plan.Channel, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No plugin release found.");

        var resolved = await _releases.ResolveAssetAsync(release, plan.PluginAssetPattern, ct)
                                      .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Release {release.TagName} has no build for {plan.Loader.Display}.");

        var fetcher = new ArchiveFetcher(staging);
        var archive = await fetcher
            .FetchAsync(resolved.Url, resolved.Sha256, "plugin", ct)
            .ConfigureAwait(false);

        Status?.Invoke($"Installing the plugin {release.Version}...");

        var before = files.WrittenFiles.Count;
        var dirsBefore = files.CreatedDirectories.Count;

        // Always the documented location, never wherever a copy happens to be.
        //
        // A previous revision followed an existing install. That was wrong, and MelonLoader's
        // changelog says exactly why a subfolder cannot be trusted: recursive scanning only
        // arrived in 0.6.6, up to 0.7.0 it required a manifest.json in the folder, and since
        // 0.7.2 a config option can turn it off. Three separate ways for the same layout to
        // silently load nothing, all of them outside our control. The root of Mods/ works on
        // every version and cannot be switched off.
        CopyTree(archive.ExtractedPath, plan.Loader.PluginDir, files);

        // ⚠ Removed, not merely announced — and the distinction matters because this is OUR
        // assembly, by name, not somebody else's file. "We never delete what we did not install"
        // exists to protect other people's work; applied to a stray copy of UnityGameTranslator it
        // protected nothing and left two of our own assemblies in one game, with the loader free
        // to load either. Telling somebody to go and delete it themselves is asking them to finish
        // an install we chose to leave broken.
        //
        // ⚠ The FILE by name, and the directory only if it empties. Anything else in there belongs
        // to whoever put it there — and under BepInEx that same folder is where settings and
        // translations live, which no install may touch.
        foreach (var stray in plan.StrayPluginDirectories)
        {
            var directory = Path.Combine(plan.Game.Path,
                stray.Replace('/', Path.DirectorySeparatorChar));

            var copy = Path.Combine(directory, LocalTranslationProbe.PluginAssemblyName);

            try
            {
                if (File.Exists(copy))
                {
                    File.Delete(copy);
                    Status?.Invoke($"Removed the other plugin copy in {stray}.");
                }

                FileOperations.TryRemoveEmptyDirectory(directory);
            }
            catch (Exception ex)
            {
                // Said, never silent: a copy still there is the one thing that makes this install
                // behave unpredictably, and the reason it survived is worth reading.
                Status?.Invoke($"Could not remove the plugin copy in {stray} ({ex.Message}). "
                             + "Delete it by hand: with two in one game, the loader may load either.");
            }
        }

        receipt.Plugin = new ReceiptPlugin
        {
            Version = release.Version,
            Build = plan.Loader.Id,
            Files = files.WrittenFiles.Skip(before).ToList(),
            DirsCreated = files.CreatedDirectories.Skip(dirsBefore).ToList(),
        };
    }

    /// <summary>
    /// Works out where an archive lives and what checksum we can hold it to.
    ///
    /// Order matters: a hash pinned in our catalog wins, then the digest GitHub publishes for
    /// the asset, then nothing. "Nothing" is a reported condition, not a failure — some
    /// publishers offer no checksum at all, and refusing to work with them would only mean the
    /// user installs by hand, unverified, with no record.
    /// </summary>
    public async Task<ResolvedDownload> ResolveAsync(LoaderDescriptor loader, LoaderAsset asset,
                                                     CancellationToken ct = default)
    {
        var url = !string.IsNullOrWhiteSpace(asset.Url)
            ? asset.Url
            : loader.GitHub is { } release && !string.IsNullOrWhiteSpace(asset.Name)
                ? GitHubAssets.BuildUrl(release.Repo, release.Tag, asset.Name)
                : throw new InvalidOperationException(
                    $"The catalog entry for {loader.Display} has no download for this system.");

        if (!string.IsNullOrWhiteSpace(asset.Sha256))
            return new ResolvedDownload(url, asset.Sha256.Trim().ToLowerInvariant(), IntegrityLevel.Pinned);

        if (loader.GitHub is { } source && !string.IsNullOrWhiteSpace(asset.Name))
        {
            var digests = await _assets.GetDigestsAsync(source.Repo, source.Tag, ct).ConfigureAwait(false);
            if (digests.TryGetValue(asset.Name, out var digest))
                return new ResolvedDownload(url, digest, IntegrityLevel.Published);
        }

        return new ResolvedDownload(url, null, IntegrityLevel.None);
    }

    /// <summary>Copies an extracted archive into the game, preserving its internal layout.</summary>
    private static void CopyTree(string sourceRoot, string targetPrefix, FileOperations files)
    {
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source).Replace('\\', '/');
            var target = string.IsNullOrEmpty(targetPrefix) ? relative : $"{targetPrefix}/{relative}";
            files.PlaceFile(source, target);
        }
    }

    /// <summary>
    /// Confirms the install is actually there. Returns null when healthy, or what is missing.
    /// Without this an interrupted extraction would be reported as a success, and the user would
    /// go looking for a bug in the mod instead of reinstalling.
    /// </summary>
    private static string? VerifyHealth(InstallPlan plan)
    {
        // Only what this plan claimed to put there. A loader-only install must not fail because
        // no plugin is present: "install the loader" is a complete request, and answering it with
        // "the plugin is missing" would refuse a job that succeeded.
        if (plan.InstallPlugin)
        {
            var pluginDll = Path.Combine(plan.Game.Path,
                plan.Loader.PluginDir.Replace('/', Path.DirectorySeparatorChar),
                LocalTranslationProbe.PluginAssemblyName);

            if (!File.Exists(pluginDll))
                return $"{LocalTranslationProbe.PluginAssemblyName} is not where it should be";
        }

        if (plan.InstallLoader)
        {
            var marker = plan.Loader.Detect.All.FirstOrDefault();
            if (marker is not null)
            {
                var path = Path.Combine(plan.Game.Path, marker.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path) && !Directory.Exists(path)) return $"{marker} is missing";
            }
        }

        return null;
    }

    private string BuildSuccessMessage(InstallPlan plan, ConfigWriteResult? configured)
    {
        var lines = new List<string> { $"{plan.Game.Name} is ready." };

        // Said plainly, because writing into someone's game without telling them what changed is
        // how a tool loses trust — and because "why is my language wrong" is answered here.
        if (configured is { Written: true, Applied.Count: > 0 })
        {
            lines.Add($"Applied your settings: {string.Join(", ", configured.Applied)}.");

            lines.Add(configured.WizardSkipped
                ? "The mod's first-run wizard is skipped: everything it asks is already answered."
                : "The mod will still run its first-run wizard, since some of its questions have "
                  + "no answer in your settings yet.");
        }
        else if (configured is { Written: false })
        {
            lines.Add($"Your settings could not be written ({configured.Failure}). The game is "
                    + "installed and the mod will ask you its own questions on first launch.");
        }

        if (_platform.NeedsDllOverride(plan.Game) && plan.Loader.ProtonDllOverride is not null)
        {
            lines.Add("One more step, and the mod will not load without it — set this as the " +
                      "game's Steam launch options:");
            lines.Add($"  WINEDLLOVERRIDES=\"{plan.Loader.ProtonDllOverride}=n,b\" %command%");
        }

        foreach (var warning in plan.Loader.Warnings) lines.Add($"Note: {warning}");

        return string.Join(Environment.NewLine, lines);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* staging cleanup is best effort */ }
    }
}
