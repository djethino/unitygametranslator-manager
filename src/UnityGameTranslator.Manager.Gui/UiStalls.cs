using System.Diagnostics;
using Avalonia.Threading;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// Where the window froze, written down — so a freeze is measured, never guessed at.
///
/// 🔴 **Why it is permanent.** A freeze at start was measured once (2026-09-19, a probe on 62 games),
/// reduced, and the probe went away with the fix; when a freeze was reported again the only thing
/// left to reason from was memory. This costs nothing while nothing freezes: it writes a line only
/// when the window was actually blocked.
///
/// Two halves:
/// - a heartbeat on the UI thread: a beat arriving late means the thread was blocked that long, and
///   the line names what was running (<see cref="Doing"/>) when it resumed;
/// - named sections: a section that ran long says so, with its own duration.
///
/// ⚠ A reporting cadence, not a wait (CLAUDE.md, "Un seuil de JOURNALISATION n'est pas une
/// attente"): nothing is deferred by it.
///
/// File: <c>ui-stalls.log</c> in the tool's data folder, kept small.
/// </summary>
public static class UiStalls
{
    private const int BeatMs = 50;
    private const int StallMs = 250;     // a blocked window a person notices
    private const int SlowSectionMs = 60;
    private const long MaxBytes = 256 * 1024;

    private static string? _path;
    private static string? _doing;
    private static string? _lastDoing;
    private static readonly Stopwatch _beat = new();

    public static void Start(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            _path = Path.Combine(dataDirectory, "ui-stalls.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes) File.Delete(_path);
            Write($"--- started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception e)
        {
            // A diagnostic that cannot write is not a reason to refuse to start: said on the console.
            Console.Error.WriteLine($"[UiStalls] cannot write the log: {e.Message}");
            _path = null;
            return;
        }

        _beat.Start();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(BeatMs) };
        timer.Tick += (_, _) =>
        {
            var gap = _beat.ElapsedMilliseconds;
            _beat.Restart();
            if (gap - BeatMs >= StallMs)
                Write($"{DateTime.Now:HH:mm:ss.fff} stall {gap} ms, during: {_lastDoing ?? "(nothing named)"}");
            _lastDoing = _doing;
        };
        timer.Start();
    }

    /// <summary>Names what runs until the returned object is disposed; says so when it ran long.</summary>
    public static IDisposable Doing(string what) => new Section(what);

    private sealed class Section : IDisposable
    {
        private readonly string _what;
        private readonly string? _outer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public Section(string what)
        {
            _what = what;
            _outer = _doing;
            _doing = _outer is null ? what : _outer + " > " + what;
            _lastDoing = _doing;
        }

        public void Dispose()
        {
            _clock.Stop();
            if (_clock.ElapsedMilliseconds >= SlowSectionMs)
                Write($"{DateTime.Now:HH:mm:ss.fff} slow {_clock.ElapsedMilliseconds} ms: {_doing}");
            _doing = _outer;
        }
    }

    private static void Write(string line)
    {
        if (_path is null) return;
        try { File.AppendAllText(_path, line + Environment.NewLine); }
        catch (Exception e) { Console.Error.WriteLine($"[UiStalls] {e.Message}"); }
    }
}
