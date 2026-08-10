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

    public SelfRemoveWindow(IPlatform platform, SelfInstaller installer, bool standalone = false)
    {
        Title = "Remove UnityGameTranslator Installer";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MinHeight = 240;

        // Same ceiling as the install window and for the same reason: this one lists what goes, and
        // a window that grows past the screen takes its buttons with it.
        MaxHeight = 620;

        // Standalone is the path Windows' uninstall button takes: there is no window behind this
        // one to centre on, because the point is that nothing else is running.
        WindowStartupLocation = standalone
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;

        ShowInTaskbar = standalone;
        CanResize = false;
        Background = this.FindResource("SurfaceBase") as IBrush;

        Content = Build(installer);
    }

    private Control Build(SelfInstaller installer)
    {
        var layout = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        layout.Children.Add(Text("Remove UnityGameTranslator Installer", 15, FontWeight.SemiBold,
                                 "TextPrimary"));

        // Which of the two things bearing that name is going, said before anything else: this
        // window can be reached from Windows' own list of installed applications, where nothing
        // around it explains that the mod inside the games is a different matter.
        layout.Children.Add(Text(
            "The program that sets your games up. The mod already inside your games is a separate "
            + "matter and is not affected.", 12, FontWeight.Normal, "TextSecondary"));

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

        var items = new StackPanel { Spacing = 2 };
        items.Children.Add(Text(plan.Directory, 11, FontWeight.Normal, "TextMuted"));

        foreach (var launcher in plan.Launchers)
            items.Children.Add(Text(launcher, 11, FontWeight.Normal, "TextMuted"));

        if (plan.Registration is not null)
        {
            items.Children.Add(Text("Its entry in the system's list of installed apps",
                11, FontWeight.Normal, "TextMuted"));
        }

        listing.Children.Add(new ScrollViewer
        {
            Content = items,
            MaxHeight = 160,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        });

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

        var outcome = new StackPanel { Spacing = 6, IsVisible = false };
        layout.Children.Add(outcome);

        // Cancel carries both: Escape and Enter both mean "leave it alone". Removing is only ever
        // reached by aiming at it.
        var cancel = new Button { Content = "Keep it", IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => Close();

        var openFolder = new Button { Content = "Open the folder", IsVisible = false };
        var remove = new Button { Content = "Remove", Classes = { "primary" } };

        remove.Click += (_, _) =>
        {
            remove.IsEnabled = false;

            var report = installer.Remove(settings.IsChecked == true);
            Removed = true;

            if (report.Complete && report.BeingDeletedAfterExit is null)
            {
                Close();
                return;
            }

            ShowOutcome(outcome, report);

            // ⚠ A dead end is the one thing this window must never be. It said "could not remove"
            // and offered nothing but Close — so somebody was told their tool was half removed and
            // left to work out the rest on their own. Trying again costs nothing (deleting what is
            // already gone counts as gone), and the folder is one click away for the cases that
            // need a human.
            cancel.Content = "Close";
            remove.Content = "Try again";
            remove.IsEnabled = report.Left.Count > 0;

            if (report.WhereItWas is { } folder && Directory.Exists(folder))
            {
                openFolder.IsVisible = true;
                openFolder.Click -= OpenTheFolder;
                openFolder.Tag = folder;
                openFolder.Click += OpenTheFolder;
            }
        };

        layout.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { openFolder, cancel, remove },
        });

        return layout;
    }

    private static void OpenTheFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string folder }) Shell.OpenFolder(folder);
    }

    /// <summary>
    /// What went, and what did not, side by side.
    ///
    /// Both halves, because a list of failures alone cannot be read: someone told only that one
    /// thing could not be deleted has no way of knowing whether the tool is nearly gone or barely
    /// touched, and those call for different reactions.
    /// </summary>
    private void ShowOutcome(StackPanel panel, SelfRemovalReport report)
    {
        panel.Children.Clear();
        panel.IsVisible = true;

        if (report.Gone.Count > 0)
        {
            panel.Children.Add(Text($"Removed ({report.Gone.Count})", 12, FontWeight.SemiBold,
                                    "StatusSuccess"));

            foreach (var item in report.Gone)
                panel.Children.Add(Text(item, 11, FontWeight.Normal, "TextMuted"));
        }

        if (report.BeingDeletedAfterExit is { } pending)
        {
            panel.Children.Add(Text(
                $"{pending} is the file this window is running from, so it cannot be deleted while "
                + "you are reading this. It goes on its own within a minute of you closing.",
                11, FontWeight.Normal, "TextSecondary"));
        }

        if (report.Left.Count > 0)
        {
            panel.Children.Add(Text($"Still there ({report.Left.Count})", 12, FontWeight.SemiBold,
                                    "StatusError"));

            foreach (var item in report.Left)
                panel.Children.Add(Text(item, 11, FontWeight.Normal, "TextMuted"));
        }
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
