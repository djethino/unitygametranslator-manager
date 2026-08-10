namespace UnityGameTranslator.Installer.Gui;

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

    public static void OpenUrl(string url)
    {
        try
        {
            Open(url);
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
