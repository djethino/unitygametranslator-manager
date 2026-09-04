namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>Which archive this is, and which version of it. One version kept per name.</summary>
/// <param name="Name">
/// The variant, not the product: a loader is one entry per OS and architecture, because a machine
/// that installs the 64-bit build never wants the 32-bit one.
/// </param>
public sealed record ArchiveCacheKey(string Name, string Version);

/// <summary>
/// Keeps the archives that were downloaded, so installing into a second game does not fetch the
/// same file again.
///
/// The case this exists for is ordinary rather than exotic: somebody with a dozen Unity games
/// installs the mod into all of them in one sitting, and every install used to pull MelonLoader
/// down again — the staging folder is named with a fresh GUID and deleted in a `finally`, so
/// nothing survived one install to the next.
///
/// 🔴 **One version per name, and the old one goes when a new one arrives.** A roulette of the last
/// N would double or triple the disk for a case that does not come up: going back to an earlier
/// loader is done through the version the catalog pins, not by hoping it is still in a cache. What
/// is bounded here is not "how much may we keep" but "how many things may we keep" — which is the
/// only bound that stays true as archives grow.
///
/// ⚠ **The hash is checked on the way OUT, not only on the way in.** A file that sat on a disk for
/// three months is not the file that was written there: a cache read without verification is a way
/// of installing something nobody ever downloaded. When it does not match, the entry is dropped and
/// the caller downloads again — the outcome is a slow install, never a wrong one.
/// </summary>
public sealed class ArchiveCache
{
    private const string EntryFileName = "entry.txt";

    private readonly string _root;

    public ArchiveCache(string root) => _root = root;

    /// <summary>
    /// The cached archive for this exact version, or null — the file being absent, stale, or no
    /// longer matching what it was stored as.
    /// </summary>
    /// <param name="expectedSha256">
    /// What the publisher says the file should be, when it says anything. A cached entry that
    /// disagrees is not the file we are being asked for: the publisher replaced it under the same
    /// version, which happens, and the answer is to fetch rather than to serve the old bytes.
    /// </param>
    public string? TryPath(ArchiveCacheKey key, string? expectedSha256, string extension)
    {
        try
        {
            var folder = FolderFor(key);
            var archive = Path.Combine(folder, "archive" + extension);
            var entry = Path.Combine(folder, EntryFileName);

            if (!File.Exists(archive) || !File.Exists(entry)) return null;

            var lines = File.ReadAllLines(entry);
            if (lines.Length < 2) return null;

            var version = lines[0];
            var storedSha = lines[1];

            if (!string.Equals(version, key.Version, StringComparison.OrdinalIgnoreCase)) return null;

            if (!string.IsNullOrWhiteSpace(expectedSha256)
                && !string.Equals(storedSha, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Drop(folder);
                return null;
            }

            // The file itself, every time. Cheap next to a download, and the only thing that makes
            // "we already have it" a statement about bytes rather than about a folder name.
            if (!string.Equals(FileOperations.HashFile(archive), storedSha,
                               StringComparison.OrdinalIgnoreCase))
            {
                Drop(folder);
                return null;
            }

            return archive;
        }
        catch
        {
            // A cache that cannot be read is a cache miss. Nothing here is worth failing an install.
            return null;
        }
    }

    /// <summary>Keeps this archive as the one for its name, replacing whatever was there.</summary>
    public void Store(ArchiveCacheKey key, string archivePath, string sha256, string extension)
    {
        try
        {
            var folder = FolderFor(key);

            // The whole variant, not just the file: an older version left beside a newer one is the
            // roulette this deliberately does not have.
            Drop(folder);
            Directory.CreateDirectory(folder);

            File.Copy(archivePath, Path.Combine(folder, "archive" + extension), overwrite: true);
            File.WriteAllLines(Path.Combine(folder, EntryFileName), new[] { key.Version, sha256 });
        }
        catch
        {
            // Storing is an optimisation. An install that worked must not fail over a copy.
        }
    }

    // ⚠ No Size() and no Clear() here yet, deliberately: nothing would call them. What bounds this
    // folder is the rule above — one version per variant — not a person remembering to empty it.
    // A way to see it and drop it belongs on a screen, and that screen does not exist.

    private string FolderFor(ArchiveCacheKey key) => Path.Combine(_root, SafeName(key.Name));

    private static void Drop(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // Left behind at worst, and the next Store overwrites the files it cares about.
        }
    }

    /// <summary>
    /// A folder name from an id we did not choose.
    ///
    /// ⚠ Path separators go too, not only the characters Windows rejects: a loader id containing a
    /// slash would otherwise make a subfolder, and one containing ".." would leave the cache.
    /// </summary>
    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();

        var cleaned = new string(name.Select(
            c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());

        return cleaned.Trim('.', ' ') is { Length: > 0 } safe ? safe : "unnamed";
    }
}
