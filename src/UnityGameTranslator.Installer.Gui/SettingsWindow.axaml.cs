using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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
    private ComboBox _channel = null!;
    private CheckBox _online = null!;

    private TextBox _apiKey = null!;
    private TextBlock _metrics = null!;
    private TextBlock _modelNote = null!;
    private Button _connectButton = null!;
    private StackPanel _aiPanel = null!;
    private StackPanel _testOutput = null!;
    private TextBlock _aiStatus = null!;
    private Button _testButton = null!;

    public bool Saved { get; private set; }

    public SettingsWindow(IPlatform platform, SettingsStore store)
    {
        _platform = platform;
        _store = store;

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
        Opened += async (_, _) => await DiscoverAsync();
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
        layout.Children.Add(ModCard());

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        var save = new Button { Content = "Save", IsDefault = true, Classes = { "primary" } };
        save.Click += (_, _) => Save();

        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        layout.Children.Add(buttons);

        return new ScrollViewer { Content = layout };
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
        refresh.Click += async (_, _) => await DiscoverAsync();

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

    private Control ModCard()
    {
        _hotkey = new TextBox { Width = 160, Text = _draft.SettingsHotkey };

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
        panel.Children.Add(Row("In-game hotkey", _hotkey));
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

    private async Task DiscoverAsync()
    {
        _aiStatus.Text = "Looking for a local AI server...";
        _aiModel.Items.Clear();
        _testButton.IsEnabled = false;

        // Fetched alongside the search, never blocking it: a note is a nicety, a server list is
        // the screen's reason to exist. Offline settings mean no note and nothing else missing.
        _modelNotes ??= await new ModelNotesProvider(_platform)
            .GetAsync(offline: !_draft.OnlineMode);

        var servers = await _probe.DiscoverAsync();

        if (servers.Count == 0)
        {
            _aiStatus.Text = "No local AI server answered on the usual ports. "
                           + "One running elsewhere still works — type its address above.";
            return;
        }

        var server = servers[0];
        if (string.IsNullOrWhiteSpace(_aiUrl.Text)) _aiUrl.Text = server.Url;

        _aiStatus.Text = $"{server.Product} answered at {server.Url} — {server.Models.Count} model(s).";

        foreach (var model in server.Models)
            _aiModel.Items.Add(new ComboBoxItem { Content = model, Tag = model });

        Select(_aiModel, _draft.AiModel);
        _aiModel.SelectedItem ??= _aiModel.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _testButton.IsEnabled = _aiModel.SelectedItem is not null;
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
        _aiModel.Items.Clear();
        _testButton.IsEnabled = false;

        var models = await _probe.ListModelsAsync(url, _apiKey.Text?.Trim());

        if (models is null)
        {
            _aiStatus.Text = "No answer from that address. Check the URL, and the key if this is an "
                           + "online provider: a rejected key looks exactly like a wrong address.";
            _connectButton.IsEnabled = true;
            return;
        }

        _aiStatus.Text = $"Connected - {models.Count} model(s) offered.";
        foreach (var name in models)
            _aiModel.Items.Add(new ComboBoxItem { Content = name, Tag = name });

        Select(_aiModel, _draft.AiModel);
        _aiModel.SelectedItem ??= _aiModel.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _testButton.IsEnabled = _aiModel.SelectedItem is not null;
        _connectButton.IsEnabled = true;
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

                _testOutput.Children.Add(TestRow(result));
            });
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
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
        _draft.EnableAi = _draft.TranslationBackend == "ai";
        _draft.SettingsHotkey = _hotkey.Text?.Trim() ?? "Ctrl+F10";
        _draft.Channel = Tag(_channel) ?? "stable";
        _draft.OnlineMode = _online.IsChecked == true;

        // Reviewed is what allows the mod's first-run wizard to be skipped later, and it is set
        // here and nowhere else: it means a human has actually looked at these values.
        _draft.Reviewed = true;

        _store.Save(_draft);
        Saved = true;
        Close();
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
