using System.Diagnostics;
using UnityGameTranslator.Installer.Core.Api;
using UnityGameTranslator.Installer.Core.Platform;

namespace UnityGameTranslator.Installer.Core.Ai;

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
    /// Starts the installed server and waits for it to answer.
    ///
    /// `ollama serve` is run detached and with no environment of our own: setting OLLAMA_HOST or
    /// OLLAMA_MODELS here would silently override the configuration we are trying not to disturb.
    /// The user's own settings apply, exactly as when they start it themselves.
    ///
    /// ⚠ Never bound to 0.0.0.0. A January 2026 Censys/SentinelOne survey found thousands of
    /// Ollama servers exposed on the open internet for precisely that reason. The default binding
    /// is local, and we do not change it.
    /// </summary>
    public async Task<bool> StartAsync(string executablePath, CancellationToken ct = default)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("serve");

            using var process = Process.Start(start);
            if (process is null) return false;
        }
        catch
        {
            return false;
        }

        // Loading takes a moment, and reporting failure too early would send someone installing a
        // second copy of something that was about to work.
        var probe = new AiServerProbe();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (await probe.ListModelsAsync("http://localhost:11434", ct).ConfigureAwait(false) is not null)
                return true;
        }

        return false;
    }
}
