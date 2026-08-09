using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace UnityGameTranslator.Installer.Gui;

/// <summary>
/// Credits.
///
/// This tool installs other people's work. BepInEx and MelonLoader are the reason any of this is
/// possible, and Ollama is what makes free local translation a real option — none of them owes us
/// anything. So they are named, linked, and the user is pointed at how to support them. An
/// installer that quietly downloads a project and never mentions it makes that project invisible
/// to the very people who depend on it.
/// </summary>
public sealed class AboutWindow : Window
{
    private sealed record Credit(
        string Name,
        string What,
        string License,
        string Url,
        string? SupportUrl = null);

    private static readonly Credit[] Distributed =
    {
        new("BepInEx", "The mod loader that lets plugins run inside a Unity game.",
            "LGPL-2.1", "https://github.com/BepInEx/BepInEx",
            "https://opencollective.com/bepinex"),

        new("MelonLoader", "The other mod loader, for games and setups BepInEx does not cover.",
            "Apache-2.0", "https://github.com/LavaGang/MelonLoader"),

        new("Ollama", "Runs a language model on your own machine, so translating costs nothing.",
            "MIT", "https://github.com/ollama/ollama"),
    };

    private static readonly Credit[] BuiltWith =
    {
        new("Avalonia", "The interface you are looking at.", "MIT", "https://avaloniaui.net"),
        new(".NET", "The runtime, shipped inside this executable.", "MIT", "https://dotnet.microsoft.com"),
        new("UniverseLib", "Used by the mod for its in-game interface.", "LGPL-2.1",
            "https://github.com/sinai-dev/UniverseLib"),
    };

    public AboutWindow()
    {
        Title = "About";
        Width = 620;
        Height = 640;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.FindResource("SurfaceBase") as IBrush;

        var layout = new StackPanel { Spacing = 18, Margin = new Thickness(24) };

        layout.Children.Add(Header());
        layout.Children.Add(Paragraph(
            "UnityGameTranslator translates Unity games as you play, and lets players share what " +
            "they translate. This installer sets it up for you."));

        layout.Children.Add(Section("What this installer downloads for you",
            "These projects are downloaded from their own official releases, verified, and never " +
            "hosted or modified by us. They are the reason this works — please consider supporting them.",
            Distributed));

        layout.Children.Add(Section("Built with", null, BuiltWith));

        layout.Children.Add(Paragraph(
            "This installer is free software under AGPL-3.0. It collects nothing: no telemetry, " +
            "no account, no identifier."));

        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        links.Children.Add(LinkButton("Website", BuildInfo.WebsiteBaseUrl));
        links.Children.Add(LinkButton("Source code", "https://github.com/djethino/unitygametranslator-installer"));

        var close = new Button { Content = "Close", IsDefault = true };
        close.Click += (_, _) => Close();
        links.Children.Add(close);

        layout.Children.Add(links);

        // The publisher band sits OUTSIDE the scrollable, margined content: it has to reach both
        // edges of the window, and it must stay put while the credits scroll.
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };

        var scroller = new ScrollViewer { Content = layout };
        Grid.SetRow(scroller, 0);
        root.Children.Add(scroller);

        var band = PublisherBand();
        Grid.SetRow(band, 1);
        root.Children.Add(band);

        Content = root;
    }

    private Control Header()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };

        try
        {
            // The website's icon, so the three products are recognisably one family.
            using var stream = AssetLoader.Open(
                new Uri("avares://UnityGameTranslatorInstaller/Assets/icon-128.png"));
            row.Children.Add(new Image
            {
                Source = new Bitmap(stream),
                Width = 56,
                Height = 56,
                VerticalAlignment = VerticalAlignment.Top,
            });
        }
        catch
        {
            // A missing icon must not stop the window from opening.
        }

        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = "UnityGameTranslator Installer",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            Foreground = this.FindResource("TextPrimary") as IBrush,
        });
        titles.Children.Add(new TextBlock
        {
            Text = $"Version {BuildInfo.Version}",
            FontSize = 12,
            Foreground = this.FindResource("TextMuted") as IBrush,
        });
        row.Children.Add(titles);
        return row;
    }

    /// <summary>
    /// The publisher's band, across the foot of the window.
    ///
    /// Product identity at the top, publisher at the bottom: that separation is what an About box
    /// is for. The logo is drawn black on white rather than inverted to white on the dark
    /// surface — the mascot is hand-drawn line art, and inverted strokes read as a negative
    /// rather than as a drawing. So the band carries its own white, and the logo is black ink on
    /// transparent, which means the two whites are the same white and there is no visible seam.
    ///
    /// The gear mark is deliberately not used: it stands for ASymptOmatik, while this is
    /// published by ASymptOmatik Games, and only the full logo says so.
    /// </summary>
    private Control PublisherBand()
    {
        var band = new Border
        {
            Background = Brushes.White,
            BorderBrush = this.FindResource("BorderSubtle") as IBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 14),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        ToolTip.SetTip(band, "https://asymptomatikgames.com");
        band.PointerPressed += (_, _) => OpenUrl("https://asymptomatikgames.com");

        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://UnityGameTranslatorInstaller/Assets/asymptomatik-full.png"));

            band.Child = new Image
            {
                Source = new Bitmap(stream),
                Height = 44,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }
        catch
        {
            // A missing logo falls back to the name, never to an empty white strip.
            band.Child = new TextBlock
            {
                Text = "ASymptOmatik Games",
                FontWeight = FontWeight.SemiBold,
                FontSize = 15,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }

        return band;
    }

    private Control Section(string title, string? intro, Credit[] credits)
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            Foreground = this.FindResource("TextPrimary") as IBrush,
        });

        if (intro is not null) panel.Children.Add(Paragraph(intro));

        foreach (var credit in credits)
        {
            var card = new Border
            {
                Background = this.FindResource("SurfaceCard") as IBrush,
                BorderBrush = this.FindResource("BorderSubtle") as IBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 11),
            };

            var body = new StackPanel { Spacing = 4 };

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            titleRow.Children.Add(new TextBlock
            {
                Text = credit.Name,
                FontWeight = FontWeight.SemiBold,
                Foreground = this.FindResource("TextPrimary") as IBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            titleRow.Children.Add(new Border
            {
                Background = this.FindResource("SurfaceControl") as IBrush,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = credit.License,
                    FontSize = 10,
                    Foreground = this.FindResource("TextSecondary") as IBrush,
                },
            });
            body.Children.Add(titleRow);

            body.Children.Add(new TextBlock
            {
                Text = credit.What,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = this.FindResource("TextSecondary") as IBrush,
            });

            var linkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            linkRow.Children.Add(LinkButton("Project page", credit.Url, small: true));
            if (credit.SupportUrl is not null)
                linkRow.Children.Add(LinkButton("Support them", credit.SupportUrl, small: true));
            body.Children.Add(linkRow);

            card.Child = body;
            panel.Children.Add(card);
        }

        return panel;
    }

    private TextBlock Paragraph(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = this.FindResource("TextSecondary") as IBrush,
    };

    private Button LinkButton(string label, string url, bool small = false)
    {
        var button = new Button
        {
            Content = label,
            FontSize = small ? 11 : 13,
            Padding = small ? new Thickness(9, 3) : new Thickness(14, 6),
        };

        // The URL is shown on hover: a button that opens a browser should say where it goes.
        ToolTip.SetTip(button, url);
        button.Click += (_, _) => OpenUrl(url);
        return button;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No browser, or a locked-down session: nothing worth interrupting the user over.
        }
    }
}
