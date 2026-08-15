using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UnityGameTranslator.Manager.Core.Api;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Settings;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The mod settings of ONE game: the same questions the defaults screen asks, answered for this
/// game alone.
///
/// 🔴 **Three sources, and the reader must be able to tell them apart at a glance.** Every field
/// shows one value and NAMES where it came from — set for this game, read from this game, or from
/// Mod defaults. Without that mark the form is a lie by omission: "Japanese" would mean three
/// different things and there would be no way to know which.
///
/// 🔴 **Filling a field from the game does NOT decide anything.** A value merely displayed is not
/// an override; only an edit becomes one, and an edit that lands back on what the game already
/// holds clears it again. Freezing what was shown would turn opening a card into twenty-five
/// decisions nobody took — and every one of them would stop following the defaults for good.
///
/// ⚠ **Nothing here reaches the game.** Apply stores the answers; writing them into config.json is
/// a separate, named act — the button in the differences block, or the one-click. That is the same
/// separation the defaults window keeps, and the reason the same word "Apply" is honest in both.
///
/// ⚠ **No server sweep and no test bench.** Those set a translator up, once, for this machine;
/// they live in the defaults window and this form links to it. What it does offer is a Refresh on
/// the model list — one request to an address somebody typed, on their click, which is the only
/// thing that makes the model field usable at all.
/// </summary>
public sealed class GameModSettingsForm
{
    private readonly InstallerSettings _defaults;
    private readonly GameModOverrides _inGame;

    /// <summary>
    /// The key this game already carries, shown and never edited — see the note in the "in the
    /// game" block, and GameConfigSnapshot.InGameHotkey for why it is not a setting.
    /// </summary>
    private readonly string? _inGameHotkey;

    private readonly IPlatform _platform;

    /// <summary>
    /// The answers being edited: a COPY, so leaving the card without applying changes nothing.
    ///
    /// ⚠ GamePreferences.Read hands back the stored object itself, not a copy — every other screen
    /// changes one field and saves it in the same breath, which is what makes that safe. This one
    /// cannot: it edits twenty-five fields and offers a button. Copying is that button's condition.
    /// </summary>
    private readonly GameModOverrides _draft;

    private readonly AiServerProbe _probe = new();

    private ComboBox _language = null!;
    private ComboBox _backend = null!;
    private TextBox _aiUrl = null!;
    private TextBox _aiKey = null!;
    private ComboBox _aiModel = null!;
    private ComboBox _provider = null!;
    private TextBox _providerKey = null!;
    private CheckBox _deeplFree = null!;
    private ComboBox _channel = null!;
    private CheckBox _modOnline = null!;
    private CheckBox _checkModUpdates = null!;
    private CheckBox _notifyUpdates = null!;
    private CheckBox _autoDownload = null!;
    private ComboBox _mergeStrategy = null!;
    private CheckBox _notificationsEnabled = null!;
    private ComboBox _noticePosition = null!;

    private Control _aiCard = null!;
    private Control _apiCard = null!;
    private Button _apply = null!;
    private TextBlock _modelStatus = null!;

    /// <summary>
    /// True while the form fills itself in, so what it does to itself is not counted as an edit.
    ///
    /// Selecting the saved value in a freshly built list raises the same event a person clicking it
    /// does; counting those would greet everybody with "Apply (17)" on a form nobody had touched.
    /// The defaults window guards its own counter the same way, for the same reason.
    /// </summary>
    private bool _populating = true;

    /// <summary>Raised once the answers have been stored, so the card can redraw what depends on them.</summary>
    public event Action? Applied;

    /// <summary>Asked to open the defaults window — the place a translator is actually set up.</summary>
    public event Action? OpenDefaults;

    /// <summary>
    /// The language this game will be set to whatever the picker says, or null when the picker
    /// decides.
    ///
    /// 🔴 **Without this, the language field is a setting that silently does nothing.** A game
    /// already holding a translation keeps that translation's language — its target is not a
    /// preference, it is what the file IS, and retargeting it would leave the mod hunting for one
    /// language while a file in another sits beside it. That rule is right and it is not moving;
    /// what was wrong was letting somebody pick a language here and watch nothing happen, with no
    /// line anywhere saying why.
    /// </summary>
    private readonly string? _languagePinnedTo;

    public GameModSettingsForm(IPlatform platform, InstallerSettings defaults,
                               GameConfigSnapshot snapshot, GameModOverrides? stored,
                               string? languagePinnedTo = null)
    {
        _platform = platform;
        _defaults = defaults;
        _inGame = snapshot.Values;
        _inGameHotkey = snapshot.InGameHotkey;
        _draft = stored?.Copy() ?? new GameModOverrides();
        _languagePinnedTo = languagePinnedTo;
    }

    /// <summary>What the person has answered for this game, ready to be stored.</summary>
    public GameModOverrides Draft => _draft;

    // ---------------------------------------------------------------- the three sources

    private string? EffectiveText(Func<GameModOverrides, string?> pick, string? fallback) =>
        pick(_draft) ?? pick(_inGame) ?? fallback;

    private bool EffectiveFlag(Func<GameModOverrides, bool?> pick, bool fallback) =>
        pick(_draft) ?? pick(_inGame) ?? fallback;

    /// <summary>
    /// The mark beside a field: where its value came from, and the way back when it is ours.
    ///
    /// ⚠ The way back is offered ONLY where there is something to go back to — an override. A reset
    /// beside a value that is merely being displayed would suggest a decision had been taken.
    /// </summary>
    private Control Origin(object? own, object? inGame, Action clear)
    {
        var origin = ModSettingsResolver.OriginOf(own, inGame);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(new TextBlock
        {
            Text = ModSettingsResolver.Describe(origin),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,

            // Ours is the only one worth catching the eye: the other two say "nobody decided this
            // here", which is the quiet, ordinary state of most fields on most games.
            Foreground = Palette.Of(origin == ModValueOrigin.ThisGame ? "StatusInfo" : "TextMuted"),
        });

        if (origin == ModValueOrigin.ThisGame)
        {
            // ⚠ Named by where it lands, not by "reset". Falling back to what the game holds and
            // falling back to the defaults are two different outcomes, and somebody clicking has
            // the right to know which one they are about to get.
            var back = new Button
            {
                Content = inGame is not null
                    ? "back to this game's value"
                    : "back to Mod defaults",
                FontSize = 10,
                Padding = new Avalonia.Thickness(6, 1),
                VerticalAlignment = VerticalAlignment.Center,
            };

            // ⚠ Posted, never called straight. Rebuilding empties the panel this button lives in,
            // so calling it from inside its own Click destroys the control while its event is
            // still running — and takes the keyboard focus with it. The card above learned this
            // the same way; see MainWindow.ModSettings.
            back.Click += (_, _) =>
            {
                clear();
                Dispatcher.UIThread.Post(Rebuild);
            };
            row.Children.Add(back);
        }

        return row;
    }

    /// <summary>
    /// Stores a text answer, and clears it when it lands back on what would be there anyway.
    ///
    /// ⚠ That second half is what keeps the form honest. Typing a value, thinking better of it and
    /// typing the original back would otherwise leave an override behind — one that looks identical
    /// today and silently stops following the defaults for ever after.
    /// </summary>
    private void Answer(Action<string?> set, string? value, string? wouldBeAnyway)
    {
        if (_populating) return;

        set(string.Equals(value, wouldBeAnyway, StringComparison.Ordinal) ? null : value);
        RefreshApply();
    }

    private void Answer(Action<bool?> set, bool value, bool wouldBeAnyway)
    {
        if (_populating) return;

        set(value == wouldBeAnyway ? null : value);
        RefreshApply();
    }

    // ---------------------------------------------------------------- building

    private StackPanel _host = null!;

    /// <summary>The whole form, ready to be dropped into a card.</summary>
    public Control Build()
    {
        _host = new StackPanel { Spacing = 10 };
        Rebuild();
        return _host;
    }

    /// <summary>
    /// Redrawn whole when an origin changes, because the marks beside every field are what that
    /// change is about.
    ///
    /// 🔴 **Never call this from inside one of its own controls' events.** It empties the panel
    /// those controls live in, so the one raising the event is destroyed mid-handler and the
    /// keyboard focus goes with it — the control is left looking pressed and the next keystroke
    /// goes nowhere. Every caller posts it.
    /// </summary>
    private void Rebuild()
    {
        _populating = true;
        _host.Children.Clear();

        _host.Children.Add(new TextBlock
        {
            Text = "These are the settings this game is set up with. Each one starts from what the "
                 + "game already holds, or from Mod defaults when it holds nothing, and a change "
                 + "made here applies to this game only.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.Of("TextSecondary"),
        });

        _host.Children.Add(LanguageRow());

        if (_languagePinnedTo is not null)
        {
            _host.Children.Add(new TextBlock
            {
                Text = $"This game stays on {_languagePinnedTo}: it already holds a translation in "
                     + "that language, and pointing it elsewhere would leave the mod looking for a "
                     + "file that is not there. Take a translation in another language, or start a "
                     + "new one, and this follows.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(120, 0, 0, 0),
                Foreground = Palette.Of("StatusWarning"),
            });
        }

        _host.Children.Add(BackendRow());

        _aiCard = AiBlock();
        _apiCard = ApiBlock();
        _host.Children.Add(_aiCard);
        _host.Children.Add(_apiCard);

        _host.Children.Add(Separator());
        foreach (var control in InGameBlock()) _host.Children.Add(control);

        _host.Children.Add(Separator());
        foreach (var control in UpdatesBlock()) _host.Children.Add(control);

        _host.Children.Add(ApplyBar());

        ShowBackendBlocks();

        _populating = false;
        RefreshApply();
    }

    private Control Separator() => new Border
    {
        Height = 1,
        Background = Palette.Of("BorderSubtle"),
        Margin = new Avalonia.Thickness(0, 4),
    };

    private Control LanguageRow()
    {
        _language = ModSettingControls.LanguagePicker(_platform, 220);

        // ⚠ The game stores a language NAME, the picker works in codes. Matched by the shared table
        // rather than by string equality: "French" and "fr" are the same answer, and comparing them
        // as text would show every configured game as having no language set.
        Select(_language, EffectiveText(o => o.TargetLanguage, _defaults.TargetLanguage));

        // 🔴 Both sides through Canonical, and it is not a nicety. The game stores a NAME
        // ("French"), this picker hands back a code ("fr") — so comparing them as text answers
        // "different" for a language against itself. Picking the language the game already has
        // would then store an override that changes nothing today and quietly stops following the
        // defaults for ever after, which is the one thing this form promises not to do.
        _language.SelectionChanged += (_, _) => Answer(
            v => _draft.TargetLanguage = v,
            Code(ModSettingControls.Tag(_language)),
            Code(_inGame.TargetLanguage ?? _defaults.TargetLanguage));

        return Row("Language", _language,
                   Origin(_draft.TargetLanguage, _inGame.TargetLanguage,
                          () => _draft.TargetLanguage = null));
    }

    private Control BackendRow()
    {
        _backend = ModSettingControls.BackendPicker(220);

        var effective = EffectiveText(o => o.TranslationBackend, _defaults.TranslationBackend);
        ModSettingControls.Select(_backend, effective == "deepl" ? "google" : effective);

        _backend.SelectionChanged += (_, _) =>
        {
            ShowBackendBlocks();
            StoreBackend();
        };

        return Row("Backend", _backend,
                   Origin(_draft.TranslationBackend, _inGame.TranslationBackend,
                          () => _draft.TranslationBackend = null));
    }

    /// <summary>
    /// "Google / DeepL" is one row on screen and two values in the file, exactly as the mod stores
    /// it — so the backend cannot be settled without looking at the provider beside it.
    /// </summary>
    private void StoreBackend()
    {
        var chosen = ModSettingControls.Tag(_backend);

        if (chosen == "google") chosen = ModSettingControls.Tag(_provider) ?? "google";

        Answer(v => _draft.TranslationBackend = v, chosen,
               _inGame.TranslationBackend ?? _defaults.TranslationBackend);
    }

    private void ShowBackendBlocks()
    {
        var backend = ModSettingControls.Tag(_backend);
        _aiCard.IsVisible = backend == "llm";
        _apiCard.IsVisible = backend == "google";
    }

    private Control AiBlock()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(0, 4, 0, 0) };

        _aiUrl = new TextBox
        {
            Width = 220,
            Text = EffectiveText(o => o.AiUrl, _defaults.AiUrl) ?? "",
            Watermark = Endpoints.OllamaDefault,
            FontSize = 12,
        };

        _aiUrl.TextChanged += (_, _) => Answer(
            v => _draft.AiUrl = string.IsNullOrWhiteSpace(v) ? null : v,
            _aiUrl.Text?.Trim(), _inGame.AiUrl ?? _defaults.AiUrl);

        panel.Children.Add(Row("AI server", _aiUrl,
                               Origin(_draft.AiUrl, _inGame.AiUrl, () => _draft.AiUrl = null)));

        // ⚠ What is said depends on WHERE the address points, and it is settled in the shared
        // library because it is a statement about somebody's money and somebody's data — the mod
        // and the defaults screen have to make it identically.
        //
        // ⚠ An empty field says nothing either: somebody who has not typed an address has not made
        // a decision to be cautioned about, and a bill notice would answer a question nobody asked.
        var typed = _aiUrl.Text?.Trim();

        if (!string.IsNullOrWhiteSpace(typed) && Endpoints.CautionFor(typed) is { } caution)
        {
            panel.Children.Add(new TextBlock
            {
                Text = caution,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Palette.Of("StatusWarning"),
            });
        }

        _aiKey = new TextBox
        {
            Width = 220,
            PasswordChar = '*',
            Text = EffectiveText(o => o.AiApiKey, _defaults.AiApiKey) ?? "",
            Watermark = "leave empty for a server on your machine",
            FontSize = 12,
        };

        _aiKey.TextChanged += (_, _) => Answer(
            v => _draft.AiApiKey = string.IsNullOrWhiteSpace(v) ? null : v,
            _aiKey.Text?.Trim(), _inGame.AiApiKey ?? _defaults.AiApiKey);

        panel.Children.Add(Row("API key", _aiKey,
                               Origin(_draft.AiApiKey, _inGame.AiApiKey, () => _draft.AiApiKey = null)));

        // ⚠ Never empty. The list is filled with whatever this game is set to before anything is
        // fetched, so the field says what it is rather than nothing while a request is in flight —
        // or for ever, on a machine that is offline.
        _aiModel = new ComboBox { Width = 220, FontSize = 12 };

        var model = EffectiveText(o => o.AiModel, _defaults.AiModel);
        if (!string.IsNullOrWhiteSpace(model))
        {
            _aiModel.Items.Add(new ComboBoxItem { Content = model, Tag = model });
            _aiModel.SelectedIndex = 0;
        }

        _aiModel.SelectionChanged += (_, _) => Answer(
            v => _draft.AiModel = v, ModSettingControls.Tag(_aiModel),
            _inGame.AiModel ?? _defaults.AiModel);

        var refresh = new Button { Content = "Refresh", FontSize = 11 };
        refresh.Click += async (_, _) => await ListModelsAsync();

        panel.Children.Add(Row("Model", _aiModel, refresh,
                               Origin(_draft.AiModel, _inGame.AiModel, () => _draft.AiModel = null)));

        _modelStatus = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Foreground = Palette.Of("TextMuted"),
        };

        panel.Children.Add(_modelStatus);
        panel.Children.Add(DefaultsLink(
            "Looking for a server on this machine, and putting a model through the mod's own "
            + "tests, is done once in Mod defaults."));

        return panel;
    }

    /// <summary>
    /// Asks the address in the field what it holds. One request, on a click, to a server somebody
    /// named — not the six-port sweep, which belongs to the screen where a machine gets set up.
    /// </summary>
    private async Task ListModelsAsync()
    {
        var url = _aiUrl.Text?.Trim();

        if (string.IsNullOrWhiteSpace(url))
        {
            Say("Type a server address first.", "StatusWarning");
            return;
        }

        Say($"Asking {url}...", "TextMuted");

        var models = await _probe.ListModelsAsync(url, _aiKey.Text?.Trim());

        if (models is null)
        {
            // Not dressed up as a failure: a laptop away from its server, or a server not started
            // yet, is an ordinary situation and nothing here is broken.
            Say($"{url} did not answer just now — it may simply not be running. Nothing was changed.",
                "StatusWarning");
            return;
        }

        var chosen = ModSettingControls.Tag(_aiModel);

        _populating = true;
        _aiModel.Items.Clear();
        foreach (var name in models) _aiModel.Items.Add(new ComboBoxItem { Content = name, Tag = name });

        // ⚠ The saved value is never quietly replaced by another model. Left unselected, the choice
        // is visibly theirs to make — swapping one in would leave somebody believing they are
        // running the model they picked.
        ModSettingControls.Select(_aiModel, chosen);
        _populating = false;

        Say($"{url} answered — {models.Count} model(s).", "StatusSuccess");
    }

    private void Say(string text, string colour)
    {
        _modelStatus.Text = text;
        _modelStatus.Foreground = Palette.Of(colour);
        _modelStatus.IsVisible = true;
    }

    private Control ApiBlock()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(0, 4, 0, 0) };

        _provider = ModSettingControls.ProviderPicker(220);

        var backend = EffectiveText(o => o.TranslationBackend, _defaults.TranslationBackend);
        ModSettingControls.Select(_provider, backend == "deepl" ? "deepl" : "google");

        _providerKey = new TextBox { Width = 220, PasswordChar = '*', FontSize = 12 };
        _deeplFree = new CheckBox { Content = "Free tier (api-free.deepl.com)", FontSize = 12 };

        void ShowProvider()
        {
            var isDeepl = ModSettingControls.Tag(_provider) == "deepl";

            // Each provider keeps its own key. Sharing one field would overwrite the key you were
            // using the moment you looked at the other one.
            _providerKey.Text = (isDeepl
                ? EffectiveText(o => o.DeeplApiKey, _defaults.DeeplApiKey)
                : EffectiveText(o => o.GoogleApiKey, _defaults.GoogleApiKey)) ?? "";

            _deeplFree.IsVisible = isDeepl;
            _deeplFree.IsChecked = EffectiveFlag(o => o.DeeplUseFree, _defaults.DeeplUseFree);
        }

        _provider.SelectionChanged += (_, _) => { ShowProvider(); StoreBackend(); };

        _providerKey.TextChanged += (_, _) =>
        {
            var typed = string.IsNullOrWhiteSpace(_providerKey.Text) ? null : _providerKey.Text.Trim();

            if (ModSettingControls.Tag(_provider) == "deepl")
                Answer(v => _draft.DeeplApiKey = v, typed, _inGame.DeeplApiKey ?? _defaults.DeeplApiKey);
            else
                Answer(v => _draft.GoogleApiKey = v, typed, _inGame.GoogleApiKey ?? _defaults.GoogleApiKey);
        };

        _deeplFree.IsCheckedChanged += (_, _) => Answer(
            v => _draft.DeeplUseFree = v, _deeplFree.IsChecked == true,
            _inGame.DeeplUseFree ?? _defaults.DeeplUseFree);

        ShowProvider();

        panel.Children.Add(Row("Provider", _provider));
        panel.Children.Add(Row("API key", _providerKey));
        panel.Children.Add(_deeplFree);
        panel.Children.Add(new TextBlock
        {
            Text = "Both bill you on your own account. The key is stored encrypted and tied to "
                 + "this machine.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.Of("TextMuted"),
        });

        return panel;
    }

    private IEnumerable<Control> InGameBlock()
    {
        // 🔴 **No hotkey field here, and this gap is the design.** Every other row on this form is a
        // value this tool may write; the hotkey is not. The key a game carries was captured INSIDE
        // it, against the real keyboard — the only measurement of it that exists — so the question
        // is never "what key does this game use" but "do I replace the one it measured", and it can
        // only be answered with both keys in front of you. That is the line in the differences
        // block just above, with its own box. See analyse/hotkey-keycode-divergence.md.
        //
        // ⚠ Said rather than simply left out. A form that mirrors the defaults screen and silently
        // drops one of its rows reads as an oversight, and somebody would put it back.
        yield return new TextBlock
        {
            Text = "The in-game hotkey is not set here. Mod defaults uses "
                 + $"{_defaults.SettingsHotkey}; "
                 + (_inGameHotkey is null
                    ? "this game has none yet, so that key is written when anything is installed."
                    : $"this game uses {_inGameHotkey}, captured against the real keyboard inside "
                      + "it. Whether Mod defaults replaces it is asked in the list of differences, "
                      + "above this block."),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.Of("TextMuted"),
        };

        _modOnline = new CheckBox
        {
            Content = "Let the mod go online while you play",
            IsChecked = EffectiveFlag(o => o.ModOnlineMode, _defaults.ModOnlineMode),
            FontSize = 12,
        };

        _modOnline.IsCheckedChanged += (_, _) => Answer(
            v => _draft.ModOnlineMode = v, _modOnline.IsChecked == true,
            _inGame.ModOnlineMode ?? _defaults.ModOnlineMode);

        yield return WithOrigin(_modOnline, _draft.ModOnlineMode, _inGame.ModOnlineMode,
                                () => _draft.ModOnlineMode = null);
    }

    private IEnumerable<Control> UpdatesBlock()
    {
        _channel = ModSettingControls.ChannelPicker(200);
        ModSettingControls.Select(_channel, EffectiveText(o => o.Channel, _defaults.Channel));

        _channel.SelectionChanged += (_, _) => Answer(
            v => _draft.Channel = v, ModSettingControls.Tag(_channel),
            _inGame.Channel ?? _defaults.Channel);

        yield return Row("Plugin builds", _channel,
                         Origin(_draft.Channel, _inGame.Channel, () => _draft.Channel = null));

        _checkModUpdates = Toggle("Tell me when a new version of the mod is out",
            o => o.CheckModUpdates, _defaults.CheckModUpdates, v => _draft.CheckModUpdates = v);

        yield return WithOrigin(_checkModUpdates, _draft.CheckModUpdates, _inGame.CheckModUpdates,
                                () => _draft.CheckModUpdates = null);

        _notifyUpdates = Toggle("Tell me when a translation I use is updated",
            o => o.NotifyUpdates, _defaults.NotifyUpdates, v => _draft.NotifyUpdates = v);

        yield return WithOrigin(_notifyUpdates, _draft.NotifyUpdates, _inGame.NotifyUpdates,
                                () => _draft.NotifyUpdates = null);

        _autoDownload = Toggle("Download translation updates without asking",
            o => o.AutoDownload, _defaults.AutoDownload, v => _draft.AutoDownload = v);
        _autoDownload.Margin = new Avalonia.Thickness(20, 0, 0, 0);

        yield return WithOrigin(_autoDownload, _draft.AutoDownload, _inGame.AutoDownload,
                                () => _draft.AutoDownload = null);

        _mergeStrategy = ModSettingControls.MergeStrategyPicker(200);
        ModSettingControls.Select(_mergeStrategy, EffectiveText(o => o.MergeStrategy, _defaults.MergeStrategy));

        _mergeStrategy.SelectionChanged += (_, _) => Answer(
            v => _draft.MergeStrategy = v, ModSettingControls.Tag(_mergeStrategy),
            _inGame.MergeStrategy ?? _defaults.MergeStrategy);

        yield return Row("When both changed", _mergeStrategy,
                         Origin(_draft.MergeStrategy, _inGame.MergeStrategy,
                                () => _draft.MergeStrategy = null));

        _notificationsEnabled = Toggle("Show notices while playing",
            o => o.NotificationsEnabled, _defaults.NotificationsEnabled,
            v => _draft.NotificationsEnabled = v);

        yield return WithOrigin(_notificationsEnabled, _draft.NotificationsEnabled,
                                _inGame.NotificationsEnabled, () => _draft.NotificationsEnabled = null);

        _noticePosition = ModSettingControls.NoticePositionPicker(200);
        ModSettingControls.Select(_noticePosition,
            EffectiveText(o => o.NotificationPosition, _defaults.NotificationPosition));

        _noticePosition.SelectionChanged += (_, _) => Answer(
            v => _draft.NotificationPosition = v, ModSettingControls.Tag(_noticePosition),
            _inGame.NotificationPosition ?? _defaults.NotificationPosition);

        yield return Row("Notice position", _noticePosition,
                         Origin(_draft.NotificationPosition, _inGame.NotificationPosition,
                                () => _draft.NotificationPosition = null));
    }

    private CheckBox Toggle(string label, Func<GameModOverrides, bool?> pick, bool fallback,
                            Action<bool?> set)
    {
        var box = new CheckBox
        {
            Content = label,
            IsChecked = EffectiveFlag(pick, fallback),
            FontSize = 12,
        };

        box.IsCheckedChanged += (_, _) => Answer(set, box.IsChecked == true, pick(_inGame) ?? fallback);
        return box;
    }

    // ---------------------------------------------------------------- applying

    private Control ApplyBar()
    {
        _apply = new Button { Content = "Apply", Classes = { "primary" }, FontSize = 12 };

        // ⚠ Posted, and it does NOT rebuild afterwards. Whoever listens stores the answers and
        // redraws the whole block from them — with a fresh form, so every origin is recomputed from
        // what is now on disk rather than from what this instance remembers. Rebuilding here as
        // well would redraw a form that is already being thrown away, from inside the click of a
        // button that redraw destroys.
        _apply.Click += (_, _) => Dispatcher.UIThread.Post(() => Applied?.Invoke());

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
            Children = { _apply },
        };
    }

    /// <summary>
    /// "Apply (3)" while something is pending, greyed and plain otherwise.
    ///
    /// ⚠ Never hidden and never renamed to "Close": this is a block inside a card, not a window, so
    /// there is nothing to close — and a button that vanishes sends somebody looking for it. The
    /// reason it is unavailable is one hover away, which is the rule this program holds everywhere:
    /// no greyed control without words.
    /// </summary>
    private void RefreshApply()
    {
        if (_populating || _apply is null) return;

        var count = _draft.Count;

        _apply.Content = count > 0 ? $"Apply ({count})" : "Apply";
        _apply.IsEnabled = count > 0;

        ToolTip.SetTip(_apply, count > 0
            ? $"{count} setting(s) set for this game. Applying stores them; writing them into the "
              + "game is a separate act — \"Apply this game's own settings\", or the one-click."
            : "Nothing to apply — no setting has been changed in this form.");
    }

    // ---------------------------------------------------------------- layout

    private Control DefaultsLink(string text)
    {
        var panel = new StackPanel { Spacing = 2 };

        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.Of("TextMuted"),
        });

        var open = new Button
        {
            Content = "Open Mod defaults",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        open.Click += (_, _) => OpenDefaults?.Invoke();
        panel.Children.Add(open);

        return panel;
    }

    /// <summary>A checkbox and its origin on one line — the shape a labelled row has, without a label.</summary>
    private Control WithOrigin(Control box, object? own, object? inGame, Action clear)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(box);
        row.Children.Add(Origin(own, inGame, clear));
        return row;
    }

    private Control Row(string label, params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 120,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Palette.Of("TextMuted"),
        });

        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    /// <summary>
    /// Selects a language whether it is spelled as a code or as a name.
    ///
    /// ⚠ The game stores a NAME ("French"), this picker works in codes ("fr") — the mod's own
    /// contract, and the reason GameConfigWriter converts on the way out. Comparing the two as
    /// plain text would leave every configured game looking as though it had no language set, and
    /// the form would then offer to "set" one it already has.
    /// </summary>
    private static void Select(ComboBox box, string? value) =>
        ModSettingControls.Select(box, Code(value));

    /// <summary>
    /// One language written one way, whichever way it arrived: a name from a game's config.json, a
    /// code from the defaults or from this picker.
    ///
    /// ⚠ "auto" is not a language and must survive as itself — canonicalising it would turn "follow
    /// the system" into whatever "auto" happens not to match, and the picker would land on the
    /// first row of the list instead.
    /// </summary>
    private static string? Code(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return value.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : Languages.Canonical(value) ?? value;
    }
}
