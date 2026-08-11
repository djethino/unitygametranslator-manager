using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using UnityGameTranslator.Manager.Cli;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Update;

namespace UnityGameTranslator.Manager.Gui;

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

        // Where Windows' own "Uninstall" button lands (the UninstallString we register). It opens
        // the removal window rather than removing anything: pressing uninstall in a list of
        // installed apps is a request to be asked, not an instruction to be obeyed silently. It is
        // intercepted before the command line, which would otherwise see the leading dashes and
        // answer "unknown command" into a console nobody asked for.
        var removing = args.Any(a => a.Equals("--remove", StringComparison.OrdinalIgnoreCase));

        if (!removing && CommandLine.Handles(args))
        {
            SpeakToTheTerminal();
            return CommandLine.RunAsync(args).GetAwaiter().GetResult();
        }

        // Kept in step with the version actually running: an entry frozen at the version installed
        // first would tell Windows something untrue after every self-update.
        RefreshSystemEntry();

        // One window at a time. Two of them share one settings file, one set of receipts and one
        // catalogue cache, so the second to save quietly overwrites the first — and two copies
        // updating the tool at once would each rename the running binary.
        //
        // Only the window is guarded: asking a question on the command line while the window is
        // open has to keep working, and it does.
        using var single = AcquireWindowRight();
        if (single is null)
        {
            // The person double-clicked and expects to see the tool. Showing them the copy they
            // already have is the answer; doing nothing at all would read as a launch that failed.
            RaiseExistingWindow();
            return 0;
        }

        App.OpenRemovalOnStart = removing;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static void RefreshSystemEntry()
    {
        try
        {
            new SelfInstaller(PlatformFactory.Create()).RefreshRegistrationIfStale();
        }
        catch
        {
            // Nothing here is worth failing a launch over.
        }
    }

    private static SingleInstance? AcquireWindowRight()
    {
        try
        {
            return SingleInstance.TryAcquire(PlatformFactory.Create());
        }
        catch (PlatformNotSupportedException)
        {
            // A system we have no adapter for. Whatever complains about that, it will not be this.
            return SingleInstance.Unguarded();
        }
    }

    /// <summary>
    /// Brings the copy that is already running to the front.
    ///
    /// Windows only, and best effort on purpose: there is no portable way to raise someone else's
    /// window, and the alternative — refusing to start with an error box — would be worse on the
    /// systems where it cannot be done. There, the second launch simply ends.
    /// </summary>
    private static void RaiseExistingWindow()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        if (SingleInstance.HolderProcessId is not { } id) return;

        try
        {
            using var holder = System.Diagnostics.Process.GetProcessById(id);
            var window = holder.MainWindowHandle;
            if (window == IntPtr.Zero) return;

            NativeWindow.ShowWindow(window, NativeWindow.SW_RESTORE);
            NativeWindow.SetForegroundWindow(window);
        }
        catch (ArgumentException)
        {
            // It ended between the lock failing and now. Nothing to raise, and nothing wrong.
        }
        catch (InvalidOperationException)
        {
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()

        // ⚠ How the window is put on screen, chosen by measuring rather than by taking the default.
        //
        // A window doing NOTHING cost three and a half per cent of a core, and none of it was ours:
        // an empty Avalonia window with a single line of text cost the same. It is the composition
        // path — the default keeps a compositor ticking whether or not anything changed. Measured
        // here, on an idle window, four ways:
        //
        //     WinUIComposition (the default) .... 3.67 %
        //     DirectComposition ................ 98.71 %   ⚠ never
        //     LowLatencyDxgiSwapChain ........... 0.51 %
        //     RedirectionSurface ................ 0.78 %
        //
        // So the swap chain, with the redirection surface behind it for any machine that cannot do
        // it — a list, not a demand, because a rendering path that fails is a window nobody sees.
        // The GPU is still used either way; this is not a retreat to software drawing.
        //
        // 🔸 What it gives up is a transparent or acrylic window, which this program does not have
        // and does not want. If one is ever wanted, this is where the cost of it will be.
        .With(new Win32PlatformOptions
        {
            CompositionMode =
            [
                Win32CompositionMode.LowLatencyDxgiSwapChain,
                Win32CompositionMode.RedirectionSurface,
            ],
        })

        // Inter ships with the app: game names are routinely Chinese, Japanese or Cyrillic, and
        // a missing glyph turns the list into boxes on exactly the games that need translating.
        .WithInterFont()
        .LogToTrace();

    /// <summary>
    /// Borrows the console of whoever started us, so the command line face has somewhere to write.
    ///
    /// ⚠ THE WHOLE REASON THIS EXISTS. The file declares itself a window program, so Windows hands
    /// it no console at all — which is the point: opening the window must not put a black rectangle
    /// on someone's screen. The first attempt did the opposite (console program, hide the console
    /// afterwards) and it does not work on Windows 11: when the console host is Windows Terminal,
    /// GetConsoleWindow returns a hidden stand-in of the pseudo-console, not the window a person
    /// can see, so hiding it hides nothing and an empty terminal stays on screen for the whole
    /// session. Measured on the user's machine, not deduced.
    ///
    /// So the window face gets no console ever, and the command line face attaches to the one it
    /// was launched from.
    ///
    /// 🔸 What this costs, and it is real: a window-subsystem program does not hold the shell, so
    /// typing a command by hand gives the prompt back immediately while the output keeps arriving
    /// under it. Scripts are unaffected — a redirected or captured stream is inherited whatever the
    /// subsystem, and the caller waits for end-of-stream as usual, which is why our own end-to-end
    /// tests read this exactly as they did before.
    /// </summary>
    private static void SpeakToTheTerminal()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            // Asked BEFORE attaching, and this ordering is the whole trick. A handle already
            // present means the caller gave us somewhere to write — a pipe when our output is being
            // captured, or their console handle when it is not — and in both cases the streams must
            // be left exactly as they are. Nothing means we were handed nothing, which is when the
            // console we are about to attach to becomes the answer.
            //
            // ⚠ Console.IsOutputRedirected is NOT the test to use here: with no handle at all it
            // reports "redirected", so trusting it would skip the rebinding in precisely the case
            // that needs it, and the command would print into nowhere.
            // ⚠ And when there IS one, we attach to nothing at all: AttachConsole replaces the
            // process's standard handles with the console's, which quietly destroys a redirection
            // the caller had already set up. Measured, not read: with the attach done first,
            // `tool help > file` produced an empty file every time, through cmd and through a
            // batch shim alike.
            if (HasStandardHandle()) return;

            // ATTACH_PARENT_PROCESS. Fails harmlessly when the parent has no console — someone who
            // double-clicked the file and passed arguments through a shortcut, for instance.
            if (!NativeConsole.AttachConsole(unchecked((uint)-1))) return;

            Console.SetOut(new StreamWriter(OpenConsole(Console.OpenStandardOutput, "CONOUT$",
                                                        FileAccess.Write)) { AutoFlush = true });
            Console.SetError(new StreamWriter(OpenConsole(Console.OpenStandardError, "CONOUT$",
                                                          FileAccess.Write)) { AutoFlush = true });

            // Without this, every question the command line asks reads end-of-input and answers
            // itself "no" — which for self-update means quietly declining on the person's behalf.
            Console.SetIn(new StreamReader(OpenConsole(Console.OpenStandardInput, "CONIN$",
                                                       FileAccess.Read)));
        }
        catch (DllNotFoundException)
        {
            // Wine, or a Windows without the usual console host: nothing to attach to.
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (IOException)
        {
            // A standard handle that cannot be opened. The command still runs; it just has nowhere
            // to speak, which is better than refusing to run at all.
        }
    }

    /// <summary>Did whoever started us hand us a stream to write to?</summary>
    private static bool HasStandardHandle()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;

        var handle = NativeConsole.GetStdHandle(NativeConsole.StdOutput);
        return handle != IntPtr.Zero && handle != new IntPtr(-1);
    }

    /// <summary>
    /// The console stream, by whichever of the two routes works.
    ///
    /// Attaching normally gives the process its standard handles, and then the framework's own
    /// opener is enough. When it does not, Stream.Null comes back — a stream that swallows
    /// everything without complaining, which would leave someone staring at a command that printed
    /// nothing and returned zero. The console devices are opened by name in that case.
    /// </summary>
    private static Stream OpenConsole(Func<Stream> standard, string device, FileAccess access)
    {
        var stream = standard();
        if (stream != Stream.Null) return stream;

        return File.Open(device, FileMode.Open, access, FileShare.ReadWrite);
    }
}

[SupportedOSPlatform("windows")]
internal static class NativeConsole
{
    internal const int StdOutput = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int handle);
}

[SupportedOSPlatform("windows")]
internal static class NativeWindow
{
    internal const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr window);
}
