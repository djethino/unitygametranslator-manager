using UnityGameTranslator.Manager.Core.Interaction;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// How the end of a scroll gives, and how it comes back.
///
/// 🔴 **The defect these exist for could not be seen in a still picture.** Every notch used to start
/// its own out-and-back animation, so the edge was in the right place at the end of each one and
/// wrong all the way through: turning the wheel steadily produced a bounce per notch — "it bounces
/// and bounces and bounces until the wheel stops" — while any single reading of the offset looked
/// perfectly sane. A moment only exists in a sequence, so these cases are sequences.
///
/// ⚠ Time is handed in rather than read, which is the whole reason this rule lives in Core: a frame
/// is a number here, so a wheel turned for half a second is a loop and not a stopwatch.
/// </summary>
internal static class EdgeGiveChecks
{
    /// <summary>One frame at 60 Hz, which is what every sequence below is counted in.</summary>
    private const double Frame = 1.0 / 60;

    internal static void HowFarOneNotchLeans()
    {
        Program.Section("How far the edge gives");

        // ⚠ These read `Want`, not `Offset`: the resistance and the ceiling are about where the
        // wheel ASKS the edge to be. What is drawn follows it through a second spring, which is
        // what the smoothness section below is about.
        var give = new EdgeGive();
        Program.Check(give.AtRest && give.Offset == 0,
            "a scroller that has not been pushed leans by nothing",
            "a view that leans while nobody is at the end is answering a question nobody asked");

        give.Push(1);
        Program.Check(Math.Abs(give.Want - EdgeGive.PerNotch) < 0.001,
            "one notch from rest asks for exactly PerNotch",
            "this is the figure the tool already leaned by; changing it silently changes the feel");

        // 🔴 The resistance, and the reason it is squared: the edge has to firm up under the hand
        // rather than arrive at a second wall.
        var first = give.Want;
        give.Push(1);
        var second = give.Want - first;
        Program.Check(second < EdgeGive.PerNotch && second > 0,
            "the second notch is worth less than the first, and still worth something",
            "a give with no resistance opens like a drawer; one that stops dead is a second wall");

        // Pushing for ever approaches the ceiling and never reaches it.
        for (var i = 0; i < 500; i++) give.Push(1);
        Program.Check(give.Want < EdgeGive.MaxPull && give.Want > EdgeGive.MaxPull * 0.9,
            "five hundred notches approach the ceiling without reaching it",
            "an edge that can be opened indefinitely stops reading as an edge");

        // ⚠ The case a trackpad produces and a detent never does: one event worth far more than a
        // notch. `Give` is read from where the edge sat BEFORE the push, so the clamp is what stops
        // a single large delta spending the whole of it at full give.
        var flick = new EdgeGive();
        flick.Push(9000);
        Program.Check(flick.Want <= EdgeGive.MaxPull,
            "one enormous delta cannot push past the ceiling",
            "a free-spinning wheel sends deltas in the hundreds; overshooting here is what the spring then yanks back");

        var upward = new EdgeGive();
        upward.Push(-1);
        Program.Check(Math.Abs(upward.Want + EdgeGive.PerNotch) < 0.001,
            "the other end gives exactly as much, the other way",
            "an edge that behaves differently at the top than at the bottom reads as a fault");
    }

    internal static void TheWheelHoldsTheEdgeWhileItTurns()
    {
        Program.Section("The wheel holds the edge while it turns");

        // 🔴 THE regression case. A wheel spun steadily delivers something every frame; the edge
        // must stay out for as long as that lasts, and never travel back through zero in between.
        var give = new EdgeGive();
        var lowest = double.MaxValue;
        var reversals = 0;
        var previous = 0.0;

        // ⚠ The first frames are the edge OPENING, which is a climb and not a state. What is being
        // measured here is the steady turn, so the climb is skipped — mixing the two would read the
        // lowest point of a rising curve as a fall.
        for (var frame = 0; frame < 60; frame++)
        {
            give.Push(1);
            give.Advance(Frame);

            if (frame > 15)
            {
                lowest = Math.Min(lowest, give.Offset);
                if (give.Offset < previous - 0.001) reversals++;
            }

            previous = give.Offset;
        }

        Program.Check(lowest > EdgeGive.PerNotch * 0.9,
            "a wheel turning every frame keeps the edge out the whole time",
            "this is the ping-pong: the edge used to travel back to zero between two notches");

        Program.Check(reversals == 0,
            "and it never travels backwards while the wheel is still pushing",
            "a push and a pull netted against each other is what made a free-spinning wheel tremble");

        // 🔴 **A real mouse, which is the gesture that was reported.** A detent every three frames —
        // around twenty a second, an ordinary steady turn — is the case the old shape failed: each
        // notch restarted a whole out-and-back, so the edge slammed home between two of them. Here
        // the spring only gets the frames in between, so the edge sags a little and is pushed back
        // out, and it climbs rather than oscillating.
        var detents = new EdgeGive();
        var floor = double.MaxValue;
        var ceiling = 0.0;

        for (var notch = 0; notch < 40; notch++)
        {
            detents.Push(1);
            detents.Advance(Frame);
            detents.Advance(Frame);
            detents.Advance(Frame);

            // Again the steady turn, not the climb into it.
            if (notch < 12) continue;

            floor = Math.Min(floor, detents.Offset);
            ceiling = Math.Max(ceiling, detents.Offset);
        }

        Program.Check(floor > EdgeGive.PerNotch / 2,
            "a detent every three frames never lets the edge slam back home",
            "this is what 'it bounces and bounces and bounces until the wheel stops' was");

        // 🔴 **The band is the measurement, not the height.** Steady turning settles a little BELOW
        // one notch — the spring takes its share of the frames between two detents — and what makes
        // it read as a held edge rather than as a bounce is that the band is narrow. The shape this
        // replaces swung the full height on every notch, so the same reading would have been 0 to 8.
        Program.Check(ceiling - floor < EdgeGive.PerNotch / 4 && ceiling < EdgeGive.MaxPull / 2,
            $"and it holds within a couple of pixels instead of swinging (low {floor:0.0}, high {ceiling:0.0})",
            "a wide band IS the ping-pong; a narrow one is an edge being held open");

        // ⚠ A notched wheel is the other half of the same line, and it needs no constant of its own:
        // the gaps between detents are far longer than a frame, so the spring gets its turn.
        var notched = new EdgeGive();
        notched.Push(1);
        for (var frame = 0; frame < 12; frame++) notched.Advance(Frame);

        Program.Check(notched.Offset < EdgeGive.PerNotch / 2,
            "a single notch is already on its way back two hundred milliseconds later",
            "waiting for the wheel to be declared stopped is what made the view look stuck");
    }

    /// <summary>
    /// 🔴 **Not "does it bounce" but "is it smooth", and they are different questions.**
    ///
    /// Reported after the ping-pong was fixed: *"it still trembles, less, but it is not smooth —
    /// and the site does it too"*. A real wheel does not deliver one notch every N frames on the
    /// dot; the gaps are uneven. If pushing and springing are two regimes that take turns, the edge
    /// climbs on the frames a notch lands and falls on the frames none does — a sawtooth whose
    /// teeth are a few pixels and whose rate is the rate of the wheel. Every single position in it
    /// is defensible, which is why nothing but a sequence can see it.
    ///
    /// The measurement is the TOOTH: the biggest fall between two rises while the wheel is still
    /// turning. A held edge has none worth seeing.
    /// </summary>
    internal static void WhetherItTremblesUnderARealWheel()
    {
        Program.Section("Whether it trembles under a real wheel");

        // The gaps a hand actually produces: mostly two or three frames, sometimes one, sometimes
        // four. Fixed rather than random so a failure is the same failure tomorrow.
        int[] gaps = { 2, 1, 3, 2, 1, 2, 4, 1, 2, 3, 1, 2, 2, 3, 1, 2, 1, 3, 2, 2 };

        var give = new EdgeGive();
        var worstFall = 0.0;
        var previous = 0.0;
        var falling = 0.0;
        var first = true;

        foreach (var gap in gaps)
        {
            give.Push(1);
            for (var f = 0; f < gap; f++)
            {
                give.Advance(Frame);

                if (!first)
                {
                    // Accumulate a run of falls, so one long slide counts once and at full size.
                    if (give.Offset < previous) falling += previous - give.Offset;
                    else { worstFall = Math.Max(worstFall, falling); falling = 0; }
                }

                previous = give.Offset;
                first = false;
            }
        }

        worstFall = Math.Max(worstFall, falling);

        // A fifth of a notch is under a pixel and a half here: the eye reads that as a held edge
        // breathing, not as a shake. Anything at a whole notch is the sawtooth.
        Program.Check(worstFall < EdgeGive.PerNotch / 5,
            $"an uneven wheel does not saw the edge up and down (worst fall {worstFall:0.0}px)",
            "pushing and springing taking turns is a tremble, however small each turn is");
    }

    internal static void HowTheEdgeComesBack()
    {
        Program.Section("How the edge comes back");

        var give = new EdgeGive();
        give.Push(1);

        // The frame that consumes the push. From the next one the spring has it.
        give.Advance(Frame);
        var held = give.Want;

        var frames = 0;
        var crossings = 0;
        var sign = Math.Sign(give.Offset);
        while (give.Advance(Frame))
        {
            if (Math.Sign(give.Offset) != 0 && Math.Sign(give.Offset) != sign) crossings++;
            sign = Math.Sign(give.Offset);
            if (++frames > 600) break;
        }

        Program.Check(Math.Abs(held - EdgeGive.PerNotch) < 0.001,
            "the frame that takes the push leaves the asked-for edge alone",
            "the wheel decides where the edge sits for that frame; the spring may not net against it");

        Program.Check(crossings == 0,
            "it comes back without ever overshooting",
            "critically damped on purpose: a wobble reads as a bug, a single soft return as a material");

        Program.Check(frames > 0 && frames * Frame < 0.45,
            $"and it is home in well under half a second (took {frames * Frame:0.00}s)",
            "an edge that hangs open is the shape that made the page feel stuck");

        Program.Check(give.AtRest && give.Offset == 0,
            "and it stops exactly at zero rather than near it",
            "a sub-pixel left behind keeps a render transform alive on every scroller for ever");
    }

    internal static void WhatAStalledWindowHandsBack()
    {
        Program.Section("What a stalled window hands back");

        // ⚠ A window that was not drawing — dragged, occluded, the machine asleep — hands back a
        // step of seconds rather than milliseconds. The integrator is explicit in the stiffness
        // term, so an unclamped step of that size does not merely look wrong, it diverges.
        var give = new EdgeGive();
        give.Push(1);
        give.Advance(Frame);
        give.Advance(4.0);

        Program.Check(double.IsFinite(give.Offset) && Math.Abs(give.Offset) <= EdgeGive.MaxPull,
            "a four-second step leaves the edge somewhere real",
            "an unclamped step through this spring returns NaN, and a NaN transform takes the panel with it");

        var settled = new EdgeGive();
        settled.Push(1);
        settled.Advance(Frame);
        while (settled.Advance(1.0)) { }

        Program.Check(settled.AtRest,
            "and a run of long steps still arrives",
            "a spring that cannot settle keeps a frame callback alive for the life of the window");
    }
}
