using System.Text.Json;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;
using UnityGameTranslator.Installer.Core.Update;

namespace UnityGameTranslator.Installer.Core.Install;

/// <summary>Everything the tool would write to install itself, listed before a single byte is.</summary>
public sealed record SelfInstallPlan(
    string SourceExecutable,
    string TargetDirectory,
    string TargetExecutable,
    IReadOnlyList<string> Files,
    IReadOnlyList<LauncherKind> Launchers,
    bool RegistersWithTheSystem,
    bool AlreadyInstalled,
    string? Refusal);

/// <summary>What removing it would take away, read from the receipt rather than assumed.</summary>
public sealed record SelfRemovalPlan(
    string Directory,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Launchers,
    string? Registration,
    string SettingsDirectory);

/// <summary>
/// The tool putting itself on the machine, and taking itself off again.
///
/// Portable first: the download runs where it lands and asks for nothing. This is the offer to
/// stay — proposed, never done on its own, and written down as it happens so that removing it
/// reads what was really written instead of what we believe we wrote.
///
/// ⚠ It never touches a game. Removing the tool and removing what the tool put in your games are
/// different acts with different costs, and folding them together would let one click undo months
/// of somebody's translating. The games are dealt with per game, from the game's own card.
/// </summary>
public sealed class SelfInstaller
{
    private readonly IPlatform _platform;

    public SelfInstaller(IPlatform platform) => _platform = platform;

    /// <summary>Where the record of an installation lives, when there is one.</summary>
    private string ReceiptPath => Path.Combine(_platform.UserDataDirectory, ToolInstallation.FileName);

    /// <summary>The installation on this machine, or null when the tool is running portable.</summary>
    public ToolInstallation? Installed()
    {
        try
        {
            if (!File.Exists(ReceiptPath)) return null;

            var installation = JsonSerializer.Deserialize<ToolInstallation>(
                File.ReadAllText(ReceiptPath));

            // A receipt describing an executable that is no longer there describes nothing. Someone
            // deleted the folder by hand, and the honest reading is "not installed" — not "installed
            // somewhere that does not answer".
            if (installation is null || !File.Exists(installation.Executable)) return null;

            return installation;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when the running process IS the installed copy.
    ///
    /// Compared by path rather than by the receipt existing: someone can install the tool and keep
    /// using the downloaded file, and telling them "you are installed" while they run the other one
    /// would be false in the way that matters — the update they apply lands on the copy in front of
    /// them, not on the one in their Start menu.
    /// </summary>
    public bool RunningTheInstalledCopy()
    {
        var installed = Installed();
        if (installed is null) return false;

        var running = SelfUpdater.RunningExecutable;
        if (running is null) return false;

        return string.Equals(Path.GetFullPath(running), Path.GetFullPath(installed.Executable),
                             StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What installing would do, or why it cannot be done.
    ///
    /// Everything is named before anything is written, which is the same rule the game installer
    /// follows. Nobody should have to accept "install it properly" and find out afterwards what
    /// "properly" meant on their machine.
    /// </summary>
    public SelfInstallPlan Plan()
    {
        var source = SelfUpdater.RunningExecutable;
        var target = _platform.SelfInstallDirectory;
        var targetExecutable = Path.Combine(target, _platform.ExecutableFileName);

        if (source is null)
        {
            return new SelfInstallPlan("", target, targetExecutable, [], [], false, false,
                "This build cannot tell where it is running from, so it will not copy itself.");
        }

        var files = new List<string> { targetExecutable };

        // Whatever came out of the archive alongside the executable travels with it: the licence
        // and notices because they must, the command line shim because without it the installed
        // copy would be less usable than the one being replaced.
        foreach (var companion in Companions(source))
            files.Add(Path.Combine(target, Path.GetFileName(companion)));

        var refusal = RefusalFor(source, target);

        return new SelfInstallPlan(
            SourceExecutable: source,
            TargetDirectory: target,
            TargetExecutable: targetExecutable,
            Files: files,
            Launchers: _platform.LauncherKinds,
            RegistersWithTheSystem: OperatingSystem.IsWindows(),
            AlreadyInstalled: Installed() is not null,
            Refusal: refusal);
    }

    private string? RefusalFor(string source, string target)
    {
        if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(source) ?? ""),
                          Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return "This is already the installed copy.";
        }

        if (NotASelfContainedBuild(source) is { } reason) return reason;

        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(target) ?? target,
                                     $".ugt-write-probe-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"{target} cannot be written to on this account.";
        }
    }

    /// <summary>
    /// Why a build cannot install itself, when it is not the one we ship.
    ///
    /// What ships is a single self-contained file: everything it needs is inside it, so copying it
    /// elsewhere gives a copy that runs. A build straight out of the compiler is the opposite — the
    /// executable is a small host beside a couple of hundred runtime files — and copying it alone
    /// produces an installed copy that cannot start, in a folder the person now believes holds a
    /// working tool.
    ///
    /// Told apart by what a bundled build does NOT leave on disk: its runtime configuration is
    /// inside the bundle, so these two files exist only next to an unbundled one. The release
    /// script already checks the other side of the same fact — that a publish produced exactly one
    /// file — so the two agree by construction.
    /// </summary>
    private static string? NotASelfContainedBuild(string executable)
    {
        var folder = Path.GetDirectoryName(executable);
        if (folder is null) return null;

        var stem = Path.GetFileNameWithoutExtension(executable);

        foreach (var suffix in new[] { ".deps.json", ".runtimeconfig.json" })
        {
            if (!File.Exists(Path.Combine(folder, stem + suffix))) continue;

            return "This is a development build: its runtime files sit beside it rather than "
                   + "inside it, so an installed copy would not start. Install from a published "
                   + "build instead.";
        }

        return null;
    }

    /// <summary>
    /// Copies the tool into place, writes the launchers asked for, and records all of it.
    ///
    /// <paramref name="launchers"/> is what the person ticked — an empty list is a legitimate
    /// answer and means the tool is installed with no shortcut at all.
    /// </summary>
    public ToolInstallation Install(SelfInstallPlan plan, IReadOnlyList<LauncherKind> launchers)
    {
        if (plan.Refusal is not null) throw new InvalidOperationException(plan.Refusal);

        var createdDirectory = !Directory.Exists(plan.TargetDirectory);
        Directory.CreateDirectory(plan.TargetDirectory);

        var written = new List<string>();

        // The executable last, after its companions: a folder holding a runnable tool that is
        // missing its licence is a worse half-finished state than one that is not runnable yet.
        foreach (var companion in Companions(plan.SourceExecutable))
        {
            var destination = Path.Combine(plan.TargetDirectory, Path.GetFileName(companion));
            File.Copy(companion, destination, overwrite: true);
            written.Add(destination);
        }

        File.Copy(plan.SourceExecutable, plan.TargetExecutable, overwrite: true);
        written.Add(plan.TargetExecutable);
        MakeExecutable(plan.TargetExecutable);

        var previous = Installed();

        var installation = new ToolInstallation
        {
            Version = SelfUpdater.CurrentVersion,
            InstalledAt = previous?.InstalledAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = previous is null ? null : DateTimeOffset.UtcNow,
            Directory = plan.TargetDirectory,
            Executable = plan.TargetExecutable,
            Files = written,
            CreatedDirectory = createdDirectory || previous?.CreatedDirectory == true,
        };

        foreach (var kind in launchers)
            installation.Launchers.AddRange(_platform.CreateLauncher(kind, plan.TargetExecutable));

        installation.Registration = _platform.RegisterInstalled(installation);

        Save(installation);
        return installation;
    }

    /// <summary>
    /// Copies THIS build over the installed one, keeping everything else as it stands.
    ///
    /// The case it serves: someone downloads a newer version, runs it, and the copy in their menu
    /// is still the old one. Without this they would have to remove and reinstall, and the shortcut
    /// they use every day would be recreated for no reason.
    ///
    /// Shortcuts are deliberately left alone rather than recreated: they point at a path that has
    /// not changed. ⚠ And they must be carried over in the receipt — writing a fresh record with no
    /// launchers would leave a shortcut on the machine that nothing knows about, so removing the
    /// tool would leave it behind pointing at nothing.
    /// </summary>
    public ToolInstallation UpdateInstalled()
    {
        var installed = Installed()
            ?? throw new InvalidOperationException("Nothing is installed to update.");

        var source = SelfUpdater.RunningExecutable
            ?? throw new InvalidOperationException(
                "This build cannot tell where it is running from.");

        if (NotASelfContainedBuild(source) is { } reason) throw new InvalidOperationException(reason);

        var written = new List<string>();

        foreach (var companion in Companions(source))
        {
            var destination = Path.Combine(installed.Directory, Path.GetFileName(companion));
            File.Copy(companion, destination, overwrite: true);
            written.Add(destination);
        }

        File.Copy(source, installed.Executable, overwrite: true);
        written.Add(installed.Executable);
        MakeExecutable(installed.Executable);

        installed.Version = SelfUpdater.CurrentVersion;
        installed.UpdatedAt = DateTimeOffset.UtcNow;
        installed.Files = written;
        installed.Registration = _platform.RegisterInstalled(installed);

        Save(installed);
        return installed;
    }

    /// <summary>
    /// Brings the system's entry back in step after the tool has updated itself.
    ///
    /// Without it, Windows' list of installed applications keeps showing the version that was
    /// installed the first time, forever. Cheap enough to do at every start, and it writes nothing
    /// when there is nothing to correct — the entry is one fixed key, so this updates it rather
    /// than adding a second one.
    /// </summary>
    public void RefreshRegistrationIfStale()
    {
        var installation = Installed();
        if (installation is null) return;
        if (!RunningTheInstalledCopy()) return;
        if (installation.Version == SelfUpdater.CurrentVersion) return;

        installation.Version = SelfUpdater.CurrentVersion;
        installation.UpdatedAt = DateTimeOffset.UtcNow;
        installation.Registration = _platform.RegisterInstalled(installation);

        Save(installation);
    }

    /// <summary>What a removal would take, read from what was written.</summary>
    public SelfRemovalPlan? PlanRemoval()
    {
        var installation = Installed();
        if (installation is null) return null;

        return new SelfRemovalPlan(
            Directory: installation.Directory,
            Files: installation.Files,
            Launchers: installation.Launchers,
            Registration: installation.Registration,
            SettingsDirectory: _platform.UserDataDirectory);
    }

    /// <summary>
    /// Takes the tool off the machine, and only the tool.
    ///
    /// <paramref name="alsoSettings"/> is asked separately and defaults to no, because settings are
    /// the one thing here somebody may have spent time on — API keys, the folders they added, the
    /// games they overruled. The record of the installation is inside that folder, so it goes last.
    ///
    /// ⚠ Games are never touched, whatever is passed. What the tool put into a game is removed from
    /// that game's own card, one game at a time, with its own confirmation.
    /// </summary>
    public IReadOnlyList<string> Remove(bool alsoSettings)
    {
        var installation = Installed();
        if (installation is null) return ["Nothing to remove: this copy was never installed."];

        var problems = new List<string>();

        foreach (var launcher in installation.Launchers)
            Delete(launcher, problems);

        if (installation.Registration is { } registration)
            _platform.UnregisterInstalled(registration);

        // The file we are running from is dealt with last and separately. Everything else goes now.
        string? running = null;

        foreach (var file in installation.Files)
        {
            if (IsRunning(file)) { running = file; continue; }
            Delete(file, problems);
        }

        if (running is null)
        {
            if (installation.CreatedDirectory) RemoveIfEmpty(installation.Directory, problems);
        }

        if (alsoSettings)
        {
            try
            {
                Directory.Delete(_platform.UserDataDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                problems.Add($"Could not remove the settings folder: {ex.Message}");
            }
        }
        else
        {
            // The tool is no longer installed, so the record of an installation must not survive to
            // say otherwise the next time the portable copy is opened.
            Delete(ReceiptPath, problems);
        }

        // Last, because it outlives us: nothing after this line is guaranteed to run.
        if (running is not null) FinishAfterWeExit(running, installation, problems);

        return problems;
    }

    /// <summary>
    /// Deletes the file we are running from, once we are no longer running.
    ///
    /// ⚠ The question this answers: can a program that has been asked to uninstall itself actually
    /// finish the job? Windows refuses to delete a running executable, and the first version dodged
    /// that by renaming it aside and stopping there — which left ninety megabytes and a folder
    /// behind, for good, because nothing of ours ever runs again to clear them. A removal that
    /// leaves the bulk of what it removed is not a removal.
    ///
    /// So the last act is to hand the job to the shell and get out of the way: a command that waits
    /// a few seconds, deletes the file, and removes the folder if nothing else is in it. No helper
    /// program of ours — there is nothing to ship and nothing to sign — and no scheduled reboot
    /// operation, which a per-user installation has no right to ask for anyway.
    ///
    /// On anything but Windows this is not a problem at all: a running executable can be unlinked,
    /// and the file simply goes.
    /// </summary>
    private static void FinishAfterWeExit(string executable, ToolInstallation installation,
                                          List<string> problems)
    {
        if (!OperatingSystem.IsWindows())
        {
            Delete(executable, problems);
            if (installation.CreatedDirectory) RemoveIfEmpty(installation.Directory, problems);
            return;
        }

        // ping as the wait: timeout refuses to run without a console of its own, and this command
        // is started without one. Two passes, because how long we take to close is not ours to know.
        var folder = installation.CreatedDirectory ? $" & rd /q \"{installation.Directory}\"" : "";
        var command = $"ping 127.0.0.1 -n 4 >nul & del /f /q \"{executable}\" "
                      + $"& ping 127.0.0.1 -n 3 >nul & del /f /q \"{executable}\"{folder}";

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            // Said rather than swallowed: the person can delete the folder themselves, and they can
            // only do that if they are told which one and why it is still there.
            problems.Add($"Everything else is gone, but {executable} is still in use and could not "
                         + $"be scheduled for deletion ({ex.Message}). Delete "
                         + $"{installation.Directory} by hand once this window has closed.");
        }
    }

    /// <summary>
    /// What ships beside the executable, by name.
    ///
    /// ⚠ A named list, and the first version of this was not — it took everything sitting in the
    /// same folder, on the reasoning that prepare-release.ps1 decides what ships and a list written
    /// twice is a list that will disagree with itself.
    ///
    /// That reasoning ignored where the file actually is. People unpack a zip into the folder they
    /// downloaded it to, so "everything beside the executable" is routinely somebody's entire
    /// Downloads folder — and offering to copy that into Programs is not a small price, it is a
    /// different program. Seen for real on a development build, where the folder holds two hundred
    /// runtime files.
    ///
    /// So the names are written here, and this is one contract in two files: add a file to the
    /// Windows archive in prepare-release.ps1 and it belongs in this list too. Three names drifting
    /// is a far smaller risk than the one it replaces.
    /// </summary>
    private static readonly string[] ShippedBesideTheExecutable =
    [
        "LICENSE",
        "THIRD_PARTY_LICENSES.md",
        "ugt-installer.cmd",
    ];

    private static IEnumerable<string> Companions(string executable)
    {
        var folder = Path.GetDirectoryName(executable);
        if (folder is null) yield break;

        foreach (var name in ShippedBesideTheExecutable)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) yield return path;
        }
    }

    private void Save(ToolInstallation installation)
    {
        Directory.CreateDirectory(_platform.UserDataDirectory);

        File.WriteAllText(ReceiptPath, JsonSerializer.Serialize(installation,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsRunning(string file) =>
        string.Equals(SelfUpdater.RunningExecutable, file, StringComparison.OrdinalIgnoreCase);


    private static void Delete(string path, List<string> problems)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            problems.Add($"Could not remove {path}: {ex.Message}");
        }
    }

    private static void RemoveIfEmpty(string directory, List<string> problems)
    {
        try
        {
            if (!Directory.Exists(directory)) return;

            // Only when nothing else is in there. A folder somebody else has been using is not ours
            // to delete, even if we made it.
            if (Directory.EnumerateFileSystemEntries(directory).Any()) return;

            Directory.Delete(directory);
        }
        catch (Exception ex)
        {
            problems.Add($"Could not remove {directory}: {ex.Message}");
        }
    }

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
            // A filesystem without Unix modes.
        }
    }
}
