using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// A language, shown as its flag and — when the flag cannot name it alone — its tag beside it.
///
/// 🔴 **The same control as the mod's and the site's**, and the rule behind it is decided once in
/// <see cref="Flags.Mark"/>: ten Indian languages share one flag because no Indian state has one of
/// its own, and bokmål and nynorsk are two written standards of one country. Those show their tag;
/// the ones a flag identifies on its own do not, because a chip beside every flag would be noise.
///
/// ⚠ **The flags are drawn by us, as pixels** — a national flag is an official symbol, not a
/// copyrighted work, and what the usual icon sets license is their artwork. See the socle.
/// </summary>
public static class LanguageMark
{
    /// <summary>Height of the flag on screen. Its width follows the catalogue's grid.</summary>
    private const int FlagHeight = 11;

    /// <summary>Built once per flag: a bitmap is immutable here and a control is not shareable.</summary>
    private static readonly Dictionary<string, Bitmap?> Drawn = new();

    /// <summary>
    /// The mark for one language, or null when nothing can name it — an unknown language, which
    /// the caller then writes out in words rather than decorating.
    /// </summary>
    public static Control? For(string? languageName)
    {
        var mark = Flags.Mark(languageName ?? "");
        if (mark.Flag is null && string.IsNullOrEmpty(mark.Tag)) return null;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (Bitmap(mark.Flag) is { } bitmap)
        {
            var image = new Image
            {
                Source = bitmap,
                Height = FlagHeight,
                Width = FlagHeight * (double)Flags.Width / Flags.Height,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // ⚠ Sixteen pixels wide, shown at about eighteen. Smoothing turns a drawn flag into a
            // smudge, and half of them are told apart by a single edge.
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);

            row.Children.Add(image);
        }

        if (mark.ShowTag && !string.IsNullOrEmpty(mark.Tag))
        {
            row.Children.Add(new TextBlock
            {
                Text = mark.Tag,
                FontSize = 10,
                Foreground = Palette.Of("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return row;
    }

    /// <summary>
    /// One flag as a bitmap, or null when it has not been drawn yet.
    ///
    /// ⚠ Cached including the MISSES: a language with no flag would otherwise rebuild nothing, over
    /// and over, once per row on every refresh of a list of ninety.
    /// </summary>
    private static Bitmap? Bitmap(string? flagId)
    {
        if (string.IsNullOrEmpty(flagId)) return null;

        if (Drawn.TryGetValue(flagId, out var cached)) return cached;

        var pixels = Flags.Pixels(flagId);
        if (pixels is null)
        {
            Drawn[flagId] = null;
            return null;
        }

        var bitmap = new WriteableBitmap(new PixelSize(Flags.Width, Flags.Height),
                                         new Vector(96, 96),
                                         PixelFormat.Bgra8888,
                                         AlphaFormat.Unpremul);

        using (var buffer = bitmap.Lock())
        {
            // ⚠ Built row by row through the buffer's OWN stride, never width * 4. A row is padded
            // to the platform's alignment, and assuming it is not skews every line after the first
            // — which reads as a flag drawn on a diagonal.
            //
            // ⚠ Copied through Marshal rather than a pointer: turning `unsafe` on for the whole
            // assembly to fill one bitmap is a wide permission for a narrow need.
            var line = new byte[buffer.RowBytes];

            for (var y = 0; y < Flags.Height; y++)
            {
                Array.Clear(line, 0, line.Length);

                for (var x = 0; x < Flags.Width; x++)
                {
                    var pixel = pixels[y * Flags.Width + x];
                    if (pixel.Transparent) continue;

                    // BGRA, in that order. RGBA here silently swaps every flag's red and blue,
                    // which turns France into a flag nobody flies.
                    var at = x * 4;
                    line[at] = (byte)(pixel.Rgb & 0xFF);
                    line[at + 1] = (byte)((pixel.Rgb >> 8) & 0xFF);
                    line[at + 2] = (byte)((pixel.Rgb >> 16) & 0xFF);
                    line[at + 3] = 255;
                }

                System.Runtime.InteropServices.Marshal.Copy(
                    line, 0, buffer.Address + y * buffer.RowBytes, line.Length);
            }
        }

        Drawn[flagId] = bitmap;
        return bitmap;
    }
}
