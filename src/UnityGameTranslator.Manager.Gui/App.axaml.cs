using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Gui;

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
            // ⚠ Removing brings up the removal window ALONE — not the whole program with a question
            // laid over it. Building the main window means scanning every drive for Unity games and
            // asking the community site about them, which is a great deal of work, and network, to
            // put someone through when they have said they want the thing gone. It also meant the
            // program had every one of its parts running while trying to delete itself.
            desktop.MainWindow = OpenRemovalOnStart
                ? RemovalWindow()
                : new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Window RemovalWindow()
    {
        var platform = PlatformFactory.Create();
        return new SelfRemoveWindow(platform, new SelfInstaller(platform), standalone: true);
    }
}
