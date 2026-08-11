namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// Handing something to the system: a folder to open, a page to show.
///
/// In one place because both windows need both, and because the way to do it is the same
/// everywhere and the reason is not obvious: the path is given to the shell rather than to a named
/// program, so Windows opens Explorer, a Linux desktop hands it to whatever the user set, and
/// macOS to Finder. Spawning "explorer.exe" would work on exactly one of the three.
/// </summary>
public static class Shell
{
    /// <summary>
    /// Opens a folder, or does nothing at all.
    ///
    /// A locked-down desktop with no file manager is not an error worth interrupting anybody for —
    /// which is why the path is always written beside the button rather than hidden behind it.
    /// </summary>
    public static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            Open(path);
        }
        catch
        {
            // Nothing to say: the path is on screen, and it can be pasted.
        }
    }

    /// <summary>
    /// Opens a web page, and refuses to open anything else.
    ///
    /// ⚠ Not paranoia about our own site: handing a string to the shell means handing it to
    /// ShellExecute, which runs whatever the string turns out to name. One of these addresses comes
    /// off the network — the sign-in page, sent back by the server in the device flow — and
    /// "file:///C:/Windows/System32/…" or a UNC path would be started as readily as a web page. A
    /// server we trust today can be a server somebody else answers for tomorrow: a hijacked name, a
    /// proxy in the middle, or an instance somebody self-hosts and points this tool at.
    ///
    /// So the gate is here rather than at each caller, because the next address read from somewhere
    /// else will arrive through this same door.
    /// </summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return;

        try
        {
            Open(parsed.AbsoluteUri);
        }
        catch
        {
            // No browser we may start. A convenience that fails is not a failure to report.
        }
    }

    private static void Open(string target) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
        {
            UseShellExecute = true,
        });
}
