using System.Collections.Concurrent;
using Avalonia.Threading;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// Packs handed to this window — by the launch that opened it, or by a second copy through
/// PackHandoff — waiting for the list of games to exist.
///
/// ⚠ A queue rather than a call into the window: the first one arrives before there is a window,
/// and any of them can arrive while the drives are still being searched. The window takes them
/// once it has games to offer (MainWindow.OpenArrivedPacksAsync).
/// </summary>
internal static class PackInbox
{
    private static readonly ConcurrentQueue<string> Waiting = new();

    /// <summary>Raised on the interface thread when a pack arrives after the window is up.</summary>
    public static event Action? Arrived;

    /// <summary>From any thread.</summary>
    public static void Deliver(string path)
    {
        Waiting.Enqueue(path);

        // Nobody listening yet means the window has not been built: it drains the queue itself.
        if (Arrived is not null) Dispatcher.UIThread.Post(() => Arrived?.Invoke());
    }

    public static bool TryTake(out string path) => Waiting.TryDequeue(out path!);
}
