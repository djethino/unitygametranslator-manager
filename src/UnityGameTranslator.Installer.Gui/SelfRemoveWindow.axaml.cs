using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Installer.Core.Install;
using UnityGameTranslator.Installer.Core.Platform;

namespace UnityGameTranslator.Installer.Gui;

/// <summary>
/// Taking the tool off the machine — the three questions, kept apart.
///
/// They are three because they cost different things. Removing the program costs a download.
/// Removing the settings costs whatever was configured: keys, the folders that were added, the
/// games that were overruled. Removing what the tool put INTO a game could cost months of
/// somebody's translating — so it is not offered here at all, and the window says so plainly
/// rather than staying silent and letting people guess.
///
/// This is also where Windows' own "Uninstall" button lands, which is why it is a window and not a
/// command: someone pressing uninstall in the system settings expects to be asked, not obeyed.
/// </summary>
public sealed class SelfRemoveWindow : Window
{
    public bool Removed { get; private set; }

    public SelfRemoveWindow(IPlatform platform, SelfInstaller installer)
    {
        Title = "Remove UnityGameTranslator Installer";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = this.FindResource("SurfaceBase") as IBrush;

        Content = Build(installer);
    }

    private Control Build(SelfInstaller installer)
    {
        var layout = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        layout.Children.Add(Text("Remove this tool", 15, FontWeight.SemiBold, "TextPrimary"));

        var plan = installer.PlanRemoval();

        if (plan is null)
        {
            layout.Children.Add(Text(
                "This copy was never installed — it is running from wherever you put it. Deleting "
                + "the file is all there is to do.", 12, FontWeight.Normal, "TextSecondary"));

            var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
            close.Click += (_, _) => Close();

            layout.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { close },
            });

            return layout;
        }

        var listing = new StackPanel { Spacing = 4 };
        listing.Children.Add(Text("What goes", 12, FontWeight.SemiBold, "TextPrimary"));
        listing.Children.Add(Text(plan.Directory, 11, FontWeight.Normal, "TextMuted"));

        foreach (var launcher in plan.Launchers)
            listing.Children.Add(Text(launcher, 11, FontWeight.Normal, "TextMuted"));

        if (plan.Registration is not null)
        {
            listing.Children.Add(Text("Its entry in the system's list of installed apps",
                11, FontWeight.Normal, "TextMuted"));
        }

        layout.Children.Add(listing);

        var settings = new CheckBox
        {
            Content = "Also remove my settings",
            IsChecked = false,
            FontSize = 12,
        };

        layout.Children.Add(settings);
        layout.Children.Add(Text(
            $"Your settings live in {plan.SettingsDirectory} — the language you chose, any API key, "
            + "the folders you added, the games you overruled, and the translations this tool moved "
            + "aside before replacing one. Left alone by default: reinstalling then finds everything "
            + "where you left it.", 11, FontWeight.Normal, "TextMuted"));

        layout.Children.Add(Text(
            "Your games are not touched. The mod and the translations already in them stay exactly "
            + "as they are — removing those is done from each game's own card, one at a time.",
            11, FontWeight.Normal, "TextMuted"));

        var problem = Text("", 11, FontWeight.Normal, "StatusError");
        problem.IsVisible = false;
        layout.Children.Add(problem);

        // Cancel carries both: Escape and Enter both mean "leave it alone". Removing is only ever
        // reached by aiming at it.
        var cancel = new Button { Content = "Keep it", IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => Close();

        var remove = new Button { Content = "Remove", Classes = { "primary" } };
        remove.Click += (_, _) =>
        {
            remove.IsEnabled = false;

            var problems = installer.Remove(settings.IsChecked == true);
            Removed = true;

            if (problems.Count == 0)
            {
                Close();
                return;
            }

            // Reported rather than swallowed: a removal that half worked is exactly what someone
            // needs to know about, and the running executable is the usual reason.
            problem.Text = string.Join(Environment.NewLine, problems);
            problem.IsVisible = true;
            cancel.Content = "Close";
        };

        layout.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, remove },
        });

        return layout;
    }

    private TextBlock Text(string text, double size, FontWeight weight, string colour) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        TextWrapping = TextWrapping.Wrap,
        Foreground = this.FindResource(colour) as IBrush,
    };
}
