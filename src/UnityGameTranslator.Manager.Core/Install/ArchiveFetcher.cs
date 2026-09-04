using System.Formats.Tar;
using System.IO.Compression;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Net;

namespace UnityGameTranslator.Manager.Core.Install;

public sealed record FetchedArchive(string ExtractedPath, string Sha256);

/// <summary>
/// Downloads an archive, checks it, and unpacks it into a staging folder.
///
/// Nothing ever lands in a game folder straight from the network. The archive is verified
/// against the checksum published in the catalog first, and an archive we cannot verify is
/// refused outright — this tool exists to place executable code into people's games, which is
/// exactly the position an attacker would want to be in.
/// </summary>
public sealed class ArchiveFetcher
{
    private readonly HttpClient _http;
    private readonly string _stagingRoot;
    private readonly ArchiveCache? _cache;

    public ArchiveFetcher(string stagingRoot, HttpClient? http = null, ArchiveCache? cache = null)
    {
        _stagingRoot = stagingRoot;
        _cache = cache;
        _http = http ?? Http.Create(TimeSpan.FromMinutes(10));

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"UnityGameTranslatorManager/{BuildInfo.Version}");
        }
    }

    /// <summary>Reports bytes downloaded so far and the total when the server states one.</summary>
    public event Action<long, long?>? Progress;

    /// <summary>
    /// Downloads, verifies when a checksum is available, and unpacks.
    ///
    /// <paramref name="expectedSha256"/> may be null: not every publisher offers one, and
    /// refusing to install in that case would block the tool on projects that simply do not
    /// publish hashes. A mismatch is always fatal; an absent checksum is reported, not fatal.
    /// The hash actually observed is returned either way and recorded in the receipt.
    /// </summary>
    public async Task<FetchedArchive> FetchAsync(string url, string? expectedSha256, string label,
                                                 ArchiveCacheKey? cacheKey = null,
                                                 CancellationToken ct = default)
    {
        Directory.CreateDirectory(_stagingRoot);

        var extension = ExtensionOf(url);
        var archivePath = Path.Combine(_stagingRoot, SafeFileName(label) + extension);

        // Already downloaded once, for this exact version, and still the bytes it was stored as —
        // ArchiveCache re-hashes before answering. A second game therefore costs no network at all.
        var cached = cacheKey is null ? null : _cache?.TryPath(cacheKey, expectedSha256, extension);
        var fromCache = cached is not null;

        string actual;

        if (fromCache)
        {
            archivePath = cached!;
            actual = FileOperations.HashFile(archivePath);

            // Said, so a progress bar does not sit at zero through an install that is simply not
            // downloading anything.
            var size = new FileInfo(archivePath).Length;
            Progress?.Invoke(size, size);
        }
        else
        {
            await DownloadAsync(url, archivePath, ct).ConfigureAwait(false);

            actual = FileOperations.HashFile(archivePath);

            if (!string.IsNullOrWhiteSpace(expectedSha256)
                && !string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(archivePath);
                throw new InvalidOperationException(
                    $"Checksum mismatch for {label}. Expected {expectedSha256}, got {actual}. " +
                    "The download was discarded.");
            }

            // ⚠ After the checksum, never before: a cache is only worth having if what goes into it
            // has already been held to the same standard as what goes into a game.
            if (cacheKey is not null) _cache?.Store(cacheKey, archivePath, actual, extension);
        }

        var extractPath = Path.Combine(_stagingRoot, SafeFileName(label));
        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, recursive: true);
        Directory.CreateDirectory(extractPath);

        ExtractSafely(archivePath, extractPath);

        // ⚠ Only the staging copy. Deleting the cached file would empty the cache on every use,
        // which is a cache that costs a copy and saves nothing.
        if (!fromCache) TryDelete(archivePath);

        return new FetchedArchive(extractPath, actual);
    }

    /// <summary>
    /// The most an archive fetched here may weigh. The largest real one is this tool's own update
    /// (40 MB, measured 2026-09-04); loaders are a few megabytes. Room for twenty-five of those, and
    /// still a bound rather than none.
    /// </summary>
    private const long MaxArchiveBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// The most an archive may hold once unpacked, and in how many entries. This tool's update
    /// unpacks to about a hundred megabytes and a dozen files; a loader to a few megabytes and a
    /// few dozen. A zip that claims gigabytes, or ten thousand entries, is not one of ours.
    /// </summary>
    private const long MaxUnpackedBytes = 2048L * 1024 * 1024;
    private const int MaxEntries = 10_000;

    private Task DownloadAsync(string url, string destination, CancellationToken ct) =>
        Download.ToFileAsync(_http, url, destination, MaxArchiveBytes,
                             (done, total) => Progress?.Invoke(done, total), ct);

    /// <summary>
    /// How two resolved paths are compared when deciding whether one is under the other.
    ///
    /// 🔴 The file system's rule, not one comparison for every system. Windows ignores case, so
    /// "…\Games\x" and "…\games\x" are one folder there and must compare equal; Linux does not, so
    /// on the published linux-x64 build the same comparison let "../Games/x" out of "/home/u/games/"
    /// — the two spellings are different folders, and only one of them was the root.
    /// </summary>
    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The archive format, read from the address rather than guessed from the bytes.
    ///
    /// Loaders ship zips; this tool's own Linux build ships a tar.gz, because a zip cannot carry
    /// the execute bit (see prepare-release.ps1). Both go through the same unpacking rules below.
    /// </summary>
    private static string ExtensionOf(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;

        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return ".tar.gz";
        if (path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return ".tar.gz";
        return ".zip";
    }

    /// <summary>
    /// Unpacks while refusing entries that would escape the destination. An archive is attacker
    /// controlled by definition, and "../../windows/system32" is the oldest trick there is.
    /// </summary>
    private static void ExtractSafely(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ExtractTarSafely(archivePath, root);
            return;
        }

        using var archive = ZipFile.OpenRead(archivePath);

        // Counted from the table of contents, before a single byte is written: a zip states each
        // entry's unpacked size up front, so an archive that would fill the disk is refused whole.
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidOperationException($"Archive holds {archive.Entries.Count} entries. Refusing to extract.");

        long unpacked = 0;
        foreach (var entry in archive.Entries)
        {
            unpacked += entry.Length;
            if (unpacked > MaxUnpackedBytes)
                throw new InvalidOperationException("Archive unpacks to more than this tool ever ships. Refusing to extract.");

            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));

            if (!target.StartsWith(root, PathComparison))
            {
                throw new InvalidOperationException(
                    $"Archive entry escapes its folder: '{entry.FullName}'. Refusing to extract.");
            }

            // A directory entry has an empty name.
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// Same rules as the zip path, plus one of its own: only plain files and folders come out.
    ///
    /// A tar can carry symbolic and hard links, and a link is an escape route that survives the
    /// path check — the entry itself sits inside the folder while pointing anywhere on the disk.
    /// Nothing we publish contains one, so anything else is refused rather than interpreted.
    ///
    /// The entry's mode is applied as it goes down (that is what ExtractToFile does on Unix), which
    /// is the whole reason this format exists here: the executable arrives executable.
    /// </summary>
    private static void ExtractTarSafely(string archivePath, string root)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        // A tar is read as a stream, so the same bounds are held as it goes rather than up front.
        var entries = 0;
        long unpacked = 0;

        while (reader.GetNextEntry() is { } entry)
        {
            if (++entries > MaxEntries)
                throw new InvalidOperationException($"Archive holds more than {MaxEntries} entries. Refusing to extract.");

            unpacked += Math.Max(0, entry.Length);
            if (unpacked > MaxUnpackedBytes)
                throw new InvalidOperationException("Archive unpacks to more than this tool ever ships. Refusing to extract.");

            var target = Path.GetFullPath(Path.Combine(root, entry.Name));

            if (!target.StartsWith(root, PathComparison))
            {
                throw new InvalidOperationException(
                    $"Archive entry escapes its folder: '{entry.Name}'. Refusing to extract.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Archive entry '{entry.Name}' is a {entry.EntryType}, not a file. " +
                        "Refusing to extract.");
            }
        }
    }

    /// <summary>
    /// The staging name for a label. Same rule as the cache's folder names, and for the same
    /// reason: a separator or a ".." in a label would name a place outside the staging folder.
    /// </summary>
    private static string SafeFileName(string label) => ArchiveCache.SafeName(label);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* staging cleanup is best effort */ }
    }
}
