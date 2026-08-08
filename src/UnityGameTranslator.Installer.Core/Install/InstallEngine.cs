using UnityGameTranslator.Installer.Core.Detection;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;

namespace UnityGameTranslator.Installer.Core.Install;

public sealed record InstallPlan(
    GameInstall Game,
    LoaderDescriptor Loader,
    bool InstallLoader,
    string PluginAssetPattern,
    ReleaseChannel Channel)
{
    /// <summary>Human-readable summary shown before anything is written.</summary>
    public IEnumerable<string> Describe()
    {
        yield return InstallLoader
            ? $"Install {Loader.Display} {Loader.Version} into {Game.Name}"
            : $"Use the {Loader.Display} already installed in {Game.Name}";

        yield return $"Install the plugin into {Loader.PluginDir}/";

        if (!string.Equals(Loader.UserDataDir, Loader.PluginDir, StringComparison.OrdinalIgnoreCase))
            yield return $"Settings and translations live in {Loader.UserDataDir}/";

        yield return "Existing settings and translations are left untouched";
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
    private readonly ModReleaseClient _releases;

    public InstallEngine(IPlatform platform, LoaderCatalogDocument catalog,
                         ModReleaseClient? releases = null)
    {
        _platform = platform;
        _catalog = catalog;
        _releases = releases ?? new ModReleaseClient();
    }

    public event Action<string>? Status;

    /// <summary>
    /// Turns a report into a plan, or explains why there is none. Never partially applies:
    /// planning and doing are separate so the user can see the whole thing first.
    /// </summary>
    public InstallPlan? Plan(GameReport report, ReleaseChannel channel = ReleaseChannel.Stable)
    {
        if (report.Blockers.Count > 0) return null;
        if (report.RecommendedLoader is null || report.PluginBuildId is null) return null;

        return new InstallPlan(
            Game: report.Game,
            Loader: report.RecommendedLoader,
            InstallLoader: report.InstalledLoader is null,
            PluginAssetPattern: report.PluginBuildId,
            Channel: channel);
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
                await InstallLoaderAsync(plan, files, receipt, staging, ct).ConfigureAwait(false);
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

            Status?.Invoke("Downloading the plugin...");
            await InstallPluginAsync(plan, files, receipt, staging, ct).ConfigureAwait(false);

            var health = VerifyHealth(plan);
            if (health is not null)
            {
                files.Rollback();
                return new InstallOutcome(false, $"Install check failed: {health}. Nothing was kept.", null);
            }

            ReceiptStore.Write(plan.Game.Path, receipt);
            Status?.Invoke("Done.");

            return new InstallOutcome(true, BuildSuccessMessage(plan), receipt);
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

    private async Task InstallLoaderAsync(InstallPlan plan, FileOperations files, Receipt receipt,
                                          string staging, CancellationToken ct)
    {
        var inventory = new GameInventory(_platform, _catalog);
        var asset = inventory.FindAsset(plan.Loader, plan.Game)
            ?? throw new InvalidOperationException(
                $"No {plan.Loader.Display} build for this system and architecture.");

        var fetcher = new ArchiveFetcher(staging);
        var archive = await fetcher
            .FetchAsync(asset.Url, asset.Sha256, plan.Loader.Id, ct)
            .ConfigureAwait(false);

        Status?.Invoke($"Installing {plan.Loader.Display}...");

        var before = files.WrittenFiles.Count;
        var dirsBefore = files.CreatedDirectories.Count;

        CopyTree(archive.ExtractedPath, "", files);

        receipt.Loader = new ReceiptLoader
        {
            Id = plan.Loader.Id,
            Version = plan.Loader.Version,
            InstalledByUs = true,
            Files = files.WrittenFiles.Skip(before).ToList(),
            DirsCreated = files.CreatedDirectories.Skip(dirsBefore).ToList(),
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

        CopyTree(archive.ExtractedPath, plan.Loader.PluginDir, files);

        receipt.Plugin = new ReceiptPlugin
        {
            Version = release.Version,
            Build = plan.Loader.Id,
            Files = files.WrittenFiles.Skip(before).ToList(),
            DirsCreated = files.CreatedDirectories.Skip(dirsBefore).ToList(),
        };
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
        var pluginDll = Path.Combine(plan.Game.Path,
            plan.Loader.PluginDir.Replace('/', Path.DirectorySeparatorChar),
            LocalTranslationProbe.PluginAssemblyName);

        if (!File.Exists(pluginDll)) return $"{LocalTranslationProbe.PluginAssemblyName} is not where it should be";

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

    private string BuildSuccessMessage(InstallPlan plan)
    {
        var lines = new List<string> { $"{plan.Game.Name} is ready." };

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
