namespace UnityGameTranslator.Manager.Core.Interaction;

/// <summary>
/// The give at the end of a scroll: how far the content leans past the last line, and how it comes
/// back. One of these per scroller.
///
/// 🔴 **The wheel holds the edge; the spring only takes over once the wheel stops.** That is the
/// whole of it, and the shape it replaces got it backwards: every notch started its own out-and-back
/// animation, so turning the wheel steadily at the end of a list produced a bounce per notch —
/// reported as "it bounces and bounces and bounces until the wheel stops". A give is one edge being
/// held, not a queue of independent rebounds.
///
/// ⚠ **There is no delay anywhere in here, and that is deliberate.** The site reached this same
/// shape by trying the alternatives: waiting ninety milliseconds after the last event made the page
/// look stuck at the moment it should have been coming back, and letting the spring pull during the
/// frames the wheel was pushing made a free-spinning wheel tremble, because the drawn position was
/// a push and a pull netted against each other. The window is ONE FRAME — <see cref="Advance"/>
/// consumes the push and returns — so a wheel that delivers something every frame holds the edge
/// for as long as it turns, and a notched wheel that leaves longer gaps returns between notches.
/// No constant to get wrong.
///
/// ⚠ **Pure on purpose.** No control, no clock, no dispatcher — the caller hands it seconds and
/// reads <see cref="Offset"/>. That is what lets the behaviour be replayed in the checks, which is
/// how the two wrong shapes above were caught rather than shipped.
/// </summary>
public sealed class EdgeGive
{
    /// <summary>
    /// How far the content can be pulled past the end, however hard it is pushed.
    ///
    /// ⚠ Rarely approached: the spring never stops pulling, so steady wheeling settles at an
    /// equilibrium between push and return well below this.
    /// </summary>
    public const double MaxPull = 22;

    /// <summary>
    /// What one wheel notch is worth from a standing start. Eight pixels is felt; more is watched —
    /// the figure the tool already leaned by, kept so the first notch feels exactly as it did.
    /// </summary>
    public const double PerNotch = 8;

    /// <summary>
    /// Spring stiffness, critically damped so it arrives without wobbling. A wobble reads as a bug;
    /// a single soft return reads as a material. ω = 30 closes in about 190 ms.
    /// </summary>
    public const double Omega = 30;

    /// <summary>
    /// Largest ω·h a substep may carry. The integrator is explicit in the stiffness term, so it
    /// diverges past ω·h = 1 — and a window that was not drawing hands back a step far larger than
    /// a frame. Substepped rather than softened, because softer is the thing being fixed.
    /// </summary>
    private const double MaxSubstep = 0.35;

    /// <summary>The longest step the spring is asked to solve, whatever the caller was handed.</summary>
    private const double LongestStep = 1.0 / 30;

    /// <summary>
    /// How much of a new frame time is believed at once.
    ///
    /// 🔴 **Frames are not even, and integrating on uneven steps IS the fast tremble.** A window
    /// hands back 12ms, then 20, then 14 — that is scheduling, not frame rate. A spring solved on
    /// those steps moves by an uneven amount each time, and an uneven amount per frame is a shake
    /// whatever the model above does. Measured on a real jitter pattern: the change in speed
    /// between two frames went from about 12px/s on a perfect clock to 14.9px/s, i.e. the jitter
    /// alone was the larger half of what was left.
    ///
    /// ⚠ This is the one thing a simulation on a perfect 16.67ms clock can never show, which is why
    /// the jitter is written into the cases rather than assumed away.
    ///
    /// ⚠ Smoothed, not clamped to the real step: clamping to what actually arrived puts the jitter
    /// straight back. The simulated clock therefore drifts a little from the wall — which is
    /// invisible over the 300ms this ever runs for, and is why nothing else here is allowed to read
    /// it. A real change of cadence (60Hz to 30Hz) is followed in about eight frames.
    /// </summary>
    private const double ClockBlend = 0.2;

    /// <summary>
    /// How fast what is DRAWN follows where the wheel asked the edge to be.
    ///
    /// 🔴 **This is the whole of the smoothness, and it is a second spring rather than a smaller
    /// number.** A wheel is discrete and a spring is continuous, so the asked-for edge necessarily
    /// climbs on the frames a notch lands and falls on the frames none does — a sawtooth measured
    /// at 6.9px on an edge that only opens 8, which reads as a tremble however small each step is.
    /// No single regime can fix that: the two behaviours are both correct, and they take turns.
    ///
    /// Drawing through a critically damped spring makes the drawn edge a low-pass filter of the
    /// asked-for one. At this stiffness a sawtooth arriving twenty times a second comes through at
    /// about a hundredth of its size, while a real return — which is slow — passes untouched.
    ///
    /// ⚠ Softer than <see cref="Omega"/> on purpose: it has to be well below the rate a hand turns
    /// a wheel, and stiffening it back up is exactly how the tremble returns.
    /// </summary>
    public const double DrawOmega = 16;

    /// <summary>Below this it has arrived; anything smaller is a sub-pixel nobody can see.</summary>
    private const double Settled = 0.2;

    private double _want;
    private double _wantVelocity;
    private double _offset;
    private double _velocity;
    private double _clock;
    private bool _carried;

    /// <summary>Pixels past the edge, as DRAWN. Positive leans down, negative up.</summary>
    public double Offset => _offset;

    /// <summary>
    /// Where the wheel has asked the edge to be. Not what anybody sees — <see cref="Offset"/> is —
    /// but it is what a push moves, and the resistance is measured against it.
    /// </summary>
    public double Want => _want;

    /// <summary>Nothing to draw and nothing to integrate.</summary>
    public bool AtRest => _offset == 0 && _velocity == 0 && _want == 0 && _wantVelocity == 0;

    /// <summary>
    /// Takes a wheel notch at the edge. <paramref name="notches"/> is signed the way the wheel
    /// reports it: positive is a turn upwards, which leans the content down.
    ///
    /// 🔴 **The resistance lives HERE rather than in the spring**, and that is what makes the spring
    /// usable: it acts directly on the pixels the eye is watching instead of on an accumulated push
    /// measured in thousands, which is what left the page hanging at full stretch for most of a
    /// second. Each notch is worth less the further out it already is — squared, so the edge firms
    /// up under the hand instead of arriving at a second wall. Pushing for ever approaches
    /// <see cref="MaxPull"/> and never reaches it.
    ///
    /// ⚠ Clamped even so, and `Give` alone was not enough: the give is read from where the edge sits
    /// BEFORE the push, so one large event — a trackpad reports far more than a detent — would spend
    /// the whole of it at full give and overshoot a curve that was supposed to stop.
    /// </summary>
    public void Push(double notches)
    {
        if (notches == 0 || double.IsNaN(notches)) return;

        var left = 1 - Math.Abs(_want) / MaxPull;
        var give = left > 0 ? left * left : 0;

        _want = Math.Clamp(_want + notches * PerNotch * give, -MaxPull, MaxPull);
        _carried = true;
    }

    /// <summary>
    /// Moves the edge on by <paramref name="seconds"/>. Returns whether there is still something to
    /// draw — false means it has arrived and the caller can stop asking.
    /// </summary>
    public bool Advance(double seconds)
    {
        if (AtRest) return false;

        var arrived = Math.Min(Math.Max(seconds, 0), LongestStep);

        // The step the springs are actually solved on — see ClockBlend. The first frame has nothing
        // to average with and is believed as it stands.
        _clock = _clock == 0 ? arrived : _clock + (arrived - _clock) * ClockBlend;
        var dt = _clock;

        // ── where the wheel asks the edge to be ────────────────────────────────────────────────
        // 🔴 While the wheel is still turning, the wheel decides. Being carried is not travelling,
        // so the release starts from rest rather than from whatever the previous return built up.
        if (_carried)
        {
            _carried = false;
            _wantVelocity = 0;
        }
        else
        {
            Spring(ref _want, ref _wantVelocity, 0, Omega, dt);
            if (Math.Abs(_want) < Settled && Math.Abs(_wantVelocity) < Settled * Omega)
            {
                _want = 0;
                _wantVelocity = 0;
            }
        }

        // ── and what is actually drawn ─────────────────────────────────────────────────────────
        // Always, every frame, whatever the wheel is doing. That is what makes the drawn edge a
        // filter of the asked-for one rather than a copy of it — see DrawOmega.
        Spring(ref _offset, ref _velocity, _want, DrawOmega, dt);

        if (_want == 0 && Math.Abs(_offset) < Settled && Math.Abs(_velocity) < Settled * DrawOmega)
        {
            _offset = 0;
            _velocity = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// One critically damped step toward <paramref name="target"/>.
    ///
    /// ⚠ Substepped: the integrator is explicit in the stiffness term, so it diverges past ω·h = 1
    /// — and a window that was not drawing hands back a step far larger than a frame.
    /// </summary>
    private static void Spring(ref double position, ref double velocity, double target,
                               double omega, double dt)
    {
        var steps = Math.Min(8, Math.Max(1, (int)Math.Ceiling(omega * dt / MaxSubstep)));
        var h = dt / steps;

        for (var i = 0; i < steps; i++)
        {
            velocity += (-omega * omega * (position - target) - 2 * omega * velocity) * h;
            position += velocity * h;
        }
    }

    /// <summary>Hands the edge back at once — the scroller left, or the list closed under it.</summary>
    public void Release()
    {
        _want = 0;
        _wantVelocity = 0;
        _offset = 0;
        _velocity = 0;
        _clock = 0;
        _carried = false;
    }
}
