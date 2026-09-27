using System.Diagnostics;
using System.Threading;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Where <see cref="GameInventory.BuildReport"/> spends its time, step by step — read and reset by
/// the window's stall log (UiStalls), which is where a slow list shows.
///
/// 🔴 Why it exists: the report is built for every game on every redraw of the list, and a freeze
/// at start came back (2026-09-27: 15-40 ms per game, five passes, 12 s) with nothing able to say
/// which of its fifteen reads had grown. Atomic: reports are also built off the UI thread.
/// </summary>
public static class ReportTimings
{
    public static readonly string[] Names =
        { "loader", "preferences", "receipt", "runtime-libraries", "translation", "plugin", "requirements", "texts-account", "online", "sync" };

    private static readonly long[] _ticks = new long[Names.Length];

    public static long Mark() => Stopwatch.GetTimestamp();

    /// <summary>Adds the time since <paramref name="since"/> to step <paramref name="step"/>; returns now.</summary>
    public static long Add(int step, long since)
    {
        var now = Stopwatch.GetTimestamp();
        Interlocked.Add(ref _ticks[step], now - since);
        return now;
    }

    /// <summary>"loader 12 ms, preferences 300 ms, …", then zero — one reading per pass.</summary>
    public static string TakeSummary()
    {
        var parts = new List<string>();
        for (int i = 0; i < Names.Length; i++)
        {
            var ms = Interlocked.Exchange(ref _ticks[i], 0) * 1000 / Stopwatch.Frequency;
            if (ms > 0) parts.Add($"{Names[i]} {ms} ms");
        }
        return string.Join(", ", parts);
    }
}
