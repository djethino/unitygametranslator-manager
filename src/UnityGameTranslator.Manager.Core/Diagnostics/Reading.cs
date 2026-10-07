using System.Text.Json;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Core.Diagnostics;

/// <summary>
/// What a catch around READING a file recognises — and only that.
///
/// 🔴 **Written once** (2026-10-07): the Manager read dozens of files — a game's config, a backup's
/// description, a cache, a store's manifest — each inside a bare <c>catch</c> that took a bug in
/// this program for a damaged file and said nothing either way. A filter names the cases a file can
/// really be in; anything else is this code failing, and goes on to the caller where it is seen.
/// </summary>
public static class Reading
{
    /// <summary>
    /// The file could not be read (gone, locked, not ours), or what it holds is not what was
    /// expected: not JSON (<see cref="JsonException"/>), a value of another type
    /// (<see cref="InvalidOperationException"/> from JsonNode.GetValue, <see cref="FormatException"/>
    /// from a parse), or a path the system refuses (<see cref="ArgumentException"/>,
    /// <see cref="NotSupportedException"/>).
    /// </summary>
    public static bool Failed(Exception e) =>
        e is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException or ArgumentException or NotSupportedException;

    /// <summary>
    /// A request that did not get an answer it could use: the network (<see cref="HttpRequestException"/>,
    /// a stream cut mid-read: <see cref="IOException"/>), no answer in time (a
    /// <see cref="TaskCanceledException"/> the caller did NOT ask for), or an answer of another shape
    /// (<see cref="JsonException"/>, <see cref="InvalidOperationException"/> from a JsonElement of
    /// another kind, <see cref="KeyNotFoundException"/>, <see cref="FormatException"/>).
    ///
    /// ⚠ A cancellation the caller asked for is NOT one of them: it goes on, to the code that asked.
    /// </summary>
    public static bool RequestFailed(Exception e, CancellationToken asked) =>
        e is HttpRequestException or IOException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException
        || (e is TaskCanceledException && !asked.IsCancellationRequested);

    /// <summary>The file or folder could not be written, moved or deleted.</summary>
    public static bool WriteFailed(Exception e) =>
        e is IOException or UnauthorizedAccessException;

    /// <summary>
    /// Every file below <paramref name="root"/>, folder by folder, lazily; a folder that may not be
    /// read is said (under <paramref name="place"/>) and skipped, and its neighbours are still read.
    ///
    /// ⚠ Not <c>Directory.EnumerateFiles(…, AllDirectories)</c> inside a try: that enumeration is
    /// lazy, so an unreadable subfolder throws from the CALLER's loop, past the try meant to catch
    /// it — the same defect common's FontFileNames had (2026-10-07). Nor
    /// <c>EnumerationOptions.IgnoreInaccessible</c>, which skips them without a word.
    /// </summary>
    public static IEnumerable<string> FilesUnder(string root, string place)
    {
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            string[] files, children;
            try
            {
                files = Directory.GetFiles(folder);
                children = Directory.GetDirectories(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Faults.Say(place, e, $"{Sanitize.Path(folder)} skipped");
                continue;
            }
            foreach (var file in files) yield return file;
            for (var i = children.Length - 1; i >= 0; i--) folders.Push(children[i]);
        }
    }

    /// <summary>What became of a file of this program's own that could not be read.</summary>
    public enum OwnFile
    {
        /// <summary>It went between "is it there" and the read: nothing to keep, start afresh.</summary>
        Gone,
        /// <summary>Its content was damaged: moved aside under a dated name, start afresh.</summary>
        SetAside,
        /// <summary>
        /// It could not be opened (locked by another program, refused): left where it is, and the
        /// caller must NOT write over it for the rest of this run — it is most likely intact.
        /// </summary>
        LeftInPlace,
    }

    /// <summary>One of this program's files the window has to tell about.</summary>
    /// <param name="Name">The file's name, as it is in the data folder.</param>
    /// <param name="SetAsideAs">The name it was moved to; null when it was left in place.</param>
    public sealed record OwnFileTrouble(string Name, string? SetAsideAs);

    private static readonly object TroublesGate = new();
    private static readonly List<OwnFileTrouble> Troubles = new();

    /// <summary>Every one of this program's files that could not be read during this run.</summary>
    public static IReadOnlyList<OwnFileTrouble> OwnFileTroubles
    {
        get { lock (TroublesGate) return Troubles.ToArray(); }
    }

    /// <summary>Whether this file was left in place earlier in this run.</summary>
    public static bool IsLeftInPlace(string path)
    {
        var full = Path.GetFullPath(path);
        lock (TroublesGate) return LeftAlone.Contains(full);
    }

    private static readonly HashSet<string> LeftAlone = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised when <see cref="OwnFileTroubles"/> gains a file — from whichever thread read it. Some
    /// stores are read on first use, after the window has drawn its notices.
    /// </summary>
    public static event Action? OwnFileTroubled;

    /// <summary>
    /// A file of this program's own that could not be read: said in the journal, told to the window
    /// (<see cref="OwnFileTroubles"/>), and either moved aside or left alone — see <see cref="OwnFile"/>.
    ///
    /// 🔴 **Why aside, and not just ignored** (2026-10-07). Each store read a damaged file as empty —
    /// and its next save wrote over it. The folders somebody added, the settings and the keys they
    /// typed, the answers given per game: gone for good, over what may have been one bad line. The
    /// copy set aside is what can still be repaired by hand, or attached to a report.
    ///
    /// ⚠ **And why a locked file is NOT set aside.** A file another program holds for a moment (an
    /// antivirus, a sync client) is intact: moving it aside would make the person start from nothing
    /// for a file that was fine. It stays, and the store keeps its hands off it until the next launch.
    ///
    /// ⚠ The name is dated, never reused: a second damaged file must not overwrite the first copy.
    /// </summary>
    public static OwnFile Unreadable(string path, Exception cause, string place)
    {
        var name = Path.GetFileName(path);

        if (cause is FileNotFoundException or DirectoryNotFoundException)
        {
            Journal.Note(place, $"{name} went while it was being read");
            return OwnFile.Gone;
        }

        if (cause is not (JsonException or InvalidOperationException or FormatException))
        {
            Faults.Say(place, cause, $"{Sanitize.Path(path)} could not be opened; left in place, not written until the next launch");
            Tell(path, new OwnFileTrouble(name, null));
            return OwnFile.LeftInPlace;
        }

        var aside = $"{path}.{DateTime.Now:yyyy-MM-dd-HHmmss-fff}.unreadable";
        try
        {
            File.Move(path, aside, overwrite: false);
        }
        catch (Exception ex) when (WriteFailed(ex))
        {
            // Not moved: then not overwritten either — left alone like a locked file.
            Faults.Say(place, ex, $"{Sanitize.Path(path)} is damaged and could not be set aside; left in place, not written until the next launch");
            Tell(path, new OwnFileTrouble(name, null));
            return OwnFile.LeftInPlace;
        }

        Faults.Say(place, cause, $"{Sanitize.Path(path)} is damaged; set aside as {Path.GetFileName(aside)} and started afresh");
        Tell(path, new OwnFileTrouble(name, Path.GetFileName(aside)));
        return OwnFile.SetAside;
    }

    private static void Tell(string path, OwnFileTrouble trouble)
    {
        lock (TroublesGate)
        {
            if (trouble.SetAsideAs is null) LeftAlone.Add(Path.GetFullPath(path));

            // A file read again and again (the install ledger is read at every use) is one notice.
            if (Troubles.Contains(trouble)) return;
            Troubles.Add(trouble);
        }
        OwnFileTroubled?.Invoke();
    }
}
