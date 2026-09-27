using UnityGameTranslator.Manager.Core.Detection;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// A game Play was pressed for: counted as running from the click, and given back on the first
/// event that says how the launch ended — or, for a store that says nothing, past the patience.
/// </summary>
internal static class LaunchWatchChecks
{
    private const string Game = @"C:\games\a-game";

    internal static void WhenAStartingGameIsGivenBack()
    {
        Program.Section("When a game being started is given back");

        var t0 = new DateTime(2026, 9, 27, 20, 0, 0, DateTimeKind.Utc);
        bool NotRunning(string _) => false;

        var watch = new LaunchWatch();
        watch.Begin(Game, t0);
        Program.Check(watch.IsStarting(Game), "a game is starting from the click",
            "the store can take seconds to wake: writes must be refused before the process exists");
        Program.Check(RunningGames.None.With(watch.Paths).IsRunning(Game), "and counts as running for every guard",
            "every button asks 'is it running'; one question, answered from the click");

        Program.Check(watch.Settle(NotRunning, t0.AddSeconds(30)).Count == 0, "a store launch still waiting is not given back",
            "Steam may still be starting it — nothing has said otherwise");

        var seen = new LaunchWatch();
        seen.Begin(Game, t0);
        var appeared = seen.Settle(p => p == Game, t0.AddSeconds(5));
        Program.Check(appeared.Count == 1 && appeared[0].How == LaunchEnd.Appeared && !seen.IsStarting(Game),
            "the game seen running ends the wait", "from then on it is simply running");

        var direct = new LaunchWatch();
        var ended = false;
        direct.Begin(Game, t0, () => ended);
        Program.Check(direct.Settle(NotRunning, t0.AddSeconds(1)).Count == 0, "a direct launch still alive is waited for",
            "its process is loading the game");
        ended = true;
        var closed = direct.Settle(NotRunning, t0.AddSeconds(2));
        Program.Check(closed.Count == 1 && closed[0].How == LaunchEnd.ClosedFirst, "its process closing first gives the hand back at once",
            "an event, not a delay: nothing is left to wait for");

        var launcher = new LaunchWatch();
        launcher.Begin(Game, t0, () => true);
        var relaunched = launcher.Settle(p => p == Game, t0.AddSeconds(3));
        Program.Check(relaunched.Count == 1 && relaunched[0].How == LaunchEnd.Appeared,
            "seen running wins over its launcher having closed", "a game that restarts itself through its store did start");

        var store = new LaunchWatch();
        store.Begin(Game, t0);
        Program.Check(store.Settle(NotRunning, t0 + LaunchWatch.Patience - TimeSpan.FromSeconds(1)).Count == 0,
            "a silent store is waited for up to the patience", "an update or a sign-in can take a while");
        var gaveUp = store.Settle(NotRunning, t0 + LaunchWatch.Patience);
        Program.Check(gaveUp.Count == 1 && gaveUp[0].How == LaunchEnd.GaveUp, "and given back past it",
            "a store that failed says nothing: the card must not stay locked for ever");

        var stopped = new LaunchWatch();
        stopped.Begin(Game, t0);
        Program.Check(stopped.Stop(Game) && !stopped.IsStarting(Game) && !stopped.Stop(Game),
            "Stop waiting gives the hand back, once", "the person knows the store will not start it");
    }
}
