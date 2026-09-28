using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// Hands the window over to another copy of UGT Manager — the same file after an update, or the
/// installed copy — and ends this one.
///
/// 🔴 **The successor waits for this process to END before taking the one-window lock.** Started
/// while this window still holds it (SingleInstance), it would find the lock taken, raise this
/// window and exit — so a restart could close everything and open nothing, depending on which of
/// the two processes got there first.
///
/// ⚠ Told through an environment variable, never an argument: any argument starting with a dash is
/// a command-line verb to the successor (CommandLine.Handles), and an older installed copy that
/// does not know this one would answer "unknown command" instead of opening. A variable it does
/// not know is simply ignored — it then races as it always did, no worse.
/// </summary>
internal static class Relaunch
{
    private const string Variable = "UGT_MANAGER_AFTER_EXIT";

    /// <summary>
    /// Starts <paramref name="executable"/> and ends this process. Null when it started; the reason
    /// otherwise, with this window left open.
    /// </summary>
    public static string? Start(string executable, string? directory = null)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = directory ?? Path.GetDirectoryName(executable) ?? "",
            };
            start.Environment[Variable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

            using var _ = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or FileNotFoundException)
        {
            return $"Could not start {executable}: {ex.Message}";
        }

        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        return null;
    }

    /// <summary>
    /// In the successor, before the one-window lock is asked for: waits for the copy that started
    /// it to end. Returns at once when nobody handed over.
    ///
    /// ⚠ The wait is on the process's exit, an event. The ceiling only covers a predecessor that
    /// hangs while closing; past it, the lock decides as for any second launch.
    ///
    /// ⚠ The variable is removed first: games and servers started from this window inherit its
    /// environment, and a stale pid there means nothing to anybody.
    /// </summary>
    public static void WaitForPredecessor()
    {
        var value = Environment.GetEnvironmentVariable(Variable);
        if (value is null) return;
        Environment.SetEnvironmentVariable(Variable, null);

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return;

        try
        {
            using var predecessor = Process.GetProcessById(id);
            predecessor.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            // Already gone: nothing to wait for.
        }
        catch (InvalidOperationException)
        {
        }
    }
}
