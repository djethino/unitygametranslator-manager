using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UnityGameTranslator.Installer.Core.Api;
using UnityGameTranslator.Installer.Core.Install;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;
using UnityGameTranslator.Installer.Core.Settings;
using UnityGameTranslator.Installer.Core.Update;

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
    private TextBlock _saved = null!;
    private CancellationTokenSource? _signIn;

    private ComboBox _toolChannel = null!;
    private CheckBox _checkToolUpdates = null!;
    private StackPanel _updatePanel = null!;

    /// <summary>
    /// What the main window already found out at startup, so opening this from its notice does not
    /// ask GitHub the same question again — and, when the check failed, lands on the reason rather
    /// than on an empty panel.
    /// </summary>
    private readonly SelfUpdateCheck? _known;

    /// <summary>Kept for the one thing this window says about the machine: where its files live.</summary>
    private readonly IPlatform _platform;

    public bool Saved { get; private set; }

    public ToolSettingsWindow(IPlatform platform, SettingsStore store,
                              SelfUpdateCheck? known = null)
    {
        _store = store;
        _platform = platform;
        _known = known;

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
            ToolChannel = current.ToolChannel,
            CheckToolUpdates = current.CheckToolUpdates,
        };

        Title = "Settings — this tool";

        // Tall enough for both cards without a scrollbar in the ordinary case: a bar that appears
        // to reveal two lines is more noticeable than the two lines are worth. The scroller stays
        // for the cases that do overflow — a proxy expanded to four fields, a long error.
        Width = 780;
        Height = 780;
        MinWidth = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.FindResource("SurfaceBase") as IBrush;

        Content = Build();
    }

    private Control Build()
    {
        var layout = new StackPanel { Spacing = 16, Margin = new Thickness(24) };

        // Named once, at the top, so every card below can go on saying "this tool" without anyone
        // having to work out which of the two programs is meant.
        layout.Children.Add(new TextBlock
        {
            Text = "These are about UnityGameTranslator Manager itself — this program. What gets "
                 + "written into your games is under Mod defaults.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        layout.Children.Add(AccountCard());
        layout.Children.Add(UpdatesCard());
        layout.Children.Add(HomeCard());
        layout.Children.Add(NetworkCard());
        layout.Children.Add(FilesCard());

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        // ⚠ Applying does NOT close the window, and that is the correction. Saving and closing in
        // one gesture answers a question nobody asked: someone who presses Apply is saying "keep
        // this", not "keep this and I am done here" — and being thrown out mid-way through a screen
        // they were still reading turns one settled decision into a lost place.
        //
        // What tells them it worked is the button itself: it counts what is pending, so going from
        // "Apply (3)" back to "Close" IS the receipt. The line beside it says so in words, because a
        // label changing is easy to miss when you were looking at the setting you just changed.
        //
        // 🔸 The same shape exists in the Mod defaults window. Change one, change the other.
        _saved = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("StatusSuccess"),
            IsVisible = false,
        };

        _applyButton = new Button { Content = "Close", IsDefault = true, Classes = { "primary" } };
        _applyButton.Click += (_, _) =>
        {
            if (CountPendingChanges() == 0) { Close(); return; }
            Save();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _saved, cancel, _applyButton },
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

        // A field rather than a label: a code has to be selectable, and reading one off a screen
        // to retype it is exactly where a character goes missing.
        var code = new TextBox
        {
            Text = start.UserCode,
            IsReadOnly = true,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Width = 200,

            // Centred: this is a short string somebody reads character by character to type it
            // elsewhere, not a field they are filling in. Left-aligned it sat against one edge of
            // a box twice its width.
            TextAlignment = TextAlignment.Center,
        };

        var copy = Glyphs.Button(Glyphs.Clipboard(), "Copy");
        copy.Click += async (_, _) =>
        {
            // Selecting six characters by hand and hitting Ctrl+C is work nobody should do when a
            // button can. The confirmation matters as much as the copy: without it there is no way
            // to tell it happened, and people copy twice to be sure.
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;

            await clipboard.SetTextAsync(start.UserCode);

            Glyphs.SetLabel(copy, "Copied");
            await Task.Delay(1500);
            Glyphs.SetLabel(copy, "Copy");
        };

        var open = Glyphs.Button(Glyphs.Site(), "Open the page");
        open.Click += (_, _) => OpenUrl(start.VerificationUri);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { code, copy, open },
        };

        _accountPanel.Children.Add(row);

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
        _proxyMode = new ComboBox { Width = 310 };
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

    /// <summary>
    /// Where this tool keeps its own files, with a way to go there.
    ///
    /// Everything it writes about itself is in one folder and nowhere else, which is worth both
    /// saying and showing: someone who wants to start clean, or to hand a settings file to
    /// support, or simply to leave nothing behind when they are done with the tool, should not
    /// have to be told a path by a stranger on a forum.
    ///
    /// The path is written out as well as opened, because a machine where the file manager cannot
    /// be started is exactly the machine where the path matters most.
    /// </summary>
    // ---------------------------------------------------------------- updates

    /// <summary>
    /// This tool's own updates.
    ///
    /// Its channel sits here rather than beside the mod's, because it is a different bet: a beta of
    /// this program misbehaves while you are watching it, a beta of the mod is loaded into every
    /// game you play and meets you mid-session.
    ///
    /// Nothing is ever applied without the button being pressed. Until there is a signing
    /// certificate, a program that rewrites itself unannounced is indistinguishable — to a person
    /// and to their antivirus — from something they would want stopped.
    /// </summary>
    private Control UpdatesCard()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = $"You are running {SelfUpdater.CurrentVersion}.",
            FontSize = 12,
            Foreground = Brush("TextPrimary"),
        });

        // Only ever shown by a build compiled against something other than GitHub. Without it, such
        // a build reports "could not reach the release list" and every reader — including the
        // people who made it — reads a firewall problem.
        if (SelfUpdater.UnusualReleaseHost is { } host)
        {
            panel.Children.Add(Note(
                $"This build looks for its updates at {host}, not at GitHub. That is either a "
                + "self-hosted setup or a build made for testing.", "StatusWarning"));
        }

        _toolChannel = new ComboBox
        {
            Width = 220,
            ItemsSource = new[]
            {
                new ComboBoxItem { Content = "Stable releases", Tag = "stable" },
                new ComboBoxItem { Content = "Also test builds (beta)", Tag = "beta" },
            },
        };
        Select(_toolChannel, _draft.ToolChannel);

        panel.Children.Add(Row("Updates", _toolChannel));
        panel.Children.Add(Note(
            "This is about UnityGameTranslator Manager itself. Which build of the mod goes into "
            + "your games is under Mod defaults, and the two are separate on purpose.", "TextMuted"));

        _checkToolUpdates = new CheckBox
        {
            Content = "Look for a new version when the tool starts",
            IsChecked = _draft.CheckToolUpdates,
            FontSize = 12,
        };
        panel.Children.Add(_checkToolUpdates);

        var check = new Button { Content = "Check now", FontSize = 12 };
        check.HorizontalAlignment = HorizontalAlignment.Left;
        check.Click += async (_, _) => await CheckForUpdateAsync(check);
        panel.Children.Add(check);

        _updatePanel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_updatePanel);

        // Opened from the main window's notice: it already asked, so show the answer rather than
        // making someone press a button to be told what they were just told.
        if (_known is not null) ShowResult(_known);

        return Card("Updating this tool", null, panel);
    }

    private void ShowResult(SelfUpdateCheck result)
    {
        switch (result.State)
        {
            case SelfUpdateState.Available when result.Offer is not null:
                ShowOffer(result.Offer);
                break;

            case SelfUpdateState.UpToDate:
                _updatePanel.Children.Clear();
                _updatePanel.Children.Add(Note(result.Message ?? "You are up to date.", "StatusSuccess"));
                break;

            default:
                // Failing to look is not the same as having nothing to find, and it never reads as
                // if it were. The network card is right below, and Check now is the way back.
                _updatePanel.Children.Clear();
                _updatePanel.Children.Add(Note(
                    (result.Message ?? "Could not check.")
                    + " A firewall, an antivirus or a company proxy blocking this tool looks "
                    + "exactly like this. Let it through and press Check now, or set up a proxy "
                    + "below.", "StatusError"));
                break;
        }
    }

    private async Task CheckForUpdateAsync(Button trigger)
    {
        _updatePanel.Children.Clear();

        var waiting = new SpinningGear("Asking GitHub what the latest version is...");
        _updatePanel.Children.Add(waiting);
        trigger.IsEnabled = false;

        // The channel as it stands on screen, not as it was last saved: someone who just switched
        // to beta and pressed Check expects to be told about betas. It is an action, not a setting,
        // so it does not wait for Apply.
        var channel = string.Equals(Tag(_toolChannel), "beta", StringComparison.OrdinalIgnoreCase)
            ? ReleaseChannel.Beta
            : ReleaseChannel.Stable;

        var updater = new SelfUpdater(_platform);
        var result = await updater.CheckAsync(channel);

        _updatePanel.Children.Remove(waiting);
        trigger.IsEnabled = true;

        ShowResult(result);
    }

    private void ShowOffer(SelfUpdateOffer offer)
    {
        _updatePanel.Children.Clear();

        var headline = $"Version {offer.NewVersion} is out"
                       + (offer.IsPrerelease ? " (beta)" : "")
                       + (offer.PublishedAt is { } date ? $", published {date:d MMMM yyyy}" : "")
                       + ".";

        _updatePanel.Children.Add(new TextBlock
        {
            Text = headline,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextPrimary"),
        });

        var notes = new Button { Content = "What changed", FontSize = 12 };
        notes.Click += (_, _) => OpenUrl(offer.ReleasePageUrl);

        // Said before anything is downloaded rather than after: someone keeping the tool where they
        // cannot write has nothing to gain from fifty megabytes first.
        var updater = new SelfUpdater(_platform);
        if (updater.WhyCannotApply() is { } blocked)
        {
            _updatePanel.Children.Add(Note(blocked, "StatusWarning"));
            _updatePanel.Children.Add(notes);
            return;
        }

        var size = offer.SizeBytes is { } bytes ? $" ({bytes / 1024d / 1024d:0.#} MB)" : "";
        var apply = new Button
        {
            Content = $"Update to {offer.NewVersion}{size}",
            FontSize = 12,
            Classes = { "primary" },
        };

        var progress = Note("", "TextMuted");
        var working = new SpinningGear("Downloading...") { IsVisible = false };

        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            working.IsVisible = true;

            updater.Progress += (done, total) => Dispatcher.UIThread.Post(() =>
                progress.Text = total is { } t
                    ? $"Downloading... {done / 1024d / 1024:F0} of {t / 1024d / 1024:F0} MB"
                    : $"Downloading... {done / 1024d / 1024:F0} MB");

            try
            {
                var result = await updater.ApplyAsync(offer);

                _updatePanel.Children.Clear();
                _updatePanel.Children.Add(Note(
                    $"Updated to {result.Version}. Close the tool and open it again to run it — "
                    + "the version you were using is kept beside it until then.", "StatusSuccess"));
            }
            catch (Exception ex)
            {
                working.IsVisible = false;
                progress.Text = ex.Message;
                progress.Foreground = Brush("StatusError");
                apply.IsEnabled = true;
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { apply, notes },
        };

        _updatePanel.Children.Add(buttons);
        _updatePanel.Children.Add(working);
        _updatePanel.Children.Add(progress);
    }

    // ---------------------------------------------------------------- where it lives

    /// <summary>
    /// Installed or portable, and the way in and out of both.
    ///
    /// The banner on the overview is the invitation; this is the permanent home for the question,
    /// and the only place removal is offered. Both matter: someone who dismissed the banner still
    /// has somewhere to go, and someone who installed the tool needs a way back that is not
    /// "delete the folder and hope".
    /// </summary>
    private Control HomeCard()
    {
        var panel = new StackPanel { Spacing = 10 };
        var installer = new SelfInstaller(_platform);
        var installed = installer.Installed();

        if (installed is null)
        {
            var plan = installer.Plan();

            panel.Children.Add(new TextBlock
            {
                Text = "Running from the file you downloaded.",
                FontSize = 12,
                Foreground = Brush("TextPrimary"),
            });

            panel.Children.Add(Note(plan.SourceExecutable, "TextMuted"));

            if (plan.Refusal is { } refusal)
            {
                panel.Children.Add(Note(refusal, "StatusWarning"));
            }
            else
            {
                var keep = new Button { Content = "Install it on this machine", FontSize = 12 };
                keep.HorizontalAlignment = HorizontalAlignment.Left;
                keep.Click += async (_, _) =>
                {
                    var window = new SelfInstallWindow(_platform, installer, plan);
                    await window.ShowDialog(this);

                    // Redrawn where it stands: the card has just become the other half of itself.
                    if (window.Installed is not null) Rebuild();
                };

                panel.Children.Add(keep);
            }

            return Card("Where this tool lives", null, panel);
        }

        var state = installer.Inspect();

        panel.Children.Add(new TextBlock
        {
            Text = state.NeedsRepair
                ? "Installed on this machine, but pieces of it are missing."
                : installer.RunningTheInstalledCopy()
                    ? "Installed on this machine, and this is that copy."
                    : "Installed on this machine — but this window is another copy of it.",
            FontSize = 12,
            Foreground = Brush("TextPrimary"),
        });

        panel.Children.Add(Note(installed.Directory, "TextMuted"));

        // Named one by one, because "something is missing" leaves nobody able to judge whether it
        // matters to them.
        if (state.NeedsRepair)
        {
            panel.Children.Add(Note("Missing: " + string.Join(", ", state.Missing), "StatusWarning"));

            var repair = new Button { Content = "Repair the installation...", FontSize = 12 };
            repair.HorizontalAlignment = HorizontalAlignment.Left;
            repair.Click += async (_, _) =>
            {
                var window = new SelfInstallWindow(_platform, installer, installer.Plan());
                await window.ShowDialog(this);
                if (window.Installed is not null) Rebuild();
            };

            panel.Children.Add(repair);
        }

        if (!installer.RunningTheInstalledCopy())
        {
            // Worth saying because it changes what every other button here means: an update applied
            // from this window lands on the file in front of them, not on the one in their menu.
            panel.Children.Add(Note(
                "Anything you change or update here applies to the copy you are running, not to "
                + "the installed one.", "StatusWarning"));
        }

        var remove = new Button { Content = "Remove this tool...", FontSize = 12 };
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        remove.Click += async (_, _) =>
        {
            var window = new SelfRemoveWindow(_platform, installer);
            await window.ShowDialog(this);
            if (window.Removed) Rebuild();
        };

        panel.Children.Add(remove);

        return Card("Where this tool lives", null, panel);
    }

    /// <summary>
    /// Redraws the whole window after something outside the draft has changed on disk.
    ///
    /// Installing or removing the tool is not a setting: it happens immediately and there is
    /// nothing to Apply. What is on screen has to catch up, and rebuilding is honest — every card
    /// reads its own state again rather than one of them being patched by hand.
    /// </summary>
    private void Rebuild() => Content = Build();

    private Control FilesCard()
    {
        var panel = new StackPanel { Spacing = 8 };
        var folder = _platform.UserDataDirectory;

        panel.Children.Add(Note(
            "Everything this tool remembers is in one folder: your settings, the folders you "
            + "added, the games you overruled, the catalogues it caches, and the translations it "
            + "moved aside before replacing one. Deleting it puts the tool back to how it "
            + "arrived — it touches nothing in your games.", "TextSecondary"));

        panel.Children.Add(new TextBlock
        {
            Text = folder,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        });

        var open = Glyphs.Button(Glyphs.Folder(), "Open this folder");
        open.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        open.Click += (_, _) => Shell.OpenFolder(folder);

        panel.Children.Add(open);

        return Card("This tool's files", null, panel);
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
        _draft.ToolChannel = Tag(_toolChannel) ?? "stable";
        _draft.CheckToolUpdates = _checkToolUpdates.IsChecked == true;
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
        settings.ToolChannel = edited.ToolChannel;
        settings.CheckToolUpdates = edited.CheckToolUpdates;

        var count = CountPendingChanges();

        _store.Save(settings);
        Saved = true;

        _saved.Text = count == 1 ? "1 change applied" : $"{count} changes applied";
        _saved.IsVisible = true;

        // Reads the store again, which now matches what is on screen — so the button says "Close".
        RefreshApplyButton();
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

        Compare("this tool's update channel", Tag(_toolChannel), saved.ToolChannel);

        if ((_proxyInGames.IsChecked == true) != saved.ProxyInGames) changes.Add("proxy in games");
        if ((_online.IsChecked == true) != saved.OnlineMode) changes.Add("community catalog");
        if ((_checkToolUpdates.IsChecked == true) != saved.CheckToolUpdates)
            changes.Add("look for updates to this tool");

        return changes;
    }

    private int CountPendingChanges() => PendingChanges().Count;

    private void RefreshApplyButton()
    {
        var changes = PendingChanges();
        _applyButton.Content = changes.Count > 0 ? $"Apply ({changes.Count})" : "Close";

        // The moment something is pending again, what was applied a minute ago is no longer the
        // state of this screen, and saying so would be a lie of omission.
        if (changes.Count > 0) _saved.IsVisible = false;

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
        _toolChannel.SelectionChanged += (_, _) => RefreshApplyButton();
        _checkToolUpdates.IsCheckedChanged += (_, _) => RefreshApplyButton();

        ShowProxyFields();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// ⚠ Through Shell, not around it. This window had a copy of this that went straight to
    /// ShellExecute — and it is the window that opens the sign-in address the SERVER sends back,
    /// which is the one address here that does not come from us. A copy of a helper is a copy of
    /// its door, and this one had no lock on it. The address is shown as text beside the button
    /// either way, so a refusal costs nothing.
    /// </summary>
    private static void OpenUrl(string url) => Shell.OpenUrl(url);

    /// <summary>Through Palette, which will not let an unknown key pass unnoticed.</summary>
    private static IBrush? Brush(string key) => Palette.Of(key);

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
