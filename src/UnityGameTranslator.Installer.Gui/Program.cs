using Avalonia;

namespace UnityGameTranslator.Installer.Gui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        // Inter ships with the app: game names are routinely Chinese, Japanese or Cyrillic, and
        // a missing glyph turns the list into boxes on exactly the games that need translating.
        .WithInterFont()
        .LogToTrace();
}
