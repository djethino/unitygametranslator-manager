using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace UnityGameTranslator.Installer.Gui;

public partial class App : Application
{
    /// <summary>
    /// Set when the tool was started by the system's uninstall button, so the removal window opens
    /// over the main one rather than the person having to go and find it.
    ///
    /// The main window still opens underneath: cancelling the removal must leave them somewhere,
    /// and "somewhere" is the tool they were about to remove.
    /// </summary>
    public static bool OpenRemovalOnStart { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow();
            desktop.MainWindow = main;

            if (OpenRemovalOnStart) main.OpenRemovalWhenReady();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
