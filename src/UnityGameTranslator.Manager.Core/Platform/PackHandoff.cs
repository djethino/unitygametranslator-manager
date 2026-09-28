using System.IO.Pipes;

namespace UnityGameTranslator.Manager.Core.Platform;

/// <summary>
/// Hands a pack to the UGT Manager window already open, when a double-click starts a second copy.
///
/// 🔴 **Why it exists.** One window at a time (SingleInstance): a second launch raises the first
/// window and ends. For a launch that carried a file, ending there dropped the file — the person
/// double-clicked a pack and saw the tool come forward doing nothing.
///
/// A named pipe, per user: `CurrentUserOnly` on both ends, so another account on the machine can
/// neither listen in nor send. What travels is one line, a path, and the window checks it again
/// (PackFileType.PackIn) — nothing arriving here does more than open a plan the person still has to
/// Apply.
/// </summary>
public static class PackHandoff
{
    private static string PipeName => $"UnityGameTranslatorManager-{Environment.UserName}-packs";

    /// <summary>
    /// How long a second copy tries to reach the window. ⚠ A ceiling on a system that sends no
    /// signal (the listener may be a heartbeat away from starting, or gone): no answer by then
    /// is an answer.
    /// </summary>
    private const int ReachCeilingMs = 3000;

    /// <summary>True when the open window took the path.</summary>
    public static bool Send(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(ReachCeilingMs);

            using var writer = new StreamWriter(client);
            writer.WriteLine(path);
            writer.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Listens for as long as the window holds the right to run, and passes each pack on. Started
    /// right after that right is taken, so a second copy started a moment later finds it.
    /// </summary>
    public static async Task ListenAsync(Action<string> received, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancel).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(cancel).ConfigureAwait(false);

                if (line is not null && PackFileType.PackIn([line]) is { } pack) received(pack);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A sender that went away mid-line. The next one gets a fresh pipe.
            }
        }
    }
}
