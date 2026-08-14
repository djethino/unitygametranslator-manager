using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The edit-scope switch shrunk to its three marks, for sitting inside a button.
///
/// ⚠ **Three marks, not one.** The point of the small form is to be recognised as the big one —
/// the strip of three positions shown beside a title on the website and in the mod. A single icon
/// standing for the chosen side would be a new label to learn; the three, with one lit, are the
/// same control read at a glance.
///
/// ⚠ **Inside the button, before the label.** It qualifies the ACTION, and a mark floating above a
/// button only sits near one. Above is also how this went wrong the first time: little boxes left
/// aligned over buttons of very different widths read as a strip of selectors, which is exactly the
/// thing this control must not be mistaken for.
///
/// ⚠ Order comes from <see cref="EditScope.Sides"/> and the picture from <see cref="EditScope.Mark"/>
/// — never from a reading of our own. A control read left to right must not be mirrored between two
/// screens of the same product, let alone between three products.
/// </summary>
public static class ScopeMark
{
    /// <summary>Small enough to ride inside a 12pt button without becoming its subject.</summary>
    private const double MarkSize = 12;

    /// <summary>
    /// The three marks, with <paramref name="side"/> lit and the other two dimmed.
    /// </summary>
    public static Control For(EditSide side, bool enabled = true)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ⚠ Every side, always, in the switch's own order — including the ones this action does not
        // aim at. Drawing only the lit one is what turns a control into a decoration.
        foreach (var standing in EditScope.Sides(hasLocalFile: true, canReachMachine: true,
                                                 signedIn: true, publishedByThisAccount: true,
                                                 publishedBySomebodyElse: false))
        {
            var lit = standing.Side == side;

            // ⚠ **On a button nobody can press, the lit mark drops its colour.** The accent is
            // there to catch an eye, and catching one for a dead control is exactly wrong — a
            // disabled button was reading as the brightest thing in the row. It stays
            // recognisable as the lit one, by being LIGHTER than the other two rather than a
            // different hue: which side the action aims at is still worth knowing when you are
            // reading why you cannot take it.
            var colour = lit
                ? (enabled ? "AccentSoft" : "TextSecondary")
                : "TextMuted";

            row.Children.Add(Glyphs.Sized(Draw(standing.Side, colour), MarkSize));
        }

        ToolTip.SetTip(row, EditScope.Effect(side));
        return row;
    }

    /// <summary>
    /// A button that says where it writes before it says what it does.
    ///
    /// The separator is a hairline rather than a gap: the marks belong to the button, and a gap
    /// wide enough to read as a boundary would put them back outside it.
    /// </summary>
    public static Button Marked(EditSide side, string label, bool enabled = true)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(For(side, enabled));
        row.Children.Add(new Border
        {
            Width = 1,
            Background = Palette.Of("BorderSubtle"),
            Margin = new Avalonia.Thickness(0, 1),
        });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Button { Content = row, IsEnabled = enabled };
    }

    /// <summary>Replaces a marked button's label, keeping its marks.</summary>
    public static void SetLabel(Button button, string label)
    {
        if (button.Content is StackPanel row && row.Children.Count == 3
            && row.Children[2] is TextBlock text)
        {
            text.Text = label;
        }
    }

    /// <summary>
    /// The identity the socle gives a side, turned into this program's picture.
    ///
    /// ⚠ No default case on purpose-by-omission: an unknown mark would come back as an empty box,
    /// and a button would then be missing the one thing saying where it writes. The socle's checks
    /// pin the three names precisely so this cannot happen silently.
    /// </summary>
    private static Control Draw(EditSide side, string colour)
    {
        switch (EditScope.Mark(side))
        {
            case "cloud": return Glyphs.Cloud(colour);
            case "link": return Glyphs.Link(colour);
            case "display": return Glyphs.Display(colour);
            default: return new Panel { Width = 0 };
        }
    }
}
