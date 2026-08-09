using System.Diagnostics;
using UnityGameTranslator.Installer.Core.Api;
using UnityGameTranslator.Installer.Core.Install;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;
using UnityGameTranslator.Installer.Core.Net;

namespace UnityGameTranslator.Installer.Core.Ai;

/// <summary>
/// What we can offer on this machine, and at what cost — decided before anything is downloaded.
/// </summary>
public sealed record OllamaOffer(
    bool CanInstall,
    string? AssetName,
    long? SizeBytes,
    string? Sha256,
    string? Refusal)
{
    public string SizeText => SizeBytes is { } bytes
        ? $"{bytes / 1024.0 / 1024 / 1024:F1} GB"
        : "unknown size";
}

/// <summary>
/// Installs Ollama the normal way, or does not install at all.
///
/// Route A of the decision tree (§8.1): the official installer, run silently. It is per-user, needs
/// no administrator, puts Ollama in PATH and leaves it updated by Ollama itself. The alternative
/// considered — unpacking a private copy somewhere of our own — was rejected: it would give
/// someone a server only we know about, that we would then have to maintain, and that would be
/// useless to them anywhere else.
///
/// Two rules this class does not bend:
///
/// 1. **A checksum is mandatory here.** Elsewhere in this tool a missing hash is reported and the
///    install proceeds, because refusing would block projects that publish none. Not here: this
///    downloads an executable and runs it silently, with the user's own rights. Without a hash we
///    do not run anything — we send them to the official download page instead. Ollama publishes
///    a digest for every release asset, so this costs nothing in practice, and it means a
///    compromised mirror or a corrupted transfer cannot end in code execution.
///
/// 2. **We never touch an existing install.** Callers must have checked OllamaProbe first. This
///    class does not verify it a second time, but installing over a working Ollama is exactly the
///    thing the user asked us never to do.
/// </summary>
public sealed class OllamaInstaller
{
    private const string Repository = "ollama/ollama";

    private readonly IPlatform _platform;
    private readonly GitHubAssets _assets;
    private readonly HttpClient _http;

    public OllamaInstaller(IPlatform platform, HttpClient? http = null)
    {
        _platform = platform;
        _http = http ?? Http.Create(TimeSpan.FromMinutes(30));
        _assets = new GitHubAssets(_http);
    }

    /// <summary>Bytes downloaded so far, and the total when the server states one.</summary>
    public event Action<long, long?>? Progress;

    /// <summary>
    /// The asset for this machine, or null when we have nothing to offer.
    ///
    /// ⚠ Linux is deliberately not automated. The official route asks for sudo, and on a Steam Deck
    /// the system partition is read-only — an unattended install there would either fail halfway or
    /// break an immutable system to place a translation helper. Someone on Linux gets the official
    /// command and installs it themselves, which they are far better placed to do than we are.
    /// </summary>
    private string? AssetFor() => _platform.OsId switch
    {
        "windows" => _platform.HostArchitecture == GameArchitecture.X64
            ? "OllamaSetup.exe"
            : null,
        _ => null,
    };

    /// <summary>
    /// What is on offer, priced. Nothing is downloaded here — a gigabyte-and-a-half download must
    /// be announced before it starts, not discovered while it runs.
    /// </summary>
    public async Task<OllamaOffer> PrepareAsync(CancellationToken ct = default)
    {
        var asset = AssetFor();
        if (asset is null)
        {
            return new OllamaOffer(false, null, null, null, _platform.OsId == "windows"
                ? "Ollama does not publish an installer for this processor architecture."
                : "On this system Ollama is installed with its own script, which needs your "
                  + "password and knows your distribution better than we do. Run: "
                  + "curl -fsSL https://ollama.com/install.sh | sh");
        }

        var release = await LatestReleaseAsync(ct).ConfigureAwait(false);
        if (release is null)
        {
            return new OllamaOffer(false, asset, null, null,
                "Could not reach GitHub to check the current Ollama release.");
        }

        var (tag, sizes) = release.Value;
        var digests = await _assets.GetDigestsAsync(Repository, tag, ct).ConfigureAwait(false);

        if (!digests.TryGetValue(asset, out var sha))
        {
            return new OllamaOffer(false, asset, sizes.GetValueOrDefault(asset), null,
                "Ollama published no checksum for this file. We will not download and run an "
                + "installer we cannot verify — install it from ollama.com instead.");
        }

        return new OllamaOffer(true, asset, sizes.GetValueOrDefault(asset), sha, null);
    }

    /// <summary>
    /// Downloads the verified installer and runs it silently, then waits for the server.
    ///
    /// /VERYSILENT belongs to Inno Setup, which is what Ollama ships. No /DIR is passed: the
    /// default location is the one every Ollama guide, and Ollama's own updater, expects.
    /// </summary>
    public async Task<string?> InstallAsync(OllamaOffer offer, CancellationToken ct = default)
    {
        if (!offer.CanInstall || offer.AssetName is null || offer.Sha256 is null)
            return offer.Refusal ?? "Nothing to install.";

        var release = await LatestReleaseAsync(ct).ConfigureAwait(false);
        if (release is null) return "Could not reach GitHub.";

        var staging = Path.Combine(_platform.UserDataDirectory, "staging");
        Directory.CreateDirectory(staging);
        var target = Path.Combine(staging, offer.AssetName);

        try
        {
            var url = GitHubAssets.BuildUrl(Repository, release.Value.Tag, offer.AssetName);
            await DownloadAsync(url, target, ct).ConfigureAwait(false);

            var actual = FileOperations.HashFile(target);
            if (!string.Equals(actual, offer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(target);
                return $"Checksum mismatch — expected {offer.Sha256}, got {actual}. "
                     + "The download was discarded and nothing was run.";
            }

            var start = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("/VERYSILENT");

            using var process = Process.Start(start);
            if (process is null) return "The installer would not start.";

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return $"The Ollama installer stopped with code {process.ExitCode}.";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            TryDelete(target);
        }

        // The installer starts the server itself; give it a moment rather than declaring failure
        // on a machine that is simply slower than ours.
        var probe = new AiServerProbe();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (await probe.ListModelsAsync("http://localhost:11434", ct).ConfigureAwait(false) is not null)
                return null;
        }

        return "Ollama was installed but is not answering yet. Starting it from the Start menu "
             + "usually settles it.";
    }

    /// <summary>Tag and asset sizes of the current release.</summary>
    private async Task<(string Tag, Dictionary<string, long> Sizes)?> LatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            var json = await _http
                .GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct)
                .ConfigureAwait(false);

            using var document = System.Text.Json.JsonDocument.Parse(json);
            var tag = document.RootElement.GetProperty("tag_name").GetString();
            if (tag is null) return null;

            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is not null && asset.TryGetProperty("size", out var s))
                        sizes[name] = s.GetInt64();
                }
            }

            return (tag, sizes);
        }
        catch
        {
            return null;
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            Progress?.Invoke(done, total);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* staging cleanup is best effort */ }
    }
}
