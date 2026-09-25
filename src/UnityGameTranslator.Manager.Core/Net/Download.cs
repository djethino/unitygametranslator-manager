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
        await using var source = await OpenAsync(http, url, declaredBytes, progress, ct).ConfigureAwait(false);
        await using var target = File.Create(destination);

        await source.CopyToAsync(target, 81920, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The same download, as a stream the caller reads as far as it needs — held to every question
    /// above before the first byte is handed over, and to the size bound while it is read.
    ///
    /// ⚠ For a file whose useful part is at its start: Unity's engine modules sit in the first
    /// megabytes of a 550 MB package, and reading on would fetch half a gigabyte for nothing.
    /// Disposing the stream ends the download.
    /// </summary>
    public static async Task<Stream> OpenAsync(HttpClient http, string url, long? declaredBytes,
                                               Action<long, long?>? progress, CancellationToken ct)
    {
        if (!DownloadOrigins.IsAllowedDownload(url))
        {
            throw new InvalidOperationException(
                $"Refusing to download from {Sanitize.Url(url)}: UGT Manager does not download "
                + "from that address. Nothing was fetched.");
        }

        var requested = new Uri(url);

        var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        try
        {
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

            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new Bounded(response, body, limit, url, progress);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A response body that refuses to run past the size it was said to be, reports progress, and
    /// ends the download when it is disposed.
    /// </summary>
    private sealed class Bounded : Stream
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _body;
        private readonly long? _limit;
        private readonly string _url;
        private readonly Action<long, long?>? _progress;
        private long _done;

        public Bounded(HttpResponseMessage response, Stream body, long? limit, string url, Action<long, long?>? progress)
        {
            _response = response;
            _body = body;
            _limit = limit;
            _url = url;
            _progress = progress;
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(_body.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await _body.ReadAsync(buffer, ct).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Count(int read)
        {
            _done += read;

            if (_limit is { } most && _done > most)
            {
                throw new InvalidOperationException(
                    $"The download from {Sanitize.Url(_url)} kept going past the {Human(most)} it was "
                    + "said to be. It was discarded.");
            }

            if (read > 0) _progress?.Invoke(_done, _limit);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _body.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _done; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
