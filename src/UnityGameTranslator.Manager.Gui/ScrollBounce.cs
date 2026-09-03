using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The give at the end of a scroll: push past the last line and the content leans a few pixels,
/// then settles back.
///
/// It answers a question a scrollbar answers badly and a long list asks constantly — *is there more
/// below, or is that everything?* A view that simply stops dead is indistinguishable from one that
/// has frozen, and people scroll again to find out. The lean says "that was the end" in the same
/// gesture, without a word and without a control.
///
/// ⚠ **The CONTENT leans, never the scroller.** Moving the scroller would move the panel it sits
/// in, and everything laid out beside it. This is a render transform: it displaces pixels and
/// nothing else — no reflow, no relayout, nothing that can shift a button under a pointer.
///
/// ⚠ **Silent when everything fits.** With nothing to scroll there is no end to reach, and a view
/// that bounced anyway would be answering a question nobody could have asked.
/// </summary>
public static class ScrollBounce
{
    /// <summary>
    /// Set by a style on every ScrollViewer in the application — see App.axaml.
    ///
    /// 🔴 **A property rather than a call, because calling it is something a window can forget.**
    /// It was attached by hand in the main window, and the eight other windows that scroll had
    /// nothing: the give was a property of one screen instead of a property of scrolling. A style
    /// reaches every scroller there is and every one added later, including the ones built inside
    /// a ListBox's or a ComboBox's own template, which no call site can reach at all.
    /// </summary>
    public static readonly AttachedProperty<bool> GiveProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("Give", typeof(ScrollBounce));

    public static void SetGive(ScrollViewer scroll, bool value) => scroll.SetValue(GiveProperty, value);

    public static bool GetGive(ScrollViewer scroll) => scroll.GetValue(GiveProperty);

    static ScrollBounce() =>
        GiveProperty.Changed.AddClassHandler<ScrollViewer>((scroll, e) =>
        {
            if (e.NewValue is true) Attach(scroll);
        });

    /// <summary>How far the content leans. Eight pixels is felt; more is watched.</summary>
    private const double Give = 8;

    /// <summary>Out, then back — the second longer than the first, which is what makes it settle
    /// rather than snap.</summary>
    private static readonly TimeSpan Held = TimeSpan.FromMilliseconds(90);

    /// <summary>
    /// The scrollers already carrying this, so nothing is given two.
    ///
    /// ⚠ A window can leave the visual tree and come back, and the hook below fires each time. Two
    /// handlers on one scroller would lean twice as far on every notch — which reads as a jolt, not
    /// as a give. Conditional so a scroller that is genuinely gone can be collected.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, object>
        Carrying = new();

    public static void Attach(ScrollViewer? scroll)
    {
        if (scroll is null || Carrying.TryGetValue(scroll, out _)) return;

        Carrying.Add(scroll, new object());

        // handledEventsToo: by the time a wheel notch reaches here the scroller has usually acted
        // on it already, and "it was handled" is not the same as "there was somewhere to go".
        scroll.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => Consider(scroll, e),
                          RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Leans a scroller that has been pushed past its end by something other than the wheel event
    /// it listens for.
    ///
    /// 🔴 **A dropdown needs this, and cannot use the handler above.** Inside a Popup on Windows the
    /// wheel never reaches the scroller at all (Avalonia#16646, see SearchPicker), so the list is
    /// scrolled by hand from the top level — and a give that waits for an event which never arrives
    /// is a give that never plays. The one place that knows the list did not move is the one doing
    /// the moving, so it says so.
    /// </summary>
    public static void Nudge(ScrollViewer scroll, bool upward)
    {
        if (scroll.Content is Control content) Lean(content, upward ? Give : -Give);
    }

    private static void Consider(ScrollViewer scroll, PointerWheelEventArgs e)
    {
        if (scroll.Content is not Control content) return;

        var reach = scroll.Extent.Height - scroll.Viewport.Height;
        if (reach <= 0.5) return;

        var atTop = scroll.Offset.Y <= 0.5;
        var atBottom = scroll.Offset.Y >= reach - 0.5;

        if (e.Delta.Y > 0 && atTop) Lean(content, Give);
        else if (e.Delta.Y < 0 && atBottom) Lean(content, -Give);
    }

    private static void Lean(Control content, double by)
    {
        // ⚠ TransformOperations, never a TranslateTransform: TransformOperationsTransition can only
        // interpolate the former, and handed the latter it does nothing at all — no error, no
        // warning, just a bounce that never plays.
        content.Transitions ??= new Transitions
        {
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = TimeSpan.FromMilliseconds(160),
                Easing = new CubicEaseOut(),
            },
        };

        content.RenderTransform = TransformOperations.Parse(
            $"translateY({by.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");

        // ⚠ InvariantCulture above, and it is not pedantry: this parses a CSS-like string, so on a
        // machine whose decimal separator is a comma a fractional value would stop being a number.

        DispatcherTimer.RunOnce(
            () => content.RenderTransform = TransformOperations.Parse("none"), Held);
    }
}
