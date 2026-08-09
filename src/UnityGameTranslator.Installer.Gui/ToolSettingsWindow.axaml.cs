using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Installer.Core.Api;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;
using UnityGameTranslator.Installer.Core.Settings;

namespace UnityGameTranslator.Installer.Gui;

/// <summary>
/// This tool's own settings — its account and how it reaches the network.
///
/// ⚠ Separate window from Mod defaults, on purpose. They were grouped under one title with two
/// headings, which was a patch over the real problem: they are two subjects. What goes into a game
/// is answered once and written to disk; what this tool does is about the program in front of you.
/// Someone changing a value has to know, without thinking, whether it will reach a game they have
/// already set up.
///
/// The proxy is the one thing that legitimately belongs to both, and it says so: it lives here,
/// with a box to pass it on to games. The box exists because it is genuinely a decision — the same
/// network usually serves both, but a game that never needed a proxy should not inherit one just
/// because the installer did.
/// </summary>
public sealed class ToolSettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly InstallerSettings _draft;

    private StackPanel _accountPanel = null!;
    private ComboBox _proxyMode = null!;
    private TextBox _proxyUrl = null!;
    private TextBox _proxyUser = null!;
    private TextBox _proxyPassword = null!;
    private StackPanel _proxyFields = null!;
    private CheckBox _proxyInGames = null!;
    private CheckBox _online = null!;
    private TextBlock _netStatus = null!;
    private Button _applyButton = null!;
    private CancellationTokenSource? _signIn;

    public bool Saved { get; private set; }

    public ToolSettingsWindow(IPlatform platform, SettingsStore store)
    {
        _store = store;

        var current = store.Current;
        _draft = new InstallerSettings
        {
            ProxyMode = current.ProxyMode,
            ProxyUrl = current.ProxyUrl,
            ProxyUsername = current.ProxyUsername,
            ProxyPassword = current.ProxyPassword,
            ProxyBypassLocal = current.ProxyBypassLocal,
            ProxyInGames = current.ProxyInGames,
            OnlineMode = current.OnlineMode,
        };

        Title = "Settings — this tool";
        Width = 720;
        Height = 620;
        MinWidth = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.FindResource("SurfaceBase") as IBrush;

        Content = Build();
    }

    private Control Build()
    {
        var layout = new StackPanel { Spacing = 16, Margin = new Thickness(24) };

        layout.Children.Add(new TextBlock
        {
            Text = "These are about this program. What gets written into your games is under "
                 + "Mod defaults.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        layout.Children.Add(AccountCard());
        layout.Children.Add(NetworkCard());

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        _applyButton = new Button { Content = "Close", IsDefault = true, Classes = { "primary" } };
        _applyButton.Click += (_, _) =>
        {
            if (CountPendingChanges() == 0) { Close(); return; }
            Save();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, _applyButton },
        };

        var bar = new Border
        {
            Background = Brush("SurfaceBar"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 12),
            Child = buttons,
        };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = layout });

        WatchForChanges();
        RefreshApplyButton();

        return root;
    }

    // ---------------------------------------------------------------- account

    private Control AccountCard()
    {
        _accountPanel = new StackPanel { Spacing = 10 };
        ShowAccount();

        return Card("Your community account",
            "Optional. Published translations can be taken without one — this is for reaching your "
            + "own work, and for sharing what you translate.",
            _accountPanel);
    }

    private void ShowAccount()
    {
        _accountPanel.Children.Clear();
        var settings = _store.Current;

        if (settings.SignedIn)
        {
            _accountPanel.Children.Add(Note($"Signed in as {settings.ApiUser ?? "your account"}.",
                "StatusSuccess"));

            var signOut = new Button { Content = "Sign out", FontSize = 12 };
            signOut.Click += (_, _) =>
            {
                // Local only, and said as such: revoking the token server-side would also cut off
                // anything else using it, and that decision belongs on the site.
                settings.ApiToken = null;
                settings.ApiUser = null;
                settings.ApiTokenServer = null;
                _store.Save(settings);
                Saved = true;
                ShowAccount();
            };

            _accountPanel.Children.Add(signOut);
            _accountPanel.Children.Add(Note(
                "Signing out here forgets the token on this machine. To cut it off everywhere, "
                + "revoke it from your account on the site.", "TextMuted"));
            return;
        }

        var signIn = new Button { Content = "Sign in", FontSize = 12, Classes = { "primary" } };
        signIn.Click += async (_, _) => await SignInAsync();
        _accountPanel.Children.Add(signIn);
    }

    /// <summary>
    /// Signs in without ever asking for a password: the site shows a code, you type it there.
    ///
    /// The code stays on screen, selectable, for the whole wait. A dropped stream is no reason to
    /// invalidate a code still good for fifteen minutes, and making someone hunt for it is the one
    /// thing this flow must never cause.
    /// </summary>
    private async Task SignInAsync()
    {
        _signIn?.Cancel();
        _signIn = new CancellationTokenSource();
        var token = _signIn.Token;

        _accountPanel.Children.Clear();
        _accountPanel.Children.Add(new SpinningGear("Asking the site for a code..."));

        var client = new DeviceFlowClient();
        var start = await client.BeginAsync(token);

        if (start is null)
        {
            _accountPanel.Children.Clear();
            _accountPanel.Children.Add(Note(
                "Could not reach the site to start signing in. A firewall or a proxy blocking this "
                + "program looks exactly like this — nothing was changed.", "StatusError"));

            var again = new Button { Content = "Try again", FontSize = 12 };
            again.Click += async (_, _) => await SignInAsync();
            _accountPanel.Children.Add(again);
            return;
        }

        _accountPanel.Children.Clear();
        _accountPanel.Children.Add(Note(
            $"Open {start.VerificationUri} while signed in to your account, and enter this code:",
            "TextSecondary"));

        // A field rather than a label: a code has to be selectable and copyable, and reading one
        // off a screen to retype it is exactly where a character goes missing.
        _accountPanel.Children.Add(new TextBox
        {
            Text = start.UserCode,
            IsReadOnly = true,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        var open = new Button { Content = "Open the page", FontSize = 12 };
        open.Click += (_, _) => OpenUrl(start.VerificationUri);
        _accountPanel.Children.Add(open);

        var waiting = new SpinningGear("Waiting for you to enter it...");
        _accountPanel.Children.Add(waiting);

        var cancel = new Button { Content = "Cancel", FontSize = 12 };
        cancel.Click += (_, _) => { _signIn?.Cancel(); ShowAccount(); };
        _accountPanel.Children.Add(cancel);

        var result = await client.WaitAsync(start.DeviceCode, token);
        if (token.IsCancellationRequested) return;

        if (!result.Authorised)
        {
            waiting.IsVisible = false;
            cancel.Content = "Start over";
            if (result.Failure is not null) _accountPanel.Children.Add(Note(result.Failure, "StatusWarning"));
            return;
        }

        var settings = _store.Current;
        settings.ApiToken = result.AccessToken;
        settings.ApiUser = result.UserName;

        // Recorded so the token is dropped if this tool is ever pointed at another instance.
        settings.ApiTokenServer = BuildInfo.ApiBaseUrl;
        _store.Save(settings);

        Saved = true;
        ShowAccount();
    }

    // ---------------------------------------------------------------- network

    private Control NetworkCard()
    {
        _proxyMode = new ComboBox { Width = 260 };
        _proxyMode.Items.Add(new ComboBoxItem { Content = "Normal (whatever this computer uses)", Tag = "default" });
        _proxyMode.Items.Add(new ComboBoxItem { Content = "Follow the system proxy settings", Tag = "system" });
        _proxyMode.Items.Add(new ComboBoxItem { Content = "Never use a proxy", Tag = "none" });
        _proxyMode.Items.Add(new ComboBoxItem { Content = "Use this proxy", Tag = "custom" });
        Select(_proxyMode, _draft.ProxyMode);

        _proxyUrl = new TextBox { Width = 300, Watermark = "http://proxy.company.com:8080", Text = _draft.ProxyUrl ?? "" };
        _proxyUser = new TextBox { Width = 300, Watermark = "only if your proxy asks for it", Text = _draft.ProxyUsername ?? "" };
        _proxyPassword = new TextBox { Width = 300, PasswordChar = '*', Text = _draft.ProxyPassword ?? "" };

        _proxyInGames = new CheckBox
        {
            Content = "Use it in your games too",
            IsChecked = _draft.ProxyInGames,
        };

        _proxyFields = new StackPanel { Spacing = 10, IsVisible = Tag(_proxyMode) == "custom" };
        _proxyFields.Children.Add(Row("Address", _proxyUrl));
        _proxyFields.Children.Add(Row("Username", _proxyUser));
        _proxyFields.Children.Add(Row("Password", _proxyPassword));
        _proxyFields.Children.Add(Note(
            "The password is stored encrypted and tied to this machine, like every other secret here.",
            "TextMuted"));

        _proxyMode.SelectionChanged += (_, _) => ShowProxyFields();

        _netStatus = Note("", "TextMuted");
        _netStatus.IsVisible = false;

        var test = new Button { Content = "Test the connection", FontSize = 12 };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            _netStatus.IsVisible = true;
            _netStatus.Text = "Trying...";
            _netStatus.Foreground = Brush("TextMuted");

            // Applied before testing, not on save: testing anything other than what is on screen
            // answers a question nobody asked. Cancel still restores what was stored.
            SettingsStore.ApplyNetworkSettings(Collect());

            var (ok, detail) = await TestNetworkAsync();
            _netStatus.Text = detail;
            _netStatus.Foreground = Brush(ok ? "StatusSuccess" : "StatusError");
            test.IsEnabled = true;
        };

        _online = new CheckBox { Content = "Use the community catalog", IsChecked = _draft.OnlineMode };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_online);
        panel.Children.Add(Note(
            "Off means this tool never asks the site anything. It does not stop the mod from doing "
            + "so — that switch is under Mod defaults.", "TextMuted"));
        panel.Children.Add(Row("Connection", _proxyMode, test));
        panel.Children.Add(_proxyFields);
        panel.Children.Add(_proxyInGames);
        panel.Children.Add(Note(
            "A proxy is usually a fact about the network, so the same one serves the mod. Untick it "
            + "if your games reach the internet another way.", "TextMuted"));
        panel.Children.Add(_netStatus);

        return Card("Network",
            "Only worth touching if nothing reaches the internet. A company network usually needs a "
            + "proxy here; at home, a firewall prompt is the more likely culprit.",
            panel);
    }

    private void ShowProxyFields()
    {
        var custom = Tag(_proxyMode) == "custom";
        _proxyFields.IsVisible = custom;

        // Passing on "no proxy at all" or "follow the system" is meaningless for a game, which
        // reads its own machine anyway. The box only has something to say about a proxy we name.
        _proxyInGames.IsVisible = custom;
    }

    private static async Task<(bool Ok, string Detail)> TestNetworkAsync()
    {
        try
        {
            using var client = Core.Net.Http.Create(TimeSpan.FromSeconds(15));
            using var response = await client.GetAsync(BuildInfo.CatalogPrimaryBase + "/loaders.json");

            return response.IsSuccessStatusCode
                ? (true, "Connected. Downloads and community translations will work.")
                : (false, $"Reached the server, which answered {(int)response.StatusCode}. "
                        + "A proxy that intercepts requests often does this.");
        }
        catch (Exception ex)
        {
            return (false, Core.Net.Http.Describe(ex, "GitHub"));
        }
    }

    // ---------------------------------------------------------------- saving

    private InstallerSettings Collect()
    {
        _draft.ProxyMode = Tag(_proxyMode) ?? "default";
        _draft.ProxyUrl = string.IsNullOrWhiteSpace(_proxyUrl.Text) ? null : _proxyUrl.Text.Trim();
        _draft.ProxyUsername = string.IsNullOrWhiteSpace(_proxyUser.Text) ? null : _proxyUser.Text.Trim();
        _draft.ProxyPassword = string.IsNullOrWhiteSpace(_proxyPassword.Text) ? null : _proxyPassword.Text;
        _draft.ProxyInGames = _proxyInGames.IsChecked == true;
        _draft.OnlineMode = _online.IsChecked == true;
        return _draft;
    }

    private void Save()
    {
        var settings = _store.Current;
        var edited = Collect();

        settings.ProxyMode = edited.ProxyMode;
        settings.ProxyUrl = edited.ProxyUrl;
        settings.ProxyUsername = edited.ProxyUsername;
        settings.ProxyPassword = edited.ProxyPassword;
        settings.ProxyInGames = edited.ProxyInGames;
        settings.OnlineMode = edited.OnlineMode;

        _store.Save(settings);
        Saved = true;
        Close();
    }

    private IReadOnlyList<string> PendingChanges()
    {
        var saved = _store.Current;
        var changes = new List<string>();

        void Compare(string label, string? now, string? before)
        {
            if ((now ?? "") != (before ?? "")) changes.Add($"{label}: \"{before}\" -> \"{now}\"");
        }

        Compare("proxy mode", Tag(_proxyMode), saved.ProxyMode);
        Compare("proxy address", _proxyUrl.Text, saved.ProxyUrl);
        Compare("proxy username", _proxyUser.Text, saved.ProxyUsername);
        Compare("proxy password", _proxyPassword.Text, saved.ProxyPassword);

        if ((_proxyInGames.IsChecked == true) != saved.ProxyInGames) changes.Add("proxy in games");
        if ((_online.IsChecked == true) != saved.OnlineMode) changes.Add("community catalog");

        return changes;
    }

    private int CountPendingChanges() => PendingChanges().Count;

    private void RefreshApplyButton()
    {
        var changes = PendingChanges();
        _applyButton.Content = changes.Count > 0 ? $"Apply ({changes.Count})" : "Close";

        ToolTip.SetTip(_applyButton, changes.Count > 0
            ? string.Join(Environment.NewLine, changes)
            : "Nothing to save.");
    }

    private void WatchForChanges()
    {
        _proxyMode.SelectionChanged += (_, _) => RefreshApplyButton();

        foreach (var field in new[] { _proxyUrl, _proxyUser, _proxyPassword })
            field.TextChanged += (_, _) => RefreshApplyButton();

        _proxyInGames.IsCheckedChanged += (_, _) => RefreshApplyButton();
        _online.IsCheckedChanged += (_, _) => RefreshApplyButton();

        ShowProxyFields();
    }

    // ---------------------------------------------------------------- helpers

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // No browser we are allowed to start. The address is on screen either way, which is
            // why it is shown as text rather than hidden behind the button.
        }
    }

    private static IBrush? Brush(string key) => Application.Current?.FindResource(key) as IBrush;

    private static string? Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private static void Select(ComboBox box, string? value) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();

    private static TextBlock Note(string text, string colour) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(colour),
    };

    private static Control Row(string label, params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 130,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextMuted"),
        });
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    private static Control Card(string title, string? intro, Control content)
    {
        var body = new StackPanel { Spacing = 10 };

        body.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Foreground = Brush("TextPrimary"),
        });

        if (intro is not null)
        {
            body.Children.Add(new TextBlock
            {
                Text = intro,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });
        }

        body.Children.Add(content);

        return new Border
        {
            Background = Brush("SurfaceCard"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = body,
        };
    }
}
