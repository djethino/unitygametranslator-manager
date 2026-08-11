using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Gui;

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

        Title = "Install UnityGameTranslator Manager";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MinHeight = 260;

        // ⚠ A ceiling, and it is not decoration. This window grows to fit what it lists, and what
        // it lists is a plan — so the day a plan is long, the window grows past the screen, the
        // buttons go with it and there is no way to answer the question being asked. Seen for real
        // on a build whose folder held two hundred runtime files. The list scrolls; the window
        // stops.
        MaxHeight = 620;

        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = this.FindResource("SurfaceBase") as IBrush;

        Content = Build(platform);
    }

    private Control Build(IPlatform platform)
    {
        var layout = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        layout.Children.Add(Heading("Install UnityGameTranslator Manager on this machine"));

        // Named, and told apart from the mod. A dialog is read out of context by definition — it
        // covers whatever it was opened from — so it cannot lean on the window behind it to say
        // what "it" is.
        layout.Children.Add(Body(
            "This is the program that sets your games up, not the mod that goes into them. You are "
            + "running the file you downloaded; keeping it means copying it somewhere it belongs, "
            + "so it is there next time without you having to find the download again."));

        var written = new StackPanel { Spacing = 4 };
        written.Children.Add(Label("What gets written"));

        var paths = new StackPanel { Spacing = 2 };
        foreach (var file in _plan.Files) paths.Children.Add(Path(file));

        // The list is the one part that has no natural length, so it is the one part that scrolls.
        // Everything else — the question, the boxes, the buttons — stays where it is, which is what
        // makes the window answerable however long the plan turns out to be.
        written.Children.Add(new ScrollViewer
        {
            Content = paths,
            MaxHeight = 180,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        });

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
            Content = "Install",
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
