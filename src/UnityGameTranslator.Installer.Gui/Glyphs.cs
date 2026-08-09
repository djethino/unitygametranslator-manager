using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace UnityGameTranslator.Installer.Gui;

/// <summary>
/// The small marks that sit on buttons, in one place.
///
/// Drawn as shapes rather than typed as characters. An emoji renders differently on every system
/// and is missing outright from some Linux font stacks — a button whose meaning depends on which
/// fonts happen to be installed is a button that means nothing on the machine that lacks them. An
/// icon font would be a dependency for a handful of glyphs.
///
/// Each one is built fresh on every call: a control belongs to one place in the tree, so sharing a
/// single instance between two buttons puts it in whichever was created last and leaves the other
/// empty.
/// </summary>
public static class Glyphs
{
    private const double Size = 15;

    /// <summary>A folder, for anything that opens one.</summary>
    public static Control Folder() => Shape(
        "M2,3 L6,3 L7.5,4.5 L13,4.5 A1,1 0 0,1 14,5.5 L14,12 A1,1 0 0,1 13,13 "
        + "L2,13 A1,1 0 0,1 1,12 L1,4 A1,1 0 0,1 2,3 Z");

    /// <summary>A clipboard, for copying.</summary>
    public static Control Clipboard() => Shape(
        // The board, then the clip on top, then the lines of text — three subpaths in one geometry
        // so the whole thing scales together.
        "M4,2 L6,2 A2,2 0 0,1 10,2 L12,2 A1,1 0 0,1 13,3 L13,14 A1,1 0 0,1 12,15 "
        + "L4,15 A1,1 0 0,1 3,14 L3,3 A1,1 0 0,1 4,2 Z "
        + "M6.5,1.5 A1.5,1.5 0 0,1 9.5,1.5 L9.5,3 L6.5,3 Z");

    /// <summary>
    /// The project's own mark, for anything that opens the website.
    ///
    /// Deliberately not a generic globe: the destination is not "the internet", it is this
    /// project's site, and the logo says which one before the label is read.
    /// </summary>
    public static Control Site(double size = 16)
    {
        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://UnityGameTranslatorInstaller/Assets/icon-128.png"));

            return new Image
            {
                Source = new Bitmap(stream),
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        catch
        {
            // A missing asset must not take a button with it; the label alone still works.
            return new Panel { Width = 0 };
        }
    }

    /// <summary>A button with a mark and a label, spaced as a pair rather than two things.</summary>
    public static Button Button(Control glyph, string label, double fontSize = 12)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(glyph);
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Button { Content = row };
    }

    /// <summary>Replaces a glyph button's label, keeping its mark.</summary>
    public static void SetLabel(Button button, string label)
    {
        if (button.Content is StackPanel row && row.Children.Count > 1
            && row.Children[1] is TextBlock text)
        {
            text.Text = label;
        }
    }

    private static Control Shape(string data) => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse(data),
        Fill = Application.Current?.FindResource("TextMuted") as IBrush,
        Width = Size,
        Height = Size,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
