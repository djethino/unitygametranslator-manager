using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace UnityGameTranslator.Manager.Gui;

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
///
/// ⚠ Every path is drawn in the SAME 16x16 box and rendered at that scale, never stretched to fit
/// its own outline. Stretching is what a single glyph wants and what a row of them cannot survive:
/// the folder is 13 wide by 10 tall and the cog is square, so scaling each to fill the control made
/// the cog a head taller than the folder beside it. The box is the common ground — a mark that
/// should read smaller is drawn smaller.
/// </summary>
public static class Glyphs
{
    /// <summary>The box every path is drawn in, and the size a mark occupies on screen.</summary>
    private const double Box = 16;

    /// <summary>A folder, for anything that opens one.</summary>
    public static Control Folder(string? colour = null) => Shape(colour, FolderOutline);

    /// <summary>A folder with a cut-out plus, for adding one to the list.</summary>
    public static Control FolderPlus(string? colour = null) => Shape(colour,
        FolderOutline
        + " M6.75,6.1 L8.25,6.1 L8.25,8 L10.15,8 L10.15,9.5 L8.25,9.5 L8.25,11.4 "
        + "L6.75,11.4 L6.75,9.5 L4.85,9.5 L4.85,8 L6.75,8 Z");

    /// <summary>A clipboard, for copying.</summary>
    public static Control Clipboard(string? colour = null) => Shape(colour,
        // The board, then the clip on top — two subpaths in one geometry so the whole thing
        // scales together.
        "M4,2 L6,2 A2,2 0 0,1 10,2 L12,2 A1,1 0 0,1 13,3 L13,14 A1,1 0 0,1 12,15 "
        + "L4,15 A1,1 0 0,1 3,14 L3,3 A1,1 0 0,1 4,2 Z "
        + "M6.5,1.5 A1.5,1.5 0 0,1 9.5,1.5 L9.5,3 L6.5,3 Z");

    /// <summary>A circular arrow, for looking again.</summary>
    public static Control Refresh(string? colour = null) => Shape(colour,
        // Ring open over the top-left quarter, its end drawn out into the arrow head rather than
        // overlaid with one: an overlapping triangle punches a hole under the even-odd rule
        // instead of adding to the ring.
        "M8,2.4 A5.6,5.6 0 1 1 2.4,8 L1.2,8 L3.4,4 L5.6,8 L4.4,8 A3.6,3.6 0 1 0 8,4.4 Z");

    /// <summary>Three sliders, for what gets written into the games.</summary>
    public static Control Sliders(string? colour = null) => Shape(colour,
        // Two layers rather than one geometry: a knob sitting on a rail is an overlap, and one
        // path filled even-odd would show the crossing as a hole. Drawn one over the other,
        // there is nothing to reconcile.
        "M1.6,3.3 L14.4,3.3 L14.4,4.8 L1.6,4.8 Z "
        + "M1.6,7.25 L14.4,7.25 L14.4,8.75 L1.6,8.75 Z "
        + "M1.6,11.2 L14.4,11.2 L14.4,12.7 L1.6,12.7 Z",
        "M8.7,4.05 A2.5,2.5 0 1 1 13.7,4.05 A2.5,2.5 0 1 1 8.7,4.05 Z "
        + "M2.7,8 A2.5,2.5 0 1 1 7.7,8 A2.5,2.5 0 1 1 2.7,8 Z "
        + "M6.9,11.95 A2.5,2.5 0 1 1 11.9,11.95 A2.5,2.5 0 1 1 6.9,11.95 Z");

    /// <summary>A cogwheel, for this program's own settings.</summary>
    public static Control Gear(string? colour = null) => Shape(colour,
        "M13,8.99 L14.81,9.13 L14.81,6.87 L13,7.01 L12.24,5.17 L13.61,3.99 L12.01,2.39 "
        + "L10.83,3.76 L8.99,3 L9.13,1.19 L6.87,1.19 L7.01,3 L5.17,3.76 L3.99,2.39 L2.39,3.99 "
        + "L3.76,5.17 L3,7.01 L1.19,6.87 L1.19,9.13 L3,8.99 L3.76,10.83 L2.39,12.01 L3.99,13.61 "
        + "L5.17,12.24 L7.01,13 L6.87,14.81 L9.13,14.81 L8.99,13 L10.83,12.24 L12.01,13.61 "
        + "L13.61,12.01 L12.24,10.83 Z "
        // The hub, punched out by the even-odd rule rather than painted in the background colour:
        // a mark that assumes what is behind it stops working the moment it sits on a card.
        + "M5.55,8 A2.45,2.45 0 1 1 10.45,8 A2.45,2.45 0 1 1 5.55,8 Z");

    /// <summary>An i in a circle, for what this program is.</summary>
    public static Control Info(string? colour = null) => Shape(colour,
        "M1.8,8 A6.2,6.2 0 1 1 14.2,8 A6.2,6.2 0 1 1 1.8,8 Z "
        + "M3.3,8 A4.7,4.7 0 1 1 12.7,8 A4.7,4.7 0 1 1 3.3,8 Z "
        // Both inside the ring's hole, so even-odd turns them back into solid marks. Nothing
        // overlaps anything.
        + "M6.95,5.35 A1.05,1.05 0 1 1 9.05,5.35 A1.05,1.05 0 1 1 6.95,5.35 Z "
        + "M7.05,7.2 L8.95,7.2 L8.95,11.7 L7.05,11.7 Z");

    /// <summary>A house, for the way back to where everything is summed up.</summary>
    public static Control Home(string? colour = null) => Shape(colour,
        "M8,1.9 L14.4,8.1 L12.7,8.1 L12.7,13.9 L9.4,13.9 L9.4,9.9 L6.6,9.9 "
        + "L6.6,13.9 L3.3,13.9 L3.3,8.1 L1.6,8.1 Z");

    /// <summary>
    /// A triangle, for starting the game itself.
    ///
    /// Drawn slightly right of centre in its box: a triangle's visual weight sits at its base, so
    /// one centred by its bounding box reads as leaning left. The same correction every play
    /// button in the world carries.
    /// </summary>
    public static Control Play(string? colour = null) => Shape(colour,
        "M4.6,2.6 L13,8 L4.6,13.4 Z");

    /// <summary>A bin, for taking something back out of a list.</summary>
    public static Control Trash(string? colour = null) => Shape(colour,
        "M3,3.6 L6,3.6 L6,2.2 L10,2.2 L10,3.6 L13,3.6 L13,5.2 L3,5.2 Z "
        + "M4.3,6.3 L11.7,6.3 L11,14.2 L5,14.2 Z");

    /// <summary>Shared by the two folder marks, so the second cannot drift from the first.</summary>
    private const string FolderOutline =
        "M2,3 L6,3 L7.5,4.5 L13,4.5 A1,1 0 0,1 14,5.5 L14,12 A1,1 0 0,1 13,13 "
        + "L2,13 A1,1 0 0,1 1,12 L1,4 A1,1 0 0,1 2,3 Z";

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
                new Uri("avares://UnityGameTranslatorManager/Assets/icon-128.png"));

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
    public static Button Button(Control glyph, string label, double? fontSize = 12) =>
        new() { Content = Row(glyph, label, fontSize) };

    /// <summary>
    /// Puts a mark on a button that already exists — the ones declared in XAML, which carry their
    /// label as plain text.
    ///
    /// Here rather than in the XAML because the paths are here: a second copy of a glyph in markup
    /// is a copy free to drift from this one, and the drift would show up as two versions of the
    /// same mark on the same screen.
    ///
    /// The font size is left alone by default, so a button keeps whatever the theme gives it.
    /// </summary>
    public static void Adorn(Button button, Control glyph, double? fontSize = null)
    {
        if (button.Content is not string label) return;   // already adorned, or not a plain label
        button.Content = Row(glyph, label, fontSize);
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

    private static StackPanel Row(Control glyph, string label, double? fontSize)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        if (fontSize is { } size) text.FontSize = size;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { glyph, text },
        };
    }

    /// <summary>
    /// One mark, from one or more geometries stacked in the order given.
    ///
    /// Layers exist for the cases where two parts of a mark overlap: a single geometry is filled
    /// even-odd, so a knob crossing a rail comes out as a hole. Anything that does not overlap
    /// stays one geometry, where the even-odd rule is what punches the cog's hub and the ring of
    /// the i.
    /// </summary>
    private static Control Shape(string? colour, params string[] layers)
    {
        var brush = Palette.Of(colour ?? "TextMuted");

        if (layers.Length == 1) return Draw(layers[0], brush);

        var stack = new Panel
        {
            Width = Box,
            Height = Box,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var layer in layers) stack.Children.Add(Draw(layer, brush));
        return stack;
    }

    private static Avalonia.Controls.Shapes.Path Draw(string data, IBrush? brush) => new()
    {
        Data = Geometry.Parse(data),
        Fill = brush,
        Width = Box,
        Height = Box,

        // ⚠ Not Uniform. The path is already drawn in the 16x16 box every other glyph uses, and
        // stretching it to its own outline is exactly what breaks the common scale.
        Stretch = Stretch.None,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
