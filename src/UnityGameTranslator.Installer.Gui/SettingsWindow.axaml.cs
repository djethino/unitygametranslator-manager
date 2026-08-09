using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UnityGameTranslator.Installer.Core.Ai;
using UnityGameTranslator.Installer.Core.Api;
using UnityGameTranslator.Installer.Core.Catalog;
using UnityGameTranslator.Installer.Core.Install;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;
using UnityGameTranslator.Installer.Core.Settings;

namespace UnityGameTranslator.Installer.Gui;

/// <summary>
/// The defaults applied to every game.
///
/// Not a page of knobs for the tool itself: the target language especially is a fact about the
/// person, not a per-game preference, and it is what turns "3 translations available" into "this
/// game is playable in your language".
///
/// Nothing is written until Save. The mod holds the same rule for its own settings, and it is
/// worth keeping across the family: a screen that applies as you click gives you no way to
/// change your mind halfway through.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly IPlatform _platform;
    private readonly SettingsStore _store;
    private readonly AiServerProbe _probe = new();
    private ModelNotesDocument? _modelNotes;

    private readonly InstallerSettings _draft;

    private ComboBox _language = null!;
    private ComboBox _backend = null!;
    private TextBox _aiUrl = null!;
    private ComboBox _aiModel = null!;
    private TextBox _hotkey = null!;
    private TextBlock _hotkeyProblem = null!;
    private ComboBox _channel = null!;
    private CheckBox _online = null!;

    private TextBox _apiKey = null!;
    private TextBlock _metrics = null!;
    private TextBlock _modelNote = null!;
    private StackPanel _ollamaPanel = null!;
    private ComboBox _proxyMode = null!;
    private TextBox _proxyUrl = null!;
    private TextBox _proxyUser = null!;
    private TextBox _proxyPassword = null!;
    private StackPanel _proxyFields = null!;
    private TextBlock _netStatus = null!;
    private Button _connectButton = null!;
    private StackPanel _aiPanel = null!;
    private StackPanel _testOutput = null!;
    private TextBlock _aiStatus = null!;
    private Button _testButton = null!;
    private Button _applyButton = null!;

    /// <summary>
    /// True while the screen is filling itself in rather than being edited.
    ///
    /// The change counter must reflect what the person did, not what the window did to itself.
    /// Discovering a server empties the model list and refills it, and for that instant nothing is
    /// selected — which counts as "different from what is saved" and flashed "Apply (1)" on a
    /// screen nobody had touched. The mod guards its own counter the same way, for the same
    /// reason.
    /// </summary>
    /// <summary>
    /// Starts true, and stays true until the first discovery has finished.
    ///
    /// The window is built before it is shown, so at construction time the model list is still
    /// empty while a model is already saved — an empty selection against a saved value, counted as
    /// a pending change and shown as "Apply (1)" before anything could possibly have been edited.
    /// The screen is not in a state worth counting until it has finished filling itself in.
    /// </summary>
    private bool _populating = true;

    public bool Saved { get; private set; }

    private readonly AiServerMemory _aiServers;

    public SettingsWindow(IPlatform platform, SettingsStore store, AiServerMemory? aiServers = null)
    {
        _platform = platform;
        _store = store;
        _aiServers = aiServers ?? new AiServerMemory();

        // Edited on a copy: Cancel has to mean cancel, including for the language, which the
        // main window reads back on close.
        var current = store.Current;
        _draft = new InstallerSettings
        {
            TargetLanguage = current.TargetLanguage,
            TranslationBackend = current.TranslationBackend,
            AiUrl = current.AiUrl,
            AiModel = current.AiModel,
            EnableAi = current.EnableAi,
            OnlineMode = current.OnlineMode,
            SettingsHotkey = current.SettingsHotkey,
            Channel = current.Channel,
            AiApiKey = current.AiApiKey,
            ProxyMode = current.ProxyMode,
            ProxyUrl = current.ProxyUrl,
            ProxyUsername = current.ProxyUsername,
            ProxyPassword = current.ProxyPassword,
            ProxyBypassLocal = current.ProxyBypassLocal,
            DefaultPosture = current.DefaultPosture,
            Reviewed = current.Reviewed,
        };

        Title = "Settings — defaults for every game";
        Width = 720;
        Height = 760;
        MinWidth = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.FindResource("SurfaceBase") as IBrush;

        Content = Build();
        Opened += async (_, _) => await DiscoverAsync(reuseKnown: true);
    }

    private Control Build()
    {
        var layout = new StackPanel { Spacing = 16, Margin = new Thickness(24) };

        layout.Children.Add(new TextBlock
        {
            Text = "These apply to every game you set up. A game you have already configured is "
                 + "not touched until you ask for it.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        layout.Children.Add(LanguageCard());
        layout.Children.Add(BackendCard());
        layout.Children.Add(AiCard());
        layout.Children.Add(NetworkCard());
        layout.Children.Add(ModCard());

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        // The mod's own wording, and for the same reason: "Apply (3)" answers "did it take what I
        // changed?" before you click, and "Close" says there is nothing to apply — which is worth
        // knowing on a screen where testing a model changes nothing worth saving. Same family of
        // tools, same sentence.
        // Labelled up front rather than left to the first count, which is deliberately suppressed
        // until the screen has settled: an empty button in the meantime would be worse than the
        // wrong number.
        _applyButton = new Button { Content = "Close", IsDefault = true, Classes = { "primary" } };
        _applyButton.Click += (_, _) =>
        {
            if (CountPendingChanges() == 0) { Close(); return; }
            Save();
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(_applyButton);

        // Fixed at the bottom, out of the scroll, like the publisher band on the About screen.
        // A settings page long enough to scroll hides its own confirmation otherwise, and the way
        // to save becomes something you have to go looking for.
        var bar = new Border
        {
            // SurfaceBar, the colour the site gives its own fixed bars. A bar in the page colour
            // would only be told apart by its hairline border, which is not enough to read as
            // "this stays put while the rest scrolls".
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

    // ---------------------------------------------------------------- cards

    private Control LanguageCard()
    {
        _language = new ComboBox { Width = 260 };

        var detected = _platform.SystemLanguage();
        _language.Items.Add(new ComboBoxItem
        {
            Content = detected is not null
                ? $"Follow the system ({Languages.NameOf(detected)})"
                : "Follow the system",
            Tag = "auto",
        });

        foreach (var (code, name) in Languages.All())
            _language.Items.Add(new ComboBoxItem { Content = name, Tag = code });

        Select(_language, _draft.TargetLanguage);

        return Card("The language you play in",
            "Everything else follows from this: which of your games are already playable, and "
            + "what gets written into each game's settings.",
            Row("Target language", _language));
    }

    private Control BackendCard()
    {
        _backend = new ComboBox { Width = 260 };
        _backend.Items.Add(new ComboBoxItem { Content = "Community translations only", Tag = "none" });
        _backend.Items.Add(new ComboBoxItem { Content = "Translate with an AI (yours, or an online one)", Tag = "ai" });
        _backend.Items.Add(new ComboBoxItem { Content = "Google Translate (your key)", Tag = "google" });
        _backend.Items.Add(new ComboBoxItem { Content = "DeepL (your key)", Tag = "deepl" });
        Select(_backend, _draft.TranslationBackend);

        _backend.SelectionChanged += (_, _) =>
            _aiPanel.IsVisible = Tag(_backend) == "ai";

        return Card("How lines get translated",
            "A game someone has already translated needs none of this. The rest is for what "
            + "nobody has translated yet: your own machine, free, or a paid service with your own key.",
            Row("Backend", _backend));
    }

    private Control AiCard()
    {
        _aiUrl = new TextBox { Width = 300, Watermark = "http://localhost:11434" };
        _aiUrl.Text = _draft.AiUrl;

        // One field for a server on this machine and for an online provider alike: the mod only
        // ever knows an OpenAI-compatible address, so anything speaking that dialect fits here.
        _apiKey = new TextBox
        {
            Width = 300,
            Watermark = "leave empty for a server on your machine",
            PasswordChar = '*',
            Text = _draft.AiApiKey ?? "",
        };

        _aiModel = new ComboBox { Width = 300 };

        _aiStatus = new TextBlock
        {
            Text = "Looking for a local AI server...",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        };

        var refresh = new Button { Content = "Search again", FontSize = 12 };
        refresh.Click += async (_, _) =>
        {
            _aiServers.Forget();
            await DiscoverAsync();
        };

        _testButton = new Button
        {
            Content = "Test this model",
            FontSize = 12,
            IsEnabled = false,
        };
        _testButton.Click += async (_, _) => await RunSuiteAsync();

        _testOutput = new StackPanel { Spacing = 6 };

        _connectButton = new Button { Content = "Test connection", FontSize = 12 };
        _connectButton.Click += async (_, _) => await TestConnectionAsync();

        _metrics = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Foreground = Brush("TextSecondary"),
        };

        _aiPanel = new StackPanel { Spacing = 10, IsVisible = Tag(_backend) == "ai" };
        _aiPanel.Children.Add(_aiStatus);
        _aiPanel.Children.Add(Row("Server", _aiUrl, refresh));
        _aiPanel.Children.Add(Row("API key", _apiKey, _connectButton));
        _aiPanel.Children.Add(new TextBlock
        {
            // Said plainly, once. We do not pick a provider, we resell nothing, and we are in no
            // position to know what someone will be charged.
            Text = "An online provider bills you directly, on your own account. We take no part in "
                 + "that and cannot be held responsible for what it costs. Some providers do offer "
                 + "free allowances - DeepL through a developer account, OpenRouter on some models - "
                 + "but finding one and reading its terms is yours to do."
                 + Environment.NewLine
                 + "The key is stored encrypted and bound to this machine, so a copy of the file "
                 + "taken elsewhere cannot be read. That protects a file that leaves; it does not "
                 + "protect against something already running as you. Revoking the key at the "
                 + "provider is the real defence.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        });
        _modelNote = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Foreground = Brush("TextMuted"),
        };

        _ollamaPanel = new StackPanel { Spacing = 8, IsVisible = false };
        _aiPanel.Children.Add(_ollamaPanel);

        _aiPanel.Children.Add(Row("Model", _aiModel, _testButton));
        _aiPanel.Children.Add(_modelNote);
        _aiPanel.Children.Add(_metrics);

        // Shown as soon as a model is picked, before any test: knowing that one of the listed
        // models is the one the mod is developed against is worth more than a mark obtained
        // afterwards, because it tells someone where to start rather than judging where they went.
        _aiModel.SelectionChanged += (_, _) => ShowModelNote();
        _aiPanel.Children.Add(new TextBlock
        {
            Text = "The test asks this model to do exactly what the mod asks of it, from easy to "
                 + "hard, and shows you its answers. Our checks are guesses about free text and "
                 + "can be wrong either way — read the answers, and decide for yourself.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        });
        _aiPanel.Children.Add(_testOutput);

        return Card("AI translation", null, _aiPanel);
    }

    /// <summary>
    /// How this tool reaches the internet.
    ///
    /// Here because the situation it addresses is one where nothing else works: behind a company
    /// proxy every catalog fetch, every community lookup and every download fails, all with
    /// messages that point at our servers. Someone in that position needs one place to fix it and
    /// one button to confirm they did — not a tool that fails the same way for the tenth time.
    ///
    /// Same four modes and the same names as the mod, so a proxy configured there is described
    /// the same way here.
    /// </summary>
    private Control NetworkCard()
    {
        _proxyMode = new ComboBox { Width = 260 };
        _proxyMode.Items.Add(new ComboBoxItem { Content = "Normal (whatever this computer uses)", Tag = "default" });
        _proxyMode.Items.Add(new ComboBoxItem { Content = "Follow the system proxy settings", Tag = "system" });
        _proxyMode.Items.Add(new ComboBoxItem { Content = "Never use a proxy", Tag = "none" });
        _proxyMode.Items.Add(new ComboBoxItem { Content = "Use this proxy", Tag = "custom" });
        Select(_proxyMode, _draft.ProxyMode);

        _proxyUrl = new TextBox { Width = 300, Watermark = "http://proxy.company.com:8080" };
        _proxyUrl.Text = _draft.ProxyUrl ?? "";

        _proxyUser = new TextBox { Width = 300, Watermark = "only if your proxy asks for it" };
        _proxyUser.Text = _draft.ProxyUsername ?? "";

        _proxyPassword = new TextBox { Width = 300, PasswordChar = '*' };
        _proxyPassword.Text = _draft.ProxyPassword ?? "";

        _proxyFields = new StackPanel { Spacing = 10, IsVisible = Tag(_proxyMode) == "custom" };
        _proxyFields.Children.Add(Row("Address", _proxyUrl));
        _proxyFields.Children.Add(Row("Username", _proxyUser));
        _proxyFields.Children.Add(Row("Password", _proxyPassword));
        _proxyFields.Children.Add(Note(
            "The password is stored encrypted and tied to this machine, like every other secret "
            + "here.", "TextMuted"));

        _proxyMode.SelectionChanged += (_, _) =>
            _proxyFields.IsVisible = Tag(_proxyMode) == "custom";

        _netStatus = Note("", "TextMuted");
        _netStatus.IsVisible = false;

        var test = new Button { Content = "Test the connection", FontSize = 12 };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            _netStatus.IsVisible = true;
            _netStatus.Text = "Trying...";
            _netStatus.Foreground = Brush("TextMuted");

            // Applied before testing, not on Save: testing the settings someone is looking at is
            // the only thing that answers their question. Cancel still restores the saved ones.
            SettingsStore.ApplyNetworkSettings(DraftWithNetwork());

            var (ok, detail) = await TestNetworkAsync();
            _netStatus.Text = detail;
            _netStatus.Foreground = Brush(ok ? "StatusSuccess" : "StatusError");
            test.IsEnabled = true;
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Row("Connection", _proxyMode, test));
        panel.Children.Add(_proxyFields);
        panel.Children.Add(_netStatus);

        return Card("Network",
            "Only worth touching if nothing reaches the internet. A company network usually needs "
            + "a proxy here; at home, a firewall prompt is the more likely culprit.",
            panel);
    }

    /// <summary>The draft with the network fields as they currently read on screen.</summary>
    private InstallerSettings DraftWithNetwork()
    {
        _draft.ProxyMode = Tag(_proxyMode) ?? "default";
        _draft.ProxyUrl = string.IsNullOrWhiteSpace(_proxyUrl.Text) ? null : _proxyUrl.Text.Trim();
        _draft.ProxyUsername = string.IsNullOrWhiteSpace(_proxyUser.Text) ? null : _proxyUser.Text.Trim();
        _draft.ProxyPassword = string.IsNullOrWhiteSpace(_proxyPassword.Text) ? null : _proxyPassword.Text;
        return _draft;
    }

    /// <summary>
    /// Fetches the catalog to prove the route works end to end.
    ///
    /// GitHub rather than our own site: it is what the tool actually needs to reach to install
    /// anything, and a corporate proxy that allows one may well block the other.
    /// </summary>
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

    private Control ModCard()
    {
        // Same shape as the mod's own HotkeyCapture: three modifier boxes, a "+", and one button
        // that captures the base key. Deliberately identical — it is the same setting in the same
        // ecosystem, and someone who has met one should recognise the other. It also beats
        // capturing the whole combination at once: changing Ctrl for Alt does not mean redoing
        // the capture.
        var initial = _draft.SettingsHotkey ?? Hotkeys.Default;

        var ctrlBox = new CheckBox { Content = "Ctrl", IsChecked = initial.Contains("Ctrl+") };
        var altBox = new CheckBox { Content = "Alt", IsChecked = initial.Contains("Alt+") };
        var shiftBox = new CheckBox { Content = "Shift", IsChecked = initial.Contains("Shift+") };

        var baseKey = Hotkeys.BaseKeyOf(initial);
        var keyButton = new Button { Content = baseKey, MinWidth = 110, FontSize = 12 };

        _hotkeyProblem = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Foreground = Brush("StatusWarning"),
        };

        // Not shown, not editable: the field only exists so Save reads one value from one place.
        _hotkey = new TextBox { IsVisible = false, Text = initial };

        void Recompose()
        {
            var prefix = (ctrlBox.IsChecked == true ? "Ctrl+" : "")
                       + (altBox.IsChecked == true ? "Alt+" : "")
                       + (shiftBox.IsChecked == true ? "Shift+" : "");
            _hotkey.Text = prefix + keyButton.Content;
        }

        ctrlBox.IsCheckedChanged += (_, _) => Recompose();
        altBox.IsCheckedChanged += (_, _) => Recompose();
        shiftBox.IsCheckedChanged += (_, _) => Recompose();

        var capturing = false;

        keyButton.Click += (_, _) =>
        {
            capturing = true;
            keyButton.Content = "Press a key...";
            _hotkeyProblem.IsVisible = false;
            keyButton.Focus();
        };

        keyButton.KeyDown += (_, e) =>
        {
            if (!capturing) return;
            e.Handled = true;

            // Modifiers have their own boxes here, so pressing one alone is not an answer.
            if (e.PhysicalKey is PhysicalKey.ControlLeft or PhysicalKey.ControlRight
                or PhysicalKey.AltLeft or PhysicalKey.AltRight
                or PhysicalKey.ShiftLeft or PhysicalKey.ShiftRight
                or PhysicalKey.MetaLeft or PhysicalKey.MetaRight)
            {
                return;
            }

            capturing = false;

            // The physical position, not the character printed on the key — which is exactly what
            // Unity records too. That is why this holds on any layout and any system: the key left
            // of "1" is BackQuote to both, whether it prints `, ² or ^. Verified against what the
            // mod had actually written into real games.
            var unityName = Hotkeys.FromPhysicalKey(e.PhysicalKey.ToString());

            if (unityName is null)
            {
                // Said, never worked around. Substituting another key silently would leave someone
                // pressing the one they chose and concluding the mod is broken.
                keyButton.Content = Hotkeys.BaseKeyOf(_hotkey.Text ?? Hotkeys.Default);
                _hotkeyProblem.Text = "The mod cannot use that key: Unity has no name for its "
                                    + "position, so it would never respond. Your previous key was kept.";
                _hotkeyProblem.IsVisible = true;
                return;
            }

            keyButton.Content = unityName;
            _hotkeyProblem.IsVisible = false;
            Recompose();
        };

        var hotkeyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        hotkeyRow.Children.Add(ctrlBox);
        hotkeyRow.Children.Add(altBox);
        hotkeyRow.Children.Add(shiftBox);
        hotkeyRow.Children.Add(new TextBlock
        {
            Text = "+",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextMuted"),
        });
        hotkeyRow.Children.Add(keyButton);

        _channel = new ComboBox { Width = 200 };
        _channel.Items.Add(new ComboBoxItem { Content = "Stable", Tag = "stable" });
        _channel.Items.Add(new ComboBoxItem { Content = "Beta (test releases)", Tag = "beta" });
        Select(_channel, _draft.Channel);

        _online = new CheckBox
        {
            Content = "Use the community catalog",
            IsChecked = _draft.OnlineMode,
        };

        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(Row("In-game hotkey", hotkeyRow));
        panel.Children.Add(_hotkey);
        panel.Children.Add(Note(
            "Click the key button, then press the key you want. It is stored by position on the "
            + "keyboard, exactly as the mod reads it - so the key left of \"1\" works whatever "
            + "character your layout prints on it.", "TextMuted"));
        panel.Children.Add(_hotkeyProblem);
        panel.Children.Add(Row("Updates", _channel));
        panel.Children.Add(_online);

        return Card("In the game",
            "The hotkey opens the mod's own panel while you play. It is asked here because the "
            + "mod's first-run wizard asks for it: answer everything and it can be skipped, "
            + "leave anything out and the wizard still runs — we will not pretend to have "
            + "answered on your behalf.",
            panel);
    }

    // ---------------------------------------------------------------- AI

    /// <param name="reuseKnown">
    /// Show what the last search found instead of searching again. True when the window opens:
    /// probing six ports takes a couple of seconds, and nothing about the machine changed while
    /// this dialog was closed. False for "Search again" and after anything we did ourselves.
    /// </param>
    private async Task DiscoverAsync(bool reuseKnown = false)
    {
        _populating = true;
        try
        {
            if (reuseKnown && _aiServers.Remembered is { } known)
            {
                ShowServers(known);
                return;
            }

            await DiscoverCoreAsync();
        }
        finally
        {
            _populating = false;

            // Recounted once the pending input events have been dealt with, not straight away.
            // Rebuilding a list raises its selection change through the dispatcher, so counting
            // here would read a selection that has not landed yet — which is what produced a
            // brief "Apply (1)" on a screen nobody had touched, settling back to "Close" a moment
            // later. Posted at background priority: driven by the queue draining, not by a delay
            // we guessed at.
            Dispatcher.UIThread.Post(RefreshApplyButton, DispatcherPriority.Background);
        }
    }

    private async Task DiscoverCoreAsync()
    {
        _aiStatus.Text = "Looking for a local AI server...";
        _aiModel.Items.Clear();
        _testButton.IsEnabled = false;

        // Fetched alongside the search, never blocking it: a note is a nicety, a server list is
        // the screen's reason to exist. Offline settings mean no note and nothing else missing.
        _modelNotes ??= await new ModelNotesProvider(_platform)
            .GetAsync(offline: !_draft.OnlineMode);

        // An address already configured is asked first, and on its own. Sweeping the local ports
        // regardless would list a local server's models next to an online provider's URL — two
        // things that have nothing to do with each other, shown as if they did. It is also one
        // request instead of six.
        var configured = _aiUrl.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var models = await _probe.ListModelsAsync(configured, _apiKey.Text?.Trim());
            if (models is not null)
            {
                var known = new[] { new AiServer(configured, "Your server", models) };
                _aiServers.Remember(known);
                ShowServers(known);
                return;
            }
        }

        var servers = await _probe.DiscoverAsync();
        _aiServers.Remember(servers);

        ShowServers(servers);
    }

    /// <summary>
    /// Puts a set of servers on screen. Split out so a remembered result and a fresh one produce
    /// exactly the same window — two code paths drawing the same thing is how they drift.
    /// </summary>
    private void ShowServers(IReadOnlyList<AiServer> servers)
    {
        if (servers.Count == 0)
        {
            _aiStatus.Text = "No local AI server answered on the usual ports. "
                           + "One running elsewhere still works — type its address above.";

            // Nothing answered: this is the only moment we are allowed to talk about installing
            // anything. What we offer depends on what is already on the machine, so ask first.
            _ = OfferOllamaAsync();
            return;
        }

        _ollamaPanel.Children.Clear();
        _ollamaPanel.IsVisible = false;

        var server = servers[0];
        if (string.IsNullOrWhiteSpace(_aiUrl.Text)) _aiUrl.Text = server.Url;

        _aiStatus.Text = $"{server.Product} answered at {server.Url} — {server.Models.Count} model(s).";

        // A server with nothing loaded is the state a fresh Ollama is left in, and the one that
        // reads as "it worked" while translating nothing. Offering a model here is the difference
        // between an engine and an engine with fuel.
        if (server.Models.Count == 0)
        {
            _ = OfferModelAsync(server.Url);
            return;
        }

        foreach (var model in server.Models)
            _aiModel.Items.Add(new ComboBoxItem { Content = model, Tag = model });

        Select(_aiModel, _draft.AiModel);
        _aiModel.SelectedItem ??= _aiModel.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _testButton.IsEnabled = _aiModel.SelectedItem is not null;
    }

    /// <summary>
    /// Offers the smallest thing that would fix the situation, and nothing more.
    ///
    /// Three situations, three different answers, and only the last one downloads anything:
    /// an Ollama already installed only needs starting, and a machine with none gets an offer
    /// with the size stated up front. Installing a second Ollama beside a working one would
    /// leave someone with two servers, two model folders and gigabytes duplicated — so the
    /// question "what is already here" is asked before the question "what can we install".
    /// </summary>
    private async Task OfferOllamaAsync()
    {
        _ollamaPanel.Children.Clear();
        _ollamaPanel.IsVisible = true;

        var probe = new OllamaProbe(_platform);
        var status = await probe.InspectAsync();

        if (status.State == OllamaState.Running)
        {
            // Serving but our port scan missed it — nothing to install, and saying otherwise
            // would be the start of a duplicate install.
            _ollamaPanel.IsVisible = false;
            return;
        }

        if (status.State == OllamaState.InstalledButStopped)
        {
            _ollamaPanel.Children.Add(Note(
                "Ollama is installed on this machine but is not running. Nothing to download.",
                "TextSecondary"));

            var start = new Button { Content = "Start Ollama", FontSize = 12, Classes = { "primary" } };
            start.Click += async (_, _) =>
            {
                start.IsEnabled = false;
                start.Content = "Starting...";

                var outcome = await probe.StartAsync(status.ExecutablePath!);

                if (outcome.Started)
                {
                    // Said once, here, while it is relevant. Something we started on their behalf
                    // has to come with the way to undo it: a background server nobody knows how to
                    // stop is not a favour.
                    if (outcome.HowToStop is not null)
                        _ollamaPanel.Children.Add(Note($"To stop it later: {outcome.HowToStop}", "TextMuted"));

                    // We just changed the situation ourselves, so what we remembered is wrong.
                    _aiServers.Forget();
                    await DiscoverAsync();
                    return;
                }

                start.Content = "Start Ollama";
                start.IsEnabled = true;

                if (outcome.Command is not null)
                {
                    // We could act but must not: this needs an administrator password, and asking
                    // for one to start a translation helper would be out of proportion. The exact
                    // command is worth more than an apology.
                    _ollamaPanel.Children.Add(Note(
                        "Starting it needs administrator rights, which we will not ask you for. "
                        + "Run this in a terminal, then search again:", "StatusWarning"));
                    _ollamaPanel.Children.Add(new TextBox
                    {
                        Text = outcome.Command,
                        IsReadOnly = true,
                        FontFamily = new FontFamily("Consolas, monospace"),
                        FontSize = 12,
                    });
                }
                else
                {
                    _ollamaPanel.Children.Add(Note(
                        outcome.Failure ?? "It would not start from here. Launching Ollama "
                        + "yourself and searching again works — we would rather say so than keep "
                        + "retrying.", "StatusWarning"));
                }
            };

            _ollamaPanel.Children.Add(start);
            return;
        }

        var installer = new OllamaInstaller(_platform);

        // Two network calls behind this, and until now nothing on screen while they ran. On a
        // slow link that is several seconds of a panel that looks empty and finished.
        var checking = new SpinningGear("Checking what the current Ollama release is...");
        _ollamaPanel.Children.Add(checking);

        var offer = await installer.PrepareAsync();
        _ollamaPanel.Children.Remove(checking);

        if (!offer.CanInstall)
        {
            _ollamaPanel.Children.Add(Note(offer.Refusal ?? "Ollama cannot be installed from here.",
                "TextSecondary"));
            return;
        }

        _ollamaPanel.Children.Add(Note(
            "Ollama runs a language model on your own machine: no account, no key, nothing "
            + "billed. It is a real download and a real load on your graphics card — "
            + $"{offer.SizeText} for the program, and a model on top of that.",
            "TextSecondary"));

        var progress = Note("", "TextMuted");
        var install = new Button { Content = $"Install Ollama ({offer.SizeText})", FontSize = 12 };

        var downloading = new SpinningGear("Starting the download...") { IsVisible = false };

        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            downloading.IsVisible = true;
            installer.Progress += (done, total) => Dispatcher.UIThread.Post(() =>
                progress.Text = total is { } t
                    ? $"Downloading... {done / 1024.0 / 1024:F0} of {t / 1024.0 / 1024:F0} MB"
                    : $"Downloading... {done / 1024.0 / 1024:F0} MB");

            var failure = await installer.InstallAsync(offer);

            if (failure is null)
            {
                progress.Text = "Installed. Looking for it now.";
                downloading.Message = "Waiting for Ollama to answer...";
                _aiServers.Forget();
                await DiscoverAsync();
                return;
            }

            downloading.IsVisible = false;
            progress.Text = failure;
            progress.Foreground = Brush("StatusError");
            install.IsEnabled = true;
        };

        _ollamaPanel.Children.Add(install);
        _ollamaPanel.Children.Add(downloading);
        _ollamaPanel.Children.Add(progress);
    }

    /// <summary>
    /// Offers a model to a server that has none, sized to the machine.
    ///
    /// Ordered by what this card can actually run: a model that spills out of video memory falls
    /// back to the processor and takes minutes per line, which someone will read as "the mod is
    /// broken". What does not fit is still shown, last and labelled — refusing to offer it would
    /// be deciding for them.
    ///
    /// ⚠ Never ordered or filtered by language. Every part of this project stays language-agnostic
    /// and this is the screen where the temptation is strongest.
    /// </summary>
    private async Task OfferModelAsync(string serverUrl)
    {
        _ollamaPanel.Children.Clear();
        _ollamaPanel.IsVisible = true;

        _modelNotes ??= await new ModelNotesProvider(_platform)
            .GetAsync(offline: !_draft.OnlineMode);

        var vram = _platform.VideoMemoryBytes();
        var candidates = ModelNotesProvider.Installable(_modelNotes, vram);

        if (candidates.Count == 0)
        {
            _ollamaPanel.Children.Add(Note(
                "This server has no model loaded yet, and we could not reach our list of models "
                + "to suggest one. Any model pulled with Ollama works — the mod only needs a "
                + "server that answers.", "StatusWarning"));
            return;
        }

        _ollamaPanel.Children.Add(Note(
            vram is { } bytes
                ? $"No model on this server yet. Your graphics card has "
                  + $"{bytes / 1024.0 / 1024 / 1024:F0} GB, so these are worth considering:"
                : "No model on this server yet. We could not read your graphics card size, so "
                  + "here is the whole list with what each one needs:",
            "TextSecondary"));

        var progress = Note("", "TextMuted");

        foreach (var candidate in candidates)
        {
            var fits = ModelNotesProvider.Fits(candidate, vram);
            var size = candidate.DownloadGb is { } gb ? $"{gb:F1} GB" : "size unknown";
            var need = candidate.MinVramGb is { } min ? $", wants {min:F0} GB of video memory" : "";

            var row = new StackPanel { Spacing = 2 };

            var button = new Button
            {
                Content = $"Download {candidate.Pull} ({size})",
                FontSize = 12,
                Classes = { "primary" },
            };

            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                await PullModelAsync(serverUrl, candidate.Pull!, progress);
                button.IsEnabled = true;
            };

            row.Children.Add(button);
            row.Children.Add(Note(
                $"{candidate.Note} ({size}{need})",
                fits == false ? "StatusWarning" : "TextMuted"));

            if (fits == false)
            {
                row.Children.Add(Note(
                    "Larger than your card: it will run on the processor instead, which means "
                    + "minutes per line rather than seconds. It still works.", "StatusWarning"));
            }

            _ollamaPanel.Children.Add(row);
        }

        _ollamaPanel.Children.Add(progress);
    }

    /// <summary>Pulls one model, saying where it is up to the whole way.</summary>
    private async Task PullModelAsync(string serverUrl, string model, TextBlock progress)
    {
        var puller = new OllamaModelPuller(serverUrl);

        puller.Progress += (status, done, total) => Dispatcher.UIThread.Post(() =>
            progress.Text = done is { } d && total is { } t && t > 0
                ? $"{status} — {d / 1024.0 / 1024 / 1024:F1} of {t / 1024.0 / 1024 / 1024:F1} GB"
                : status);

        progress.Foreground = Brush("TextMuted");
        progress.Text = "Starting...";

        // Several gigabytes: the byte counter answers "how far", the gear answers "is it still
        // going" during the stretches where the counter does not move.
        var pulling = new SpinningGear($"Downloading {model}...");
        _ollamaPanel.Children.Add(pulling);

        var failure = await puller.PullAsync(model);
        _ollamaPanel.Children.Remove(pulling);

        if (failure is null)
        {
            progress.Text = "Downloaded. Reading the server again.";
            _aiServers.Forget();
            await DiscoverAsync();
            Select(_aiModel, model);
            return;
        }

        progress.Text = failure;
        progress.Foreground = Brush("StatusError");

        // Never a dead end: a download cut off by a firewall or a dropped line resumes where it
        // stopped, and the person has to be able to say so from here.
        var retry = new Button { Content = "Try again", FontSize = 12 };
        retry.Click += async (_, _) =>
        {
            retry.IsEnabled = false;
            await PullModelAsync(serverUrl, model, progress);
        };
        _ollamaPanel.Children.Add(retry);
    }

    private TextBlock Note(string text, string colour) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(colour),
    };

    /// <summary>
    /// Says what we have run ourselves against the selected model, and nothing more.
    ///
    /// Never a recommendation, and never a ranking: the suite is a heuristic on free text, and
    /// the machine matters as much as the model. Silence when we have never run it — an absent
    /// line is honest, an invented one is not.
    /// </summary>
    private void ShowModelNote()
    {
        var model = Tag(_aiModel);
        var text = model is null ? null : ModelNotesProvider.Describe(_modelNotes, model);

        _modelNote.Text = text ?? "";
        _modelNote.IsVisible = text is not null;
    }

    /// <summary>
    /// Checks that the address answers, before anything else is attempted.
    ///
    /// Separate from the model test on purpose: a wrong address, a rejected key and a model that
    /// disobeys are three different problems with three different fixes, and folding them into
    /// one red line sends people looking in the wrong place.
    /// </summary>
    private async Task TestConnectionAsync()
    {
        var url = _aiUrl.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            _aiStatus.Text = "Enter an address first.";
            return;
        }

        _connectButton.IsEnabled = false;
        _aiStatus.Text = "Connecting...";
        _populating = true;
        _aiModel.Items.Clear();
        _testButton.IsEnabled = false;

        // Quick when it works, and precisely not quick when it does not — which is the case where
        // someone needs to be told the tool has not given up.
        var connecting = new SpinningGear($"Asking {url}...");
        _ollamaPanel.Children.Clear();
        _ollamaPanel.Children.Add(connecting);
        _ollamaPanel.IsVisible = true;

        var models = await _probe.ListModelsAsync(url, _apiKey.Text?.Trim());

        _ollamaPanel.Children.Remove(connecting);
        _ollamaPanel.IsVisible = _ollamaPanel.Children.Count > 0;

        if (models is null)
        {
            _aiStatus.Text = "No answer from that address. Check the URL, and the key if this is an "
                           + "online provider: a rejected key looks exactly like a wrong address.";
            _connectButton.IsEnabled = true;
            _populating = false;
            Dispatcher.UIThread.Post(RefreshApplyButton, DispatcherPriority.Background);
            return;
        }

        _aiStatus.Text = $"Connected - {models.Count} model(s) offered.";
        foreach (var name in models)
            _aiModel.Items.Add(new ComboBoxItem { Content = name, Tag = name });

        Select(_aiModel, _draft.AiModel);
        _aiModel.SelectedItem ??= _aiModel.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _testButton.IsEnabled = _aiModel.SelectedItem is not null;
        _connectButton.IsEnabled = true;

        _populating = false;
        Dispatcher.UIThread.Post(RefreshApplyButton, DispatcherPriority.Background);
    }

    /// <summary>
    /// Runs the instruction suite and shows every answer beside its verdict.
    ///
    /// Showing the answer is the point, not decoration: the checks are heuristics over free
    /// text and produce both false positives and false negatives — a model that repeats the
    /// rules before answering makes a placeholder look duplicated and a technical term look
    /// preserved, on the very same reply. Whoever reads this has to be able to see that.
    /// </summary>
    private async Task RunSuiteAsync()
    {
        var model = Tag(_aiModel);
        var url = _aiUrl.Text?.Trim();
        if (model is null || string.IsNullOrWhiteSpace(url)) return;

        _testButton.IsEnabled = false;
        _testOutput.Children.Clear();

        // Sits at the bottom of the list, which is where the next result will appear. A model
        // slow enough to spill out of video memory takes long enough between answers that a
        // finished-looking screen is the honest reading — this is what says otherwise.
        var waiting = new SpinningGear("Measuring how long a line takes...");
        _testOutput.Children.Add(waiting);

        // Measured first, and shown on its own line: what a model costs to run is a different
        // question from whether it obeys, and both decide whether someone keeps it.
        _metrics.IsVisible = true;
        _metrics.Text = "Measuring...";

        var trial = await _probe.MeasureAsync(url, model);
        var gpu = trial.OnGpu switch
        {
            true => "in use",
            false => "NOT used - running on the processor",
            _ => "unknown, this server does not report it",
        };

        _metrics.Text = trial.Succeeded
            ? $"First line {trial.Elapsed.TotalSeconds:F1}s "
              + (trial.FirstRunWasCold ? "(the model had to be loaded)" : "(it was already loaded)")
              + $" - then {(trial.WarmElapsed ?? trial.Elapsed).TotalSeconds:F1}s per line"
              + $" - {trial.VramText} of video memory - GPU {gpu}."
              + Environment.NewLine
              + "Measured with no game running. In play the model shares the graphics card, so expect slower."
            : $"Could not measure ({trial.Detail}).";

        var language = string.Equals(Tag(_language), "auto", StringComparison.OrdinalIgnoreCase)
            ? _platform.SystemLanguage() ?? "en"
            : Tag(_language) ?? "en";

        var passed = 0;
        var required = 0;
        var echoed = 0;
        var done = 0;

        // Known before the first request, which is the point: "3 of 9" tells someone there is
        // more coming. A bare spinner would not.
        var total = ModelTestSuite.Build(language).Count;
        waiting.Message = $"Running test 1 of {total}...";

        await _probe.RunSuiteAsync(url, model, language, result =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (result.Test.UnlocksOption is null)
                {
                    required++;
                    if (result.Passed) passed++;
                }
                if (result.EchoedInstructions) echoed++;

                done++;

                // Inserted above the gear so the gear stays last: results accumulate, and the
                // thing that says "more is coming" keeps sitting where the next one will land.
                _testOutput.Children.Insert(_testOutput.Children.Count - 1, TestRow(result));

                waiting.Message = done < total
                    ? $"Running test {done + 1} of {total}..."
                    : "Finishing...";
                waiting.IsVisible = done < total;
            });
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _testOutput.Children.Remove(waiting);

            _testOutput.Children.Add(new TextBlock
            {
                Text = $"{passed}/{required} required instructions followed.",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = Brush(passed == required ? "StatusSuccess" : "StatusWarning"),
            });

            if (echoed > 0)
            {
                _testOutput.Children.Add(new TextBlock
                {
                    Text = $"{echoed} answer(s) repeated the instructions back. On its own, a reason "
                         + "not to use this model: the mod prints what comes back into the game, "
                         + "word for word.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("StatusWarning"),
                });
            }

            _testButton.IsEnabled = true;
        });
    }

    private Control TestRow(ModelTestResult result)
    {
        var experimental = result.Test.UnlocksOption is not null;

        var mark = experimental
            ? (result.Passed ? "can" : "cannot")
            : (result.Passed ? "ok" : "KO");

        // An experimental test never fails a model, so "cannot" must not be red — that would read
        // as a defect where there is none. But it was grey on both sides, and grey buried the one
        // outcome worth seeing: "can" means an option the mod keeps off can be switched on for
        // this model, and almost no model manages it. Green for the gain, amber for the closed
        // door — visible, and unmistakably not an error.
        var colour = experimental
            ? (result.Passed ? "StatusSuccess" : "StatusWarning")
            : (result.Passed ? "StatusSuccess" : "StatusError");

        var body = new StackPanel { Spacing = 2 };

        body.Children.Add(new TextBlock
        {
            Text = $"[{mark}]  {result.Test.Name}",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = Brush(colour),
        });

        body.Children.Add(new TextBlock
        {
            Text = $"asked: {result.Test.Source.ReplaceLineEndings(" / ")}",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        });

        body.Children.Add(new TextBlock
        {
            Text = $"answer: {result.Answer?.ReplaceLineEndings(" / ") ?? "(nothing)"}",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        if (experimental)
        {
            body.Children.Add(new TextBlock
            {
                Text = result.Passed
                    ? $"This model can do it — the mod's '{result.Test.UnlocksOption}' option may be switched on."
                    : $"Not followed — leave the mod's '{result.Test.UnlocksOption}' option off. "
                      + "It is experimental, and models keep getting better at this.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });

            // Shown on success too, and in amber so it is not read as small print under a green
            // mark. Passing means the model is capable, not that the option is safe: the mod
            // ships it disabled because its failure mode is silent, and a green line saying
            // "you may switch it on" with nothing beside it would quietly recommend it.
            if (result.Test.Caveat is not null)
            {
                body.Children.Add(new TextBlock
                {
                    Text = result.Test.Caveat,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("StatusWarning"),
                });
            }
        }

        if (result.EchoedInstructions)
        {
            body.Children.Add(new TextBlock
            {
                Text = "The model repeated the instructions; the check was run on the last line.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
            });
        }

        return new Border
        {
            Background = Brush("SurfaceBase"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Child = body,
        };
    }

    // ---------------------------------------------------------------- saving

    private void Save()
    {
        _draft.TargetLanguage = Tag(_language) ?? "auto";
        _draft.TranslationBackend = Tag(_backend) ?? "none";
        _draft.AiUrl = _aiUrl.Text?.Trim() ?? "";
        _draft.AiModel = Tag(_aiModel) ?? "";
        _draft.AiApiKey = string.IsNullOrWhiteSpace(_apiKey.Text) ? null : _apiKey.Text.Trim();
        DraftWithNetwork();
        _draft.EnableAi = _draft.TranslationBackend == "ai";
        // Whatever is on screen is what gets saved. The field can only hold something captured,
        // so it cannot be unusable — and quietly substituting a different key would be the exact
        // behaviour this whole mechanism exists to avoid.
        var captured = _hotkey.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(captured)) _draft.SettingsHotkey = captured;
        _draft.Channel = Tag(_channel) ?? "stable";
        _draft.OnlineMode = _online.IsChecked == true;

        // Reviewed is what allows the mod's first-run wizard to be skipped later, and it is set
        // here and nowhere else: it means a human has actually looked at these values.
        _draft.Reviewed = true;

        _store.Save(_draft);
        Saved = true;
        Close();
    }

    /// <summary>
    /// How many settings differ from what is currently saved.
    ///
    /// Compared against the store, not against the draft: the draft is edited in place by the
    /// connection test, and counting against it would report zero changes right after someone
    /// changed something.
    /// </summary>
    private IReadOnlyList<string> PendingChanges()
    {
        var saved = _store.Current;
        var changes = new List<string>();

        void Compare(string label, string? now, string? before)
        {
            // Empty and null mean the same thing to every one of these settings, and treating
            // them as different is how a screen claims to have unsaved work it does not have.
            if ((now ?? "") != (before ?? "")) changes.Add($"{label}: \"{before}\" -> \"{now}\"");
        }

        Compare("language", Tag(_language), saved.TargetLanguage);
        Compare("backend", Tag(_backend), saved.TranslationBackend);
        Compare("AI server", _aiUrl.Text, saved.AiUrl);
        Compare("AI model", Tag(_aiModel), saved.AiModel);
        Compare("API key", _apiKey.Text, saved.AiApiKey);
        Compare("hotkey", _hotkey.Text, saved.SettingsHotkey);
        Compare("updates channel", Tag(_channel), saved.Channel);
        Compare("proxy mode", Tag(_proxyMode), saved.ProxyMode);
        Compare("proxy address", _proxyUrl.Text, saved.ProxyUrl);
        Compare("proxy username", _proxyUser.Text, saved.ProxyUsername);
        Compare("proxy password", _proxyPassword.Text, saved.ProxyPassword);

        if ((_online.IsChecked == true) != saved.OnlineMode)
            changes.Add($"community catalog: {saved.OnlineMode} -> {_online.IsChecked == true}");

        return changes;
    }

    /// <summary>
    /// How many settings differ from what is currently saved.
    ///
    /// Compared against the store, not against the draft: the draft is edited in place by the
    /// connection test, and counting against it would report zero changes right after someone
    /// changed something.
    /// </summary>
    private int CountPendingChanges() => PendingChanges().Count;

    /// <summary>"Apply (3)" while there is something to save, "Close" when there is not.</summary>
    private void RefreshApplyButton()
    {
        if (_populating) return;

        var changes = PendingChanges();
        _applyButton.Content = changes.Count > 0 ? $"Apply ({changes.Count})" : "Close";

        // What exactly is pending, on hover. A count alone is a claim; this is the evidence — and
        // on a screen with a dozen fields it is the difference between trusting the number and
        // clicking Apply to find out.
        ToolTip.SetTip(_applyButton, changes.Count > 0
            ? string.Join(Environment.NewLine, changes)
            : "Nothing to save.");
    }

    /// <summary>
    /// Recounts on every edit. Wired once, here, rather than at each control's creation: a count
    /// that misses one field is worse than no count, because it says "nothing to save" about work
    /// that would then be lost on Cancel.
    /// </summary>
    private void WatchForChanges()
    {
        foreach (var box in new[] { _language, _backend, _aiModel, _channel, _proxyMode })
            box.SelectionChanged += (_, _) => RefreshApplyButton();

        foreach (var field in new[] { _aiUrl, _apiKey, _hotkey, _proxyUrl, _proxyUser, _proxyPassword })
            field.TextChanged += (_, _) => RefreshApplyButton();

        _online.IsCheckedChanged += (_, _) => RefreshApplyButton();
    }

    // ---------------------------------------------------------------- helpers

    private static IBrush? Brush(string key) =>
        Application.Current?.FindResource(key) as IBrush;

    private static string? Tag(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private static void Select(ComboBox box, string? value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedItem ??= box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private Control Row(string label, params Control[] controls)
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

    private Control Card(string title, string? intro, Control content)
    {
        var body = new StackPanel { Spacing = 10 };

        body.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
        });

        if (intro is not null)
        {
            body.Children.Add(new TextBlock
            {
                Text = intro,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
            });
        }

        body.Children.Add(content);

        return new Border
        {
            Background = Brush("SurfaceCard"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 15),
            Child = body,
        };
    }
}
