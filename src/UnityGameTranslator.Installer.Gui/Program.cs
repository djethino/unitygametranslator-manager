using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using UnityGameTranslator.Installer.Cli;
using UnityGameTranslator.Installer.Core.Update;

namespace UnityGameTranslator.Installer.Gui;

/// <summary>
/// The one entry point of the one executable.
///
/// Given a known command it hands over to the command line front-end; given anything else — no
/// arguments, or a folder dropped onto the icon — it opens the window. See the Cli project file
/// for why this is a single binary rather than two.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Reaching this line is the proof an update worked: the new binary started. Until it does,
        // the version it replaced is still sitting beside it under its own name, which is the only
        // way back a tool without a signing certificate can honestly offer.
        SelfUpdater.ClearPreviousVersions();

        if (CommandLine.Handles(args))
            return CommandLine.RunAsync(args).GetAwaiter().GetResult();

        HideOwnConsoleWindow();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        // Inter ships with the app: game names are routinely Chinese, Japanese or Cyrillic, and
        // a missing glyph turns the list into boxes on exactly the games that need translating.
        .WithInterFont()
        .LogToTrace();

    /// <summary>
    /// Puts away the console Windows hands a console-subsystem program, but ONLY when it is ours.
    ///
    /// The executable declares itself a console program so that its command line face behaves like
    /// every other command: the shell waits for it, output arrives in order, Ctrl+C stops it, and
    /// a pipe works. A window-subsystem binary gets none of that — the prompt comes back at once
    /// and a run of several minutes looks like a run that did nothing.
    ///
    /// ⚠ The ownership test is the whole point of this method. Launched from a terminal, the
    /// console on the other end of GetConsoleWindow is the PERSON'S terminal: hiding it would make
    /// their shell disappear while they watch. So we only hide a console we are alone in, which is
    /// the one Windows created for us when the icon was double-clicked. When the count cannot be
    /// read we leave it alone — a console left visible behind the window is untidy; a terminal
    /// swallowed by our tool is a bug someone has to reboot out of.
    /// </summary>
    private static void HideOwnConsoleWindow()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            var window = NativeConsole.GetConsoleWindow();
            if (window == IntPtr.Zero) return;

            var processes = new uint[4];
            var attached = NativeConsole.GetConsoleProcessList(processes, (uint)processes.Length);
            if (attached != 1) return;

            NativeConsole.ShowWindow(window, NativeConsole.SW_HIDE);
        }
        catch (DllNotFoundException)
        {
            // Wine, or a Windows build without the usual console host. Nothing to hide.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class NativeConsole
{
    internal const int SW_HIDE = 0;

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();

    /// <summary>
    /// How many processes share this console. One means we were given a fresh one and may close
    /// it; more means we are a guest in someone else's terminal.
    /// </summary>
    [DllImport("kernel32.dll")]
    internal static extern uint GetConsoleProcessList([Out] uint[] processList, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr window, int command);
}
