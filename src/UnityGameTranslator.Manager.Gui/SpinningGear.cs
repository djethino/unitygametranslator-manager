using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The ASymptOmatik gear, turning, with a line saying what is being waited for.
///
/// It exists because of a real misreading: running the instruction suite against a large model,
/// each answer took long enough that the screen looked finished after the first one. Nothing said
/// more was coming, so the honest conclusion from what was on screen was "it is done" — while the
/// tool was still working through seven more.
///
/// Two rules it follows:
///
/// 1. **It sits where the next block will appear.** A spinner in a corner says "something,
///    somewhere, is busy". A spinner in the empty space below the last result says "the next one
///    lands here", which is the actual question being asked.
///
/// 2. **It never blocks anything.** Results already on screen stay readable and scrollable while
///    it turns. Nothing is disabled that does not need to be.
///
/// A gear rather than the plain bar Avalonia ships: it is our own mark, and an engineering part
/// turning is the one animation that needs no explanation.
/// </summary>
public sealed class SpinningGear : StackPanel
{
    private readonly TextBlock _label;

    /// <summary>
    /// The step being worked through right now, between the gear and the caption.
    ///
    /// ⚠ Deliberately a SECOND line rather than a livelier caption. The caption says what the wait
    /// is for and does not move; this says where the work has got to and changes under it. Folding
    /// the two into one string would make the steady sentence flicker, which is the thing that
    /// makes a screen feel unstable.
    ///
    /// Hidden until something is put in it, so every caller that does not use it keeps the shape
    /// it had.
    /// </summary>
    private readonly TextBlock _detail;

    /// <param name="size">
    /// How big the gear is drawn. The default suits a line of waiting inside a list of results;
    /// a panel with nothing else in it wants far more, or the mark reads as a stray icon rather
    /// than as the thing the screen is currently doing.
    /// </param>
    /// <param name="stacked">
    /// Caption under the gear instead of beside it.
    ///
    /// ⚠ It follows from the size and is not a taste: a large gear with a small caption on its
    /// right is a lopsided pair with a lot of air under it. Centred one above the other, the two
    /// read as one block — which is what an otherwise empty panel needs at its middle.
    /// </param>
    public SpinningGear(string message = "Working...", double size = 30, bool stacked = false)
    {
        Orientation = stacked ? Orientation.Vertical : Orientation.Horizontal;
        Spacing = stacked ? 14 : 8;
        VerticalAlignment = VerticalAlignment.Center;

        // Centred across the panel rather than left-aligned like the results above it. Sharing
        // their left edge made it read as one more entry in the list; on its own axis it reads as
        // the state of the list, which is what it is.
        HorizontalAlignment = HorizontalAlignment.Center;

        Margin = new Thickness(0, 6, 0, 6);

        var image = new Image
        {
            // Big enough to read as movement at a glance. At 20 it was a speck: the thing has to
            // catch the eye of someone who has stopped looking at the screen, which is exactly
            // the moment it exists for.
            Width = size,
            Height = size,
            Source = Load(),

            // Rotation happens about the middle of the control, so the gear turns on its own axis
            // rather than orbiting a corner.
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new RotateTransform(0),
            Opacity = 0.85,

            // ⚠ Stacked, a child of a vertical StackPanel is stretched to the full width by
            // default — the gear would still be drawn at its size, but anchored left of the
            // caption's centre. Said explicitly rather than relied upon.
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // One full turn, linear, forever. Linear on purpose: an eased rotation reads as hesitation,
        // which is the opposite of what this is here to say.
        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(2.4),
            IterationCount = IterationCount.Infinite,
            Easing = new LinearEasing(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(RotateTransform.AngleProperty, 0d) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(RotateTransform.AngleProperty, 360d) },
                },
            },
        };

        animation.RunAsync(image);

        _label = new TextBlock
        {
            Text = message,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,

            // Asked of the application, not of this control. A control being built is not yet in
            // the visual tree, so looking the resource up through itself returns null — and a
            // TextBlock with no brush is invisible. That is what made this look off-centre: the
            // gear and an unreadable label were centred together, so the gear sat left of middle
            // by half a caption nobody could see.
            Foreground = Application.Current?.FindResource("TextSecondary") as IBrush,

            // ⚠ Collapsed rather than empty when there is nothing to say — the same off-centre trap
            // the comment above describes, in its quiet form: an empty label still costs the row's
            // Spacing, so the gear would sit four pixels left of the middle for ever.
            IsVisible = !string.IsNullOrEmpty(message),

            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _detail = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current?.FindResource("TextMuted") as IBrush,
            IsVisible = false,
        };

        Children.Add(image);
        Children.Add(_detail);
        Children.Add(_label);
    }

    /// <summary>Where the work has got to, shown between the gear and the caption. Empty hides it.</summary>
    public string Detail
    {
        get => _detail.Text ?? "";
        set
        {
            _detail.Text = value;
            _detail.IsVisible = !string.IsNullOrEmpty(value);
        }
    }

    /// <summary>What we are waiting for, updated as the work moves on.</summary>
    public string Message
    {
        get => _label.Text ?? "";
        set
        {
            _label.Text = value;
            _label.IsVisible = !string.IsNullOrEmpty(value);
        }
    }

    private static Bitmap? Load()
    {
        try
        {
            return new Bitmap(AssetLoader.Open(
                new Uri("avares://UnityGameTranslatorManager/Assets/gear.png")));
        }
        catch
        {
            // A missing asset must not take a window down. The label alone still answers the
            // question the gear is here for.
            return null;
        }
    }
}
