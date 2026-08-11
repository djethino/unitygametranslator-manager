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

    public ArchiveFetcher(string stagingRoot, HttpClient? http = null)
    {
        _stagingRoot = stagingRoot;
        _http = http ?? Http.Create(TimeSpan.FromMinutes(10));

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"UnityGameTranslatorInstaller/{BuildInfo.Version}");
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
    public async Task<FetchedArchive> FetchAsync(string url, string? expectedSha256,
                                                 string label, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_stagingRoot);
        var archivePath = Path.Combine(_stagingRoot, SafeFileName(label) + ExtensionOf(url));

        await DownloadAsync(url, archivePath, ct).ConfigureAwait(false);

        var actual = FileOperations.HashFile(archivePath);

        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(archivePath);
            throw new InvalidOperationException(
                $"Checksum mismatch for {label}. Expected {expectedSha256}, got {actual}. " +
                "The download was discarded.");
        }

        var extractPath = Path.Combine(_stagingRoot, SafeFileName(label));
        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, recursive: true);
        Directory.CreateDirectory(extractPath);

        ExtractSafely(archivePath, extractPath);
        TryDelete(archivePath);

        return new FetchedArchive(extractPath, actual);
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
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));

            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
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

        while (reader.GetNextEntry() is { } entry)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.Name));

            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
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

    private static string SafeFileName(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(label.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* staging cleanup is best effort */ }
    }
}
