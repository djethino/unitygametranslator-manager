using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UnityGameTranslator.Installer.Core.Api;
using UnityGameTranslator.Installer.Core.Catalog;
using UnityGameTranslator.Installer.Core.Detection;
using UnityGameTranslator.Installer.Core.Install;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;
using UnityGameTranslator.Installer.Core.Settings;
using UnityGameTranslator.Installer.Core.Update;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Installer.Gui;

/// <summary>
/// The window. It renders what Core decided and calls back into Core — it holds no rule of its
/// own, which is why the command line and this show the same thing.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IPlatform _platform;
    private LoaderCatalogDocument _catalog = null!;
    private GameInventory _inventory = null!;

    private readonly List<GameInstall> _games = new();
    private GameInstall? _selected;

    private SettingsStore _settings = null!;
    private OnlineCatalogCache _online = null!;
    private CancellationTokenSource? _sweep;

    /// <summary>
    /// The account's place in the lineages it takes part in, read once and kept for the life of
    /// this window. Dropped when the account changes — see <see cref="OpenToolSettingsAsync"/>.
    /// </summary>
    private readonly AccountLineages _lineages = new();

    /// <summary>Which games are open right now. Never null: an unswept machine is an empty answer.</summary>
    private RunningGames _running = RunningGames.None;

    private DispatcherTimer? _runningClock;

    /// <summary>
    /// When each watched game's translation file was last seen to change, by game path.
    ///
    /// Only games being played are in here, and they leave it when they close. It is what turns a
    /// ten-second question into a directory lookup instead of a file read.
    /// </summary>
    private readonly Dictionary<string, DateTime> _watchedStamps =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ⚠ The beginning of an orchestration, and deliberately still small.
    ///
    /// Two cadences on one clock, because two questions have different costs and different worth:
    /// which games are open is asked every four seconds because it is nearly free, and whether a
    /// played game's file has moved is asked every ten because that is as often as it can usefully
    /// change — the mod's own save is debounced to thirty seconds.
    ///
    /// One clock rather than two, because two clocks drift apart and nothing then knows the whole
    /// picture. When the periodic checks of versions, translations and branches arrive, they join
    /// this counter instead of starting their own — see the note in TODO.md about the tray, which is
    /// the point at which registering services would stop being an abstraction.
    /// </summary>
    private int _clockTicks;

    private const int TicksBetweenFileChecks = 3;   // 4 s per tick → about twelve seconds

    /// <summary>Situation per game path, so a row can be redrawn without redoing the work.</summary>
    private readonly Dictionary<string, GameSituationInfo> _situations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What the list is being asked to show. Not a <see cref="Situation"/>: those describe a game,
    /// and one of these describes the person — which games they take part in — so it could not be
    /// expressed as one without inventing a state a game does not have.
    /// </summary>
    private enum Lens { All, Playable, NeedsTranslator, Ready, Mine, Running, Blocked }

    private Lens _lens = Lens.All;

    /// <summary>
    /// Games whose installed translation belongs to a lineage this account takes part in, by path.
    ///
    /// Gathered while the situations are computed, where the local file is read anyway. Asking the
    /// question per row at filter time would re-read a file from disk on every keystroke in the
    /// search box.
    /// </summary>
    private readonly HashSet<string> _mine = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The rows currently in the list, by game path, with what each was saying when it was built.
    ///
    /// ⚠ These belong to the ListBox. They are here to be UPDATED in place, never to be handed
    /// back through a new ItemsSource: an item carries its visual parent, and reusing one across
    /// two sources leaves the virtualising panel unable to anchor it — the window then goes down
    /// with no message at all.
    /// </summary>
    private readonly Dictionary<string, (string Signature, ListBoxItem Item)> _rows =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True while the list is putting the selection back after being rebuilt.
    ///
    /// The event cannot tell a player's click from our own housekeeping, and treating the second
    /// as the first meant redrawing the card on the right for nothing — repeatedly, while the
    /// sweep was still running.
    /// </summary>
    private bool _restoringSelection;

    public MainWindow()
    {
        // InitializeComponent is generated by the Avalonia XAML compiler, and it is what wires
        // up the x:Name fields. Declaring one by hand hides it, leaving every named control
        // null — which fails at construction, not at build.
        InitializeComponent();

        _platform = PlatformFactory.Create();

        SearchBox.TextChanged += (_, _) => RefreshList();
        RescanButton.Click += async (_, _) => await ScanAsync();
        AddFolderButton.Click += async (_, _) => await AddFolderAsync();
        FoldersButton.Click += async (_, _) => await ManageFoldersAsync();
        SettingsButton.Click += async (_, _) => await OpenSettingsAsync();
        ToolSettingsButton.Click += async (_, _) => await OpenToolSettingsAsync();
        AboutButton.Click += async (_, _) => await new AboutWindow().ShowDialog(this);
        GameList.SelectionChanged += async (_, _) =>
        {
            if (_restoringSelection) return;
            await ShowSelectedAsync();
        };

        // Escape closes the card, like it closes everything else. Tunnelling rather than bubbling
        // so it is heard even while the search box or the list has the focus, which is where the
        // focus actually is when someone wants out.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape) return;
            if (_selected is null) return;

            CloseCard();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _settings = new SettingsStore(_platform);
        _online = new OnlineCatalogCache(_platform);

        // After the settings exist, not while the buttons are being wired: this reads the stored
        // token, and reading it a line too early is a null reference at startup rather than a
        // wrong pixel.
        ShowAccount();

        BuildLanguageBox();
        BuildFilterBar();

        Loaded += async (_, _) =>
        {
            await ScanAsync();

            // After the scan, never before: the games are what someone opened the tool for, and a
            // question put to GitHub must not delay the list by so much as a frame.
            await LookForToolUpdateAsync();
        };
    }

    // ---------------------------------------------------------------- this tool's own updates

    /// <summary>
    /// Asks once, at startup, whether a newer build of the tool exists — and says so with a notice
    /// that leads somewhere rather than a box to dismiss.
    ///
    /// Silent when there is nothing new: "up to date" on every launch is a line people stop seeing,
    /// and then it is not there when it matters.
    ///
    /// ⚠ NOT silent when the check failed, and that distinction was got wrong here first. A tool
    /// that says nothing after a blocked request is telling someone it looked, when it did not —
    /// and a firewall prompt that was refused, or never appeared, looks exactly like this. The same
    /// rule as everywhere else in this window: name it, and put the way out one click away. There
    /// is no automatic retry, on purpose — a connection a firewall is refusing does not become
    /// available by being asked again a second later, and asking on a timer is how a tool ends up
    /// looking like the thing the firewall was right to stop.
    /// </summary>
    private async Task LookForToolUpdateAsync()
    {
        var settings = _settings.Current;
        if (!settings.OnlineMode || !settings.CheckToolUpdates) return;

        SelfUpdateCheck result;
        try
        {
            result = await new SelfUpdater(_platform).CheckAsync(settings.ToolReleaseChannel);
        }
        catch (Exception ex)
        {
            result = new SelfUpdateCheck(SelfUpdateState.CheckFailed, null, ex.Message);
        }

        switch (result.State)
        {
            case SelfUpdateState.Available when result.Offer is not null:
                ShowUpdateNotice($"Update available: {result.Offer.NewVersion}",
                    "Open Settings to see what changed and install it.",
                    primary: true, result);
                break;

            case SelfUpdateState.UpToDate:
                break;

            default:
                ShowUpdateNotice("Couldn't check for updates",
                    "Updates to UnityGameTranslator Manager — this program, not the mod in your "
                    + $"games.\n\n{result.Message}\n\nA firewall, an antivirus or a company proxy "
                    + "blocking it looks exactly like this. Open Settings to try again or to set "
                    + "up a proxy.",
                    primary: false, result);
                break;
        }
    }

    private void ShowUpdateNotice(string label, string tip, bool primary, SelfUpdateCheck found)
    {
        var notice = new Button { Content = label, FontSize = 12 };
        if (primary) notice.Classes.Add("primary");

        ToolTip.SetTip(notice, tip);

        // The answer travels with the click: someone who opened this window to find out why must
        // land on the reason, not on an empty panel and a button to press again.
        notice.Click += (_, _) => OpenToolSettings(found);

        UpdateSlot.Content = notice;
    }

    // ---------------------------------------------------------------- scanning

    private async Task ScanAsync()
    {
        Busy(true, "Looking for the loader catalog...");

        _sweep?.Cancel();

        var result = await Task.Run(() => new CatalogProvider(_platform).Get());
        _catalog = result.Document;
        _inventory = new GameInventory(_platform, _catalog, new CatalogApiClient())
        {
            Lineages = _lineages,
        };

        Status($"Catalog: {_catalog.Loaders.Count} loaders ({result.Source}). Scanning your drives...");

        var found = await Task.Run(() => _inventory.ScanAll());

        // The games themselves are being replaced, so rows built for the previous set have nothing
        // left to describe.
        _rows.Clear();

        _games.Clear();
        _games.AddRange(found.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase));

        // Before the rows are built, so the first thing drawn is already right rather than correct
        // itself four seconds later.
        var toSweep = _games.ToList();
        _running = await Task.Run(() => RunningGames.Sweep(toSweep));
        WatchForRunningGames();

        var blocked = _games.Count(g => !g.IsModdable);
        SubtitleText.Text = blocked == 0
            ? $"{_games.Count} Unity games found"
            : $"{_games.Count} Unity games found, {blocked} that cannot be modded";

        // Read before the situations, not on the first click: "My translations" has to be able to
        // answer as soon as the window is up, and this is one call for the whole library.
        await _lineages.EnsureAsync(_settings.Current.ApiToken);

        BuildFilterBar();
        RecomputeSituations();
        RefreshList();
        ShowOverview();
        Busy(false, "Ready.");

        StartOnlineSweep();
    }

    /// <summary>
    /// Fills in what the community has, in the background.
    ///
    /// The window is usable immediately and rows sharpen as answers arrive, rather than the list
    /// staying empty until every game has been looked up. Only Steam gives us an id to look up
    /// with, so the others simply say so instead of pretending to know.
    /// </summary>
    private void StartOnlineSweep()
    {
        if (!_settings.Current.OnlineMode) return;

        // Every moddable game, not just the Steam ones: a library bought on Epic or installed
        // by hand would otherwise read "no translation yet" for ever.
        var ids = _games.Where(g => g.IsModdable)
                        .Select(OnlineCatalogCache.KeyFor)
                        .Distinct()
                        .ToList();
        if (ids.Count == 0) return;

        _sweep = new CancellationTokenSource();
        var token = _sweep.Token;

        _ = Task.Run(async () =>
        {
            var done = 0;
            await _online.RefreshAsync(ids, async (appId, _) =>
            {
                done++;
                var progress = done;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    RecomputeSituations();

                    // Contents only while a sweep is running. A lookup changes what we know about
                    // a game, never whether it exists — except under a filter, where learning
                    // something can move a game in or out of the visible set, and then the list
                    // genuinely has to be rebuilt.
                    if (_lens == Lens.All) RefreshRowContents();
                    else RefreshList();

                    Status($"Checking community translations... {progress}/{ids.Count}");
                });
            }, token);

            if (!token.IsCancellationRequested)
                await Dispatcher.UIThread.InvokeAsync(() => Status("Ready."));
        }, token);
    }

    /// <summary>
    /// Rebuilds every row's situation from what is currently known. Cheap: it reads the caches,
    /// it does not go looking again.
    /// </summary>
    private void RecomputeSituations()
    {
        _situations.Clear();
        _mine.Clear();

        foreach (var game in _games)
        {
            var (situation, mine) = ReadSituation(game);
            _situations[game.Path] = situation;
            if (mine) _mine.Add(game.Path);
        }
    }

    /// <summary>
    /// One game's situation, read from the disk and from what the community lookup already said.
    ///
    /// Pulled out of the loop so a single game can be re-read on its own — which is what a game
    /// being played needs, since the mod is writing to its translation file while somebody plays.
    /// Nothing here touches the network: the online answer comes from the cache and never from a
    /// fresh request, so re-reading one game costs a file and no quota.
    ///
    /// ⚠ Touches no control AND no shared state: it runs on a background thread while a game is
    /// being played, and _mine is a set the interface thread edits elsewhere. Whether this game
    /// belongs to the account is therefore RETURNED rather than recorded, and the caller writes it
    /// down where doing so is safe. Reaching into that set from here was a race with a full
    /// recompute — rare, and the kind that corrupts a collection rather than failing cleanly.
    /// </summary>
    private (GameSituationInfo Situation, bool Mine) ReadSituation(GameInstall game)
    {
        var language = _settings.ResolveTargetLanguage();
        var online = _online.Peek(game);
        var report = new GameReport { Game = game };
        var mine = false;

        var detected = LoaderProbe.Detect(game.Path, _catalog);
        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == detected?.Id);

        if (descriptor is not null)
        {
            report.InstalledPluginVersion =
                LocalTranslationProbe.ReadInstalledPluginVersion(game.Path, descriptor);
            report.LocalTranslation = LocalTranslationProbe.Read(game.Path, descriptor);

            // Noted while the file is open anyway. Asked again at filter time it would mean
            // re-reading a translation from disk on every keystroke in the search box.
            mine = _lineages.For(report.LocalTranslation?.Uuid) is not null;
        }

        if (online is not null)
        {
            report.OnlineTranslations = online;
            if (report.LocalTranslation?.Uuid is { Length: > 0 } uuid)
            {
                report.MatchingOnline = online.FirstOrDefault(
                    t => string.Equals(t.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
            }
        }

        var checkedOnline = online is not null || !_settings.Current.OnlineMode;

        return (SituationReader.Read(report, language, checkedOnline), mine);
    }

    /// <summary>
    /// Opens the defaults, and re-reads the list when they change.
    ///
    /// The language lives in two places on purpose — the header, for a quick switch while
    /// looking at the list, and the settings, because a screen called "defaults for every game"
    /// that does not hold the language would send the reader looking for it. They stay in step
    /// because the settings are a modal dialog: nothing can drift while it is open.
    /// </summary>
    /// <summary>
    /// What the last AI server search found, for as long as this window lives.
    ///
    /// Held here rather than in the settings dialog because the dialog is built anew every time it
    /// is opened, which is exactly the thing that was making it re-probe six ports on each visit.
    /// </summary>
    private readonly AiServerMemory _aiServers = new();

    private async Task OpenSettingsAsync()
    {
        var window = new SettingsWindow(_platform, _settings, _aiServers);
        await window.ShowDialog(this);

        if (!window.Saved) return;

        SyncLanguageBox();
        RecomputeSituations();
        RefreshList();
        await ShowWhateverIsOnTheRightAsync();
    }

    /// <summary>
    /// Redraws the right-hand side, whichever of its two faces is showing.
    ///
    /// ⚠ It used to redraw the game's card and nothing else, so with no game selected — the state
    /// somebody is in when they open the settings from the overview — nothing was redrawn at all.
    /// The notice saying nothing had been decided about the games stayed up after deciding it.
    ///
    /// Worth noticing that this only became visible once applying stopped closing the window: the
    /// two changes are a day apart and the second one exposed the first.
    /// </summary>
    private async Task ShowWhateverIsOnTheRightAsync()
    {
        if (_selected is null) { ShowOverview(); return; }

        await ShowSelectedAsync();
    }

    /// <summary>
    /// Asks the community site again after a failure.
    ///
    /// Cheap by construction: a failed lookup is never cached as "no translations", so the entry
    /// is still stale and a plain sweep retries it. Nothing about the games, the choices or the
    /// scan is redone — which is the whole point. Someone who has just allowed us through their
    /// firewall should be one click from carrying on, not from starting over.
    /// </summary>
    private async Task RetryOnlineAsync()
    {
        Status("Asking the community site again...");
        StartOnlineSweep();
        await ShowSelectedAsync();
    }

    /// <summary>
    /// What exists for this game, counted by language, with the reader's own language first.
    ///
    /// The order is the answer: someone scanning this line wants to know whether they can play,
    /// and only then what else is around. Naming the other languages when there are few of them
    /// costs nothing and saves opening a screen; past a handful it becomes the noise it was meant
    /// to replace, so it turns into a count.
    /// </summary>
    private static string SummariseLanguages(IReadOnlyList<OnlineTranslation> translations,
                                             string targetLanguage)
    {
        var mine = translations.Count(t => Languages.Matches(t.TargetLanguage, targetLanguage));

        var others = translations
            .Where(t => !Languages.Matches(t.TargetLanguage, targetLanguage))
            .Select(t => t.TargetLanguage)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var yours = Languages.NameOf(targetLanguage);

        var head = mine > 0
            ? $"From the community: {mine} in {yours}"
            : $"From the community: none in {yours} yet";

        if (others.Count == 0) return head + ".";

        var otherCount = translations.Count - mine;

        // Named while the list stays readable, counted once it would not. Five is where a line
        // stops being scannable at this size, not a figure with any deeper meaning.
        var tail = others.Count <= 5
            ? $"{otherCount} in {string.Join(", ", others)}"
            : $"{otherCount} in {others.Count} other languages";

        return $"{head}, {tail}.";
    }

    /// <summary>
    /// Opens the list of community translations for a game.
    ///
    /// The loader is resolved here because only this window holds the catalog, and because the
    /// folder a translation lives in is a catalog entry of its own — a detected loader knows where
    /// its plugins go, not where the mod keeps its file. Writing to the wrong one would put a
    /// translation somewhere the mod never looks, and it would read as "the download did nothing".
    /// </summary>
    private async Task OpenTranslationsAsync(GameReport report)
    {
        var loaderId = report.InstalledLoader?.Id ?? report.RecommendedLoader?.Id;
        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == loaderId);

        if (descriptor is null)
        {
            Status("No loader is set up for this game yet, so there is nowhere to put a translation.");
            return;
        }

        var window = new TranslationsWindow(report, descriptor, _settings, _lineages);
        await window.ShowDialog(this);

        // Only when something was actually written: re-reading the game on every close would
        // rescan for nothing each time somebody just looked.
        if (!window.Changed) return;

        RecomputeSituations();
        RefreshList();
        await ShowSelectedAsync();
    }

    /// <summary>
    /// Whether we are signed in, said on the screen people actually look at.
    ///
    /// Until now this only appeared inside a settings window, so the answer to "am I signed in"
    /// required opening a dialog to find out — and someone who had signed in three days earlier
    /// had no way of knowing it still held.
    ///
    /// Signed out it opens the settings, which is where signing in happens. Signed in it opens the
    /// account on the site, because everything one does with an account — revoking a token,
    /// renaming, looking at one's translations — lives there and not here.
    /// </summary>
    private void ShowAccount()
    {
        var settings = _settings.Current;

        if (!settings.SignedIn)
        {
            var signIn = new Button { Content = "Sign in", FontSize = 12 };
            signIn.Click += async (_, _) => await OpenToolSettingsAsync();
            ToolTip.SetTip(signIn, "Optional. Published translations can be taken without an account.");
            AccountSlot.Content = signIn;
            return;
        }

        var name = settings.ApiUser ?? "your account";

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        row.Children.Add(Avatar(name));
        row.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = Brush("TextSecondary"),
        });

        var button = new Button
        {
            Content = row,
            Padding = new Avalonia.Thickness(6, 2),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        ToolTip.SetTip(button, "Open your account on the website");
        button.Click += (_, _) => OpenUrl($"{BuildInfo.WebsiteBaseUrl}/profile");

        AccountSlot.Content = button;
    }

    /// <summary>
    /// An initial in a coloured disc.
    ///
    /// Deliberately NOT the website's avatar: those are drawn by DiceBear in the browser from a
    /// seed, and reproducing that here would be a lot of work for a 26-pixel dot — and the seed is
    /// not even in the API. An initial claims nothing it cannot deliver.
    ///
    /// The colour comes from the name, so it is the same disc every time rather than a shade that
    /// moves between launches for no reason anybody could explain.
    /// </summary>
    private static Control Avatar(string name)
    {
        var initial = name.Trim().Length > 0 ? char.ToUpperInvariant(name.Trim()[0]).ToString() : "?";

        // A stable hash of our own: string.GetHashCode is randomised per process in .NET, so it
        // would give a different colour on every launch.
        var hash = name.Aggregate(17, (current, c) => current * 31 + c);
        var hues = new[] { "#9333EA", "#3B82F6", "#22C55E", "#F97316", "#A855F7", "#06B6D4" };
        var colour = hues[Math.Abs(hash) % hues.Length];

        return new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new Avalonia.CornerRadius(13),
            Background = Avalonia.Media.SolidColorBrush.Parse(colour),
            Child = new TextBlock
            {
                Text = initial,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = Avalonia.Media.Brushes.White,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };
    }

    private static void OpenUrl(string url) => Shell.OpenUrl(url);

    /// <summary>
    /// This tool's own settings — account and network.
    ///
    /// A second window rather than a section of the first: what goes into a game and what this
    /// program does are two subjects, and someone changing a value has to know which one they are
    /// touching without reading a heading.
    /// </summary>
    private void OpenToolSettings(SelfUpdateCheck? found) =>
        _ = OpenToolSettingsAsync(found);

    private async Task OpenToolSettingsAsync(SelfUpdateCheck? found = null)
    {
        var window = new ToolSettingsWindow(_platform, _settings, found);
        await window.ShowDialog(this);

        // Redrawn whatever was saved: signing in and out both happen in that window, and the
        // header would otherwise keep claiming the opposite until the next launch.
        ShowAccount();

        // The roles belong to whoever was signed in. Keeping them after a sign-out would leave a
        // card claiming "you are the Main here" to nobody in particular, and after a switch of
        // account it would claim it for the wrong person.
        _lineages.Forget();
        await _lineages.EnsureAsync(_settings.Current.ApiToken);

        // "My translations" appears on signing in and goes away on signing out, so the bar has to
        // be rebuilt — and the list with it, since what belongs to whom has just changed.
        if (_lens == Lens.Mine && !_settings.Current.SignedIn) _lens = Lens.All;

        BuildFilterBar();
        RecomputeSituations();
        RefreshList();

        // The strip above the summary answers questions about this program — where it lives, which
        // channel it follows — and every one of them can have changed in that window. Redrawn even
        // when nothing was saved: installing or removing the tool happens immediately, with no
        // Apply of its own.
        await ShowWhateverIsOnTheRightAsync();

        if (!window.Saved) return;

        // Signing in or changing the proxy both change what the community lookup can answer, so
        // what is on screen has to be asked again rather than left as it was.
        await RetryOnlineAsync();
    }

    /// <summary>Puts the header picker back in step with what was just saved.</summary>
    private void SyncLanguageBox()
    {
        var current = _settings.Current.TargetLanguage;
        foreach (var item in LanguageBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase))
            {
                LanguageBox.SelectedItem = item;
                return;
            }
        }
    }

    // ---------------------------------------------------------------- language and filters

    private void BuildLanguageBox()
    {
        // "auto" first: following the system is a legitimate answer, and it is the mod's default.
        // "auto" says which language it resolved to: "System language" alone leaves the reader
        // guessing what the rest of the window is talking about.
        var detected = _platform.SystemLanguage();
        var autoLabel = detected is not null
            ? $"System language ({Languages.NameOf(detected)})"
            : "System language";
        LanguageBox.Items.Add(new ComboBoxItem { Content = autoLabel, Tag = "auto" });
        foreach (var (code, name) in Languages.All())
            LanguageBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });

        var current = _settings.Current.TargetLanguage;
        foreach (var item in LanguageBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, current, StringComparison.OrdinalIgnoreCase))
            {
                LanguageBox.SelectedItem = item;
                break;
            }
        }
        LanguageBox.SelectedItem ??= LanguageBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (LanguageBox.SelectedItem is not ComboBoxItem { Tag: string code }) return;
            if (code == _settings.Current.TargetLanguage) return;

            var updated = _settings.Current;
            updated.TargetLanguage = code;
            updated.Reviewed = true;
            _settings.Save(updated);

            // The language is the context for every row: changing it re-reads the whole list.
            RecomputeSituations();
            RefreshList();
        };
    }

    private void BuildFilterBar()
    {
        FilterBar.Children.Clear();

        // Short on the tag, whole in the tooltip. Six tags at their full wording wrapped onto three
        // lines in a 370px column, which costs a row of the game list to say what a hover already
        // says — and the sentences were there for readers who did not need them by their second
        // visit.
        var filters = new List<(string Label, string Meaning, Lens Value)>
        {
            ("All", "Every game found, whatever its state.", Lens.All),
            ("In my language", "Games with a translation published in the language you are setting up.", Lens.Playable),
            ("Untranslated", "Games nobody has published a translation for in your language yet — where you would come in.", Lens.NeedsTranslator),
            ("Set up", "Games that already have the mod installed.", Lens.Ready),
        };

        // Only offered when there is an account to answer it. A filter that can only ever return
        // nothing is worse than an absent one: it reads as "you have none" rather than "we cannot
        // know". The bar is rebuilt when the account changes, so it appears on signing in.
        if (_settings.Current.SignedIn)
        {
            filters.Add(("Mine", "Games carrying a translation you take part in — the one you "
                               + "publish, or a branch of somebody else's.", Lens.Mine));
        }

        // Before "not moddable", and only while something is running: a lens that can only ever
        // return nothing reads as "you have none" rather than "there are none right now". It comes
        // and goes with the games themselves, which is why the bar is rebuilt when the sweep
        // changes its mind.
        if (_running.Paths.Count > 0)
        {
            filters.Add(("Running", "Games open right now — they cannot be set up or removed until "
                                  + "they are closed.", Lens.Running));
        }

        filters.Add(("Not moddable", "Games no loader can start in, with the reason on each card.",
                     Lens.Blocked));

        foreach (var (label, meaning, value) in filters)
        {
            var button = new Button
            {
                Content = label,
                Tag = value,
                Classes = { "filter" },
                Margin = new Avalonia.Thickness(0, 0, 6, 6),

                // Filling its cell, so the three columns line up and a tag's width says nothing
                // about its importance. Centred inside, since a stretched button with left-aligned
                // text drifts away from the column it belongs to.
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Avalonia.Thickness(6, 4),
            };

            ToolTip.SetTip(button, meaning);
            button.Click += (_, _) =>
            {
                _lens = value;
                foreach (var other in FilterBar.Children.OfType<Button>())
                    other.Classes.Set("selected", ReferenceEquals(other, button));
                RefreshList();
            };
            FilterBar.Children.Add(button);
        }

        foreach (var button in FilterBar.Children.OfType<Button>())
            button.Classes.Set("selected", (Lens?)button.Tag == _lens);
    }

    private async Task AddFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where is the game installed?",
            AllowMultiple = false,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;

        // Remember it, so the next run finds it without asking again — and so it can be shown
        // and taken back out. A folder added and then invisible is a folder the user cannot
        // manage.
        _inventory.Folders.Add(path);

        // The folder may hold one game or a whole library; scanning covers both.
        var found = StoreScanner
            .ScanFolder(Path.GetFullPath(path), GameStore.Manual, maxDepth: 2)
            .ToList();

        if (found.Count == 0)
        {
            Status($"No Unity game found in {path}. The folder was still added to your list.");
            RefreshList();
            return;
        }

        var added = 0;
        foreach (var game in found)
        {
            if (_games.Any(g => string.Equals(g.Path, game.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            _games.Add(game);
            added++;
        }

        _games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        Status(added == 0
            ? "Those games were already listed."
            : $"Added {added} game(s) from {path}.");

        RefreshList();
        SelectByPath(found[0].Path);
    }

    /// <summary>
    /// Shows the folders the user added, and lets them be removed. Adding a folder with no way
    /// to see or undo it is a one-way door.
    /// </summary>
    private async Task ManageFoldersAsync()
    {
        var folders = _inventory.Folders;
        var layout = new StackPanel { Spacing = 10 };

        layout.Children.Add(new TextBlock
        {
            Text = "Steam, Epic and GOG are found on their own. These are the extra folders you " +
                   "asked us to look in.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
        });

        var list = new StackPanel { Spacing = 6 };
        var toRemove = new List<string>();

        if (folders.All.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "None yet. Use “Add a folder…” for games installed outside a launcher.",
                Opacity = 0.5,
                FontSize = 12,
            });
        }

        foreach (var folder in folders.All)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

            var missing = folders.IsMissing(folder);
            var label = new TextBlock
            {
                Text = missing ? $"{folder}   (not found right now)" : folder,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                // A missing folder is flagged, never dropped on its own: an unplugged drive is
                // not a decision to forget what the user asked us to remember.
                Opacity = missing ? 0.55 : 1,
            };
            Grid.SetColumn(label, 0);

            var remove = new Button { Content = "Remove", FontSize = 11 };
            var captured = folder;
            remove.Click += (_, _) =>
            {
                toRemove.Add(captured);
                remove.IsEnabled = false;
                label.Opacity = 0.35;
                label.TextDecorations = TextDecorations.Strikethrough;
            };
            Grid.SetColumn(remove, 1);

            row.Children.Add(label);
            row.Children.Add(remove);
            list.Children.Add(row);
        }

        layout.Children.Add(list);

        if (!await ConfirmAsync("Folders you added", layout, "Apply")) return;
        if (toRemove.Count == 0) return;

        foreach (var folder in toRemove) folders.Remove(folder);
        await ScanAsync();
    }

    private void RefreshList()
    {
        var filter = SearchBox.Text ?? "";
        var previous = _selected?.Path;

        _rows.Clear();

        var items = _games
            .Where(g => filter.Length == 0 || g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(MatchesFilter)
            .Select(game =>
            {
                var item = BuildListItem(game);
                _rows[game.Path] = (Signature(game.Path), item);
                return item;
            })
            .ToList();

        GameList.ItemsSource = items;

        // Restoring the selection is bookkeeping, not a choice the player made. Left unguarded it
        // raised SelectionChanged, which rebuilt the whole card on the right — fifty-three times
        // during the opening sweep, which is what made it flash.
        _restoringSelection = true;
        if (previous is not null) SelectByPath(previous);
        _restoringSelection = false;
    }

    /// <summary>
    /// Watches for games opening and closing, while this window is open and no longer.
    ///
    /// ⚠ A clock, which this project avoids on principle — and here there is nothing else. Windows
    /// will tell you when a process YOU HOLD ends, but there is no cheap way to be told that some
    /// process started, and "some process" is the half that matters: someone launches a game and
    /// the buttons must go grey without them wondering why they failed.
    ///
    /// So it is a clock made cheap enough to be uninteresting. Measured on a real machine: 20 ms
    /// for 764 processes and 38 games, because the sweep needs no handle to read a name and only
    /// opens one for a process whose name could be a game (see RunningGames). Four seconds between
    /// looks is slower than a person can notice and far below anything a machine would feel.
    ///
    /// Off the interface thread, because 20 ms is small and not zero, and a list that stutters
    /// every four seconds would be a worse bargain than the badge is worth.
    /// </summary>
    private void WatchForRunningGames()
    {
        _runningClock?.Stop();

        _runningClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _runningClock.Tick += async (_, _) => await OnClockTickAsync();
        _runningClock.Start();

        // Coming back to the window is the one moment the answer is certainly stale — somebody was
        // away, and away is where games get started and stopped. Asked once here rather than
        // waited for, so what they see on returning is already right.
        Activated -= OnActivated;
        Activated += OnActivated;
    }

    private async void OnActivated(object? sender, EventArgs e) => await LookForRunningGamesAsync();

    /// <summary>
    /// One beat, two questions, each asked as often as it is worth asking.
    ///
    /// Which games are open: every beat, because a name comparison over the process list is nearly
    /// free. Whether a played game's file has moved: every third, because it cannot usefully change
    /// faster than the mod saves it.
    /// </summary>
    private async Task OnClockTickAsync()
    {
        _clockTicks++;

        await LookForRunningGamesAsync();

        if (_clockTicks % TicksBetweenFileChecks != 0) return;
        if (WindowState == WindowState.Minimized) return;

        await FollowGamesBeingPlayedAsync();
    }

    private async Task LookForRunningGamesAsync()
    {
        if (_games.Count == 0) return;

        // ⚠ Minimised, not unfocused — and the difference was got wrong first. Focus seemed like
        // the right cursor and is not: a window loses it the moment somebody types anywhere else,
        // while still being read. On two screens, or side by side, the badge would have sat there
        // stale on a window in plain view.
        //
        // Minimised is the state where there is genuinely nothing to read, and it is the state a
        // full-screen game puts this window into. The rest of the time it costs a third of a per
        // cent of one core, measured — which is not a price worth being wrong for.
        if (WindowState == WindowState.Minimized) return;

        var games = _games.ToList();
        var sweep = await Task.Run(() => RunningGames.Sweep(games));

        if (!sweep.Differs(_running)) return;

        var was = _running;
        _running = sweep;

        // The tag comes and goes with the games themselves: offered while something is running,
        // gone when nothing is. A filter that can only return nothing reads as "you have none"
        // rather than "there are none right now".
        if ((was.Paths.Count == 0) != (sweep.Paths.Count == 0))
        {
            // Nothing running and that lens selected would leave an empty list and no way to see it
            // was a filter doing it. Same treatment as "Mine" when somebody signs out.
            if (_lens == Lens.Running && sweep.Paths.Count == 0) _lens = Lens.All;

            BuildFilterBar();
        }

        // ⚠ Membership moves under this one lens, and only under it. Everywhere else a sweep changes
        // what we KNOW about a game, never whether it belongs in the list — which is why rows are
        // otherwise updated in place rather than rebuilt.
        if (_lens == Lens.Running)
        {
            RefreshList();
        }
        else
        {
            // Only the rows whose answer changed, and only their contents — an item belongs to the
            // list that holds it, and handing one back through a new source is what brought the
            // window down once already.
            foreach (var (path, entry) in _rows.ToList())
            {
                if (entry.Item.Tag is not GameInstall game) continue;
                if (sweep.IsRunning(game) == was.IsRunning(game)) continue;

                entry.Item.Content = BuildRowContent(game);
            }
        }

        // The card carries buttons whose enabled state is exactly this question, so it is redrawn
        // when the game it is about has started or stopped — and left alone otherwise, since
        // rebuilding it would throw away a loader picked in a dropdown.
        if (_selected is not null && sweep.IsRunning(_selected) != was.IsRunning(_selected))
            await ShowSelectedAsync();

        // A game that has just been closed gets one last look, whatever its file's date says: the
        // mod writes on the way out, and this is the moment somebody turns back to this window
        // expecting to see what their session produced. Then it is left alone — see the two
        // paragraphs on FollowGamesBeingPlayedAsync for why "left alone" is the whole design.
        foreach (var game in _games)
        {
            if (!was.IsRunning(game) || sweep.IsRunning(game)) continue;

            await RereadAsync(game);

            // Off the watch list: at rest, nothing writes to that file, so there is nothing to
            // notice. Forgetting the date as well means the next session starts by recording where
            // the file stands rather than by reacting to a change that happened while nobody
            // was watching.
            _watchedStamps.Remove(game.Path);
        }
    }

    /// <summary>
    /// Keeps up with a game while it is being played, and stops the moment it is not.
    ///
    /// Three states, and the point of the design is that only one of them costs anything:
    ///
    /// · **being played** — the mod is writing to the translation file as lines are captured, so
    ///   what this window says about that game is going stale while somebody watches it;
    /// · **just closed** — one last read, dealt with by the caller, because the mod saves on the
    ///   way out and that is the moment somebody comes back to look;
    /// · **at rest** — nothing at all. Nothing is writing to those files, so re-reading them would
    ///   be work whose answer is known in advance.
    ///
    /// ⚠ What makes ten seconds free is that the question asked is NOT "what does the file say" but
    /// "has the file changed". Measured on a real translation of 1679 KB: reading the modification
    /// date costs 0.13 ms, reading and parsing the file costs 19.3 ms — a hundred and fifty times
    /// more. So the date is what gets polled, and the file is opened only when the date has moved,
    /// which the mod does at most once every thirty seconds (its own save is debounced).
    ///
    /// 🔸 On the worry about wearing disks out: these are reads, and it is writes that wear flash.
    /// The operating system also holds a file this size in its cache, so repeated reads mostly never
    /// reach the disk at all. The date-first design makes the question moot either way, which is
    /// better than having to be right about it.
    /// </summary>
    private async Task FollowGamesBeingPlayedAsync()
    {
        if (_running.Paths.Count == 0) return;

        foreach (var game in _games.Where(_running.IsRunning).ToList())
        {
            var stamp = await Task.Run(() => TranslationFileStamp(game));

            if (_watchedStamps.TryGetValue(game.Path, out var seen) && seen == stamp) continue;

            _watchedStamps[game.Path] = stamp;

            // The first sighting only records where the file stood; there is nothing new to show.
            if (seen == default) continue;

            await RereadAsync(game);
        }
    }

    /// <summary>
    /// Re-reads one game and puts what changed on screen — its row, and its card when it is the
    /// one open.
    ///
    /// The disk work happens off the interface thread: nineteen milliseconds is not much and it is
    /// not nothing, and a list that hitches while somebody plays would be a poor trade for a line
    /// count that could have waited a moment.
    /// </summary>
    private async Task RereadAsync(GameInstall game)
    {
        var before = _situations.TryGetValue(game.Path, out var was) ? was : null;
        var (now, mine) = await Task.Run(() => ReadSituation(game));

        _situations[game.Path] = now;
        if (mine) _mine.Add(game.Path); else _mine.Remove(game.Path);
        _watchedStamps[game.Path] = TranslationFileStamp(game);

        // Nothing said differently means nothing to redraw. A game can save its file without any of
        // it reaching this window — a setting changed in the mod, say.
        if (before is not null && before.Headline == now.Headline && before.Detail == now.Detail)
            return;

        if (_rows.TryGetValue(game.Path, out var row) && row.Item.Tag is GameInstall shown)
        {
            row.Item.Content = BuildRowContent(shown);
            _rows[game.Path] = (Signature(game.Path), row.Item);
        }

        if (_selected is not null && _selected.Path == game.Path) await ShowSelectedAsync();
    }

    /// <summary>
    /// When this game's translation file was last written, or default when there is none.
    ///
    /// Cheap on purpose: this is the question asked every ten seconds, and it must stay a look at a
    /// directory entry rather than a read of a file.
    /// </summary>
    private DateTime TranslationFileStamp(GameInstall game)
    {
        try
        {
            var detected = LoaderProbe.Detect(game.Path, _catalog);
            var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == detected?.Id);
            if (descriptor is null) return default;

            var path = System.IO.Path.Combine(game.Path,
                descriptor.UserDataDir.Replace('/', System.IO.Path.DirectorySeparatorChar),
                "translations.json");

            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        }
        catch
        {
            return default;
        }
    }

    /// <summary>What a row is currently saying, so a change can be noticed without comparing controls.</summary>
    private string Signature(string path) =>
        _situations.TryGetValue(path, out var situation) ? situation.ToString() : "";

    /// <summary>
    /// Updates what the rows say, without touching the list that holds them.
    ///
    /// The community sweep answers one game at a time. Rebuilding the whole list on each answer
    /// threw every row away dozens of times in a few seconds, and a control replaced under the
    /// pointer loses its hover — that was the blinking cursor. Handing the same rows back instead
    /// was worse: an item belongs to the list that holds it, and reusing one across a new source
    /// left it with a stale parent, which brought the window down silently.
    ///
    /// So the rows stay exactly where they are and only their contents change. Membership cannot
    /// move here: a sweep changes what we know about a game, never whether it exists.
    /// </summary>
    private void RefreshRowContents()
    {
        foreach (var (path, entry) in _rows.ToList())
        {
            var signature = Signature(path);
            if (signature == entry.Signature) continue;
            if (entry.Item.Tag is not GameInstall game) continue;

            entry.Item.Content = BuildRowContent(game);
            _rows[path] = (signature, entry.Item);
        }
    }

    /// <summary>
    /// Filters ask about FACTS, not about the situation label.
    ///
    /// Tying them to the situation was wrong and hid most of the answer: "playable in my
    /// language" only matched games that were not installed yet, so a game already set up with a
    /// French translation disappeared from the very filter meant to find it. What the reader
    /// means is "a translation exists in my language", whatever else is going on.
    /// </summary>
    private bool MatchesFilter(GameInstall game)
    {
        if (_lens == Lens.All) return true;

        var language = _settings.ResolveTargetLanguage();
        var online = _online.Peek(game);
        var inLanguage = online?.Any(t => Languages.Matches(t.TargetLanguage, language)) == true;

        return _lens switch
        {
            Lens.Blocked => !game.IsModdable,

            // A state of this minute rather than of the game, which is why it is the one lens whose
            // membership changes on its own — see LookForRunningGamesAsync.
            Lens.Running => _running.IsRunning(game),
            Lens.Playable => game.IsModdable && inLanguage,
            Lens.NeedsTranslator => game.IsModdable && online is not null && !inLanguage,
            Lens.Ready => game.IsModdable && IsSetUp(game),

            // Where this account leads a translation or contributes to one. A fact about the
            // person, which is why it needed a lens of its own rather than a game state.
            Lens.Mine => _mine.Contains(game.Path),

            _ => true,
        };
    }

    private bool IsSetUp(GameInstall game) =>
        _situations.TryGetValue(game.Path, out var situation)
        && situation.Situation is Situation.Ready
            or Situation.UpdateAvailable
            or Situation.UnpublishedWork;

    /// <summary>
    /// A row states a situation and offers a verb, in the player's terms.
    ///
    /// The technical facts (runtime, Unity version, architecture) are not here on purpose: they
    /// answer "what is this" while someone scanning this list is asking "what can I do". They
    /// live in the card on the right, where they serve diagnosis.
    /// </summary>
    private ListBoxItem BuildListItem(GameInstall game) =>
        new() { Tag = game, Content = BuildRowContent(game) };

    /// <summary>
    /// What a row shows, separate from the row itself.
    ///
    /// Split apart so the sweep can replace what a row SAYS without replacing the row: a
    /// ListBoxItem belongs to the list that holds it, and handing the same instance back through a
    /// new ItemsSource leaves it with a stale visual parent — the virtualising panel then fails to
    /// anchor it and the window goes down without a word. Measured, not deduced: that was this
    /// morning's crash.
    /// </summary>
    private Control BuildRowContent(GameInstall game)
    {
        var title = new TextBlock
        {
            Text = game.Name,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("TextPrimary"),
        };

        var body = new StackPanel { Spacing = 3, Children = { title } };

        // Said first, because it changes what every other line on this row is worth: a game that is
        // open cannot be set up or removed until it is closed, whatever its situation says.
        if (_running.IsRunning(game))
        {
            body.Children.Add(new TextBlock
            {
                Text = "Running now",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("StatusWarning"),
            });
        }

        if (_situations.TryGetValue(game.Path, out var situation))
        {
            var headline = new TextBlock
            {
                Text = situation.Headline,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = situation.StatusKey is { } key ? Brush(key) : Brush("TextSecondary"),
            };
            body.Children.Add(headline);

            if (situation.Detail is { Length: > 0 } detail)
            {
                body.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("TextMuted"),
                });
            }
        }

        // The game's own icon, when it has one. Purely to make a library look like a library:
        // a column of names reads as a system tool, and these people are looking for THEIR games.
        //
        // The row keeps its exact shape when there is no icon — nothing is reserved, nothing is
        // stood in for. A placeholder repeated down the list would be noise pretending to be
        // information, and on Linux there is never an icon at all.
        if (GameIcons.For(game.ExecutablePath) is { } icon)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
            };

            row.Children.Add(new Image
            {
                Source = icon,
                Width = 28,
                Height = 28,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Avalonia.Thickness(0, 2, 0, 0),
            });

            // The text column takes what is left, so a long name still wraps and trims as before
            // instead of pushing the icon out of view.
            body.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            row.Children.Add(body);

            return row;
        }

        return body;
    }

    private void SelectByPath(string path)
    {
        if (GameList.ItemsSource is not IEnumerable<ListBoxItem> items) return;

        foreach (var item in items)
        {
            if (item.Tag is GameInstall game
                && string.Equals(game.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                GameList.SelectedItem = item;
                return;
            }
        }
    }

    // ---------------------------------------------------------------- detail

    /// <summary>
    /// What fills the right-hand side before anything is chosen.
    ///
    /// It used to hold "Select a game on the left." on an otherwise empty panel, which says
    /// nothing about a scan that has just read a whole machine — and reads as though the tool were
    /// waiting rather than finished. Selecting a game automatically was the other option and it is
    /// worse: picking one for somebody implies it is the one that matters, and we have no idea
    /// which of their games they came for.
    ///
    /// So it answers the questions the scan can actually answer, and says what to do next.
    /// </summary>
    private void ShowOverview()
    {
        if (_selected is not null) return;

        DetailPanel.Children.Clear();

        // Centred, and only here. The panel is the scroll viewer's own content, so aligning it is
        // enough — nothing needs to be wrapped. A dozen short lines pinned to the top left of a
        // wide empty panel look like a page that failed to load; the same lines in the middle look
        // like an answer. A game's report goes straight back to filling the panel from the top,
        // where a long document belongs.
        DetailPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        DetailPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        DetailPanel.MaxWidth = SummaryWidth;

        BuildOverviewTop();

        var language = Languages.NameOf(_settings.ResolveTargetLanguage());
        var moddable = _games.Count(g => g.IsModdable);
        var setUp = _games.Count(g => g.IsModdable && IsSetUp(g));

        var playable = _games.Count(g => g.IsModdable
            && _online.Peek(g)?.Any(t => Languages.Matches(t.TargetLanguage,
                                                           _settings.ResolveTargetLanguage())) == true);

        DetailPanel.Children.Add(new TextBlock
        {
            Text = $"{_games.Count} Unity games on this machine",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        var facts = new List<string>
        {
            $"{moddable} can take the mod.",
            setUp > 0
                ? $"{setUp} already have it."
                : "None of them has it yet.",
            playable > 0
                ? $"{playable} already have a translation in {language} waiting on the community site."
                : $"None has a published translation in {language} yet — which is where you would come in.",
        };

        if (_mine.Count > 0)
            facts.Add($"{_mine.Count} carry a translation you take part in.");

        foreach (var fact in facts)
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = fact,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = Brush("TextSecondary"),
                Margin = new Avalonia.Thickness(0, 2, 0, 0),
            });
        }

        DetailPanel.Children.Add(new TextBlock
        {
            Text = "Pick a game on the left to see what it needs, what the community has for it, "
                 + "and to set it up. The tags above the list narrow it down.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.75,
            Foreground = Brush("TextMuted"),
            Margin = new Avalonia.Thickness(0, 14, 0, 0),
        });
    }

    /// <summary>
    /// The summary's width. A dozen short lines centred read as an answer; the same lines
    /// stretched across a wide panel read as a page that failed to lay itself out.
    ///
    /// It applies to the summary alone. What sits in the row above takes the panel's full width,
    /// which is why it lives outside this panel rather than inside it.
    /// </summary>
    private const double SummaryWidth = 460;

    /// <summary>
    /// The strip pinned above the overview: the offer to stay, and where this program keeps its
    /// own files.
    ///
    /// Both belong at the top and neither belongs in the summary. The summary answers "what is on
    /// this machine"; these two answer "what about this program", which is a different question and
    /// a quieter one. Pinned in their own row they span the panel and align with the content below,
    /// where centred with the summary they would drift into the middle and read as the main event.
    ///
    /// Cleared and rebuilt on every overview rather than toggled: the offer disappears the moment
    /// the tool is installed, and a strip that remembers a state that has changed is worse than one
    /// that is redrawn.
    /// </summary>
    private void BuildOverviewTop()
    {
        OverviewTop.Children.Clear();

        // Ordered by how much each one is asking of the person, and the last is not asking at all:
        // where the tool lives, then what goes into the games, then an invitation, then a plain
        // fact about a folder. ⚠ The middle two never appear together — see WhatGoesIntoGames.
        if (PortableBanner() is { } portable) OverviewTop.Children.Add(portable);
        if (WhatGoesIntoGames() is { } defaults) OverviewTop.Children.Add(defaults);

        OverviewTop.Children.Add(DataFolderRow());

        OverviewTop.IsVisible = true;
    }

    /// <summary>
    /// The one thing to say about what will be written into the games — never two.
    ///
    /// Two states, and they are the same person at two moments, so only one is ever shown:
    ///
    /// · nobody has been through the defaults yet, so every game would be set up with whatever the
    ///   program guessed. That is worth saying once, and it goes away for good the moment somebody
    ///   applies anything;
    /// · they have been through it and chose community translations only. That is a perfectly good
    ///   answer, and it stays — which is why the second one is an invitation and not a warning.
    ///
    /// ⚠ The invitation says what becomes possible, not what somebody ought to do. Whether anyone
    /// can translate at all depends on having a machine that can run a model, or the patience to
    /// find a free API key, and neither is a thing to lean on people about: someone who plays with
    /// what the community has published is using this exactly as intended.
    /// </summary>
    private Control? WhatGoesIntoGames()
    {
        var settings = _settings.Current;

        if (!settings.Reviewed)
        {
            return Banner(
                "Nothing has been decided about what goes into your games yet",
                "Mod defaults holds the language, how lines get translated, and the in-game "
                + "shortcut. Until you have been through it once, each game is set up with what "
                + "this program guessed.",
                "Open Mod defaults",
                async () => await OpenSettingsAsync());
        }

        if (settings.TranslationBackend != "none") return null;

        return Banner(
            "You are playing with what the community has published",
            "Which is the whole point of it, and enough on its own. If you ever want to go the "
            + "other way, a game with no translation in your language can be started by anyone — "
            + "the mod captures the lines as you play, and you decide what to do with them. It "
            + "needs no AI and no account to begin.",
            "See what people share",
            () => { OpenUrl(BuildInfo.WebsiteBaseUrl); return Task.CompletedTask; });
    }

    /// <summary>
    /// The row's "be the first" said again, on the card of the game it was said about.
    ///
    /// The list can only afford a few words, and a few words are easy to read past. Opening the
    /// game is the moment somebody is actually considering it, so that is where the sentence has
    /// room to say what taking it up would mean — and it is the same invitation, not a second one.
    ///
    /// ⚠ Only when the game can take the mod. Telling somebody to be the first to translate a game
    /// this tool has just refused to touch would be an invitation to nothing, and the card already
    /// says why it was refused.
    ///
    /// 🔸 Both routes are named, and by hand comes first. "Be the first" with no idea of what that
    /// involves is a slogan; naming the two ways in is what makes it a proposition somebody can
    /// weigh. By hand first because it needs nothing at all — the mod edits captured lines in the
    /// game itself — where the AI route needs a machine that can run a model. Someone whose machine
    /// cannot should not read the harder path as the only path.
    ///
    /// Still no button. The way to take it up is the one this card already offers further down, and
    /// the AI is set up in a screen of its own, which the sentence names so it can be found.
    /// </summary>
    private Control? BeTheFirstBanner(GameReport report)
    {
        if (!report.Game.IsModdable) return null;

        var language = _settings.ResolveTargetLanguage();

        if (report.OnlineTranslations.Any(t => Languages.Matches(t.TargetLanguage, language)))
            return null;

        // Somebody already translating this game is not being invited to start it.
        if (report.LocalTranslation is { EntryCount: > 0 }) return null;

        var name = Languages.NameOf(language);

        var text = new StackPanel { Spacing = 2 };

        text.Children.Add(new TextBlock
        {
            Text = $"Nobody has published a {name} translation of this game — you could be first",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
        });

        text.Children.Add(new TextBlock
        {
            Text = "Set the mod up and play: it collects the lines the game shows you as it shows "
                 + "them. From there it is your choice — write the translations yourself in the "
                 + "game, line by line, or set up a local AI under Mod defaults and let it take a "
                 + "first pass you can correct. Either way the file stays on your machine until "
                 + "you decide to share it.",
            FontSize = 11,
            Foreground = Brush("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
        });

        // The card's own shape rather than the strip's: it is a block on this game's page, among
        // the others, and a box with different padding sitting between two cards reads as something
        // that arrived from somewhere else. No button either — the way to take it up is the one the
        // card already offers further down, and a second path to the same act invites the question
        // of how the two differ.
        return Card(text);
    }

    /// <summary>One shape for every notice in the strip, so none of them drifts from the others.</summary>
    private Control Banner(string title, string body, string action, Func<Task> onClick)
    {
        var text = new StackPanel { Spacing = 2 };

        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
        });

        text.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 11,
            Foreground = Brush("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
        });

        var button = new Button
        {
            Content = action,
            FontSize = 12,
            Classes = { "primary" },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(14, 0, 0, 0),
        };

        button.Click += async (_, _) => await onClick();

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(button, 1);
        row.Children.Add(text);
        row.Children.Add(button);

        return OverviewBox(row);
    }

    /// <summary>
    /// Where this program keeps what it remembers, and a way straight there.
    ///
    /// Practical and, more to the point, plain: a program that writes to a folder of its own should
    /// say which one without being asked. It is in the settings as well, which is where someone
    /// goes looking on purpose — this is for everyone else, who never opens settings and would
    /// otherwise have no idea there is a folder at all.
    ///
    /// ⚠ Boxed like the offer above it, and that was a correction. Left bare it was the lighter of
    /// the two by design — a standing note should not carry the weight of a question waiting for an
    /// answer — but two blocks stacked in the same strip, one framed and one not, read as one
    /// finished thing and one that was not got round to. The difference in weight is carried by the
    /// text instead: muted, smaller, no heading.
    /// </summary>
    private Control DataFolderRow()
    {
        var folder = _platform.UserDataDirectory;

        var text = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        text.Children.Add(new TextBlock
        {
            Text = "Everything this program remembers is in one folder — your settings, the folders "
                 + "you added, and the translations it moved aside before replacing one.",
            FontSize = 11,
            Foreground = Brush("TextMuted"),
            TextWrapping = TextWrapping.Wrap,
        });

        text.Children.Add(new TextBlock
        {
            Text = folder,
            FontSize = 11,
            Foreground = Brush("TextMuted"),
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });

        var open = new Button
        {
            Content = "Open this folder",
            FontSize = 11,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(14, 0, 0, 0),
        };

        open.Click += (_, _) => Shell.OpenFolder(folder);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(open, 1);
        row.Children.Add(text);
        row.Children.Add(open);

        return OverviewBox(row);
    }

    /// <summary>
    /// The frame both blocks of the top strip share.
    ///
    /// One helper rather than the same six properties written twice: they sit one above the other,
    /// so any drift between them is visible at a glance — which is exactly the kind of difference
    /// nobody notices while writing it and everybody notices on screen.
    /// </summary>
    private Control OverviewBox(Control child) => new Border
    {
        Background = Brush("SurfaceCard"),
        BorderBrush = Brush("BorderSubtle"),
        BorderThickness = new Avalonia.Thickness(1),
        CornerRadius = new Avalonia.CornerRadius(8),
        Padding = new Avalonia.Thickness(14, 12),
        Child = child,
    };

    /// <summary>
    /// The offer to stay, at the top of the overview, whenever this copy is not installed.
    ///
    /// Standing rather than one-off, and here rather than anywhere else. A banner that appears once
    /// and never again is a banner most people meet at the worst possible moment — the first
    /// launch, before they know whether they are keeping the tool at all. Sitting above the summary
    /// it is seen every time the overview is, costs one line, and waits.
    ///
    /// It is not a dialog and it does not block: the answer to "not now" is to go on reading the
    /// page it sits on. Which is also why the way back to the overview had to exist first — an
    /// invitation you can only see before your first click is not an invitation.
    /// </summary>
    private Control? PortableBanner()
    {
        var installer = new SelfInstaller(_platform);

        // Running the installed copy: there is nothing to say, and saying it anyway is how a notice
        // becomes wallpaper.
        if (installer.RunningTheInstalledCopy()) return null;

        // Installed, but this is not that copy. Silence here was the gap: someone who keeps a
        // downloaded file around opens it out of habit, changes settings in it, updates it — and
        // every one of those lands on the copy they are about to close rather than on the one in
        // their menu. The only place that was ever said was inside the settings window.
        if (installer.Installed() is { } installed) return OtherCopyBanner(installer, installed);

        var plan = installer.Plan();
        if (plan.Refusal is not null) return null;

        var text = new StackPanel { Spacing = 2 };

        // ⚠ The product is named here rather than called "this tool". The banner sits among a list
        // of games, above figures about games, in a window whose heading is "Your Unity games" —
        // read cold, "this tool" could as easily mean the mod that goes into them.
        //
        // 🔸 The title carries the offer and the body carries the situation, rather than the other
        // way round. That is not only a matter of register: the title has to survive on one line at
        // the NARROWEST the window is allowed to be, where the banner has about 480 pixels beside
        // its button — and the sentence describing the situation does not fit in that, while the
        // offer does with room to spare. A title that wraps to three lines turns a standing notice
        // into a paragraph of what looks like bad news.
        text.Children.Add(new TextBlock
        {
            Text = "Install UnityGameTranslator Manager on this machine?",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
        });

        text.Children.Add(new TextBlock
        {
            Text = "You are running the file you downloaded — the program that sets your games up, "
                 + "not the mod that goes into them. Kept here it lands in your menu, with a proper "
                 + "way to remove it. Nothing in your games changes either way.",
            FontSize = 11,
            Foreground = Brush("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
        });

        // ⚠ "Keep it here" read both ways — as "install it" and as "leave it where it is, portable"
        // — and the second reading is the one somebody had, on a banner whose whole purpose is the
        // first. A word everybody already knows beats a gentler one that can mean its own opposite;
        // what "install" involves here is listed in full before anything is written.
        var keep = new Button
        {
            Content = "Install it",
            FontSize = 12,
            Classes = { "primary" },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(14, 0, 0, 0),
        };

        keep.Click += async (_, _) => await OfferSelfInstallAsync(installer, plan);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(keep, 1);
        row.Children.Add(text);
        row.Children.Add(keep);

        return OverviewBox(row);
    }

    /// <summary>
    /// What to say when this is a loose copy and another one is installed.
    ///
    /// Which of the two is newer decides the verb, and nothing else does:
    ///
    /// · the installed copy is as new or newer — there is no reason to be in this one at all, so
    ///   the offer is to go across to it;
    /// · this copy is newer — someone has downloaded a new version and is running it out of the
    ///   folder. Sending them to the older installed copy would be sending them backwards, so the
    ///   offer is to put this build over it, keeping the shortcut they already have.
    ///
    /// Both versions are named either way. "You are running another copy" without the two numbers
    /// leaves the only useful question — which one is the good one — unanswered.
    /// </summary>
    private Control OtherCopyBanner(SelfInstaller installer, ToolInstallation installed)
    {
        // ⚠ Before anything about versions: is that installation still whole? Files copied back into
        // the folder by hand look installed to a receipt and are not — no shortcut, no entry in the
        // system's list. Offering to switch to it would send somebody into a copy nothing can find
        // or remove again.
        var state = installer.Inspect();
        if (state.NeedsRepair) return RepairBanner(installer, installed, state);

        var running = SelfUpdater.CurrentVersion;
        var newer = Versions.Compare(running, installed.Version) > 0;

        // A build that cannot install itself cannot update one either; going across still can.
        var canUpdate = newer && installer.Plan().Refusal is null;

        var text = new StackPanel { Spacing = 2 };

        text.Children.Add(new TextBlock
        {
            Text = canUpdate
                ? $"This copy is newer than the one installed ({running} against {installed.Version})"
                : "You are running a loose copy, not the installed one",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
        });

        text.Children.Add(new TextBlock
        {
            Text = canUpdate
                ? $"UnityGameTranslator Manager {installed.Version} is installed in "
                  + $"{installed.Directory}. Putting this build over it keeps the shortcut you "
                  + "already have, and your settings are shared by both either way."
                : $"UnityGameTranslator Manager {installed.Version} is installed in "
                  + $"{installed.Directory}, and this window is version {running} running from "
                  + "somewhere else. Settings are shared, but an update applied here lands on this "
                  + "file rather than on the copy in your menu.",
            FontSize = 11,
            Foreground = Brush("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
        });

        var action = new Button
        {
            Content = canUpdate ? "Update the installed copy" : "Open the installed copy",
            FontSize = 12,
            Classes = { "primary" },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(14, 0, 0, 0),
        };

        action.Click += async (_, _) =>
        {
            if (!canUpdate) { SwitchTo(installed); return; }

            action.IsEnabled = false;

            try
            {
                var updated = installer.UpdateInstalled();

                // Offered rather than done: they are still in the loose copy, and the point of
                // updating the installed one is to end up in it.
                var across = await ConfirmationWindow.AskAsync(this,
                    $"Open the copy you just updated to {updated.Version}?",
                    $"It is in {updated.Directory}, and it is what your shortcut points at. This "
                    + "window is the file you downloaded; it is left exactly where it is.",
                    "Open it");

                if (across) SwitchTo(updated); else ShowOverview();
            }
            catch (Exception ex)
            {
                Status(ex.Message);
                action.IsEnabled = true;
            }
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(action, 1);
        row.Children.Add(text);
        row.Children.Add(action);

        return OverviewBox(row);
    }

    /// <summary>
    /// An installation with pieces missing, and the one word that fits: repair.
    ///
    /// What is missing is listed rather than summarised. "Something is wrong with your
    /// installation" is the kind of sentence that leaves somebody unable to tell whether it matters
    /// — three files or one shortcut are very different situations, and only they can judge which
    /// of the two they are in.
    ///
    /// Repairing opens the same window as installing, which lists everything that will be written
    /// and lets the shortcuts be ticked again. Nothing else would be honest: putting the missing
    /// pieces back silently is still writing to somebody's machine.
    /// </summary>
    private Control RepairBanner(SelfInstaller installer, ToolInstallation installed,
                                 SelfInstallationState state)
    {
        var text = new StackPanel { Spacing = 2 };

        text.Children.Add(new TextBlock
        {
            Text = state.Missing.Count == 1
                ? "The installed copy is missing a piece"
                : $"The installed copy is missing {state.Missing.Count} pieces",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
        });

        text.Children.Add(new TextBlock
        {
            Text = $"{installed.Directory} — missing: {string.Join(", ", state.Missing)}. "
                 + "Until it is put back, that copy may not start, and the system may have no way "
                 + "to remove it.",
            FontSize = 11,
            Foreground = Brush("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
        });

        var repair = new Button
        {
            Content = "Repair it",
            FontSize = 12,
            Classes = { "primary" },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(14, 0, 0, 0),
        };

        repair.Click += async (_, _) => await OfferSelfInstallAsync(installer, installer.Plan());

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(repair, 1);
        row.Children.Add(text);
        row.Children.Add(repair);

        return OverviewBox(row);
    }

    /// <summary>
    /// Starts the installed copy and stands down.
    ///
    /// Ours closes rather than lingering: two windows would both be holding the same settings file,
    /// and the one instance lock means the new one would meet ours and close again — which reads as
    /// a tool that refuses to open.
    /// </summary>
    private void SwitchTo(ToolInstallation installed)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installed.Executable)
            {
                UseShellExecute = true,
                WorkingDirectory = installed.Directory,
            });

            Close();
        }
        catch (Exception ex)
        {
            // It could not be started. The window in front of them still works, so this is a line
            // in the status bar rather than a dialog.
            Status($"Could not start {installed.Executable}: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the removal window as soon as there is a window to open it over — the path taken when
    /// the system's own uninstall button started us.
    ///
    /// Queued on Loaded rather than called straight away: a dialog needs a shown owner, and asking
    /// for one during construction is how a tool ends up with a modal nobody can reach.
    /// </summary>
    public void OpenRemovalWhenReady() => Loaded += async (_, _) =>
    {
        var window = new SelfRemoveWindow(_platform, new SelfInstaller(_platform));
        await window.ShowDialog(this);

        // Removed means the files are gone, including quite possibly the one we are running from.
        // Staying open would be a window belonging to a program that no longer exists.
        if (window.Removed) Close();
    };

    private async Task OfferSelfInstallAsync(SelfInstaller installer, SelfInstallPlan plan)
    {
        var window = new SelfInstallWindow(_platform, installer, plan);
        await window.ShowDialog(this);

        if (window.Installed is not { } installed) return;

        // The copy in front of them is still the downloaded one, and an update applied here would
        // land on the file they are about to stop using. So the offer to move across is made now,
        // while the reason is obvious, rather than left as a difference nobody notices.
        var switchOver = await ConfirmationWindow.AskAsync(this,
            "Open the copy you just installed?",
            $"It is now in {installed.Directory}. This window is still the file you downloaded — "
            + "switching over means updates and settings apply to the copy that stays. The "
            + "downloaded file is left where it is; you can delete it whenever you like.",
            "Open it");

        if (!switchOver)
        {
            ShowOverview();
            return;
        }

        SwitchTo(installed);
    }

    /// <summary>
    /// The way back out of a game's card.
    ///
    /// There was none: picking a game replaced the overview, and nothing put it back — the summary
    /// of the whole machine could be read exactly once, before the first click of the session.
    /// Clearing the selection is the honest gesture rather than a second "Overview" button
    /// somewhere: the card is on screen BECAUSE a game is selected on the left, so leaving it means
    /// no game is selected, and the list shows that.
    /// </summary>
    private Control BackToOverview()
    {
        var back = new Button
        {
            Content = "← All games",
            FontSize = 12,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Margin = new Avalonia.Thickness(0, 0, 0, 2),
        };

        ToolTip.SetTip(back, "Back to the summary of every game found (Esc)");
        back.Click += (_, _) => CloseCard();

        return back;
    }

    private void CloseCard()
    {
        if (_selected is null) return;

        _selected = null;

        // Guarded like every other selection we set ourselves: unguarded it would raise
        // SelectionChanged and rebuild a card for a game nobody chose.
        _restoringSelection = true;
        GameList.SelectedItem = null;
        _restoringSelection = false;

        ShowOverview();
    }

    private async Task ShowSelectedAsync()
    {
        if (GameList.SelectedItem is not ListBoxItem { Tag: GameInstall game }) return;
        _selected = game;

        DetailPanel.Children.Clear();
        DetailPanel.Children.Add(new TextBlock { Text = game.Name, FontSize = 20, FontWeight = FontWeight.SemiBold });
        DetailPanel.Children.Add(new TextBlock { Text = "Reading...", Opacity = 0.6 });

        Busy(true, $"Reading {game.Name}...");

        // One call for the whole library, and only the first selection pays for it. A failure is
        // recorded rather than raised: not knowing one's role costs a line on a card, and must
        // never stand between someone and installing the mod.
        await _lineages.EnsureAsync(_settings.Current.ApiToken);

        var report = await _inventory.BuildReportAsync(game);
        Busy(false, "Ready.");

        // The user may have clicked elsewhere while we were reading.
        if (!ReferenceEquals(_selected, game)) return;

        RenderReport(report);
    }

    private void RenderReport(GameReport report)
    {
        var game = report.Game;
        DetailPanel.Children.Clear();

        // Back to filling the panel from the top: a report is a document, and a centred document
        // that grows past the viewport starts scrolled to its middle.
        DetailPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        DetailPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        DetailPanel.MaxWidth = double.PositiveInfinity;

        // The strip above belongs to the overview: it answers questions about this program, and a
        // game's card is not the place to be asked them. Its row collapses, so the card gets the
        // height back rather than keeping an empty band.
        OverviewTop.IsVisible = false;

        DetailPanel.Children.Add(BackToOverview());
        DetailPanel.Children.Add(Header(report));

        if (BeTheFirstBanner(report) is { } invitation) DetailPanel.Children.Add(invitation);

        DetailPanel.Children.Add(Card(Facts(report)));

        foreach (var blocker in report.Blockers)
            DetailPanel.Children.Add(Callout(blocker, "CalloutErrorBg", "StatusError"));

        foreach (var warning in report.Warnings)
            DetailPanel.Children.Add(Callout(warning, "CalloutWarningBg", "StatusWarning"));

        DetailPanel.Children.Add(Card(Translations(report)));
        DetailPanel.Children.Add(Card(Actions(report)));
    }

    /// <summary>
    /// The game's name, where it lives, and its own icon.
    ///
    /// The icon is sized to the two lines beside it rather than to a round number: a picture that
    /// matches the height of the text it belongs to reads as one block, while any other size reads
    /// as two things that happen to be adjacent. Pushed to the right so the eye still starts on
    /// the name, which is what the panel is about.
    ///
    /// Folders get a button each, and only the ones that exist: a game always has one, the mod has
    /// its own once installed, and MelonLoader keeps its data somewhere else again — Mods against
    /// UserData/UnityGameTranslator, where BepInEx uses a single folder for both. Showing an
    /// identical path twice would suggest a distinction that is not there.
    /// </summary>
    private Control Header(GameReport report)
    {
        var game = report.Game;

        var text = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

        // The name and, when it applies, the one word that changes what the rest of this card can
        // do. In the title line rather than under it, and in the colour the row and the note below
        // the buttons already use — three places saying the same thing in the same colour is one
        // fact, where three different treatments would read as three.
        //
        // Inline so it wraps with the name: a long title on a narrow window would otherwise push
        // the word out of view, which is the case where it is most needed.
        var title = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
        };

        title.Inlines?.Add(new Avalonia.Controls.Documents.Run(game.Name));

        if (_running.IsRunning(game))
        {
            title.Inlines?.Add(new Avalonia.Controls.Documents.Run("  (running)")
            {
                Foreground = Brush("StatusWarning"),
            });
        }

        text.Children.Add(title);

        text.Children.Add(FolderRow(game.Path, "the game"));

        // The mod's folders, once there is a mod. Resolved from the catalog because a detected
        // loader knows where its plugins go, not where the mod keeps its own files.
        var loaderId = report.InstalledLoader?.Id;
        var descriptor = loaderId is null
            ? null
            : _catalog.Loaders.FirstOrDefault(l => l.Id == loaderId);

        if (descriptor is not null)
        {
            var pluginDir = System.IO.Path.Combine(game.Path,
                descriptor.PluginDir.Replace('/', System.IO.Path.DirectorySeparatorChar));

            var dataDir = System.IO.Path.Combine(game.Path,
                descriptor.UserDataDir.Replace('/', System.IO.Path.DirectorySeparatorChar));

            if (System.IO.Directory.Exists(pluginDir)) text.Children.Add(FolderRow(pluginDir, "the mod"));

            // Only when it is genuinely another place.
            if (!string.Equals(pluginDir, dataDir, StringComparison.OrdinalIgnoreCase)
                && System.IO.Directory.Exists(dataDir))
            {
                text.Children.Add(FolderRow(dataDir, "its settings and translation"));
            }
        }

        // ⚠ The icon's column is a FIXED width, not Auto, and that is what makes the rest safe.
        //
        // Sizing the icon to the text is circular by nature: a wider icon leaves the text less
        // room, the text wraps, the block grows taller, the icon grows again. Reserving the
        // largest width up front means the text always has the same space to lay itself out in,
        // whatever the icon ends up being — so it never wraps because of the picture.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"*,{IconColumnWidth}"),
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
        };

        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        if (GameIcons.For(game.ExecutablePath) is { } icon)
        {
            var image = new Image
            {
                Source = icon,
                Width = MinIconSize,
                Height = MinIconSize,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Avalonia.Thickness(12, 2, 0, 0),
            };

            // Followed rather than computed: the height of the text is only known once it has been
            // laid out, and it changes with the window's width and with how many folders this game
            // turns out to have.
            text.PropertyChanged += (_, e) =>
            {
                if (e.Property != Visual.BoundsProperty) return;

                var height = Math.Clamp(text.Bounds.Height, MinIconSize, MaxIconSize);
                image.Width = height;
                image.Height = height;
            };

            Grid.SetColumn(image, 1);
            grid.Children.Add(image);
        }

        return grid;
    }

    /// <summary>
    /// Never smaller than the title line — an icon shorter than the name it belongs to reads as an
    /// afterthought — and never taller than four lines, which is the most folders a game can show.
    /// Beyond that it would be a picture with a caption rather than a game with an icon.
    /// </summary>
    private const double MinIconSize = 30;
    private const double MaxIconSize = 78;

    /// <summary>Reserved for the icon whatever its size, so the text's width never moves.</summary>
    private const double IconColumnWidth = MaxIconSize + 12;

    /// <summary>A path, with a way to go there. The button is the only thing added.</summary>
    private static Control FolderRow(string path, string what)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var open = new Button
        {
            Content = Glyphs.Folder(),
            Padding = new Avalonia.Thickness(4, 1),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        ToolTip.SetTip(open, $"Open the folder of {what}");
        open.Click += (_, _) => OpenFolder(path);

        row.Children.Add(open);
        row.Children.Add(new TextBlock
        {
            Text = path,
            FontSize = 11,
            Foreground = Brush("TextMuted"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });

        return row;
    }

    /// <summary>
    /// Opens a folder in whatever the system uses for that.
    ///
    /// UseShellExecute on the path itself: Windows opens Explorer, Linux hands it to the desktop's
    /// own handler, macOS to Finder. Spawning explorer.exe by name would work on one system only.
    /// </summary>
    private static void OpenFolder(string path) => Shell.OpenFolder(path);

    private static Control Facts(GameReport report)
    {
        var game = report.Game;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), Margin = new Avalonia.Thickness(0, 6, 0, 0) };

        var rows = new List<(string Label, string Value)>
        {
            ("Type", game.Runtime switch
            {
                UnityRuntime.Mono => "Mono",
                UnityRuntime.Il2Cpp => "IL2CPP",
                _ => "could not be determined",
            }),
            ("Unity", game.UnityVersion ?? "unknown"),
            ("Mod loader", report.InstalledLoader is null
                ? "none installed"
                : $"{report.InstalledLoader.Display} {report.InstalledLoader.Version ?? ""}".Trim()),
            ("Plugin", report.InstalledPluginVersion ?? "not installed"),
        };

        if (report.RecommendationReason is not null)
            rows.Add(("What we would do", report.RecommendationReason));

        if (game.RunsUnderProton)
            rows.Add(("Proton", "yes — a Steam launch option is required"));

        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var label = new TextBlock { Text = rows[i].Label, Foreground = Brush("TextMuted"), FontSize = 12, Margin = new Avalonia.Thickness(0, 3, 12, 3) };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);

            var value = new TextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("TextSecondary"), Margin = new Avalonia.Thickness(0, 3, 0, 3) };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);

            grid.Children.Add(label);
            grid.Children.Add(value);
        }

        return grid;
    }

    /// <summary>
    /// Where the signed-in account stands in the lineage of the file installed here.
    ///
    /// Two positions and no more: whoever publishes leads it — a Main — and whoever contributes
    /// privately writes a branch that the Main reviews. It matters to a manager because the two
    /// have opposite next moves: a Main has contributions waiting on them, a branch is waiting on
    /// somebody else. The mod says the same thing in game, from the same server answer, and the
    /// colours match it — green for the Main, amber for a branch — so the two never seem to
    /// disagree about the same file.
    ///
    /// Silent for a translation this account has no part in, which is the ordinary case: a player
    /// who downloaded somebody's work owes no explanation for it. Silent, too, while the answer is
    /// unread or the account signed out — the alternative would be a guess printed as a fact.
    /// </summary>
    private IEnumerable<Control> LineageNotes(GameReport report)
    {
        if (report.MyPosition is not { } position) yield break;

        if (position.IsMain)
        {
            var waiting = position.BranchesCount ?? 0;

            yield return new TextBlock
            {
                Text = position.Describe(),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusSuccess"),
                Margin = new Avalonia.Thickness(0, 2, 0, 0),
            };

            // Reviewing happens on the site — merging a contribution means reading both files side
            // by side, which is a screen, not a line on a card. Offered only when there is
            // something to review: a button that leads to an empty page is worse than no button.
            if (waiting > 0)
            {
                var review = Glyphs.Button(Glyphs.Site(), "Review them on the site");
                review.Margin = new Avalonia.Thickness(0, 6, 0, 0);
                review.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
                review.Click += (_, _) =>
                    OpenUrl($"{BuildInfo.WebsiteBaseUrl}/translations/{position.Uuid}/merge");

                yield return review;
            }

            yield break;
        }

        // A branch. The Main's name is not in the account's own listing — but the published entry
        // of this very lineage IS the Main, and the community search already carries who published
        // it. Read from there rather than asked for again.
        yield return new TextBlock
        {
            Text = position.Describe(report.MatchingOnline?.Author),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("StatusWarning"),
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
        };

        // Only when the server said so. Null means the site is older than the field, and an
        // installer that read silence as "the Main is fine" would reassure people wrongly.
        if (position.MainMissing == true)
        {
            yield return new TextBlock
            {
                Text = LineagePosition.OrphanNote,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
                Opacity = 0.9,
                Margin = new Avalonia.Thickness(0, 2, 0, 0),
            };
        }
    }

    private Control Translations(GameReport report)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Translations", FontWeight = FontWeight.SemiBold, Foreground = Brush("TextPrimary") });

        if (report.LocalTranslation is { } local)
        {
            var count = local.EntryCount < 0 ? "unreadable file" : $"{local.EntryCount} entries";
            var unsynced = local.LocalChanges > 0 ? $", {local.LocalChanges} not uploaded yet" : "";

            // The pair comes first, as it does on every community entry below. Without it the two
            // lines invite a comparison they do not support: a local file and a published one can
            // share a target and differ on the source, and the reader had no way to see it.
            var loaderId = report.InstalledLoader?.Id ?? report.RecommendedLoader?.Id;
            var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == loaderId);
            var languages = descriptor is null
                ? null
                : LocalTranslationProbe.DescribeLanguages(report.Game.Path, descriptor);

            var prefix = languages is null ? "" : $"{languages}, ";

            panel.Children.Add(new TextBlock { Text = $"On this machine: {prefix}{count}{unsynced}", FontSize = 12, Foreground = Brush("TextSecondary") });
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = "On this machine: none", FontSize = 12, Foreground = Brush("TextSecondary") });
        }

        if (report.MatchingOnline is { } mine)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"You already have this one: {mine}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusSuccess"),
            });
        }

        foreach (var line in LineageNotes(report)) panel.Children.Add(line);

        // One button rather than a list of names: choosing between translations needs what they
        // are made of, who reviewed them and which language they came FROM — none of which fits
        // on a line here, and all of which decides the choice.
        var offered = report.OnlineTranslations.Count;
        if (offered > 0)
        {
            var browse = new Button
            {
                Content = offered == 1 ? "See the translation" : $"See the {offered} translations",
                FontSize = 12,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            browse.Click += async (_, _) => await OpenTranslationsAsync(report);
            panel.Children.Add(browse);
        }

        // Counted over everything published, not over the alternatives. Excluding the one already
        // installed made the card announce "none in French" to the very person who wrote the only
        // French one and published it — the count contradicted the line above it.
        var published = report.OnlineTranslations;

        if (published.Count > 0)
        {
            // A count by language, not a list of names.
            //
            // This section used to print up to eight entries. It was written when it was the only
            // place a translation could be seen; now a button opens a screen with filters and full
            // cards, and eight raw lines both duplicate it and fail at it — on a game with two
            // hundred translations they are eight arbitrary ones, taking the room without
            // answering the question the card is for: is there something here for ME.
            //
            // Not a silent truncation either: every figure below is the real total.
            panel.Children.Add(new TextBlock
            {
                Text = SummariseLanguages(published, _settings.ResolveTargetLanguage()),
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            });
        }
        else if (report.OnlineSearchError is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Could not reach the community site ({report.OnlineSearchError}). " +
                       "A firewall, an antivirus or a company proxy blocking UnityGameTranslator " +
                       "Installer looks exactly like this. Nothing was lost.",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });

            // A dead end with no way out is the thing this tool must never be: someone who lets
            // the firewall prompt through, or fills in their proxy, has to be able to carry on
            // from here rather than start the whole thing again.
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            };

            var retry = new Button { Content = "Try again", FontSize = 12 };
            retry.Click += async (_, _) =>
            {
                retry.IsEnabled = false;
                retry.Content = "Trying...";
                await RetryOnlineAsync();
            };

            var network = new Button { Content = "Network settings", FontSize = 12 };
            network.Click += async (_, _) => await OpenSettingsAsync();

            actions.Children.Add(retry);
            actions.Children.Add(network);
            panel.Children.Add(actions);
        }
        else if (report.Game.SteamAppId is null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Nobody has published a translation for this game yet — the mod builds one as "
                     + "you play, and you can be the first to share it.",
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return panel;
    }

    private Control Actions(GameReport report)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(0, 16, 0, 0) };
        var engine = new InstallEngine(_platform, _catalog);

        // The recommendation is a default, not a decision made for the user: some games work
        // with one loader and not another for reasons no probe can see.
        ComboBox? loaderPicker = null;

        if (report.InstalledLoader is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Using the {report.InstalledLoader.Display} already installed. " +
                       "It will not be replaced — other mods may depend on it.",
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (report.EligibleLoaders.Count > 0)
        {
            loaderPicker = new ComboBox { Width = 260 };
            foreach (var loader in report.EligibleLoaders)
            {
                loaderPicker.Items.Add(new ComboBoxItem
                {
                    Content = loader == report.RecommendedLoader
                        ? $"{loader.Display} {loader.Version}  (recommended)"
                        : $"{loader.Display} {loader.Version}",
                    Tag = loader,
                });
            }
            loaderPicker.SelectedIndex = Math.Max(0,
                report.EligibleLoaders.ToList().IndexOf(report.RecommendedLoader!));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock
            {
                Text = "Mod loader",
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.55,
                FontSize = 12,
            });
            row.Children.Add(loaderPicker);
            panel.Children.Add(row);
        }

        // A refusal we are willing to let the user overrule gets a way forward. A dead button and
        // a red paragraph, with nothing to click, is the same dead end as refusing forever.
        if (!report.Game.IsModdable
            && ModdabilityProbe.CanBeOverridden(report.Game.Verdict))
        {
            var tryAnyway = new Button { Content = "Let me try anyway...", FontSize = 12 };
            tryAnyway.Click += async (_, _) => await OverrideVerdictAsync(report);
            panel.Children.Add(tryAnyway);
            return panel;
        }

        // A decision one can make but not unmake is a trap. Overruling a refusal is reversible
        // by design, so the way back has to be as reachable as the way in.
        if (report.Game.VerdictOverridden)
        {
            var reconsider = new Button { Content = "Treat as not possible again", FontSize = 12 };
            reconsider.Click += async (_, _) => await ClearOverrideAsync(report);
            panel.Children.Add(reconsider);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        LoaderDescriptor? Chosen() =>
            (loaderPicker?.SelectedItem as ComboBoxItem)?.Tag as LoaderDescriptor;

        // Reviewed settings only: before someone has opened the settings screen we know nothing
        // about their language, and writing our defaults into their game would be deciding for
        // them. Unreviewed, the mod's own first-run wizard asks instead — which is correct.
        var plan = engine.Plan(report, ReleaseChannel.Stable, Chosen(),
            _settings.Current.Reviewed ? _settings.Current : null);
        var installed = report.InstalledPluginVersion is not null;

        // ⚠ Writing into a folder the game is holding open fails, and it fails halfway: some files
        // replaced, some refused. The engines already check this at the moment they run, which is
        // the check that must exist — but a button that cannot work should not look like one that
        // can. Greyed out here, with the reason above them, so nobody spends a click finding out.
        var running = _running.IsRunning(report.Game);

        var primary = new Button
        {
            Content = installed ? "Reinstall / update" : "Install",
            IsEnabled = plan is not null && !running,
            Classes = { "primary" },
        };
        primary.Click += async (_, _) =>
            await RunInstallAsync(report, engine, engine.Plan(report, ReleaseChannel.Stable, Chosen(),
                _settings.Current.Reviewed ? _settings.Current : null));
        buttons.Children.Add(primary);

        var uninstall = new Button
        {
            Content = "Uninstall...",
            IsEnabled = ReceiptStore.Read(report.Game.Path) is not null && !running,
        };
        uninstall.Click += async (_, _) => await RunUninstallAsync(report);
        buttons.Children.Add(uninstall);

        panel.Children.Add(buttons);

        if (running)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "The game is running. Close it and this comes back on its own.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
            });
        }

        if (plan is null && report.Blockers.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = report.RecommendationReason ?? "Nothing can be installed here.",
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return panel;
    }

    // ---------------------------------------------------------------- actions

    /// <summary>
    /// Lets the user overrule a refusal, after showing exactly what they are overruling.
    ///
    /// Offered only for refusals Core considers reversible: everything installed is recorded and
    /// can be removed, so a loader that turns out not to work costs time. An anti-cheat never
    /// reaches here — that cost is a banned account, and no uninstall undoes it.
    /// </summary>
    /// <summary>
    /// Puts a game back to refused. Warns first when something is still installed: the override
    /// is what allowed that install, and taking it away while the files are still there would
    /// leave a game the tool then declines to manage.
    /// </summary>
    private async Task ClearOverrideAsync(GameReport report)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = "This game will be listed as not possible again, and the tool will stop " +
                   "offering to install into it.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        var stillInstalled = ReceiptStore.Read(report.Game.Path) is not null;
        if (stillInstalled)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Something is still installed here. Uninstall it first, otherwise the " +
                       "files stay behind and the tool will no longer offer to remove them.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
            });
        }

        if (!await ConfirmAsync($"Treat {report.Game.Name} as not possible?", body, "Confirm"))
            return;

        _inventory.Overrides.Clear(report.Game.Path);

        // Re-read the game from the files, so the verdict comes back from detection rather than
        // from a stale in-memory object that still believes it was overruled.
        ModdabilityProbe.Evaluate(report.Game);
        report.Game.VerdictOverridden = false;
        report.Game.OverriddenVerdict = null;

        RecomputeSituations();
        RefreshList();
        await ShowSelectedAsync();
    }

    private async Task OverrideVerdictAsync(GameReport report)
    {
        var verdict = report.Game.Verdict;

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = ModdabilityProbe.Explain(report.Game),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });
        body.Children.Add(new TextBlock
        {
            Text = ModdabilityProbe.OverrideCaveat(verdict),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("StatusWarning"),
        });

        if (!await ConfirmAsync($"Proceed with {report.Game.Name} anyway?", body, "Let me try"))
            return;

        _inventory.Overrides.Set(report.Game.Path, new GameOverride { IgnoreVerdict = true });
        _inventory.Overrides.Apply(report.Game);

        RecomputeSituations();
        RefreshList();
        await ShowSelectedAsync();
    }

    private async Task RunInstallAsync(GameReport report, InstallEngine engine, InstallPlan? plan)
    {
        if (plan is null) return;

        // Nothing is written before this is shown and accepted. The notes are recomputed for the
        // loader actually chosen, which may not be the recommended one shown in the report.
        var lines = plan.Describe().Select(line => "• " + line).ToList();

        var notes = _inventory.WarningsFor(plan.Loader, report.Game, plan.InstallLoader).ToList();
        if (notes.Count > 0)
        {
            lines.Add("");
            lines.AddRange(notes.Select(note => "! " + note));
        }

        var body = string.Join(Environment.NewLine, lines);
        if (!await ConfirmAsync($"Install into {report.Game.Name}?", body, "Install")) return;

        Busy(true, "Starting...");
        engine.Status += OnEngineStatus;

        var outcome = await engine.ApplyAsync(plan);

        engine.Status -= OnEngineStatus;
        Busy(false, outcome.Success ? "Done." : "Failed.");

        await MessageAsync(outcome.Success ? "Installed" : "Nothing was changed", outcome.Message);
        await ShowSelectedAsync();
    }

    private async Task RunUninstallAsync(GameReport report)
    {
        var engine = new UninstallEngine(_platform, _catalog);
        var available = engine.Available(report.Game);

        var loaderBox = new CheckBox
        {
            Content = "Also remove the mod loader",
            IsEnabled = available.RemoveLoader,
            IsChecked = false,
        };

        if (!available.RemoveLoader)
        {
            ToolTip.SetTip(loaderBox,
                "It was already there before, or other mods still use it.");
        }

        // Off by default, and deliberately worded so nobody deletes months of work by reflex.
        var dataBox = new CheckBox
        {
            Content = "Also remove my settings and translations (a copy is kept aside)",
            IsChecked = false,
        };

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "The plugin will be removed. Files you changed since installing are left alone.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(loaderBox);
        content.Children.Add(dataBox);

        if (!await ConfirmAsync($"Uninstall from {report.Game.Name}?", content, "Uninstall")) return;

        Busy(true, "Removing...");
        var outcome = engine.Apply(report.Game, new UninstallChoice(
            RemovePlugin: true,
            RemoveLoader: loaderBox.IsChecked == true,
            RemoveUserData: dataBox.IsChecked == true));
        Busy(false, "Ready.");

        var message = outcome.Message;
        if (outcome.Kept.Count > 0)
            message += Environment.NewLine + Environment.NewLine + "Left in place:" +
                       Environment.NewLine + string.Join(Environment.NewLine, outcome.Kept.Select(k => "• " + k));
        if (outcome.BackupPath is not null)
            message += Environment.NewLine + Environment.NewLine +
                       "Your settings and translations were copied to:" + Environment.NewLine + outcome.BackupPath;

        await MessageAsync("Uninstalled", message);
        await ShowSelectedAsync();
    }

    private void OnEngineStatus(string message) =>
        Dispatcher.UIThread.Post(() => Status(message));

    // ---------------------------------------------------------------- chrome

    /// <summary>
    /// ⚠ IsIndeterminate is turned off as well as hidden, and that is not tidiness.
    ///
    /// An indeterminate progress bar is an animation, and an animation whose control is merely
    /// invisible goes on running: its clock keeps ticking, the layout keeps being invalidated, and
    /// the window keeps redrawing for something nobody can see. Measured on a window sitting idle
    /// with nothing happening — eighteen per cent of a core, for a four-pixel stripe that was not
    /// on screen. This is a program somebody is invited to leave open.
    /// </summary>
    private void Busy(bool busy, string message)
    {
        BusyBar.IsVisible = busy;
        BusyBar.IsIndeterminate = busy;
        Status(message);
    }

    private void Status(string message) => StatusText.Text = message;

    private Task<bool> ConfirmAsync(string title, string body, string confirmLabel) =>
        ConfirmAsync(title, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap }, confirmLabel);

    /// <summary>
    /// A modal confirmation. Written by hand rather than pulled from a dialog package: one
    /// window type is not worth a dependency that would also have to be kept current.
    /// </summary>
    private async Task<bool> ConfirmAsync(string title, Control body, string confirmLabel)
    {
        var result = false;

        var confirm = new Button { Content = confirmLabel, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, confirm },
        };

        var layout = new StackPanel { Spacing = 16, Margin = new Avalonia.Thickness(20) };
        layout.Children.Add(body);
        layout.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = layout },
        };

        confirm.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task MessageAsync(string title, string body)
    {
        var ok = new Button { Content = "Close", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };

        var layout = new StackPanel { Spacing = 16, Margin = new Avalonia.Thickness(20) };
        layout.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        layout.Children.Add(ok);

        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = layout },
        };

        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// The site's card, reproduced: gray-800 fill, gray-700 edge, 8px radius, generous padding.
    /// Framing each section is what turns a wall of lines into things you can look at one at a
    /// time — which is the whole reason the site uses them.
    /// </summary>
    private static Control Card(Control content) => new Border
    {
        Background = Brush("SurfaceCard"),
        BorderBrush = Brush("BorderSubtle"),
        BorderThickness = new Avalonia.Thickness(1),
        CornerRadius = new Avalonia.CornerRadius(8),
        Padding = new Avalonia.Thickness(18, 15),
        Child = content,
    };

    /// <summary>
    /// Looks a brush up in the shared palette (Theme.axaml), through Palette — which will not let
    /// an unknown key pass unnoticed.
    /// </summary>
    private static IBrush? Brush(string key) => Palette.Of(key);

    /// <summary>
    /// A message that needs to stand out, tinted rather than shouted: the hue laid over the base
    /// surface, with a coloured edge. A flat saturated block would fight the rest of the window.
    /// </summary>
    private static Control Callout(string text, string backgroundKey, string edgeKey) => new Border
    {
        Background = Brush(backgroundKey),
        BorderBrush = Brush(edgeKey),
        BorderThickness = new Avalonia.Thickness(3, 0, 0, 0),
        CornerRadius = new Avalonia.CornerRadius(4),
        Padding = new Avalonia.Thickness(12, 9),
        Child = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("TextPrimary"),
        },
    };
}
