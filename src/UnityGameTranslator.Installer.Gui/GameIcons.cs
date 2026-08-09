using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Avalonia.Media.Imaging;

namespace UnityGameTranslator.Installer.Gui;

/// <summary>
/// The icon a game already carries in its own executable.
///
/// Nothing is downloaded and nothing is guessed: every Windows game ships its icon inside its .exe,
/// whatever store it came from. The Steam image cache was considered and rejected — its files are
/// named by SHA1 with no way to tell an icon from a banner, most entries are empty, and it would
/// only ever cover Steam.
///
/// ⚠ Windows only, on purpose and without apology. System.Drawing throws everywhere else on .NET,
/// so the call sits behind a platform guard: Linux gets no icon and a list that reads exactly as it
/// did before. Extracting icons from ELF binaries is not a thing, and Proton games keep a .exe we
/// could read the day it matters.
///
/// Same approach as MultiDisplayMaster, which does this for process icons. Only the last step
/// differs: that project is WPF and hands the handle to CreateBitmapSourceFromHIcon, which Avalonia
/// has no equivalent for — so the icon goes through a PNG in memory instead.
/// </summary>
public static class GameIcons
{
    /// <summary>
    /// Cached by path, because a list redraws on every scroll, filter and selection while the file
    /// on disk does not change. Extraction opens and parses an executable; doing it per repaint
    /// would make scrolling cost more than scanning.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's icon, or null when there is none to be had. Never throws.</summary>
    public static Bitmap? For(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        if (!OperatingSystem.IsWindows()) return null;

        // GetOrAdd rather than TryGetValue then Add: a failed extraction is cached as null too, so
        // a game whose icon cannot be read is not reopened on every redraw.
        return Cache.GetOrAdd(executablePath, Extract);
    }

    [SupportedOSPlatform("windows")]
    private static Bitmap? Extract(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();

            // PNG rather than the .ico bytes: Avalonia decodes PNG everywhere, and an icon file is
            // a container of several sizes that it would have to be taught to choose from.
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;

            return new Bitmap(stream);
        }
        catch
        {
            // A packed executable, a file being written, a permission — none of it is worth a
            // message. A game without an icon simply shows none.
            return null;
        }
    }
}
