using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Installer.Core.Install;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;

namespace UnityGameTranslator.Installer.Gui;

/// <summary>
/// The offer to stay on the machine, with everything it would write on screen first.
///
/// Not a yes/no box: "install it properly?" means different things on different systems, and
/// nobody should agree to it without seeing the folder, the shortcuts and the entry in the
/// system's own list. Every line here is read from the plan, so what is shown is what happens.
///
/// The shortcuts are ticked by the person, not by us — the menu on, the desktop off. People feel
/// strongly about their desktop in both directions, and the only way not to be wrong is to ask.
/// </summary>
public sealed class SelfInstallWindow : Window
{
    private readonly SelfInstaller _installer;
    private readonly SelfInstallPlan _plan;
    private readonly Dictionary<LauncherKind, CheckBox> _launchers = new();

    /// <summary>The installation, once it has happened. Null when the window was closed instead.</summary>
    public ToolInstallation? Installed { get; private set; }

    public SelfInstallWindow(IPlatform platform, SelfInstaller installer, SelfInstallPlan plan)
    {
        _installer = installer;
        _plan = plan;

        Title = "Keep this tool on your machine";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = this.FindResource("SurfaceBase") as IBrush;

        Content = Build(platform);
    }

    private Control Build(IPlatform platform)
    {
        var layout = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        layout.Children.Add(Heading("Keep this tool on your machine"));

        layout.Children.Add(Body(
            "You are running the file you downloaded. Keeping it means copying it somewhere it "
            + "belongs, so it is there next time without you having to find the download again."));

        var written = new StackPanel { Spacing = 4 };
        written.Children.Add(Label("What gets written"));

        foreach (var file in _plan.Files) written.Children.Add(Path(file));

        if (_plan.RegistersWithTheSystem)
        {
            written.Children.Add(Path(
                "An entry in Windows' list of installed apps, so it can be removed from there too"));
        }

        layout.Children.Add(written);

        var choices = new StackPanel { Spacing = 6 };
        choices.Children.Add(Label("Shortcuts"));

        foreach (var kind in _plan.Launchers)
        {
            var box = new CheckBox
            {
                Content = kind == LauncherKind.Desktop ? "On the desktop" : MenuLabel(),
                IsChecked = kind == LauncherKind.Menu,
                FontSize = 12,
            };

            _launchers[kind] = box;
            choices.Children.Add(box);
        }

        layout.Children.Add(choices);

        layout.Children.Add(Note(
            "The file you downloaded is left exactly where it is — this copies, it does not move. "
            + "You can delete it afterwards, or keep it on a stick."));

        layout.Children.Add(Note("Nothing in your games is touched by this."));

        var problem = Note("");
        problem.Foreground = this.FindResource("StatusError") as IBrush;
        problem.IsVisible = false;
        layout.Children.Add(problem);

        var cancel = new Button { Content = "Not now", IsCancel = true };
        cancel.Click += (_, _) => Close();

        var accept = new Button
        {
            Content = "Keep it here",
            Classes = { "primary" },
            IsEnabled = _plan.Refusal is null,
        };

        if (_plan.Refusal is { } refusal)
        {
            problem.Text = refusal;
            problem.IsVisible = true;
        }

        accept.Click += (_, _) =>
        {
            accept.IsEnabled = false;

            try
            {
                var chosen = _launchers
                    .Where(pair => pair.Value.IsChecked == true)
                    .Select(pair => pair.Key)
                    .ToList();

                Installed = _installer.Install(_plan, chosen);

                // Said rather than assumed: a shortcut can fail on its own (a policy, a locked
                // shell) while the installation itself is perfectly good.
                var missing = chosen.Count > 0 && Installed.Launchers.Count == 0;
                if (missing)
                {
                    problem.Text = "Installed, but the shortcut could not be created. "
                                   + $"The tool is in {Installed.Directory}.";
                    problem.Foreground = this.FindResource("StatusWarning") as IBrush;
                    problem.IsVisible = true;
                    accept.Content = "Done";
                    accept.IsEnabled = false;
                    cancel.Content = "Close";
                    return;
                }

                Close();
            }
            catch (Exception ex)
            {
                problem.Text = ex.Message;
                problem.IsVisible = true;
                accept.IsEnabled = true;
            }
        };

        layout.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, accept },
        });

        return layout;
    }

    private static string MenuLabel() =>
        OperatingSystem.IsWindows() ? "In the Start menu" : "In the applications menu";

    private TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Foreground = this.FindResource("TextPrimary") as IBrush,
    };

    private TextBlock Body(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = this.FindResource("TextSecondary") as IBrush,
    };

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        Foreground = this.FindResource("TextPrimary") as IBrush,
    };

    private TextBlock Path(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = this.FindResource("TextMuted") as IBrush,
    };

    private TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = this.FindResource("TextMuted") as IBrush,
    };
}
