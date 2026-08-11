using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Gui;

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

    /// <summary>A published translation, as the server describes it.</summary>
    public QualityBar(OnlineTranslation translation) : this(TagCounts.From(translation)) { }

    /// <summary>
    /// Any five counts — which is what lets the file sitting in a game be drawn by the same bar
    /// as the one published on the site. They are the same measurement; only where they were
    /// counted differs.
    /// </summary>
    public QualityBar(TagCounts counts)
    {
        Height = BarHeight;
        CornerRadius = new CornerRadius(BarHeight / 2);
        ClipToBounds = true;
        Background = Brush("QualityTrack");

        var total = counts.Settled + counts.Captured;

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

        Add(counts.Human, "QualityHuman");
        Add(counts.Validated, "QualityValidated");
        Add(counts.Ai, "QualityAi");
        Add(counts.Skipped, "QualityKept");
        Add(counts.Captured, "QualityCapture");

        Child = grid;
    }

    /// <summary>Whether there is anything to draw. Callers use it to drop the whole row.</summary>
    public static bool HasSomethingToShow(OnlineTranslation translation) =>
        HasSomethingToShow(TagCounts.From(translation));

    public static bool HasSomethingToShow(TagCounts counts) => !counts.IsEmpty;

    /// <summary>
    /// The colour key, each share as a whole percent, rounding absorbed by the last entry so the
    /// figures always read 100 — the mod's rule, for the same reason: a key that adds up to 99
    /// invites the reader to look for the missing one.
    /// </summary>
    public static Control? Legend(OnlineTranslation translation) => Legend(TagCounts.From(translation));

    public static Control? Legend(TagCounts tags)
    {
        var counts = new (int Count, string Colour, string Label)[]
        {
            (tags.Human, "QualityHuman", "human"),
            (tags.Validated, "QualityValidated", "reviewed"),
            (tags.Ai, "QualityAi", "AI"),
            (tags.Skipped, "QualityKept", "kept as is"),
            (tags.Captured, "QualityCapture", "not done yet"),
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

    /// <summary>
    /// Where the reading stands, in the mod's own words — copied from its TranslationQuality so
    /// the same file is described identically in the game and here.
    ///
    /// ⚠ A step, never a mark. Every translation starts as machine output because that is how the
    /// mod works; calling that a poor grade tells a newcomer their starting point is worthless.
    /// Null when it is too early to say anything, and silence is then the honest answer.
    /// </summary>
    public static string? StageOf(TagCounts counts) => counts.Stage switch
    {
        ReviewStage.Reviewed => "Fully reviewed",
        ReviewStage.Advanced => "Review well under way",
        ReviewStage.Started => "Review started",
        ReviewStage.Machine => "Machine translation",
        _ => null,
    };

    /// <summary>Through Palette, which will not let an unknown key pass unnoticed.</summary>
    private static IBrush? Brush(string key) => Palette.Of(key);
}
