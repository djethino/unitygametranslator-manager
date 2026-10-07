using System.Text.Json;

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

    /// <summary>The file or folder could not be written, moved or deleted.</summary>
    public static bool WriteFailed(Exception e) =>
        e is IOException or UnauthorizedAccessException;
}
