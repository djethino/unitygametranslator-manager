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

    /// <summary>
    /// A file of this program's own that could not be read: said, and moved aside as
    /// <c>&lt;name&gt;.unreadable</c> before the caller starts afresh.
    ///
    /// 🔴 **Why aside, and not just ignored** (2026-10-07). Each store read a damaged file as empty —
    /// and its next save wrote over it. The folders somebody added, the settings and the keys they
    /// typed, the answers given per game: gone for good, over what may have been one bad line. The
    /// copy set aside is what can still be repaired by hand, or attached to a report.
    /// </summary>
    public static void SetAside(string path, Exception cause, string place)
    {
        var aside = path + ".unreadable";
        Faults.Say(place, cause, $"{Sanitize.Path(path)} could not be read; set aside as {Path.GetFileName(aside)} and started afresh");
        try
        {
            File.Move(path, aside, overwrite: true);
        }
        catch (Exception ex) when (WriteFailed(ex))
        {
            // Left in place, the next save overwrites it — which is what this exists to prevent,
            // so it is said in its own words.
            Faults.Say(place + " set aside", ex, $"{Sanitize.Path(path)} stays in place and will be overwritten at the next save");
        }
    }
}
