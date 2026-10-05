using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Api;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// Which game this is, confirmed with the site: the game line, a search field, the site's answers
/// marked ★ and ☆ — and, on a publication, the "Adults only" box under it.
///
/// 🔴 **One block for every place a game is chosen** (user, 2026-10-05: "ce serait le même écran
/// qu'à la publication"). It was built inside the publish window; Change on a game's card asks the
/// same question, and a second copy beside it would answer it differently the day one of them
/// moves. Both windows hold this.
///
/// ⚠ The same three parts as the mod's setup screen, in the same order: the game as read here and
/// whether it is confirmed, a search field, the site's answers.
/// </summary>
internal sealed class GamePickerBlock
{
    private readonly GameToConfirm _game;
    private readonly Func<string, IBrush?> _brush;
    private readonly Action _changed;

    private readonly TextBlock _gameName;
    private readonly TextBlock _gameState;
    private readonly TextBox _gameSearch;
    private readonly Button _gameSearchButton;
    private readonly TextBlock _gameSearchStatus;
    private readonly ListBox _gameResults;

    /// <summary>Under the game: "Different game?" when the one confirmed does not look like the one read here.</summary>
    private readonly TextBlock _gameWarning;

    // The "Adults only" box under the game, and what the site said about the confirmed one — null
    // until it answered about THAT game. The counter drops an answer that arrives after another
    // game was confirmed. Same box, same place and same words as the mod's (Common.AdultMarks).
    private readonly CheckBox? _adult;
    private readonly TextBlock? _adultNote;
    private CatalogApiClient.GameAdult? _adultAnswer;
    private int _adultAsked;

    /// <summary>
    /// 🔴 **One behaviour, wherever it is held** (user, 2026-10-05: "la validation doit demander un
    /// click même pour le publier... cohérent avec le change. on met en avant celui qui a le steamid
    /// ou le nom exacte mais on laisse clicker"). Nothing is picked on the person's behalf: the
    /// answers are listed, the likeliest first and marked ★, and only a row clicked — or the game
    /// already confirmed in this game — can be kept or published. It stops somebody clicking OK
    /// without reading. A game "taken as detected" is no longer sent at all.
    /// </summary>
    /// <param name="askAdult">A publication asks the "Adults only" question; choosing a game alone does not.</param>
    /// <param name="changed">Called whenever what can be sent changes, so the window re-judges its button.</param>
    public GamePickerBlock(GameToConfirm game, bool askAdult, Func<string, IBrush?> brush, Action changed)
    {
        _game = game;
        _brush = brush;
        _changed = changed;

        var gameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _gameName = new TextBlock
        {
            Text = game.DetectedName ?? "No game detected",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = brush("TextPrimary"),
        };
        _gameState = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Foreground = brush("StatusWarning"),
        };
        Grid.SetColumn(_gameName, 0);
        Grid.SetColumn(_gameState, 1);
        gameRow.Children.Add(_gameName);
        gameRow.Children.Add(_gameState);
        Controls.Add(gameRow);

        // Right under the game it is about: the one moment a wrong pick can still be undone.
        // A warning, never a block — GameCandidates.DifferentGame says why.
        _gameWarning = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Foreground = brush("StatusWarning"),
        };
        Controls.Add(_gameWarning);

        // Under the game it is about, as in the mod's setup screen. Hidden until the site has
        // answered about the confirmed game — see RefreshAdult for the three states.
        if (askAdult)
        {
            _adult = new CheckBox
            {
                Content = AdultMarks.Box,
                IsVisible = false,
                Foreground = brush("TextPrimary"),
            };
            Controls.Add(_adult);
            _adultNote = Hint("");
            _adultNote.IsVisible = false;
            Controls.Add(_adultNote);
        }

        var searchRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
        };
        // The box takes ids and store page links too (GET games/search) — said where it is typed.
        _gameSearch = new TextBox { Watermark = "Search by title, Steam ID or Steam link" };
        _gameSearchButton = new Button { Content = "Search" };
        Grid.SetColumn(_gameSearch, 0);
        Grid.SetColumn(_gameSearchButton, 1);
        searchRow.Children.Add(_gameSearch);
        searchRow.Children.Add(_gameSearchButton);
        Controls.Add(searchRow);

        _gameSearchStatus = Hint("");
        Controls.Add(_gameSearchStatus);

        // Each answer with what tells it apart from a game of the same title — its cover, its ids in
        // each store, its year, who made it (2026-10-05) — so the person can check before picking.
        _gameResults = new ListBox
        {
            MaxHeight = 240,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<CandidateRow?>((row, _) => RowOf(row)),
        };
        Controls.Add(_gameResults);

        Controls.Add(Hint(GameCandidates.Legend));

        _gameSearchButton.Click += async (_, _) => await SearchGamesAsync(_gameSearch.Text, null);
        _gameSearch.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) await SearchGamesAsync(_gameSearch.Text, null);
        };

        // 🔴 **Highlighting a row IS choosing it here**, unlike the language picker: the list is
        // short, the rows are answers, and the mod's screen confirms on a single click too.
        _gameResults.SelectionChanged += (_, _) =>
        {
            if (_showingConfirmed) return;
            if (_gameResults.SelectedItem is CandidateRow row)
                ConfirmGame((row.Candidate.Name ?? game.DetectedName ?? "", row.Candidate.SteamId ?? game.DetectedSteamId,
                             row.Candidate.Pick), row.Candidate.Ids);
        };

        ShowGame();
    }

    /// <summary>The block's controls, in reading order, for the window to lay out.</summary>
    public List<Control> Controls { get; } = new();

    /// <summary>
    /// The game picked so far — a row clicked, or the game already confirmed in this game — with the
    /// site's answer it came from, sent back as `game_pick`. Only a pick can be sent (<see cref="Complaint"/>).
    /// </summary>
    public (string Name, string? SteamId, GameCandidates.Pick? Pick)? Confirmed { get; private set; }

    /// <summary>Every id the answer confirmed gathers (by source), when it came from the list.</summary>
    private IReadOnlyDictionary<string, string>? _confirmedIds;

    /// <summary>Raised while the list highlights the game already confirmed — a display, not a pick.</summary>
    private bool _showingConfirmed;

    /// <summary>
    /// Whether what is confirmed IS <paramref name="held"/> — by any id of the answer, not only its
    /// source (Common.GameChoices.Holds): the same game clicked on another of its rows is nothing to apply.
    /// </summary>
    public bool Holds(GameChoice? held) =>
        Confirmed?.Pick is { } pick && GameChoices.Holds(held, pick.Source, pick.Id, _confirmedIds);

    /// <summary>The "Adults only" box, ticked where the site offered it — false everywhere else.</summary>
    public bool AdultDeclared =>
        _adultAnswer is { } adult && AdultMarks.Open(adult.Adult, adult.Declarable) && _adult?.IsChecked == true;

    /// <summary>
    /// Why what is confirmed cannot be sent, or null when it can: nothing confirmed, an answer of
    /// the list required and none taken, or a game the site said nothing identifies — which it
    /// would refuse (`game_not_found`), so it is said before the click.
    /// </summary>
    public string? Complaint =>
        Confirmed is not { Pick: not null } ? "Please select a game"
        : _adultAnswer?.Identified == false ? GameChoices.NotIdentified
        : null;

    /// <summary>
    /// What the mod's setup screen does on opening, the same in Change and in a publication: the
    /// game already confirmed in this game, shown as it is; otherwise the site's answers for the
    /// Steam id, else for the name, listed with the likeliest first — and nothing picked.
    ///
    /// 🔴 Never a game "taken as detected", never the first answer taken on the person's behalf
    /// (user, 2026-10-05). A Steam id the site answers nothing for is followed by the name, so the
    /// list still has something to click.
    /// </summary>
    public async Task StartAsync()
    {
        if (_game.Confirmed is { } chosen)
        {
            ConfirmGame((chosen.Name, chosen.Source == "steam" ? chosen.Id : null, chosen.AsPick()));
            return;
        }

        if (!string.IsNullOrWhiteSpace(_game.DetectedSteamId))
        {
            var found = await SearchGamesAsync(null, _game.DetectedSteamId);
            if (found is { Count: > 0 } || string.IsNullOrWhiteSpace(_game.DetectedName)) return;
        }

        if (!string.IsNullOrWhiteSpace(_game.DetectedName))
        {
            _gameSearch.Text = _game.DetectedName;
            await SearchGamesAsync(_game.DetectedName, null);
        }
    }

    /// <summary>Asks the site and fills the list, likeliest first. Returns what it answered.</summary>
    private async Task<IReadOnlyList<CatalogApiClient.GameCandidate>?> SearchGamesAsync(string? query, string? steamId)
    {
        query = query?.Trim();
        if (steamId is null && (query is null || query.Length < 2))
        {
            Ui.Say(_gameSearchStatus, "Enter at least 2 characters", Tone.Warning);
            return null;
        }

        // Already asking: the same question, not a second one (Busy holds the button meanwhile).
        if (!_gameSearchButton.IsEnabled) return null;

        Ui.Say(_gameSearchStatus, "Searching…");
        _gameResults.ItemsSource = null;

        // The gear on the button, for as long as the site is asked — the automatic search at
        // opening included, which nobody pressed and which otherwise showed nothing (2026-10-05).
        IReadOnlyList<CatalogApiClient.GameCandidate>? found = null;
        await Busy.While(_gameSearchButton, async () => found = await _game.Search(query, steamId));

        if (found is null)
        {
            // The reason, and nothing taken in its place: the person searches again.
            Ui.Say(_gameSearchStatus, _game.WhyNot() ?? "UGT Website could not be reached.", Tone.Warning);
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

        // The game confirmed so far, highlighted when this answer lists it — by any of its ids — so
        // the list shows what Apply would keep. Shown, not chosen again: nothing to re-confirm.
        if (Confirmed is { Pick: { } held } confirmed
            && rows.FirstOrDefault(row => row.Candidate.Holds(new GameChoice(held.Source, held.Id, confirmed.Name))) is { } shown)
        {
            _showingConfirmed = true;
            _gameResults.SelectedItem = shown;
            _showingConfirmed = false;
        }

        // An empty list says what to try next — the box takes ids and store links too — in the
        // words the site's own list uses (GameCandidates.NothingFound). Without an account the list
        // is the site's games alone, and both sentences say so (GameCandidates.CatalogueOnly).
        bool stores = _game.AskedStores?.Invoke() ?? true;
        if (rows.Count == 0) Ui.Say(_gameSearchStatus, GameCandidates.NothingFoundFor(stores), Tone.Warning);
        else Ui.Say(_gameSearchStatus, (rows.Count == 1 ? "Found 1 game" : $"Found {rows.Count} games")
                                       + (stores ? "" : ". " + GameCandidates.CatalogueOnly));

        return found;
    }

    /// <summary>
    /// The one way the confirmed game changes — the line, the button and the adult question follow
    /// it, so no path can leave the box answering about the previous game.
    /// </summary>
    private void ConfirmGame((string Name, string? SteamId, GameCandidates.Pick? Pick) game,
                             IReadOnlyDictionary<string, string>? ids = null)
    {
        Confirmed = game;
        _confirmedIds = ids;
        ShowGame();

        // Said before sending, never refused here: the site decides what is sure (a demo reads its
        // own Steam id), this only shows what does not look alike.
        var warning = GameCandidates.DifferentGame(_game.DetectedSteamId, _game.DetectedName, game.SteamId, game.Name);
        _gameWarning.Text = warning ?? "";
        _gameWarning.IsVisible = warning is not null;

        _changed();
        _ = AskTheSiteAsync(game);
    }

    /// <summary>
    /// Ask the site about the confirmed game, with the name, id and pick the upload will send —
    /// whether it is for adults only, and whether anything identifies it.
    /// </summary>
    private async Task AskTheSiteAsync((string Name, string? SteamId, GameCandidates.Pick? Pick) game)
    {
        int asked = ++_adultAsked;
        _adultAnswer = null;
        RefreshAdult();

        var answer = await _game.Adult(game.SteamId, game.Name, game.Pick);
        if (asked != _adultAsked) return; // another game was confirmed meanwhile

        _adultAnswer = answer;
        RefreshAdult();
        _changed();
    }

    /// <summary>
    /// The box, from the site's answer — the mod's three states (Common.AdultMarks): classified,
    /// shown ticked and locked with who says so; not classified and added by this upload, open with
    /// what ticking it does; anything else, or no answer, nothing at all.
    ///
    /// ⚠ Locked and ticked is deliberate here, and not the "ticked then greyed" this program avoids
    /// elsewhere: the person is being TOLD a fact about the game, and the sentence under it says who
    /// decided — it is not a choice somebody else made in their place (decided with the owner,
    /// 2026-09-23).
    /// </summary>
    private void RefreshAdult()
    {
        if (_adult is null || _adultNote is null) return;

        var answer = _adultAnswer;
        bool shown = answer is not null && AdultMarks.Shown(answer.Adult, answer.Declarable);

        _adult.IsVisible = shown;
        _adultNote.IsVisible = shown;
        if (!shown) return;

        bool open = AdultMarks.Open(answer!.Adult, answer.Declarable);
        _adult.IsChecked = answer.Adult;
        _adult.IsEnabled = open;
        _adultNote.Text = open ? AdultMarks.WhatItDoes : AdultMarks.Source(answer.Source);
    }

    /// <summary>
    /// The game line: the game the window's act would keep or send, and its state against what is
    /// SAVED, in the words of the title's chip (Common.GameChoices.IdentityBadge) — Confirmed only
    /// for the game written in this game, Detected while nothing is. A row clicked and not yet
    /// applied or published carries no state: the button beside it says it.
    /// </summary>
    private void ShowGame()
    {
        var held = _game.Confirmed;
        bool pending = Confirmed is { } picked && !Holds(held);

        if (pending)
        {
            _gameName.Text = Confirmed!.Value.Name;
            _gameName.Foreground = _brush("TextPrimary");
            _gameState.Text = "";
            return;
        }

        var name = held?.Name ?? _game.DetectedName;
        _gameName.Text = string.IsNullOrWhiteSpace(name) ? "No game detected" : name;
        _gameName.Foreground = _brush("TextPrimary");

        // Not on the site from here: this window is only offered while the game is not fixed by it.
        var chip = GameChoices.IdentityBadge(!string.IsNullOrWhiteSpace(name), held is not null, onTheSite: false);
        _gameState.Text = chip?.Text ?? "";
        _gameState.Foreground = _brush(chip is { } c ? TranslationBadges.ToneKey(c.Tone) : "TextMuted");
        if (chip is { } tip) ToolTip.SetTip(_gameState, tip.Tip);
    }

    /// <summary>One answer: its cover, then its name with source and mark, then what tells it apart.</summary>
    private Control RowOf(CandidateRow? row)
    {
        // 🔴 **Called with nothing when the list is emptied** — a new search sets the items to
        // null and the containers are rebuilt empty. Unchecked, the template threw on the UI
        // thread and the whole program closed without a word (2026-10-05).
        if (row is null) return new Panel();

        // The picture, and behind it a blurred copy that only shows for a wide one — the frame of
        // the site's lists (`<x-game-cover>`), by the socle's rule (GameCandidates.FillsFrame).
        var backdrop = new Image
        {
            Stretch = Stretch.UniformToFill,
            IsVisible = false,
            Opacity = 0.75,
            Effect = new BlurEffect { Radius = 6 },
        };
        var cover = new Image { Stretch = Stretch.UniformToFill };
        var frame = new Border
        {
            Width = 30,
            Height = 42,
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Background = _brush("SurfaceInput"),
            Child = new Panel { Children = { backdrop, cover } },
        };
        _ = LoadCoverAsync(cover, backdrop, row.Candidate.ImageUrl);

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = row.ToString(), TextWrapping = TextWrapping.Wrap, Foreground = _brush("TextPrimary") });
        if (row.Candidate.Facts.Length > 0)
            text.Children.Add(new TextBlock { Text = row.Candidate.Facts, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = _brush("TextMuted") });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(frame, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(frame);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>Where covers are fetched from: the stores' own image servers, directly.</summary>
    private static readonly System.Net.Http.HttpClient Covers =
        UnityGameTranslator.Manager.Core.Net.Http.Create(TimeSpan.FromSeconds(15));

    /// <summary>
    /// The cover, fetched from where the site says it is, while the list is on screen — kept in
    /// memory with the row, never written to disk (user, 2026-10-04: "on veut juste les afficher
    /// temporairement… un moyen d'identification éphémère"). Directly from the store's image server,
    /// not through the site: a middle hop is the same image twice and bandwidth paid for nothing.
    ///
    /// ⚠ HTTPS only. A cover that does not come leaves the empty frame: it is a help to recognise
    /// the game, and the name and the facts beside it still say which one it is.
    ///
    /// 🔴 **Its shape decides how it is shown** (GameCandidates.FillsFrame, user 2026-10-06): taller
    /// than wide fills the frame; wider — a store header, a screenshot — is shown whole, over its
    /// own blurred copy. Cropped, a header kept its middle third.
    /// </summary>
    private static async Task LoadCoverAsync(Image cover, Image backdrop, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var bytes = await Covers.GetByteArrayAsync(url);

            // Decoded off the UI thread: a list of covers decoded on it is a window that stutters
            // while the rows appear. Only the result is handed back to the row.
            var bitmap = await Task.Run(() =>
            {
                using var stream = new System.IO.MemoryStream(bytes);
                return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 60);
            });

            bool fills = GameCandidates.FillsFrame(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            cover.Stretch = fills ? Stretch.UniformToFill : Stretch.Uniform;
            cover.Source = bitmap;
            backdrop.Source = fills ? null : bitmap;
            backdrop.IsVisible = !fills;
        }
        catch (Exception ex)
        {
            // Said in the log, once per address: the frame stays empty on screen.
            System.Diagnostics.Trace.WriteLine($"[GamePicker] Cover not shown ({url}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = _brush("TextMuted"),
    };

    /// <summary>One answer from the site, as the list shows it.</summary>
    private sealed record CandidateRow(CatalogApiClient.GameCandidate Candidate, int Confidence)
    {
        public override string ToString() =>
            GameCandidates.Row(Candidate.Name, Candidate.Source, Confidence);
    }
}
