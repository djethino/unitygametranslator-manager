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

    /// <summary>Below this it has arrived; anything smaller is a sub-pixel nobody can see.</summary>
    private const double Settled = 0.2;

    private double _offset;
    private double _velocity;
    private bool _carried;

    /// <summary>Pixels past the edge. Positive leans down (the top was pushed), negative up.</summary>
    public double Offset => _offset;

    /// <summary>Nothing to draw and nothing to integrate.</summary>
    public bool AtRest => _offset == 0 && _velocity == 0;

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

        var left = 1 - Math.Abs(_offset) / MaxPull;
        var give = left > 0 ? left * left : 0;

        _offset = Math.Clamp(_offset + notches * PerNotch * give, -MaxPull, MaxPull);
        _carried = true;
    }

    /// <summary>
    /// Moves the edge on by <paramref name="seconds"/>. Returns whether there is still something to
    /// draw — false means it has arrived and the caller can stop asking.
    /// </summary>
    public bool Advance(double seconds)
    {
        // 🔴 While the wheel is still turning, the wheel decides where the edge sits. Being carried
        // is not travelling, so the release starts from rest rather than from whatever the previous
        // return had built up.
        if (_carried)
        {
            _carried = false;
            _velocity = 0;
            return true;
        }

        if (AtRest) return false;

        var dt = Math.Min(Math.Max(seconds, 0), LongestStep);
        var steps = Math.Min(8, Math.Max(1, (int)Math.Ceiling(Omega * dt / MaxSubstep)));
        var h = dt / steps;

        // Critically damped, semi-implicit: velocity first, then position.
        for (var i = 0; i < steps; i++)
        {
            _velocity += (-Omega * Omega * _offset - 2 * Omega * _velocity) * h;
            _offset += _velocity * h;
        }

        if (Math.Abs(_offset) < Settled && Math.Abs(_velocity) < Settled * Omega)
        {
            _offset = 0;
            _velocity = 0;
            return false;
        }

        return true;
    }

    /// <summary>Hands the edge back at once — the scroller left, or the list closed under it.</summary>
    public void Release()
    {
        _offset = 0;
        _velocity = 0;
        _carried = false;
    }
}
