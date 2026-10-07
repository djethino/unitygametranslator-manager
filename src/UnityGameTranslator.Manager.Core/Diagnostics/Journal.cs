using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Core.Diagnostics;

/// <summary>
/// Where the Manager writes what went wrong, and the ordinary facts worth knowing afterwards.
///
/// 🔴 **Why this exists** (2026-10-07). The Manager had no log at all — one stray Trace call in the
/// whole program — so 234 of its catches had nowhere to speak and said nothing. One of them let the
/// uninstall screen announce "the translation was backed up one last time" over a backup that had
/// failed, just before deleting the translation. A failure the program cannot say is a failure
/// nobody can report.
///
/// Two voices, and they are not the same thing:
/// - <see cref="Faults.Say"/> (common) — something failed. Said once in full, then counted. This
///   journal is its sink.
/// - <see cref="Note"/> — a case recognised and handled (a process that ended between two
///   questions, a cache that was unreadable and is rebuilt). One line, once per distinct fact.
///
/// ⚠ **One file per face, kept for one launch.** The window writes <c>manager.log</c>, the command
/// line <c>manager-cli.log</c>: a command run while the window is open must not wipe the window's
/// account. Each launch moves the last one to <c>….previous.log</c> — the run before the one that
/// went wrong is often the one that explains it. Rotated by launch, never by a size picked here.
///
/// ⚠ **Opened per line, never held.** A file held open for the life of the process in
/// UserDataDirectory makes "remove my settings" fail on its last step (IPlatform.RuntimeStateDirectory
/// says how that was learned). And a folder the person removed is not created again behind their back.
///
/// ⚠ **Through Sanitize**, like crash.txt beside it: it is written to be attached to a public issue,
/// and an account name inside a path is identifying on its own.
///
/// ⚠ The command line also prints each FAULT on standard error: the person who ran the command is
/// looking at the terminal, not at a file. Notes stay in the file.
/// </summary>
public static class Journal
{
    public enum Face { Window, CommandLine }

    private static readonly object Gate = new();
    private static readonly HashSet<string> Noted = new(StringComparer.Ordinal);
    private static string? _path;
    private static bool _echoToTerminal;

    /// <summary>The file being written this run, or null when there is none.</summary>
    public static string? FilePath
    {
        get { lock (Gate) return _path; }
    }

    /// <summary>
    /// Starts this run's journal and makes it the sink of <see cref="Faults"/>. Called once, first
    /// thing, by the entry point. <paramref name="platform"/> is null on a system this tool has no
    /// adapter for: the lines then go to the terminal (command line) and the system's trace only.
    /// </summary>
    public static void Open(IPlatform? platform, Face face)
    {
        if (platform is null)
        {
            lock (Gate) _echoToTerminal = face == Face.CommandLine;
            Faults.AttachSink(Write);
            return;
        }

        var folder = platform.UserDataDirectory;
        var path = Path.Combine(folder, (face == Face.Window ? "manager" : "manager-cli") + ".log");

        Exception? refused = null;
        try
        {
            Directory.CreateDirectory(folder);
            KeepPrevious(path);
            File.WriteAllText(path,
                $"UnityGameTranslator Manager {BuildInfo.Version} ({(face == Face.Window ? "window" : "command line")}) — "
                + $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n{Environment.OSVersion}\n\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            refused = e;
        }

        lock (Gate)
        {
            _path = refused is null ? path : null;
            _echoToTerminal = face == Face.CommandLine;
        }
        Faults.AttachSink(Write);

        // Said through the sink just attached: on the command line it reaches the terminal; in the
        // window, the system's trace — the only place left when the file itself is what failed.
        if (refused is not null) Faults.Say("Journal.Open", refused, Sanitize.Path(path));
    }

    /// <summary>
    /// A log of this program's, started afresh for this launch: the last one is moved to
    /// <c>&lt;name&gt;.previous.log</c> (replacing the one before it).
    ///
    /// ⚠ Rotated by an EVENT, the launch — never by a size picked here (no constant cap: what is
    /// kept is "this run and the one before", which is what a report needs). Shared by every log
    /// of the tool (this journal, the window's UiStalls) so they cannot keep history two ways.
    /// Throws what moving a file throws: the caller decides what a log that cannot start costs.
    /// </summary>
    public static void KeepPrevious(string path)
    {
        if (!File.Exists(path)) return;
        var previous = Path.Combine(Path.GetDirectoryName(path)!,
                                    Path.GetFileNameWithoutExtension(path) + ".previous" + Path.GetExtension(path));
        File.Move(path, previous, overwrite: true);
    }

    /// <summary>
    /// A case recognised and handled — written once per distinct <paramref name="topic"/> and
    /// <paramref name="detail"/>, never a count.
    /// </summary>
    public static void Note(string topic, string detail)
    {
        lock (Gate)
        {
            if (!Noted.Add(topic + "\u0001" + detail)) return;
        }
        Append($"[Note] {topic}: {detail}", toTerminal: false);
    }

    private static void Write(string line) => Append(line, toTerminal: true);

    private static void Append(string line, bool toTerminal)
    {
        var text = $"{DateTime.UtcNow:HH:mm:ss} {Sanitize.Text(line)}";
        string? path;
        bool echo;
        lock (Gate)
        {
            path = _path;
            echo = _echoToTerminal && toTerminal;
        }

        if (echo) Console.Error.WriteLine(text);

        if (path is null)
        {
            System.Diagnostics.Trace.WriteLine(text);
            return;
        }

        // A folder the person removed ("remove my settings") is not created again for a log line.
        if (!Directory.Exists(Path.GetDirectoryName(path)!))
        {
            lock (Gate) _path = null;
            System.Diagnostics.Trace.WriteLine(text);
            return;
        }

        lock (Gate)
        {
            try
            {
                File.AppendAllText(path, text + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The file stopped taking lines (a full disk, a lock): the rest of the run goes to
                // the system's trace, and this is the line that says why.
                _path = null;
                System.Diagnostics.Trace.WriteLine($"UGT Manager journal stopped ({e.GetType().Name}: {e.Message}); {text}");
            }
        }
    }
}
