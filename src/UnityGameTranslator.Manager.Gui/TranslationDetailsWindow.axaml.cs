using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Api;
using UnityGameTranslator.Manager.Core.Platform;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>What was said about a published translation, next to the file itself.</summary>
/// <param name="Saved">False when the window was closed without agreeing.</param>
/// <param name="Notes">The description, empty to clear it. Never null once saved.</param>
/// <param name="ResourcesUrl">The link, empty to clear it. Never null once saved.</param>
/// <param name="Finished">The author's declaration. Meaningless on a branch — see the window.</param>
/// <param name="SourceLanguage">
/// The language declared as the one the game is written in — only when the window asked for it,
/// which is a first publication. Null everywhere else: the pair is the lineage's and was shown,
/// not asked.
/// </param>
/// <param name="GameName">The game confirmed for a first publication — the site's name for it. Null when not asked.</param>
/// <param name="GameSteamId">Its Steam id as the site knows it, or the detected one when the site has none.</param>
public readonly record struct TranslationDetails(bool Saved, string Notes, string ResourcesUrl,
                                                 bool Finished, bool AcceptsContributions,
                                                 string? SourceLanguage = null,
                                                 string? GameName = null, string? GameSteamId = null);

/// <summary>
/// The game a first publication has to be filed under, and how to ask the site about it.
///
/// 🔴 **Asked BEFORE the upload, as the mod has always done.** The server will create a game
/// around whatever name arrives; a name read from a repack's folder or a product called "Game"
/// then becomes a translation nobody else ever finds. So the site is asked — by Steam id when
/// there is one, by name otherwise — and the person picks from what it answers.
/// </summary>
/// <param name="DetectedName">What this machine read: the product name, or the folder's.</param>
/// <param name="DetectedSteamId">The Steam id read on this machine, when there is one.</param>
/// <param name="Search">Asks the site: a name, a Steam id, or both. Null when it could not be asked.</param>
/// <param name="WhyNot">Why the last search could not be asked, for the sentence under the field.</param>
public sealed record GameToConfirm(string? DetectedName, string? DetectedSteamId,
                                   Func<string?, string?, Task<IReadOnlyList<CatalogApiClient.GameCandidate>?>> Search,
                                   Func<string?> WhyNot);

/// <summary>
/// The things said ABOUT a translation rather than in it: what it is, where to find the fonts or
/// images it needs, and whether its author calls it finished.
///
/// 🔴 **Reachable with nothing to publish, and that is the point.** These are exactly the edits
/// that come after the work — a clearer description, a link that moved, a translation its author
/// now considers done. Tying them to an upload meant they could only be made by having something
/// else to send.
///
/// 🔴 **And the same window IS the publication (2026-09-05).** Publishing asked two boxes and
/// nothing else, so a first publication left the site with no description and no link, and the
/// button that could add them afterwards said "the same description and link are asked for as
/// part of it" — which was false. The mod asks all of it in one act; this window now does too, and
/// what differs between the two acts is only the head of it: a publication says which two
/// languages it travels under, and on a first one asks the source (see
/// <see cref="PublishLanguages"/>).
///
/// ⚠ **A contribution does not declare itself finished.** A branch inherits its Main's status, the
/// server enforces it, and the other two products say so in these words. What is shown here is the
/// sentence, not a switch that would be discarded on arrival — the ecosystem rule: the same fact
/// reads the same way in all three.
///
/// ⚠ **Everything else on a branch IS editable**, and deliberately so. Proposing a better
/// description, or the link to the font pack the contribution needs, is contributing.
/// </summary>
public sealed class TranslationDetailsWindow : Window
{
    /// <summary>Matches the endpoint's own limits, so a refusal is never the first feedback.</summary>
    private const int NotesLimit = 1000;
    private const int UrlLimit = 2048;

    private readonly TextBox _notes;
    private readonly TextBox _url;
    private readonly CheckBox? _finished;

    /// <summary>The Main's decision on contributions. Null on a branch, which may not take it.</summary>
    private readonly CheckBox? _contributions;
    private readonly TextBlock _complaint;
    private readonly Button _save;

    /// <summary>The source picker of a first publication. Null when the pair is fixed.</summary>
    private readonly SearchPicker? _source;

    /// <summary>The target the picker is judged against. Null when no language is in play.</summary>
    private readonly string? _target;

    // The game block of a first publication. All null when the game is not in question.
    private readonly GameToConfirm? _game;
    private readonly TextBlock? _gameName;
    private readonly TextBlock? _gameState;
    private readonly TextBox? _gameSearch;
    private readonly Button? _gameSearchButton;
    private readonly TextBlock? _gameSearchStatus;
    private readonly ListBox? _gameResults;

    /// <summary>The game confirmed so far: the site's name and id, or the detected ones.</summary>
    private (string Name, string? SteamId)? _confirmedGame;

    private bool _saved;

    private TranslationDetailsWindow(string heading, string? body, PublishLanguages.Ask? languages,
                                     IPlatform? platform, string notes, string url,
                                     bool finished, bool onABranch, bool acceptsContributions,
                                     string confirm, GameToConfirm? game = null)
    {
        _game = game;
        Title = languages is null ? "Translation details" : "Publish translation";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = this.FindResource("SurfaceBase") as IBrush;

        var layout = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        layout.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.FindResource("TextPrimary") as IBrush,
        });

        // What this act does, in the server's own reading — new, replaces mine, contributes to
        // theirs. Said before anything is filled in, because it changes what the fields mean.
        if (!string.IsNullOrWhiteSpace(body))
        {
            layout.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = this.FindResource("TextSecondary") as IBrush,
            });
        }

        // ── The game, first: everything below is filed under it ──────────────
        //
        // ⚠ The same three parts as the mod's setup screen, in the same order: the game as read
        // here and whether it is confirmed, a search field, the site's answers marked ★ and ☆.
        if (game is not null)
        {
            layout.Children.Add(Label("Game"));

            var gameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            _gameName = new TextBlock
            {
                Text = game.DetectedName ?? "No game detected",
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = this.FindResource("TextPrimary") as IBrush,
            };
            _gameState = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Foreground = this.FindResource("StatusWarning") as IBrush,
            };
            Grid.SetColumn(_gameName, 0);
            Grid.SetColumn(_gameState, 1);
            gameRow.Children.Add(_gameName);
            gameRow.Children.Add(_gameState);
            layout.Children.Add(gameRow);

            var searchRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8,
            };
            _gameSearch = new TextBox { Watermark = "Search for a game…" };
            _gameSearchButton = new Button { Content = "Search" };
            Grid.SetColumn(_gameSearch, 0);
            Grid.SetColumn(_gameSearchButton, 1);
            searchRow.Children.Add(_gameSearch);
            searchRow.Children.Add(_gameSearchButton);
            layout.Children.Add(searchRow);

            _gameSearchStatus = Hint("");
            layout.Children.Add(_gameSearchStatus);

            _gameResults = new ListBox { MaxHeight = 160 };
            layout.Children.Add(_gameResults);

            layout.Children.Add(Hint(GameCandidates.Legend));

            _gameSearchButton.Click += async (_, _) => await SearchGamesAsync(_gameSearch.Text, null);
            _gameSearch.KeyDown += async (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter) await SearchGamesAsync(_gameSearch.Text, null);
            };

            // 🔴 **Highlighting a row IS choosing it here**, unlike the language picker: the list
            // is short, the rows are answers, and the mod's screen confirms on a single click too.
            _gameResults.SelectionChanged += (_, _) =>
            {
                if (_gameResults.SelectedItem is CandidateRow row)
                {
                    _confirmedGame = (row.Candidate.Name ?? game.DetectedName ?? "", row.Candidate.SteamId ?? game.DetectedSteamId);
                    ShowGame(confirmed: true);
                    Acceptable();
                }
            };

            ShowGame(confirmed: false);
        }

        if (languages is { } ask)
        {
            _target = ask.Target;

            // ⚠ Source above target, the order every pair in this product reads in ("English →
            // French") and the order the mod's own setup panel asks in.
            layout.Children.Add(Label("From (the language this game is written in)"));

            if (ask.SourceIsAsked)
            {
                // The same picker as every other language list here, minus "follow the system":
                // a publication travels under a language, never under a mode.
                _source = ModSettingControls.LanguagePicker(platform!, 300, followSystem: false);

                // Prefilled only when something already names it. ⚠ Select falls back on the
                // first row when it finds nothing, and a first row is not an answer somebody gave
                // — so it is not called at all when there is nothing to find.
                if (Languages.CodeOf(ask.Source) is { } code)
                    ModSettingControls.Select(_source, code);

                _source.SelectionChanged += (_, _) => Acceptable();
                layout.Children.Add(_source);

                // Why it is asked here and nowhere else: one sentence, the fact only.
                layout.Children.Add(Hint("Asked once, at the first publication. The mod detects it "
                                         + "line by line until then."));
            }
            else
            {
                layout.Children.Add(Fixed(ask.Source));
            }

            layout.Children.Add(Label("Into"));
            layout.Children.Add(Fixed(ask.Target));

            // ⚠ The reason it cannot be changed, the mod's own words (OptionsPanel, languages
            // locked). A fixed row with nothing said reads as a control that is broken.
            layout.Children.Add(Hint(ask.SourceIsAsked
                ? "Settled: this file already holds lines in this language."
                : "Settled: this translation is published under these languages."));
        }

        layout.Children.Add(Label("Description"));
        _notes = new TextBox
        {
            Text = notes,
            MaxLength = NotesLimit,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            Watermark = "What this translation covers, what it does not, who it is for.",
        };
        layout.Children.Add(_notes);

        layout.Children.Add(Label("Link to fonts or images"));
        _url = new TextBox
        {
            Text = url,
            MaxLength = UrlLimit,
            Watermark = "https://…",
        };
        layout.Children.Add(_url);

        layout.Children.Add(Hint("Optional. Some translations need a font or replacement images "
                                 + "that cannot travel inside the file; this is where players are "
                                 + "told to find them."));

        if (onABranch)
        {
            // ⚠ The mod's words, to the letter. Two products explaining one server rule differently
            // is how somebody concludes they behave differently.
            layout.Children.Add(Hint("Whether this is finished is the Main's to say — your "
                                     + "contribution inherits it."));
        }
        else
        {
            _finished = new CheckBox
            {
                Content = "This translation is finished",
                IsChecked = finished,
                Foreground = this.FindResource("TextPrimary") as IBrush,
            };
            layout.Children.Add(_finished);

            // The Main's other declaration, beside the first because they are the same kind of
            // thing: only a Main takes them, and both describe the lineage rather than the file.
            _contributions = new CheckBox
            {
                Content = "Let others contribute to this translation",
                IsChecked = acceptsContributions,
                Foreground = this.FindResource("TextPrimary") as IBrush,
            };
            layout.Children.Add(_contributions);

            // ⚠ What a contribution IS, in one line. The word means nothing to somebody who has
            // published once, and a box whose subject is unknown gets left alone — which is the
            // safe answer here, but arrived at for the wrong reason.
            layout.Children.Add(Hint("A contribution is a copy of your work with someone else's "
                                     + "changes, sent to you to accept or not. Left off, others "
                                     + "can still publish their own version."));
        }

        // Said above the buttons rather than after a refusal: a URL the site will reject, or a
        // source still to pick, is worth knowing about while the field is still in front of you.
        _complaint = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Foreground = this.FindResource("StatusWarning") as IBrush,
        };
        layout.Children.Add(_complaint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);

        _save = new Button { Content = confirm, Classes = { "primary" } };
        _save.Click += (_, _) =>
        {
            if (!Acceptable()) return;
            _saved = true;
            Close();
        };
        buttons.Children.Add(_save);

        layout.Children.Add(buttons);

        // Re-judged as it is typed, so the refusal disappears the moment it stops being true.
        _url.TextChanged += (_, _) => Acceptable();

        Content = layout;

        // ⚠ Judged once on opening too: a first publication with no source suggested opens with
        // the button off and the reason written, rather than with a button that refuses on click.
        Acceptable();

        // Then the site is asked about the game, once the window is on screen: by Steam id when
        // there is one, by the detected name otherwise. Posted so the window opens first — a
        // search that fails must not delay it, and its answer redraws the block when it comes.
        if (game is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(async () => await ConfirmDetectedGameAsync(game));
    }

    /// <summary>
    /// What the mod's setup screen does on opening: a Steam id is looked up on the site and its
    /// answer taken as the game, else the detected name is searched and the person picks.
    ///
    /// ⚠ A Steam id the site does not know is not a refusal: the game is taken as detected and the
    /// server creates it on upload, exactly as the mod does. What is never done is taking a NAME
    /// as confirmed without the person having seen the site's answers.
    /// </summary>
    private async Task ConfirmDetectedGameAsync(GameToConfirm game)
    {
        if (!string.IsNullOrWhiteSpace(game.DetectedSteamId))
        {
            var found = await SearchGamesAsync(null, game.DetectedSteamId);

            // The site's own name and id when it knows the game; what this machine read when it
            // does not, or could not be asked — the mod falls back the same way on a network
            // error, and the sentence under the field says which of the two happened.
            _confirmedGame = found is { Count: > 0 }
                ? (found[0].Name ?? game.DetectedName ?? "", found[0].SteamId ?? game.DetectedSteamId)
                : (game.DetectedName ?? "", game.DetectedSteamId);

            ShowGame(confirmed: true);
            Acceptable();
            return;
        }

        if (!string.IsNullOrWhiteSpace(game.DetectedName) && _gameSearch is not null)
        {
            _gameSearch.Text = game.DetectedName;
            await SearchGamesAsync(game.DetectedName, null);
        }
    }

    /// <summary>Asks the site and fills the list, likeliest first. Returns what it answered.</summary>
    private async Task<IReadOnlyList<CatalogApiClient.GameCandidate>?> SearchGamesAsync(string? query, string? steamId)
    {
        if (_game is null || _gameResults is null || _gameSearchStatus is null || _gameSearchButton is null)
            return null;

        query = query?.Trim();
        if (steamId is null && (query is null || query.Length < 2))
        {
            _gameSearchStatus.Text = "Enter at least 2 characters";
            return null;
        }

        _gameSearchButton.IsEnabled = false;
        _gameSearchStatus.Text = "Searching…";
        _gameResults.ItemsSource = null;

        var found = await _game.Search(query, steamId);

        _gameSearchButton.IsEnabled = true;

        if (found is null)
        {
            // The reason, then the consequence — and the consequence is only true on a Steam id
            // lookup (see ConfirmDetectedGameAsync); a name search that fails leaves the person
            // to try again, so it says nothing it cannot keep.
            var why = _game.WhyNot() ?? "The site could not be reached.";
            _gameSearchStatus.Text = steamId is not null
                ? why + " The game is taken as detected."
                : why;
            return null;
        }

        // Likeliest first — the socle's score, the same order the mod shows.
        var rows = found
            .Select(candidate => new CandidateRow(candidate,
                GameCandidates.Confidence(candidate.SteamId, candidate.Name, candidate.Source,
                                          _game.DetectedSteamId, _game.DetectedName)))
            .OrderByDescending(row => row.Confidence)
            .ToList();

        _gameResults.ItemsSource = rows;
        _gameSearchStatus.Text = rows.Count == 0
            ? "No games found"
            : rows.Count == 1 ? "Found 1 game" : $"Found {rows.Count} games";

        return found;
    }

    /// <summary>The game line: its name, and whether it is confirmed or still to confirm.</summary>
    private void ShowGame(bool confirmed)
    {
        if (_gameName is null || _gameState is null || _game is null) return;

        if (confirmed && _confirmedGame is { } picked)
        {
            _gameName.Text = picked.Name;
            _gameName.Foreground = this.FindResource("StatusSuccess") as IBrush;
            _gameState.Text = "✓ confirmed";
            _gameState.Foreground = this.FindResource("StatusSuccess") as IBrush;
            return;
        }

        var detected = !string.IsNullOrWhiteSpace(_game.DetectedName);
        _gameName.Text = detected ? _game.DetectedName : "No game detected";
        _gameName.Foreground = this.FindResource("StatusWarning") as IBrush;
        _gameState.Text = detected ? "⚠ confirm below" : "- please search";
        _gameState.Foreground = this.FindResource(detected ? "StatusWarning" : "TextMuted") as IBrush;
    }

    /// <summary>One answer from the site, as the list shows it.</summary>
    private sealed record CandidateRow(CatalogApiClient.GameCandidate Candidate, int Confidence)
    {
        public override string ToString() =>
            GameCandidates.Row(Candidate.Name, Candidate.Source, Confidence);
    }

    /// <summary>
    /// Whether what is in the fields can be sent, saying why when it cannot.
    ///
    /// ⚠ Two things can be wrong here, and each in one way the server would refuse outright: a
    /// link that is not an http(s) address, and — on a first publication — a source not chosen or
    /// equal to the target. Anything stricter would be this window inventing a rule the site does
    /// not have.
    /// </summary>
    private bool Acceptable()
    {
        string? complaint = null;

        // The game first, as the mod's screen judges it: nothing below it can be sent unfiled.
        if (_game is not null && _confirmedGame is null)
            complaint = "Please select a game";

        if (complaint is null && _source is not null && _target is not null)
            complaint = PublishLanguages.Complaint(SourceName(), _target);

        var url = _url.Text?.Trim() ?? "";

        var urlOk = url.Length == 0
                    || (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps));

        if (complaint is null && !urlOk)
            complaint = "The link has to be a full web address, starting with https://";

        _complaint.Text = complaint ?? "";
        _complaint.IsVisible = complaint is not null;
        _save.IsEnabled = complaint is null;
        return complaint is null;
    }

    /// <summary>The picked source as the NAME the site stores, or null when nothing is picked.</summary>
    private string? SourceName() =>
        _source is null ? null : Languages.NameOf(ModSettingControls.Tag(_source));

    /// <summary>A language that is not a question: its flag and its name, and nothing to click.</summary>
    private static Control Fixed(string? language) =>
        LanguageMark.Named(language, language ?? NotSet);

    /// <summary>
    /// Never expected on screen: <see cref="PublishLanguages.Decide"/> refuses before this window
    /// opens when a fixed language is missing. Written out rather than left blank all the same.
    /// </summary>
    private const string NotSet = "(not set)";

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        Foreground = this.FindResource("TextSecondary") as IBrush,
    };

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = this.FindResource("TextMuted") as IBrush,
    };

    /// <summary>
    /// Opens the window on what the server currently holds, and returns what the author decided.
    ///
    /// ⚠ The values passed in must come from the SERVER's answer, not from anything remembered
    /// here: this window's whole output is sent back as the new truth, so opening it on a stale
    /// description would quietly restore it.
    /// </summary>
    public static async Task<TranslationDetails> EditAsync(
        Window owner, string heading, string? notes, string? resourcesUrl,
        bool finished, bool onABranch, bool acceptsContributions = false)
    {
        var window = new TranslationDetailsWindow(heading, null, null, null, notes ?? "",
                                                  resourcesUrl ?? "", finished, onABranch,
                                                  acceptsContributions, "Save");
        await window.ShowDialog(owner);

        return Read(window);
    }

    /// <summary>
    /// The same window as the act of publishing: the languages it travels under, then everything
    /// said about it, then the button that sends.
    /// </summary>
    /// <param name="body">What the publication becomes, in the server's reading — see LineageStanding.Describe.</param>
    /// <param name="languages">
    /// What to show and what to ask, decided by <see cref="PublishLanguages.Decide"/>. ⚠ Must be
    /// one that <see cref="PublishLanguages.Ask.CanProceed"/>: a refusal is said by the caller in
    /// its own dialogue, never by opening a form that cannot be sent.
    /// </param>
    /// <param name="platform">Needed to build the source picker. Unused when the source is fixed.</param>
    /// <param name="confirm">The verb: Upload or Update.</param>
    /// <param name="game">
    /// On a first publication, the game to confirm with the site before anything is filed under
    /// it. Null on an update: the lineage already names its game and the server ignores any other.
    /// </param>
    public static async Task<TranslationDetails> PublishAsync(
        Window owner, string heading, string body, PublishLanguages.Ask languages,
        IPlatform platform, string? notes, string? resourcesUrl,
        bool finished, bool onABranch, bool acceptsContributions, string confirm,
        GameToConfirm? game = null)
    {
        if (!languages.CanProceed)
            throw new ArgumentException("A refusal is said before this window, not by it.", nameof(languages));

        var window = new TranslationDetailsWindow(heading, body, languages, platform, notes ?? "",
                                                  resourcesUrl ?? "", finished, onABranch,
                                                  acceptsContributions, confirm, game);
        await window.ShowDialog(owner);

        return Read(window);
    }

    private static TranslationDetails Read(TranslationDetailsWindow window) => new(
        window._saved,
        window._notes.Text?.Trim() ?? "",
        window._url.Text?.Trim() ?? "",
        window._finished?.IsChecked == true,
        window._contributions?.IsChecked == true,
        window._saved ? window.SourceName() : null,
        window._saved ? window._confirmedGame?.Name : null,
        window._saved ? window._confirmedGame?.SteamId : null);
}
