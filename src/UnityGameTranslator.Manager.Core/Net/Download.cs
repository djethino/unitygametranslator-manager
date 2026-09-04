using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Diagnostics;

namespace UnityGameTranslator.Manager.Core.Net;

/// <summary>
/// The one way a file is downloaded to disk by this tool.
///
/// 🔴 **One implementation, because there were two, and neither asked any question.** The archive
/// fetcher and the Ollama installer each carried the same streaming loop; both took an address on
/// trust, followed every redirect, and read until the server stopped. Everything that comes through
/// here is later unpacked into a game folder, put in place of this tool, or executed — so this is
/// where the questions belong, once:
///
///  · is the address one we agree to start from (<see cref="DownloadOrigins.IsAllowedDownload"/>);
///  · after the redirects, did it land with the same publisher (<see cref="DownloadOrigins.IsAllowedLanding"/>);
///  · is the file the size the server said, and under the ceiling the caller set.
///
/// ⚠ The ceiling is the CALLER's, because the files differ by two orders of magnitude: a loader
/// archive is a few megabytes, this tool's update forty, Ollama's installer 1.5 GB (measured
/// 2026-09-04). One number for all of them would either mean nothing or refuse a real file. What
/// a ceiling protects against, with the origin already checked, is a publisher serving something
/// absurd — a disk filled, not a machine compromised — so it is set with room to spare, never
/// close to the file.
/// </summary>
public static class Download
{
    public static async Task ToFileAsync(HttpClient http, string url, string destination, long maxBytes,
                                         Action<long, long?>? progress, CancellationToken ct)
    {
        if (!DownloadOrigins.IsAllowedDownload(url))
        {
            throw new InvalidOperationException(
                $"Refusing to download from {Sanitize.Url(url)}: that address is not one of the "
                + "publishers this tool downloads from. Nothing was fetched.");
        }

        var requested = new Uri(url);

        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Where it actually came from, once HttpClient followed the redirects. Nothing has been
        // read yet, so a refusal here costs the headers and nothing else.
        var landed = response.RequestMessage?.RequestUri ?? requested;
        if (!DownloadOrigins.IsAllowedLanding(requested, landed))
        {
            throw new InvalidOperationException(
                $"Refusing the download from {Sanitize.Url(url)}: it was redirected to "
                + $"{landed.Host}, which is not where this publisher serves its files. Nothing was fetched.");
        }

        var total = response.Content.Headers.ContentLength;
        if (total > maxBytes)
        {
            throw new InvalidOperationException(
                $"Refusing the download from {Sanitize.Url(url)}: the server announces "
                + $"{Human(total.Value)}, more than the {Human(maxBytes)} this could ever be.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            done += read;

            // The stated length is a promise the server made; more bytes than that is a server
            // that is not sending the file it described. And the ceiling holds whether or not a
            // length was stated at all.
            if (done > maxBytes || (total is { } stated && done > stated))
            {
                throw new InvalidOperationException(
                    $"The download from {Sanitize.Url(url)} kept going past its announced size. "
                    + "It was discarded.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            progress?.Invoke(done, total);
        }
    }

    /// <summary>A size a person can read, in the unit that fits it: "622 KB", "40 MB", "1.5 GB".</summary>
    private static string Human(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024L => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };
}
