using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UnityGameTranslator.Manager.Core.Api;
using UnityGameTranslator.Manager.Core.Catalog;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Settings;
using UnityGameTranslator.Manager.Core.Update;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Gui;

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

    /// <summary>
    /// The newest published plugin build, looked up once and compared against every game.
    ///
    /// Held by the window rather than by the inventory it feeds, because the inventory is rebuilt
    /// on every scan and this answer is about the internet, not about this machine's drives.
    /// </summary>
    private readonly PluginReleases _releases = new();

    /// <summary>
    /// What was decided for each game: apply the defaults here, start translating here, what this
    /// game is about, which translation was picked. Loaded once — it is a small file, and every
    /// card rendering reads from it.
    /// </summary>
    private GamePreferences _preferences = null!;

    /// <summary>
    /// The loader the picker is currently on, for the card being shown.
    ///
    /// A function rather than a value: the picker can be changed after the card is drawn, and
    /// everything that installs has to act on what it says at the moment of the click. Reset on
    /// every render, since it closes over controls belonging to that one rendering.
    /// </summary>
    private Func<LoaderDescriptor?> _chosenLoader = () => null;

    /// <summary>
    /// Whether the one-click should also bring a translation down, for the card being shown.
    ///
    /// ⚠ Held here rather than read from the preferences on every draw, and the difference matters.
    /// The stored answer is honoured only where nothing is at stake (see
    /// <see cref="TranslationOffers.MayDefaultToYes"/>); on a game carrying unpublished work the
    /// box starts unticked whatever was stored, because that "yes" was given about a game in
    /// another state. Once somebody ticks it here, it has to STAY ticked — recomputing the safe
    /// default on the redraw their own click causes would untick it under their hand.
    ///
    /// Set in <see cref="RenderReport"/> and nowhere else.
    /// </summary>
    private bool _takeTranslation;

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
    /// The site account each game is signed in with, by game path. Absent means signed in with
    /// none, which is the ordinary case.
    ///
    /// It is a fact about the GAME, not about this tool: somebody signs in from inside the mod,
    /// per game, and with twenty games installed there is otherwise no way to know which of them
    /// can publish. ⚠ Only the name is ever held — see LocalTranslationProbe.ReadSiteAccount.
    /// </summary>
    private readonly Dictionary<string, string> _accounts =
        new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// The window opens as large as this screen comfortably allows, rather than at a size someone
    /// guessed once.
    ///
    /// ⚠ A fixed default cannot be right for everybody, and picking a bigger number only moves who
    /// it is wrong for. The figures that matter are not the ones a hardware survey reports: those
    /// are PHYSICAL pixels, while everything here is measured in LOGICAL ones. A 1920x1080 screen
    /// at 150% scaling is 1280x720 to us — around 670 once the taskbar is out — so the person who
    /// shows up in the statistics as comfortably full-HD is the one a taller window would push off
    /// their own screen, with the bottom of the panel and its buttons simply unreachable.
    ///
    /// Below the comfortable size there are real machines, not rounding errors: about one Steam
    /// user in thirty has a screen 800 logical pixels tall or less (1366x768 alone is 2.5%), and
    /// the Steam Deck's 1280x800 is one of them — a platform this tool targets.
    ///
    /// So: ask the screen, take most of its working area, and never go under the minimum the
    /// layout needs. A large screen stops showing scrollbars for content that would have fitted;
    /// a small one keeps a window it can actually display.
    /// </summary>
    private void FitToScreen()
    {
        // Comfortable rather than maximal: a window that fills the screen edge to edge reads as
        // one that took over the machine, and leaves nowhere to grab it.
        const double comfortableWidth = 1220;
        const double comfortableHeight = 940;
        const double margin = 0.92;

        var screen = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
        if (screen is null) return;   // No screen to ask: the XAML defaults stand.

        // WorkingArea excludes taskbars and panels, and comes in physical pixels; Scaling turns it
        // into the units Width and Height are expressed in.
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var availableWidth = screen.WorkingArea.Width / scaling;
        var availableHeight = screen.WorkingArea.Height / scaling;

        Width = Math.Max(MinWidth, Math.Min(comfortableWidth, availableWidth * margin));
        Height = Math.Max(MinHeight, Math.Min(comfortableHeight, availableHeight * margin));
    }

    public MainWindow()
    {
        // InitializeComponent is generated by the Avalonia XAML compiler, and it is what wires
        // up the x:Name fields. Declaring one by hand hides it, leaving every named control
        // null — which fails at construction, not at build.
        InitializeComponent();

        FitToScreen();

        _platform = PlatformFactory.Create();

        SearchBox.TextChanged += (_, _) => RefreshList();
        RescanButton.Click += async (_, _) => await ScanAsync();
        FoldersButton.Click += async (_, _) => await OpenFoldersAsync();
        SettingsButton.Click += async (_, _) => await OpenSettingsAsync();
        ToolSettingsButton.Click += async (_, _) => await OpenToolSettingsAsync();
        AboutButton.Click += async (_, _) => await new AboutWindow().ShowDialog(this);

        AdornToolbar();

        // ⚠ Nothing to manage until the machine has been read once: the folder list is read by the
        // inventory, and the inventory does not exist until the first scan has run. Pressing this
        // during the opening sweep — several seconds, with the window fully up — went straight into
        // a null reference. Disabled rather than guarded silently, so the button says why it cannot
        // be pressed instead of doing nothing when it is. ScanAsync turns it on.
        FoldersButton.IsEnabled = false;
        ToolTip.SetTip(FoldersButton, "Reading this machine first...");
        GameList.SelectionChanged += async (_, _) =>
        {
            if (_restoringSelection) return;

            // ⚠ Back to Home on every game. The tab is a place in ONE card, not a preference about
            // the tool: carried across a click in the list, it would drop somebody into the
            // machinery of a game they have not looked at yet.
            _gameTab = GameTab.Home;

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
        _preferences = new GamePreferences(_platform);
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

    /// <summary>
    /// Puts a mark on each toolbar button.
    ///
    /// Here rather than in the XAML because the paths live in Glyphs, and a second copy of a mark
    /// written in markup is a copy free to drift from the first. It also keeps the markup readable:
    /// a row of buttons still reads as a row of buttons with their labels on.
    ///
    /// ⚠ The row has to keep fitting at the minimum window width. It does because "Add a folder..."
    /// left it for the window it belongs to — five marks cost about what that one button did. Adding
    /// a seventh control here means measuring again, not assuming.
    /// </summary>
    /// <summary>Written once: the button says this again once the first scan has run.</summary>
    private const string FoldersTip = "Places to look for games beyond Steam, Epic and GOG";

    private void AdornToolbar()
    {
        Glyphs.Adorn(FoldersButton, Glyphs.Folder());
        Glyphs.Adorn(RescanButton, Glyphs.Refresh());
        Glyphs.Adorn(SettingsButton, Glyphs.Sliders());
        Glyphs.Adorn(ToolSettingsButton, Glyphs.Gear());
        Glyphs.Adorn(AboutButton, Glyphs.Info());

        // Said once here rather than in five places: which of the two settings windows a button
        // opens is the one thing about this row people get wrong.
        ToolTip.SetTip(FoldersButton, FoldersTip);
        ToolTip.SetTip(RescanButton, "Look for games again");
        ToolTip.SetTip(SettingsButton, "What gets written into your games");
        ToolTip.SetTip(ToolSettingsButton, "Settings for this program itself");
        ToolTip.SetTip(AboutButton, "About UnityGameTranslator Manager");
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
        // Asked once and shared by every report built from here — see PluginReleases. Forgotten
        // first because reaching this method IS the gesture that means "look again": a rescan
        // that re-read the drives and kept yesterday's idea of the newest plugin would be a
        // refresh button that refreshes some things.
        _releases.Forget();

        _inventory = new GameInventory(_platform, _catalog, new CatalogApiClient())
        {
            Lineages = _lineages,
            Releases = _settings.Current.OnlineMode ? _releases : null,
            Channel = string.Equals(_settings.Current.Channel, "beta", StringComparison.OrdinalIgnoreCase)
                ? ReleaseChannel.Beta
                : ReleaseChannel.Stable,
        };

        // There is a folder list to show from here on — see the note where it is switched off.
        FoldersButton.IsEnabled = true;
        ToolTip.SetTip(FoldersButton, FoldersTip);

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
        _accounts.Clear();

        foreach (var game in _games)
        {
            var (situation, mine, account) = ReadSituation(game);
            _situations[game.Path] = situation;
            if (mine) _mine.Add(game.Path);
            if (account is not null) _accounts[game.Path] = account;
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
    private (GameSituationInfo Situation, bool Mine, string? Account) ReadSituation(GameInstall game)
    {
        var language = _settings.ResolveTargetLanguage();
        var online = _online.Peek(game);
        var report = new GameReport { Game = game };
        var mine = false;

        var detected = LoaderProbe.Detect(game.Path, _catalog);
        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == detected?.Id);
        string? account = null;

        if (descriptor is not null)
        {
            report.InstalledPluginVersion =
                LocalTranslationProbe.ReadInstalledPluginVersion(game.Path, descriptor);
            report.LocalTranslation = LocalTranslationProbe.Read(game.Path, descriptor);

            // Read while we are in this game's folder anyway. ⚠ The token is never touched — the
            // mod clears the name along with it, so the name answers the question on its own.
            account = LocalTranslationProbe.ReadSiteAccount(game.Path, descriptor).User;

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

        return (SituationReader.Read(report, language, checkedOnline), mine, account);
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
    private async Task OpenTranslationsAsync(GameReport report, bool anyLanguage = false)
    {
        var loaderId = report.InstalledLoader?.Id ?? report.RecommendedLoader?.Id;
        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == loaderId);

        if (descriptor is null)
        {
            Status("No loader is set up for this game yet, so there is nowhere to put a translation.");
            return;
        }

        var window = new TranslationsWindow(report, descriptor, _settings, _lineages, _preferences,
                                            anyLanguage);
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
        //
        // ⚠ The LANGUAGE leads and the reason follows — "French (system language)", not "System
        // language (French)". Every other line in this list is a language name, so putting the
        // machinery first made the one entry that matters most read as a setting rather than as an
        // answer; and it is the entry most likely to be selected, so it is the one the closed box
        // shows, where the language was pushed past the edge and cut off entirely.
        var detected = Languages.FromLocale(_platform.SystemLanguage());
        var autoLabel = detected is not null
            ? $"{Languages.NameOf(detected)} (system language)"
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

    /// <summary>
    /// The folders this tool was told to look in — added, listed and removed in one window.
    ///
    /// ⚠ Both halves used to be here: a toolbar button that opened a folder picker, and a second
    /// one that opened a list which could only take things away. Adding and managing are the same
    /// subject, so they are one window now, and this method does nothing but open it and act on
    /// what came back.
    ///
    /// The rescan happens once, here, and only when something actually changed — a folder added or
    /// removed changes which games exist, and nothing short of reading the drives again can say
    /// what the list should now contain.
    /// </summary>
    private async Task OpenFoldersAsync()
    {
        var window = new FoldersWindow(_inventory.Folders, _games);
        await window.ShowDialog(this);

        if (!window.Changed) return;

        await ScanAsync();

        // Somebody who pointed us at a folder was pointing at a game. Landing on it is the answer
        // to that; leaving them to find it in a list of eighty is not.
        if (window.FirstGameFound is { } path) SelectByPath(path);
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
        var (now, mine, account) = await Task.Run(() => ReadSituation(game));

        _situations[game.Path] = now;
        if (mine) _mine.Add(game.Path); else _mine.Remove(game.Path);

        // Signing in happens INSIDE the game, so this is one of the few things that can change
        // while somebody plays — which is exactly when this re-read runs.
        if (account is not null) _accounts[game.Path] = account;
        else _accounts.Remove(game.Path);
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

            return WithAccountMark(game, row);
        }

        return WithAccountMark(game, body);
    }

    /// <summary>
    /// Puts the account this game is signed in with in its top-right corner, when there is one.
    ///
    /// ⚠ Shown only when there IS one, and that is the rule the rest of this window follows: badges
    /// work by being rare. Most games are signed in with nothing, so marking every one of them
    /// "not linked" would put a label on the ordinary case and make the meaningful one invisible.
    /// The absence is stated on the game's card instead, where a reader is asking about one game
    /// and silence would be an unanswered question.
    ///
    /// Its own column rather than a line in the text: it belongs to the game, not to the situation,
    /// and it must not push the name or the headline around as it comes and goes.
    /// </summary>
    private Control WithAccountMark(GameInstall game, Control content)
    {
        _accounts.TryGetValue(game.Path, out var account);

        var play = PlayButton(game, small: true);
        if (account is null && play is null) return content;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        Grid.SetColumn(content, 0);
        grid.Children.Add(content);

        // Stacked in the corner: the account says what this game IS, the button is what to do with
        // it. Right-aligned so neither pushes the name around as they come and go.
        var corner = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(8, 1, 0, 0),
        };

        if (account is not null)
        {
            var mark = new TextBlock
            {
                Text = "@" + account,
                FontSize = 10,
                Foreground = Brush("StatusSuccess"),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                MaxWidth = 110,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            ToolTip.SetTip(mark, $"This game is signed in to the site as {account}, so it can publish "
                               + "and contribute from inside the game.");

            corner.Children.Add(mark);
        }

        if (play is not null)
        {
            play.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            corner.Children.Add(play);
        }

        Grid.SetColumn(corner, 1);
        grid.Children.Add(corner);

        return grid;
    }

    /// <summary>
    /// The button that starts the game, or null when there is nothing to start it with.
    ///
    /// ⚠ Absent while the game is already running rather than disabled. The row and the card both
    /// say "Running now" a line away; a second control repeating it in grey is noise, and the one
    /// thing a second press could do — start a second copy — is not something to offer.
    ///
    /// ⚠ The route is Core's decision, not this button's: a Steam title goes through Steam so its
    /// launch options apply, which is where the Proton override this very tool tells people to set
    /// actually lives. See GameLaunch, and the trap it documents about app ids.
    ///
    /// ⚠ Green in BOTH sizes, and that is not decoration. It is one act, so it wears one colour
    /// wherever it appears — and grey is what the rest of this interface means by "cannot be
    /// pressed". A grey play mark on the card, next to green ones in the list, reads as disabled.
    /// </summary>
    private Button? PlayButton(GameInstall game, bool small)
    {
        if (_running.IsRunning(game)) return null;
        if (GameLaunch.RouteFor(game) is not { } route) return null;

        var button = small
            ? new Button
            {
                Content = Glyphs.Play("StatusSuccess"),
                Padding = new Avalonia.Thickness(6, 2),
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            }
            : Glyphs.Button(Glyphs.Play("StatusSuccess"), "Play");

        ToolTip.SetTip(button, $"Start {game.Name}. {route.Why}");

        button.Click += async (_, _) =>
        {
            // Said before it happens: a store that has to wake up first can take several seconds,
            // and a button that appears to do nothing gets pressed again.
            Status($"Starting {game.Name}...");

            if (GameLaunch.Start(route) is { } failure)
            {
                Status("Ready.");
                await MessageAsync($"{game.Name} did not start", failure);
                return;
            }

            // The sweep notices it on its own within a few seconds — this only spares those
            // seconds, so the row a person just pressed stops offering to start it again.
            await LookForRunningGamesAsync();
        };

        return button;
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

        // The band belongs to a game, and there is none here. Its row collapses with it, so the
        // summary gets the height back rather than sitting above an empty strip.
        ActionBar.IsVisible = false;
        ActionBar.Content = null;

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

        // ⚠ Only once somebody has actually looked. An empty list is evidence that nobody has
        // published anything ONLY when a search ran and came back — offline, or with community
        // features switched off, this would tell a player their game is untranslated and send them
        // off to redo work that already exists.
        if (!report.OnlineChecked) return null;

        // ⚠ THE GAME'S language, not the reader's default, and they are routinely different: the
        // target is set per game, and somebody translating one game into a language they do not
        // otherwise play in would be told about the wrong one. Falls back to the default only when
        // the game names none — which, for a game holding a translation, is itself a gap the
        // differences list offers to close.
        var loaderId = report.InstalledLoader?.Id ?? report.RecommendedLoader?.Id;
        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == loaderId);

        var inGame = descriptor is null
            ? null
            : LocalTranslationProbe.ReadTargetLanguage(report.Game.Path, descriptor);

        var name = inGame ?? Languages.NameOf(_settings.ResolveTargetLanguage());

        if (report.OnlineTranslations.Any(t => Languages.Matches(t.TargetLanguage, name)))
            return null;

        var text = new StackPanel { Spacing = 2 };

        // Two propositions, and they are not the same one worded twice. Nothing here yet is an
        // invitation to start; work here that has never left the machine is an invitation to
        // publish — and that second case had no banner at all, because the guard that stopped the
        // first from nagging a translator also silenced the one message they had earned.
        var started = report.LocalTranslation is { EntryCount: > 0 };

        // Published translations of this file would appear as the matching entry. Its absence,
        // once a search has run, is what "never shared" means.
        if (started && report.MatchingOnline is not null) return null;

        text.Children.Add(new TextBlock
        {
            Text = started
                ? $"You have {report.LocalTranslation!.EntryCount} {name} lines nobody else has — "
                  + "you could be the first to publish them"
                : $"Nobody has published a {name} translation of this game — you could be first",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(started ? "StatusSuccess" : "TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
        });

        text.Children.Add(new TextBlock
        {
            Text = started
                // ⚠ Named from the GAME's account, not this tool's. Publishing happens inside the
                // game, with the game's own sign-in — and the two are allowed to differ, which on
                // a shared machine is the ordinary case rather than a mistake.
                ? (report.SiteAccount.User is { Length: > 0 } who
                    ? $"This game is signed in as {who}, so it can publish. Open it, press the "
                      + "mod's hotkey and use its upload panel — sharing is a decision you take "
                      + "there, line by line if you want to review first."
                    : "Sharing them needs an account, and signing in happens inside the game, from "
                      + "the mod's own panel. Until then the file stays on this machine.")
                  + " Nothing leaves this machine until you say so."
                : "Set the mod up and play: it collects the lines the game shows you as it shows "
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
    private Control BackToOverview(GameReport report)
    {
        // A house rather than a close cross, and on the left rather than the right. The card is
        // not a dialog laid over the window — it IS the window's right-hand side — so "close" would
        // name the wrong gesture: what happens is a return to the summary of the whole machine,
        // which is a place, and a place is what a house means. On the left because that is where
        // the way back lives everywhere else people use.
        var back = Glyphs.Button(Glyphs.Home(), "Home");
        back.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;

        ToolTip.SetTip(back, "Back to the summary of every game found (Esc)");
        back.Click += (_, _) => CloseCard();

        // Facing it across the same line: where this game stands with the site.
        //
        // ⚠ Said in BOTH directions here, unlike the list. A reader on one game's card is asking
        // about that game, and silence would leave the question open — where in a list of fifty,
        // marking every unlinked one would bury the few that are.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Avalonia.Thickness(0, 0, 0, 2),
        };

        grid.Children.Add(back);

        var (user, server) = report.SiteAccount;

        var linked = user is { Length: > 0 };

        var mark = new TextBlock
        {
            Text = linked ? $"Signed in to the site as {user}" : "Not signed in to the site",
            FontSize = 11,
            Foreground = Brush(linked ? "StatusSuccess" : "TextMuted"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Avalonia.Thickness(12, 0, 0, 0),
        };

        // ⚠ Named when it is not our own server. Somebody running their own instance would
        // otherwise read "signed in" and assume it is the same place this tool talks to.
        var elsewhere = linked && server is { Length: > 0 }
                        && !server.TrimEnd('/').StartsWith(BuildInfo.ApiBaseUrl.TrimEnd('/'),
                                                           StringComparison.OrdinalIgnoreCase);

        ToolTip.SetTip(mark, linked
            ? (elsewhere ? $"Signed in on {server}, which is not the site UnityGameTranslator "
                         + "Manager is set to." : null)
              ?? "Signed in from inside the game, and remembered per game — one machine can hold "
                 + "several accounts. This is what lets this game publish a translation or "
                 + "contribute to somebody else's."
            : "Not signed in. Signing in happens inside the game, in the mod's own panel. This "
              + "game can still use community translations, but cannot publish or contribute.");

        if (elsewhere) mark.Foreground = Brush("StatusWarning");

        Grid.SetColumn(mark, 1);
        grid.Children.Add(mark);

        return grid;
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

        DetailPanel.Children.Add(BackToOverview(report));
        DetailPanel.Children.Add(Header(report));

        // ⚠ Directly under the name. Placed after the technical card, the tabs sat below a screenful
        // of paths and engine versions — somebody had to scroll to discover the card even had two
        // halves, which is the same as not having them.
        DetailPanel.Children.Add(TabStrip(report));

        if (BeTheFirstBanner(report) is { } invitation) DetailPanel.Children.Add(invitation);

        // A blocker belongs to both halves: "this game cannot be modded" IS the answer somebody
        // came for, and hiding it behind a tab would let Home offer a translation for a game that
        // can never run one.
        foreach (var blocker in report.Blockers)
            DetailPanel.Children.Add(Callout(blocker, "CalloutErrorBg", "StatusError"));

        if (_gameTab == GameTab.Home)
        {
            foreach (var control in GameHome(report)) DetailPanel.Children.Add(control);

            // No fixed bar on Home: it offers one way forward at a time, and a second call to
            // action pinned underneath would compete with the one the page is built around.
            ActionBar.IsVisible = false;
            ActionBar.Content = null;
            return;
        }

        // ⚠ Paths, engine version, architecture: the technical answer, and it opens the SET UP
        // half rather than the card. It was the first thing on every game — before knowing whether
        // a translation even existed — which is the wrong first question for almost everybody.
        DetailPanel.Children.Add(Card(Facts(report)));

        foreach (var warning in report.Warnings)
            DetailPanel.Children.Add(Callout(warning, "CalloutWarningBg", "StatusWarning"));

        // Three cards for three subjects, where there used to be one called "Actions".
        //
        // The loader and the mod are published by different people on different days and are
        // installed by separate steps; folding them into one block meant their versions could not
        // both be shown, and the single button had to pretend they moved together. Each card now
        // carries its own version, its own verb, and nothing that belongs to the other.
        // Settled once per card, before anything reads it. A stored "yes" is deliberately ignored
        // where a translation would replace something — that answer was given about a game in
        // another state, and re-applying it is precisely the mistake this is here to prevent.
        var offer = TranslationOffers.For(report, PickTranslation(report));
        _takeTranslation = TranslationOffers.MayDefaultToYes(offer)
                           && _preferences.Read(report.Game.Path).InstallTranslation;

        DetailPanel.Children.Add(Card(LoaderSection(report)));
        DetailPanel.Children.Add(Card(ModSection(report)));
        DetailPanel.Children.Add(Card(Translations(report)));

        ShowActionBar(report);
    }

    /// <summary>
    /// The answer somebody came for, before any machinery: what this game has, what exists for it,
    /// and the one thing to do next.
    /// </summary>
    private IEnumerable<Control> GameHome(GameReport report)
    {
        var target = _settings.ResolveTargetLanguage();
        var mine = MyTranslationHere(report);

        var inMyLanguage = report.OnlineTranslations
            .Where(t => Languages.Matches(t.TargetLanguage, target))
            .ToList();

        var elsewhere = report.OnlineTranslations.Count - inMyLanguage.Count;

        // ── What this game carries right now ──────────────────────────────────────────────────
        if (report.LocalTranslation is { } local)
        {
            var body = new StackPanel { Spacing = 4 };

            body.Children.Add(new TextBlock
            {
                Text = mine
                    ? "This game is running your own translation."
                    : "This game already has a translation installed.",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextPrimary"),
            });

            var detail = local.EntryCount < 0
                ? "The file could not be read."
                : $"{local.EntryCount} entries"
                  + (local.LocalChanges > 0 ? $", {local.LocalChanges} never uploaded" : "");

            body.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
            });

            yield return Card(body);
        }

        // ── What exists for this game ─────────────────────────────────────────────────────────
        //
        // ⚠ Built on WHAT EXISTS, never on what is missing from one language. The first version
        // announced "no translation exists in <your language>" — which, next to a card saying the
        // game was running one, read as a plain lie. It was worse than wrong: the language it
        // judged against is resolved from the system when set to "auto", so a locale we fail to
        // read turns into a denial about a translation sitting right there.
        //
        // Only one fact here cannot be misread: whether anything is published at all. Everything
        // else is stated as a list of what there IS, per language.
        var question = new StackPanel { Spacing = 4 };

        if (report.OnlineTranslations.Count == 0)
        {
            question.Children.Add(new TextBlock
            {
                Text = "Nothing has been published for this game yet.",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextPrimary"),
            });
        }
        else
        {
            var byLanguage = report.OnlineTranslations
                .GroupBy(t => t.TargetLanguage ?? "unknown")
                .OrderByDescending(g => Languages.Matches(g.Key, target))
                .ThenByDescending(g => g.Count())
                .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ({g.Count()})")
                .ToList();

            question.Children.Add(new TextBlock
            {
                Text = report.OnlineTranslations.Count == 1
                    ? "One translation is published for this game."
                    : $"{report.OnlineTranslations.Count} translations are published for this game.",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextPrimary"),
            });

            // The languages themselves, yours first when there is one. A list somebody can read
            // and judge, rather than a verdict about a language we may have resolved wrongly.
            question.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", byLanguage),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(inMyLanguage.Count > 0 ? "StatusSuccess" : "TextSecondary"),
            });

            if (inMyLanguage.Count == 0 && elsewhere > 0)
            {
                question.Children.Add(new TextBlock
                {
                    Text = $"None of them is in {Languages.NameOf(target)}, the language set in "
                         + "your mod defaults — taking one still works, and the game can be "
                         + "pointed at its language.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("TextMuted"),
                });
            }
        }

        if (report.OnlineTranslations.Count > 0)
        {
            // Two buttons rather than a language picker: the app already has one at the top, and a
            // second would be two places to set the same thing. These open the SAME list, filtered
            // differently — the everyday case first, the wider net beside it.
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            };

            if (inMyLanguage.Count > 0)
            {
                var mineFirst = new Button
                {
                    Content = $"Choose one in {Languages.NameOf(target)}",
                    FontSize = 12,
                    Classes = { "primary" },
                };

                mineFirst.Click += async (_, _) => await OpenTranslationsAsync(report);
                buttons.Children.Add(mineFirst);
            }

            if (elsewhere > 0)
            {
                var anyLanguage = new Button
                {
                    Content = inMyLanguage.Count > 0 ? "See every language" : "See what exists",
                    FontSize = 12,
                    Classes = { inMyLanguage.Count > 0 ? "" : "primary" },
                };

                anyLanguage.Click += async (_, _) => await OpenTranslationsAsync(report, anyLanguage: true);
                buttons.Children.Add(anyLanguage);
            }

            question.Children.Add(buttons);
        }
        else
        {
            question.Children.Add(new TextBlock
            {
                Text = TranslationBackendLabel(_settings.Current) is not null
                    ? "Yours would be the first: play with the mod on and it translates as it "
                      + "meets text, then you can publish it for everyone else."
                    : "You can still make one without any translator: the mod captures the game's "
                      + "text as you play, and its live editor lets you write the lines yourself.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
            });
        }

        yield return Card(question);

        // ── Mine, published, and not the one running here ─────────────────────────────────────
        //
        // The case that had nowhere to appear: a translation of yours sits on the site for this
        // game while the file installed belongs to somebody else's lineage. Both are legitimate —
        // testing another one is ordinary — but nothing said the first still existed, so it looked
        // lost. AccountLineages knows every lineage the account holds; nobody was asking it.
        foreach (var control in MyOtherTranslations(report)) yield return control;

        // ── The one thing to do next ──────────────────────────────────────────────────────────
        var next = new StackPanel { Spacing = 6 };
        var installed = report.InstalledPluginVersion is not null;

        // What Set up would have to do, said here rather than found there. "Up to date" is worth
        // as much as a pending update: it is the answer to "do I need to go and look".
        var pending = new List<string>();
        if (report.InstalledLoader is null) pending.Add("the loader");
        else if (report.LoaderStanding is { UpdateAvailable: true }) pending.Add("a newer loader");

        if (!installed) pending.Add("the mod");
        else if (report.PluginStanding is { UpdateAvailable: true }) pending.Add("a newer mod");

        next.Children.Add(new TextBlock
        {
            Text = pending.Count == 0
                ? "The loader and the mod are installed and up to date."
                : $"Needs {string.Join(" and ", pending)}.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(pending.Count == 0 ? "StatusSuccess" : "TextSecondary"),
        });

        var go = new Button
        {
            Content = installed ? "Manage this game" : "Set this game up",
            FontSize = 12,
            Classes = { "primary" },
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        go.Click += async (_, _) =>
        {
            _gameTab = GameTab.Setup;
            await ShowSelectedAsync();
        };

        next.Children.Add(go);
        yield return Card(next);
    }

    /// <summary>
    /// Translations this account published for THIS game that are not the one installed.
    ///
    /// ⚠ Reads the account's lineages, never infers them. And it is deliberately silent when the
    /// answer is unknown — signed out, offline, or a site too old to report roles — because
    /// "you have none" and "we could not ask" must not look alike on a screen about someone's own
    /// work.
    /// </summary>
    private IEnumerable<Control> MyOtherTranslations(GameReport report)
    {
        var installedUuid = report.LocalTranslation?.Uuid;

        // The account THIS GAME uses, as everywhere else on this card: one machine can carry
        // several, and judging with the tool's own would claim a stranger's work.
        var descriptor = InstalledDescriptor(report);
        var account = GameConfigWriter.InGameValue(
            report.Game.Path, descriptor, GameConfigWriter.ApiUserKey);

        if (string.IsNullOrWhiteSpace(account)) yield break;

        var mine = report.OnlineTranslations
            .Where(t => string.Equals(t.Author, account, StringComparison.OrdinalIgnoreCase))
            .Where(t => !string.Equals(t.Uuid, installedUuid, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mine.Count == 0) yield break;

        var body = new StackPanel { Spacing = 4 };

        body.Children.Add(new TextBlock
        {
            Text = mine.Count == 1
                ? "You have published a translation for this game."
                : $"You have published {mine.Count} translations for this game.",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextPrimary"),
        });

        foreach (var owned in mine)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"{owned.SourceLanguage} → {owned.TargetLanguage} · {owned.LineCount} lines",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = installedUuid is null
                ? "It is not the one installed here — this game has no translation file yet."
                : "The file in this game is a different one. Yours is still on the site, untouched.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        });

        var take = new Button
        {
            Content = mine.Count == 1 ? "Put mine back in this game" : "Choose which one to install",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = !_running.IsRunning(report.Game),
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };

        take.Click += async (_, _) =>
        {
            // More than one: the choice needs what separates them, which is the list's job.
            if (mine.Count > 1)
            {
                await OpenTranslationsAsync(report);
                return;
            }

            var preference = _preferences.Read(report.Game.Path);
            preference.TranslationId = mine[0].Id;
            _preferences.Set(report.Game.Path, preference);

            // Selected, then acted on through the same path as any other translation — including
            // the warnings, because putting mine back still replaces what is there.
            if (report.InstalledPluginVersion is null)
            {
                _gameTab = GameTab.Setup;
                await ShowSelectedAsync();
                return;
            }

            var replacing = TranslationOffers.For(report, mine[0])
                is TranslationOffer.ReplacesWork or TranslationOffer.ReplacesChoice;

            await TakeSelectedTranslationAsync(report, mine[0], replacing);
        };

        body.Children.Add(take);
        yield return Callout(body, "CalloutInfoBg", "StatusInfo");
    }

    /// <summary>
    /// Whether the translation installed here belongs to the account THIS GAME is signed in as.
    ///
    /// ⚠ The game's account, never this tool's. One machine can carry several — one game signed in
    /// as one person, the next as another, a third not signed in at all — and judging with the
    /// installer's own account would claim somebody else's work as yours. The rule AccountLineages
    /// already states: a role is read, never inferred from a game and a language.
    ///
    /// False whenever anything is missing: no file, no lineage, nobody signed in, or nothing
    /// published under that lineage. "Unknown" and "not mine" lead to the same restraint, so they
    /// are answered the same way rather than guessed apart.
    /// </summary>
    private bool MyTranslationHere(GameReport report)
    {
        if (report.LocalTranslation?.Uuid is not { } uuid) return false;

        var descriptor = InstalledDescriptor(report);
        var account = GameConfigWriter.InGameValue(report.Game.Path, descriptor, GameConfigWriter.ApiUserKey);
        if (string.IsNullOrWhiteSpace(account)) return false;

        return report.OnlineTranslations.Any(t =>
            string.Equals(t.Uuid, uuid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Author, account, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Which half of a game's card is showing. Home first, always — see TabStrip.</summary>
    private enum GameTab { Home, Setup }

    private GameTab _gameTab = GameTab.Home;

    /// <summary>
    /// The two tabs, and the only place that switches between them.
    ///
    /// ⚠ Reset to Home on every game, deliberately: the tab is a place in ONE game's card, not a
    /// preference about the tool. Carrying "Set up" across a click in the list would drop somebody
    /// into the machinery of a game they have not yet looked at.
    /// </summary>
    private Control TabStrip(GameReport report)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        foreach (var tab in new[] { GameTab.Home, GameTab.Setup })
        {
            var active = tab == _gameTab;

            var button = new Button
            {
                Content = tab == GameTab.Home ? "This game" : "Set up",
                FontSize = 12,

                // The active tab wears the section colour; the other stays plain. No "quiet"
                // class is invented here — App.axaml has no such style, and naming one that does
                // not exist styles nothing while looking deliberate.
                Classes = { active ? "primary" : "" },
            };

            var chosen = tab;
            button.Click += async (_, _) =>
            {
                if (_gameTab == chosen) return;
                _gameTab = chosen;
                await ShowSelectedAsync();
            };

            strip.Children.Add(button);
        }

        return strip;
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
            //
            // ⚠ Read from what this game IS set to, and shown as "→ auto" only where that is
            // genuinely the answer — which, for a file that exists, it should never be. The pair
            // used to come straight out of config.json, so a game left on the mod's default
            // announced "French → auto" over a translation that plainly had a target: the display
            // was faithful and the configuration was wrong. Both are fixed; this line is what
            // makes the second one visible when it happens again.
            var loaderId = report.InstalledLoader?.Id ?? report.RecommendedLoader?.Id;
            var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == loaderId);

            var pair = descriptor is null
                ? null
                : LocalTranslationProbe.DescribeLanguages(report.Game.Path, descriptor);

            var prefix = pair is null ? "" : $"{pair}, ";

            panel.Children.Add(new TextBlock { Text = $"On this machine: {prefix}{count}{unsynced}", FontSize = 12, Foreground = Brush("TextSecondary") });

            // What the file is actually made of, drawn by the same bar as every community entry.
            //
            // ⚠ Counted from the file on disk, never taken from the published figures of the same
            // lineage: the moment somebody plays, their copy stops being the published one, and
            // borrowing its numbers would describe a stranger's file under the words "on this
            // machine". It is also the only thing that answers "is what I am using any good" —
            // which used to be unanswerable here, on the one screen devoted to it.
            if (local.Counts is { } counts && QualityBar.HasSomethingToShow(counts))
            {
                if (QualityBar.StageOf(counts) is { } stage)
                {
                    // The verdict first, the make-up under it: somebody wants to know where this
                    // stands before they want to know what it is made of.
                    panel.Children.Add(new TextBlock
                    {
                        Text = counts.Completeness is { } done
                            ? $"{stage} · {done * 100:F0}% of what it has met is settled"
                            : stage,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush("TextSecondary"),
                        Margin = new Avalonia.Thickness(0, 4, 0, 0),
                    });
                }
                else if (counts.IsCaptureOnly)
                {
                    // Not "a translation at zero": nothing has been attempted. The difference
                    // decides whether somebody is looking at work in progress or at the game's
                    // own text handed back to them.
                    panel.Children.Add(new TextBlock
                    {
                        Text = "Nothing translated yet — the mod has met "
                             + $"{counts.Captured} line(s) and is waiting on a translation for them.",
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush("TextSecondary"),
                        Margin = new Avalonia.Thickness(0, 4, 0, 0),
                    });
                }

                panel.Children.Add(new QualityBar(counts) { Margin = new Avalonia.Thickness(0, 6, 0, 2) });
                if (QualityBar.Legend(counts) is { } legend) panel.Children.Add(legend);
            }
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

        // Which one the button at the bottom would bring down, named here rather than only in the
        // band: somebody scrolling this card is asking exactly that question, and the answer is
        // held two hundred pixels below where they are looking.
        //
        // Silent once a file is in place and matches it — the line above already says what this
        // game runs, and repeating it as an intention would read as a pending change.
        if (_takeTranslation
            && PickTranslation(report) is { } picked
            && !(report.MatchingOnline is { } current && current.Id == picked.Id))
        {
            var deliberate = _preferences.Read(report.Game.Path).TranslationId == picked.Id;

            panel.Children.Add(new TextBlock
            {
                Text = deliberate
                    ? $"Chosen for this game: {picked.SourceLanguage} → {picked.TargetLanguage} by {picked.Author ?? "unknown"}."
                    : $"Setting this game up would take: {picked.SourceLanguage} → {picked.TargetLanguage} "
                      + $"by {picked.Author ?? "unknown"} — the best ranked in your language.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(deliberate ? "StatusSuccess" : "TextSecondary"),
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            });
        }

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

        foreach (var control in TranslationVerb(report)) panel.Children.Add(control);
        foreach (var control in TranslationPlanning(report)) panel.Children.Add(control);

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

    /// <summary>
    /// The mod loader: which one, which version, and the one thing to do about it.
    ///
    /// Its own card because it is somebody else's software on somebody else's release schedule.
    /// It used to share a block — and a button — with the plugin, which meant the only way to
    /// bring a loader up to date was to reinstall the mod at the same time, and neither version
    /// could be shown next to the other.
    /// </summary>
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // THE GAME CARD'S CONVENTIONS — decided once, here, because they were being decided per
    // section. The loader hid a button the mod greyed out for the same reason, and a reader had
    // to learn each section separately to know whether an absent control meant "never" or "not
    // now". Every section below obeys these three rules.
    //
    //  · GREYED — the act belongs here and will become possible again. The reason is said next
    //    to it, and the way out is within reach: close the game, set up a translator. A greyed
    //    button is a promise for later.
    //
    //  · ABSENT — the act has nothing to work on in this situation: no receipt to remove, no
    //    loader that is ours, no eligible loader at all. It is replaced by a sentence whenever
    //    its absence could be read as something having gone wrong.
    //
    //  · ONE "primary" PER SECTION — the section's own verb, and only it. The one-click is not
    //    dressed as a fourth: it stands apart by living in the fixed bar at the bottom.
    //
    // ⚠ A running game GREYS anything that writes into it — install, remove, apply — because the
    // files come back the moment it closes. It does NOT grey "Play": there the running game is
    // not an obstacle, it is the act already done, and the only thing a second press could do is
    // start a second copy. Same fact, two readings, and the rule above is what tells them apart.
    //
    //  · WORDING — name the subject, then the consequence. "It was here before you started using
    //    this tool" said nothing about WHICH loader, WHICH version, or who "this tool" was: three
    //    blanks the reader had to fill in to make sense of one sentence. Write the loader and its
    //    version, the game's name, the file — and say "UnityGameTranslator Manager", never "this
    //    tool" and never a bare "it". Two short sentences beat one long one, and a sentence that
    //    only sets a mood beats nothing at all.
    //
    //  · COLOUR SAYS WHAT KIND OF SENTENCE IT IS, and it is not decoration:
    //      StatusError   — broken or impossible. Nothing to try here.
    //      StatusWarning — a LIMIT or a risk: what we will not do, what could be lost. "This
    //                      loader is not ours, so it is never updated from here" is this one.
    //      StatusInfo    — something is available, an act is offered.
    //      StatusSuccess — the state somebody wanted is reached.
    //      muted/opacity — context. Nothing to decide, nothing at stake.
    //    ⚠ A warning faded to 0.65 with no colour reads as a footnote, which is exactly how the
    //    "not installed by us" line ended up looking less important than the version above it.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private Control LoaderSection(GameReport report)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SectionTitle("Mod loader"));

        var running = _running.IsRunning(report.Game);
        var standing = report.LoaderStanding;

        // The recommendation is a default, not a decision made for the user: some games work
        // with one loader and not another for reasons no probe can see.
        ComboBox? loaderPicker = null;

        if (report.InstalledLoader is { } installed)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{installed.Display} {installed.Version}".Trim(),
                FontSize = 13,
                Foreground = Brush("TextPrimary"),
            });

            // ⚠ Names what it is talking about. "It was here before you started using this tool"
            // described a situation without ever saying WHICH loader, WHICH version, or who "this
            // tool" was — three things the reader has to supply themselves to make sense of it.
            panel.Children.Add(StandingLine(standing, installed.InstalledByUs
                ? null
                : $"{installed.Display} {installed.Version} was not installed by "
                + "UnityGameTranslator Manager. Other mods may need this exact version, so it is "
                + "never updated or removed from here."));
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
                Text = "None installed — we would use",
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.55,
                FontSize = 12,
            });
            row.Children.Add(loaderPicker);
            panel.Children.Add(row);
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = report.RecommendationReason ?? "No loader in the catalog fits this game.",
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // Read back by every action on this card and by the bar below it, so the loader somebody
        // picked here is the loader that gets installed. Reset on each render, because the picker
        // it closes over belongs to this rendering of this game.
        _chosenLoader = () => (loaderPicker?.SelectedItem as ComboBoxItem)?.Tag as LoaderDescriptor;

        // ⚠ Its own verb, exactly as the section below has one. The decision was "each section
        // carries its own version and its own verb, and the one-click orchestrates both" — only
        // the mod's half was ever built. Everything else was already in place: the engine does the
        // two halves separately (InstallPlan.InstallPlugin), the one-click already names this step,
        // and LoaderStanding already knows whether there is an update. Without a button here, a
        // game with no loader could only be set up through the one-click, or through the mod's
        // button, which drags the plugin along with it.
        var loaderButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
        };

        if (LoaderVerb(report) is { } verb)
        {
            var act = new Button
            {
                Content = verb,
                IsEnabled = !running,
                Classes = { "primary" },
            };

            act.Click += async (_, _) => await RunLoaderInstallAsync(report);
            loaderButtons.Children.Add(act);
        }

        // ⚠ The mod's own dialogue, on purpose — not a loader-only removal. Taking a loader away
        // while the plugin stays would leave a mod loading into nothing, which is the very order
        // this card exists to protect. That dialogue is where the three levels are chosen
        // together, and it already refuses a loader that is not ours.
        if (report.InstalledLoader is { InstalledByUs: true }
            && ReceiptStore.Read(report.Game.Path) is not null)
        {
            var remove = new Button { Content = "Uninstall...", IsEnabled = !running };

            remove.Click += async (_, _) => await RunUninstallAsync(report);
            loaderButtons.Children.Add(remove);
        }

        if (loaderButtons.Children.Count > 0) panel.Children.Add(loaderButtons);


        // Not ours: no verb, because nothing here may touch it. But a refusal with nowhere to go
        // is a dead end — the uninstaller already declines this loader, and somebody who wants it
        // gone has to be told where to look. So the way to act becomes a way to act BY HAND.
        if (report.InstalledLoader is { InstalledByUs: false } theirs)
        {
            var open = Glyphs.Button(Glyphs.Folder(), "Open the game folder");
            open.FontSize = 12;
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Click += (_, _) => Shell.OpenFolder(report.Game.Path);
            panel.Children.Add(open);

            panel.Children.Add(new Expander
            {
                Header = new TextBlock
                {
                    Text = "How to let this Manager look after the loader",
                    FontSize = 12,
                    Foreground = Brush("TextSecondary"),
                },
                Content = new TextBlock
                {
                    Text = ForeignLoaderAdvice(report, theirs),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("TextMuted"),
                },
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            });
        }

        // A refusal we are willing to let the user overrule gets a way forward. A dead button and
        // a red paragraph, with nothing to click, is the same dead end as refusing forever.
        if (!report.Game.IsModdable && ModdabilityProbe.CanBeOverridden(report.Game.Verdict))
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

        return panel;
    }

    /// <summary>
    /// The mod itself: its version, what to do about it, and the settings this game carries.
    ///
    /// The settings live here rather than on the defaults screen because they are answers about
    /// ONE game — whether to start translating in it, and what it is about. The defaults screen
    /// holds what is true of the person; this holds what is true of the game in front of them.
    /// </summary>
    private Control ModSection(GameReport report)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SectionTitle("UnityGameTranslator"));

        var running = _running.IsRunning(report.Game);
        var standing = report.PluginStanding;
        var installed = report.InstalledPluginVersion is not null;

        panel.Children.Add(new TextBlock
        {
            Text = installed ? $"Version {report.InstalledPluginVersion}" : "Not installed",
            FontSize = 13,
            Foreground = Brush("TextPrimary"),
        });

        panel.Children.Add(StandingLine(standing, null));

        // ⚠ The MOD's problems, on the MOD's card. Both were under the loader, where they read
        // as facts about BepInEx or MelonLoader — but a second copy of our assembly and our own
        // config left in the wrong folder are ours, and the buttons that settle them are here.
        foreach (var control in DuplicatePluginNotice(report)) panel.Children.Add(control);
        foreach (var control in DataBesideThePlugin(report)) panel.Children.Add(control);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
        };

        // Present once there is anywhere to put it. Installing the mod alone is a real request —
        // somebody with the loader already in place wants the plugin and nothing else — and it is
        // also how a version behind gets fixed without touching the loader underneath it.
        if (report.RecommendedLoader is not null || report.InstalledLoader is not null)
        {
            var primary = new Button
            {
                Content = installed
                    ? standing is { UpdateAvailable: true } ? $"Update to {standing.Available}" : "Reinstall"
                    : "Install the mod",
                IsEnabled = !running,
                Classes = { "primary" },
            };

            primary.Click += async (_, _) => await RunModInstallAsync(report);
            buttons.Children.Add(primary);
        }

        // ⚠ Offered whenever something of ours can be removed — which is NOT the same as "we have
        // a receipt". A mod dropped in by hand, or by a build script, or installed before receipts
        // existed, showed no button at all: the only screen able to remove it pretended there was
        // nothing there. UninstallEngine answers for both cases; asking it is what keeps this
        // button and that engine from disagreeing.
        if (new UninstallEngine(_platform, _catalog).Available(report.Game) is
            { RemovePlugin: true } or { RemoveLoader: true })
        {
            var uninstall = new Button { Content = "Uninstall...", IsEnabled = !running };
            uninstall.Click += async (_, _) => await RunUninstallAsync(report);
            buttons.Children.Add(uninstall);
        }

        // ⚠ Settings first, acts last — and it was the other way round. The buttons sat between
        // the version and a block of preferences, so "In this game" read as an afterthought
        // hanging off them rather than as what the next install would carry. The order now says
        // what it does: here is the state, here is what you want, here is the button that goes
        // and does it. Same shape as the card as a whole, whose one-click sits at the bottom.
        foreach (var control in ModSettings(report)) panel.Children.Add(control);

        panel.Children.Add(buttons);

        if (running)
        {
            // ⚠ Writing into a folder the game is holding open fails, and it fails halfway: some
            // files replaced, some refused. The engines check this again at the moment they run,
            // which is the check that must exist — but a button that cannot work should not look
            // like one that can. Kept next to the buttons it explains.
            panel.Children.Add(new TextBlock
            {
                Text = $"{report.Game.Name} is running, so its files are locked. Close it to "
                     + "install, update or remove anything.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
            });
        }

        return panel;
    }

    /// <summary>
    /// What this game does with the mod, remembered for this game alone.
    ///
    /// ⚠ The boxes save the moment they are ticked, and that is not the "apply immediately" this
    /// project refuses elsewhere: nothing here reaches the game. They record an intention, and
    /// writing it into config.json is a separate, named button. Ticking a box and closing the
    /// window with an unsaved decision would be the worse trade — it is the kind of setting one
    /// ticks on the way past.
    /// </summary>
    private IEnumerable<Control> ModSettings(GameReport report)
    {
        var settings = _settings.Current;
        var preference = _preferences.Read(report.Game.Path);

        yield return new Border
        {
            Height = 1,
            Background = Brush("BorderSubtle"),
            Margin = new Avalonia.Thickness(0, 10, 0, 4),
        };

        yield return new TextBlock
        {
            Text = "In this game",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextSecondary"),
        };

        // ⚠ Only the parts that depend on these controls are rebuilt, never the whole card.
        //
        // Redrawing the section from inside one of its own checkboxes destroys that checkbox while
        // its event is still running, and takes the keyboard focus with it — the box is left
        // looking pressed and the next Space goes nowhere. Two things react to a change here: the
        // list of differences, and the band at the bottom that says what one click would do.
        var driftHost = new StackPanel { Spacing = 4 };

        var dependents = new List<Control>();

        void Refresh()
        {
            driftHost.Children.Clear();
            foreach (var control in ConfigDrift(report, preference)) driftHost.Children.Add(control);

            // Untick "use my defaults" and one click leaves this game's config.json alone — so a
            // setting that only travels with the defaults cannot travel either. Greyed rather than
            // hidden: it keeps the value it carries, and the reason is one hover away.
            //
            // ⚠ Only the hotkey remains here. What a game translates with, and what it is about,
            // moved to the Translations section — they describe the translation, not the plugin,
            // and greying them alongside the defaults claimed a dependency they never had.
            foreach (var control in dependents)
            {
                control.IsEnabled = preference.ApplyModDefaults;

                ToolTip.SetTip(control, preference.ApplyModDefaults
                    ? control.Tag as string
                    : "Only applies while \"Use my mod defaults here\" is ticked: this setting is "
                      + "written into the game's config.json, and that box decides whether "
                      + "anything is written into it at all.");
            }

            ShowActionBar(report);
        }

        var applyDefaults = new CheckBox
        {
            Content = "Use my mod defaults here",
            IsChecked = preference.ApplyModDefaults,
            FontSize = 12,
        };

        ToolTip.SetTip(applyDefaults,
            "Language, backend, hotkey and update preferences are written into this game when "
            + "something is installed. Untick to leave its own configuration alone.");

        applyDefaults.IsCheckedChanged += (_, _) =>
        {
            preference.ApplyModDefaults = applyDefaults.IsChecked == true;
            _preferences.Set(report.Game.Path, preference);
            Refresh();
        };

        yield return applyDefaults;

        // Directly under the box that governs it: the list of differences is what ticking that box
        // would change, so it belongs to it. Further down it read as an unrelated warning about
        // the game, and the connection between the two had to be guessed.
        yield return driftHost;

        // ⚠ Asked about this game, and nowhere else. A hotkey is the one setting a game may
        // legitimately know better than the defaults do: inside it, the mod captured the key
        // against the real keyboard, which is the only measurement that exists. So the question is
        // never "do I replace hotkeys" but "do I replace THIS one" — unanswerable without both
        // keys in front of you, which is why it left the defaults screen.
        //
        // Only when the game has a key AND it differs from ours: with none it is written outright
        // (GameConfigWriter.Intended), and one that already agrees has nothing to decide. Read
        // from the config rather than from the list of differences, which deliberately hides this
        // key once the game answers it — that is exactly what makes the question ours to ask.
        var descriptor = InstalledDescriptor(report);

        var inGameHotkey = GameConfigWriter.InGameValue(
            report.Game.Path, descriptor, GameConfigWriter.HotkeyKey);

        if (inGameHotkey is not null
            && !string.Equals(inGameHotkey, settings.SettingsHotkey, StringComparison.Ordinal))
        {
            var replaceHotkey = new CheckBox
            {
                Content = "Replace this game's hotkey with mine",
                IsChecked = preference.ReplaceHotkey,
                FontSize = 12,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                Tag = "Left unticked, this game keeps the key it already has - everything else is "
                    + "still written into it. The key you set inside a game was measured against "
                    + "your real keyboard there, so it wins unless you say otherwise here.",
            };

            replaceHotkey.IsCheckedChanged += (_, _) =>
            {
                preference.ReplaceHotkey = replaceHotkey.IsChecked == true;
                _preferences.Set(report.Game.Path, preference);
                Refresh();
            };

            dependents.Add(replaceHotkey);
            yield return replaceHotkey;

            // Both keys, side by side. "Replace it?" cannot be answered without them, and this is
            // the only screen where the game's own key is ever shown.
            yield return new TextBlock
            {
                Text = $"In this game: {inGameHotkey}    Yours: {settings.SettingsHotkey}",
                FontSize = 11,
                Margin = new Avalonia.Thickness(24, 0, 0, 0),
                Foreground = Brush("StatusWarning"),
            };
        }


        // The first fill, which also settles whether the boxes above start out available.
        Refresh();
    }

    /// <summary>
    /// The catalog entry for the loader this game actually has, or null when it has none we know.
    ///
    /// Everything that reads or writes a game's config.json needs it — the file lives under a
    /// folder the descriptor names — so it is resolved once rather than by each caller in its own
    /// slightly different way.
    /// </summary>
    private LoaderDescriptor? InstalledDescriptor(GameReport report)
    {
        var loaderId = report.InstalledLoader?.Id;

        return loaderId is null
            ? null
            : _catalog.Loaders.FirstOrDefault(l => l.Id == loaderId);
    }

    /// <summary>
    /// Where this game's configuration disagrees with the defaults, and an offer to settle it.
    ///
    /// ⚠ Worded as a difference, never as a fault, and never acted on by itself. Somebody may
    /// have set this game to another language on purpose, or turned its translation off for the
    /// evening — a tool that quietly corrected that would be taking a decision back from the
    /// person who made it. Shown once the mod has a configuration to disagree with; a game that
    /// has never been launched has nothing to say.
    /// </summary>
    private IEnumerable<Control> ConfigDrift(GameReport report, GamePreference preference)
    {
        // ⚠ Shown whether or not the box above is ticked, and it used to be hidden when it was not.
        // That was backwards: what this game holds against what the defaults say is precisely the
        // information somebody needs in order to decide whether to tick it. Hiding it left the
        // choice to be made blind, and an unticked box looking like a game with nothing to settle.
        // Only the offer to write is withheld — see the foot of this block.
        if (!_settings.Current.Reviewed) yield break;

        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) yield break;

        var target = GameLanguages.TargetFor(report, descriptor, _settings.ResolveTargetLanguage());

        var differences = new GameConfigWriter()
            .Compare(report.Game.Path, descriptor, _settings.Current, target, preference);

        if (differences.Count == 0) yield break;

        var body = new StackPanel { Spacing = 4 };

        body.Children.Add(new TextBlock
        {
            Text = differences.Count == 1
                ? "One setting here differs from your defaults:"
                : $"{differences.Count} settings here differ from your defaults:",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        foreach (var difference in differences)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"• {difference.Label}: {difference.InGame} → {difference.Ours}",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });

            // Indented under its own line rather than appended to it: this is a caveat about ONE
            // setting, and folded into the row it would read as part of the value.
            if (difference.Note is { } note)
            {
                body.Children.Add(new TextBlock
                {
                    Text = note,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(12, 0, 0, 2),
                    Foreground = Brush("StatusWarning"),
                });
            }
        }

        // ⚠ Offered whether or not the box above is ticked — and it was not, which had it exactly
        // backwards. Unticking means "one click leaves this game alone", never "I may no longer
        // apply anything here": keeping the control IS the reason somebody unticks, and taking the
        // button away takes that control with it. The box governs what happens without being
        // asked; this button is the asking.
        var apply = new Button
        {
            Content = "Apply my defaults to this game",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = !_running.IsRunning(report.Game),
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
        };

        apply.Click += async (_, _) => await ApplyDefaultsAsync(report, descriptor, preference);
        body.Children.Add(apply);

        if (!preference.ApplyModDefaults)
        {
            body.Children.Add(new TextBlock
            {
                Text = "One click will not write any of this — that is what unticking above means. "
                     + "Applying it now is still yours to do.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                Foreground = Brush("TextMuted"),
            });
        }

        var notice = Callout(body, "CalloutWarningBg", "StatusWarning");
        ((Border)notice).Margin = new Avalonia.Thickness(0, 8, 0, 0);
        yield return notice;
    }

    /// <summary>
    /// One version against another, in a single line, or silence when there is nothing to say.
    /// </summary>
    private Control StandingLine(VersionStanding? standing, string? instead)
    {
        // ⚠ A warning, and it was dressed as a footnote — faded to 0.65 with no colour, so the one
        // line saying "this is not ours, we will never update or remove it" read as less important
        // than the version above it. It is the opposite: it is the sentence that explains why no
        // update will ever be offered here.
        if (instead is not null)
        {
            return new TextBlock
            {
                Text = instead,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
            };
        }

        // ⚠ Three states. Saying "up to date" when the lookup failed is the one sentence this
        // must never produce: somebody behind a firewall would be told they are current on the
        // strength of a request that never arrived.
        if (standing is null) return new TextBlock { IsVisible = false };

        if (standing.CheckFailed is { } failure)
        {
            return new TextBlock
            {
                Text = $"Could not check for a newer version ({failure}).",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
            };
        }

        if (standing.UpdateAvailable)
        {
            return new TextBlock
            {
                Text = $"{standing.Available} is available.",
                FontSize = 12,
                Foreground = Brush("StatusInfo"),
            };
        }

        if (standing.UpToDate)
        {
            return new TextBlock { Text = "Up to date.", FontSize = 12, Opacity = 0.6 };
        }

        if (!standing.IsInstalled && standing.Available is { } offered)
        {
            return new TextBlock
            {
                Text = $"{offered} would be installed.",
                FontSize = 12,
                Opacity = 0.6,
            };
        }

        return new TextBlock { IsVisible = false };
    }

    private static Control SectionTitle(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush("TextPrimary"),
    };

    // ---------------------------------------------------------------- the one button

    /// <summary>
    /// Why the one-click cannot run yet, in one sentence, or null when it can.
    ///
    /// ⚠ Never a bare disabled button. A greyed control with no reason is the single most
    /// frustrating thing an installer can show: the person can see the thing they want and has no
    /// way to learn what stands between them and it. Every branch here produces words, and the
    /// ones a person can act on produce a button too.
    /// </summary>
    private string? WhyNotReady(GameReport report)
    {
        if (!report.Game.IsModdable)
            return ModdabilityProbe.Explain(report.Game);

        if (report.Blockers.Count > 0)
            return report.Blockers[0];

        if (_running.IsRunning(report.Game))
            return "The game is running — its files are locked until it closes.";

        if (report.RecommendedLoader is null && report.InstalledLoader is null)
            return report.RecommendationReason ?? "No loader in the catalog fits this game.";

        // The prerequisite that is about the person rather than the game. Without it we do not
        // know their language, so "install everything and be ready to play" is a promise we
        // cannot keep — the mod would open its own wizard on first launch and ask them there.
        if (!_settings.Current.Reviewed)
            return "Your mod defaults have not been set yet, so there is nothing to configure this game with.";

        return null;
    }

    /// <summary>
    /// The one button, pinned under the card, and everything it needs to be honest about.
    ///
    /// Hidden entirely on the overview and on a game that is already finished — a button that
    /// would do nothing is worse than no button, because it invites a click to find out.
    /// </summary>
    private void ShowActionBar(GameReport report)
    {
        // A game nothing can be installed into gets no band at all. The card already says why, in
        // red, at the top; a disabled button repeating it underneath would be a second voice
        // saying the same no — and it would suggest there is a way to press through it.
        if (!report.Game.IsModdable)
        {
            ActionBar.IsVisible = false;
            ActionBar.Content = null;
            return;
        }

        var preference = _preferences.Read(report.Game.Path);
        var blocked = WhyNotReady(report);

        var body = new StackPanel { Spacing = 8 };

        // What it is about to do, listed before it does it. The same courtesy the install
        // confirmation already extends — here it is permanent, so the button never has to be
        // pressed to find out what it means.
        var steps = OneClickSteps(report, preference).ToList();

        // ⚠ An unticked box makes the step list empty, so "nothing left to do" cannot be decided
        // on that list alone: on a game already up to date, holding a translation with unpublished
        // work, every step is absent precisely BECAUSE there is an offer standing — and taking the
        // shortcut here left the box that offers it unreachable.
        var offered = TranslationOffers.For(report, PickTranslation(report))
                      is not (TranslationOffer.None or TranslationOffer.AlreadyInPlace);

        if (steps.Count == 0 && blocked is null && !offered)
        {
            // Everything this button could do is already done. Said rather than left blank:
            // an empty bar where a button used to be reads as something having gone wrong.
            //
            // ⚠ And it still carries the way to play. This is the card somebody opens precisely
            // because there is nothing left to arrange; sending them back to the list to find the
            // button they just walked past would be the one wrong turn left on this screen.
            var done = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

            var settled = new TextBlock
            {
                Text = "Everything is set up here. Nothing left for one click to do.",
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(settled, 0);
            done.Children.Add(settled);

            if (PlayButton(report.Game, small: false) is { } start)
            {
                start.Margin = new Avalonia.Thickness(16, 0, 0, 0);
                start.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(start, 1);
                done.Children.Add(start);
            }

            body.Children.Add(done);

            ActionBar.Content = ActionBarShell(body);
            ActionBar.IsVisible = true;
            return;
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var explanation = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        if (blocked is not null)
        {
            explanation.Children.Add(new TextBlock
            {
                Text = blocked,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
            });

            // The one blocker the person can clear from here without leaving the window. The
            // others are about the game or about it being open, and no button of ours fixes those.
            if (!_settings.Current.Reviewed)
            {
                var open = new Button
                {
                    Content = "Set my mod defaults",
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Avalonia.Thickness(0, 4, 0, 0),
                };
                open.Click += async (_, _) => await OpenSettingsAsync();
                explanation.Children.Add(open);
            }
        }
        else
        {
            // An empty list with an offer standing is the one case where the button has nothing to
            // do and the bar still has something to say: everything is installed, and the only
            // thing left is a decision nobody has taken.
            explanation.Children.Add(new TextBlock
            {
                Text = steps.Count > 0
                    ? string.Join("  ·  ", steps)
                    : "Everything is installed and up to date here.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
            });
        }

        Grid.SetColumn(explanation, 0);
        row.Children.Add(explanation);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(16, 0, 0, 0),
        };

        // Beside the button rather than up in the card: it changes what the button does, and a
        // switch for an action belongs where the action is.
        //
        // ⚠ Absent when there is nothing for it to do — nothing published to take, or the file
        // here already IS the one that would be taken. A ticked box that re-downloads the same
        // bytes reads as an action, and an unticked one reads as something being withheld.
        var offer = TranslationOffers.For(report, PickTranslation(report));

        if (offer is not (TranslationOffer.None or TranslationOffer.AlreadyInPlace))
        {
            var replaces = offer is TranslationOffer.ReplacesWork or TranslationOffer.ReplacesChoice;

            var withTranslation = new CheckBox
            {
                // The verb tells them which of the two acts this is, before they read anything
                // else. "with a translation" on a game holding their own month of work was the
                // same four words as on an empty one.
                Content = replaces ? "and replace the translation here" : "with a translation",
                IsChecked = _takeTranslation,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush(replaces ? "StatusWarning" : "TextSecondary"),
            };

            ToolTip.SetTip(withTranslation, replaces
                ? "Not ticked on purpose: " + TranslationOffers.Caution(offer)
                  + ". Tick it to take the community one anyway — you will be asked again, with "
                  + "what is at stake spelled out, and a copy is kept aside either way."
                : "Takes the best-ranked translation published in your language. Untick to start "
                  + "from a blank sheet and build your own as you play.");

            withTranslation.IsCheckedChanged += (_, _) =>
            {
                _takeTranslation = withTranslation.IsChecked == true;

                // Remembered as their answer. It is only ever honoured again where nothing is at
                // stake, so storing a yes here cannot come back to bite them on a game that has
                // acquired unpublished work since.
                preference.InstallTranslation = _takeTranslation;
                _preferences.Set(report.Game.Path, preference);

                // The list of steps depends on it, so the sentence beside the button has to follow.
                ShowActionBar(report);
            };

            right.Children.Add(withTranslation);
        }

        var go = new Button
        {
            Content = "OneClick Set Up this Game",
            Classes = { "primary" },

            // Nothing in the list means nothing would happen. Enabled, it would spend a click and
            // a confirmation to do nothing — which reads as a failure rather than as "there was
            // nothing to do", and sends people looking for what went wrong.
            IsEnabled = blocked is null && steps.Count > 0,
            MinWidth = 150,
        };

        go.Click += async (_, _) => await RunOneClickAsync(report);
        right.Children.Add(go);

        // After the set-up button, not before it: the order on this bar is the order of the two
        // acts. Present even when there is nothing left to set up — a card whose every job is done
        // is exactly the one somebody opened in order to go and play.
        if (PlayButton(report.Game, small: false) is { } play) right.Children.Add(play);

        Grid.SetColumn(right, 1);
        row.Children.Add(right);

        body.Children.Add(row);

        ActionBar.Content = ActionBarShell(body);
        ActionBar.IsVisible = true;
    }

    /// <summary>The band itself: same surface as the status bar, so it reads as part of the frame.</summary>
    private Control ActionBarShell(Control content) => new Border
    {
        Background = Brush("SurfaceBar"),
        BorderBrush = Brush("BorderSubtle"),
        BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
        Padding = new Avalonia.Thickness(20, 12, 24, 12),
        Child = content,
    };

    /// <summary>
    /// What one click would actually do here, in order, and nothing it would not.
    ///
    /// Recomputed rather than remembered, so the sentence cannot describe a state the game left
    /// behind two rescans ago.
    /// </summary>
    private IEnumerable<string> OneClickSteps(GameReport report, GamePreference preference)
    {
        if (report.InstalledLoader is null && report.RecommendedLoader is { } loader)
            yield return $"install {loader.Display}";
        else if (report.LoaderStanding is { UpdateAvailable: true } loaderStanding)
            yield return $"update the loader to {loaderStanding.Available}";

        if (report.InstalledPluginVersion is null)
            yield return "install the mod";
        else if (report.PluginStanding is { UpdateAvailable: true } pluginStanding)
            yield return $"update the mod to {pluginStanding.Available}";

        if (preference.ApplyModDefaults && _settings.Current.Reviewed)
            yield return "apply your settings";

        if (!_takeTranslation || PickTranslation(report) is not { } chosen) yield break;

        // Worded by what it would DO, not by what exists. The three are different acts and the
        // person is about to authorise one of them with a single click.
        yield return TranslationOffers.For(report, chosen) switch
        {
            TranslationOffer.ReplacesWork =>
                "replace the translation here, losing what was never uploaded (it will ask first)",
            TranslationOffer.ReplacesChoice =>
                "swap the translation here for another one (it will ask first)",
            TranslationOffer.FreeToTake when report.LocalTranslation is not null =>
                "update the translation",
            _ =>
                $"take the {chosen.TargetLanguage ?? Languages.NameOf(_settings.ResolveTargetLanguage())} "
                + $"translation by {chosen.Author ?? "its author"}",
        };
    }

    /// <summary>
    /// Which translation one click would take, or null when it would take none.
    ///
    /// The rules, in order, and each of them is a decision rather than a convenience:
    ///  · what the person already chose wins — a pick made in the translations window is an
    ///    answer, and quietly preferring our own would make that window advisory;
    ///  · otherwise the FIRST one published in their language, in the order the SERVER sent. That
    ///    order is Translation::ranking_score, which normalises by the best score of the game and
    ///    already leaves branches out. Re-sorting here would produce a different best from the
    ///    website's for the same data, and neither could be called wrong;
    ///  · a file already in the game does NOT stop the pick, because a newer version of that very
    ///    translation is worth taking. What it does is turn the step into a replacement, which is
    ///    asked about.
    /// </summary>
    private OnlineTranslation? PickTranslation(GameReport report)
    {
        if (report.OnlineTranslations.Count == 0) return null;

        var preference = _preferences.Read(report.Game.Path);

        if (preference.TranslationId is { } chosen)
        {
            var picked = report.OnlineTranslations.FirstOrDefault(t => t.Id == chosen);

            // Gone from the catalogue — taken down, or made private. Falling through to the
            // ranking rather than failing: the person asked for a translation, and the one they
            // named no longer being there is not a reason to leave them without one.
            if (picked is not null) return picked;
        }

        var target = _settings.ResolveTargetLanguage();

        return report.OnlineTranslations
            .FirstOrDefault(t => Languages.Matches(t.TargetLanguage, target));
    }

    /// <summary>
    /// Does everything this game still needs, in one go, asking only where something is at stake.
    ///
    /// ⚠ The questions are asked BEFORE anything is written, not between steps. Somebody who
    /// pressed one button should not be interrupted three times while files are half in place —
    /// and a refusal at question two would otherwise leave a game in a state nobody chose.
    /// </summary>
    private async Task RunOneClickAsync(GameReport report)
    {
        if (WhyNotReady(report) is not null) return;

        var preference = _preferences.Read(report.Game.Path);
        var translation = _takeTranslation ? PickTranslation(report) : null;

        // Everything at stake, gathered and asked once.
        var body = new StackPanel { Spacing = 10 };

        body.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine,
                OneClickSteps(report, preference).Select(step => "• " + step)),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        if (translation is not null && report.LocalTranslation is { } local)
        {
            foreach (var warning in ReplacementWarnings(report, local, translation))
                body.Children.Add(warning);
        }

        if (!await ConfirmAsync($"Set up {report.Game.Name}?", body, "Set it up")) return;

        Busy(true, "Starting...");

        var engine = new InstallEngine(_platform, _catalog);
        engine.Status += OnEngineStatus;

        try
        {
            var plan = BuildPlan(report, preference, loader: true, plugin: true);
            if (plan is null)
            {
                Busy(false, "Ready.");
                await MessageAsync("Nothing was changed",
                    report.RecommendationReason ?? "No plan could be made for this game.");
                return;
            }

            var outcome = await engine.ApplyAsync(plan);
            if (!outcome.Success)
            {
                Busy(false, "Failed.");
                await MessageAsync("Nothing was changed", outcome.Message);
                return;
            }

            var message = outcome.Message;

            if (translation is not null)
                message += Environment.NewLine + Environment.NewLine + await TakeTranslationAsync(report, plan.Loader, translation);

            Busy(false, "Done.");
            await MessageAsync("Ready to play", message);
        }
        finally
        {
            engine.Status -= OnEngineStatus;
            Busy(false, "Ready.");
        }

        await ShowSelectedAsync();
    }

    /// <summary>
    /// What the person stands to lose by taking this translation, in their own terms.
    ///
    /// ⚠ Role-aware, because the same replacement means three different things. Somebody who only
    /// plays loses a file they can take again; somebody who leads this translation, or contributes
    /// a branch to it, loses work nobody else has a copy of. The mod says the same three things,
    /// from the same server answer.
    /// </summary>
    private IEnumerable<Control> ReplacementWarnings(GameReport report, LocalTranslation local,
                                                     OnlineTranslation taking)
    {
        // Already the one being taken, untouched: nothing is at stake beyond a re-download.
        if (TranslationInstaller.LooksRecoverableOnline(local, taking)) yield break;

        if (local.LocalChanges > 0)
        {
            yield return new TextBlock
            {
                Text = $"You have {local.LocalChanges} line(s) here that have never been uploaded. "
                     + "A copy is kept aside, but this game will stop using them.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
            };

            // The way to keep both, named rather than left to be discovered. This tool does not
            // merge, and pretending otherwise here would be the moment it lost somebody's work.
            yield return new TextBlock
            {
                Text = "To keep your work AND take this one, do it from inside the mod: it holds "
                     + "the original version and the screens to settle line by line.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
                FontSize = 12,
            };
        }
        else if (local.EntryCount > 0)
        {
            yield return new TextBlock
            {
                Text = $"The {local.EntryCount} line(s) already here will be replaced. A copy is kept aside.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
            };
        }

        if (report.MyPosition is not { } position) yield break;

        yield return new TextBlock
        {
            Text = position.IsMain
                ? "This translation is yours — you are its Main. Replacing the file here does not "
                + "touch what you published, but anything you have not published yet leaves this game."
                : "You contribute a branch to this translation. Replacing the file here leaves "
                + "your branch untouched on the site, and this game stops showing your version of it.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("StatusWarning"),
            FontSize = 12,
        };
    }

    /// <summary>
    /// Fetches and installs one translation, and reports it in a sentence. Never throws into the
    /// caller: a mod that installed correctly must not be reported as a failure because the
    /// download that followed it did not arrive.
    /// </summary>
    private async Task<string> TakeTranslationAsync(GameReport report, LoaderDescriptor loader,
                                                    OnlineTranslation translation)
    {
        Status("Downloading the translation...");

        var api = new CatalogApiClient();
        var json = await api.DownloadAsync(translation.Id, _settings.Current.ApiToken);

        if (json is null)
            return $"The translation could not be downloaded ({api.LastError ?? "no reason given"}). Everything else is in place.";

        var result = new TranslationInstaller()
            .Install(report.Game.Path, loader, json, translation.FileHash);

        if (!result.Written)
            return $"The translation could not be written ({result.Failure}). Everything else is in place.";

        // Remembered so the card can say which one this game runs, and so a later one-click does
        // not silently pick a different translation than the one already in place.
        var preference = _preferences.Read(report.Game.Path);
        preference.TranslationId = translation.Id;
        _preferences.Set(report.Game.Path, preference);

        var message = "The translation is in place.";
        if (result.BackupPath is not null)
        {
            message += $" Your previous file was kept in {TranslationInstaller.BackupFolderName}/"
                     + $"{Path.GetFileName(result.BackupPath)}.";
        }

        return message;
    }

    /// <summary>
    /// The plan for one game, with the two halves asked for separately.
    ///
    /// ⚠ Settings are passed only when this game asked for them AND a human has been through the
    /// defaults screen. Before that we know nothing about their language, and writing our own
    /// into their game would be deciding for them — the mod's first-run wizard asks instead,
    /// which is correct.
    /// </summary>
    /// <param name="settings">
    /// False for the acts that are not about the mod's configuration at all — putting a loader in
    /// place, first of all.
    ///
    /// ⚠ It was not a parameter, and that cost a real install: pressing "install the loader" wrote
    /// the language and the backend into a config.json, in a plugin folder the plugin had not been
    /// installed into. The orphan folder was then counted as somebody else's mod, and the loader we
    /// had just installed could no longer be removed — "other mods use it". A button writes what it
    /// names, and nothing else.
    /// </param>
    private InstallPlan? BuildPlan(GameReport report, GamePreference preference, bool loader, bool plugin,
                                   bool settings = true, bool force = false)
    {
        var writeSettings = settings && preference.ApplyModDefaults && _settings.Current.Reviewed;

        var plan = new InstallEngine(_platform, _catalog).Plan(
            report,
            _settings.Current.Channel == "beta" ? ReleaseChannel.Beta : ReleaseChannel.Stable,
            _chosenLoader(),
            writeSettings ? _settings.Current : null,
            writeSettings ? preference : null);

        if (plan is null) return null;

        // InstallLoader is decided by Plan from what is on disk; forcing it true is how a loader
        // of ours gets replaced by a newer one, and it is only ever asked for where the card has
        // already established that the loader is ours to replace.
        return plan with
        {
            // ⚠ force exists for one case: a reinstall asked for by name. Plan() only turns this
            // on when there is no loader, and the standing test adds the one that is behind — so
            // without it, a "reinstall" button would confirm, run, report success and replace
            // nothing. The one-click must NOT force: it passes loader: true meaning "put one there
            // if needed", and forcing would replace a perfectly current loader on every click.
            InstallLoader = loader && (force
                                       || plan.InstallLoader
                                       || report.LoaderStanding is { UpdateAvailable: true }),
            InstallPlugin = plugin,
        };
    }

    /// <summary>
    /// A second copy of the mod in this game, and the button that settles it.
    ///
    /// Said on the card, not only in an install confirmation nobody has opened yet. Two assemblies
    /// is the quietest serious fault this tool can meet: both loaders read the shared folder before
    /// its subfolders, so the OLDER copy wins, and every update lands correctly while the game goes
    /// on running the previous version. Nothing fails, nothing is logged, and the person concludes
    /// the update did nothing.
    /// </summary>
    private IEnumerable<Control> DuplicatePluginNotice(GameReport report)
    {
        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) yield break;

        var strays = LocalTranslationProbe.FindStrayPlugins(report.Game.Path, descriptor);
        if (strays.Count == 0) yield break;

        var body = new StackPanel { Spacing = 4 };

        body.Children.Add(new TextBlock
        {
            Text = strays.Count == 1
                ? $"Another copy of the mod is installed in {strays[0]}/."
                : $"Other copies of the mod are installed in {string.Join(", ", strays)}.",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("StatusWarning"),
        });

        body.Children.Add(new TextBlock
        {
            Text = $"The loader reads {descriptor.PluginDir}/ before anything under it, so the older "
                 + "copy is the one that runs. Updates would install correctly and change nothing "
                 + "you can see.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        });

        var fix = new Button
        {
            Content = strays.Count == 1 ? "Remove the other copy" : "Remove the other copies",
            FontSize = 12,
            Classes = { "primary" },
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = !_running.IsRunning(report.Game),
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };

        fix.Click += async (_, _) =>
        {
            // Our assembly by name, nothing else in those folders - the same rule the installer
            // follows. A folder that empties goes with it; anything else there is somebody's.
            var removed = new List<string>();
            var failed = new List<string>();

            foreach (var stray in strays)
            {
                var directory = Path.Combine(report.Game.Path,
                    stray.Replace('/', Path.DirectorySeparatorChar));

                var copy = Path.Combine(directory, LocalTranslationProbe.PluginAssemblyName);

                try
                {
                    if (File.Exists(copy)) File.Delete(copy);
                    FileOperations.TryRemoveEmptyDirectory(directory);
                    removed.Add(stray);
                }
                catch (Exception ex)
                {
                    failed.Add($"{stray} ({ex.Message})");
                }
            }

            await MessageAsync(failed.Count == 0 ? "Removed" : "Partly removed",
                failed.Count == 0
                    ? $"The extra copy in {string.Join(", ", removed)} is gone. This game now runs "
                      + "the one version installed here."
                    : "Some copies could not be removed:" + Environment.NewLine
                      + string.Join(Environment.NewLine, failed.Select(f => "- " + f)));

            await ShowSelectedAsync();
        };

        body.Children.Add(fix);
        yield return Callout(body, "CalloutWarningBg", "StatusWarning");
    }

    /// <summary>
    /// Our own data files sitting one level above where the mod reads them — reported, never
    /// touched.
    ///
    /// ⚠ BepInEx only, and it follows from where the mod keeps things. Its documented home is
    /// plugins/UnityGameTranslator/, so a config, a translation or a font dropped straight into
    /// plugins/ is invisible to the mod: it will read none of them and quietly start from
    /// defaults, which somebody experiences as "my translation disappeared".
    ///
    /// ⚠ We do NOT move or delete them, and that is deliberate rather than lazy. A translation
    /// there may be work nobody else has, the mod's own file may already exist in the right place,
    /// and merging two is a decision with a loser. So the folder is opened and the choice is left
    /// to the person who made those files.
    /// </summary>
    private IEnumerable<Control> DataBesideThePlugin(GameReport report)
    {
        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) yield break;

        var pluginDir = Path.Combine(report.Game.Path,
            descriptor.PluginDir.Replace('/', Path.DirectorySeparatorChar));

        // Only where our folder IS the documented location — under MelonLoader the parent is the
        // game's root and everything in it belongs to the game.
        if (!string.Equals(Path.GetFileName(pluginDir), LocalTranslationProbe.PluginFolderName,
                           StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        if (Path.GetDirectoryName(pluginDir) is not { } parent || !Directory.Exists(parent)) yield break;

        var strays = new List<string>();

        foreach (var name in new[] { LocalTranslationProbe.ConfigFileName, "translations.json" })
        {
            if (File.Exists(Path.Combine(parent, name))) strays.Add(name);
        }

        foreach (var folder in new[] { "fonts", "images" })
        {
            if (Directory.Exists(Path.Combine(parent, folder))) strays.Add(folder + "/");
        }

        if (strays.Count == 0) yield break;

        var body = new StackPanel { Spacing = 4 };

        body.Children.Add(new TextBlock
        {
            Text = $"{string.Join(", ", strays)} sit in {descriptor.PluginDir}/../ rather than "
                 + "beside the mod. It reads none of them.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("StatusWarning"),
        });

        body.Children.Add(new TextBlock
        {
            Text = "Nothing here moves or deletes them: a translation there may be work nobody "
                 + "else has, and the mod may already have its own file in the right place. "
                 + "Compare them yourself and keep the one you want.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        });

        var open = Glyphs.Button(Glyphs.Folder(), "Open that folder");
        open.FontSize = 12;
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Click += (_, _) => Shell.OpenFolder(parent);
        body.Children.Add(open);

        yield return Callout(body, "CalloutWarningBg", "StatusWarning");
    }

    /// <summary>
    /// What somebody may safely delete themselves under a loader that is not ours, and what they
    /// must not.
    ///
    /// ⚠ Only ever names paths WE own. Listing the loader's own files would be this tool giving
    /// removal instructions for someone else's software, from a catalogue entry that describes how
    /// to detect it rather than how to take it apart — and being wrong there costs somebody every
    /// other mod they had.
    /// </summary>
    private string ForeignLoaderAdvice(GameReport report, DetectedLoader theirs)
    {
        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == theirs.Id);

        var lines = new List<string>
        {
            $"{theirs.Display} was not installed by UnityGameTranslator Manager, so it is never "
            + "modified or removed from here.",
        };

        // The count is already measured for the uninstaller's refusal; saying it here turns that
        // refusal into something checkable rather than something to take on trust.
        if (theirs.ForeignPluginCount > 0)
        {
            lines.Add($"{theirs.ForeignPluginCount} other mod(s) sit beside ours in "
                    + $"{theirs.PluginDir}/. Removing the loader removes those too.");
        }

        // ⚠ This note answers ONE question: how to let this Manager look after the loader. It used
        // to name "the UnityGameTranslator folder" as safe to delete by hand, which was wrong three
        // times over — it holds translations that may exist nowhere else, under MelonLoader the mod
        // is a DLL in Mods/ rather than a folder at all, and under BepInEx the named directory is
        // the one holding everything. Removing OUR files is what the Uninstall button is for, and
        // it asks about your data first.
        lines.Add($"To let UnityGameTranslator Manager look after the loader instead, remove "
                + $"{theirs.Display} by hand — follow its own documentation — then install it "
                + "again from this card.");

        lines.Add("⚠ Do not delete the mod's folder or files to do that. They hold your settings "
                + "and your translation, including lines captured while playing that may exist "
                + "nowhere else. Use \"Uninstall...\" above instead: it asks whether to keep them, "
                + "and copies them aside before removing anything.");

        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    /// <summary>
    /// Puts the loader in place — or brings it up to date — and touches nothing else.
    ///
    /// ⚠ No plugin and NO settings. A loader is the thing that lets mods load; the mod's own
    /// configuration is not its business, and writing it here created a plugin folder with no
    /// plugin in it, which the uninstaller then read as another mod's.
    /// </summary>
    private async Task RunLoaderInstallAsync(GameReport report)
    {
        var preference = _preferences.Read(report.Game.Path);

        // force: this button was pressed by name. Without it a reinstall would do nothing.
        var plan = BuildPlan(report, preference,
            loader: true, plugin: false, settings: false, force: true);

        await RunInstallAsync(report, new InstallEngine(_platform, _catalog), plan);
    }

    /// <summary>Installs or replaces the plugin, and leaves the loader alone.</summary>
    private async Task RunModInstallAsync(GameReport report)
    {
        var preference = _preferences.Read(report.Game.Path);

        // The loader still comes along when there is none — a plugin without one loads in no game,
        // and refusing here would mean the mod's own button could not work on a fresh game.
        var plan = BuildPlan(report, preference,
            loader: report.InstalledLoader is null, plugin: true);

        await RunInstallAsync(report, new InstallEngine(_platform, _catalog), plan);
    }

    /// <summary>
    /// What this game's translation is meant to become, and the two settings that follow from it.
    ///
    /// ⚠ These lived under the mod's card, and neither belongs there. "Start translating" decides
    /// whether a translation is MADE while playing; "what is this game about" is the context sent
    /// to the translator and has no effect at all without one. Both describe the translation, not
    /// the plugin — and they sat above the very section that is about translations, so somebody
    /// who only wanted a community file had to walk through them first.
    ///
    /// ⚠ The posture DECIDES NOTHING. It proposes those two settings, which stay editable beside
    /// it, so changing it never touches a game: the buttons act, with the warnings they carry.
    /// That is what makes it safe to switch after an install rather than a way to lose a file.
    /// </summary>
    /// <summary>
    /// What this game is set up to do, READ from its state rather than from a stored answer.
    ///
    /// ⚠ This is the whole reason the posture stopped being a picker. A value kept in preferences
    /// is a claim about a game somebody may have changed since — and for every game that existed
    /// before the field did, it is a claim nobody ever made. Reading it from the file, the
    /// selection and the translate-while-playing switch cannot go stale: a game already carrying
    /// somebody's own translation reports itself as such at first sight, on a refresh, and on a
    /// machine that has never seen it. That was the original complaint — a jeu already has a
    /// profile, so do not overwrite it with a default.
    /// </summary>
    private Posture DeducedPosture(GameReport report, GamePreference preference)
    {
        var translating = preference.StartTranslation ?? _settings.Current.EnableAi;

        // Nothing to read and nothing chosen: whatever happens here starts from nothing.
        var hasTranslation = report.LocalTranslation is not null
                             || (preference.InstallTranslation && PickTranslation(report) is not null);

        if (!hasTranslation) return Posture.Start;

        return translating ? Posture.Complete : Posture.Use;
    }

    private IEnumerable<Control> TranslationPlanning(GameReport report)
    {
        var preference = _preferences.Read(report.Game.Path);
        var descriptor = InstalledDescriptor(report);

        // ⚠ No posture picker here, and it was here for a day. Asking "use it / complete it /
        // start from nothing" as three radio buttons put the same question twice: the Home tab
        // already answers it by what somebody does — select a translation, or choose to make one
        // — and a form repeating a decision the journey has taken is how two answers end up
        // disagreeing. The posture stays as a stored value that the journey writes; it is no
        // longer something to fill in.
        yield return new Border
        {
            Height = 1,
            Background = Brush("BorderSubtle"),
            Margin = new Avalonia.Thickness(0, 10, 0, 6),
        };

        var planHost = new StackPanel { Spacing = 4 };

        void RefreshPlan()
        {
            planHost.Children.Clear();
            foreach (var control in PlanDetail(report, preference, descriptor, RefreshPlan))
                planHost.Children.Add(control);
        }

        RefreshPlan();
        yield return planHost;
    }

    /// <summary>
    /// What this game is about, in the words handed to the translator — the mod's game_context.
    ///
    /// The one wizard question whose answer cannot be shared between two games, which is why it is
    /// per game. It belongs to the GAME rather than to the defaults, so it is never greyed with
    /// them and it has its own button: saving a sentence must not carry the language and the
    /// backend in with it.
    /// </summary>
    private IEnumerable<Control> GameContextField(GameReport report, GamePreference preference,
                                                  LoaderDescriptor? descriptor, Action refresh)
    {
        // ⚠ Pre-filled from the game when it already carries one — somebody may well have written
        // it from inside the mod's options while playing. An empty box over an answer that exists
        // invites retyping it, and cannot be told apart from "nothing was ever asked".
        var inGameContext = GameConfigWriter.InGameValue(
            report.Game.Path, descriptor, GameConfigWriter.GameContextKey);

        yield return new TextBlock
        {
            Text = "What is this game about?    (optional)",
            FontSize = 12,
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
            Foreground = Brush("TextSecondary"),
        };

        yield return new TextBlock
        {
            Text = "Sent with every line it translates, so it reaches for the right words: a game "
                 + "about starships and one about Roman legions do not share a vocabulary.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        };

        var context = new TextBox
        {
            Text = (preference.GameContext ?? inGameContext) ?? "",
            Watermark = "Genre, tone, setting - a sentence is enough",
            FontSize = 12,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 84,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };

        var actions = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(0, 4, 0, 0) };

        // Saved when the field loses focus rather than on every keystroke: a file written per
        // character is a file written a hundred times for one sentence.
        context.LostFocus += (_, _) =>
        {
            var written = string.IsNullOrWhiteSpace(context.Text) ? null : context.Text.Trim();

            // Against what is DISPLAYED: a value read from the game and left alone is not an edit,
            // and storing it would turn merely opening the card into a decision to write it back.
            if (written == (preference.GameContext ?? inGameContext)) return;

            preference.GameContext = written;
            _preferences.Set(report.Game.Path, preference);
            refresh();
        };

        yield return context;

        var current = preference.GameContext ?? inGameContext;

        if (string.Equals(current, inGameContext, StringComparison.Ordinal))
        {
            if (preference.GameContext is null && inGameContext is not null)
            {
                actions.Children.Add(new TextBlock
                {
                    Text = "Read from this game.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("TextMuted"),
                });
            }
        }
        else if (descriptor is not null)
        {
            var save = new Button
            {
                Content = "Save this into the game",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = !_running.IsRunning(report.Game),
            };

            save.Click += async (_, _) =>
            {
                Busy(true, "Saving...");

                // One key, on its own — going through Apply would write the language, the backend
                // and the update preferences in the same breath.
                var result = new GameConfigWriter().ApplyOne(
                    report.Game.Path, descriptor, GameConfigWriter.GameContextKey,
                    current, "what this game is about");

                Busy(false, "Ready.");

                if (!result.Written)
                {
                    await MessageAsync("Nothing was changed",
                        $"It could not be written ({result.Failure}).");
                    return;
                }

                refresh();
            };

            actions.Children.Add(save);
            actions.Children.Add(new TextBlock
            {
                Text = inGameContext is null
                    ? "This game has nothing written for it yet."
                    : $"This game currently says: {inGameContext}",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });
        }

        yield return actions;
    }

    /// <summary>
    /// The consequence of the posture, and the two settings that carry it out.
    /// </summary>
    private IEnumerable<Control> PlanDetail(GameReport report, GamePreference preference,
                                            LoaderDescriptor? descriptor, Action refresh)
    {
        var settings = _settings.Current;
        var posture = DeducedPosture(report, preference);
        var backend = TranslationBackendLabel(settings);

        yield return new TextBlock
        {
            Text = SituationReader.Consequence(posture),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(24, 0, 0, 6),
            Foreground = Brush("TextMuted"),
        };

        // ⚠ Greyed with no backend rather than hidden: hiding it left the one question this
        // section exists to answer — will this game actually translate anything — unanswered.
        var start = new CheckBox
        {
            Content = "Translate while I play",
            IsChecked = backend is not null && (preference.StartTranslation ?? settings.EnableAi),
            IsEnabled = backend is not null,
            FontSize = 12,
        };

        start.IsCheckedChanged += (_, _) =>
        {
            preference.StartTranslation = start.IsChecked == true;
            _preferences.Set(report.Game.Path, preference);
            ShowActionBar(report);
        };

        yield return start;

        if (backend is not null)
        {
            // What it would translate WITH. Which service it is decides whether it costs money,
            // so it cannot stay implicit behind a switch.
            yield return new TextBlock
            {
                Text = backend,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(24, 0, 0, 0),
                Foreground = Brush("TextMuted"),
            };
        }
        else
        {
            yield return new TextBlock
            {
                Text = "No translator is set up, so nothing can be translated as you play. "
                     + "A published translation still works — it is already written.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(24, 0, 0, 0),
                Foreground = Brush("TextMuted"),
            };

            var configure = new Button
            {
                Content = "Set up a translator...",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Avalonia.Thickness(24, 2, 0, 0),
            };

            configure.Click += async (_, _) => await OpenSettingsAsync();
            yield return configure;
        }

        // Only where it can do something: it is the context handed to a translator, and without
        // one it is a box that changes nothing. Kept under the switch that gives it its purpose.
        if (backend is null) yield break;

        foreach (var control in GameContextField(report, preference, descriptor, refresh))
            yield return control;
    }

    /// <summary>
    /// What the card offers to do about translations: which one is selected, whether the game is
    /// running it, and the one button that settles the difference.
    ///
    /// ⚠ The translations window selects; this acts. Taking a file used to happen there too, so
    /// the same screen meant two things depending on the game behind it and a replacement was
    /// weighed in two places. Here we can see what the game already carries, which is the only
    /// place that comparison exists.
    /// </summary>
    private IEnumerable<Control> TranslationVerb(GameReport report)
    {
        // Nothing published for this game. The most important thing this card can say, and it said
        // nothing at all: somebody was left looking at an empty section with no idea that the mod
        // is meant to be used exactly like this.
        if (report.OnlineTranslations.Count == 0)
        {
            yield return new TextBlock
            {
                Text = "No translation has been published for this game yet.",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
                Foreground = Brush("TextPrimary"),
            };

            // ⚠ Conditional on purpose. Somebody with a translator set up needs one sentence;
            // somebody without one needs to know the mod is still usable, or they will conclude
            // it is not for them and stop here.
            yield return new TextBlock
            {
                Text = TranslationBackendLabel(_settings.Current) is not null
                    ? "Yours would be the first: play with the mod on and it translates as it "
                      + "meets text, then you can publish it for everyone else."
                    : "You can still make one without any translator. The mod captures the game's "
                      + "text as you play, and its live editor lets you write the lines yourself, "
                      + "in game, one at a time — that is how a translation is made by hand.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 2, 0, 0),
                Foreground = Brush("TextSecondary"),
            };

            yield break;
        }

        if (PickTranslation(report) is not { } picked) yield break;

        var preference = _preferences.Read(report.Game.Path);
        var offer = TranslationOffers.For(report, picked);
        var author = picked.Author ?? "its author";

        // ⚠ Said whenever nobody chose it. A pick made for somebody, presented as theirs, is how
        // they end up with a translation they never agreed to — and the rule that made it (best
        // ranked in your language) is worth naming, because it is a defensible rule rather than
        // a coin toss.
        if (preference.TranslationId is null)
        {
            yield return new TextBlock
            {
                Text = $"Chosen for you: the best-ranked one in {picked.TargetLanguage ?? "your language"}, "
                     + $"by {author}. Open the list to pick another.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
                Foreground = Brush("TextMuted"),
            };
        }

        // Nothing to do about it: the file here IS this translation, unchanged.
        if (offer == TranslationOffer.AlreadyInPlace)
        {
            yield return new TextBlock
            {
                Text = $"This game is running it — the one by {author}, up to date.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
                Foreground = Brush("StatusSuccess"),
            };

            yield break;
        }

        // ⚠ The dependency, stated rather than enforced by a dead button: without the mod there is
        // nowhere to write. The selection is kept and the one-click carries it out.
        if (report.InstalledPluginVersion is null)
        {
            yield return new TextBlock
            {
                Text = $"Selected: the one by {author}. It goes in when the mod is installed — "
                     + "the button at the bottom does both.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
                Foreground = Brush("TextMuted"),
            };

            yield break;
        }

        var replacing = offer is TranslationOffer.ReplacesWork or TranslationOffer.ReplacesChoice;

        // Said before the button, not only in the dialogue it opens: somebody has to know the game
        // is not running the translation they picked WITHOUT having to press anything to find out.
        if (replacing)
        {
            yield return new TextBlock
            {
                Text = $"This game is not running the one you selected. Yours is by {author}.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
                Foreground = Brush("StatusWarning"),
            };
        }

        var act = new Button
        {
            Content = offer switch
            {
                TranslationOffer.ReplacesWork or TranslationOffer.ReplacesChoice => "Replace it with this one...",
                _ when report.LocalTranslation is not null => "Update the translation",
                _ => "Download this translation",
            },
            FontSize = 12,
            Classes = { "primary" },
            IsEnabled = !_running.IsRunning(report.Game),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
        };

        act.Click += async (_, _) => await TakeSelectedTranslationAsync(report, picked, replacing);
        yield return act;
    }

    /// <summary>
    /// Downloads the selected translation into the game, asking first when something is at stake.
    ///
    /// ⚠ The warnings are the ones the one-click already shows, from the same place: a replacement
    /// decided here and a replacement decided there must weigh the same, or the safer-looking
    /// route becomes the one that loses work.
    /// </summary>
    private async Task TakeSelectedTranslationAsync(GameReport report, OnlineTranslation picked,
                                                    bool replacing)
    {
        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) return;

        if (replacing && report.LocalTranslation is { } local)
        {
            var body = new StackPanel { Spacing = 6 };
            foreach (var warning in ReplacementWarnings(report, local, picked)) body.Children.Add(warning);

            if (!await ConfirmAsync($"Replace the translation in {report.Game.Name}?", body, "Replace it"))
                return;
        }

        Busy(true, "Downloading the translation...");
        var message = await TakeTranslationAsync(report, descriptor, picked);
        Busy(false, "Ready.");

        await MessageAsync("Translation", message);

        // Asked AFTER the file is in place, because it only matters once it exists — and because
        // saying no must leave a working translation behind rather than a cancelled operation.
        await OfferToAlignGameAsync(report, descriptor, picked);

        await ShowSelectedAsync();
    }

    /// <summary>
    /// Points the game at the language of the translation just taken, with permission.
    ///
    /// This is the case that actually happens: no translation in your language for a Japanese or
    /// Chinese game, so you take the English one. Without this the file lands in a game still set
    /// to French — the mod ignores what was just installed and carries on translating towards a
    /// language nobody published, and nothing on screen explains why.
    ///
    /// ⚠ Asked, never done silently. The target language also decides what the mod translates as
    /// you play, so changing it reaches beyond this file — and somebody running two games in two
    /// languages has a reason we cannot guess.
    ///
    /// ⚠ Writes that ONE key. It used to go through Apply, which carried the backend and the
    /// update preferences along with it — a language question answered by rewriting the whole
    /// configuration.
    /// </summary>
    private async Task OfferToAlignGameAsync(GameReport report, LoaderDescriptor descriptor,
                                             OnlineTranslation translation)
    {
        var taken = translation.TargetLanguage;
        if (string.IsNullOrWhiteSpace(taken)) return;

        // What the GAME is set to, not what this tool defaults to: they are allowed to differ, and
        // this one is what the mod acts on.
        var configured = LocalTranslationProbe.ReadTargetLanguage(report.Game.Path, descriptor);

        if (configured is null) return;
        if (string.Equals(configured, taken, StringComparison.OrdinalIgnoreCase)) return;

        var agreed = await ConfirmAsync($"Point the game at {taken}?",
            $"This game is set to {configured}, and the translation you just took is in {taken}. "
            + $"Left as it is, the mod will keep working towards {configured} and will not use the "
            + "file you just installed."
            + Environment.NewLine + Environment.NewLine
            + $"Switching only changes this game. Your default stays "
            + $"{Languages.NameOf(_settings.ResolveTargetLanguage())}.",
            $"Use {taken} for this game");

        if (!agreed) return;

        // ⚠ The SOURCE language is deliberately not carried across. That field describes the person
        // who made the translation, not the game: nothing here can read what language a game's own
        // text is in, and writing a guess would put "translate from English" into every prompt —
        // and, under strict_source_language, retire every line judged to be in another language.
        new GameConfigWriter().ApplyOne(report.Game.Path, descriptor,
            GameConfigWriter.TargetLanguageKey, taken, "language");
    }

    /// <summary>
    /// The translator these defaults would run, in one line, or null when there is none.
    ///
    /// ⚠ Names the service AND what it needs to work. "AI translation" over an empty server
    /// address is a promise the game cannot keep, and the failure would surface in-game with
    /// nothing to explain it — the same reason AnswersTheWizard refuses to skip the mod's wizard
    /// on a half-configured backend.
    /// </summary>
    private static string? TranslationBackendLabel(InstallerSettings settings) =>
        settings.TranslationBackend switch
        {
            "llm" when !string.IsNullOrWhiteSpace(settings.AiUrl) =>
                string.IsNullOrWhiteSpace(settings.AiModel)
                    ? $"Using your own AI at {settings.AiUrl} — no model chosen yet"
                    : $"Using {settings.AiModel} on your own AI at {settings.AiUrl}",

            "google" when !string.IsNullOrWhiteSpace(settings.GoogleApiKey) =>
                "Using Google Translate with your key",

            "deepl" when !string.IsNullOrWhiteSpace(settings.DeeplApiKey) =>
                $"Using DeepL ({(settings.DeeplUseFree ? "free" : "paid")}) with your key",

            // Everything else — "none", or a backend chosen but left without what it needs — is
            // reported as nothing set up. A key that is missing translates exactly as little as a
            // backend that was never picked.
            _ => null,
        };

    /// <summary>
    /// What the loader section offers to do, or null when it offers nothing.
    ///
    /// ⚠ A loader we did not install gets no verb at all, and that is a decision rather than a
    /// gap: it was here before us, other mods may depend on that exact version, and replacing it
    /// is not ours to offer. The line above it already says so.
    /// </summary>
    private string? LoaderVerb(GameReport report)
    {
        if (report.InstalledLoader is { } installed)
        {
            if (!installed.InstalledByUs) return null;

            // Same three verbs as the mod's section, in the same order, for the same reasons.
            // "Reinstall" is what puts back a loader whose files were damaged — reachable only by
            // removing everything first, until now.
            return report.LoaderStanding is { UpdateAvailable: true } standing
                ? $"Update the loader to {standing.Available}"
                : "Reinstall the loader";
        }

        // Nothing installed: offered as soon as something could be. The picker beside it says
        // which one, so the button does not repeat the name and cannot drift from the choice.
        return report.EligibleLoaders.Count > 0 ? "Install the loader" : null;
    }

    /// <summary>
    /// Writes the defaults into a game that already has the mod, without reinstalling anything.
    ///
    /// The case it serves: somebody changed a default and wants this game to follow. Reinstalling
    /// the plugin to move one setting would be a download and a receipt rewrite for nothing.
    /// </summary>
    private async Task ApplyDefaultsAsync(GameReport report, LoaderDescriptor descriptor,
                                          GamePreference preference)
    {
        Busy(true, "Applying your settings...");

        var target = GameLanguages.TargetFor(report, descriptor, _settings.ResolveTargetLanguage());

        var result = new GameConfigWriter()
            .Apply(report.Game.Path, descriptor, _settings.Current, target, perGame: preference);

        Busy(false, "Ready.");

        await MessageAsync(
            result.Written ? "Applied" : "Nothing was changed",
            result.Written
                ? $"Applied to {report.Game.Name}: {string.Join(", ", result.Applied)}."
                : $"Your settings could not be written ({result.Failure}).");

        await ShowSelectedAsync();
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

        // The list appears once somebody asks for it, and everything in it starts ticked. Ticking
        // the box above IS the decision to remove the data; the list exists to take part of it
        // back - a font pack, an old backup - not to make somebody assemble a deletion from
        // nothing. Hidden until then, because most uninstalls never open it.
        var picker = UserDataPicker(report);
        if (picker is not null)
        {
            content.Children.Add(picker.View);
            picker.View.IsVisible = false;
            dataBox.IsCheckedChanged += (_, _) => picker.View.IsVisible = dataBox.IsChecked == true;
        }

        if (!await ConfirmAsync($"Uninstall from {report.Game.Name}?", content, "Uninstall")) return;

        var chosenData = dataBox.IsChecked == true ? picker?.Chosen() : null;

        // Asked again, and only here: everything above was a form. This is the last moment before
        // files leave the disk, and the sentence NAMES what goes rather than asking "are you
        // sure" - somebody told "12 files, including your translation" can decide; somebody asked
        // "are you sure" can only guess.
        if (dataBox.IsChecked == true && chosenData is { Count: > 0 })
        {
            var summary = $"{chosenData.Count} file(s) will be deleted from {report.Game.Name}, "
                        + "including anything captured while playing that was never uploaded. "
                        + "A copy is kept aside first.";

            if (!await ConfirmAsync("Delete this game's data?", summary, "Delete them")) return;
        }

        Busy(true, "Removing...");
        var outcome = engine.Apply(report.Game, new UninstallChoice(
            RemovePlugin: true,
            RemoveLoader: loaderBox.IsChecked == true,
            RemoveUserData: dataBox.IsChecked == true,
            UserDataFiles: chosenData));
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

    /// <summary>The list, and a way to read back what stayed ticked.</summary>
    private sealed record DataPicker(Control View, Func<IReadOnlyList<string>> Chosen);

    /// <summary>
    /// Every file the mod wrote into this game, grouped, each with its own tick.
    ///
    /// Groups rather than a flat list, because "my data" is several things with nothing in common:
    /// a translation may be work nobody else has, fonts rebuild themselves in seconds, a config is
    /// two minutes of settings. One tick for all three made somebody choose between keeping a
    /// stale config and losing months of captured lines.
    ///
    /// A group tick reflects its files rather than only driving them: untick one file and the
    /// group shows a partial state, which is what says at a glance that a collapsed section is no
    /// longer "everything".
    /// </summary>
    private DataPicker? UserDataPicker(GameReport report)
    {
        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) return null;

        var groups = UserDataInventory.Scan(report.Game.Path, descriptor);
        if (groups.Count == 0) return null;

        var boxes = new List<CheckBox>();
        var panel = new StackPanel { Spacing = 6 };

        foreach (var group in groups)
        {
            var files = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(22, 4, 0, 0) };
            var groupBoxes = new List<CheckBox>();

            foreach (var item in group.Items)
            {
                var box = new CheckBox
                {
                    IsChecked = true,
                    Tag = item.RelativePath,
                    Content = new TextBlock
                    {
                        // Red says "this leaves the disk", which a list of paths does not.
                        Text = $"{item.RelativePath}   {UserDataInventory.Describe(item.Bytes)}",
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush("StatusError"),
                    },
                };

                groupBoxes.Add(box);
                boxes.Add(box);
                files.Children.Add(box);
            }

            var header = new CheckBox
            {
                IsChecked = true,
                IsThreeState = true,
                Content = new TextBlock
                {
                    Text = $"{group.Label} - {group.Items.Count} file(s), "
                         + UserDataInventory.Describe(group.Bytes),
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                },
            };

            var settling = false;

            header.IsCheckedChanged += (_, _) =>
            {
                // Null is how a mixed state is DISPLAYED, never a command: driving the files from
                // it would wipe the very selection that produced it.
                if (settling || header.IsChecked is null) return;

                foreach (var box in groupBoxes) box.IsChecked = header.IsChecked;
            };

            foreach (var box in groupBoxes)
            {
                box.IsCheckedChanged += (_, _) =>
                {
                    settling = true;

                    var ticked = groupBoxes.Count(b => b.IsChecked == true);
                    header.IsChecked = ticked == groupBoxes.Count ? true
                                     : ticked == 0 ? false
                                     : null;

                    settling = false;
                };
            }

            var body = new StackPanel { Spacing = 0 };
            body.Children.Add(new TextBlock
            {
                Text = group.Consequence,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(22, 0, 0, 0),
                Foreground = Brush("TextMuted"),
            });
            body.Children.Add(files);

            panel.Children.Add(new Expander
            {
                Header = header,
                Content = body,

                // Open on what cannot be got back, closed on what rebuilds itself: the one thing
                // somebody must actually look at should not need a click to be seen.
                IsExpanded = group.Label is "Translation" or "Replacement images",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            });
        }

        var view = new Border
        {
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(10),
            Child = new ScrollViewer { MaxHeight = 260, Content = panel },
        };

        return new DataPicker(view,
            () => boxes.Where(b => b.IsChecked == true)
                       .Select(b => (string)b.Tag!)
                       .ToList());
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
    private static Control Callout(string text, string backgroundKey, string edgeKey) =>
        Callout(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("TextPrimary"),
        }, backgroundKey, edgeKey);

    /// <summary>
    /// The same notice, around something richer than a sentence — a list, a button, both.
    ///
    /// ⚠ One shape for every notice on this screen, and it had drifted into three: the blockers
    /// used this, the configuration differences built their own Border with a full outline and a
    /// different radius, and the newest warnings were dressed as plain cards, which made a problem
    /// look like a section. A notice is recognised by its edge before it is read; three edges mean
    /// nothing is recognised at all.
    /// </summary>
    private static Control Callout(Control content, string backgroundKey, string edgeKey) => new Border
    {
        Background = Brush(backgroundKey),
        BorderBrush = Brush(edgeKey),

        // The left rule, not a box: it reads as a margin note against the cards it sits between,
        // and an outlined rectangle inside another outlined rectangle reads as a dialog.
        BorderThickness = new Avalonia.Thickness(3, 0, 0, 0),
        CornerRadius = new Avalonia.CornerRadius(4),
        Padding = new Avalonia.Thickness(12, 9),
        Child = content,
    };
}
