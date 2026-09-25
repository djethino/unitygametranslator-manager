using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UnityGameTranslator.Manager.Core.Api;
using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Settings;
using UnityGameTranslator.Manager.Core.Update;
using static UnityGameTranslator.Manager.Gui.Ui;

namespace UnityGameTranslator.Manager.Gui;

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
    private SearchPicker _proxyMode = null!;
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

    private SearchPicker _toolChannel = null!;
    private CheckBox _checkToolUpdates = null!;
    private StackPanel _updatePanel = null!;
    private SearchPicker _bepinex6Channel = null!;
    private CheckBox _checkContentUpdates = null!;
    private CheckBox _showLocalCopyNotice = null!;
    private CheckBox _showUnityNotice = null!;
    private SearchPicker _preferMono = null!;
    private SearchPicker _preferIl2cpp = null!;

    /// <summary>
    /// The loader catalog, so this screen can say what each BepInEx 6 channel currently offers.
    /// Null when the window is opened without one — the card then explains that instead of
    /// showing an empty line.
    /// </summary>
    private readonly LoaderCatalogDocument? _catalog;

    /// <summary>
    /// What the main window already found out at startup, so opening this from its notice does not
    /// ask GitHub the same question again — and, when the check failed, lands on the reason rather
    /// than on an empty panel.
    /// </summary>
    private readonly SelfUpdateCheck? _known;

    /// <summary>Kept for the one thing this window says about the machine: where its files live.</summary>
    private readonly IPlatform _platform;

    public bool Saved { get; private set; }

    /// <summary>
    /// The most recent answer about tool updates, or null when nothing was asked here.
    ///
    /// 🔴 **Exists so the notice in the main window can be RECONCILED rather than left as it was.**
    /// That notice is written once, at startup, from a check that may have failed; pressing "Check
    /// now" here and succeeding changed nothing on it, so "Couldn't check for updates" stayed on
    /// screen for the rest of the session — over a machine that had since been told it is up to
    /// date. The recurring shape of that defect in this project: coding the transition instead of
    /// reconciling from the state.
    ///
    /// ⚠ Carried out rather than re-fetched. Asking GitHub a second time when this window closes
    /// would spend a request to learn what it already knows, and would fail on its own for anyone
    /// behind the firewall this whole screen exists to work around.
    /// </summary>
    public SelfUpdateCheck? LastCheck { get; private set; }

    public ToolSettingsWindow(IPlatform platform, SettingsStore store,
                              SelfUpdateCheck? known = null,
                              LoaderCatalogDocument? catalog = null)
    {
        _store = store;
        _platform = platform;
        _known = known;
        _catalog = catalog;

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
            CheckContentUpdates = current.CheckContentUpdates,
            BepInEx6Channel = current.BepInEx6Channel,

            // ⚠ Copied like the rest, or the two boxes open ticked against a stored "read" and the
            // window greets somebody with "Apply (2)" for changes nobody made (2026-09-22).
            LocalCopyNoticeRead = current.LocalCopyNoticeRead,
            UnityDownloadNoticeRead = current.UnityDownloadNoticeRead,
        };

        Title = "UGT Manager settings";

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

        // Named once, at the top, so nobody has to work out which of the programs is meant — and
        // pointing at the other screen, since the two are easy to mistake for each other.
        layout.Children.Add(Intro(
            "Settings for UnityGameTranslator Manager itself. What goes into your games is in Mod defaults."));

        // ⚠ **Ordered by subject, and the order is the argument.** Who you are · what gets
        // updated and when · what gets installed into games · how the network is reached · where
        // this program lives on the disk. A card dropped in wherever it was written — which is how
        // Mod loaders first landed between Updates and Where this tool lives — makes the window a
        // pile rather than a list of decisions.
        layout.Children.Add(AccountCard());
        layout.Children.Add(UpdatesCard());
        layout.Children.Add(LoadersCard());
        layout.Children.Add(NetworkCard());
        layout.Children.Add(HomeCard());
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

        return Card("Account",
            "Optional. Needed to publish translations and to find your own on UGT Website. "
            + "Downloading works without an account.",
            _accountPanel);
    }

    private void ShowAccount()
    {
        _accountPanel.Children.Clear();
        var settings = _store.Current;

        if (settings.SignedIn)
        {
            _accountPanel.Children.Add(Note($"Signed in as {settings.ApiUser ?? "your account"}.",
                Tone.Success));

            // 🔴 The code this access carries on the account's "Linked devices" page. That page
            // names every line "#QKADJN" and offers to rename the machine it belongs to — while the
            // code appeared in no program of ours, so it asked somebody to name a machine nothing
            // let them identify. Reported from production on 2026-08-27.
            //
            // ⚠ Asked for, never stored: it belongs to the token, the site is what knows it, and a
            // copy in the settings would be one more thing able to go stale.
            //
            // ⚠ The row is added when the answer lands, so an unreachable site costs nothing but
            // the absence of a line — never a window that waits.
            var accessToken = settings.ApiToken;
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var codeRow = Note("");
                codeRow.IsVisible = false;
                _accountPanel.Children.Add(codeRow);

                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var code = await new DeviceFlowClient().AccessCodeAsync(accessToken).ConfigureAwait(true);

                    if (string.IsNullOrEmpty(code)) return;

                    codeRow.Text = $"Shown as #{code} in Linked devices on UGT Website.";
                    codeRow.IsVisible = true;
                });
            }

            var signOut = new Button { Content = "Sign out", FontSize = 12 };
            var signOutNote = Note("Signing out also removes this device from your account.");

            signOut.Click += async (_, _) =>
            {
                // ⚠ This used to be local only, on the belief that revoking would "also cut off
                // anything else using it". It would not: the device flow gives this installation
                // its own token, deliberately separate from the mod's — the note on
                // DeviceFlowClient says so. Forgetting it here only left a line on the account that
                // nobody could identify, for ever.
                //
                // 🔴 Local first. Signing out cannot be made to wait on the site being up, or a
                // machine somebody is trying to leave stays signed in because the network is down.
                var token = settings.ApiToken;

                settings.ApiToken = null;
                settings.ApiUser = null;
                settings.ApiTokenServer = null;
                _store.Save(settings);
                Saved = true;
                ShowAccount();

                if (string.IsNullOrWhiteSpace(token)) return;

                if (!await new DeviceFlowClient().RevokeAsync(token).ConfigureAwait(true))
                {
                    // Said, not swallowed: the access is still live and the only way to cut it now
                    // is from the site.
                    _accountPanel.Children.Add(Note(
                        "Signed out, but UGT Website could not be reached. Remove this device in "
                        + "Linked devices on your account.", Tone.Warning));
                }
            };

            _accountPanel.Children.Add(signOut);
            _accountPanel.Children.Add(signOutNote);
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
        _accountPanel.Children.Add(new SpinningGear("Getting a sign-in code..."));

        var client = new DeviceFlowClient();
        var start = await client.BeginAsync(token);

        if (start is null)
        {
            _accountPanel.Children.Clear();
            _accountPanel.Children.Add(Note(
                "Could not reach UGT Website. A firewall or proxy may be blocking UGT Manager.",
                Tone.Error));

            var again = new Button { Content = "Try again", FontSize = 12 };
            again.Click += async (_, _) => await SignInAsync();
            _accountPanel.Children.Add(again);
            return;
        }

        _accountPanel.Children.Clear();
        // The instruction the whole wait depends on: the card's reading size, not a note's.
        _accountPanel.Children.Add(Intro(
            $"Open {start.VerificationUri}, sign in to your account, and enter this code:"));

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

        var open = Glyphs.Button(Glyphs.Site(), "Open page");
        open.Click += (_, _) => OpenUrl(start.VerificationUri);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { code, copy, open },
        };

        _accountPanel.Children.Add(row);

        var waiting = new SpinningGear("Waiting for the code to be entered...");
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
            if (result.Failure is not null) _accountPanel.Children.Add(Note(result.Failure, Tone.Warning));
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
        _proxyMode = new SearchPicker { Width = 310 };
        // The words every browser's connection settings use. "default" leaves .NET to pick up the
        // machine's configuration; "system" reads the system proxy explicitly (Http.cs).
        _proxyMode.Items.Add(new Choice("default", "Automatic"));
        _proxyMode.Items.Add(new Choice("system", "Use system proxy settings"));
        _proxyMode.Items.Add(new Choice("none", "No proxy"));
        _proxyMode.Items.Add(new Choice("custom", "Manual proxy"));
        Select(_proxyMode, _draft.ProxyMode);

        _proxyUrl = new TextBox { Width = 300, Watermark = "http://proxy.company.com:8080", Text = _draft.ProxyUrl ?? "" };
        _proxyUser = new TextBox { Width = 300, Watermark = "Optional", Text = _draft.ProxyUsername ?? "" };
        _proxyPassword = new TextBox { Width = 300, PasswordChar = '*', Text = _draft.ProxyPassword ?? "" };

        _proxyInGames = new CheckBox
        {
            Content = "Also use this proxy in games",
            IsChecked = _draft.ProxyInGames,
        };

        // ⚠ The box and its note sit INSIDE the manual-proxy fields: they only mean something about a
        // proxy we name. The note used to stand outside, and stayed on screen explaining a box that
        // was hidden.
        _proxyFields = new StackPanel { Spacing = 10, IsVisible = Tag(_proxyMode) == "custom" };
        _proxyFields.Children.Add(Row("Address", _proxyUrl));
        _proxyFields.Children.Add(Row("Username", _proxyUser));
        _proxyFields.Children.Add(Row("Password", _proxyPassword));
        _proxyFields.Children.Add(Note("The password is stored encrypted on this machine."));
        _proxyFields.Children.Add(_proxyInGames);
        _proxyFields.Children.Add(Note("Untick it if your games reach the internet another way."));

        _proxyMode.SelectionChanged += (_, _) => ShowProxyFields();

        _netStatus = Note("");
        _netStatus.IsVisible = false;

        var test = new Button { Content = "Test connection", FontSize = 12 };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            Ui.Say(_netStatus, "Testing...");

            // Applied before testing, not on save: testing anything other than what is on screen
            // answers a question nobody asked. Cancel still restores what was stored.
            SettingsStore.ApplyNetworkSettings(Collect());

            var (ok, detail) = await TestNetworkAsync();
            Ui.Say(_netStatus, detail, ok ? Tone.Success : Tone.Error);
            test.IsEnabled = true;
        };

        // ⚠ Named for what it governs, which is not a catalogue. It was "Use the community
        // catalog" and it decides far more than that: whether the site is asked about translations
        // for the games found here, whether the account's own lineages are fetched, which mod
        // loaders and versions have been published, and whether this tool looks for its own
        // updates. Somebody turning off "the community catalog" was turning off the internet, and
        // somebody looking for the internet switch had no reason to open this one.
        //
        // 🔴 **"Work online", because "Work offline" is what every program has called this for
        // thirty years** — mail clients, browsers, word processors. Nobody has to be taught it, and
        // `online`/`offline` are borrowed as-is into most languages, which matters when nothing
        // here is translated.
        //
        // ⚠ A first attempt said "Look things up online". That is a PHRASAL VERB, and this
        // interface is read in a fourth language: it was unreadable, and it broke the plain-English
        // rule while trying to be plain. The status bar says Online/Offline, this box says Work
        // online, the first-run window offers Work online or Stay offline — one word for one thing.
        _online = new CheckBox { Content = "Work online", IsChecked = _draft.OnlineMode };

        // ⚠ What is sent is said (game names or Steam IDs): it is the privacy fact this box decides.
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_online);
        panel.Children.Add(Note(
            "On: UGT Manager asks UGT Website for translations of your games (it sends their names "
            + "or Steam IDs) and checks for new versions. Off: nothing is sent. Finding games, "
            + "installing UGT Mod and managing local files still work."));
        panel.Children.Add(Note("This does not affect UGT Mod in games. That setting is in Mod defaults."));
        panel.Children.Add(Row("Connection", _proxyMode, test));
        panel.Children.Add(Note(
            "Change it only if UGT Manager cannot connect. Company networks often need a proxy."));
        panel.Children.Add(_proxyFields);
        panel.Children.Add(_netStatus);

        return Card("Network", null, panel);
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
                : (false, $"The server answered with error {(int)response.StatusCode}. "
                        + "A proxy may be blocking the request.");
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
    /// <summary>
    /// Which BepInEx 6 builds go into games. The third channel on this screen, and a third risk.
    ///
    /// 🔴 **Neither option is called "stable", because BepInEx 6 has none.** Its GitHub page
    /// stopped at a pre-release in August 2024 while development continued in Bleeding Edge
    /// builds. Writing "stable" on a two-year-old pre-release would promise a guarantee nobody is
    /// offering — the choice here is between old-and-published and recent-and-untested, and the
    /// words say exactly that.
    ///
    /// ⚠ The dates are READ from each publisher, not written here. A hardcoded "August 2024" is
    /// a fact that rots; and the whole point of this change is that versions stop being typed by
    /// hand. Fetched once when the screen opens — two requests, and only for somebody who came
    /// looking at loader settings.
    /// </summary>
    private Control LoadersCard()
    {
        var panel = new StackPanel { Spacing = 10 };

        _bepinex6Channel = new SearchPicker { Width = 320 };
        _bepinex6Channel.Items.Add(new Choice("be", "Bleeding Edge"));
        _bepinex6Channel.Items.Add(new Choice("github", "GitHub release"));
        Select(_bepinex6Channel, _draft.BepInEx6Channel);

        panel.Children.Add(Row("BepInEx 6 builds", _bepinex6Channel));

        // 🔴 **Which loader is offered first, and it had no answer at all.** The order comes from
        // an integer in the catalog — BepInEx 5 on Mono, BepInEx 6 on IL2CPP — which is a sound
        // default and was the ONLY one: somebody who has settled on MelonLoader had to say so on
        // every game, one card at a time, for ever.
        //
        // ⚠ Two lists because the two runtimes have different candidates, and a single "preferred
        // loader" would offer MelonLoader for IL2CPP and BepInEx 5 for it as if they were
        // interchangeable. Empty means the catalog decides — the honest default, and the one that
        // keeps following our judgement as it changes.
        _preferMono = LoaderChoice("mono");
        _preferIl2cpp = LoaderChoice("il2cpp");

        panel.Children.Add(Row("Prefer on Mono games", _preferMono));
        panel.Children.Add(Row("Prefer on IL2CPP games", _preferIl2cpp));
        // ⚠ No note under these. "Reorders what each game offers; it never forces" explained how
        // the field works — mechanics the reader did not ask about and cannot act on. What a
        // preference does is what the word means; a loader that cannot host a game not being
        // offered is not news to anybody.
        // ⚠ Kept, and it is the reason this field exists: BepInEx 6 has no stable release, so
        // neither option is the safe one and the reader has to be told.
        panel.Children.Add(Note(
            "BepInEx 6 has no stable release. Its GitHub page stopped in 2024; Bleeding Edge is "
            + "where development continues. UGT Mod works with either."));
        // The one fact worth a line: it is not retroactive. Which loaders it concerns is
        // answered by the label on the field itself.
        panel.Children.Add(Note("Games already set up are not changed."));

        // 🔴 **The two source warnings, said in full once, and here to see them again** (user,
        // 2026-09-21: « dans settings on laisse la possibilité de cocher/décocher ? »). Ticked means
        // the next confirmation shows the warning in full; accepting one unticks it — the same
        // flags LocalCopies.WithNoticesRead reads. Built like the two checkboxes of Updates.
        _showLocalCopyNotice = new CheckBox
        {
            Content = "Show the full warning before copying libraries from another game",
            IsChecked = !_draft.LocalCopyNoticeRead,
            FontSize = 12,
        };
        panel.Children.Add(_showLocalCopyNotice);

        _showUnityNotice = new CheckBox
        {
            Content = "Show Unity's terms before downloading libraries from Unity",
            IsChecked = !_draft.UnityDownloadNoticeRead,
            FontSize = 12,
        };
        panel.Children.Add(_showUnityNotice);
        panel.Children.Add(Note("Each is shown in full once. After that, only the source is named."));

        // Filled in as the answers arrive, so the screen is readable before the network is.
        var dates = Note("Checking the latest BepInEx 6 versions...");
        panel.Children.Add(dates);

        _ = ShowChannelDatesAsync(dates);

        return Card("Mod loaders", null, panel);
    }

    /// <summary>
    /// The loaders that can host one runtime, plus "let the catalog decide".
    ///
    /// ⚠ Built from the catalog, never from a list written here: a loader added tomorrow appears
    /// on its own, and one withdrawn stops being offered. The empty entry comes first because it
    /// is the default and the answer most people should keep.
    /// </summary>
    private SearchPicker LoaderChoice(string runtime)
    {
        var box = new SearchPicker { Width = 320 };
        // "Automatic": the catalog's own order decides, and "catalog" is a word of ours.
        box.Items.Add(new Choice("", "Automatic"));

        foreach (var loader in _catalog?.Loaders ?? new List<LoaderDescriptor>())
        {
            if (!loader.Runtimes.Contains(runtime, StringComparer.OrdinalIgnoreCase)) continue;
            box.Items.Add(new Choice(loader.Id, loader.Display));
        }

        // ⚠ Select already falls back to the first row rather than to nothing — "let the catalog
        // decide" here, which is the right answer for a preference nobody has expressed.
        Select(box, runtime == "il2cpp" ? _draft.PreferredLoaderIl2cpp : _draft.PreferredLoaderMono);

        return box;
    }

    /// <summary>
    /// What each BepInEx 6 channel offers right now, side by side, so the gap between them is a
    /// fact on screen rather than something to go and check.
    /// </summary>
    private async Task ShowChannelDatesAsync(TextBlock target)
    {
        var loader = _catalog?.Loaders.FirstOrDefault(
            l => l.Id.StartsWith("bepinex6", StringComparison.OrdinalIgnoreCase));

        if (loader is null)
        {
            Ui.Say(target, "BepInEx 6 is missing from the loader list.", Tone.Warning);
            return;
        }

        var resolver = new LoaderBuildResolver();
        var lines = new List<string>();

        foreach (var channel in new[] { "be", "github" })
        {
            var found = await resolver.ResolveAsync(loader, channel, count: 1).ConfigureAwait(true);
            var build = found[0];

            lines.Add(build.IsPinnedFallback
                ? $"{Label(channel)}: could not be reached"
                : $"{Label(channel)}: {build.Describe()}");
        }

        target.Text = string.Join("   ·   ", lines);

        static string Label(string channel) =>
            channel == "be" ? "Bleeding Edge" : "GitHub release";
    }

    private Control UpdatesCard()
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = $"Current version: {SelfUpdater.CurrentVersion}",
            FontSize = 12,
            Foreground = Brush("TextPrimary"),
        });

        // Only ever shown by a build compiled against something other than GitHub. Without it, such
        // a build reports "could not reach the release list" and every reader — including the
        // people who made it — reads a firewall problem.
        if (SelfUpdater.UnusualReleaseHost is { } host)
        {
            panel.Children.Add(Note(
                $"This build checks for updates at {host} instead of GitHub (self-hosted or test build).",
                Tone.Warning));
        }

        // The same two words as UGT Mod's channel in Mod defaults (ModSettingControls.ChannelPicker).
        _toolChannel = new SearchPicker { Width = 220 };
        _toolChannel.Items.Add(new Choice("stable", "Stable"));
        _toolChannel.Items.Add(new Choice("beta", "Beta (test releases)"));
        Select(_toolChannel, _draft.ToolChannel);

        // Separate from UGT Mod's channel on purpose: a beta of this program misbehaves while you
        // watch it, a beta of the mod meets you mid-game.
        panel.Children.Add(Row("Update channel", _toolChannel));
        panel.Children.Add(Note("For UGT Manager only. UGT Mod has its own update channel in Mod defaults."));

        _checkToolUpdates = new CheckBox
        {
            Content = "Check for UGT Manager updates at startup",
            IsChecked = _draft.CheckToolUpdates,
            FontSize = 12,
        };
        panel.Children.Add(_checkToolUpdates);

        // ⚠ Its own answer, beside the other, because it is a different cost and a different
        // subject: this one is two or three requests about what would go INTO games, made before
        // anybody asked for anything. Somebody on a metered connection can want one and not the
        // other — the same reasoning that keeps the two update channels apart.
        _checkContentUpdates = new CheckBox
        {
            Content = "Check for UGT Mod and loader updates at startup",
            IsChecked = _draft.CheckContentUpdates,
            FontSize = 12,
        };
        panel.Children.Add(_checkContentUpdates);
        panel.Children.Add(Note("Without it, game pages cannot show which version would be installed."));

        var check = new Button { Content = "Check now", FontSize = 12 };
        check.HorizontalAlignment = HorizontalAlignment.Left;
        check.Click += async (_, _) => await CheckForUpdateAsync(check);
        panel.Children.Add(check);

        _updatePanel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_updatePanel);

        // Opened from the main window's notice: it already asked, so show the answer rather than
        // making someone press a button to be told what they were just told.
        if (_known is not null) ShowResult(_known);

        return Card("Updates", null, panel);
    }

    /// <summary>
    /// Puts a check result on screen — and records it, so the window that opened this one can put
    /// its own notice back in step when this closes. See <see cref="LastCheck"/>.
    /// </summary>
    private void ShowResult(SelfUpdateCheck result)
    {
        // ⚠ Recorded before anything is drawn, so it holds even if a branch below returns early.
        // ⚠ And recorded here rather than in CheckForUpdateAsync, because this is the one place
        // every path goes through — the button, and the result handed in by the main window.
        LastCheck = result;

        switch (result.State)
        {
            case SelfUpdateState.Available when result.Offer is not null:
                ShowOffer(result.Offer);
                break;

            case SelfUpdateState.UpToDate:
                _updatePanel.Children.Clear();
                _updatePanel.Children.Add(Note(result.Message ?? "UGT Manager is up to date.", Tone.Success));
                break;

            default:
                // Failing to look is not the same as having nothing to find, and it never reads as
                // if it were. The Network card is named — it is further down — and Check now is the
                // way back.
                _updatePanel.Children.Clear();
                _updatePanel.Children.Add(Note(
                    (result.Message ?? "Could not check for updates.")
                    + " A firewall, antivirus or proxy may be blocking UGT Manager. Allow it, then "
                    + "click Check now, or set a proxy under Network.", Tone.Error));
                break;
        }
    }

    private async Task CheckForUpdateAsync(Button trigger)
    {
        _updatePanel.Children.Clear();

        var waiting = new SpinningGear("Checking for updates...");
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

        var headline = $"Version {offer.NewVersion} is available"
                       + (offer.IsPrerelease ? " (beta)" : "")
                       + (offer.PublishedAt is { } date ? $", released {date:d MMMM yyyy}" : "")
                       + ".";

        _updatePanel.Children.Add(new TextBlock
        {
            Text = headline,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextPrimary"),
        });

        var notes = new Button { Content = "Release notes", FontSize = 12 };
        notes.Click += (_, _) => OpenUrl(offer.ReleasePageUrl);

        // Said before anything is downloaded rather than after: someone keeping the tool where they
        // cannot write has nothing to gain from fifty megabytes first.
        var updater = new SelfUpdater(_platform);
        if (updater.WhyCannotApply() is { } blocked)
        {
            _updatePanel.Children.Add(Note(blocked, Tone.Warning));
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

        // Where a failure is said once the veil is down — beside the button that can try again.
        var progress = Note("");

        // 🔴 Subscribed ONCE, outside the click. It was added inside it, so every retry after a
        // failure stacked one more handler and each chunk was reported that many times over.
        //
        // ⚠ The veil is asked for at the moment of use, never here: this card is built inside
        // Build(), before the window's Content is set, and wrapping it now would wrap nothing.
        updater.Progress += (done, total) => Dispatcher.UIThread.Post(() =>
            WorkOverlay.On(this).Report(total is { } t
                ? $"Downloading... {done / 1024d / 1024:F0} of {t / 1024d / 1024:F0} MB"
                : $"Downloading... {done / 1024d / 1024:F0} MB"));

        apply.Click += async (_, _) =>
        {
            // The window darkened for the whole update: it replaces the program itself, and nothing
            // in this window is worth pressing while it does. Same veil as the games' installs.
            var veil = WorkOverlay.On(this);
            veil.Show($"Updating UGT Manager to {offer.NewVersion}...");

            try
            {
                // Off this thread: the download yields, but the checksum over the archive and its
                // extraction run wherever the last await left them.
                var result = await Task.Run(() => updater.ApplyAsync(offer));

                _updatePanel.Children.Clear();
                // The version in use is kept beside the new one until the restart (SelfUpdater).
                _updatePanel.Children.Add(Note(
                    $"Updated to {result.Version}. Restart UGT Manager to use it.", Tone.Success));
            }
            catch (Exception ex)
            {
                Ui.Say(progress, ex.Message, Tone.Error);
            }
            finally
            {
                veil.Hide();
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { apply, notes },
        };

        _updatePanel.Children.Add(buttons);
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

            panel.Children.Add(Note(plan.SourceExecutable));

            if (plan.Refusal is { } refusal)
            {
                panel.Children.Add(Note(refusal, Tone.Warning));
            }
            else
            {
                var keep = new Button { Content = "Install on this computer", FontSize = 12 };
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

            return Card("Installation", null, panel);
        }

        var state = installer.Inspect();

        panel.Children.Add(new TextBlock
        {
            Text = state.NeedsRepair
                ? "Installed on this computer, but some files are missing."
                : installer.RunningTheInstalledCopy()
                    ? "Installed on this computer."
                    : "Installed on this computer, but you are running another copy.",
            FontSize = 12,
            Foreground = Brush("TextPrimary"),
        });

        panel.Children.Add(Note(installed.Directory));

        // Named one by one, because "something is missing" leaves nobody able to judge whether it
        // matters to them.
        if (state.NeedsRepair)
        {
            panel.Children.Add(Note("Missing: " + string.Join(", ", state.Missing), Tone.Warning));

            var repair = new Button { Content = "Repair...", FontSize = 12 };
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
                "Changes and updates apply to the copy you are running, not to the installed one.",
                Tone.Warning));
        }

        var remove = new Button { Content = "Uninstall...", FontSize = 12 };
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        remove.Click += async (_, _) =>
        {
            var window = new SelfRemoveWindow(_platform, installer);
            await window.ShowDialog(this);
            if (window.Removed) Rebuild();
        };

        panel.Children.Add(remove);

        return Card("Installation", null, panel);
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

        // ⚠ The backups are named: deleting the folder resets the tool AND loses them, and that is
        // the one consequence somebody about to delete it must read.
        panel.Children.Add(Intro(
            "UGT Manager keeps all its data in this folder: settings, added folders, choices made "
            + "for each game, cached lists and translation backups. Deleting it resets UGT Manager "
            + "and deletes those backups. Your games are not changed."));

        panel.Children.Add(Note(folder));

        var open = Glyphs.Button(Glyphs.Folder(), "Open folder");
        open.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        open.Click += (_, _) => Shell.OpenFolder(folder);

        panel.Children.Add(open);

        return Card("Data folder", null, panel);
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
        _draft.CheckContentUpdates = _checkContentUpdates.IsChecked == true;
        _draft.BepInEx6Channel = Tag(_bepinex6Channel) ?? "be";
        _draft.LocalCopyNoticeRead = _showLocalCopyNotice.IsChecked != true;
        _draft.UnityDownloadNoticeRead = _showUnityNotice.IsChecked != true;

        // Empty means "let the catalog decide", and it is stored as null rather than as "": a
        // blank string would read as an answer nobody gave.
        _draft.PreferredLoaderMono = Blank(Tag(_preferMono));
        _draft.PreferredLoaderIl2cpp = Blank(Tag(_preferIl2cpp));
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
        settings.CheckContentUpdates = edited.CheckContentUpdates;
        settings.BepInEx6Channel = edited.BepInEx6Channel;
        settings.LocalCopyNoticeRead = edited.LocalCopyNoticeRead;
        settings.UnityDownloadNoticeRead = edited.UnityDownloadNoticeRead;
        settings.PreferredLoaderMono = edited.PreferredLoaderMono;
        settings.PreferredLoaderIl2cpp = edited.PreferredLoaderIl2cpp;

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

        Compare("UGT Manager update channel", Tag(_toolChannel), saved.ToolChannel);
        Compare("BepInEx 6 builds", Tag(_bepinex6Channel), saved.BepInEx6Channel);
        Compare("preferred loader on Mono", Blank(Tag(_preferMono)), saved.PreferredLoaderMono);
        Compare("preferred loader on IL2CPP", Blank(Tag(_preferIl2cpp)), saved.PreferredLoaderIl2cpp);

        if ((_proxyInGames.IsChecked == true) != saved.ProxyInGames) changes.Add("proxy in games");
        if ((_online.IsChecked == true) != saved.OnlineMode) changes.Add("work online");
        if ((_checkToolUpdates.IsChecked == true) != saved.CheckToolUpdates)
            changes.Add("check for UGT Manager updates");
        if ((_checkContentUpdates.IsChecked == true) != saved.CheckContentUpdates)
            changes.Add("check for UGT Mod and loader updates");
        if ((_showLocalCopyNotice.IsChecked != true) != saved.LocalCopyNoticeRead)
            changes.Add("warning before copying from another game");
        if ((_showUnityNotice.IsChecked != true) != saved.UnityDownloadNoticeRead)
            changes.Add("Unity's terms before downloading");

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

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private void WatchForChanges()
    {
        _proxyMode.SelectionChanged += (_, _) => RefreshApplyButton();

        foreach (var field in new[] { _proxyUrl, _proxyUser, _proxyPassword })
            field.TextChanged += (_, _) => RefreshApplyButton();

        _proxyInGames.IsCheckedChanged += (_, _) => RefreshApplyButton();
        _online.IsCheckedChanged += (_, _) => RefreshApplyButton();
        _toolChannel.SelectionChanged += (_, _) => RefreshApplyButton();
        _checkToolUpdates.IsCheckedChanged += (_, _) => RefreshApplyButton();
        _checkContentUpdates.IsCheckedChanged += (_, _) => RefreshApplyButton();
        _showLocalCopyNotice.IsCheckedChanged += (_, _) => RefreshApplyButton();
        _showUnityNotice.IsCheckedChanged += (_, _) => RefreshApplyButton();

        // ⚠ Every control on this window belongs in this list, and one missing does not break
        // saving — Collect and Save read it either way — it breaks the PROMISE: nothing is applied
        // without pressing Apply, and Apply says how much is waiting. A control whose change is
        // never counted looks either already saved or ignored, and both readings are wrong.
        _bepinex6Channel.SelectionChanged += (_, _) => RefreshApplyButton();
        _preferMono.SelectionChanged += (_, _) => RefreshApplyButton();
        _preferIl2cpp.SelectionChanged += (_, _) => RefreshApplyButton();

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

    private static string? Tag(SearchPicker box) => ModSettingControls.Tag(box);

    private static void Select(SearchPicker box, string? value) =>
        ModSettingControls.Select(box, value);
}
