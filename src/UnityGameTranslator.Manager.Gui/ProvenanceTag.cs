using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The tag over a quality bar saying whose figures it counts.
///
/// ⚠ **This program draws the same bar over two different things**: the file on disk, on a game's
/// page, and a published translation, in the community list. Nothing on the bar says which, and the
/// two diverge the moment somebody plays. Without this, published figures read as a description of
/// your own work.
///
/// 🔴 **Deliberately unlike <see cref="ScopeMark"/>.** That one says where a save LANDS and is a
/// choice; this says where a count CAME FROM and is never chosen. Same shape and same words for two
/// different questions is how somebody publishes thinking they are counting — so: one flat tag
/// against three segments, no icons against three, muted against the accent, and nothing to click.
/// The wording is <see cref="Provenance"/>'s, which its checks hold apart from the switch's.
/// </summary>
public static class ProvenanceTag
{
    public static Control For(Origin origin)
    {
        var tag = new Border
        {
            Background = Palette.Of("SurfaceDeep"),
            BorderBrush = Palette.Of("BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(3),
            Padding = new Avalonia.Thickness(7, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
            Child = new TextBlock
            {
                Text = Provenance.Name(origin),
                FontSize = 10,
                Foreground = Palette.Of("TextMuted"),
            },
        };

        ToolTip.SetTip(tag, Provenance.Effect(origin));
        return tag;
    }
}
