using UnityGameTranslator.Installer.Core.Install;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;

namespace UnityGameTranslator.Installer.Core.Update;

/// <summary>What came back from looking, in the words the caller has to put on screen.</summary>
public enum SelfUpdateState
{
    /// <summary>Nothing newer on the chosen channel.</summary>
    UpToDate,

    /// <summary>A newer build exists and can be applied.</summary>
    Available,

    /// <summary>A newer build exists but publishes nothing for this system or architecture.</summary>
    NoBuildForThisSystem,

    /// <summary>A newer build exists and cannot be verified. Never offered — see the note below.</summary>
    CannotBeVerified,

    /// <summary>We could not look. Not the same as "up to date", and must not read like it.</summary>
    CheckFailed,
}

public sealed record SelfUpdateOffer(
    string CurrentVersion,
    string NewVersion,
    string TagName,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt,
    string AssetName,
    string Url,
    string Sha256,
    long? SizeBytes,
    string ReleasePageUrl);

public sealed record SelfUpdateCheck(SelfUpdateState State, SelfUpdateOffer? Offer, string? Message);

public sealed record SelfUpdateResult(string ExecutablePath, string PreviousCopy, string Version);

/// <summary>
/// The tool updating itself.
///
/// Three rules decided with the user, and each one is here for a reason that has already bitten
/// somebody somewhere:
///
/// 1. **It offers, it never applies on its own.** Until there is a signing certificate, an update
///    that lands silently is indistinguishable — to the person and to their antivirus — from
///    something else writing to their disk. The moment where the tool replaces its own binary is
///    the single most abusable second in its life; a human says go, every time.
///
/// 2. **Nothing is replaced that could not be verified.** The archive is held to the sha256 we
///    publish beside it, cross-checked against the digest GitHub computes for the same asset.
///    Two independent sources, neither maintained by hand: if they disagree, we stop and say so
///    rather than pick one.
///
/// 3. **The version being replaced survives until the new one has run.** The old binary is not
///    deleted, it is renamed; the next successful launch clears it. So a build that cannot start
///    leaves the working one sitting right there, rather than leaving a person with nothing.
///
/// There is no helper program. A separate updater would be a second executable to ship and a
/// second thing to sign, and it exists only to work around a limitation Windows does not actually
/// have: a running executable cannot be deleted, but it CAN be renamed out of the way.
/// </summary>
public sealed class SelfUpdater
{
    /// <summary>
    /// The mark on a set-aside binary. Searched for at startup, so it has to be recognisable
    /// without knowing which version wrote it.
    /// </summary>
    public const string PreviousMarker = ".previous-";

    private readonly IPlatform _platform;
    private readonly GitHubReleaseClient _releases;
    private readonly GitHubAssets _assets;

    public SelfUpdater(IPlatform platform, GitHubReleaseClient? releases = null,
                       GitHubAssets? assets = null)
    {
        _platform = platform;
        _releases = releases ?? GitHubReleaseClient.ForTool();

        // The digests come from wherever the release list comes from, rather than from GitHub by
        // assumption: a build pointed at another host for testing must not quietly ask the real
        // GitHub about a tag that only exists on the other one, and then report "no digest".
        _assets = assets ?? new GitHubAssets(apiBase: ApiBaseOf(BuildInfo.ToolReleasesApi));
    }

    /// <summary>
    /// The API root behind a releases address: everything before "/repos/".
    /// Returns null — meaning "GitHub" — for any address that does not have that shape.
    /// </summary>
    private static string? ApiBaseOf(string releasesApi)
    {
        var marker = releasesApi.IndexOf("/repos/", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? releasesApi[..marker] : null;
    }

    /// <summary>Bytes fetched so far, and the total when the server states one.</summary>
    public event Action<long, long?>? Progress;

    /// <summary>The version this process is running.</summary>
    public static string CurrentVersion => BuildInfo.Version;

    /// <summary>
    /// The file this process is running from, or null when it cannot be established.
    ///
    /// ProcessPath rather than the assembly location: in a single-file build the assembly has no
    /// path of its own on disk, and Assembly.Location returns an empty string. Reading the wrong
    /// one would have us replacing a file inside a temporary extraction folder.
    /// </summary>
    public static string? RunningExecutable => Environment.ProcessPath;

    /// <summary>
    /// Why an update could not be applied here, or null when it could.
    ///
    /// Asked BEFORE downloading fifty megabytes. Someone who put the tool in a read-only place —
    /// Program Files without elevation, /usr on SteamOS — needs to hear that at the moment they
    /// ask, not after a long download that then fails on the last step.
    /// </summary>
    public string? WhyCannotApply()
    {
        var executable = RunningExecutable;
        if (executable is null)
            return "This build cannot tell where it is running from, so it will not replace itself.";

        var folder = Path.GetDirectoryName(executable);
        if (folder is null || !Directory.Exists(folder))
            return "This build cannot tell which folder it is running from.";

        try
        {
            var probe = Path.Combine(folder, $".ugt-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return $"The folder it lives in cannot be written to: {folder}. "
                   + "Move the tool somewhere you own, or download the new version yourself.";
        }
        catch (IOException)
        {
            return $"The folder it lives in cannot be written to: {folder}. "
                   + "Move the tool somewhere you own, or download the new version yourself.";
        }
    }

    /// <summary>
    /// Looks for a newer build on the given channel.
    ///
    /// Never throws for a network reason: not being able to look is an outcome, and one that must
    /// read differently from "you are up to date". A tool that says "up to date" while offline is
    /// telling someone something it does not know.
    /// </summary>
    public async Task<SelfUpdateCheck> CheckAsync(ReleaseChannel channel = ReleaseChannel.Stable,
                                                  CancellationToken ct = default)
    {
        PublishedRelease? release;
        try
        {
            release = await _releases.GetLatestAsync(channel, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SelfUpdateCheck(SelfUpdateState.CheckFailed, null,
                $"Could not reach the release list: {ex.Message}");
        }

        if (release is null)
        {
            return new SelfUpdateCheck(SelfUpdateState.CheckFailed, null,
                "The release list came back empty.");
        }

        if (!Versions.IsNewer(CurrentVersion, release.Version))
        {
            return new SelfUpdateCheck(SelfUpdateState.UpToDate, null,
                $"{CurrentVersion} is the latest on the {Describe(channel)} channel.");
        }

        var assetName = AssetNameFor(release.Version);
        if (assetName is null)
        {
            return new SelfUpdateCheck(SelfUpdateState.NoBuildForThisSystem, null,
                $"Version {release.Version} exists, but nothing is published for "
                + $"{_platform.OsId}/{_platform.HostArchitecture}.");
        }

        if (!release.Assets.TryGetValue(assetName, out var url))
        {
            return new SelfUpdateCheck(SelfUpdateState.NoBuildForThisSystem, null,
                $"Version {release.Version} does not include {assetName}.");
        }

        string? sha;
        try
        {
            sha = await ResolveChecksumAsync(release, assetName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SelfUpdateCheck(SelfUpdateState.CannotBeVerified, null, ex.Message);
        }

        if (sha is null)
        {
            return new SelfUpdateCheck(SelfUpdateState.CannotBeVerified, null,
                $"Version {release.Version} publishes {assetName} without a checksum, and GitHub "
                + "reports no digest for it either. Refusing to replace the tool with a file "
                + "nothing vouches for.");
        }

        release.AssetSizes.TryGetValue(assetName, out var size);

        var offer = new SelfUpdateOffer(
            CurrentVersion: CurrentVersion,
            NewVersion: release.Version,
            TagName: release.TagName,
            IsPrerelease: release.IsPrerelease,
            PublishedAt: release.PublishedAt,
            AssetName: assetName,
            Url: url,
            Sha256: sha,
            SizeBytes: size > 0 ? size : null,
            ReleasePageUrl: $"https://github.com/{BuildInfo.ToolRepo}/releases/tag/{release.TagName}");

        return new SelfUpdateCheck(SelfUpdateState.Available, offer, null);
    }

    /// <summary>
    /// Downloads the offered build, checks it, and puts it in place of this one.
    ///
    /// Only ever called after a person said yes. Returns where the previous binary was set aside,
    /// so the caller can say what happened rather than leaving a mystery file behind.
    /// </summary>
    public async Task<SelfUpdateResult> ApplyAsync(SelfUpdateOffer offer,
                                                   CancellationToken ct = default)
    {
        var blocked = WhyCannotApply();
        if (blocked is not null) throw new InvalidOperationException(blocked);

        var executable = RunningExecutable!;
        var folder = Path.GetDirectoryName(executable)!;

        var staging = Path.Combine(_platform.UserDataDirectory, "update-staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        var fetcher = new ArchiveFetcher(staging);
        fetcher.Progress += (done, total) => Progress?.Invoke(done, total);

        var fetched = await fetcher
            .FetchAsync(offer.Url, offer.Sha256, $"installer-{offer.NewVersion}", ct)
            .ConfigureAwait(false);

        var incoming = Path.Combine(fetched.ExtractedPath, _platform.ExecutableFileName);
        if (!File.Exists(incoming))
        {
            throw new InvalidOperationException(
                $"{offer.AssetName} does not contain {_platform.ExecutableFileName}. "
                + "Nothing was replaced.");
        }

        // A name that says what it is and which version it holds, so someone who finds it in their
        // folder can tell whether it matters.
        var previous = $"{executable}{PreviousMarker}{offer.CurrentVersion}";
        var attempt = 1;
        while (File.Exists(previous))
        {
            previous = $"{executable}{PreviousMarker}{offer.CurrentVersion}-{++attempt}";
        }

        // Renaming a running executable is allowed on both systems; deleting it is not. Everything
        // after this point either finishes or puts the old name back.
        File.Move(executable, previous);

        try
        {
            File.Move(incoming, executable);
        }
        catch
        {
            File.Move(previous, executable);
            throw;
        }

        MakeExecutable(executable);
        RefreshCompanionFiles(fetched.ExtractedPath, folder);

        try { Directory.Delete(staging, recursive: true); } catch { /* staging is disposable */ }

        return new SelfUpdateResult(executable, previous, offer.NewVersion);
    }

    /// <summary>
    /// Clears binaries set aside by a previous update, and reports how many went.
    ///
    /// Called at startup, and that timing IS the safety net: reaching this line means the new
    /// build launched. Until it does, the previous one is still sitting there under its own name,
    /// ready to be run by anyone who needs it. Nothing here is allowed to throw — a leftover file
    /// is untidy, a tool that will not start is not.
    /// </summary>
    public static int ClearPreviousVersions()
    {
        var executable = RunningExecutable;
        if (executable is null) return 0;

        var folder = Path.GetDirectoryName(executable);
        if (folder is null) return 0;

        var cleared = 0;
        try
        {
            var stem = Path.GetFileName(executable) + PreviousMarker;
            foreach (var file in Directory.EnumerateFiles(folder, "*" + PreviousMarker + "*"))
            {
                if (!Path.GetFileName(file).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    File.Delete(file);
                    cleared++;
                }
                catch
                {
                    // Still locked, or not ours to delete. It will be tried again next time.
                }
            }
        }
        catch
        {
            // An unreadable folder is not a reason to refuse to start.
        }

        return cleared;
    }

    /// <summary>
    /// The archive this system needs, named exactly as prepare-release.ps1 writes it.
    ///
    /// ⚠ That script and this method are one contract in two files. Rename an archive there and
    /// every installed copy stops finding its update — quietly, because "no asset for this system"
    /// is a legitimate answer. Change the two together.
    ///
    /// Returns null when we publish nothing for this system, which is a fact to state rather than
    /// a case to work around: handing someone an x64 binary for an arm64 machine would produce a
    /// tool that no longer starts, and the previous one is already renamed by then.
    /// </summary>
    private string? AssetNameFor(string version)
    {
        if (_platform.HostArchitecture != GameArchitecture.X64) return null;

        return _platform.OsId switch
        {
            "windows" => $"UnityGameTranslatorInstaller-v{version}-win-x64.zip",
            "linux" => $"UnityGameTranslatorInstaller-v{version}-linux-x64.tar.gz",
            _ => null,
        };
    }

    /// <summary>
    /// The checksum to hold the download to, from the two places it can be stated.
    ///
    /// The sidecar we publish is the primary: it is written by prepare-release.ps1 at the same
    /// moment as the archive. GitHub's own digest is the second, computed by them from what they
    /// actually store. Either alone is enough to catch a corrupted download. Together they catch
    /// something else: if they disagree, one of the two files has been changed after the fact, and
    /// that is precisely the case where continuing would be the worst possible choice.
    /// </summary>
    private async Task<string?> ResolveChecksumAsync(PublishedRelease release, string assetName,
                                                     CancellationToken ct)
    {
        var sidecar = await _releases.ReadChecksumAsync(release, assetName, ct).ConfigureAwait(false);

        var digests = await _assets
            .GetDigestsAsync(BuildInfo.ToolRepo, release.TagName, ct)
            .ConfigureAwait(false);
        digests.TryGetValue(assetName, out var published);

        if (sidecar is not null && published is not null
            && !string.Equals(sidecar, published, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The checksum published beside {assetName} does not match the digest GitHub "
                + $"reports for it ({sidecar} against {published}). Refusing to go further: one "
                + "of the two was changed after the release was made.");
        }

        return sidecar ?? published;
    }

    /// <summary>
    /// Gives the new binary the execute bit on systems that have one.
    ///
    /// The tar already carries it and extraction preserves it, so this is insurance rather than
    /// the mechanism — but the cost of being wrong is a tool that will not start and a person with
    /// no way to see why, so it is set explicitly.
    /// </summary>
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // A filesystem without Unix modes. The bit came from the archive in that case.
        }
    }

    /// <summary>
    /// Brings the licence and notice files up to date, and only those already present.
    ///
    /// Refreshed because they are part of what was distributed and can change between versions.
    /// Only the ones already sitting there, because someone who deleted them has said something,
    /// and an updater that puts files back is an updater nobody can tidy up after.
    /// </summary>
    private void RefreshCompanionFiles(string extractedPath, string destination)
    {
        foreach (var source in Directory.EnumerateFiles(extractedPath))
        {
            var name = Path.GetFileName(source);
            if (string.Equals(name, _platform.ExecutableFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var target = Path.Combine(destination, name);
            if (!File.Exists(target)) continue;

            try { File.Copy(source, target, overwrite: true); }
            catch { /* a notice file that could not be refreshed is not worth failing an update */ }
        }
    }

    private static string Describe(ReleaseChannel channel) =>
        channel == ReleaseChannel.Beta ? "beta" : "stable";
}
