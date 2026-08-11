using System.Diagnostics;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Which of these games are running right now, from ONE look at the machine.
///
/// ⚠ Why this exists next to IPlatform.IsGameRunning rather than instead of it: that one answers
/// for a single game and, to do it, opens the main module of every process on the machine. Asking
/// it fifty-three times, every few seconds, would be a couple of thousand handle opens for a badge
/// on a list. It stays where it is — the install and uninstall engines ask about one game, once,
/// at the moment it matters, and there precision beats speed.
///
/// This one is the opposite trade. It is meant to run while somebody watches a window, so it is
/// built to cost almost nothing:
///
/// · one enumeration of the processes, which needs no handle at all — the name is free;
/// · the names are matched against the executables we already know the games have;
/// · only for the few that match is a path resolved, which is the part that costs.
///
/// So a machine with three hundred processes and fifty games opens a handle for the one or two
/// processes that could possibly be a game.
///
/// 🔸 What it cannot see: a game running as another user or elevated, where the path cannot be
/// read. It answers "not running" there, and the install engine's own check — which fails loudly
/// rather than quietly — is what catches that case.
/// </summary>
public sealed class RunningGames
{
    private readonly HashSet<string> _running;

    private RunningGames(HashSet<string> running) => _running = running;

    /// <summary>Nothing running: what a sweep that could not be taken should look like.</summary>
    public static RunningGames None { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Game folders currently holding a running process.</summary>
    public IReadOnlyCollection<string> Paths => _running;

    public bool IsRunning(GameInstall game) => _running.Contains(game.Path);

    /// <summary>True when this sweep says something different from that one.</summary>
    public bool Differs(RunningGames other) => !_running.SetEquals(other._running);

    public static RunningGames Sweep(IEnumerable<GameInstall> games)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // What a process would have to be called to be one of these games, and which games that
        // name could belong to. Two games can carry the same executable name — a repack and an
        // original, say — so the value is a list.
        var byName = new Dictionary<string, List<GameInstall>>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            if (game.ExecutablePath is not { Length: > 0 } executable) continue;

            var name = Path.GetFileNameWithoutExtension(executable);
            if (name.Length == 0) continue;

            if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
            list.Add(game);
        }

        if (byName.Count == 0) return None;

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return None;
        }

        foreach (var process in processes)
        {
            try
            {
                if (!byName.TryGetValue(process.ProcessName, out var candidates)) continue;

                // Only now is a handle opened, and only for a process whose name says it might be
                // one of these games.
                var file = process.MainModule?.FileName;
                if (file is null) continue;

                foreach (var game in candidates)
                {
                    if (IsInside(game.Path, file)) running.Add(game.Path);
                }
            }
            catch
            {
                // Access denied, or a process that ended between the enumeration and the question.
            }
            finally
            {
                process.Dispose();
            }
        }

        return new RunningGames(running);
    }

    private static bool IsInside(string folder, string file)
    {
        try
        {
            var root = Path.GetFullPath(folder);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

            return Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
