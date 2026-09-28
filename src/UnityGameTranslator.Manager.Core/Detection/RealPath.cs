namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// A folder's one true path, every symbolic link on the way resolved — what <c>realpath</c> says.
///
/// 🔴 **Why detection needs it.** On Linux, Steam is reachable through several doors that are the
/// same room: <c>~/.steam/steam</c> and <c>~/.steam/root</c> are links to
/// <c>~/.local/share/Steam</c>. <see cref="Path.GetFullPath"/> only tidies the text, so each
/// door was a different library, and every game was listed once per door — four times on Bazzite
/// (2026-09-28). The same holds for a Windows junction, which .NET also reports as a link.
///
/// ⚠ A path that does not exist is returned tidied but otherwise as given: there is nothing on
/// disk to follow.
/// </summary>
public static class RealPath
{
    /// <summary>Deep enough for any real chain; a cycle stops here instead of looping.</summary>
    private const int MaxLinks = 40;

    public static string Of(string path)
    {
        var budget = MaxLinks;
        return Resolve(Path.GetFullPath(path), ref budget);
    }

    private static string Resolve(string full, ref int budget)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return full;

        var current = root;
        var parts = full.Substring(root.Length)
                        .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                               StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var next = Path.Combine(current, part);

            string? target;
            try
            {
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                target = info.Exists ? info.LinkTarget : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                target = null;
            }

            if (target is not null && budget-- > 0)
            {
                // A relative target is relative to the folder holding the link, and the target can
                // itself pass through links — hence resolved again, from the root.
                var joined = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target));
                next = Resolve(joined, ref budget);
            }

            current = next;
        }

        return current;
    }
}
