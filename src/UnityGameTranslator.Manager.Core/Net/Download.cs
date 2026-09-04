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
///  · is the file the size it was said to be.
///
/// ⚠ **The size is the publisher's word, never a figure of ours.** GitHub's API states each
/// asset's size, and every caller that has read it hands it in; the server then states a length
/// with the answer. What is read may not exceed the first when it is known, nor the second
/// otherwise. There is no ceiling here on purpose: the files differ by two orders of magnitude
/// (a loader is a few megabytes, Ollama's installer 1.5 GB on 2026-09-04) and they grow, so a
/// number chosen today would be either meaningless or, one day, a refusal of a real file that
/// nobody would trace back to this line. A bound that follows the file cannot go stale.
/// </summary>
public static class Download
{
    /// <param name="declaredBytes">
    /// The size the publisher stated for this file (GitHub's `size`), when the caller has it.
    /// Null means "only the server's own length is known", which is what a Bleeding Edge href
    /// or a `.sha256` sidecar gives.
    /// </param>
    public static async Task ToFileAsync(HttpClient http, string url, string destination, long? declaredBytes,
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

        var announced = response.Content.Headers.ContentLength;

        // The publisher's figure first, the server's second. A server announcing more than the
        // publisher stated is not sending the file that was described.
        var limit = declaredBytes ?? announced;
        if (declaredBytes is { } declared && announced > declared)
        {
            throw new InvalidOperationException(
                $"Refusing the download from {Sanitize.Url(url)}: the publisher lists this file at "
                + $"{Human(declared)} and the server is sending {Human(announced.Value)}. Nothing was fetched.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            done += read;

            if (limit is { } most && done > most)
            {
                throw new InvalidOperationException(
                    $"The download from {Sanitize.Url(url)} kept going past the {Human(most)} it was "
                    + "said to be. It was discarded.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            progress?.Invoke(done, limit);
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
