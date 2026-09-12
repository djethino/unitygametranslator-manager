using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// Carries the wheel into an open dropdown, because nothing else will.
///
/// 🔴 **An Avalonia bug, not a choice.** AvaloniaUI/Avalonia#16646 — open, seen from 11.1.2 to
/// 11.3.10, Windows only: a wheel turned over a Popup never reaches what is inside it, the event
/// stops at the LightDismissOverlayLayer. So **no dropdown in this program scrolls with the wheel**,
/// an ordinary Fluent ComboBox included. A handler on PART_Popup is never called — that tree is
/// precisely where the event does not arrive.
///
/// 🔴 **Why this is a file of its own rather than a method in one control.** It was written inside
/// SearchPicker, for the three lists long enough to need searching, and every other dropdown was
/// left with a wheel that does nothing — the same problem, in the places nobody thought to look.
/// Something that answers "how does this program scroll" belongs to the program.
///
/// ⚠ **There is nothing here for Avalonia's own ComboBox, and that is the point.** Repairing its
/// popup was written and thrown away the same day: it would have been a second kind of dropdown
/// living beside ours, with its own behaviour to keep in step. Every list in this program is a
/// <see cref="SearchPicker"/>, so this only ever has one shape of popup to carry the wheel into.
///
/// ⚠ **Hooked on the TOP LEVEL, in Tunnel | Bubble, handledEventsToo.** The overlay has already
/// marked the event handled by the time it passes, and the popup's own tree never sees it at all.
///
/// ⚠ **Taken off on the popup's own close, never on the paths that close it.** A light dismiss — a
/// click anywhere else — closes a dropdown without passing through any of them, and a handler left
/// behind eats the wheel for the whole window from then on. And removed before being added: a click
/// on a face while its list is open closes and reopens in one gesture, and two copies scroll twice
/// as far per notch for the rest of the session.
/// </summary>
public static class PopupWheel
{
    /// <summary>Who is currently listening, so a handler is never added twice or left behind.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Listener>
        Listening = new();

    private sealed class Listener
    {
        public EventHandler<PointerWheelEventArgs>? Hooked;
        public TopLevel? On;
    }

    /// <summary>
    /// Carries the wheel into <paramref name="scroller"/> for as long as <paramref name="isOpen"/>
    /// says the list is up. Call <see cref="Drop"/> when it closes.
    ///
    /// ⚠ The scroller is asked for at every notch rather than kept: a dropdown builds its list when
    /// it opens, so what to scroll does not exist yet at the moment somebody asks for this.
    /// </summary>
    public static void Follow(Control owner, Func<bool> isOpen, Func<ScrollViewer?> scroller)
    {
        Drop(owner);

        if (TopLevel.GetTopLevel(owner) is not { } top) return;

        var listener = new Listener { On = top };
        listener.Hooked = (_, e) =>
        {
            if (!isOpen()) return;
            if (scroller() is not { } scroll) return;

            Turn(scroll, e);
        };

        top.AddHandler(InputElement.PointerWheelChangedEvent, listener.Hooked,
                       RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        Listening.Remove(owner);
        Listening.Add(owner, listener);
    }

    /// <summary>Stops listening. Safe to call when nothing is listening.</summary>
    public static void Drop(Control owner)
    {
        if (!Listening.TryGetValue(owner, out var listener)) return;

        if (listener.On is { } top && listener.Hooked is { } hooked)
            top.RemoveHandler(InputElement.PointerWheelChangedEvent, hooked);

        Listening.Remove(owner);
    }

    /// <summary>
    /// Moves one open list by one wheel event, and leans its end when there is nowhere left to go.
    ///
    /// ⚠ Three rows per notch, the figure Windows itself reports for a wheel detent — and the row
    /// height is read from the list rather than assumed, so a notch travels the same number of
    /// ENTRIES in a list of flags as in a list of plain words. Scrolling by a fixed pixel count
    /// moves a different distance in every list.
    /// </summary>
    private static void Turn(ScrollViewer scroll, PointerWheelEventArgs e)
    {
        var reach = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);

        // A list that fits has no end to reach and nothing to scroll — but the wheel is still spoken
        // for, or it would scroll the panel behind the open list.
        if (reach <= 0)
        {
            e.Handled = true;
            return;
        }

        var before = scroll.Offset.Y;
        var moved = Math.Clamp(before - e.Delta.Y * 3 * RowOf(scroll), 0, reach);

        scroll.Offset = new Vector(scroll.Offset.X, moved);

        // Nothing moved means the end was already reached. The give is played from here because it
        // cannot be played from where it listens — see the note at the top of this file.
        if (Math.Abs(moved - before) < 0.5) ScrollBounce.Nudge(scroll, e.Delta.Y);

        e.Handled = true;
    }

    /// <summary>About one row, measured rather than assumed.</summary>
    private static double RowOf(ScrollViewer scroll)
    {
        var panel = (scroll.Content as Visual ?? scroll)
            .GetVisualDescendants()
            .OfType<ItemsPresenter>()
            .FirstOrDefault()?.Panel;

        var first = panel?.Children.FirstOrDefault()?.Bounds.Height ?? 0;

        // ⚠ Not a fallback hiding a fault: before the first row has been measured there is genuinely
        // no answer, and a list that has not been laid out cannot be scrolled either. The figure is
        // what one row of plain text comes to in this theme.
        return first > 0 ? first : 28;
    }

}
