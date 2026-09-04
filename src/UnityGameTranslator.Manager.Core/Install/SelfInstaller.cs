using System.Text.Json;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Update;

namespace UnityGameTranslator.Manager.Core.Install;

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

/// <summary>An installation as the disk describes it, rather than as the receipt remembers it.</summary>
public sealed record SelfInstallationState(ToolInstallation? Record, IReadOnlyList<string> Missing)
{
    /// <summary>There is an installation, and every piece of it is where it should be.</summary>
    public bool Sound => Record is not null && Missing.Count == 0;

    /// <summary>There is a record, but pieces of what it describes are gone.</summary>
    public bool NeedsRepair => Record is not null && Missing.Count > 0;
}

/// <summary>
/// What a removal actually did, item by item.
///
/// Not a list of complaints: what went matters as much as what did not. Someone told only about
/// the failure has no idea whether the tool is half removed or barely touched, and the answer
/// changes what they should do next.
/// </summary>
public sealed record SelfRemovalReport(
    IReadOnlyList<string> Gone,
    IReadOnlyList<string> Left,
    string? BeingDeletedAfterExit,
    string? WhereItWas)
{
    public bool Complete => Left.Count == 0;
}

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
    /// The installation checked against what is actually on the machine, item by item.
    ///
    /// ⚠ A receipt is a memory, not a fact. "Installed" used to mean "there is a receipt and its
    /// executable exists", and that is true of a folder somebody copied files into by hand — no
    /// shortcut, no entry in the system's list, nothing that a removal could ever find again. Seen
    /// for real: after a removal, files copied back, and the tool offered to switch to an
    /// installation that was not one.
    ///
    /// So every piece the receipt names is looked for, and what is missing is named. What that is
    /// worth on screen is one word: repair, rather than a switch to somewhere broken.
    /// </summary>
    public SelfInstallationState Inspect()
    {
        var installation = Installed();
        if (installation is null) return new SelfInstallationState(null, []);

        var missing = new List<string>();

        foreach (var file in installation.Files)
            if (!File.Exists(file)) missing.Add(file);

        foreach (var launcher in installation.Launchers)
            if (!File.Exists(launcher)) missing.Add(launcher);

        if (installation.Registration is { } registration && !_platform.IsRegistered(registration))
            missing.Add("its entry in the system's list of installed apps");

        return new SelfInstallationState(installation, missing);
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

    /// <summary>
    /// Reasons not to offer this at all — and only ones that can be known without touching the disk.
    ///
    /// ⚠ There used to be a third: a test file written into the installation folder's parent and
    /// deleted again, to prove the folder could be written to. Three things were wrong with it, and
    /// the third is the one that matters.
    ///
    /// It ran on every drawing of the overview, to answer a question whose answer changes about
    /// never. It wrote to disk to decide whether to ASK — and worse, it created
    /// %LOCALAPPDATA%\Programs when that folder did not exist, so merely looking at the list of
    /// games created a folder on the machine. This program's own rule is that nothing is written
    /// until somebody says so, and the check meant to protect an installation was breaking it.
    ///
    /// So nothing is proven in advance. Install copies files; if the folder cannot be written to,
    /// the copy fails and the window shows the reason the system gave — which is more accurate than
    /// anything we would have guessed from a probe, and costs nothing until somebody asks for it.
    ///
    /// 🔸 Not the same trade as the updater's WhyCannotApply, which stays: that one exists to avoid
    /// forty megabytes of download that could not be applied, and it probes a folder that already
    /// exists because the running executable is in it.
    /// </summary>
    private string? RefusalFor(string source, string target)
    {
        if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(source) ?? ""),
                          Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return "This is already the installed copy.";
        }

        return NotASelfContainedBuild(source);
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
    public SelfRemovalReport Remove(bool alsoSettings)
    {
        var installation = Installed();
        if (installation is null)
        {
            return new SelfRemovalReport([], [], null, null);
        }

        var gone = new List<string>();
        var left = new List<string>();

        // ⚠ Nothing here is deleted just because the receipt names it. The receipt is a JSON file in
        // a folder the person can open, so a mistake in it — ours in a future version, or theirs
        // with a text editor — would otherwise be a list of things to delete anywhere on the disk.
        // A shortcut has to look like a shortcut, and a file has to be inside the folder we
        // installed into. Anything else is reported rather than acted on.
        foreach (var launcher in installation.Launchers)
        {
            if (!LooksLikeOurLauncher(launcher))
            {
                left.Add($"{launcher} — left alone: it is not a shortcut this tool would have made");
                continue;
            }

            Take(launcher, "shortcut", gone, left);
        }

        if (installation.Registration is { } registration)
        {
            _platform.UnregisterInstalled(registration);
            gone.Add("The entry in the system's list of installed apps");
        }

        // The file we are running from is dealt with last and separately. Everything else goes now.
        string? running = null;

        foreach (var file in installation.Files)
        {
            if (!Inside(installation.Directory, file))
            {
                left.Add($"{file} — left alone: it is outside {installation.Directory}");
                continue;
            }

            if (IsRunning(file)) { running = file; continue; }
            Take(file, "file", gone, left);
        }

        // Whoever made the folder, it goes if our files were the only thing in it — see
        // FinishAfterWeExit for why "only when we created it" was the wrong test.
        if (running is null) RemoveIfEmpty(installation.Directory, gone, left);

        if (alsoSettings)
        {
            try
            {
                Directory.Delete(_platform.UserDataDirectory, recursive: true);
                gone.Add(_platform.UserDataDirectory);
            }
            catch (Exception ex)
            {
                left.Add($"{_platform.UserDataDirectory} — {ex.Message}");
            }
        }
        else
        {
            // The tool is no longer installed, so the record of an installation must not survive to
            // say otherwise the next time the portable copy is opened.
            Take(ReceiptPath, "record", gone, left);
        }

        // Last, because it outlives us: nothing after this line is guaranteed to run.
        if (running is not null) FinishAfterWeExit(running, installation, _platform.SelfInstallDirectory, left);

        return new SelfRemovalReport(gone, left, running, installation.Directory);
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
    /// So the last act is to hand the job to the shell and get out of the way: a command that keeps
    /// trying to delete the file until it succeeds, then removes the folder if nothing else is in
    /// it. No helper program of ours — there is nothing to ship and nothing to sign — and no
    /// scheduled reboot operation, which a per-user installation has no right to ask for anyway.
    ///
    /// ⚠ It RETRIES rather than waiting a fixed few seconds, and that is not caution for its own
    /// sake. It is started while the window is still up, and how long that window stays up is not
    /// ours to know: the first version waited about six seconds, so anyone who stopped to read
    /// something before closing came back to a folder still holding ninety megabytes. A minute of
    /// patience costs nothing — the command is asleep for all of it.
    ///
    /// On anything but Windows this is not a problem at all: a running executable can be unlinked,
    /// and the file simply goes.
    /// </summary>
    private static void FinishAfterWeExit(string executable, ToolInstallation installation,
                                          string expectedDirectory, List<string> left)
    {
        if (!OperatingSystem.IsWindows())
        {
            Take(executable, "file", [], left);
            RemoveIfEmpty(installation.Directory, [], left);
            return;
        }

        // 🔴 The folder handed to the shell is read from a JSON in the user's profile, and this tool
        // only ever installs itself in ONE place. A record naming any other folder is not a record
        // this tool wrote — so it is not a folder this tool removes, however empty, however quoted.
        // The files above were already held to `Inside(installation.Directory, …)`; this holds the
        // directory itself to the same standard.
        if (!Inside(expectedDirectory, installation.Directory)
            && !string.Equals(Path.GetFullPath(expectedDirectory).TrimEnd(Path.DirectorySeparatorChar),
                              Path.GetFullPath(installation.Directory).TrimEnd(Path.DirectorySeparatorChar),
                              ArchiveFetcher.PathComparison))
        {
            left.Add($"{installation.Directory} — left alone: it is not where this tool installs "
                     + $"itself ({expectedDirectory}). Delete it by hand if it is yours to delete.");
            return;
        }

        // ⚠ The one command in this program built by pasting strings together, so it is the one
        // place where a path could stop being a path and start being an instruction. Quoting covers
        // spaces and even ampersands, but not the percent sign: cmd pairs percent signs across the
        // whole line to expand variables, and this line names the file twice, so two of them would
        // pair up and leave a command that means something else entirely.
        //
        // A path we cannot quote for certain is not a path we send to a shell. Everything else has
        // been removed by now; this says which folder is left and why, which is what someone needs
        // in order to finish it themselves.
        if (Unquotable(executable) || Unquotable(installation.Directory))
        {
            left.Add($"{executable} — in use, and its path contains a character this cannot safely "
                     + $"hand to the shell. Delete {installation.Directory} by hand once this "
                     + "window has closed.");
            return;
        }

        // ping as the wait: timeout refuses to run without a console of its own, and this command is
        // started without one. Sixty passes of roughly a second, giving up quietly after that.
        //
        // ⚠ The folder goes whatever the receipt says about who made it. It used to be attempted
        // only when we had created it, which sounds careful and is wrong in the ordinary case:
        // install, remove, install again, and the second install finds the folder already there, so
        // it records that it did not make it — and the second removal leaves an empty folder behind
        // for good. Somebody watched exactly that happen.
        //
        // rd without /s is the guard that actually matters: it refuses any folder holding anything
        // else, so nothing of anybody's is taken by this, whoever made the folder.
        var done = $"rd /q \"{installation.Directory}\" & exit";

        var command = $"for /l %i in (1,1,60) do (ping 127.0.0.1 -n 2 >nul "
                      + $"& del /f /q \"{executable}\" 2>nul "
                      + $"& if not exist \"{executable}\" ({done}))";

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
            left.Add($"{executable} — in use, and the deletion could not be handed on ({ex.Message}). "
                     + $"Delete {installation.Directory} by hand once this window has closed.");
        }
    }

    /// <summary>
    /// A path that cannot be put inside double quotes and still mean itself to cmd.
    ///
    /// A quote cannot appear in a Windows path at all, so it can only arrive through something that
    /// has already gone wrong. A percent sign can, and it is the one that matters: cmd pairs them
    /// across the line and expands what is between.
    /// </summary>
    private static bool Unquotable(string path) => path.Contains('"') || path.Contains('%');

    /// <summary>
    /// Is this file genuinely under that folder? Compared on the resolved paths, so "…\Installer\..\
    /// ..\Windows\System32" answers no rather than being read as a name that starts the right way.
    /// </summary>
    private static bool Inside(string folder, string file)
    {
        try
        {
            var root = Path.GetFullPath(folder);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

            // The file system's rule for case, not Windows' everywhere — see ArchiveFetcher.
            return Path.GetFullPath(file).StartsWith(root, ArchiveFetcher.PathComparison);
        }
        catch
        {
            // A path the system will not even resolve is not one to delete on the strength of.
            return false;
        }
    }

    /// <summary>
    /// A shortcut we would have written: our name, and the extension the system uses for one.
    ///
    /// Shortcuts are the one thing that legitimately lives outside the installation folder — in the
    /// Start menu, on the desktop — so they cannot be checked by where they are. They are checked
    /// by what they are instead.
    /// </summary>
    private static bool LooksLikeOurLauncher(string path)
    {
        var name = Path.GetFileName(path);

        return name.StartsWith("UnityGameTranslator", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("unitygametranslator", StringComparison.Ordinal);
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
        "ugt-manager.cmd",
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


    /// <summary>
    /// Deletes one thing and records which side of the ledger it ended up on.
    ///
    /// A file that was already gone counts as gone: the person asked for it not to be there, and it
    /// is not there. Reporting it as a failure would send someone looking for a problem that has
    /// already been solved — by them, or by an earlier attempt.
    /// </summary>
    private static void Take(string path, string what, List<string> gone, List<string> left)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            gone.Add(path);
        }
        catch (Exception ex)
        {
            left.Add($"{path} — {ex.Message}");
        }
    }

    private static void RemoveIfEmpty(string directory, List<string> gone, List<string> left)
    {
        try
        {
            if (!Directory.Exists(directory)) { gone.Add(directory); return; }

            // Only when nothing else is in there. A folder somebody else has been using is not ours
            // to delete, even if we made it — and it is not a failure either, so it is said plainly
            // rather than counted as something that went wrong.
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                left.Add($"{directory} — left alone: it holds something that is not ours");
                return;
            }

            Directory.Delete(directory);
            gone.Add(directory);
        }
        catch (Exception ex)
        {
            left.Add($"{directory} — {ex.Message}");
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
