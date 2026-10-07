using System.ComponentModel;
using System.Diagnostics;

namespace UnityGameTranslator.Manager.Core.Platform;

/// <summary>
/// Runs one of the system's own commands to the end and says whether it succeeded — the one way
/// this tool does that (systemctl for Ollama, the desktop's MIME tools on Linux).
///
/// 🔴 **Both outputs are read, at the same time, before waiting** (2026-10-07). Two copies of this
/// existed: one redirected the outputs and never read them, the other read standard output to the
/// end before touching standard error. A command that fills a pipe nobody empties blocks on its
/// write; waiting for it to exit then waits for ever (or for the ceiling, reported as a failure of a
/// command that was fine). Reading both concurrently is the only order that cannot hold it.
///
/// ⚠ Never elevated, never a shell: the arguments go one by one, and nothing the system did not
/// already allow happens.
/// </summary>
public static class Commands
{
    /// <summary>
    /// True when <paramref name="fileName"/> ran and exited with 0. False when the system does not
    /// have it (noted: an ordinary answer), when it exited otherwise (its own words noted), or when it
    /// had not finished within <paramref name="ceiling"/> (null: no ceiling).
    /// </summary>
    /// <param name="ceiling">
    /// ⚠ A ceiling on a system that sends no signal, not a wait: the command either finishes, or it is
    /// stuck on something this tool cannot see (a password prompt it will never get, say), and no
    /// event would ever tell us which. The command is left running past it: ending it is not ours to
    /// decide.
    /// </param>
    public static bool Run(string fileName, IEnumerable<string> arguments, TimeSpan? ceiling = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception ex)
        {
            // Not on this system: an ordinary answer to "can it be run", noted.
            Journal.Note("Commands.Run", $"{fileName}: {ex.Message}");
            return false;
        }
        if (process is null) return false;

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            bool finished = ceiling is { } limit ? process.WaitForExit(limit) : WaitToTheEnd(process);
            if (!finished)
            {
                Journal.Note("Commands.Run", $"{fileName}: not finished after {ceiling!.Value.TotalSeconds:0} s");
                // Disposing below closes the pipes under the two pending reads: what they then raise
                // is the expected end of this run, observed here rather than left unobserved.
                output.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                error.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return false;
            }

            if (process.ExitCode == 0) return true;

            // Its own words, which the person can act on ("Interactive authentication required").
            Journal.Note("Commands.Run", $"{fileName} exited with {process.ExitCode}: {First(error.Result, output.Result)}");
            return false;
        }
    }

    // WaitForExit() with no argument also waits for the redirected streams to close: the reads above
    // are what lets them close.
    private static bool WaitToTheEnd(Process process)
    {
        process.WaitForExit();
        return true;
    }

    private static string First(string error, string output)
    {
        var said = string.IsNullOrWhiteSpace(error) ? output : error;
        said = said.Trim();
        return said.Length > 300 ? said[..300] + "…" : said;
    }
}
