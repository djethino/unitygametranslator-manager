using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace UnityGameTranslator.Installer.Gui;

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

    public SpinningGear(string message = "Working...")
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(0, 6, 0, 6);

        var image = new Image
        {
            Width = 20,
            Height = 20,
            Source = Load(),

            // Rotation happens about the middle of the control, so the gear turns on its own axis
            // rather than orbiting a corner.
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new RotateTransform(0),
            Opacity = 0.85,
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
            Foreground = this.FindResource("TextSecondary") as IBrush,
        };

        Children.Add(image);
        Children.Add(_label);
    }

    /// <summary>What we are waiting for, updated as the work moves on.</summary>
    public string Message
    {
        get => _label.Text ?? "";
        set => _label.Text = value;
    }

    private static Bitmap? Load()
    {
        try
        {
            return new Bitmap(AssetLoader.Open(
                new Uri("avares://UnityGameTranslatorInstaller/Assets/gear.png")));
        }
        catch
        {
            // A missing asset must not take a window down. The label alone still answers the
            // question the gear is here for.
            return null;
        }
    }
}
