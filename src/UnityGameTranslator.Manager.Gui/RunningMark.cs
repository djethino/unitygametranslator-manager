using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Styling;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// A game that is running, marked where its Play button stands: a green dot, pulsing gently
/// (user's choice, 2026-09-25).
///
/// ⚠ **In the Play button's place and colour, on purpose.** The button disappears while its game is
/// open — a second press would start a second copy — and a hole where it stood said nothing. The
/// same green, beating, reads as "this is the one playing". The conventional "live" mark; not
/// equaliser bars, which read as audio, and not a stop square, which would promise an act this
/// tool does not offer.
///
/// ⚠ Pulsing only while on screen, like <see cref="SpinningGear"/>: an infinite animation outlives
/// its control and keeps the window redrawing for a mark nobody can see.
/// </summary>
public sealed class RunningMark : Panel
{
    public RunningMark()
    {
        Width = 26;
        Height = 22;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;

        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = Ui.Brush("StatusSuccess"),
        };
        Children.Add(dot);

        ToolTip.SetTip(this, "Running. Close the game to set it up or remove it.");

        // Slow and eased: a heartbeat, not an alarm — nothing is wrong with a game being played.
        var pulse = new Animation
        {
            Duration = TimeSpan.FromSeconds(1.6),
            IterationCount = IterationCount.Infinite,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(0.5d), Setters = { new Setter(OpacityProperty, 0.3d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 1d) } },
            },
        };

        CancellationTokenSource? beating = null;

        AttachedToVisualTree += (_, _) =>
        {
            beating?.Cancel();
            beating = new CancellationTokenSource();
            _ = pulse.RunAsync(dot, beating.Token);
        };

        DetachedFromVisualTree += (_, _) =>
        {
            beating?.Cancel();
            beating = null;
        };
    }
}
