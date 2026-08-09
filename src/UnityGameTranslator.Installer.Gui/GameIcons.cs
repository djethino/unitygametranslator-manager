using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using UnityGameTranslator.Installer.Core.Detection;

namespace UnityGameTranslator.Installer.Gui;

/// <summary>
/// The icon a game already carries in its own executable, turned into something Avalonia can draw.
///
/// Nothing is downloaded and nothing is guessed. The reading is done by parsing the file
/// (ExecutableIconReader), not by asking the operating system, and that choice is what makes it
/// work beyond Windows: **most games played on Linux are Windows games running under Proton or
/// Wine**, and their .exe travels with its icon. The same holds for Wine on macOS. Only a native
/// Linux build has genuinely nothing to read — an ELF holds no icon at all.
///
/// An earlier version called System.Drawing here. It worked, in twenty fewer lines, and only on
/// Windows; it also pulled in a platform-restricted package. One path for every system is worth
/// the parser.
///
/// The Steam image cache was considered and rejected: files named by SHA1 with no way to tell an
/// icon from a banner, most entries empty, and Steam only.
/// </summary>
public static class GameIcons
{
    /// <summary>
    /// Cached by path, because a list redraws on every scroll, filter and selection while the file
    /// on disk does not change. Reading an icon means reading a whole executable; doing it per
    /// repaint would make scrolling cost more than the scan did.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's icon, or null when there is none to be had. Never throws.</summary>
    public static Bitmap? For(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;

        // GetOrAdd rather than a lookup then an insert: a failure is cached as null too, so a game
        // whose icon cannot be read is not reopened on every redraw.
        return Cache.GetOrAdd(executablePath, Build);
    }

    private static Bitmap? Build(string path)
    {
        try
        {
            var icon = ExecutableIconReader.Read(path);
            if (icon is null) return null;

            if (icon.IsPng)
            {
                using var stream = new MemoryStream(icon.Data);
                return new Bitmap(stream);
            }

            // Rows arrive top-down and premultiplied by the format's own convention, which is what
            // Avalonia expects — so the bytes go straight in with no per-pixel work.
            var bitmap = new WriteableBitmap(
                new PixelSize(icon.Width, icon.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            using (var buffer = bitmap.Lock())
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    icon.Data, 0, buffer.Address, icon.Data.Length);
            }

            return bitmap;
        }
        catch
        {
            // A packed executable, a file being written, a format we do not decode — none of it is
            // worth a message. A game without an icon simply shows none.
            return null;
        }
    }
}
