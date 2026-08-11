using System.Security.Cryptography;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// Puts files into a game folder in a way that can be undone exactly.
///
/// Every write is recorded with its hash and whether something was already there. That record —
/// not a hardcoded list of what a loader is supposed to contain — is what uninstall works from.
/// Loaders change their layout; what we actually wrote does not.
/// </summary>
public sealed class FileOperations
{
    /// <summary>Backups of files we overwrote, kept until the user uninstalls.</summary>
    public const string BackupDirectory = ".ugt-manager-backup";

    private readonly string _gameRoot;

    public FileOperations(string gameRoot) => _gameRoot = Path.GetFullPath(gameRoot);

    public List<ReceiptFile> WrittenFiles { get; } = new();
    public List<string> CreatedDirectories { get; } = new();

    /// <summary>
    /// Copies one file into the game, backing up anything it replaces.
    /// <paramref name="relativeTarget"/> uses forward slashes and is relative to the game root.
    /// </summary>
    public void PlaceFile(string sourcePath, string relativeTarget)
    {
        var target = ResolveInsideGame(relativeTarget);

        EnsureDirectory(Path.GetDirectoryName(target)!);

        string? backupRelative = null;
        var preExisting = File.Exists(target);

        if (preExisting)
        {
            // Overwriting someone's file without a copy is not something we can undo, so we
            // never do it. The backup outlives the install and is removed only on uninstall.
            backupRelative = $"{BackupDirectory}/{relativeTarget}";
            var backupPath = ResolveInsideGame(backupRelative);
            EnsureDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(target, backupPath, overwrite: true);
        }

        File.Copy(sourcePath, target, overwrite: true);

        WrittenFiles.Add(new ReceiptFile
        {
            Path = relativeTarget,
            Sha256 = HashFile(target),
            PreExisting = preExisting,
            Backup = backupRelative,
        });
    }

    /// <summary>Creates a directory, remembering it only if it did not exist before.</summary>
    public void EnsureDirectory(string absolutePath)
    {
        if (Directory.Exists(absolutePath)) return;

        // Record every level we bring into existence, so uninstall can peel them back off in
        // reverse — and only while they are empty.
        var missing = new List<string>();
        var current = absolutePath;
        while (current is not null && !Directory.Exists(current))
        {
            missing.Add(current);
            current = Path.GetDirectoryName(current);
        }

        Directory.CreateDirectory(absolutePath);

        missing.Reverse();
        foreach (var dir in missing)
        {
            var relative = Path.GetRelativePath(_gameRoot, dir).Replace('\\', '/');
            if (!relative.StartsWith("..", StringComparison.Ordinal)) CreatedDirectories.Add(relative);
        }
    }

    /// <summary>
    /// Turns a relative path into an absolute one and refuses anything that escapes the game
    /// folder. The catalog is remote data: a path traversal in it must not be able to write
    /// outside the game the user chose.
    /// </summary>
    public string ResolveInsideGame(string relativePath)
    {
        var combined = Path.GetFullPath(
            Path.Combine(_gameRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var root = _gameRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _gameRoot
            : _gameRoot + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to write outside the game folder: '{relativePath}'");
        }

        return combined;
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>Undoes everything recorded so far. Used when an install fails halfway.</summary>
    public void Rollback()
    {
        foreach (var file in Enumerable.Reverse(WrittenFiles))
        {
            try
            {
                var target = ResolveInsideGame(file.Path);

                if (file.PreExisting && file.Backup is not null)
                {
                    var backup = ResolveInsideGame(file.Backup);
                    if (File.Exists(backup)) File.Copy(backup, target, overwrite: true);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch
            {
                // A rollback that cannot finish must still try the remaining files: stopping at
                // the first failure would leave more behind, not less.
            }
        }

        foreach (var relative in Enumerable.Reverse(CreatedDirectories))
        {
            TryRemoveEmptyDirectory(ResolveInsideGame(relative));
        }

        WrittenFiles.Clear();
        CreatedDirectories.Clear();
    }

    /// <summary>
    /// Removes a directory only when it is empty. Never recursive: a folder that still holds
    /// something holds someone else's files.
    /// </summary>
    public static bool TryRemoveEmptyDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            if (Directory.EnumerateFileSystemEntries(path).Any()) return false;
            Directory.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
