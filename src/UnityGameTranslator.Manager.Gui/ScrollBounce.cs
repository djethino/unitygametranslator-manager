using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;
using UnityGameTranslator.Manager.Core.Interaction;

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
/// 🔴 **The wheel holds the edge; the spring only takes it back once the wheel stops.** Where the
/// edge sits lives in <see cref="EdgeGive"/> — pure, in Core, and held by its own cases. This file
/// is the part that cannot be checked without a window: which scroller, which pixels, which frame.
///
/// ⚠ **It used to be one animation per notch**, each leaning out and coming back on a timer of its
/// own, so turning the wheel steadily at the end of a list bounced once per detent — reported as
/// "it bounces and bounces and bounces until the wheel stops". The offsets were all individually
/// correct; what was wrong was that there were several of them at once. A give is one edge being
/// held, not a queue of rebounds.
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

    /// <summary>
    /// Everything one scroller needs to keep an edge open between two frames.
    ///
    /// ⚠ Held per scroller rather than in one place: two lists can be at their end at once — a
    /// dropdown over a panel — and a single edge would have them share a position.
    /// </summary>
    private sealed class Edge
    {
        public readonly EdgeGive Give = new();
        public bool Running;
        public long Last;
    }

    /// <summary>
    /// The scrollers already carrying this, and where each one's edge currently sits.
    ///
    /// ⚠ A window can leave the visual tree and come back, and the hook below fires each time. Two
    /// handlers on one scroller would lean twice as far on every notch — which reads as a jolt, not
    /// as a give. Conditional so a scroller that is genuinely gone can be collected.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, Edge>
        Edges = new();

    public static void Attach(ScrollViewer? scroll)
    {
        if (scroll is null || Edges.TryGetValue(scroll, out _)) return;

        Edges.Add(scroll, new Edge());

        // handledEventsToo: by the time a wheel notch reaches here the scroller has usually acted
        // on it already, and "it was handled" is not the same as "there was somewhere to go".
        scroll.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => Consider(scroll, e),
                          RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// Leans a scroller that has been pushed past its end by something other than the wheel event
    /// it listens for. <paramref name="notches"/> is the wheel's own delta, signed as it reports it.
    ///
    /// 🔴 **A dropdown needs this, and cannot use the handler above.** Inside a Popup on Windows the
    /// wheel never reaches the scroller at all (Avalonia#16646, see PopupWheel), so the list is
    /// scrolled by hand from the top level — and a give that waits for an event which never arrives
    /// is a give that never plays. The one place that knows the list did not move is the one doing
    /// the moving, so it says so.
    ///
    /// ⚠ The AMOUNT is passed on rather than a direction. The edge firms up under the hand, and it
    /// can only do that if it knows how hard it was pushed — a trackpad reports far more per event
    /// than a detent does.
    /// </summary>
    public static void Nudge(ScrollViewer scroll, double notches)
    {
        Attach(scroll);
        if (!Edges.TryGetValue(scroll, out var edge)) return;

        edge.Give.Push(notches);
        Pump(scroll, edge);
    }

    /// <summary>Hands an edge back at once — the list closed, or the scroller is going away.</summary>
    public static void Settle(ScrollViewer scroll)
    {
        if (!Edges.TryGetValue(scroll, out var edge)) return;

        edge.Give.Release();
        Draw(scroll, edge);
    }

    private static void Consider(ScrollViewer scroll, PointerWheelEventArgs e)
    {
        if (!Edges.TryGetValue(scroll, out var edge)) return;

        var reach = scroll.Extent.Height - scroll.Viewport.Height;
        if (reach <= 0.5) return;

        var atTop = scroll.Offset.Y <= 0.5;
        var atBottom = scroll.Offset.Y >= reach - 0.5;

        // Still inside the list: nothing to do, and nothing to undo either — if an edge is open the
        // spring is already pulling it home.
        if (!(e.Delta.Y > 0 && atTop) && !(e.Delta.Y < 0 && atBottom)) return;

        edge.Give.Push(e.Delta.Y);
        Pump(scroll, edge);
    }

    /// <summary>
    /// Starts the frame loop if it is not already running.
    ///
    /// ⚠ One loop per scroller, and it stops itself the moment the edge has arrived. A callback that
    /// kept being requested would keep a window rendering for as long as it is open, which on this
    /// framework is measured in percent of a core — see the note on composition in pieges-projet.md.
    /// </summary>
    private static void Pump(ScrollViewer scroll, Edge edge)
    {
        if (edge.Running) return;
        if (TopLevel.GetTopLevel(scroll) is not { } top) return;

        edge.Running = true;
        edge.Last = Stopwatch.GetTimestamp();
        Step(top, scroll, edge);
    }

    private static void Step(TopLevel top, ScrollViewer scroll, Edge edge) =>
        top.RequestAnimationFrame(_ =>
        {
            // ⚠ Timed here rather than from what the frame hands us: what that TimeSpan counts from
            // is the framework's business and has changed before, while a stopwatch reads the same
            // on every platform. EdgeGive clamps a long step anyway, so a window that was not
            // drawing cannot hand the spring something it fails to solve.
            var now = Stopwatch.GetTimestamp();
            var dt = (now - edge.Last) / (double)Stopwatch.Frequency;
            edge.Last = now;

            // The scroller left while its edge was open — a popup dismissed, a panel replaced.
            // Nothing to draw on and nothing to draw into.
            if (scroll.GetVisualRoot() is null)
            {
                edge.Give.Release();
                edge.Running = false;
                return;
            }

            var more = edge.Give.Advance(dt);
            Draw(scroll, edge);

            if (more) Step(top, scroll, edge);
            else edge.Running = false;
        });

    private static void Draw(ScrollViewer scroll, Edge edge)
    {
        if (scroll.Content is not Control content) return;

        // Handed back completely at rest, rather than set to a translation of zero: the stylesheet
        // is then the only thing describing this control again.
        if (edge.Give.Offset == 0)
        {
            content.RenderTransform = null;
            return;
        }

        // ⚠ Built rather than parsed. The string form is a CSS-like literal, so on a machine whose
        // decimal separator is a comma a fractional value stops being a number — and this now runs
        // every frame rather than once per notch, where parsing was already the expensive part.
        var ops = TransformOperations.CreateBuilder(1);
        ops.AppendTranslate(0, edge.Give.Offset);
        content.RenderTransform = ops.Build();
    }
}
