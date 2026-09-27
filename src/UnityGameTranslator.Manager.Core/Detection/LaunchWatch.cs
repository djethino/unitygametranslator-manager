namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>How a launch stopped being "starting".</summary>
public enum LaunchEnd
{
    /// <summary>The game's process was seen: it is running now.</summary>
    Appeared,

    /// <summary>Started directly, and the process we started ended before the game was seen.</summary>
    ClosedFirst,

    /// <summary>Started through a store that never said anything, and <see cref="LaunchWatch.Patience"/> ran out.</summary>
    GaveUp,
}

/// <summary>One launch that has just ended, and how.</summary>
public sealed record LaunchSettled(string GamePath, LaunchEnd How);

/// <summary>
/// The games Play was pressed for and that are not seen running yet.
///
/// 🔴 **Why "starting" is a state of its own.** The process of a game started through Steam or
/// Epic appears seconds after the click — the store wakes up first — and the sweep that notices it
/// passes every four seconds. Until then the card offered Play again and kept live every button
/// that writes into the game, while the game was about to load and the mod about to rewrite its
/// files whole. From the click, a starting game is treated as running (see
/// <see cref="RunningGames.With"/>): blocking a little early costs nothing.
///
/// ⚠ **Giving the hand back, on events first.** The game appears (the sweep sees it); or, started
/// directly, the process we started ends without the game having been seen — known at once, no
/// wait. A store says nothing when it fails to start a game (an update required, a dialogue
/// cancelled, offline), so for those the person has "Stop waiting", and past
/// <see cref="Patience"/> the hand comes back by itself: the one wait CLAUDE.md allows, on an
/// external system that emits no signal, decided with the user on 2026-09-27.
///
/// Pure: the clock and the "has the process ended" question are handed in — the checks replay it.
/// </summary>
public sealed class LaunchWatch
{
    /// <summary>
    /// How long a store launch is waited for before the hand comes back by itself. Long enough for
    /// a store that has to start, sign in and apply a short update; the person does not have to
    /// wait it out — "Stop waiting" is there.
    /// </summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private sealed class Pending
    {
        public DateTime Since;
        public Func<bool>? Ended;
    }

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game folders being started.</summary>
    public IReadOnlyCollection<string> Paths => _pending.Keys;

    public bool IsStarting(string gamePath) => _pending.ContainsKey(gamePath);

    /// <summary>Play was pressed and the launch accepted.</summary>
    /// <param name="ended">For a direct launch, whether the process we started has ended; null through a store.</param>
    public void Begin(string gamePath, DateTime now, Func<bool>? ended = null) =>
        _pending[gamePath] = new Pending { Since = now, Ended = ended };

    /// <summary>"Stop waiting": the person takes the hand back. True when there was something to stop.</summary>
    public bool Stop(string gamePath) => _pending.Remove(gamePath);

    /// <summary>
    /// Ends the launches that have ended, and says how. Asked at each sweep and when a directly
    /// started process ends.
    /// </summary>
    public IReadOnlyList<LaunchSettled> Settle(Func<string, bool> isRunning, DateTime now)
    {
        var settled = new List<LaunchSettled>();

        foreach (var (path, pending) in _pending.ToList())
        {
            // Seen running wins over everything: a game whose launcher ended once it had started
            // the game is a game that started.
            LaunchEnd? how = isRunning(path) ? LaunchEnd.Appeared
                           : pending.Ended?.Invoke() == true ? LaunchEnd.ClosedFirst
                           : now - pending.Since >= Patience ? LaunchEnd.GaveUp
                           : null;

            if (how is not { } end) continue;
            _pending.Remove(path);
            settled.Add(new LaunchSettled(path, end));
        }

        return settled;
    }
}
