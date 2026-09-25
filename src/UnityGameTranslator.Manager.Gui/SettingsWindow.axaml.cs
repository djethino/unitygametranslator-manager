using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UnityGameTranslator.Manager.Core.Ai;
using UnityGameTranslator.Manager.Core.Api;
using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Settings;
using UnityGameTranslator.Common;
using static UnityGameTranslator.Manager.Gui.Ui;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The mod's settings, decided once here and written into each game.
///
/// ⚠ Not the installer's own preferences, and the name matters: "Settings" made people expect
/// options for this tool. Almost everything here belongs to the MOD — it is answered once and
/// written into each game's config.json, which is what lets the mod's first-run wizard be skipped
/// and what lets an already-configured game be reconfigured without opening it.
///
/// Everything here goes into a game. The tool's own settings — its account, how it reaches the
/// network — live in their own window: they were grouped under one title with two headings, which
/// was a patch over the real problem. They are two subjects, and someone changing a value has to
/// know without thinking whether it will reach a game.
///
/// The target language especially is a fact about the person, not a per-game preference, and it is
/// what turns "3 translations available" into "this game is playable in your language".
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

    private SearchPicker _language = null!;
    private SearchPicker _backend = null!;
    private TextBox _aiUrl = null!;

    /// <summary>
    /// What has to be said about the address currently in the field: nothing for a server on this
    /// machine, privacy for one on the network, privacy and cost for anything else.
    /// </summary>
    private TextBlock _locality = null!;
    private SearchPicker _aiModel = null!;
    private SearchPicker _testInto = null!;

    /// <summary>What the run will cost, said before the button rather than after it.</summary>
    private TextBlock _testCost = null!;

    /// <summary>
    /// Set while a run is going, so the same button can stop it.
    ///
    /// 🔴 The counterpart of the cost line above: a screen that says "this will be forty requests
    /// on a paid service" and then offers no way out has told the reader about a decision they
    /// cannot take back. Null when nothing is running.
    /// </summary>
    private CancellationTokenSource? _suiteStop;
    private SearchPicker _testFrom = null!;
    private HotkeyEditor _hotkey = null!;
    private TextBlock _hotkeyProblem = null!;

    /// <summary>The mod's optional shortcuts, by config.json key — see ModShortcuts.</summary>
    private readonly Dictionary<string, HotkeyEditor> _shortcuts = new(StringComparer.Ordinal);
    private SearchPicker _channel = null!;
    private CheckBox _modOnline = null!;
    private CheckBox _autoDownload = null!;
    private CheckBox _notifyUpdates = null!;
    private CheckBox _checkModUpdates = null!;
    private SearchPicker _mergeStrategy = null!;
    private CheckBox _notificationsEnabled = null!;
    private SearchPicker _notificationPosition = null!;

    private TextBox _apiKey = null!;
    private TextBlock _metrics = null!;
    private TextBlock _modelNote = null!;
    private StackPanel _ollamaPanel = null!;
    private Button _connectButton = null!;
    private Button _refreshModels = null!;
    private StackPanel _aiPanel = null!;
    private StackPanel _apiPanel = null!;
    private Control _aiCard = null!;

    /// <summary>
    /// Putting a model through the mod's own instructions — its own card, since it sets nothing.
    /// Shown and hidden with <see cref="_aiCard"/>: there is nothing to test without a model.
    /// </summary>
    private Control _testCard = null!;
    private Control _apiCard = null!;
    private SearchPicker _provider = null!;
    private TextBox _providerKey = null!;
    private CheckBox _deeplFree = null!;
    private StackPanel _testOutput = null!;
    private TextBlock _aiStatus = null!;

    /// <summary>What the provider said about the key. Its own line, beside its own button.</summary>
    private TextBlock _apiStatus = null!;

    /// <summary>
    /// Turning while a local server is being looked for.
    ///
    /// 🔴 **A sentence is not an indicator.** The search says "Looking for a local AI server..."
    /// in the same grey, in the same place, as every other thing it says — so a screen that is
    /// sweeping six ports is indistinguishable from one that has finished and found nothing. The
    /// wait is seconds long and there was no way to tell the two apart.
    ///
    /// ⚠ The gear this project already uses for its game scan, not a second kind of waiting mark.
    /// </summary>
    private SpinningGear _aiGear = null!;
    private Button _testButton = null!;
    private Button _applyButton = null!;
    private TextBlock _saved = null!;

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
            Shortcuts = ModShortcuts.CopyOf(current.Shortcuts) ?? new Dictionary<string, string>(),
            TranslateModUi = current.TranslateModUi,
            Channel = current.Channel,
            AiApiKey = current.AiApiKey,
            GoogleApiKey = current.GoogleApiKey,
            DeeplApiKey = current.DeeplApiKey,
            DeeplUseFree = current.DeeplUseFree,
            ModOnlineMode = current.ModOnlineMode,
            AutoDownload = current.AutoDownload,
            NotifyUpdates = current.NotifyUpdates,
            CheckModUpdates = current.CheckModUpdates,
            MergeStrategy = current.MergeStrategy,
            NotificationsEnabled = current.NotificationsEnabled,
            NotificationPosition = current.NotificationPosition,
            Reviewed = current.Reviewed,
        };

        // The screen's name, and nothing after it: every other screen sends people here as
        // "Mod defaults", and the intro under it says what it does.
        Title = "Mod defaults";
        // Wide enough for the longest row: the model list plus Refresh plus "Test this model",
        // after a 130px label. At 720 that row reached the card's edge and the last button sat
        // against it. The minimum is kept above that width rather than merely below the default,
        // so shrinking the window cannot recreate the same collision.
        Width = 840;
        Height = 760;
        MinWidth = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.FindResource("SurfaceBase") as IBrush;

        Content = Build();

        // ⚠ Only when AI is the backend they have chosen. The search sweeps six ports on the
        // machine, and it used to run on every visit whatever was selected — for somebody playing
        // with community translations, or paying for DeepL, that is work they did not ask for, to
        // fill in a card that is not even on screen: the AI card is hidden unless AI is chosen.
        //
        // The code already made this argument for a server that is configured ("probing it on every
        // visit is a request the person did not ask for") and then went ahead and swept anyway for
        // one that is not. Same reasoning, one step earlier.
        if (Tag(_backend) == "llm")
        {
            Opened += async (_, _) => await DiscoverAsync(reuseKnown: true);
        }
        else
        {
            // ⚠ The search is also what marks the screen as settled, and skipping it left that flag
            // raised for good — with the counter suppressed, so nothing anyone changed would ever
            // be counted. The screen is settled the moment it is built when there is no search.
            _populating = false;
        }
    }

    private Control Build()
    {
        var layout = new StackPanel { Spacing = 16, Margin = new Thickness(24) };

        // ⚠ "When everything is filled in": the wizard is only skipped when every answer it asks for
        // is here (InstallerSettings.AnswersTheWizard) — a partial setup still lets it run.
        layout.Children.Add(Intro(
            "Written into every game you set up. When everything on this screen is filled in, "
            + "UGT Mod skips its first-run questions. Games already set up are not changed unless "
            + "you ask."));

        layout.Children.Add(LanguageCard());
        layout.Children.Add(BackendCard());
        // The whole card is hidden, title included — not just its contents. Hiding only the inside
        // left two headings sitting over nothing, which reads as a screen that failed to load
        // rather than as a section that does not apply.
        _aiCard = AiCard();

        // ⚠ After the AI card, and it must be: it hooks the model picker that one builds.
        _testCard = TestCard();

        _apiCard = ApiCard();
        layout.Children.Add(_aiCard);
        layout.Children.Add(_testCard);
        layout.Children.Add(_apiCard);
        layout.Children.Add(HotkeysCard());
        layout.Children.Add(ModUiCard());
        layout.Children.Add(SyncCard());


        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        // ⚠ Applying does NOT close the window. Someone pressing Apply is saying "keep this", not
        // "keep this and I am done here" — and this screen in particular is long enough that being
        // thrown out of it after one decision means finding your place again.
        //
        // The button is the receipt: it counts what is pending, so going from "Apply (3)" back to
        // "Close" is the answer. The line beside it says it in words, since a label changing is easy
        // to miss when you were looking at the setting you just changed.
        //
        // 🔸 Same shape in the Settings window for the tool itself. Change one, change the other.
        _saved = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("StatusSuccess"),
            IsVisible = false,
        };

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

        buttons.Spacing = 12;
        buttons.Children.Add(_saved);
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

        ShowBackendCards();
        WatchForChanges();
        RefreshApplyButton();

        return root;
    }

    // ---------------------------------------------------------------- cards

    private Control LanguageCard()
    {
        // 🔸 Built by ModSettingControls, which a game's own card reads too. The same question asked
        // twice must offer the same answers, or a value chosen on one screen stops meaning the same
        // thing on the other.
        _language = ModSettingControls.LanguagePicker(_platform, 260);

        Select(_language, _draft.TargetLanguage);

        return Card("Language",
            "The language you play in. Used to find translations for your games.",
            Row("Target language", _language));
    }

    private Control BackendCard()
    {
        // The mod's own two-level shape, and its own words: one choice of kind, then a provider
        // if the kind has several. Listing Google and DeepL as siblings of "an AI" made them look
        // like three unrelated things, when the mod treats the last two as one backend with a
        // provider setting. 🔸 Shared with a game's own card — see ModSettingControls.
        _backend = ModSettingControls.BackendPicker(260);
        Select(_backend, _draft.TranslationBackend == "deepl" ? "google" : _draft.TranslationBackend);

        _backend.SelectionChanged += (_, _) => ShowBackendCards();

        // "Translate with", not "Backend": the mod's own screen says "Type", and neither word of
        // developer jargon helps somebody picking between community work, an AI and their own hands.
        return Card("Translation",
            "For lines that no community translation covers yet.",
            Row("Translate with", _backend));
    }

    /// <summary>
    /// What the mod does about updates and what it shows while playing.
    ///
    /// Here rather than in the game because these are facts about a person: someone with twenty
    /// games does not want to answer "download updates automatically?" twenty times, and least of
    /// all what to do when a translation and their own edits both changed.
    /// </summary>
    private Control SyncCard()
    {
        // The mod's own connection, not this tool's. Someone who installs everything from here,
        // translation included, has what they need before the game starts.
        //
        // ⚠ FIRST in this card: off, there are no update notices and no community translations in
        // the game — it governs everything below it, so it is read before them.
        _modOnline = new CheckBox
        {
            Content = "Allow UGT Mod to go online",
            IsChecked = _draft.ModOnlineMode,
        };

        // 🔸 Shared with a game's own card — see ModSettingControls. Which builds are installed and
        // announced: an update matter, so here rather than beside the hotkey where it used to sit.
        _channel = ModSettingControls.ChannelPicker(200);
        Select(_channel, _draft.Channel);

        _checkModUpdates = new CheckBox { Content = "Notify me about mod updates",
                                          IsChecked = _draft.CheckModUpdates };
        _notifyUpdates = new CheckBox { Content = "Notify me about translation updates",
                                       IsChecked = _draft.NotifyUpdates };
        // "those updates" sat under both boxes and read as covering the mod too. It never did:
        // the mod is only ever updated from this tool, deliberately and with a confirmation. Named
        // in full, and indented under the line it depends on.
        _autoDownload = new CheckBox
        {
            Content = "Download translation updates automatically",
            IsChecked = _draft.AutoDownload,
            Margin = new Thickness(20, 0, 0, 0),
        };

        // 🔸 Shared with a game's own card — see ModSettingControls.
        _mergeStrategy = ModSettingControls.MergeStrategyPicker(260);
        Select(_mergeStrategy, _draft.MergeStrategy);

        // "Notifications": the mod's own word for the corner overlay (options.json).
        _notificationsEnabled = new CheckBox { Content = "Show notifications in the game",
                                              IsChecked = _draft.NotificationsEnabled };

        _notificationPosition = ModSettingControls.NoticePositionPicker(260);
        Select(_notificationPosition, _draft.NotificationPosition);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_modOnline);
        panel.Children.Add(Note(
            "Off: UGT Mod stays offline in the game. No update notices, no community translations. "
            + "What is already installed keeps working."));
        panel.Children.Add(Row("Update channel", _channel));
        panel.Children.Add(_checkModUpdates);
        panel.Children.Add(Note("UGT Mod is only updated from UGT Manager, after you confirm."));
        panel.Children.Add(_notifyUpdates);
        panel.Children.Add(_autoDownload);
        panel.Children.Add(Row("When both changed", _mergeStrategy));
        panel.Children.Add(Note("A translation you use was updated, and you edited it too."));
        panel.Children.Add(_notificationsEnabled);
        panel.Children.Add(Row("Position", _notificationPosition));

        return Card("Updates and notifications", null, panel);
    }

    /// <summary>
    /// The one line the AI card speaks through, and the colour that says which kind of line it is.
    ///
    /// ⚠ Everything used to come out in the same muted grey — "Connecting...", "Connected — 3
    /// models" and "No answer from that address" alike. So a test against a port with nothing on it
    /// answered in a sentence that looked exactly like the running commentary, and read as though
    /// nothing had happened at all. The words were there; nothing marked them as a failure.
    ///
    /// The colour is the tone of what is said (Ui.Say): muted while something is happening, green
    /// when it worked, amber when it did not but nothing is broken, red when it cannot work.
    /// </summary>
    private void Say(string text, Tone tone = Tone.Neutral) => Ui.Say(_aiStatus, text, tone);

    private void SayWorked(string text) => Say(text, Tone.Success);

    private void SayFailed(string text) => Say(text, Tone.Error);

    /// <summary>The same, for the other card. Two translators, two conversations, two lines.</summary>
    private void SayAboutKey(string text, Tone tone) => Ui.Say(_apiStatus, text, tone);

    /// <summary>Only the card for the chosen backend is on screen; the other is gone entirely.</summary>
    private void ShowBackendCards()
    {
        var backend = Tag(_backend);
        _aiCard.IsVisible = backend == "llm";

        // ⚠ The same condition, never a second one: there is nothing to test without a model, and
        // two conditions for one fact is how a card ends up on screen alone.
        _testCard.IsVisible = _aiCard.IsVisible;

        _apiCard.IsVisible = backend == "google";

        if (backend == "llm") _ = LookNowThatAiIsChosenAsync();
    }

    /// <summary>
    /// Searches — now that AI has actually been chosen — and fills in what is found.
    ///
    /// This is the whole of the discovery, and it waits here on purpose. Choosing AI is the gesture
    /// that makes a local server worth looking for, and the moment its address may be written into
    /// the form. Before that, the machine is not swept at all.
    ///
    /// Never during population: setting the backend to what was saved raises this same event, and
    /// searching then would be the original fault wearing a different hat.
    /// </summary>
    private async Task LookNowThatAiIsChosenAsync()
    {
        if (_populating) return;

        // What was already found, or what was already configured, rather than sweeping again.
        await DiscoverAsync(reuseKnown: true);

        if (_aiServers.Remembered is not { } servers || servers.Count == 0) return;

        if (string.IsNullOrWhiteSpace(_aiUrl.Text)) _aiUrl.Text = servers[0].Url;

        _aiModel.Reselect(_aiModel.SelectedItem ?? _aiModel.Items.FirstOrDefault());

        RefreshApplyButton();
    }

    /// <summary>
    /// Google Translate and DeepL: one card, one provider choice, one key per provider.
    ///
    /// Written because choosing either of them used to configure nothing at all — the backend was
    /// written into the game without the key it needs, so the mod started with something that
    /// could not translate a single line and no screen said why.
    /// </summary>
    private Control ApiCard()
    {
        // 🔸 Shared with a game's own card — see ModSettingControls.
        _provider = ModSettingControls.ProviderPicker(260);
        Select(_provider, _draft.TranslationBackend == "deepl" ? "deepl" : "google");

        _providerKey = new TextBox { Width = 300, PasswordChar = '*' };
        _deeplFree = new CheckBox
        {
            Content = "Free tier (api-free.deepl.com)",
            IsChecked = _draft.DeeplUseFree,
        };

        void ShowProvider()
        {
            var isDeepl = Tag(_provider) == "deepl";

            // Each provider keeps its own key. Sharing one field would overwrite the key you were
            // using the moment you looked at the other one.
            _providerKey.Text = (isDeepl ? _draft.DeeplApiKey : _draft.GoogleApiKey) ?? "";
            _deeplFree.IsVisible = isDeepl;
        }

        _provider.SelectionChanged += (_, _) => ShowProvider();
        _providerKey.TextChanged += (_, _) =>
        {
            if (Tag(_provider) == "deepl") _draft.DeeplApiKey = _providerKey.Text;
            else _draft.GoogleApiKey = _providerKey.Text;
        };

        ShowProvider();

        // 🔴 **A way to find out, before a game is set up with it.** There was none: a key was
        // typed, encrypted, stored and written into a config.json, and the first thing that ever
        // tested it was the mod failing to translate mid-game with no screen saying why. The local
        // AI server has had a Refresh and a test bench since the beginning.
        var test = new Button { Content = "Test key", FontSize = 12 };

        _apiStatus = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        test.Click += async (_, _) =>
        {
            var key = _providerKey.Text;
            var deepl = Tag(_provider) == "deepl";

            test.IsEnabled = false;
            SayAboutKey("Testing with " + (deepl ? "DeepL" : "Google") + "...", Tone.Neutral);

            var probe = new TranslatorKeyProbe();
            var result = deepl
                ? await probe.CheckDeeplAsync(key, _deeplFree.IsChecked == true)
                : await probe.CheckGoogleAsync(key);

            // A key that does not work is the same kind of answer as an address that does not
            // answer on the AI card — the mod would translate nothing with it — so the same red.
            SayAboutKey(result.Message, result.Works ? Tone.Success : Tone.Error);
            test.IsEnabled = true;
        };

        _apiPanel = new StackPanel { Spacing = 10 };
        // ⚠ On the row, beside the field it acts on — the shape the AI card uses for its own two
        // buttons. A button on a line of its own underneath is a second grammar on one screen.
        _apiPanel.Children.Add(Row("Provider", _provider));
        _apiPanel.Children.Add(Row("API key", _providerKey, test));
        _apiPanel.Children.Add(_deeplFree);
        _apiPanel.Children.Add(_apiStatus);

        // 🔴 **A key of your own comes first, and the allowance second.** This led with "both have
        // a free allowance", which is the half that lands — and read beside a tickbox saying "Free
        // tier", it said plainly that anybody could use these without a key. Nobody can: both
        // require an account of your own, and the free allowance is a property of that account.
        //
        // ⚠ Amber: it is about the reader's money. The storage line is a plain fact, apart.
        _apiPanel.Children.Add(Note(
            "Needs your own account and API key. Free up to a monthly limit, then billed to you.",
            Tone.Warning));
        _apiPanel.Children.Add(Note("The key is stored encrypted on this machine."));

        return Card("Google / DeepL", null, _apiPanel);
    }

    /// <summary>
    /// Puts the caution in step with the address, or takes it away when there is nothing to say.
    ///
    /// ⚠ An empty field says nothing either. Somebody who has not typed an address yet has not
    /// made a decision to be cautioned about, and greeting them with a bill notice would answer a
    /// question they have not asked.
    /// </summary>
    private void ShowLocality()
    {
        var typed = _aiUrl.Text?.Trim();

        var caution = string.IsNullOrWhiteSpace(typed) ? null : Endpoints.CautionFor(typed);

        _locality.Text = caution ?? "";
        _locality.IsVisible = caution is not null;
    }

    private Control AiCard()
    {
        _aiUrl = new TextBox { Width = 300, Watermark = Endpoints.OllamaDefault };
        _aiUrl.Text = _draft.AiUrl;

        // One field for a server on this machine and for an online provider alike: the mod only
        // ever knows an OpenAI-compatible address, so anything speaking that dialect fits here.
        _apiKey = new TextBox
        {
            Width = 300,
            Watermark = "Not needed for a local server",
            PasswordChar = '*',
            Text = _draft.AiApiKey ?? "",
        };

        _aiModel = new SearchPicker { Width = 300, TextOf = model => model as string ?? "" };

        _aiStatus = new TextBlock
        {
            Text = "Choose a local or online AI server, then test it.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ⚠ Left and tight against the line it belongs to. The gear centres itself and keeps its
        // own air, which is right in an empty panel and wrong beside a sentence.
        _aiGear = new SpinningGear(string.Empty, size: 18)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0),
            IsVisible = false,
        };

        var refresh = new Button { Content = "Find local AI", FontSize = 12 };
        Busy.OnClick(refresh, () =>
        {
            // Explicit means explicit: forget what we knew and sweep the ports again, even when
            // an address is already saved. This is how someone moves from an online provider back
            // to a server on their own machine.
            _aiServers.Forget();
            return DiscoverAsync(asked: true);
        });

        // Beside the list, because that is what it acts on. "Test connection" does the same
        // request, but it sits next to the API key and reads as "is this working" — not as
        // "show me what is on the server now", which is the question someone has after pulling a
        // model in another window.
        _refreshModels = new Button { Content = "Refresh", FontSize = 12 };
        Busy.OnClick(_refreshModels, () => TestConnectionAsync(asRefresh: true));

        _connectButton = new Button { Content = "Test connection", FontSize = 12 };
        Busy.OnClick(_connectButton, () => TestConnectionAsync());

        _aiPanel = new StackPanel { Spacing = 10 };
        _aiPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _aiGear, _aiStatus },
        });
        _aiPanel.Children.Add(Row("Server", _aiUrl, refresh));
        _aiPanel.Children.Add(Row("API key", _apiKey, _connectButton));
        // ⚠ What is said depends on WHERE the address points, and it used to be said to everyone.
        //
        // A permanent bill notice under a local Ollama contradicts the offer this whole project is
        // built on — free translation on your own machine — and a caution everybody sees is a
        // caution nobody reads, so it was also absent exactly where it mattered. The three cases
        // are settled once, in the shared library, because it is a statement about somebody's money
        // and somebody's data and the mod has to make it identically.
        _locality = Note("", Tone.Warning);
        _locality.IsVisible = false;
        _aiPanel.Children.Add(_locality);

        // The same sentence as the Google / DeepL card: one fact, one wording. What it does not
        // protect against (a program already running as the same user) is in TokenProtection's
        // own documentation — the reader only needs to know the key does not sit in plain text.
        _aiPanel.Children.Add(Note("The key is stored encrypted on this machine."));

        // Follows what is typed rather than what was saved: somebody pasting a provider's address
        // needs to read this before they press Save, not after.
        _aiUrl.TextChanged += (_, _) =>
        {
            ShowLocality();

            // The test's cost line says whether it is billed, and that follows the address too.
            // Null while the window is still being built: the test card comes after this one.
            if (_testCost is not null) ShowTestCost();
        };
        ShowLocality();
        _modelNote = Note("");
        _modelNote.IsVisible = false;

        _ollamaPanel = new StackPanel { Spacing = 8, IsVisible = false };
        _aiPanel.Children.Add(_ollamaPanel);

        _aiPanel.Children.Add(Row("Model", _aiModel, _refreshModels));

        return Card("AI translation", null, _aiPanel);
    }

    /// <summary>
    /// Putting a model through what the mod actually asks of it — its own card, under the AI one.
    ///
    /// 🔴 **It is a feature, not a field.** It lived inside "AI translation", where every other line
    /// SETS something: an address, a key, which model to use. This one sets nothing at all. It
    /// answers a different question — *what would this model do in my game* — and it answers it by
    /// running for a while and printing a page. Folded in among the settings it made a card of
    /// eight controls read as a card of twenty, and its own two pickers looked like two more
    /// settings somebody had to fill in before saving.
    ///
    /// ⚠ Appears and disappears with the AI card, since there is nothing to test without a model.
    /// Both are driven from ShowBackendCards, on one condition rather than two.
    /// </summary>
    private Control TestCard()
    {
        _testButton = new Button
        {
            Content = "Test this model",
            FontSize = 12,
            IsEnabled = false,

            // Sits on the pickers' own line rather than centred on their labels: the two beside it
            // carry a word above them, and a button floating half a line up reads as belonging to
            // neither.
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _testButton.Click += async (_, _) =>
        {
            // One button, two verbs — never a second control that is greyed out half the time.
            if (_suiteStop is { } running)
            {
                running.Cancel();
                return;
            }

            await RunSuiteAsync();
        };

        _testOutput = new StackPanel { Spacing = 6 };

        _metrics = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Foreground = Brush("TextSecondary"),
        };

        // ⚠ Searchable, like every other language list in this product: about a hundred and eighty
        // entries is past what anybody scans, and a dropdown that could not be scrolled with the
        // wheel left no way through at all. See SearchPicker.
        //
        // Both default when the card opens and are never written anywhere — exactly like the
        // language pickers on the translations screen. Into starts on the language being set up,
        // From on the language most games are written in, or the next one when that IS the target:
        // asking a model for English from English is a job the mod never gives it.
        _testInto = new SearchPicker { Width = 190 };
        _testFrom = new SearchPicker { Width = 190 };

        LanguageMark.Fill(_testInto, Languages.All());

        _testInto.SelectionChanged += (_, _) => { RefreshTestSources(); ShowTestCost(); };
        _testFrom.SelectionChanged += (_, _) => { _testOutput.Children.Clear(); ShowTestCost(); };

        var panel = new StackPanel { Spacing = 10 };

        // Each picker carries its own word. They were laid out bare under a single "Test" label
        // with a caption naming them in order, which asked the reader to work out which was which
        // — and the two are not interchangeable.
        panel.Children.Add(Row("Languages",
            Labelled("Into", _testInto),
            Labelled("From", _testFrom),
            _testButton));

        // Why the Into language is missing from From: the mod never asks a model for English from
        // English, so neither does its test.
        panel.Children.Add(Note("From never offers the Into language."));

        // ⚠ Same shape as the line above on purpose — a second caption under the same row, not a
        // control of its own. It says what the run costs BEFORE it is started: free on a local
        // server, billed per request and per token on a paid one, and this run is not small.
        _testCost = Note("");
        panel.Children.Add(_testCost);

        // Defaulted once, when the card is built, and never written anywhere: this pair aims the
        // test, it does not change a setting.
        Select(_testInto, _store.ResolveTargetLanguage());
        ShowTestCost();
        RefreshTestSources();

        panel.Children.Add(_modelNote);
        panel.Children.Add(_metrics);

        // Shown as soon as a model is picked, before any test: knowing that one of the listed
        // models is the one the mod is developed against is worth more than a mark obtained
        // afterwards, because it tells someone where to start rather than judging where they went.
        _aiModel.SelectionChanged += (_, _) => ShowModelNote();

        panel.Children.Add(_testOutput);

        // The intro says what the card is for; that the checks can be wrong belongs there, before
        // anyone reads a verdict — they are heuristics over free text (see RunSuiteAsync).
        return Card("Test this model",
            "Sends the model the same requests UGT Mod makes, from easy to hard, and shows each "
            + "answer. The checks can be wrong: read the answers too.",
            panel);
    }

    /// <summary>
    /// The panel key and the mod's optional shortcuts — one subject, one card, as the mod has one
    /// "Hotkeys" tab (user's decision, 2026-09-25). The update channel and the online switch left
    /// for "Updates and notifications": they were three subjects under one title.
    /// </summary>
    private Control HotkeysCard()
    {
        // 🔴 The capture itself lives in HotkeyEditor, shared with a game's own card. It is ninety
        // lines of refusals — a key Unity cannot name, a key that means something different from
        // one game to the next — and every one of them protects against the same outcome: a mod
        // whose panel silently stops opening, in a game where it used to, with the screen that
        // could fix it sitting behind that very key. Two copies of that is one copy too many.
        _hotkey = new HotkeyEditor(_draft.SettingsHotkey, Brush("TextMuted"), Brush("StatusWarning"));
        _hotkeyProblem = _hotkey.Problem;

        // ⚠ There is no "replace it in games too" box here any more, and putting one back would be
        // a step backwards. The hotkey is the one setting a game may legitimately know better than
        // we do — inside it, the mod captured the key against the real keyboard — so the question
        // is never "replace them all", it is "replace THIS one", and it belongs where both keys
        // can be read: the game's own card (GamePreference.ReplaceHotkey). Asked here it made
        // somebody decide for every game at once, out of sight of all of them, and showed nothing
        // afterwards — a game keeping its own key never appeared as a difference anywhere.

        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(Row("In-game hotkey", _hotkey.Row));
        panel.Children.Add(Note(ModSettingControls.HotkeyAdvice));
        panel.Children.Add(_hotkeyProblem);
        panel.Children.Add(Note(
            "A game that already has a hotkey keeps it. You can replace it on that game's page."));
        panel.Children.Add(AdditionalHotkeys());

        // The hotkey is asked here because the mod's first-run wizard asks for it — the window's
        // intro says when that wizard is skipped.
        return Card("Hotkeys", "The hotkey opens the UGT Mod panel in the game.", panel);
    }

    private CheckBox _translateModUi = null!;

    /// <summary>
    /// The one translation of UGT Mod's own interface UGT Manager keeps, and whether games use it
    /// (user's decision, 2026-09-25 — see Install.ModUiLibrary for the rules).
    ///
    /// ⚠ Import and Remove are ACTS on a file and act at once, like Backup: there is nothing to hold
    /// for Apply. The switch beside them is a setting like the others, and waits for Apply (N).
    /// </summary>
    private Control ModUiCard()
    {
        var library = new ModUiLibrary(_platform);
        var panel = new StackPanel { Spacing = 10 };

        var state = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var problem = Note("");
        problem.IsVisible = false;

        var import = new Button { Content = "Import…", FontSize = 12 };
        var remove = new Button { Content = "Remove", FontSize = 12 };

        _translateModUi = new CheckBox
        {
            Content = "Translate UGT Mod's interface",
            IsChecked = _draft.TranslateModUi,
        };
        ToolTip.SetTip(_translateModUi,
            "Written with the file, into games that have no answer of their own. A game where it was "
            + "switched on or off keeps its choice.");

        void Show()
        {
            var file = library.Current;

            Ui.Say(state, file is null
                    ? $"No file imported. Take the {ModUi.FileName} of a game where UGT Mod translated its interface."
                    : $"{file.Language} · {Composition.Amount(file.Lines, "line", "lines")} · "
                      + $"imported {file.ImportedUtc.ToLocalTime():d MMM yyyy}",
                file is null ? Tone.Neutral : Tone.Success);

            remove.IsVisible = file is not null;

            // Only with a file: the switch travels with it, and says nothing on its own.
            _translateModUi.IsEnabled = file is not null;
        }

        import.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Select a {ModUi.FileName}",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
            });

            var path = picked.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;

            var refusal = library.Import(path);
            Ui.Say(problem, refusal ?? "", Tone.Error);
            Show();

            // Importing a file IS saying one wants it used (user, 2026-09-25): ticked for them — a
            // pending change like any other, counted in Apply (N), never saved by the import itself.
            if (refusal is null) _translateModUi.IsChecked = true;
        };

        remove.Click += (_, _) =>
        {
            library.Remove();
            Ui.Say(problem, "");
            Show();
        };

        panel.Children.Add(state);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { import, remove },
        });
        panel.Children.Add(problem);
        panel.Children.Add(_translateModUi);
        panel.Children.Add(Note(
            "Placed only in games translating into the same language, and only where the game has no "
            + "interface file yet. Improved it in a game? Import that game's file again."));

        Show();

        return Card("UGT Mod interface",
            "A translation of UGT Mod's own panels, placed in your games.", panel);
    }

    /// <summary>
    /// The mod's optional shortcuts, folded under the panel key — "Additional hotkeys", the mod's own
    /// name for them (user's decision, 2026-09-25: global here, reproduced in a game's own settings).
    ///
    /// ⚠ Folded: all ten are optional and empty by default, and unrolled they would bury the rest of
    /// the card. The header says how many are set, so folding hides a form, never a fact.
    ///
    /// ⚠ They FILL a game, never replace a shortcut it already has — said in the intro, because it
    /// is what somebody needs to know before expecting a game to change.
    /// </summary>
    private Control AdditionalHotkeys()
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };

        // ⚠ No second copy of HotkeyAdvice: it is written once, under the panel key, in this same card.
        body.Children.Add(Note("Optional. None is set by default. A game that already has a shortcut "
                               + "keeps it; change it on that game's page."));

        foreach (var shortcut in ModShortcuts.All)
        {
            _draft.Shortcuts.TryGetValue(shortcut.Key, out var current);

            var editor = new HotkeyEditor(current, Brush("TextMuted"), Brush("StatusWarning"), optional: true);
            _shortcuts[shortcut.Key] = editor;

            body.Children.Add(ShortcutBlock(shortcut, editor.Row, editor.Problem));
        }

        // Its neighbours' header, exactly ("Tested models" in this window): with no colour of its own
        // a header takes the theme's default, which is dark on this dark card — the fold was there
        // and read as nothing.
        var header = new TextBlock { FontSize = 12, Foreground = Brush("TextSecondary") };

        void Count()
        {
            var set = _shortcuts.Values.Count(e => e.Value.Length > 0);
            header.Text = set == 0 ? "Additional hotkeys" : $"Additional hotkeys ({set} set)";
        }

        foreach (var editor in _shortcuts.Values) editor.Changed += Count;
        Count();

        return new Expander
        {
            Header = header,
            Content = body,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    /// <summary>One shortcut as the mod lays it out: its name, its hint, then the capture.</summary>
    internal static Control ShortcutBlock(ModShortcut shortcut, Control capture, Control problem,
                                          Control? origin = null)
    {
        var block = new StackPanel { Spacing = 2 };

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.Add(new TextBlock
        {
            Text = shortcut.Label,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Ui.Brush("TextPrimary"),
        });
        if (origin is not null) title.Children.Add(origin);

        block.Children.Add(title);
        block.Children.Add(Note(shortcut.Hint));
        block.Children.Add(capture);
        block.Children.Add(problem);
        return block;
    }

    // ---------------------------------------------------------------- AI

    /// <param name="reuseKnown">
    /// Show what the last search found instead of searching again. True when the window opens:
    /// probing six ports takes a couple of seconds, and nothing about the machine changed while
    /// this dialog was closed. False for "Search again" and after anything we did ourselves.
    /// </param>
    /// <param name="asked">
    /// True when a person pressed the button that says "Find local AI". What is found then
    /// goes into the field, whatever was in it — because that is the request. Left false, a found
    /// server only fills a field that is empty, so nothing anyone typed is taken away from them.
    /// </param>
    private async Task DiscoverAsync(bool reuseKnown = false, bool asked = false)
    {
        _populating = true;
        try
        {
            // Already set up? Then nothing happens until asked. Probing a configured server on
            // every visit is a request the person did not ask for — pointless on a local server
            // they know is running, and on a paid provider it is us touching their account to
            // answer a question nobody posed. What is saved is shown; the two buttons are right
            // there for when they want more.
            if (reuseKnown && !string.IsNullOrWhiteSpace(_draft.AiUrl))
            {
                await ShowConfiguredAsync();
                return;
            }

            if (reuseKnown && _aiServers.Remembered is { } known)
            {
                ShowServers(known, asked);
                return;
            }

            await DiscoverCoreAsync(asked);
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

    private async Task DiscoverCoreAsync(bool asked)
    {
        Say("Looking for a local AI server...");
        _aiModel.Items.Clear();
        _testButton.IsEnabled = false;

        // ⚠ Turning for the whole search, and stopped in a finally: an indicator left spinning
        // after a failure says the program is still working when it has given up, which is worse
        // than never having shown one.
        _aiGear.IsVisible = true;

        try
        {
            // Fetched alongside the search, never blocking it: a note is a nicety, a server list is
            // the screen's reason to exist. Offline settings mean no note and nothing else missing.
            _modelNotes ??= await new ModelNotesProvider(_platform)
                .GetAsync(offline: !_draft.OnlineMode);

            var servers = await _probe.DiscoverAsync();
            _aiServers.Remember(servers);

            ShowServers(servers, asked);
        }
        finally
        {
            _aiGear.IsVisible = false;
        }
    }

    /// <summary>
    /// Shows the saved setup, then checks that it still holds.
    ///
    /// One request to the address already saved — not a sweep of six ports, and not a search for
    /// something else. The difference matters: a saved setup does not need discovering, but it
    /// does deserve checking, because the model can be gone. Someone who removes a model from
    /// Ollama and later finds the game translating nothing has no way of connecting the two.
    ///
    /// What is saved appears first, so the screen is readable immediately and stays honest even
    /// if the check fails or the machine is offline.
    /// </summary>
    private async Task ShowConfiguredAsync()
    {
        _aiModel.Items.Clear();
        if (!string.IsNullOrWhiteSpace(_draft.AiModel))
        {
            _aiModel.Items.Add(_draft.AiModel);
            _aiModel.Reselect(_draft.AiModel);
        }

        _testButton.IsEnabled = _aiModel.SelectedItem is not null;
        _ollamaPanel.Children.Clear();
        _ollamaPanel.IsVisible = false;
        ShowModelNote();

        Say($"Checking {_draft.AiUrl}...");

        var models = await _probe.ListModelsAsync(_draft.AiUrl, _draft.AiApiKey);

        if (models is null)
        {
            // Not dressed up as a failure: a laptop away from its server, or a server not started
            // yet, is an ordinary situation and nothing here is broken. But amber, not grey: until
            // it answers, the mod translates nothing with it.
            Say($"{_draft.AiUrl} did not answer. It may not be running. Your settings are unchanged.",
                Tone.Warning);
            return;
        }

        // The list is refreshed while we are here, so the choice offered is what the server
        // actually holds rather than what it held the last time anyone looked.
        var saved = _draft.AiModel;
        _aiModel.Items.Clear();
        foreach (var model in models)
            _aiModel.Items.Add(model);

        var stillThere = !string.IsNullOrWhiteSpace(saved)
                         && models.Any(m => string.Equals(m, saved, StringComparison.Ordinal));

        if (stillThere)
        {
            Select(_aiModel, saved);
            SayWorked($"Connected to {_draft.AiUrl}: {Composition.Amount(models.Count, "model", "models")}. "
                      + $"{saved} is available.");
        }
        else if (!string.IsNullOrWhiteSpace(saved))
        {
            // Said loudly, and the saved value is NOT quietly replaced: swapping in another model
            // would leave someone believing they are running the one they chose. The selection is
            // left empty so the choice is visibly theirs to make.
            SayFailed($"Connected to {_draft.AiUrl}, but {saved} is no longer on this server. "
                      + "Choose another model, or install it again.");
        }
        else
        {
            SayWorked($"Connected to {_draft.AiUrl}: {Composition.Amount(models.Count, "model", "models")}. Choose one.");
        }

        _testButton.IsEnabled = _aiModel.SelectedItem is not null;
        ShowModelNote();

        // The path a returning user takes, and the one that had none of this: a saved address goes
        // straight here and never through the discovery branch, so the tested models were shown to
        // everybody except the people who had already set the tool up.
        await ShowTestedModelsAsync(_draft.AiUrl, models);
    }

    /// <summary>
    /// Puts a set of servers on screen. Split out so a remembered result and a fresh one produce
    /// exactly the same window — two code paths drawing the same thing is how they drift.
    /// </summary>
    private void ShowServers(IReadOnlyList<AiServer> servers, bool asked)
    {
        if (servers.Count == 0)
        {
            Say("No local AI server found. For a server on another computer, enter its address.",
                Tone.Warning);

            // Nothing answered: this is the only moment we are allowed to talk about installing
            // anything. What we offer depends on what is already on the machine, so ask first.
            _ = OfferOllamaAsync();
            return;
        }

        _ollamaPanel.Children.Clear();
        _ollamaPanel.IsVisible = false;

        var server = servers[0];

        // ⚠ Nothing is filled in unless AI is the backend that was CHOSEN — a second lock on the
        // same door the search now stands behind. Finding a server is a fact; writing its address
        // into the form is a decision, and it was being taken for the person: opening this screen
        // with "Community translations only" set left "Apply (2)" waiting on a form nobody had
        // touched, and Apply is the natural way to leave.
        //
        // 🔸 But pressing "Find local AI" IS that decision, so what turns up goes in the
        // field even when something else was there. Only filling an EMPTY field made the button
        // useless in the case it exists for: someone with a wrong address typed in, who searches
        // and is shown a server that is answering while the box keeps the address that is not.
        var chosen = Tag(_backend) == "llm";
        if (chosen && (asked || string.IsNullOrWhiteSpace(_aiUrl.Text))) _aiUrl.Text = server.Url;

        SayWorked($"Found {server.Product} at {server.Url}: {Composition.Amount(server.Models.Count, "model", "models")}.");

        // A server with nothing loaded is the state a fresh Ollama is left in, and the one that
        // reads as "it worked" while translating nothing. Offering a model here is the difference
        // between an engine and an engine with fuel.
        //
        // ⚠ An empty LM Studio lands here too, and the offer has to be honest with it: the table
        // still helps — it says which models are worth loading — but only Ollama can be asked to
        // fetch one.
        if (server.Models.Count == 0)
        {
            _ = OfferEmptyServerAsync(server.Url);
            return;
        }

        foreach (var model in server.Models)
            _aiModel.Items.Add(model);

        // Listing them is offering; selecting one is choosing. The list is filled either way — a
        // picker with nothing in it would be a dead end — but a model is only picked for somebody
        // once they have said they want AI at all.
        Select(_aiModel, _draft.AiModel);
        if (chosen) _aiModel.Reselect(_aiModel.SelectedItem ?? _aiModel.Items.FirstOrDefault());

        _testButton.IsEnabled = _aiModel.SelectedItem is not null;

        _ = ShowTestedModelsAsync(server.Url, server.Models);
    }

    /// <summary>
    /// What we have run ourselves, offered to somebody who already has models.
    ///
    /// The list used to appear only on a server holding nothing, which quietly assumed that
    /// whoever has a model has the right one. Somebody running a model that mangles the game's
    /// placeholders is precisely who it is for, and was the one person unable to reach it.
    ///
    /// Two situations, two behaviours. With a tested model among theirs, a button: they came for
    /// a setting, not a catalogue. With none — the ordinary case for anyone who installed Ollama
    /// before meeting us — the list opens by itself, because a button labelled "other models"
    /// suggests we have something to say about the ones they have, and we have nothing.
    /// </summary>
    private async Task ShowTestedModelsAsync(string serverUrl, IReadOnlyList<string> installed)
    {
        _ollamaPanel.Children.Clear();

        _modelNotes ??= await new ModelNotesProvider(_platform)
            .GetAsync(offline: !_draft.OnlineMode);

        // No list to show, and nothing to apologise for: the models on this server work or they
        // do not, and that is settled by the test button, not by us.
        if (_modelNotes is null)
        {
            _ollamaPanel.IsVisible = false;
            return;
        }

        _ollamaPanel.IsVisible = true;

        var noneTested = !installed.Any(model => ModelNotesProvider.For(_modelNotes, model) is not null);

        // 🔴 Asked of THIS server, every time, because this method is reached from an address
        // somebody typed as well as from one we discovered — and a saved address can be LM Studio,
        // a machine on the network, or a paid online provider. Every one of them used to be shown
        // a Download button that could only fail.
        var canDownload = await new OllamaModelPuller(serverUrl).CanDownloadAsync();

        await OfferModelAsync(serverUrl, alreadyHasModels: true, noneOfTheirsTested: noneTested,
                              startExpanded: noneTested, canDownload: canDownload);
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
            // Amber: nothing translates until it runs.
            _ollamaPanel.Children.Add(Note("Ollama is installed but not running.", Tone.Warning));

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
                        _ollamaPanel.Children.Add(Note($"To stop it later: {outcome.HowToStop}"));

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
                        "Starting it needs administrator rights. Run this command in a terminal, "
                        + "then click Find local AI:", Tone.Warning));
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
                        outcome.Failure ?? "Ollama did not start. Start it yourself, then click "
                        + "Find local AI.", Tone.Warning));
                }
            };

            _ollamaPanel.Children.Add(start);
            return;
        }

        var installer = new OllamaInstaller(_platform);

        // Two network calls behind this, and until now nothing on screen while they ran. On a
        // slow link that is several seconds of a panel that looks empty and finished.
        var checking = new SpinningGear("Checking the latest Ollama version...");
        _ollamaPanel.Children.Add(checking);

        var offer = await installer.PrepareAsync();
        _ollamaPanel.Children.Remove(checking);

        if (!offer.CanInstall)
        {
            _ollamaPanel.Children.Add(Note(offer.Refusal ?? "Ollama cannot be installed from here."));
            return;
        }

        _ollamaPanel.Children.Add(Note(
            "Ollama runs AI models on your computer. Free, no account needed. "
            + $"Download: {offer.SizeText} for the program, plus a model. It uses your graphics card."));

        // Where a failure is said once the veil is down — beside the button that can try again.
        var progress = Note("");
        var install = new Button { Content = $"Install Ollama ({offer.SizeText})", FontSize = 12 };

        // 🔴 Subscribed ONCE, outside the click: inside it, every retry stacked another handler.
        //
        // ⚠ The veil is asked for at the moment of use, never while this panel is being built —
        // see WorkOverlay.On, which wraps whatever the window's Content is at that moment.
        installer.Progress += (done, total) => Dispatcher.UIThread.Post(() =>
            WorkOverlay.On(this).Report(total is { } t
                ? done >= t
                    // The download is whole; what runs now is Ollama's own installer, then the
                    // wait for its server to answer. Said, so the last line is not "N of N MB"
                    // standing still for as long as that takes.
                    ? "Installing Ollama and starting it..."
                    : $"Downloading... {done / 1024.0 / 1024:F0} of {t / 1024.0 / 1024:F0} MB"
                : $"Downloading... {done / 1024.0 / 1024:F0} MB"));

        install.Click += async (_, _) =>
        {
            // The window darkened while a program is installed on this machine — the same veil as
            // an install into a game, and for the same reason: nothing here is worth pressing
            // until it is known whether Ollama is there.
            var veil = WorkOverlay.On(this);
            veil.Show("Installing Ollama...");

            string? failure;
            try
            {
                failure = await installer.InstallAsync(offer);
            }
            finally
            {
                veil.Hide();
            }

            if (failure is null)
            {
                Ui.Say(progress, "Installed. Searching for it...");
                _aiServers.Forget();
                await DiscoverAsync();
                return;
            }

            Ui.Say(progress, failure, Tone.Error);
        };

        _ollamaPanel.Children.Add(install);
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
    /// <param name="alreadyHasModels">
    /// Changes what is said, never what is offered. "No model here yet" is a fix; the same list
    /// shown to someone who already has one is a comparison, and calling it a fix would read as
    /// "yours is wrong" about a model we have never run.
    /// </param>
    /// <param name="noneOfTheirsTested">
    /// Said plainly, because it explains why this list appeared unasked — and because the reader
    /// might otherwise take our silence about their models for approval of them.
    /// </param>
    /// <param name="startExpanded">
    /// Open when there is nothing else to go on — an empty server, or nothing of theirs we have
    /// ever run. Closed when it is merely a comparison, which is most visits.
    /// </param>
    /// <summary>
    /// The offer for a server that answered but holds nothing, whatever server it is.
    ///
    /// Split from the discovery branch so the download question is asked there too: an empty
    /// LM Studio used to be handed the same Download buttons as an empty Ollama.
    /// </summary>
    private async Task OfferEmptyServerAsync(string serverUrl) =>
        await OfferModelAsync(serverUrl,
            canDownload: await new OllamaModelPuller(serverUrl).CanDownloadAsync());

    /// <param name="canDownload">
    /// Whether this server can fetch a model. Defaults to true because the callers that omit it
    /// are the Ollama paths — an install we just performed, or a server we just started — where
    /// the answer is known without asking. Everything reached from a server somebody else set up
    /// passes the answer in.
    /// </param>
    private async Task OfferModelAsync(string serverUrl, bool alreadyHasModels = false,
                                       bool noneOfTheirsTested = false, bool startExpanded = true,
                                       bool canDownload = true)
    {
        _ollamaPanel.Children.Clear();
        _ollamaPanel.IsVisible = true;

        _modelNotes ??= await new ModelNotesProvider(_platform)
            .GetAsync(offline: !_draft.OnlineMode);

        var vram = _platform.VideoMemoryBytes();
        var candidates = ModelNotesProvider.Installable(_modelNotes, vram);

        if (candidates.Count == 0)
        {
            // Amber only when there is no model at all: then nothing translates. A server that
            // has one is fine without our list.
            _ollamaPanel.Children.Add(Note(
                alreadyHasModels
                    ? "Could not load the list of tested models. Your model still works."
                    : "This server has no model yet, and the list of tested models could not be "
                      + "loaded. Any Ollama model works.",
                alreadyHasModels ? Tone.Neutral : Tone.Warning));
            return;
        }

        var card = vram is { } bytes
            ? $"Your graphics card has {bytes / 1024.0 / 1024 / 1024:F0} GB"
            : "Could not read your graphics card memory";

        var content = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };

        // ⚠ "We have not tested yours" is about us, never a judgement on their models — and it is
        // why this list opened by itself (see startExpanded).
        content.Children.Add(Note(
            noneOfTheirsTested
                ? $"We have not tested the models on this server. {card}. "
                  + "Downloading one below keeps your current models."
                : alreadyHasModels
                    ? $"{card}. If your model works well, keep it. Downloading another one keeps both."
                    : $"No model on this server yet. {card}.",
            alreadyHasModels ? Tone.Neutral : Tone.Warning));

        // Which language the figures were taken in, and — when it is not the reader's — that the
        // button beside the model list answers the same questions in theirs. A table of marks
        // gathered in another language reads as a verdict unless it says otherwise, and marker
        // fidelity genuinely does move between languages.
        if (_modelNotes?.MeasuredIn is { Length: > 0 } measuredIn)
        {
            var mine = Languages.NameOf(_store.ResolveTargetLanguage());

            var sameLanguage = string.Equals(mine, measuredIn, StringComparison.OrdinalIgnoreCase);

            // The card matters as much as the language, and differently: what a model HOLDS is a
            // fact about the model and travels, whether it FITS is a fact about the card — and the
            // reader's own card size is already in the line above, so it is not repeated here.
            var said = $"Measured translating into {measuredIn}";
            if (_modelNotes.MeasuredOn is { Length: > 0 } testedOn) said += $" on a {testedOn}";
            said += ".";

            // A model can keep the game's markers in one language and lose them in another.
            if (!sameLanguage) said += $" Results may differ in {mine}.";

            // Amber when the figures were not taken in the reader's language: then they are a hint,
            // not a measure of what this model will do for them. Never faded — a warning is read.
            content.Children.Add(Note(said + " \"Test this model\" checks it on your computer.",
                                      sameLanguage ? Tone.Neutral : Tone.Warning));
        }

        var progress = Note("");

        // 🔴 Where these names come from, said once, above the table — and only where it changes
        // what the reader can do. On Ollama the names ARE the download, so naming them would be
        // noise; anywhere else they are a shopping list to take elsewhere, and a table of models
        // with no way to get them reads as a broken screen unless it says why.
        if (!canDownload)
        {
            content.Children.Add(Note(
                "These are Ollama model names. Only Ollama can download them from here. On another "
                + "server, install the model your usual way; it then appears in the Model list."));
        }

        content.Children.Add(ModelTable(candidates, vram, serverUrl, progress, canDownload));
        content.Children.Add(progress);

        // An expander rather than a block dropped on the screen: it opens where it is relevant and
        // — the part that was missing — closes again. Open by itself only when there is nothing
        // else to go on, closed when it is a comparison somebody may not want.
        var smallest = candidates.Select(note => note.Measured?.VramGb)
                                 .Where(gb => gb is not null)
                                 .DefaultIfEmpty(null)
                                 .Min();

        var lightestText = smallest is { } gb ? $" (from {gb:F1} GB of video memory)" : "";

        _ollamaPanel.Children.Add(new Expander
        {
            Header = new TextBlock
            {
                Text = $"Tested models: {candidates.Count}{lightestText}",
                FontSize = 12,
                Foreground = Brush("TextSecondary"),
            },
            Content = content,
            IsExpanded = startExpanded,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        });
    }

    /// <summary>
    /// The measurements as a table, because that is what they are.
    ///
    /// They used to be prose under a button each, which made comparing two models a reading
    /// exercise — the one thing a reader in this screen is trying to avoid. Columns line up, so
    /// "half the memory for the same result" is seen rather than worked out.
    ///
    /// Two rows carry a mark, and only two: what this project develops against, and the lightest
    /// that followed everything. Both are facts, not opinions, and each answers a question somebody
    /// arrives with — "what do you run yourselves" and "I have a small card". A mark on every row
    /// would be a mark on none, which is what the previous one had quietly become: its condition
    /// was met by nine rows out of ten.
    ///
    /// ⚠ The marks are INFORMATIVE, not green. Green reads as approval, and neither claim is one:
    /// the reference is a 16 GB model most readers should not start with, and the lightest needed
    /// four retries out of twenty. The amber in the columns is where a cost is stated.
    /// </summary>
    /// <param name="canDownload">
    /// Whether this server can be asked to fetch a model — see
    /// <see cref="OllamaModelPuller.CanDownloadAsync"/>. False leaves every row exactly as it is,
    /// figures included, and takes only the button away: what a model held and how many
    /// instructions it followed are facts about the MODEL, true on any server, and someone running
    /// LM Studio is entitled to them. Only the one-click fetch is Ollama's.
    /// </param>
    private Control ModelTable(IReadOnlyList<ModelNote> candidates, long? vram,
                               string serverUrl, TextBlock progress, bool canDownload)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 2,
            ColumnSpacing = 12,
        };

        void Put(Control control, int row, int column, int span = 1)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            if (span > 1) Grid.SetColumnSpan(control, span);
            grid.Children.Add(control);
        }

        TextBlock Head(string text) => new()
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextMuted"),
        };

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Put(Head("MODEL"), 0, 0);
        Put(Head("HELD"), 0, 1);
        Put(Head("DOWNLOAD"), 0, 2);

        // ⚠ Its own column, and it belongs beside the others rather than in the prose: it is paid
        // while a GAME is starting, it is the widest spread of anything measured here, and it does
        // not follow the download size — a model can be quick per line and slow to arrive.
        Put(Head("LOAD"), 0, 3);

        Put(Head("PER LINE"), 0, 4);

        // A model can follow every instruction and still need three goes at some of them. Same
        // result, three times the wait and three times the card — which is a choice, not a detail.
        Put(Head("RETRIES"), 0, 5);

        Put(Head("SUITE"), 0, 6);

        var line = 1;

        foreach (var candidate in candidates)
        {
            var fits = ModelNotesProvider.Fits(candidate, vram);
            var standout = ModelNotesProvider.Standout(candidate, candidates);
            var measured = candidate.Measured;

            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var name = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
            };

            name.Children.Add(new TextBlock
            {
                Text = candidate.Pull,
                FontSize = 13,
                FontWeight = standout is null ? FontWeight.Normal : FontWeight.SemiBold,
                Foreground = Brush("TextPrimary"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });

            if (standout is not null)
            {
                name.Children.Add(new Border
                {
                    Background = Brush("CalloutInfoBg"),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 1),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = standout,
                        FontSize = 10,
                        Foreground = Brush("StatusInfo"),
                    },
                });
            }

            // ⚠ A mark rather than a column, because it is rare — one model in ten. A column of
            // dashes would spend a seventh of the width saying "no" nine times.
            //
            // Quiet on purpose: it qualifies an option that stays experimental, so it must not
            // compete with the mark above it, which says what this project runs on.
            if (measured?.StrictSource == true)
            {
                var strict = new Border
                {
                    BorderBrush = Brush("BorderStrong"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 1),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "strict source",
                        FontSize = 10,
                        Foreground = Brush("TextMuted"),
                    },
                };

                // Earned by refusing both a real foreign language and an invented one (the two
                // experimental cases of the suite).
                ToolTip.SetTip(strict,
                    "Works with the experimental 'strict source' option. When that option fails, it "
                    + "silently skips text that was fine.");

                name.Children.Add(strict);
            }

            Put(name, line, 0);

            TextBlock Figure(string text, string colour = "TextSecondary") => new()
            {
                Text = text,
                FontSize = 12,
                Foreground = Brush(colour),
                Margin = new Thickness(0, 8, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            // The memory it actually held, next to the card size we ask for. The second is the
            // first plus headroom, and showing only one of them is how someone concludes we
            // invented a requirement.
            Put(Figure(measured?.VramGb is { } held ? $"{held:F1} GB" : "—",
                       fits == false ? "StatusWarning" : "TextSecondary"), line, 1);

            Put(Figure(candidate.DownloadGb is { } dl ? $"{dl:F1} GB" : "—"), line, 2);

            // The wait before the first line of a session, paid while a game is starting.
            Put(Figure(measured?.LoadSeconds is { } load ? $"{load:F0}s" : "—"), line, 3);

            // What a line costs in waiting, which is the figure somebody actually feels.
            Put(Figure(measured?.TypicalSeconds is { } typical ? $"{typical:F1}s" : "—"), line, 4);

            // Amber as soon as there is one: it is not a failure — the line came out right — but it
            // is the same line paid for twice, and that is what the reader is weighing.
            //
            // 🔴 A line the model never got right is shown HERE, in red, and not left to be inferred
            // from the suite column. It is the first thing the order sorts on, and text left in its
            // original language while somebody plays is not the same kind of cost as a wait. Both
            // figures are out of the same twenty lines, which is why they share a cell rather than
            // needing an eighth column.
            var retries = measured is { Retried: { } tries, Lines: { } all } ? $"{tries}/{all}" : "—";
            if (measured?.Refused is { } lost && lost > 0) retries += $" · {lost} failed";

            Put(Figure(retries,
                       measured?.Refused > 0 ? "StatusError"
                       : measured?.Retried > 0 ? "StatusWarning"
                       : "TextSecondary"), line, 5);

            Put(Figure(measured is { Suite: { } suite, SuiteOf: { } of } ? $"{suite}/{of}" : "—",
                       measured is { Suite: { } s, SuiteOf: { } o } && s < o ? "StatusWarning" : "TextSecondary"),
                line, 6);

            line++;

            // The prose under the figures, spanning the width: it says the thing no column can,
            // and it is what someone reads once the table has narrowed the choice to two.
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var details = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 0) };
            details.Children.Add(Note(candidate.Note));

            // Kept apart from the sentence above on purpose: that one is what we measured, this
            // one is what the publisher says, and we have verified none of it.
            if (candidate.Languages?.Sentence() is { } coverage)
            {
                var claim = Note($"On coverage, {coverage}.");
                claim.Opacity = 0.75;
                details.Children.Add(claim);
            }

            if (fits == false)
            {
                details.Children.Add(Note(
                    $"Too big for your graphics card (needs about {candidate.MinVramGb:F0} GB). It runs "
                    + "on the processor instead: minutes per line instead of seconds.", Tone.Warning));
            }

            // 🔴 Absent rather than greyed on a server that cannot fetch. A disabled button says
            // "not now"; there is no now — this server will never be able to, and the sentence
            // above the table has already said where the names come from. The rule everywhere else
            // in this program: a verb that cannot act does not appear.
            if (canDownload)
            {
                var take = new Button
                {
                    Content = "Download",
                    FontSize = 11,
                    Padding = new Thickness(10, 3),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Margin = new Thickness(0, 4, 0, 0),
                    Classes = { "primary" },
                };

                take.Click += async (_, _) =>
                {
                    take.IsEnabled = false;
                    await PullModelAsync(serverUrl, candidate.Pull!, progress);
                    take.IsEnabled = true;
                };

                details.Children.Add(take);
            }
            Put(details, line, 0, span: 5);
            line++;
        }

        return grid;
    }

    /// <summary>Pulls one model, saying where it is up to the whole way.</summary>
    private async Task PullModelAsync(string serverUrl, string model, TextBlock progress)
    {
        var puller = new OllamaModelPuller(serverUrl);

        puller.Progress += (status, done, total) => Dispatcher.UIThread.Post(() =>
            progress.Text = done is { } d && total is { } t && t > 0
                ? $"{status} — {d / 1024.0 / 1024 / 1024:F1} of {t / 1024.0 / 1024 / 1024:F1} GB"
                : status);

        Ui.Say(progress, "Starting...");

        // Several gigabytes: the byte counter answers "how far", the gear answers "is it still
        // going" during the stretches where the counter does not move.
        var pulling = new SpinningGear($"Downloading {model}...");
        _ollamaPanel.Children.Add(pulling);

        var failure = await puller.PullAsync(model);
        _ollamaPanel.Children.Remove(pulling);

        if (failure is null)
        {
            Ui.Say(progress, "Downloaded. Refreshing the model list...");
            _aiServers.Forget();
            await DiscoverAsync();
            Select(_aiModel, model);
            return;
        }

        Ui.Say(progress, failure, Tone.Error);

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

    /// <summary>
    /// Fills "From" with the sources we hold, minus the one being translated into.
    ///
    /// Offering it would let somebody ask a model to translate a language into itself, which the
    /// mod never does and which answers nothing — measured, some models reply in a third language
    /// entirely. So the list is rebuilt whenever "Into" moves, and any earlier result is cleared:
    /// marks gathered for another pair are marks under the wrong heading.
    /// </summary>
    /// <summary>
    /// Says what pressing the button will spend, and updates as the pair of languages changes.
    ///
    /// 🔴 **Because this run is not free for everyone.** On a local server it costs a wait; on a
    /// paid endpoint it is billed per request and per token, and a bench that quietly fires
    /// several dozen requests is a bill nobody agreed to. So the figures come BEFORE the button,
    /// not in a summary afterwards.
    ///
    /// ⚠ Counted from the cases themselves (ModelTestSuite.Cost), never written down here: add a
    /// case and this follows on its own. A number typed into this screen would be right the day it
    /// was typed and quietly wrong at the next commit.
    ///
    /// ⚠ The token figure is announced as approximate and has to stay that way — four characters
    /// to a token holds for Latin scripts and not at all for Chinese, where it is closer to one.
    /// </summary>
    private void ShowTestCost()
    {
        var language = Tag(_testInto) ?? _store.ResolveTargetLanguage();
        var cost = ModelTestSuite.Cost(language, sourceCode: Tag(_testFrom), rate: true);

        var retries = cost.MostRequests > cost.Requests
            ? $" (up to {cost.MostRequests} with retries)"
            : "";

        // Amber only where it costs money: the same address test the AI card's caution uses. On a
        // server this person runs, the figures are a wait, not a bill.
        var url = _aiUrl.Text?.Trim() ?? "";
        var paid = url.Length > 0 && !Endpoints.IsOnYourOwnNetwork(url);

        Ui.Say(_testCost,
            $"{cost.Cases} tests: {cost.Requests} requests{retries}, about {cost.AboutTokens:N0} tokens."
            + (paid ? " An online service bills each one." : ""),
            paid ? Tone.Warning : Tone.Neutral);
    }

    private void RefreshTestSources()
    {
        _testOutput.Children.Clear();

        var into = Tag(_testInto) ?? _store.ResolveTargetLanguage();
        var wanted = ModelTestSuite.SourceFor(into).Code;

        // 🔴 **The NAME comes from the shared catalogue, never from the fixture's own label.** A
        // fixture calls its language whatever its author wrote — "Chinese" where the catalogue says
        // "Simplified Chinese" — and a name that is not the catalogue's is a name nothing can match:
        // the flag is looked up by it, so the row lost its flag and read differently from every
        // other language list in the product. The fixture decides WHICH languages can be tested;
        // what each one is called is not its to say.
        LanguageMark.Fill(_testFrom, Fixtures.All
            .Where(set => !string.Equals(set.Code, into, StringComparison.OrdinalIgnoreCase))
            .Select(set => (set.Code, Languages.NameOf(set.Code) ?? set.Language)));

        Select(_testFrom, wanted);
    }

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
    private async Task TestConnectionAsync(bool asRefresh = false)
    {
        var url = _aiUrl.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            SayFailed("Enter an address first.");
            return;
        }

        _connectButton.IsEnabled = false;
        _refreshModels.IsEnabled = false;
        Say(asRefresh ? "Reading the model list..." : "Connecting...");
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
            SayFailed($"No answer from {url}. Check the address, and the key if this is an "
                      + "online provider: a rejected key looks exactly like a wrong address.");
            _connectButton.IsEnabled = true;
            _refreshModels.IsEnabled = true;
            _populating = false;
            Dispatcher.UIThread.Post(RefreshApplyButton, DispatcherPriority.Background);
            return;
        }

        SayWorked(asRefresh
            ? $"{Composition.Amount(models.Count, "model", "models")} on the server."
            : $"Connected — {Composition.Amount(models.Count, "model", "models")} offered.");
        foreach (var name in models)
            _aiModel.Items.Add(name);

        Select(_aiModel, _draft.AiModel);
        _aiModel.Reselect(_aiModel.SelectedItem ?? _aiModel.Items.FirstOrDefault());
        _testButton.IsEnabled = _aiModel.SelectedItem is not null;
        _connectButton.IsEnabled = true;
        _refreshModels.IsEnabled = true;

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

        _suiteStop = new CancellationTokenSource();
        _testButton.Content = "Stop tests";
        _testOutput.Children.Clear();

        // Sits at the bottom of the list, which is where the next result will appear. A model
        // slow enough to spill out of video memory takes long enough between answers that a
        // finished-looking screen is the honest reading — this is what says otherwise.
        var waiting = new SpinningGear("Measuring how long a line takes...");
        _testOutput.Children.Add(waiting);

        // Measured first, and shown on its own line: what a model costs to run is a different
        // question from whether it obeys, and both decide whether someone keeps it.
        Ui.Say(_metrics, "Measuring...");

        // Declared out here: the summary below the try reads them, and a cancellation must
        // still leave them in a state that can be reported.
        var passed = 0;
        var required = 0;
        var echoed = 0;
        var done = 0;
        var experimentalStarted = false;
        var outcomes = new List<ModelTestResult>();

        // ⚠ try/finally, not a defensive catch: the button has to come back whatever
        // happens, and a cancellation is a path the reader ASKED for rather than a fault
        // to swallow — it is reported on screen just below.
        try
        {
            // The token reaches the measurement too, or "Stop" would sit unanswered through the four
            // requests this makes before the suite even starts.
            var trial = await _probe.MeasureAsync(url, model, _suiteStop.Token);
            // ⚠ The wording lives on AiTrial, not here: the CLI prints the same fact and the two
            // must not phrase it differently. It also states a SHARE — a model 83% on the card was
            // being announced as running on the processor.
            var gpu = trial.GpuText;

            if (trial.Succeeded)
            {
                // Amber when part of the model runs on the processor: that is the reading which
                // explains slow lines, and in grey it sat unread among the timings.
                _metrics.Foreground = Brush(trial.SplitWithProcessor ? "StatusWarning" : "TextSecondary");
                _metrics.Text =
                    $"First line: {trial.Elapsed.TotalSeconds:F1}s "
                    + (trial.FirstRunWasCold ? "(model loading)" : "(model already loaded)")
                    + $" · then {(trial.WarmElapsed ?? trial.Elapsed).TotalSeconds:F1}s per line"
                    + $" · {trial.VramText} of video memory · GPU {gpu}."
                    + Environment.NewLine
                    + "Measured with no game running. Expect slower while playing.";
            }
            else
            {
                Ui.Say(_metrics, $"Could not measure ({trial.Detail}).", Tone.Error);
            }

            // The pair the reader chose on this screen, not the setting being edited: this test asks
            // "what would this model do", and that question is theirs to aim.
            var language = Tag(_testInto) ?? _store.ResolveTargetLanguage();
            var sourceCode = Tag(_testFrom);


            // Known before the first request, which is the point: "3 of 9" tells someone there is
            // more coming. A bare spinner would not.
            var total = ModelTestSuite.Build(language, sourceCode: sourceCode).Count;
            waiting.Message = $"Running test 1 of {total}...";

            var from = ModelTestSuite.SourceFor(language, sourceCode);

            // The ceiling is the mod's own (Placeholders.MaxAttempts), never a number typed here.
            _testOutput.Children.Add(Note(
                $"From {from.Language} into {Languages.NameOf(language)}, like UGT Mod: up to "
                + $"{Placeholders.MaxAttempts} attempts per line, then the line is left untranslated. "
                + "The times are what you would wait in the game."));

            await _probe.RunSuiteAsync(url, model, language, ct: _suiteStop.Token,
                                      sourceCode: sourceCode, rate: true,
                                      onResult: result =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (result.Test.UnlocksOption is null && !result.Test.ForReading)
                    {
                        required++;
                        if (result.Passed) passed++;
                    }
                    if (result.EchoedInstructions) echoed++;

                    outcomes.Add(result);
                    done++;

                    // The experimental cases are a different subject and had nothing to say so: they
                    // sat in the same list, in the same shape, as though a "cannot" there counted
                    // against the model. It does not — the mod ships that option off — and the score
                    // above does not include them. A heading is the cheapest way to stop the reader
                    // adding them up with the rest.
                    if (result.Test.UnlocksOption is not null && !experimentalStarted)
                    {
                        experimentalStarted = true;

                        _testOutput.Children.Insert(_testOutput.Children.Count - 1, new TextBlock
                        {
                            Text = "EXPERIMENTAL — not counted above",
                            FontSize = 10,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = Brush("TextMuted"),
                            Margin = new Thickness(0, 10, 0, 0),
                        });

                        // Both must pass: refusing invented words while still translating other
                        // real languages is not what the option promises.
                        var aside = Note("These two only decide whether UGT Mod's 'strict_source' "
                                         + "option works with this model. It is off by default, so "
                                         + "\"cannot\" is not a failure. Both must pass.");
                        aside.Margin = new Thickness(0, 0, 0, 4);
                        _testOutput.Children.Insert(_testOutput.Children.Count - 1, aside);
                    }

                    // Inserted above the gear so the gear stays last: results accumulate, and the
                    // thing that says "more is coming" keeps sitting where the next one will land.
                    _testOutput.Children.Insert(_testOutput.Children.Count - 1, TestRow(result));

                    waiting.Message = done < total
                        ? $"Running test {done + 1} of {total}..."
                        : "Finishing...";
                    waiting.IsVisible = done < total;
                });
            });
        }
        catch (OperationCanceledException)
        {
            _testOutput.Children.Add(Note("Stopped. The results above are from before you stopped.",
                                          Tone.Info));
            return;
        }
        finally
        {
            // The spinner is normally taken down by the summary below; on a cancellation there is
            // no summary, and a gear left turning says the run is still going.
            _testOutput.Children.Remove(waiting);

            _suiteStop?.Dispose();
            _suiteStop = null;
            _testButton.Content = "Test this model";
            _testButton.IsEnabled = _aiModel.SelectedItem is not null;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _testOutput.Children.Remove(waiting);

            _testOutput.Children.Add(new TextBlock
            {
                Text = $"Passed {passed} of {required} required tests.",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = Brush(passed == required ? "StatusSuccess" : "StatusWarning"),
            });

            // ⚠ Its own line, below the score and in the muted colour the marks themselves use —
            // never folded into the figure above. That one counts instructions a machine verified;
            // this one is the model grading its own work, in a language it may not even have. Two
            // different kinds of claim, and merging them would give the weaker one the authority
            // of the stronger. See ModelTestResult.SelfAssessment.
            var marks = outcomes.Where(r => r.SelfAssessment is not null)
                                .Select(r => r.SelfAssessment!.Value)
                                .ToList();

            if (marks.Count > 0)
            {
                _testOutput.Children.Add(Note(
                    $"Self-assessment: {marks.Average():F1}/10 on average ({marks.Count} answers). "
                    + "The model's own opinion, not a test result."));
            }

            // Said next to the mark, never inside it: a line that passed after the mod corrected
            // something does work in a game, and the model still got it wrong first. Two models
            // can share a score and not share that.
            var helped = outcomes.Count(r => r.Test.UnlocksOption is null && r.PassedWithHelp);
            if (helped > 0)
            {
                _testOutput.Children.Add(Note(
                    $"{Composition.Amount(helped, "test", "tests")} passed only after UGT Mod fixed "
                    + "the answer."));
            }

            // The figures a player actually needs, and only those — what a line costs them in
            // waiting, how often it had to be asked twice, and what stayed untranslated.
            if (ModelTestSuite.Summarise(outcomes) is { Length: > 0 } summary)
            {
                _testOutput.Children.Add(new TextBlock
                {
                    Text = summary,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                    // Amber when lines were given up on: that sentence is the warning of the run.
                    Foreground = Brush(ModelTestSuite.AnyGaveUp(outcomes) ? "StatusWarning" : "TextSecondary"),
                });
            }

            // Said with the times, because it usually explains them: a model split with the
            // processor is not slow, it is too big for this card, and the answer is a smaller one.
            if (_probe.LastPlacement is { } placement)
            {
                var where = Note($"This model holds {placement}",
                                 _probe.LastPlacementSplit ? Tone.Warning : Tone.Neutral);
                where.FontSize = 12;
                _testOutput.Children.Add(where);
            }

            if (echoed > 0)
            {
                // Reason enough on its own: the mod puts what comes back into the game, word for word.
                _testOutput.Children.Add(Note(
                    $"{Composition.Amount(echoed, "answer", "answers")} repeated the instructions. "
                    + "Avoid this model: UGT Mod shows its answers in the game as they are.",
                    Tone.Warning));
            }

            _testButton.IsEnabled = true;
        });
    }

    private Control TestRow(ModelTestResult result)
    {
        var experimental = result.Test.UnlocksOption is not null;

        // A case with no verdict is a third outcome: nobody judged it. Neither mark would be
        // true, and either would move a score that is meant to say something precise.
        var mark = result.Test.ForReading
            ? "read"
            : experimental
                ? (result.Passed ? "can" : "cannot")
                : (result.Passed ? "pass" : "fail");

        // An experimental test never fails a model, so "cannot" must not be red — that would read
        // as a defect where there is none. But it was grey on both sides, and grey buried the one
        // outcome worth seeing: "can" means an option the mod keeps off can be switched on for
        // this model, and almost no model manages it. Green for the gain, amber for the closed
        // door — visible, and unmistakably not an error.
        // Blue, the colour this program already uses for something worth knowing that is not a
        // judgement — never the grey of a disabled thing, which would read as "skipped".
        var colour = result.Test.ForReading
            ? "StatusInfo"
            : experimental
                ? (result.Passed ? "StatusSuccess" : "StatusWarning")
                : (result.Passed ? "StatusSuccess" : "StatusError");

        var body = new StackPanel { Spacing = 2 };

        // Verdict on the left, what it cost on the right. A model that gets there on the third
        // attempt is not the model that gets there on the first, and the difference is a wait the
        // player sits through with the original text still on screen.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var title = new TextBlock
        {
            Text = $"[{mark}]  {result.Test.Name}",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = Brush(colour),
        };

        Grid.SetColumn(title, 0);
        header.Children.Add(title);

        // Always written, and always over the ceiling. The count alone hid two things: that a line
        // asked once is a line that went well, and that a line asked three times came within one
        // attempt of being left in its original language. Nothing here is worth hiding — the way
        // the mod works is the thing being measured.
        var cost = $"{result.Elapsed.TotalSeconds:F1}s · "
                 + (result.Test.CanBeAskedAgain
                    ? $"{result.Attempts} of {Placeholders.MaxAttempts} requests"
                    // Nothing to check in the answer, so nothing to ask again.
                    : $"{result.Attempts} request (no retry)");

        if (!result.Accepted) cost += " · failed, left untranslated";
        else if (result.Repaired) cost += " · fixed by UGT Mod";

        // Not a failure: the mod takes this off before a player sees it. Said because it is a
        // habit rather than an accident — a model that wraps one answer wraps them all, and that
        // separates two models that both pass.
        if (result.NeededCleaning) cost += " · extra text removed by UGT Mod";

        var costText = new TextBlock
        {
            Text = cost,
            FontSize = 11,
            Foreground = Brush(result.Accepted ? "TextMuted" : "StatusError"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        Grid.SetColumn(costText, 1);
        header.Children.Add(costText);

        body.Children.Add(header);

        body.Children.Add(new TextBlock
        {
            Text = $"asked: {result.Test.Source.ReplaceLineEndings(" / ")}",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        });

        // 🔴 **What was being asked FOR, before what came back.** The command line has always
        // printed this and this window never did, so a failed case showed a question and an answer
        // and no reason — and on the two refusal cases the answer is a perfectly good translation,
        // marked wrong, with nothing on screen saying the model was supposed to decline instead.
        // Somebody reading that concludes the tester is broken, and they are right to.
        //
        // ⚠ Before the answer, not after: the expectation is the question the answer is judged
        // against, and read afterwards it arrives as an excuse for a verdict already given.
        //
        // ⚠ Not only on failures. A case that passed still says what it was asked to do — that is
        // what makes the twenty-two of them readable as a list rather than as a score.
        if (result.Test.Expectation is { Length: > 0 } expectation)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"expected: {expectation}",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = $"answer: {result.Answer?.ReplaceLineEndings(" / ") ?? "(empty)"}",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        // What the reader is being asked to look at. Only these cases carry it, and it is the
        // whole of their content: there is no verdict to read instead.
        if (result.Test.ReadThisFor is { } question)
        {
            body.Children.Add(new TextBlock
            {
                Text = question,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });
        }

        // ⚠ Called a self-assessment wherever it appears, and never placed where a verdict goes:
        // the model marked its own work, in a language it may not have. See
        // ModelTestResult.SelfAssessment — the number is worth something as a gap between two
        // runs, very little on its own, and it never overrules the reader.
        if (result.SelfAssessment is { } assessment)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"self-assessment: {assessment}/10",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });
        }

        if (experimental)
        {
            body.Children.Add(Note(result.Passed
                ? $"Passed: UGT Mod's '{result.Test.UnlocksOption}' option can be turned on for this model."
                : $"Failed: keep UGT Mod's '{result.Test.UnlocksOption}' option off."));

            // Shown on success too, and in amber so it is not read as small print under a green
            // mark. Passing means the model is capable, not that the option is safe: the mod
            // ships it disabled because its failure mode is silent, and a green line saying
            // "you may switch it on" with nothing beside it would quietly recommend it.
            if (result.Test.Caveat is not null)
                body.Children.Add(Note(result.Test.Caveat, Tone.Warning));
        }

        if (result.EchoedInstructions)
        {
            body.Children.Add(Note("The model repeated the instructions. Only its last line was checked.",
                                   Tone.Warning));
        }

        // Set apart by its frame, not only by its wording: these two answer a different question
        // from the rest, and a row that looks identical to a required one gets counted with them.
        // Indented and marked down the left edge — enough to read as an aside, not so much as to
        // look like a warning.
        return new Border
        {
            Background = Brush("SurfaceBase"),
            BorderBrush = Brush(experimental ? "StatusWarning" : "BorderSubtle"),
            BorderThickness = experimental
                ? new Thickness(3, 1, 1, 1)
                : new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            Margin = experimental ? new Thickness(14, 0, 0, 0) : default,
            Child = body,
        };
    }

    // ---------------------------------------------------------------- saving

    private void Save()
    {
        _draft.TargetLanguage = Tag(_language) ?? "auto";
        _draft.TranslationBackend = Tag(_backend) ?? "none";

        // 🔴 **A hidden control holds no answer, so it must not be saved as one.** The AI card is
        // shown only for the AI backend; on any other, its model list is never filled and its
        // fields sit empty. Writing them anyway ERASED a configured model — somebody who switched
        // to community translations, closed the window and came back was offered
        // `AI model: "gemma3:4b" -> ""` as a pending change they had never made, and Apply is the
        // natural way to leave a settings screen.
        //
        // ⚠ Kept rather than cleared, deliberately: switching backend is not abandoning a setup.
        // Coming back to the AI must find the server and the model that were working.
        //
        // ⚠ Same reasoning as the guard on filling _aiUrl from a discovered server, three hundred
        // lines up. That one stopped the screen from INVENTING an answer; this one stops it from
        // destroying one.
        if (Tag(_backend) == "llm")
        {
            _draft.AiUrl = _aiUrl.Text?.Trim() ?? "";
            _draft.AiModel = Tag(_aiModel) ?? "";
            _draft.AiApiKey = string.IsNullOrWhiteSpace(_apiKey.Text) ? null : _apiKey.Text.Trim();
        }

        // "Google / DeepL" is one choice on screen and two values in the file, exactly as the mod
        // stores it.
        if (Tag(_backend) == "google") _draft.TranslationBackend = Tag(_provider) ?? "google";
        _draft.DeeplUseFree = _deeplFree.IsChecked == true;
        _draft.ModOnlineMode = _modOnline.IsChecked == true;
        _draft.AutoDownload = _autoDownload.IsChecked == true;
        _draft.NotifyUpdates = _notifyUpdates.IsChecked == true;
        _draft.CheckModUpdates = _checkModUpdates.IsChecked == true;
        _draft.MergeStrategy = Tag(_mergeStrategy) ?? "ask";
        _draft.NotificationsEnabled = _notificationsEnabled.IsChecked == true;
        _draft.NotificationPosition = Tag(_notificationPosition) ?? "top-right";
        // Picking a backend on this screen means wanting it to run. It is the DEFAULT for new
        // games, not a decision about any particular one: whether a given game starts translating
        // is settled on that game's own card, which can switch it off without losing any of this.
        //
        // Deliberately not "== llm": in the mod this flag says whether translation runs, for
        // every backend. Reading it as "the backend is an AI" is what once left every Google and
        // DeepL setup marked as switched off.
        _draft.EnableAi = _draft.TranslationBackend != "none";
        // Whatever is on screen is what gets saved. The editor can only hold something captured,
        // so it cannot be unusable — and quietly substituting a different key would be the exact
        // behaviour this whole mechanism exists to avoid.
        if (!string.IsNullOrWhiteSpace(_hotkey.Value)) _draft.SettingsHotkey = _hotkey.Value;
        _draft.Shortcuts = ShortcutsOnScreen();
        _draft.TranslateModUi = _translateModUi.IsChecked == true;
        _draft.Channel = Tag(_channel) ?? "stable";

        // Reviewed is what allows the mod's first-run wizard to be skipped later, and it is set
        // here and nowhere else: it means a human has actually looked at these values.
        _draft.Reviewed = true;

        // Written onto the LIVE settings, never in place of them.
        //
        // The draft is a copy made field by field so that Cancel can mean cancel — and it copied
        // the twenty-two this window edits, which is every field this window knows about. Saving
        // it wholesale therefore erased everything it did not know about: the account token, the
        // name behind it, the server that issued it, and the proxy — all of which moved to the
        // other window and stopped being copied here the day they moved.
        //
        // That is why signing out kept happening "for no reason": it happened on Apply, in a
        // window that has nothing to do with the account. Applying what this screen owns onto what
        // is already stored cannot lose a setting it has never heard of, which is the only version
        // of this that stays correct as fields are added.
        var stored = _store.Current;

        stored.TargetLanguage = _draft.TargetLanguage;
        stored.TranslationBackend = _draft.TranslationBackend;
        stored.AiUrl = _draft.AiUrl;
        stored.AiModel = _draft.AiModel;
        stored.AiApiKey = _draft.AiApiKey;
        stored.GoogleApiKey = _draft.GoogleApiKey;
        stored.DeeplApiKey = _draft.DeeplApiKey;
        stored.DeeplUseFree = _draft.DeeplUseFree;
        stored.EnableAi = _draft.EnableAi;
        stored.ModOnlineMode = _draft.ModOnlineMode;
        stored.AutoDownload = _draft.AutoDownload;
        stored.NotifyUpdates = _draft.NotifyUpdates;
        stored.CheckModUpdates = _draft.CheckModUpdates;
        stored.MergeStrategy = _draft.MergeStrategy;
        stored.NotificationsEnabled = _draft.NotificationsEnabled;
        stored.NotificationPosition = _draft.NotificationPosition;
        stored.SettingsHotkey = _draft.SettingsHotkey;
        stored.Shortcuts = ModShortcuts.CopyOf(_draft.Shortcuts) ?? new Dictionary<string, string>();
        stored.TranslateModUi = _draft.TranslateModUi;
        stored.Channel = _draft.Channel;
        stored.Reviewed = true;

        var count = CountPendingChanges();

        _store.Save(stored);
        Saved = true;

        _saved.Text = count == 1 ? "1 change applied" : $"{count} changes applied";
        _saved.IsVisible = true;

        // Reads the store again, which now matches what is on screen — so the button says "Close".
        RefreshApplyButton();
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

        // ⚠ Counted only while the card that holds them is on screen — see the guard in Save. An
        // empty field behind a hidden card is not an edit, and counting it announced work nobody
        // had done on a window nobody had touched.
        if (Tag(_backend) == "llm")
        {
            Compare("AI server", _aiUrl.Text, saved.AiUrl);
            Compare("AI model", Tag(_aiModel), saved.AiModel);
            Compare("API key", _apiKey.Text, saved.AiApiKey);
        }
        Compare("hotkey", _hotkey.Value, saved.SettingsHotkey);

        foreach (var shortcut in ModShortcuts.All)
        {
            saved.Shortcuts.TryGetValue(shortcut.Key, out var before);
            Compare($"shortcut \"{shortcut.Label}\"", _shortcuts[shortcut.Key].Value, before);
        }

        Compare("update channel", Tag(_channel), saved.Channel);

        if ((_modOnline.IsChecked == true) != saved.ModOnlineMode)
            changes.Add($"mod online: {(saved.ModOnlineMode ? "on" : "off")} -> {(_modOnline.IsChecked == true ? "on" : "off")}");

        Compare("merge strategy", Tag(_mergeStrategy), saved.MergeStrategy);
        Compare("notification position", Tag(_notificationPosition), saved.NotificationPosition);

        if ((_autoDownload.IsChecked == true) != saved.AutoDownload) changes.Add("auto-download");
        if ((_notifyUpdates.IsChecked == true) != saved.NotifyUpdates) changes.Add("translation update notifications");
        if ((_checkModUpdates.IsChecked == true) != saved.CheckModUpdates) changes.Add("UGT Mod update notifications");
        if ((_notificationsEnabled.IsChecked == true) != saved.NotificationsEnabled) changes.Add("in-game notifications");
        if ((_translateModUi.IsChecked == true) != saved.TranslateModUi) changes.Add("UGT Mod interface translated");

        Compare("Google key", _draft.GoogleApiKey, saved.GoogleApiKey);
        Compare("DeepL key", _draft.DeeplApiKey, saved.DeeplApiKey);

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

    /// <summary>The optional shortcuts as captured on screen — only the ones that have a key.</summary>
    private Dictionary<string, string> ShortcutsOnScreen() =>
        _shortcuts.Where(pair => pair.Value.Value.Length > 0)
                  .ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal);

    /// <summary>"Apply (3)" while there is something to save, "Close" when there is not.</summary>
    private void RefreshApplyButton()
    {
        if (_populating) return;

        var changes = PendingChanges();
        _applyButton.Content = changes.Count > 0 ? $"Apply ({changes.Count})" : "Close";

        // The moment something is pending again, what was applied a minute ago is no longer the
        // state of this screen, and leaving the note up would be a lie of omission.
        if (changes.Count > 0) _saved.IsVisible = false;

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
        // ⚠ One list, and it was two until every dropdown in the program became the same control —
        // a ComboBox raised an event carrying selection args where a SearchPicker raises a plain
        // one, so they could not share an array, and a picker missing from either list was a change
        // the Apply button never noticed.
        foreach (var picker in new[]
                 {
                     _language, _backend, _aiModel, _channel, _mergeStrategy, _notificationPosition,
                     _provider,
                 })
        {
            picker.SelectionChanged += (_, _) => RefreshApplyButton();
        }

        foreach (var field in new[] { _aiUrl, _apiKey })
            field.TextChanged += (_, _) => RefreshApplyButton();

        // Its own event rather than a hidden TextBox's: the editor raises it only when the
        // composed key actually moves, so a refused capture no longer counts as a pending change.
        _hotkey.Changed += RefreshApplyButton;
        foreach (var editor in _shortcuts.Values) editor.Changed += RefreshApplyButton;

        _modOnline.IsCheckedChanged += (_, _) => RefreshApplyButton();
        foreach (var box in new[] { _autoDownload, _notifyUpdates, _checkModUpdates, _notificationsEnabled, _translateModUi })
            box.IsCheckedChanged += (_, _) => RefreshApplyButton();
        _deeplFree.IsCheckedChanged += (_, _) => RefreshApplyButton();
        _providerKey.TextChanged += (_, _) => RefreshApplyButton();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// What the selected entry stands for, and which row carries a value.
    ///
    /// ⚠ One shape now: every dropdown in this window is a SearchPicker, so both of these are the
    /// shared pair and nothing here decides anything of its own. They were duplicated while half
    /// the lists were ComboBoxes — the same rule written twice, which is how two screens end up
    /// disagreeing about what a stored value means.
    /// </summary>
    private static string? Tag(SearchPicker box) => ModSettingControls.Tag(box);

    private static void Select(SearchPicker box, string? value) =>
        ModSettingControls.Select(box, value);

    /// <summary>
    /// A control with its word above it, for the places where several sit side by side on one row
    /// and the row's own label cannot name them all.
    /// </summary>
    private Control Labelled(string label, Control control)
    {
        var stack = new StackPanel { Spacing = 2 };

        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextMuted"),
        });

        stack.Children.Add(control);
        return stack;
    }

}
