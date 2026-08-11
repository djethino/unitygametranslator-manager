using System.Diagnostics;
using UnityGameTranslator.Manager.Core.Api;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Ai;

/// <summary>What we found, which decides what we are allowed to offer.</summary>
public enum OllamaState
{
    /// <summary>A server is answering. Nothing to install, nothing to start.</summary>
    Running,

    /// <summary>Installed but not answering. We offer to start it — we install nothing.</summary>
    InstalledButStopped,

    /// <summary>Nothing found. Only here may we offer to install.</summary>
    Absent,
}

public sealed record OllamaStatus(OllamaState State, string? ExecutablePath, string? Detail);

/// <summary>
/// What happened when we tried to start it, and what the user can do about it.
///
/// More than a boolean because "we cannot do this for you" is a legitimate outcome, not a
/// failure: starting a systemd service needs a password we must never ask for or work around. In
/// that case the exact command belongs on screen, which is worth more than a red message.
/// </summary>
public sealed record OllamaStartOutcome(
    bool Started,
    string? Command = null,
    string? HowToStop = null,
    string? Failure = null);

/// <summary>
/// Finds an existing Ollama before anything else is considered.
///
/// This exists to protect what the user already has. The decision was explicit: never touch an
/// existing Ollama install. So the order is not negotiable — answering server, then installed
/// binary, and only then "nothing here". Installing a second Ollama next to a working one would
/// give them two servers, two model folders, several gigabytes duplicated, and a support problem
/// they did not have before.
///
/// ⚠ We look for the binary, never for a registry entry or an uninstall key: someone can perfectly
/// well have unpacked Ollama by hand, and the standalone zip registers nothing at all.
/// </summary>
public sealed class OllamaProbe
{
    private readonly IPlatform _platform;

    public OllamaProbe(IPlatform platform) => _platform = platform;

    /// <summary>
    /// Where the executable lives, per OS. Ollama's Windows installer is per-user by design
    /// (a machine-wide install is broken upstream, ollama#7969), so LOCALAPPDATA comes first.
    /// </summary>
    private IEnumerable<string> CandidatePaths()
    {
        if (_platform.OsId == "windows")
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "Programs", "Ollama", "ollama.exe");
            yield return Path.Combine(local, "Programs", "Ollama", "ollama app.exe");

            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles))
                yield return Path.Combine(programFiles, "Ollama", "ollama.exe");
        }
        else
        {
            yield return "/usr/local/bin/ollama";
            yield return "/usr/bin/ollama";

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".local", "bin", "ollama");
            yield return Path.Combine(home, ".ollama", "bin", "ollama");
        }
    }

    /// <summary>The executable name as it would appear in PATH.</summary>
    private string BinaryName => _platform.OsId == "windows" ? "ollama.exe" : "ollama";

    /// <summary>
    /// The path to an installed Ollama, or null.
    ///
    /// PATH is searched too, and last: someone who installed through a package manager, Homebrew
    /// or a distribution package will have it there and nowhere we could have guessed.
    /// </summary>
    public string? FindExecutable()
    {
        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate)) return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), BinaryName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // A malformed PATH entry is not a reason to stop looking at the others.
            }
        }

        return null;
    }

    /// <summary>
    /// The whole answer in one call: is something serving, is something installed, or neither.
    /// </summary>
    public async Task<OllamaStatus> InspectAsync(CancellationToken ct = default)
    {
        // Asked first and asked of the network, not of the disk: a server may be Ollama in a
        // container, on another port, or something else entirely that answers just as well. The
        // mod only ever needs an endpoint, so a working endpoint ends the question.
        var models = await new AiServerProbe()
            .ListModelsAsync("http://localhost:11434", ct)
            .ConfigureAwait(false);

        if (models is not null)
        {
            return new OllamaStatus(OllamaState.Running, FindExecutable(),
                $"already serving {models.Count} model(s)");
        }

        var executable = FindExecutable();
        return executable is not null
            ? new OllamaStatus(OllamaState.InstalledButStopped, executable, null)
            : new OllamaStatus(OllamaState.Absent, null, null);
    }

    /// <summary>
    /// The desktop application, on the systems that have one.
    ///
    /// On Windows this is what the official installer itself launches once it is done, and it is
    /// what puts the icon next to the clock. Starting the bare server instead leaves nothing to
    /// show for it: it outlives our own window and the only way left to stop it is the task
    /// manager, which is a poor trade for a tool meant to make this easy.
    ///
    /// Linux has no such application at all — Ollama ships no GUI there.
    /// </summary>
    private string? FindDesktopApp()
    {
        if (_platform.OsId != "windows") return null;

        var binary = FindExecutable();
        var folder = binary is null ? null : Path.GetDirectoryName(binary);
        if (folder is null) return null;

        var app = Path.Combine(folder, "ollama app.exe");
        return File.Exists(app) ? app : null;
    }

    /// <summary>
    /// Whether the official Linux install is in charge here.
    ///
    /// It matters more than it looks. That install runs the server as its own "ollama" user with
    /// its own model folder (/usr/share/ollama/.ollama). Starting a second server as the logged-in
    /// user would fight it for port 11434 and read a different model folder — two servers, two
    /// libraries, and a problem that did not exist before we helped.
    /// </summary>
    private static bool HasSystemdService() =>
        File.Exists("/etc/systemd/system/ollama.service")
        || File.Exists("/usr/lib/systemd/system/ollama.service");

    /// <summary>
    /// Starts what is installed, the way that system expects, and says how to stop it again.
    ///
    /// No environment of our own is ever set: an OLLAMA_HOST or OLLAMA_MODELS from us would
    /// silently override the configuration we are trying not to disturb. The user's own settings
    /// apply, exactly as when they start it themselves.
    ///
    /// ⚠ Never bound to 0.0.0.0. A January 2026 Censys/SentinelOne survey found thousands of
    /// Ollama servers exposed on the open internet for precisely that reason. The default binding
    /// is local and we do not touch it.
    ///
    /// ⚠ Never with sudo. Where a password is needed we stop and show the command instead — a
    /// tool that asks for an administrator password to start a translation helper has no business
    /// being trusted with one.
    /// </summary>
    public async Task<OllamaStartOutcome> StartAsync(string executablePath,
                                                     CancellationToken ct = default)
    {
        string howToStop;

        if (_platform.OsId != "windows" && HasSystemdService())
        {
            // Tried without elevation on purpose: some systems allow it through polkit, and where
            // they do this simply works. Where they do not, the command goes on screen.
            if (!TryRun("systemctl", new[] { "start", "ollama" }))
            {
                return new OllamaStartOutcome(false,
                    Command: "sudo systemctl start ollama",
                    HowToStop: "sudo systemctl stop ollama");
            }

            howToStop = "sudo systemctl stop ollama";
        }
        else
        {
            var app = FindDesktopApp();
            var target = app ?? executablePath;

            howToStop = app is not null
                ? "Right-click the Ollama icon next to the clock and choose Quit."
                : _platform.OsId == "windows"
                    ? "It runs in the background with no icon: ending \"ollama.exe\" in the Task "
                      + "Manager stops it."
                    : "It runs in the background: \"pkill ollama\" stops it.";

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                // The desktop app starts the server itself; only the bare binary needs telling.
                if (app is null) start.ArgumentList.Add("serve");

                using var process = Process.Start(start);
                if (process is null)
                    return new OllamaStartOutcome(false, Failure: "It would not start.");
            }
            catch (Exception ex)
            {
                return new OllamaStartOutcome(false, Failure: $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Loading takes a moment, and reporting failure too early would send someone installing a
        // second copy of something that was about to work.
        var probe = new AiServerProbe();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (await probe.ListModelsAsync("http://localhost:11434", ct).ConfigureAwait(false) is not null)
                return new OllamaStartOutcome(true, HowToStop: howToStop);
        }

        return new OllamaStartOutcome(false,
            Failure: "It was started but is not answering yet. Giving it a moment and searching "
                   + "again usually settles it.");
    }

    /// <summary>Runs a command and reports whether it succeeded. Never elevated.</summary>
    private static bool TryRun(string fileName, IEnumerable<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null) return false;

            return process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
