using System.Text;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Whether a Linux process belongs to a game, read from /proc.
///
/// 🔴 **Why the executable is not enough.** A Windows game run through Proton or Wine is a process
/// whose executable (<c>/proc/PID/exe</c>, what .NET calls MainModule) is Wine's loader —
/// <c>…/Proton 11.0/files/lib/wine/x86_64-unix/wine64-preloader</c> — outside the game's folder,
/// and whose command line is a Windows path (<c>S:\steamapps\common\…\Game.exe</c>). Asking only
/// the executable, the Manager answered "not running" for every Proton game: the badge came only
/// from the launch watch and fell two minutes after Play, and the install guard would have written
/// into a game that was open (measured 2026-10-03).
///
/// ⚠ **What does answer: the files the process has mapped.** Wine maps the game's .exe and .dll at
/// their Unix path, so <c>/proc/PID/maps</c> holds the game's folder — and so does a native game's.
/// One question for both, with no Proton-specific reading of the prefix or the environment.
///
/// ⚠ The kernel keeps a process name (<c>comm</c>) of 15 bytes at most: "The_Haunted_Island.exe"
/// is seen as "The_Haunted_Isl". <see cref="CommOf"/> says what a file would be called there.
/// </summary>
public static class LinuxGameProcess
{
    /// <summary>TASK_COMM_LEN is 16, terminator included.</summary>
    public const int CommBytes = 15;

    /// <summary>The name the kernel keeps for a process started from this file name.</summary>
    public static string CommOf(string fileName)
    {
        if (Encoding.UTF8.GetByteCount(fileName) <= CommBytes) return fileName;

        // Cut by bytes, the way the kernel does, but never inside a character: .NET decodes a
        // half character as U+FFFD, which no file name holds.
        var length = 0;
        var bytes = 0;
        while (length < fileName.Length)
        {
            var step = char.IsSurrogatePair(fileName, length) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(fileName.Substring(length, step));
            if (bytes + size > CommBytes) break;
            bytes += size;
            length += step;
        }
        return fileName.Substring(0, length);
    }

    /// <summary>
    /// True when the process runs from inside <paramref name="gameRoot"/> or has a file from it
    /// mapped. <paramref name="gameRoot"/> is a real path ending with '/' (see <see cref="RootOf"/>):
    /// /proc reports every path with its links resolved.
    /// </summary>
    public static bool Holds(int pid, string gameRoot)
    {
        var proc = $"/proc/{pid}";

        try
        {
            var target = File.ResolveLinkTarget($"{proc}/exe", returnFinalTarget: true)?.FullName;
            if (target is not null && target.StartsWith(gameRoot, StringComparison.Ordinal)) return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another user's process, or one that ended between the listing and the question.
        }

        try
        {
            return MapsHold(File.ReadLines($"{proc}/maps"), gameRoot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The form <see cref="Holds"/> compares against: the folder's real path, with a final '/'.</summary>
    public static string RootOf(string gameFolder) => RealPath.Of(gameFolder).TrimEnd('/') + "/";

    /// <summary>
    /// Whether one of these /proc/PID/maps lines maps a file inside <paramref name="gameRoot"/>.
    ///
    /// A line is "address perms offset dev inode path"; the path is the only field that can hold a
    /// '/', and it may hold spaces ("…/common/Tap Ninja/baselib.dll"), so it is taken from the
    /// first '/' to the end. Anonymous and pseudo mappings ("[heap]") have none.
    /// </summary>
    public static bool MapsHold(IEnumerable<string> mapsLines, string gameRoot)
    {
        foreach (var line in mapsLines)
        {
            var slash = line.IndexOf('/');
            if (slash < 0) continue;
            if (string.CompareOrdinal(line, slash, gameRoot, 0, gameRoot.Length) == 0) return true;
        }
        return false;
    }

    /// <summary>The ids of every process /proc lists.</summary>
    public static IEnumerable<int> AllProcessIds()
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories("/proc");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (int.TryParse(Path.GetFileName(entry), out var pid)) yield return pid;
        }
    }
}
