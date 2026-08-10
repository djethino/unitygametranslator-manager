using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Installer.Core.Model;

namespace UnityGameTranslator.Installer.Gui;

/// <summary>
/// What a translation is made of, drawn as the mod and the website draw it.
///
/// ⚠ Third implementation of one measure, so the rules are copied deliberately and in full rather
/// than approximated. A file that looked 80% reviewed here and 60% on the website would make the
/// measure worthless everywhere — the point of a shared denominator is that it is shared.
///
/// The rules, taken from QualityBar in the mod and quality-bar.blade.php on the site:
///
/// 1. **Five shares, in this order**: human, validated, AI, kept-as-is, capture. Kept-as-is owns a
///    band of its own because it is neither translated nor missing; capture is what the mod has
///    seen but nobody has worked on yet.
/// 2. **Proportions, never pixels.** The denominator is the sum of the five, so the bar stays
///    truthful at any width.
/// 3. **Nothing captured at all means no bar**, not an empty one: a full-width grey track reads as
///    "measured, and empty", when the truth is "not measured".
/// </summary>
public sealed class QualityBar : Border
{
    private const double BarHeight = 6;

    public QualityBar(OnlineTranslation translation)
    {
        Height = BarHeight;
        CornerRadius = new CornerRadius(BarHeight / 2);
        ClipToBounds = true;
        Background = Brush("QualityTrack");

        var total = translation.HumanCount + translation.ValidatedCount + translation.AiCount
                  + translation.SkippedCount + translation.CaptureCount;

        // Hidden rather than drawn empty. The caller checks HasSomethingToShow to drop the row
        // entirely; this guard is here so the control cannot lie on its own either.
        if (total <= 0)
        {
            IsVisible = false;
            return;
        }

        var grid = new Grid();

        void Add(int count, string colour)
        {
            if (count <= 0) return;

            // Star sizing IS the proportion: the grid divides the width by these weights, so
            // resizing the window cannot make the shares drift.
            grid.ColumnDefinitions.Add(new ColumnDefinition(count, GridUnitType.Star));
            var block = new Border { Background = Brush(colour) };
            Grid.SetColumn(block, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(block);
        }

        Add(translation.HumanCount, "QualityHuman");
        Add(translation.ValidatedCount, "QualityValidated");
        Add(translation.AiCount, "QualityAi");
        Add(translation.SkippedCount, "QualityKept");
        Add(translation.CaptureCount, "QualityCapture");

        Child = grid;
    }

    /// <summary>Whether there is anything to draw. Callers use it to drop the whole row.</summary>
    public static bool HasSomethingToShow(OnlineTranslation translation) =>
        translation.HumanCount + translation.ValidatedCount + translation.AiCount
        + translation.SkippedCount + translation.CaptureCount > 0;

    /// <summary>
    /// The colour key, each share as a whole percent, rounding absorbed by the last entry so the
    /// figures always read 100 — the mod's rule, for the same reason: a key that adds up to 99
    /// invites the reader to look for the missing one.
    /// </summary>
    public static Control? Legend(OnlineTranslation translation)
    {
        var counts = new (int Count, string Colour, string Label)[]
        {
            (translation.HumanCount, "QualityHuman", "human"),
            (translation.ValidatedCount, "QualityValidated", "reviewed"),
            (translation.AiCount, "QualityAi", "AI"),
            (translation.SkippedCount, "QualityKept", "kept as is"),
            (translation.CaptureCount, "QualityCapture", "not done yet"),
        };

        var total = counts.Sum(entry => entry.Count);
        if (total <= 0) return null;

        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        var running = 0;
        var shown = counts.Where(entry => entry.Count > 0).ToList();

        for (var i = 0; i < shown.Count; i++)
        {
            var (count, colour, label) = shown[i];

            // The last entry takes what is left rather than its own rounding, which is what keeps
            // the total at exactly 100.
            var percent = i == shown.Count - 1
                ? 100 - running
                : (int)Math.Round(count * 100.0 / total);
            running += percent;

            var entry = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 0, 12, 0),
            };

            entry.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = Brush(colour),
                VerticalAlignment = VerticalAlignment.Center,
            });

            entry.Children.Add(new TextBlock
            {
                Text = $"{percent}% {label}",
                FontSize = 11,
                Foreground = Brush("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            panel.Children.Add(entry);
        }

        return panel;
    }

    /// <summary>Through Palette, which will not let an unknown key pass unnoticed.</summary>
    private static IBrush? Brush(string key) => Palette.Of(key);
}
