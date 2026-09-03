using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The movements this window makes, defined once so they mean the same thing everywhere.
///
/// ⚠ **One motion, one meaning.** <see cref="Arrive"/> says *what you are looking at has been
/// replaced* — a page swapped by a tab, a list refiltered. It is deliberately the same eight pixels
/// and the same fade in both places: a vocabulary is learned once and then read without thinking,
/// while two treatments of one event are two things to learn and a reason to wonder whether they
/// differ.
///
/// ⚠ It is for a change SOMEBODY ASKED FOR. Content that reappears on its own — rows sharpening as
/// the site answers, a version resolving in the background — must not move: nobody caused it, so
/// nothing should draw the eye to it.
/// </summary>
public static class Motion
{
    /// <summary>How far replaced content rises into place. Enough to give the change a direction.</summary>
    private const double Rise = 8;

    /// <summary>
    /// Plays "this has been replaced" on a control whose contents have just been rebuilt.
    ///
    /// ⚠ Set to the "from" values here and returned to rest on the next frame, which is what makes
    /// the transitions run at all: a value assigned and read back within one pass never changed as
    /// far as the animator is concerned.
    ///
    /// ⚠ Posted at Render, not Loaded: Loaded waits for a layout pass that a panel full of cards
    /// does not finish for tens of milliseconds, and the content would sit invisible until it did.
    /// </summary>
    public static void Arrive(Control content)
    {
        // ⚠ TransformOperations, never a TranslateTransform: TransformOperationsTransition can only
        // interpolate the former, and handed the latter it does nothing — no error, no warning,
        // just content that appears instead of arriving.
        content.Transitions ??= new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(140),
                Easing = new CubicEaseOut(),
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(160),
                Easing = new CubicEaseOut(),
            },
        };

        content.Opacity = 0;
        content.RenderTransform = TransformOperations.Parse($"translateY({Rise}px)");

        Dispatcher.UIThread.Post(() =>
        {
            content.Opacity = 1;
            content.RenderTransform = TransformOperations.Parse("none");
        }, DispatcherPriority.Render);
    }
}
