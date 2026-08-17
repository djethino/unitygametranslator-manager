using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
    /// Which build of that loader to install, read the same way and at the same moment.
    ///
    /// Null means "whatever the catalog pins" and is the ordinary case: resolution only happens
    /// once somebody opens "Use another build", because asking two publishers what they currently
    /// offer, on every card that gets drawn, would burn an unauthenticated GitHub's sixty requests
    /// an hour on a machine with a large library — to answer a question that changes a few times
    /// a year.
    /// </summary>
    private Func<LoaderBuild?> _chosenBuild = () => null;

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
        ShowScanning();

        _sweep?.Cancel();

        var result = await Task.Run(() => new CatalogProvider(_platform).Get());
        _catalog = result.Document;
        // Asked once and shared by every report built from here — see PluginReleases. Forgotten
        // first because reaching this method IS the gesture that means "look again": a rescan
        // that re-read the drives and kept yesterday's idea of the newest plugin would be a
        // refresh button that refreshes some things.
        _releases.Forget();

        // ⚠ The token goes with the search, and only so the answer carries this account's own vote.
        // Without it every arrow drew neutral whatever somebody had chosen, so a second click
        // withdrew the vote they meant to confirm.
        _inventory = new GameInventory(_platform, _catalog, new CatalogApiClient(),
                                       _settings.Current.ApiToken)
        {
            Lineages = _lineages,
            // ⚠ Same answer as the loader lookup: both are "what newer build exists for a game",
            // asked before anybody clicked. Leaving one on while the other is off would make the
            // setting mean half of what it says.
            Releases = _settings.Current.OnlineMode && _settings.Current.CheckContentUpdates
                ? _releases
                : null,
            Channel = string.Equals(_settings.Current.Channel, "beta", StringComparison.OrdinalIgnoreCase)
                ? ReleaseChannel.Beta
                : ReleaseChannel.Stable,

            // Which BepInEx 6 stream to measure "up to date" against. Read once here rather than
            // per report: the settings window rebuilds the reports when it changes.
            BepInEx6Channel = _settings.Current.BepInEx6Channel,
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

        // ⚠ **After the window is up, and it redraws when the answer lands.** Asking two
        // publishers what they currently offer takes a second or two, and nothing on screen needs
        // it to be usable — so it must not hold the list back. But a card drawn before the answer
        // arrives shows a loader without its version and would keep showing it until something
        // else caused a redraw: an interface that is only right if you happen to click twice.
        _ = WarmLoaderBuildsAsync();

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

        // 🔴 **The row builds its OWN report, so anything it wants must be filled in here.** That
        // is why a newer loader appeared on the game's page and nowhere in the list: BuildReportAsync
        // works this out, and this method deliberately does not call it — one file read per game
        // instead of a network round trip.
        //
        // ⚠ Comparing versions costs nothing: the catalog is already in memory, and the loader's
        // version was just read off the disk by the probe above. The promise this method makes is
        // "no network", not "no thinking".
        report.InstalledLoader = detected;

        if (detected is not null && descriptor is not null)
        {
            // ⚠ The build resolved for the chosen channel, not the catalogue's pin. The pin is a
            // fallback that ages on purpose; comparing against it said "up to date" beside a
            // picker offering a newer build. Cache-only, so this stays a dictionary lookup on the
            // drawing path.
            report.LoaderStanding = new VersionStanding(
                detected.Version,
                LoaderBuildResolver.Known(descriptor, _settings.Current.BepInEx6Channel)?.Version
                    ?? descriptor.Version);

            // Who installed it, which decides whether the row may say "update available" plainly
            // or has to add "(not ours)". Read from the receipt, exactly as BuildReportAsync does.
            var receipt = ReceiptStore.Read(game.Path);
            detected.InstalledByUs = receipt?.Loader is { InstalledByUs: true } ours
                                     && string.Equals(ours.Id, detected.Id, StringComparison.OrdinalIgnoreCase);

            report.LoaderAdopted = _preferences.Read(game.Path).AdoptLoader;
        }

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

        // ⚠ Only when the account's lineages have actually been read. Unknown and none look
        // identical from here, and announcing "nobody is waiting" on that basis would be a guess
        // dressed as a fact — the reason AccountLineages exposes Known at all.
        var waiting = _lineages.Known
            ? _lineages.For(report.LocalTranslation?.Uuid)?.BranchesCount
            : null;

        return (SituationReader.Read(report, language, checkedOnline, waiting,
                                     _settings.Current.ApiUser), mine, account);
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

        ToolTip.SetTip(button, "Open your account on the UGT Website");
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
        // Six shades of the product's own palette rather than six hexadecimals of nobody's: these
        // were Tailwind v3 values, which the site has not used since it moved to v4.
        var hues = new[]
        {
            Common.Theme.Accent, Common.Theme.QualityValidated, Common.Theme.QualityHuman,
            Common.Theme.QualityAi, Common.Theme.AccentEdge, Common.Theme.TagModUi,
        };
        var hue = hues[Math.Abs(hash) % hues.Length];

        return new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new Avalonia.CornerRadius(13),
            Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromRgb(hue.R, hue.G, hue.B)),
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
        var window = new ToolSettingsWindow(_platform, _settings, found, _catalog);
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
        // ⚠ Each entry gets its own control, never a shared one: a control belongs to one place in
        // the tree, and the closed box renders the selected entry a second time. Handing it the
        // same instance empties whichever of the two claimed it first.
        var detected = Languages.FromLocale(_platform.SystemLanguage());
        var autoName = detected is not null ? Languages.NameOf(detected) : null;
        var autoLabel = autoName is not null ? $"{autoName} (system language)" : "System language";

        // 🔴 **A template, not a Control per item.** A ComboBox renders the SELECTED entry a second
        // time, in its closed box — and a control belongs to one place in the tree, so handing the
        // same instance to both empties whichever claimed it first. The template is asked for a
        // fresh one each time it is needed, which is the whole difference.
        LanguageBox.ItemTemplate = new FuncDataTemplate<LanguageChoice>(
            (choice, _) => LanguageMark.Named(choice?.Name, choice?.Label), supportsRecycling: false);

        LanguageBox.Items.Add(new LanguageChoice("auto", autoName, autoLabel));

        foreach (var (code, name) in Languages.All())
            LanguageBox.Items.Add(new LanguageChoice(code, name, name));

        var current = _settings.Current.TargetLanguage;
        foreach (var choice in LanguageBox.Items.OfType<LanguageChoice>())
        {
            if (string.Equals(choice.Code, current, StringComparison.OrdinalIgnoreCase))
            {
                LanguageBox.SelectedItem = choice;
                break;
            }
        }
        LanguageBox.SelectedItem ??= LanguageBox.Items.OfType<LanguageChoice>().FirstOrDefault();

        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (LanguageBox.SelectedItem is not LanguageChoice { Code: var code }) return;
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
    /// <param name="redraw">
    /// Rebuild the card even when the GAME says the same thing as before.
    ///
    /// 🔴 **Because two different things can change.** This method answers "did the game change?"
    /// and skips the redraw when it did not — right for a file saved with nothing new in it. But a
    /// browser session ending changes nothing about the game and everything about this WINDOW: the
    /// button that said "Stop browser session" has no session to stop. The card then kept the old
    /// label until somebody selected another game and came back, which is exactly what was
    /// reported.
    /// </param>
    private async Task RereadAsync(GameInstall game, bool redraw = false)
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
        //
        // ⚠ Unless the caller knows something the game does not say. See the parameter.
        if (!redraw && before is not null && before.Headline == now.Headline
            && before.Pending == now.Pending
            && before.Detail == now.Detail)
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
            or Situation.UnpublishedWork

            // A game in conflict is emphatically set up — it has a translation on both sides. It
            // would have dropped out of the "Set up" lens the day that state was split out.
            or Situation.Conflict;

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

            // ⚠ ONE line for every secondary signal, joined by the Core — "2 contributions
            // waiting · mod update available". A game whose translation needs attention still
            // deserves to say that contributors are waiting on it; the row used to rank the two
            // and drop the loser. But giving each its own line would put four under some names,
            // and each would be read less carefully than the one above it.
            //
            // Coloured as something available rather than something wrong: nothing here is at
            // risk, and a notice that shouts trains people to ignore the ones that matter.
            if (situation.Pending is { Length: > 0 } behind)
            {
                body.Children.Add(new TextBlock
                {
                    Text = behind,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("StatusInfo"),
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
            // 🔴 **A Grid, not a horizontal StackPanel — and this was the bug.** A horizontal
            // StackPanel measures its children with UNBOUNDED width, so every TextBlock in here
            // reported the width of its longest unbroken line and TextWrapping never fired. The
            // text then ran the full length of that line, straight under the Play button and the
            // account mark sitting in the column beside it.
            //
            // The comment that used to sit here claimed the text column "takes what is left". It
            // does now.
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

            var image = new Image
            {
                Source = icon,
                Width = 28,
                Height = 28,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Avalonia.Thickness(0, 2, 10, 0),
            };

            Grid.SetColumn(image, 0);
            row.Children.Add(image);

            body.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            Grid.SetColumn(body, 1);
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
            // 🔴 **Green said "you" on somebody else's account.** This mark was StatusSuccess
            // whoever the account was, and green is what the rest of this window uses for "good,
            // ready, go ahead" — so a game signed in as @somebody-else read exactly like one
            // signed in as the person at the keyboard. On that game the manager is READ-ONLY
            // (ServerIdentity): it will not publish, will not edit in the browser, will not merge.
            // The loudest, most encouraging colour sat on the one state where every act is
            // refused.
            //
            // ⚠ Not red either: nothing is wrong. Somebody else's game on a shared computer is
            // ordinary. Amber, because it changes what the buttons will do.
            var yours = People.IsYou(account, _settings.Current.ApiUser);

            var mark = new TextBlock
            {
                // ⚠ The word "(you)", not the colour, carries the answer — see People.Mention.
                Text = People.Mention(account, yours),
                FontSize = 10,
                Foreground = Brush(yours ? "StatusSuccess" : "StatusWarning"),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                MaxWidth = 130,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            ToolTip.SetTip(mark, yours
                ? $"This game is signed in to the site as {People.Mention(account, true)} — the "
                  + "account this tool is using. It can publish and contribute from inside the game."
                : $"This game is signed in to the site as {People.Mention(account)}, not as the "
                  + "account this tool is using. Nothing here will write to it: play it and look "
                  + "at it, and sign in inside the game to change that.");

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
    private Button? PlayButton(GameInstall game, bool small, GameReport? report = null)
    {
        if (_running.IsRunning(game)) return null;
        if (GameLaunch.RouteFor(game) is not { } route) return null;

        // ⚠ Only the full-size button says it. The mark in the list is a glyph with no room for
        // words, and giving it a tooltip nobody hovers would be a promise made to nobody.
        var promise = report is null
            ? PlayPromise.Plain
            : PlayPromises.For(report, GameConfig(report));

        var button = small
            ? new Button
            {
                Content = Glyphs.Play("StatusSuccess"),
                Padding = new Avalonia.Thickness(6, 2),
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            }
            : Glyphs.Button(Glyphs.Play("StatusSuccess"), PlayPromises.Label(promise));

        // Tonal green, and only on the full-size one: the small mark in the list has no fill at
        // all, so there is nothing there to tint. See the Button.play block in App.axaml for why
        // this is the single control in the application allowed a colour of its own.
        if (!small) button.Classes.Add("play");

        ToolTip.SetTip(button, small || report is null
            ? $"Start {game.Name}. {route.Why}"
            : $"Start {game.Name}. {PlayPromises.Explain(promise)} {route.Why}");

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
    /// The gear on the scanning panel while it is the panel, and null the rest of the time.
    ///
    /// Held so the status bar can feed its middle line — see <see cref="Status"/>. Null is what
    /// keeps that mirroring from reaching any other screen, which is why it is dropped by
    /// <see cref="ClearDetail"/> rather than by whoever happens to replace the panel.
    /// </summary>
    private SpinningGear? _scanGear;

    /// <summary>
    /// Empties the right-hand panel, and forgets what was live inside it.
    ///
    /// 🔴 **Every replacement of that panel goes through here.** Four places replace it, and a live
    /// control held by a field outlives three of them — the reference would still be there, still
    /// being written to, attached to nothing. One door means the next place added cannot forget.
    /// </summary>
    private void ClearDetail()
    {
        DetailPanel.Children.Clear();
        _scanGear = null;
    }

    /// <summary>
    /// The turning gear, alone in the middle, while the drives are being read.
    ///
    /// 🔴 **It replaced an instruction — "Select a game on the left." — and that line was wrong
    /// twice.** It asked for something nobody could do yet, since the list it points at is still
    /// being built; and a static sentence on an empty panel makes a tool that is working look like
    /// a tool that is waiting. The gear says the one thing that is true at that moment.
    ///
    /// ⚠ Its caption says what is being looked FOR, where the status bar says what is being read —
    /// the catalog, then the drives. Two sentences that do not repeat each other: one names the
    /// point of the wait, the other reports the step. The first is what somebody wants at the
    /// middle of an empty panel.
    ///
    /// ⚠ Only when nothing is selected. A rescan started from a game's card must not blank the card
    /// somebody is reading — the list refreshes underneath, and the card stays.
    /// </summary>
    private void ShowScanning()
    {
        if (_selected is not null) return;

        ClearDetail();

        // Centred like the overview that follows it, so the panel does not jump when one replaces
        // the other. See ShowOverview: the panel IS the scroll viewer's content, so aligning it is
        // enough — nothing needs wrapping.
        DetailPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        DetailPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        DetailPanel.MaxWidth = SummaryWidth;

        // The band belongs to a game, and there is none yet.
        ActionBar.IsVisible = false;
        ActionBar.Content = null;
        OverviewTop.IsVisible = false;

        // Large and stacked: this is the only thing on the panel, so it is the panel's subject
        // rather than a note in a corner of it.
        _scanGear = new SpinningGear("Looking for your games...", size: 72, stacked: true)
        {
            // What the status bar says right now, so the middle of the panel does not lag behind
            // the bottom of the window between two phases.
            Detail = StatusText.Text ?? "",
        };

        DetailPanel.Children.Add(_scanGear);
    }

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

        ClearDetail();

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

        ClearDetail();
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

        // 🔴 **The row is re-read from the SAME report, here, for every caller.**
        //
        // Twenty-three places redraw the card after doing something — installing a loader,
        // removing a translation, adopting a loader — and eight of them also refreshed the row.
        // The other fifteen left it saying what was true before the click: update a loader from
        // the card and the list went on offering the update. Fixing them one by one would have
        // left the sixteenth to be found by somebody else.
        //
        // ⚠ It costs nothing: the report was just built, and SituationReader reads it rather than
        // the disk. RereadAsync still exists for the other direction — a game that changed while
        // nobody was looking, where there is no report to reuse.
        RefreshRowFrom(report);

        RenderReport(report);
    }

    /// <summary>Puts what a freshly built report says onto this game's row in the list.</summary>
    private void RefreshRowFrom(GameReport report)
    {
        var game = report.Game;

        var waiting = _lineages.Known
            ? _lineages.For(report.LocalTranslation?.Uuid)?.BranchesCount
            : null;

        _situations[game.Path] = SituationReader.Read(
            report, _settings.ResolveTargetLanguage(),
            onlineChecked: report.OnlineChecked || !_settings.Current.OnlineMode,
            branchesWaiting: waiting,
            signedInAs: _settings.Current.ApiUser);

        if (_rows.TryGetValue(game.Path, out var row) && row.Item.Tag is GameInstall shown)
        {
            row.Item.Content = BuildRowContent(shown);
            _rows[game.Path] = (Signature(game.Path), row.Item);
        }
    }

    private void RenderReport(GameReport report)
    {
        var game = report.Game;
        ClearDetail();

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

        // ⚠ Settled BEFORE the tabs split, and it used to live in the Setup branch alone. The bar
        // reads it, and the bar now exists on Home too: left where it was, a game opened on Home
        // would have been offered the answer computed for whichever game was looked at last.
        var offer = TranslationOffers.For(report, PickTranslation(report));
        _takeTranslation = TranslationOffers.MayDefaultToYes(offer)
                           && _preferences.Read(report.Game.Path).InstallTranslation;

        if (_gameTab == GameTab.Home)
        {
            foreach (var control in GameHome(report)) DetailPanel.Children.Add(control);

            // ⚠ The bar belongs to BOTH tabs, where it used to be hidden here on the argument that
            // Home offers one way forward at a time. What settles it is Play: wanting to start the
            // game has nothing to do with which tab is open, and Home — the "where does this game
            // stand" tab — is exactly where it was missing. A bar that appears and disappears
            // between tabs also changes the height of the content on every switch.
            //
            // The competition that argument feared is real, and it is answered in GameHome: while
            // this bar has something to do, the buttons in the body drop to the outlined register.
            ShowActionBar(report);
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

        // ⚠ ONE filled button on screen at a time, and the bar has first claim on it: it is the
        // fixed place, in the same spot on both tabs, and what it runs is the whole job rather
        // than a step of it. These open a list to choose from — a refinement of what one click
        // would take by itself — so they step down to the outlined register while there is
        // anything for that click to do.
        //
        // Not a constant: on a game with nothing left to install, choosing a translation IS the
        // act of this screen, and it takes the fill back. Same reading either way — the loudest
        // thing on the card is the thing to do next.
        var barActs = OneClickSteps(report, _preferences.Read(report.Game.Path)).Any();
        var bodyLead = barActs ? "" : "primary";

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

            // ⚠ The measured figure when there is one, and the mod's counter only otherwise. That
            // counter describes what the MOD did — a file edited from a browser or by hand carries
            // a number that stopped describing it. Silent when neither can be trusted: a count
            // nobody can vouch for is worse than none.
            var unpublished = local.ChangedSinceAncestor ?? (local.SourceHash is null ? null : local.LocalChanges);

            var detail = local.EntryCount < 0
                ? "The file could not be read."
                : $"{local.EntryCount} entries"
                  + (unpublished > 0 ? $", {unpublished} never uploaded" : "");

            body.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
            });

            // What it is made of, whose it is, and what can be done with it — the three questions
            // this tab exists for, and none of them was answered here.
            foreach (var control in TranslationMakeup(report, local)) body.Children.Add(control);
            foreach (var control in LineageNotes(report)) body.Children.Add(control);
            foreach (var control in TranslationWorkbench(report, heading: false)) body.Children.Add(control);

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
                    Classes = { bodyLead },
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

                    // Second of the two, so it never leads: the wider net is the fallback, and
                    // it only carries the lead when it is the only button here.
                    Classes = { inMyLanguage.Count > 0 ? "" : bodyLead },
                };

                anyLanguage.Click += async (_, _) => await OpenTranslationsAsync(report, anyLanguage: true);
                buttons.Children.Add(anyLanguage);
            }

            // 🔴 **Applying sits in the SAME box as choosing, pushed to the right.**
            //
            // A choice made here that could only be carried out on the other tab left people
            // unable to say what was applied and what was merely picked: the card went on
            // describing the old translation, and the only control that mentioned the new one was
            // a tick-box in the one-click, under Set up. Choose and apply are two halves of one
            // gesture, so they share a line — the choice on the left where it is made, the
            // consequence on the right where a reader looks for the verb.
            // ⚠ **Above the buttons, not under them.** What is waiting has to be readable BEFORE
            // the verb that acts on it: a reader whose eye lands on "Apply (1)" and has to look
            // below to learn what it applies has already been asked to press something unnamed.
            if (PendingTranslationNote(report) is { } waitingNote) question.Children.Add(waitingNote);

            var choiceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            Grid.SetColumn(buttons, 0);
            choiceRow.Children.Add(buttons);

            if (PendingTranslationActions(report) is { } waiting)
            {
                Grid.SetColumn(waiting, 2);
                choiceRow.Children.Add(waiting);
            }

            question.Children.Add(choiceRow);

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

        // ── Chosen, not yet in the game ───────────────────────────────────────────────────────
        //
        // 🔴 **A choice that leaves no trace is a choice nobody can trust.** Picking a translation
        // in the list wrote a preference and said "the game's card is where you install it" — a
        // sentence that names no card, no button and no next step. Coming back here, the card went
        // on describing the OLD translation, with nothing pending, nothing to apply and nothing to
        // undo. The only control that mentioned it was a tick-box in the one-click, and only on
        // the other tab.
        //
        // ⚠ It reads Apply (1) because that is what every pending change in this program reads,
        // and being told twice in two different shapes is how somebody ends up unsure which one
        // counts. Undo sits beside it: a choice one cannot take back is not a choice, it is a
        // commitment made by accident.
        //
        // Both live in the box above, beside the buttons that make the choice — see the grid in
        // PendingTranslationActions.

        // ── The one thing to do next ──────────────────────────────────────────────────────────
        var next = new StackPanel { Spacing = 6 };
        var installed = report.InstalledPluginVersion is not null;

        // What Set up would have to do, said here rather than found there. "Up to date" is worth
        // as much as a pending update: it is the answer to "do I need to go and look".
        var pending = new List<string>();
        if (report.InstalledLoader is null) pending.Add("the loader");
        else if (report.LoaderUpdateOffered) pending.Add("a newer loader");

        // ⚠ A newer loader we may NOT touch is still worth knowing about, and this tab said
        // nothing at all: the row now announces it, and somebody arriving here found a card
        // claiming everything was up to date. Said as a fact with the way out, not as a task —
        // the permission is a per-game answer, and Set up is where it is given.
        var loaderTheirs = !report.LoaderUpdateOffered
                           && report.InstalledLoader is { InstalledByUs: false }
                           && report.LoaderStanding is { UpdateAvailable: true };

        if (!installed) pending.Add("the mod");
        else if (report.PluginStanding is { UpdateAvailable: true }) pending.Add("a newer mod");

        next.Children.Add(new TextBlock
        {
            Text = pending.Count == 0
                ? (loaderTheirs
                    ? "The mod is installed and up to date."
                    : "The loader and the mod are installed and up to date.")
                : $"Needs {string.Join(" and ", pending)}.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(pending.Count == 0 && !loaderTheirs ? "StatusSuccess" : "TextSecondary"),
        });

        if (loaderTheirs && report.LoaderStanding is { } theirs)
        {
            next.Children.Add(new TextBlock
            {
                Text = $"{report.InstalledLoader!.Display} {theirs.Installed} → {theirs.Available} "
                     + "is out. It was not installed from here, so updating it has to be allowed "
                     + "first — in Set up.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusInfo"),
            });
        }

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
            // The flags lead, then the same sentence in words. ⚠ Both, not one or the other: a
            // flag is faster to scan and cannot always name the language on its own — ten Indian
            // languages share one — so the words stay and the pictures are added in front.
            var pair = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };

            pair.Children.Add(LanguageMark.Named(owned.SourceLanguage));
            pair.Children.Add(new TextBlock
            {
                Text = "→",
                FontSize = 12,
                Foreground = Brush("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            pair.Children.Add(LanguageMark.Named(owned.TargetLanguage));

            pair.Children.Add(new TextBlock
            {
                Text = $"· {owned.LineCount} lines",
                FontSize = 12,
                Foreground = Brush("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            body.Children.Add(pair);
        }

        body.Children.Add(new TextBlock
        {
            Text = installedUuid is null
                ? "This game has no translation file yet."
                : "The file in this game is a different one. Yours is still on the site, untouched.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        });

        var take = new Button
        {
            Content = mine.Count == 1 ? "Restore my published translation" : "Choose which one to install",
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

    /// <summary>
    /// The translation chosen in the list but not yet in the game, or null when none is waiting.
    ///
    /// ⚠ **Deliberate choices only.** Without a stored TranslationId the tool is merely proposing
    /// the best-ranked one in the reader's language, and calling that "pending" would invent a
    /// decision nobody took. What is proposed is already said above, in the muted register.
    ///
    /// ⚠ Silent while the chosen one IS the one installed: a card claiming something is pending
    /// when the game already runs it teaches people to ignore the notice.
    /// </summary>
    private OnlineTranslation? TranslationWaiting(GameReport report)
    {
        if (_preferences.Read(report.Game.Path).TranslationId is not { } chosenId) return null;

        var picked = report.OnlineTranslations.FirstOrDefault(t => t.Id == chosenId)
                     ?? (report.MatchingOnline is { } main && main.Id == chosenId ? main : null);

        if (picked is null) return null;

        return report.LocalTranslation is not null
               && TranslationOffers.For(report, picked) == TranslationOffer.AlreadyInPlace
            ? null
            : picked;
    }

    /// <summary>
    /// Apply and Undo, for the right-hand side of the line where translations are chosen.
    ///
    /// ⚠ It reads Apply (1) because that is what every pending change in this program reads. Two
    /// shapes for the same idea is how somebody ends up unsure which one counts. And Undo sits
    /// beside it: a choice one cannot take back is not a choice, it is a commitment by accident.
    /// </summary>
    private Control? PendingTranslationActions(GameReport report)
    {
        if (TranslationWaiting(report) is not { } picked) return null;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var ready = report.InstalledPluginVersion is not null && !_running.IsRunning(report.Game);

        // ⚠ Marked like every other action that writes. Apply is not a lesser verb because it
        // carries a count: it puts a file into a game, and where a write lands is the first thing
        // this interface promises to say. Local — the site holds whatever it held before.
        var apply = ScopeMark.Marked(EditSide.Local, "Apply (1)", ready);
        apply.Classes.Add("primary");

        // No greyed control without words — the rule this program holds everywhere.
        ToolTip.SetTip(apply, report.InstalledPluginVersion is null
            ? "The mod is not installed in this game yet, so there is nowhere to put a translation."
            : _running.IsRunning(report.Game)
                ? "This game is open. The mod rewrites its translation file from memory while it "
                  + "runs, so anything written now would be replaced without warning."
                : $"Puts {picked.SourceLanguage} → {picked.TargetLanguage} by "
                  + $"{People.MentionOf(picked.Author, _settings.Current.ApiUser)} into this game.");

        var replacing = TranslationOffers.For(report, picked)
            is TranslationOffer.ReplacesWork or TranslationOffer.ReplacesChoice;

        apply.Click += async (_, _) => await TakeSelectedTranslationAsync(report, picked, replacing);
        row.Children.Add(apply);

        var undo = new Button { Content = "Undo", FontSize = 12 };
        ToolTip.SetTip(undo, "Forgets this choice. The game keeps whatever it runs now.");

        undo.Click += async (_, _) =>
        {
            var current = _preferences.Read(report.Game.Path);
            current.TranslationId = null;
            _preferences.Set(report.Game.Path, current);
            await ShowSelectedAsync();
        };

        row.Children.Add(undo);
        return row;
    }

    /// <summary>
    /// What is waiting, and what applying it costs — two short lines, never one long one.
    ///
    /// 🔴 **Concrete, and counted.** The first version read "Applying there is work here that has
    /// never been uploaded": a clause written to sit inside another sentence, pasted after a verb,
    /// producing something that is neither grammatical nor an answer. What somebody needs before
    /// pressing Apply is a number and a fate — how many of their lines are involved, and what
    /// happens to them.
    ///
    /// ⚠ Two TextBlocks rather than one: the identity of the translation and the cost of taking it
    /// are two facts, and a reader scanning for the second should not have to find it at the end
    /// of the first. It also stops the whole thing running as one unreadable line.
    /// </summary>
    private Control? PendingTranslationNote(GameReport report)
    {
        if (TranslationWaiting(report) is not { } picked) return null;

        var lines = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(0, 6, 0, 0) };

        // What identifies a translation to somebody about to install it: the pair of languages,
        // who made it, and how big it is — the three the list they chose from showed.
        var size = picked.LineCount > 0 ? $", {picked.LineCount} lines" : "";

        lines.Children.Add(new TextBlock
        {
            Text = $"Chosen: {picked.SourceLanguage} → {picked.TargetLanguage} by "
                 + $"{People.MentionOf(picked.Author, _settings.Current.ApiUser)}{size}. "
                 + "Not in the game yet.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("StatusInfo"),
        });

        if (Cost(report) is { } cost)
        {
            lines.Children.Add(new TextBlock
            {
                Text = cost,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
            });
        }

        return lines;

        // ⚠ The same figure the card shows two blocks above — measured against the ancestor, or
        // the mod's own counter when there is nothing to measure against. Two numbers for one
        // thing on one screen is how somebody stops believing either.
        string? Cost(GameReport r)
        {
            if (r.LocalTranslation is not { } local || local.EntryCount <= 0) return null;

            var unpublished = local.ChangedSinceAncestor
                              ?? (local.SourceHash is null ? null : local.LocalChanges);

            // 🔴 **Says LOCAL first, because the sentence is frightening without it.** Somebody who
            // leads a Main reads "applying removes 3231 lines" and has every reason to think their
            // published translation is at stake. It is not: this writes one file in one game, and
            // the site is untouched — which is exactly what makes switching safe once published.
            var count = unpublished > 0
                ? $"{local.EntryCount} lines, {unpublished} never uploaded"
                : $"{local.EntryCount} lines";

            var published = _lineages.For(local.Uuid) is not null
                ? " Your published translation is untouched."
                : "";

            return $"Replaces this game's file only ({count}). A copy is kept aside.{published}";
        }
    }

    /// <summary>
    /// How many lines this game holds that were never published, or null when nobody can say.
    ///
    /// ⚠ The measured figure first, the mod's counter only as a fallback — the same order the card
    /// uses when it prints "N never uploaded". One screen must not carry two numbers for one fact.
    /// </summary>
    private static int? Unpublished(GameReport report)
    {
        if (report.LocalTranslation is not { } local) return null;

        var count = local.ChangedSinceAncestor
                    ?? (local.SourceHash is null ? null : local.LocalChanges);

        return count > 0 ? count : null;
    }

    /// <summary>
    /// Asks each publisher what it currently offers, in the background, and redraws what changed.
    ///
    /// ⚠ Silent on failure: not knowing a version is the state every screen already handles, and a
    /// notice about a background lookup nobody asked for would be noise on the one screen somebody
    /// opened to look at their games.
    ///
    /// ⚠ Skipped entirely when online mode is off — that setting is a promise that no call is
    /// made, not a preference about speed.
    /// </summary>
    private async Task WarmLoaderBuildsAsync()
    {
        // Two promises, and both are kept here: online mode means no call at all, and this
        // setting means no call made before anybody asked.
        if (!_settings.Current.OnlineMode || !_settings.Current.CheckContentUpdates) return;

        try
        {
            await new LoaderBuildResolver()
                .WarmAsync(_catalog, _settings.Current.BepInEx6Channel)
                .ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        // Redrawn because the answer changes what a card says. Only the card: the list rows carry
        // no build version, so refreshing them would be work nobody sees.
        if (_selected is not null) await ShowSelectedAsync();
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

    /// <summary>
    /// One line saying what is installed and, when it is behind, what is published.
    ///
    /// ⚠ Silent when the lookup failed. "0.11.0" with nothing after it means "we did not find out",
    /// and that is the honest rendering — appending "up to date" to a request that never arrived
    /// is the one sentence this screen must never produce.
    /// </summary>
    private static string Published(string installed, VersionStanding? standing) =>
        standing is { UpdateAvailable: true }
            ? $"{installed}  ·  {standing.Available} available"
            : standing is { IsInstalled: false, Available: { } offered }
                ? $"{installed}  ·  {offered} would be installed"
                : installed;

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
            // ⚠ What is here AND what is published, on one line each. This block is the answer to
            // "where does this game stand", and it gave half of it: the installed version alone
            // says nothing without the one it should be compared to. Said for a loader we did not
            // install too — that it is not ours to update does not make its version a secret.
            ("Mod loader", Published(report.InstalledLoader is null
                ? "none installed"
                : $"{report.InstalledLoader.Display} {report.InstalledLoader.Version ?? ""}".Trim(),
                report.LoaderStanding)),
            ("Plugin", Published(report.InstalledPluginVersion ?? "not installed",
                                 report.PluginStanding)),
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
        if (report.MyPosition is not { } position)
        {
            // 🔴 **The third case, which used to be silence.** Leading a lineage was green,
            // contributing to one was amber, and running somebody else's work said nothing at all
            // — so it was told apart from the other two by an ABSENCE, and from "not loaded yet"
            // by nothing whatever. The name on the card looked identical in all three.
            //
            // ⚠ Only when we actually KNOW there is no part to hold: the listing read, an account
            // signed in, and a published author who is not that account. Any of those missing and
            // this stays silent, because the alternative is a guess printed as a fact.
            if (_lineages.Known
                && !string.IsNullOrWhiteSpace(_settings.Current.ApiUser)
                && report.MatchingOnline is { Author: { Length: > 0 } author } theirs
                && !People.IsYou(author, _settings.Current.ApiUser))
            {
                // The way on depends on what that person decided — the same flag their card shows
                // as "Accepts contributions" or "Solo work". Naming only the wall would leave
                // somebody to discover the door by trying it.
                var onward = theirs.AcceptsBranches == true
                    ? " They take contributions: your changes can be sent to them for review."
                    : theirs.AcceptsBranches == false
                        ? " They work alone: publish your own version to take it further."
                        : "";

                yield return new TextBlock
                {
                    // Muted, not green and not amber: those two carry a POWER over the file —
                    // reviewing contributions, having yours reviewed. Using somebody's work
                    // carries none, and colouring it like the others would say it does.
                    Text = $"{People.Mention(author)}'s translation, and you hold no part in it."
                         + onward,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("TextMuted"),
                    Margin = new Avalonia.Thickness(0, 2, 0, 0),
                };
            }

            yield break;
        }

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
            foreach (var control in TranslationMakeup(report, local)) panel.Children.Add(control);
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
                    ? $"Chosen for this game: {picked.SourceLanguage} → {picked.TargetLanguage} by {People.MentionOf(picked.Author, _settings.Current.ApiUser)}."
                    : $"Setting this game up would take: {picked.SourceLanguage} → {picked.TargetLanguage} "
                      + $"by {People.MentionOf(picked.Author, _settings.Current.ApiUser)} — the best ranked in your language.",
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

        foreach (var control in TranslationWorkbench(report)) panel.Children.Add(control);
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
    /// What a translation file is MADE OF: the stage it has reached, and the bar that shows the
    /// five buckets.
    ///
    /// ⚠ Written once and shown on both tabs. It used to live inside the Set up card alone, which
    /// meant the one screen answering "where does this game stand" — Home — could not say whether
    /// the translation it announced was any good.
    ///
    /// ⚠ Counted from the file on disk, never taken from the published figures of the same lineage:
    /// the moment somebody plays, their copy stops being the published one, and borrowing its
    /// numbers would describe a stranger's file under the words "on this machine".
    /// </summary>
    private IEnumerable<Control> TranslationMakeup(GameReport report, LocalTranslation local)
    {
        if (local.Counts is not { } counts || !QualityBar.HasSomethingToShow(counts)) yield break;

        // ⚠ **What this translation IS, before what it is made of.** Two earlier attempts sat here
        // and both answered the wrong question: the edit-scope switch, which aims an action rather
        // than describing a file, then a tag about where the figures were measured, which nobody
        // needs. What was missing is what the website has always shown on its cards — published or
        // not, Main or branch, up to date or not, reviewed or not, and what players made of it.
        yield return TranslationBadges.ForLocal(report, counts);

        if (QualityBar.StageOf(counts) is { } stage)
        {
            // The verdict first, the make-up under it: somebody wants to know where this stands
            // before they want to know what it is made of.
            yield return new TextBlock
            {
                Text = counts.Completeness is { } done
                    ? $"{stage} · {done * 100:F0}% of what it has met is settled"
                    : stage,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            };
        }
        else if (counts.IsCaptureOnly)
        {
            // Not "a translation at zero": nothing has been attempted. The difference decides
            // whether somebody is looking at work in progress or at the game's own text handed
            // back to them.
            yield return new TextBlock
            {
                Text = "Nothing translated yet — the mod has met "
                     + $"{counts.Captured} line(s) and is waiting on a translation for them.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            };
        }

        yield return new QualityBar(counts) { Margin = new Avalonia.Thickness(0, 6, 0, 2) };
        if (QualityBar.Legend(counts) is { } legend) yield return legend;
    }

    /// <summary>
    /// The edit session being followed for the selected game, and the token that stops it.
    ///
    /// ⚠ One at a time, deliberately. Two editors open on two games is a thing somebody would try
    /// once and never be able to reason about afterwards — which browser tab writes into which
    /// game — and the file being written is not a mistake worth risking for that.
    /// </summary>
    private EditSessionRunner? _editSession;
    private CancellationTokenSource? _editSessionStop;

    /// <summary>
    /// Open the browser editor on this game's translation and follow it until it closes.
    ///
    /// ⚠ The file is written as each save arrives, not once at the end: "saved" is the word the
    /// browser uses, and it has to mean the same thing here.
    /// </summary>
    private async Task OpenLocalEditorAsync(GameReport report, LoaderDescriptor descriptor, Button button)
    {
        if (_editSession is not null)
        {
            // ⚠ **Say something IMMEDIATELY.** Everything visible after this click happens in the
            // background follower's cleanup, and until it gets there the screen was identical to
            // before — click, nothing, click again, still nothing. A control that does not
            // acknowledge a press is indistinguishable from a broken one.
            button.IsEnabled = false;
            ScopeMark.SetLabel(button, "Stopping…");

            await StopLocalEditorAsync();

            // ⚠ And the state is put right HERE rather than trusted to the follower. That cleanup
            // is a fire-and-forget task: if it has already died — an error path, a session the
            // site dropped — nothing ever resets _editSession, the button keeps offering to stop
            // a session that is gone, and every further click cancels an already-cancelled token,
            // which is precisely "clicking does nothing".
            await StrandedEditorGuard(button);
            return;
        }

        // ⚠ Through SetLabel, never `button.Content = …`. These buttons carry the three scope marks
        // beside their label; assigning a string to Content would throw the marks away and leave the
        // one thing that says where the action writes missing for the rest of the session.
        button.IsEnabled = false;
        ScopeMark.SetLabel(button, "Checking…");

        var runner = new EditSessionRunner(_platform);
        var languages = LocalTranslationProbe.ReadLanguages(report.Game.Path, descriptor);

        // 🔴 **Before anything is uploaded.** Two browser editors on one translation erase each
        // other's saves — each holds the whole file and writes it back entire — and the site cannot
        // notice, because sessions are anonymous. Asking after the upload would mean the second
        // session already exists, which is the state this prevents.
        var blocking = await runner.FindBlockingAsync(report.Game, descriptor);
        var resuming = false;

        if (blocking is not null)
        {
            var agreed = await ConfirmationWindow.AskAsync(this,
                blocking.Ours ? "A session of yours is still open" : "Already being edited",
                blocking.Question,
                blocking.Ours ? "Pick it back up"
                              : blocking.ModKey is not null ? "End it and open mine"
                              : "Open mine anyway");

            if (!agreed)
            {
                button.IsEnabled = true;
                ScopeMark.SetLabel(button, "Edit in browser");
                return;
            }

            if (blocking.Ours)
            {
                resuming = runner.Resume(report.Game, descriptor, blocking.ModKey!);

                // There is nothing here to resume INTO — the translation file is gone since. End
                // that session rather than leaving it alive beside a new one nobody can reconcile.
                if (!resuming)
                    await runner.TakeOverAsync(report.Game, descriptor, blocking.ModKey!);
            }
            else if (blocking.ModKey is not null)
            {
                ScopeMark.SetLabel(button, "Ending the other one…");
                await runner.TakeOverAsync(report.Game, descriptor, blocking.ModKey);
            }
        }

        EditSession? session = runner.Current;

        if (!resuming)
        {
            ScopeMark.SetLabel(button, "Opening…");
            session = await runner.OpenAsync(report.Game, descriptor,
                                             languages.Source, languages.Target);
        }

        if (session is null)
        {
            button.IsEnabled = true;
            ScopeMark.SetLabel(button, "Edit in browser");
            await ConfirmationWindow.TellAsync(this, "The editor could not be opened",
                runner.LastError ?? "The site did not answer.");
            return;
        }

        _editSession = runner;
        _editSessionStop = new CancellationTokenSource();

        button.IsEnabled = true;
        ScopeMark.SetLabel(button, "Stop browser session");

        // ⚠ Not on a resume: the tab that is still open is already attached, and the URL that would
        // open a new one carried a one-time token that died when that page first loaded.
        if (!resuming) Shell.OpenUrl(session.Url);

        // ⚠ A session that opened WITH a complaint. The only one possible here is "the game folder
        // could not be marked", which costs the guarantee that the mod will not open a second
        // editor — silent success would be a lie about the one thing that protects the file.
        if (runner.LastError is { } warning)
            await ConfirmationWindow.TellAsync(this, "The editor is open, with one reservation",
                                               warning);

        var progress = new Progress<EditSessionProgress>(state =>
        {
            // Reported on the UI thread by Progress<T>; only the button's wording changes here, so
            // an arriving save never rebuilds the card under the user's pointer.
            ScopeMark.SetLabel(button, state.Stage switch
            {
                EditSessionStage.Applied => $"Stop browser session ({state.AppliedCount} applied)",
                EditSessionStage.Failed => "Stop browser session (a save failed)",
                _ => "Stop browser session",
            });
        });

        // Followed in the background: the window stays usable while somebody edits in a browser.
        _ = Task.Run(async () =>
        {
            try
            {
                await runner.FollowAsync(progress, _editSessionStop.Token);
            }
            finally
            {
                await runner.CloseAsync();
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (ReferenceEquals(_editSession, runner))
                    {
                        _editSession = null;
                        _editSessionStop?.Dispose();
                        _editSessionStop = null;

                        // ⚠ redraw: the file on disk may have changed under everything this card
                        // says — and the card also has to lose its "Stop browser session" button,
                        // which no reading of the game would ever ask for.
                        await RereadAsync(report.Game, redraw: true);
                    }
                });
            }
        });
    }

    /// <summary>
    /// Stop following, and close the session on the site.
    ///
    /// ⚠ Closing matters: sessions are a bounded resource there, and one abandoned per window
    /// closed adds up to a queue nobody can get into.
    /// </summary>
    private async Task StopLocalEditorAsync()
    {
        var stop = _editSessionStop;
        if (stop is null) return;

        try { await stop.CancelAsync(); }
        catch (ObjectDisposedException) { /* the follower finished first and cleaned up */ }
    }

    /// <summary>
    /// Waits briefly for the follower to tidy up, and tidies up itself if it does not.
    ///
    /// 🔴 **The follower's cleanup runs in a fire-and-forget task, so nothing guarantees it runs.**
    /// It resets the state in a `finally`, which covers the ordinary paths — but a task nobody
    /// awaits has no owner: if it has already ended for a reason the window never learned about,
    /// `_editSession` stays set forever. The button then keeps offering to stop a session that is
    /// gone, and every click cancels a token that is already cancelled — a control that does
    /// nothing, which is exactly what was reported.
    ///
    /// ⚠ This is NOT a try/catch hiding a fault. The window OWNS what it displays; making that
    /// display depend on an unowned task was the fault, and this is where the ownership goes back.
    /// The wait is short and bounded because the follower normally wins the race — it is only the
    /// fallback that matters.
    /// </summary>
    private async Task StrandedEditorGuard(Button button)
    {
        for (var waited = 0; waited < 20 && _editSession is not null; waited++)
            await Task.Delay(100);

        if (_editSession is null)
        {
            // ⚠ **Put the button back HERE, whatever the follower did.** It was left to the card
            // being rebuilt, which does happen — except when it does not, and then "Stopping…" is
            // what the window says for the rest of the session. A control that has finished its
            // work says so itself rather than hoping somebody else redraws it.
            button.IsEnabled = true;
            ScopeMark.SetLabel(button, "Edit in browser");
            return;
        }

        // The follower did not come back. Say so rather than leaving a dead button: whatever
        // happened to it, the session on the site is no longer being followed from here.
        _editSession = null;
        _editSessionStop?.Dispose();
        _editSessionStop = null;

        button.IsEnabled = true;
        ScopeMark.SetLabel(button, "Edit in browser");

        await ConfirmationWindow.TellAsync(this, "The browser session was dropped",
            "It is no longer followed from here, so saves made in the browser will not reach the "
            + "game. If the page is still open, close it.");
    }

    /// <summary>
    /// Merge the local translation with the published one.
    ///
    /// ⚠ **Applies what nobody has to arbitrate, and refuses to arbitrate the rest.** A merge with
    /// no conflict is arithmetic — every line is settled by the shared rule, with a verdict this
    /// tool did not invent. A conflict is a judgement about two people's wording, and resolving it
    /// silently, by taking a side or the newest, would be deciding that on somebody's behalf.
    ///
    /// ⚠ The published file is downloaded HERE rather than during the scan: the comparison needs
    /// all three sides, and fetching a translation for every game in a library to answer a question
    /// nobody asked would be minutes of network for a badge.
    /// </summary>
    private async Task MergeWithPublishedAsync(GameReport report, LoaderDescriptor descriptor, Button button)
    {
        if (report.MatchingOnline is not { } published) return;

        button.IsEnabled = false;
        ScopeMark.SetLabel(button, "Comparing…");

        try
        {
            var api = new CatalogApiClient();
            var remote = await api.DownloadAsync(published.Id, _settings.Current.ApiToken);

            if (remote is null)
            {
                await ConfirmationWindow.TellAsync(this, "Could not fetch the published version",
                    api.LastError ?? "The site did not answer.");
                return;
            }

            var folder = Path.Combine(report.Game.Path,
                descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar));

            var localPath = Path.Combine(folder, LocalTranslationProbe.TranslationFileName);
            var ancestorPath = Path.Combine(folder, LocalTranslationProbe.AncestorFileName);

            var local = await File.ReadAllTextAsync(localPath);
            var ancestor = File.Exists(ancestorPath) ? await File.ReadAllTextAsync(ancestorPath) : null;

            // ⚠ No snapshot on disk does not always mean "we cannot tell". When the version this
            // file last synced with IS the one still published, the file just downloaded is the
            // ancestor — exactly, not approximately: both sides last agreed on it, which is the
            // definition. Every difference is then provably ours, and nothing has to be put to the
            // user as a conflict.
            //
            // This is the ordinary case for a translation somebody took and then played with, and
            // it used to be reported as a pile of conflicts.
            if (ancestor is null
                && report.LocalTranslation?.SourceHash is { Length: > 0 } lastSynced
                && string.Equals(lastSynced, published.FileHash, StringComparison.OrdinalIgnoreCase))
            {
                ancestor = remote;
            }

            var merge = TranslationMerge.Build(local, remote, ancestor);
            if (merge is null)
            {
                await ConfirmationWindow.TellAsync(this, "The files could not be compared",
                    "One of them is not a translation file this tool can read. Nothing was changed.");
                return;
            }

            if (merge.Summary.Empty)
            {
                await ConfirmationWindow.TellAsync(this, "Nothing to merge",
                    "This translation and the published one already agree, line for line.");
                return;
            }

            // ⚠ Said before anything is written, in figures rather than in a verdict: "12 lines
            // taken, 3 of yours kept" is something somebody can judge; "merge?" is not.
            var summary = Describe(merge.Summary, ancestor is null);

            if (merge.Summary.HasConflicts)
            {
                var standing = ServerIdentity.For(_settings.Current, report.SiteAccount, BuildInfo.ApiBaseUrl);

                // ⚠ The site's merge screen needs an account: it holds the comparison under one,
                // and hands the result back to the same one. Without it there is still an answer —
                // the mod's own screens — rather than a dead end.
                if (!standing.CanAct || string.IsNullOrWhiteSpace(_settings.Current.ApiToken))
                {
                    await ConfirmationWindow.TellAsync(this, "This one needs a decision, line by line",
                        summary + "\n\nA conflict is two people having written the same line "
                        + "differently, and nothing here can choose between them for you. "
                        + (standing.Reason ?? "")
                        + "\n\nThe mod's own merge screens settle these while the game runs.");
                    return;
                }

                if (!await ConfirmationWindow.AskAsync(this, "Merge these in the browser?",
                        summary + "\n\nA conflict is two people having written the same line "
                        + "differently. The site shows both versions side by side, you choose, and "
                        + "the result comes back here — nothing is published by doing this.",
                        "Open the comparison"))
                {
                    return;
                }

                await ArbitrateInBrowserAsync(report, descriptor, merge, local, remote, published);
                return;
            }

            if (!await ConfirmationWindow.AskAsync(this, "Merge with the published version?",
                    summary + "\n\nYour current file is kept aside before anything is written.",
                    "Merge"))
            {
                return;
            }

            var merged = merge.BuildMergedJson();
            if (merged is null) return;

            ScopeMark.SetLabel(button, "Writing…");

            var result = new TranslationInstaller(_platform).WriteMerged(
                report.Game, descriptor, merged, remote, published.FileHash,
                merge.CountAheadOfServer(merged));

            if (!result.Written)
            {
                await ConfirmationWindow.TellAsync(this, "Nothing was written",
                    result.Failure ?? "The file could not be written.");
                return;
            }

            await ConfirmationWindow.TellAsync(this, "Merged",
                summary
                + (result.BackupPath is not null
                    ? $"\n\nYour previous file is in {TranslationInstaller.BackupFolderName}/"
                      + Path.GetFileName(result.BackupPath) + "."
                    : ""));

            await RereadAsync(report.Game);
        }
        finally
        {
            button.IsEnabled = true;
            ScopeMark.SetLabel(button, report.Sync == SyncDirection.Merge
                ? "Merge with the published version…"
                : "Download what changed online…");
        }
    }

    /// <summary>
    /// Send the two versions to the site's side-by-side screen, and wait for the answer.
    ///
    /// ⚠ **The waiting is the point.** Opening the browser is the easy half; somebody then reads
    /// every contested line and chooses. Without coming back for the result those choices would sit
    /// on the site and the file here would never change — the work would evaporate without a word,
    /// which is the failure this whole area keeps producing.
    ///
    /// ⚠ Bounded, and cancellable. The site answers "not yet" and "this comparison is gone" with
    /// the same 404, so waiting for ever is not a safe default — and somebody who closed the page
    /// must not leave this window watching a decision nobody is making.
    /// </summary>
    private async Task ArbitrateInBrowserAsync(GameReport report, LoaderDescriptor descriptor,
                                               TranslationMerge merge, string local, string remote,
                                               OnlineTranslation published)
    {
        var token = _settings.Current.ApiToken!;
        var client = new MergePreviewClient();

        var preview = await client.OpenAsync(published.Id, local, token);
        if (preview is null)
        {
            await ConfirmationWindow.TellAsync(this, "The comparison could not be opened",
                client.LastError ?? "The site did not answer.");
            return;
        }

        Shell.OpenUrl(preview.Url);

        // Half an hour: settling a long file line by line is not a thirty-second job, and the only
        // cost of waiting is a request every few seconds.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
        string? settled = null;

        Status("Waiting for the comparison to be merged in the browser…");

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4));

            settled = await client.ResultAsync(preview.Token, token);
            if (settled is not null) break;
        }

        if (settled is null)
        {
            await ConfirmationWindow.TellAsync(this, "Nothing came back",
                "The comparison was not settled, or the page was closed. This game was not changed, "
                + "and you can start it again whenever you like.");
            return;
        }

        var result = new TranslationInstaller(_platform).WriteMerged(
            report.Game, descriptor, settled, remote, published.FileHash,
            merge.CountAheadOfServer(settled));

        if (!result.Written)
        {
            await ConfirmationWindow.TellAsync(this, "Nothing was written",
                result.Failure ?? "The file could not be written.");
            return;
        }

        await ConfirmationWindow.TellAsync(this, "Merged",
            "What you chose in the browser is now the translation in this game."
            + (result.BackupPath is not null
                ? $"\n\nYour previous file is in {TranslationInstaller.BackupFolderName}/"
                  + Path.GetFileName(result.BackupPath) + "."
                : ""));

        await RereadAsync(report.Game);
    }

    /// <summary>A merge in figures, and the one caveat that changes how they should be read.</summary>
    private static string Describe(MergeSummary summary, bool blind)
    {
        var parts = new List<string>();
        if (summary.TakenFromServer > 0) parts.Add($"{summary.TakenFromServer} line(s) taken from the published version");
        if (summary.KeptHere > 0) parts.Add($"{summary.KeptHere} of yours kept");
        if (summary.Removed > 0) parts.Add($"{summary.Removed} removed on both sides");
        if (summary.Conflicts > 0) parts.Add($"{summary.Conflicts} in conflict");

        var text = string.Join(", ", parts) + ".";

        // ⚠ Without an ancestor every disagreement is a conflict, and saying so matters: otherwise
        // a file that was never synced looks like one somebody fought over.
        if (blind && summary.Conflicts > 0)
        {
            text += "\n\nThis translation has no record of a last sync, so there is no way to tell "
                  + "which side changed what — every difference has to count as a conflict.";
        }

        return text;
    }

    /// <summary>
    /// Publish this game's translation under the account signed into this window.
    ///
    /// ⚠ Two gates, in this order and neither skippable:
    ///
    /// 1. <see cref="ServerIdentity"/> — may this account act for this game at all. A machine holds
    ///    games belonging to different people, and the folder they sit in is shared.
    /// 2. check-uuid — what the upload would BECOME, asked of the server and shown before sending.
    ///    Uploading into a lineage somebody else leads files the work as a contribution to their
    ///    translation; that is a fine thing to choose and a bad thing to discover.
    /// </summary>
    private async Task PublishTranslationAsync(GameReport report, LoaderDescriptor descriptor, Button button)
    {
        var standing = ServerIdentity.For(_settings.Current, report.SiteAccount, BuildInfo.ApiBaseUrl);
        if (!standing.CanAct)
        {
            await ConfirmationWindow.TellAsync(this, "Not under this account",
                standing.Reason ?? "This game is linked to another account.");
            return;
        }

        var token = _settings.Current.ApiToken;
        if (string.IsNullOrWhiteSpace(token)) return;

        var path = Path.Combine(report.Game.Path,
            descriptor.UserDataDir.Replace('/', Path.DirectorySeparatorChar),
            LocalTranslationProbe.TranslationFileName);

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            await ConfirmationWindow.TellAsync(this, "The file could not be read", ex.Message);
            return;
        }

        button.IsEnabled = false;
        ScopeMark.SetLabel(button, "Checking…");

        var publisher = new TranslationPublisher();
        var lineage = await publisher.CheckAsync(report.LocalTranslation?.Uuid ?? "", token);

        button.IsEnabled = true;
        ScopeMark.SetLabel(button, "Publish…");

        if (lineage is null)
        {
            await ConfirmationWindow.TellAsync(this, "Could not check this translation",
                publisher.LastError ?? "The site did not answer.");
            return;
        }

        var languages = LocalTranslationProbe.ReadLanguages(report.Game.Path, descriptor);
        if (languages.Source is null || languages.Target is null)
        {
            await ConfirmationWindow.TellAsync(this, "Languages are not set",
                "Publishing needs to know which language this translates from, and into. Both are "
                + "set in the game's own settings, from the mod.");
            return;
        }

        // 🔴 **Whether it is finished is the author's own word**, and it is asked here because it
        // belongs to the same act. The site has offered it from the start and the mod now does;
        // this window was the only one of the three that could not say it.
        //
        // ⚠ A contribution inherits its Main's, exactly as the server decides and as the other two
        // products say — so nothing is offered for one, rather than a control that does nothing.
        //
        // ⚠ **Two ways of being a contributor, and only one used to be covered.** The outcome says
        // "sending this would MAKE you one"; somebody who already IS one comes back through
        // UpdateMine, and was shown the box. The server discarded what they set, silently.
        var contributing = lineage.Outcome == PublishOutcome.ContributeToTheirs;
        var branchWork = contributing || lineage.OnABranch;

        // 🔴 **Refused before the work is sent, not after.** Sending would make this a
        // contribution, and that lineage takes none — the server would answer 403 and the person
        // would have watched an upload run to be told no. Said as the fact plus the way on: the
        // fork is theirs to take and nobody can close it.
        //
        // ⚠ Only on a stated refusal. AcceptsBranches is null on a server that predates the
        // field, and null means "not asked" — behaving as a no there would invent a decision.
        if (contributing && lineage.AcceptsBranches == false)
        {
            await ConfirmationWindow.TellAsync(this, "This translation is solo work",
                $"{People.MentionOf(lineage.MainOwner, standing.SignedInAs)} works alone on this one and does not take "
                + "contributions.\n\nYour lines are safe. Publish your own version of it instead "
                + "— open it on the UGT Website and choose to publish yours.");
            return;
        }

        // A branch whose Main has closed since: the same wall, reached from the other side.
        if (lineage.BranchFrozen)
        {
            await ConfirmationWindow.TellAsync(this, "This contribution is frozen",
                "The translation you contribute to no longer accepts contributions, so this can "
                + "no longer be sent or described.\n\nYour lines are safe. Turn it into your own "
                + "version on the UGT Website to carry on.");
            return;
        }

        // ⚠ Starts on what the SERVER holds for our own row — not on MatchingOnline, which is the
        // lineage's public translation and belongs to somebody else whenever we are a branch.
        var alreadyComplete = string.Equals(lineage.Status, "complete",
                                            StringComparison.OrdinalIgnoreCase);

        var body = lineage.Describe() + "\n\n"
                 + $"{languages.Source} → {languages.Target}, as {People.Mention(standing.SignedInAs, true)}.";
        var confirm = contributing ? "Send as a contribution" : "Publish";

        // ⚠ Same source as "finished" right above, and the same reason: this account's own row.
        // Null — an older site, or nothing published yet — starts closed, which is the default
        // anybody publishing for the first time gets.
        var alreadyOpen = lineage.AcceptsBranches == true;

        bool agreed;
        var markComplete = alreadyComplete;
        var takeContributions = alreadyOpen;

        if (branchWork)
        {
            agreed = await ConfirmationWindow.AskAsync(this, "Publish this translation?", body, confirm);
        }
        else
        {
            (agreed, markComplete, takeContributions) = await ConfirmationWindow.AskAsync(
                this, "Publish this translation?", body, confirm,
                "This translation is finished", alreadyComplete,
                "Accept contributions", alreadyOpen,
                "A contribution is a copy of this work with somebody else's changes, sent to you "
                + "to accept or not. Left off, others can still publish their own version.");
        }

        if (!agreed) return;

        button.IsEnabled = false;
        ScopeMark.SetLabel(button, "Publishing…");

        // ⚠ Null on any branch work: the server makes a branch inherit its Main's, and sending a
        // value would be this window deciding something it has no say in.
        var status = branchWork ? null : (markComplete ? "complete" : "in_progress");

        // 🔴 **Sent back, not omitted.** The endpoint writes these two from the request on every
        // update, so leaving them out stores null — and this window erased, on each publish, the
        // description and the link their author had written in the game or on the site. Nothing
        // here changes them; restating them is what keeps them.
        var id = await publisher.PublishAsync(content, token, report.Game.SteamAppId, report.Game.Name,
                                              languages.Source, languages.Target,
                                              notes: lineage.Notes ?? "", status: status,
                                              resourcesUrl: lineage.ResourcesUrl ?? "",
                                              // ⚠ Null on branch work, exactly like status: a
                                              // contribution does not decide this for the Main.
                                              acceptsBranches: branchWork ? null : takeContributions);

        button.IsEnabled = true;
        ScopeMark.SetLabel(button, "Publish…");

        if (id is null)
        {
            await ConfirmationWindow.TellAsync(this, "Nothing was published",
                publisher.LastError ?? "The site did not answer.");
            return;
        }

        await ConfirmationWindow.TellAsync(this, "Sent",
            lineage.Outcome == PublishOutcome.ContributeToTheirs
                ? "Your contribution is waiting for the translation's owner to review it."
                : "Your translation is published.");

        // ⚠ redraw: publishing changes what the SITE holds — the badges, the votes, the author's
        // "finished" — while the file on this machine says exactly what it said a second ago.
        await RereadAsync(report.Game, redraw: true);
    }

    /// <summary>
    /// Change what is SAID about a published translation: its description, the link to what it
    /// needs, and — on a translation of one's own — whether its author calls it finished.
    ///
    /// 🔴 **Not a publication.** It goes to its own endpoint and sends no file, so a description
    /// fixed months later does not drag along whatever the local translation has gained since. The
    /// two acts were one for as long as the only way to change a word was to upload.
    ///
    /// ⚠ **Open to a contributor too**, minus the one thing that is not theirs to say. Proposing a
    /// clearer description, or the link to the fonts the contribution needs, IS contributing.
    ///
    /// ⚠ The lineage is asked BEFORE the window opens, so what it shows is what the server holds
    /// rather than what this machine last saw — everything in it is sent back as the new truth,
    /// and a stale description would be quietly restored.
    /// </summary>
    private async Task EditTranslationDetailsAsync(GameReport report, Button button)
    {
        var standing = ServerIdentity.For(_settings.Current, report.SiteAccount, BuildInfo.ApiBaseUrl);
        if (!standing.CanAct)
        {
            await ConfirmationWindow.TellAsync(this, "Not under this account",
                standing.Reason ?? "This game is linked to another account.");
            return;
        }

        var token = _settings.Current.ApiToken;
        if (string.IsNullOrWhiteSpace(token)) return;

        button.IsEnabled = false;
        ScopeMark.SetLabel(button, "Checking…");

        var publisher = new TranslationPublisher();
        var lineage = await publisher.CheckAsync(report.LocalTranslation?.Uuid ?? "", token);

        button.IsEnabled = true;
        ScopeMark.SetLabel(button, "Edit details…");

        if (lineage is null)
        {
            await ConfirmationWindow.TellAsync(this, "Could not check this translation",
                publisher.LastError ?? "The site did not answer.");
            return;
        }

        // ⚠ Nothing published means nothing to describe. Said rather than greyed: the button is
        // drawn before this answer is known, so its refusal has to arrive as a sentence.
        if (!lineage.HasARowOfItsOwn || lineage.RowId is not { } rowId)
        {
            await ConfirmationWindow.TellAsync(this, "Nothing is published yet",
                "These details belong to a published translation. Publish this one first — the "
                + "same description and link are asked for as part of it.");
            return;
        }

        // A frozen contribution can no longer be described either — the server refuses the write,
        // so offering the form would only produce an error once it is filled in.
        if (lineage.BranchFrozen)
        {
            await ConfirmationWindow.TellAsync(this, "This contribution is frozen",
                "The translation you contribute to no longer accepts contributions, so this can "
                + "no longer be sent or described.\n\nYour lines are safe. Turn it into your own "
                + "version on the UGT Website to carry on.");
            return;
        }

        var heading = lineage.OnABranch
            ? "What your contribution says about itself"
            : "What your translation says about itself";

        var edited = await TranslationDetailsWindow.EditAsync(
            this, heading, lineage.Notes, lineage.ResourcesUrl,
            string.Equals(lineage.Status, "complete", StringComparison.OrdinalIgnoreCase),
            lineage.OnABranch,
            lineage.AcceptsBranches == true);

        if (!edited.Saved) return;

        button.IsEnabled = false;
        ScopeMark.SetLabel(button, "Saving…");

        // ⚠ Null on a branch, and refused by the server if it were not: a contribution inherits
        // whether it is finished. MayDeclareFinished is the shared answer, not a second reading.
        var status = lineage.MayDeclareFinished
            ? (edited.Finished ? "complete" : "in_progress")
            : null;

        // ⚠ Null on a branch, exactly like the status above: the decision belongs to the Main of
        // the lineage, and a contributor sending it would answer for somebody else's translation.
        var contributions = lineage.MayDecideContributions ? edited.AcceptsContributions : (bool?) null;

        var saved = await publisher.UpdateDetailsAsync(rowId, token, edited.Notes,
                                                       edited.ResourcesUrl, status, contributions);

        button.IsEnabled = true;
        ScopeMark.SetLabel(button, "Edit details…");

        if (!saved)
        {
            await ConfirmationWindow.TellAsync(this, "Nothing was changed",
                publisher.LastError ?? "The site did not answer.");
            return;
        }

        // ⚠ redraw: this changed the SITE. The file on this machine is untouched, so the reading
        // would find the game exactly as it was and skip drawing the badge that just moved.
        await RereadAsync(report.Game, redraw: true);
    }

    /// <summary>
    /// What can be DONE with the translation on this machine: edit it, publish it, settle it
    /// against the published one.
    ///
    /// ⚠ **This is the part that used to be missing.** Everything above informs — how complete the
    /// file is, where it stands against the server — and the only action offered was to replace it
    /// with somebody else's. Publishing meant launching the game and finding the mod's upload
    /// panel, which this very card told people to go and do.
    ///
    /// ⚠ **Nothing here decides on the user's behalf.** The verdict is read from
    /// <see cref="GameReport.Sync"/> — the shared rule, the same one the mod reaches — and each
    /// button says what it will do before it does it.
    /// </summary>
    /// <param name="heading">
    /// False inside a card that is already about this translation. On the Set up tab the block sits
    /// under a section listing what the community has, and needs saying which one it is about;
    /// on Home it follows two lines describing the very same file, where a title would only
    /// announce what has just been said.
    /// </param>
    /// <summary>
    /// Whose translation this is, and the one thing a player can give back for it.
    ///
    /// ⚠ **Written because using somebody's work said nothing about them.** Running a downloaded
    /// translation, the tool named no author anywhere — the person whose hours you are playing on
    /// was a hash in a lineage. The name comes first for that reason, before the arrows.
    ///
    /// ⚠ **Who may rate is <see cref="Voting"/>'s answer, restated from the server's own rules** so
    /// an arrow is never drawn for a request that would come back 403. The refusal is always
    /// written out: a dead arrow with no reason is how somebody decides the tool is broken.
    ///
    /// 🔴 **One rule is weaker here than in the mod, and it is stated rather than hidden.** The mod
    /// only offers the arrows after it has actually put translated lines on screen this session —
    /// a rating from somebody who never ran the translation measures nothing. That counter is a
    /// runtime fact the manager cannot see. The nearest honest substitute is used: the local file
    /// has met text in game at some point, which proves the game HAS been played with it, without
    /// proving it was played recently.
    /// </summary>
    private IEnumerable<Control> PublishedBy(GameReport report)
    {
        if (report.MatchingOnline is not { } published) yield break;
        if (string.IsNullOrWhiteSpace(published.Author)) yield break;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        // 🔴 **Which languages this translation is, said on its own card.** The card carried the
        // author and the votes and never the pair — so the one line telling you whether it is even
        // the translation you want was missing from the translation you are using.
        row.Children.Add(LanguageMark.Named(published.SourceLanguage,
                                            published.SourceLanguage ?? "?"));
        row.Children.Add(new TextBlock
        {
            Text = "→",
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = Brush("TextMuted"),
        });
        row.Children.Add(LanguageMark.Named(published.TargetLanguage,
                                            published.TargetLanguage ?? "?"));

        row.Children.Add(new TextBlock
        {
            Text = "· published by",
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = Brush("TextMuted"),
        });

        // The name itself louder than the label around it: it is the part worth reading.
        row.Children.Add(new TextBlock
        {
            // One form everywhere, and the "(you)" is a word rather than a colour — see
            // People.Mention. Reading your own name in a list of other people's was, until this,
            // something you had to already know.
            Text = People.MentionOf(published.Author, _settings.Current.ApiUser),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = Brush("TextPrimary"),
        });

        // ⚠ Counts from the local file, not from the published entry: the question is whether THIS
        // machine has run the translation, and the published figures describe somebody else's copy.
        var metText = report.LocalTranslation?.Counts is { } counts
                      && (counts.Captured > 0 || counts.Human > 0 || counts.Ai > 0
                          || counts.Validated > 0);

        var block = Voting.Rating(
            signedIn: !string.IsNullOrWhiteSpace(_settings.Current.ApiToken),
            published: true,

            // ⚠ Only the Main's owner is refused. Holding a BRANCH of this lineage does not make
            // the Main yours — it is public and it belongs to somebody else, so the server allows
            // it, and refusing here would silence the people who have worked with it most.
            isYourOwn: report.MyPosition is { IsMain: true },
            hasUsedIt: metText);

        // ── The same picture as the site and the game: ▲ +47 ▼ ────────────────
        //
        // ⚠ Two arrows around a signed count, and the arrow YOU cast is the one that is filled.
        // That filled arrow is the entire "you have already voted" signal, and it has to be the
        // same picture in the three products — not a tick, not a word. An earlier version here
        // said "Good"/"Poor" with a tick, which was a fourth vocabulary for a control that exists
        // twice already.
        var votable = block == RateBlock.None;

        row.Children.Add(votable
            ? RateArrow(report.Game, published, +1)
            : DeadArrow(Voting.Up, Voting.Explain(block)));

        row.Children.Add(new TextBlock
        {
            Text = Voting.CountLabel(published.VoteCount),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 34,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = Brush(TranslationBadges.ToneKey(Voting.CountTone(published.VoteCount))),
        });

        row.Children.Add(votable
            ? RateArrow(report.Game, published, -1)
            : DeadArrow(Voting.Down, Voting.Explain(block)));

        yield return row;

        if (Voting.Explain(block) is { Length: > 0 } why)
        {
            yield return new TextBlock
            {
                Text = why,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 2, 0, 0),
                Foreground = Brush("TextMuted"),
            };
        }
    }

    /// <summary>
    /// An arrow you cannot press, with the reason on it.
    ///
    /// ⚠ Drawn rather than dropped, exactly as the website does for an author looking at their own
    /// translation: the control keeps its shape so the row does not change size between two people
    /// looking at the same game, and hovering says why it is inert.
    /// </summary>
    private Control DeadArrow(string mark, string why)
    {
        var arrow = new TextBlock
        {
            Text = mark,
            FontSize = 13,
            Padding = new Avalonia.Thickness(6, 2),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = Brush("TextMuted"),
            Opacity = 0.45,
        };

        if (why.Length > 0) ToolTip.SetTip(arrow, why);
        return arrow;
    }

    /// <summary>
    /// One arrow, filled when it is the one this account cast.
    ///
    /// 🔴 **The filled arrow IS the "you have already voted" signal**, and it is the same picture
    /// on the website and in the game. Not a tick, not a word: somebody who learned it in a browser
    /// has to recognise it here without being told. Two arrows are never filled at once, because
    /// nobody can vote twice — the server keeps one vote per person and replaces it.
    /// </summary>
    private Button RateArrow(GameInstall game, OnlineTranslation published, int value)
    {
        var button = new Button
        {
            Content = value > 0 ? Voting.Up : Voting.Down,
            FontSize = 13,
            Padding = new Avalonia.Thickness(6, 1),
            Foreground = Brush(TranslationBadges.ToneKey(Voting.ArrowTone(value, published.UserVote))),
        };

        ToolTip.SetTip(button, Voting.ArrowTip(value, published.UserVote));

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;

            // ⚠ One instance, kept: LastError lives on it. Building a second client to read the
            // error would read a fresh object that never made a call.
            var client = new VoteClient();
            var outcome = await client.CastAsync(published.Id, value,
                                                 _settings.Current.ApiToken ?? "");

            if (outcome is null)
            {
                button.IsEnabled = true;
                await ConfirmationWindow.TellAsync(this, "The rating was not recorded",
                    client.LastError ?? "The site did not answer.");
                return;
            }

            // Written back onto the entry the card was drawn from, then redrawn: the count and the
            // tick both come from the server's answer rather than from what we assumed it would be.
            published.VoteCount = outcome.Count;
            published.UserVote = outcome.Mine;

            // ⚠ redraw: a vote changes the SITE, not this game — so the reading below would find
            // the situation word for word identical and skip the redraw the line above just
            // prepared. Same trap as the button that would not come back after a session ended.
            await RereadAsync(game, redraw: true);
        };

        return button;
    }

    private IEnumerable<Control> TranslationWorkbench(GameReport report, bool heading = true)
    {
        // Nothing here to work on. The community list above is the whole offer in that case.
        if (report.LocalTranslation is null) yield break;

        var loaderId = report.InstalledLoader?.Id ?? report.RecommendedLoader?.Id;
        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == loaderId);
        if (descriptor is null) yield break;

        var standing = ServerIdentity.For(_settings.Current, report.SiteAccount, BuildInfo.ApiBaseUrl);

        if (heading)
        {
            yield return new TextBlock
            {
                Text = "This translation",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 12, 0, 0),
                Foreground = Brush("TextPrimary"),
            };
        }

        // Where it stands, in the words the mod uses for the same verdict.
        //
        // ⚠ Null is NOT "everything is fine": no translation published for this game, or a file we
        // could not read. Saying "in sync" there would be a claim nothing supports.
        if (report.Sync is { } sync)
        {
            yield return new TextBlock
            {
                // ⚠ **With the number, where there is one.** "This game holds changes the published
                // version does not" and "both have moved" are verdicts without a size: somebody
                // deciding whether to settle a conflict now or later needs to know if it is four
                // lines or four hundred, and the card measured it two blocks above.
                //
                // Only OUR side is counted, and the wording says so. How far the published version
                // has moved cannot be known without fetching it, and inventing a figure for it
                // would be worse than leaving the question open.
                Text = sync switch
                {
                    SyncDirection.InSync => "Up to date with the published version.",
                    SyncDirection.Download => "The published version has moved on. Nothing of yours "
                                            + "is at risk — this game holds no unpublished change.",
                    SyncDirection.Upload => Unpublished(report) is { } up
                        ? $"This game holds {up} line(s) the published version does not."
                        : "This game holds changes the published version does not.",
                    _ => Unpublished(report) is { } mine
                        ? $"Both have moved: {mine} line(s) here are unpublished, and the published "
                          + "version changed too. Settling that is done line by line."
                        : "Both this file and the published one have moved. Settling that is done "
                          + "line by line.",
                },
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 2, 0, 0),
                Foreground = Brush(sync == SyncDirection.Merge ? "StatusWarning" : "TextSecondary"),
            };
        }

        foreach (var control in PublishedBy(report)) yield return control;

        // ⚠ A WrapPanel, not a StackPanel. Each button now carries the three scope marks before its
        // label, which is some forty pixels more per button — a horizontal stack would have run off
        // the edge of a narrow window with no way to reach the last action.
        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };

        // ── Edit, in a browser ────────────────────────────────────────────────
        //
        // Available whoever the file belongs to: editing changes the copy on this machine and
        // nothing else. Ownership only decides what may be PUBLISHED.
        //
        // ⚠ The marks live INSIDE the button, before the label. A label names the verb and never
        // the destination — Edit and Publish do not aim at the same place, and nothing but this
        // says so.
        // ⚠ Guarded on CanWriteLocally, not on CanAct. Editing writes the game's own translation
        // file and touches no server — which is why it was open to everybody, and why it must not
        // be: breaking the setup another user of this computer put in place needs no server at all.
        // 🔴 **A file with no line in it has nothing to publish AND nothing to edit.**
        // A game set up a minute ago holds none: the mod captures them while it is played. Offered
        // there, Publish hands the server a translation that says nothing, and Edit opens a browser
        // session on an empty page — two costs nobody ever collects. Which is why this refusal
        // governs both buttons, and is stated in the words that say what to do instead.
        //
        // ⚠ An unreadable file falls under the same hand, for a different reason: we do not send
        // what we could not read. EntryCount is negative there, never zero.
        var lines = report.LocalTranslation?.EntryCount ?? 0;
        var nothingYet = lines switch
        {
            < 0 => "This game's translation file cannot be read, so nothing can be sent from it "
                 + "or edited in it.",
            0 => "This game holds no translated line yet — play it so the mod captures some, then "
               + "publish or edit them.",
            _ => null,
        };

        // 🔴 **And nothing to DESCRIBE when this account holds nothing in this lineage.**
        //
        // Edit details writes a description of a translation PUBLISHED UNDER THIS ACCOUNT. The
        // comment further down says the card cannot know whether one exists, because a
        // contributor's own row never appears in MatchingOnline. That was true when it was
        // written and is not any more: AccountLineages reads /me/translations once and answers
        // exactly this — Main, branch or fork, anything the account holds in that lineage.
        //
        // ⚠ Only once an answer has been READ. Before that, an account whose lineages are still
        // being fetched looks identical to one that owns nothing, and greying on that basis states
        // a guess as a fact — the very reason AccountLineages exposes Known separately.
        //
        // ⚠ This is what the earlier version got too narrowly. Guarding on an empty file caught a
        // game set up a minute ago and missed the ordinary case: hundreds of lines translated for
        // one's own use, in somebody else's lineage, never published. Nothing to edit there either.
        var noDetailsYet = _lineages.Known && _lineages.For(report.LocalTranslation?.Uuid) is null
            ? "Nothing has been published under this account for this game, so there are no "
              + "details to edit. Publish first, and the description follows."
            : null;

        var edit = ScopeMark.Marked(EditSide.Local, "Edit in browser",
                                    standing.CanWriteLocally && nothingYet is null);
        edit.Click += async (_, _) => await OpenLocalEditorAsync(report, descriptor, edit);
        actions.Children.Add(edit);

        // ── Publish ───────────────────────────────────────────────────────────
        //
        // The one action that leaves this machine.
        //
        // ⚠ **Two refusals, not one.** The account may not be allowed to act (standing.CanAct),
        // and separately there may be nothing to send. Offering a live Publish on a file already
        // in step invites somebody to re-send what is already there; the merge button beside it
        // has always followed that rule and this one did not.
        //
        // ⚠ Greyed rather than hidden, and that is a choice: Publish is the product's main act,
        // and one that vanishes reads as "I have lost the right to publish" rather than "there is
        // nothing to publish". A greyed control with its reason under it says which.
        var nothingToSend = report.Sync switch
        {
            SyncDirection.InSync => "Already up to date with the published version — nothing to send.",

            // Behind means the site moved and this file did not. Publishing would push older
            // content over newer, which is not an update, it is a rollback nobody asked for.
            SyncDirection.Download => "The published version is ahead of this file. Take what "
                                    + "changed first.",

            // Both moved. Publishing now would drop whatever the other side gained.
            SyncDirection.Merge => "Both sides have moved. Settle the difference before publishing, "
                                 + "or what is online is overwritten.",

            _ => null,
        };

        // ⚠ **Before Publish, and that is the reading order.** When both sides have moved, Publish
        // is refused with "settle the difference first" — so the button that settles it cannot
        // come after the refusal, two controls further along. The remedy goes in front of the
        // thing it unblocks.

        // ── Settle the difference ─────────────────────────────────────────────
        //
        // ⚠ Only offered when there IS something to settle. A merge button on a file in step with
        // the server invites somebody to fix what is not broken.
        if (report.Sync is SyncDirection.Merge or SyncDirection.Download
            && report.MatchingOnline is not null)
        {
            // 🔴 **One button, two acts, and they do NOT leave things on the same side.**
            //
            // Merging ends with a reconciled file that exists HERE and nowhere else, so Local.
            // Taking what changed online ends with this machine carrying the published version —
            // which is Both ONLY if that published version is ours. On somebody else's, nothing
            // published under our name moved, and the answer is Local again.
            //
            // ⚠ It was Both for both, on the reasoning "reads the published version, writes here,
            // the pair is what makes it Both". Two mistakes in one: the strip answers what is true
            // AFTER rather than which files were touched, and the side that counts is OURS. A Main
            // owner taking a stranger's translation would have been told they were in step at the
            // moment their own published copy stopped matching what they run.
            var merging = report.Sync == SyncDirection.Merge;
            var oursOnline = !merging
                             && standing.SignedInAs is { Length: > 0 } me
                             && string.Equals(report.MatchingOnline.Author, me,
                                              StringComparison.OrdinalIgnoreCase);

            var merge = ScopeMark.Marked(
                EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: oursOnline),
                merging ? "Merge with the published version…"
                        : "Download what changed online…",
                standing.CanWriteLocally);
            merge.Click += async (_, _) => await MergeWithPublishedAsync(report, descriptor, merge);
            actions.Children.Add(merge);
        }

        // ── Give up on the local changes ──────────────────────────────────────
        //
        // 🔴 **Merging is not the only answer to "the two differ".** Sometimes the local changes
        // are not worth keeping — a test, a line typed by mistake, an afternoon of AI output
        // somebody would rather drop — and the wanted outcome is simply the published version as
        // it stands. Without this the only route was: remove the translation, find it again in the
        // list, select it, apply. Four steps to say "forget mine".
        //
        // ⚠ Offered whichever way the two have drifted, including when only THIS side moved: that
        // is precisely the case where somebody wants their changes gone and nothing else to
        // happen. Merge, above, keeps both sides; this one does not pretend to.
        if (report.MatchingOnline is { } onServer
            && report.Sync is SyncDirection.Upload or SyncDirection.Merge
            && report.LocalTranslation is not null)
        {
            // Ours online? Then afterwards both carry the same thing. Somebody else's, and only
            // this machine changed — nothing published under our name moved. Same reading as the
            // merge button right above, and for the same reason.
            var oursPublished = standing.SignedInAs is { Length: > 0 } who
                                && string.Equals(onServer.Author, who, StringComparison.OrdinalIgnoreCase);

            var takeTheirs = ScopeMark.Marked(
                EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: oursPublished),
                "Take the published version…",
                standing.CanWriteLocally);

            ToolTip.SetTip(takeTheirs, Unpublished(report) is { } dropped
                ? $"Replaces this game's file with the published one. The {dropped} line(s) not "
                  + "published are set aside, not merged."
                : "Replaces this game's file with the published one, as it stands.");

            takeTheirs.Click += async (_, _) =>
                await TakeSelectedTranslationAsync(report, onServer, replacing: true);

            actions.Children.Add(takeTheirs);
        }

        // ⚠ Both, not Server. What is sent is the file from this game, so afterwards the published
        // translation and this machine carry the same thing. Server would mean "the published
        // version has the result and this machine does not", which cannot happen from a tool that
        // is sending the machine's own file.
        var publish = ScopeMark.Marked(EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: true),
                                       "Publish…",
                                       standing.CanAct && nothingYet is null && nothingToSend is null);
        publish.Click += async (_, _) => await PublishTranslationAsync(report, descriptor, publish);
        actions.Children.Add(publish);

        // ── What is said about it ─────────────────────────────────────────────
        //
        // ⚠ Deliberately NOT guarded on nothingToSend. This is the one action that exists
        // precisely for when there is nothing left to publish: a description written after the
        // fact, a link that moved, a translation its author now calls finished.
        //
        // ⚠ Server, not Both: it changes what the site holds and writes nothing on this machine.
        //
        // ⚠ MatchingOnline cannot answer whether this is published — it is the lineage's PUBLIC
        // translation, and a contributor's own row is not in it. **AccountLineages can**, and does:
        // see noDetailsYet above. Offering to edit a description that does not exist sends the
        // reader to find that out for themselves.
        //
        // ⚠ The cloud mark is right and stays: what this writes lands on the site and nothing of
        // it on this machine. It looked wrong only because the button was offered where there was
        // nothing published to write about — the mark was reporting the fault, not causing it.
        var details = ScopeMark.Marked(EditScope.SideAfter(onThisMachine: false, yourPublishedCopy: true),
                                       "Edit details…", standing.CanAct && noDetailsYet is null);
        details.Click += async (_, _) => await EditTranslationDetailsAsync(report, details);
        actions.Children.Add(details);

        // ── Clear the way for another one ─────────────────────────────────────
        //
        // 🔴 **Here, not in the uninstall dialogue.** Removing a translation to start a different
        // one is not taking the mod out of a game; it was only reachable by opening a screen
        // titled "Uninstall" and ticking one box out of three. The act belongs beside the
        // translation it acts on, on the tab about this game rather than the one about its
        // machinery.
        //
        // ⚠ Local: the published translation is untouched, which is precisely what makes this
        // safe to offer. Somebody who published can take theirs back afterwards, with their role.
        // That is the whole reason the philosophy is "publish, then switch freely".
        if ((report.LocalTranslation?.EntryCount ?? 0) != 0 || report.LocalTranslation is not null)
        {
            // ⚠ "Local" is said in words as well as by the mark. The mark tells a reader who has
            // learnt this interface; the word tells everyone else, and on the one button that
            // takes a translation out of a game, being clear twice costs nothing.
            var clear = ScopeMark.Marked(EditSide.Local, "Remove local translation…",
                                         standing.CanWriteLocally);
            clear.Click += async (_, _) => await RemoveTranslationAsync(report, descriptor);
            actions.Children.Add(clear);
        }

        // This translation's own history: copies taken by an action, and copies somebody asked for.
        //
        // 🔴 **Always offered, even at zero.** It used to appear only once something had been
        // replaced — so the one way to take a copy BEFORE doing something risky was invisible
        // until after the risk had been taken. The panel is where somebody learns the mechanism
        // exists, and the only useful moment to learn it is beforehand.
        //
        // ⚠ The words, the two families and their limits come from Backups, so this window and
        // the mod's own panel describe the same folder identically.
        if (descriptor is not null)
        {
            var kept = TranslationBackupStore.List(report.Game.Path, descriptor);

            var back = ScopeMark.Marked(EditSide.Local, "Backups…", standing.CanWriteLocally);
            ToolTip.SetTip(back, kept.Count == 0
                ? "Keep a copy of this translation before you try something, and come back to it."
                : $"{Backups.SavedCount(kept)} saved by you, "
                  + $"{kept.Count - Backups.SavedCount(kept)} kept automatically.");
            back.Click += async (_, _) => await ShowBackupsAsync(report, descriptor);
            actions.Children.Add(back);
        }

        yield return actions;

        // ⚠ The refusal is stated, never silent. A greyed button with no reason is how somebody
        // concludes the tool is broken — and here the reason is precise and actionable: this game
        // belongs to another account.
        if (!standing.CanWriteLocally)
        {
            // ⚠ Said FIRST and louder, because it is the wider refusal: nothing on this card can be
            // used at all. Leaving only the publishing reason would explain the greyed Publish and
            // leave somebody wondering why Edit is greyed too.
            yield return new TextBlock
            {
                Text = Standings.ExplainRefusal(standing.Standing, toServer: false),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                Foreground = Brush("StatusWarning"),
            };
        }
        else if (standing.Reason is { } reason)
        {
            yield return new TextBlock
            {
                Text = reason,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                Foreground = Brush(standing.Kind == ServerStandingKind.SignedOut ? "TextMuted" : "StatusWarning"),
            };
        }
        else if ((nothingYet ?? nothingToSend) is { } why)
        {
            // Only when the account COULD have acted: two refusals stacked would leave somebody
            // fixing the second while the first still stands. ⚠ And the empty file comes FIRST:
            // it governs two buttons where the sync reason governs one, and a game with no line
            // cannot be in any sync state worth explaining.
            yield return new TextBlock
            {
                Text = why,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                Foreground = Brush("TextMuted"),
            };
        }
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

    /// <summary>
    /// "Use another build" — the five most recent builds the publisher currently offers.
    ///
    /// ⚠ **Folded away, and that is the design.** Taking an older build is a repair, done when a
    /// game refuses the current one; showing five versions beside three loaders would put fifteen
    /// lines on a card that answers "what do I install here" for everybody else.
    ///
    /// ⚠ **Nothing is fetched until it is opened.** Resolution costs two publishers a request, and
    /// unauthenticated GitHub allows sixty an hour per address.
    ///
    /// ⚠ A source that does not answer falls back to the pinned entry AND SAYS SO. Quietly
    /// installing a two-year-old build because a page timed out is precisely the failure this
    /// whole change exists to end.
    /// </summary>
    private Control BuildChooser(GameReport report, ComboBox loaderPicker)
    {
        var builds = new ComboBox { Width = 300, IsEnabled = false };
        var note = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(builds);
        body.Children.Add(note);

        var expander = new Expander
        {
            Header = "Use another build",
            FontSize = 12,
            Content = body,
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
        };

        var loaded = false;

        async Task LoadAsync()
        {
            if (_chosenLoader() is not { } loader) return;

            builds.IsEnabled = false;
            builds.Items.Clear();
            note.Text = $"Asking what {loader.Display} currently offers...";

            var channel = loader.Id.StartsWith("bepinex6", StringComparison.OrdinalIgnoreCase)
                ? _settings.Current.BepInEx6Channel
                : null;

            var found = await new LoaderBuildResolver()
                .ResolveAsync(loader, channel, count: 5).ConfigureAwait(true);

            foreach (var build in found)
            {
                builds.Items.Add(new ComboBoxItem { Content = build.Describe(), Tag = build });
            }

            builds.SelectedIndex = 0;
            builds.IsEnabled = found.Count > 1;

            note.Text = found[0].IsPinnedFallback
                ? $"Could not reach the place {loader.Display} is published, so only the build "
                  + "recorded in the catalog is available. It may be far behind."
                : $"From {found[0].SourceLabel}. The newest is used unless another is picked in this list.";

            loaded = true;
        }

        expander.Expanding += async (_, _) =>
        {
            if (!loaded) await LoadAsync();
        };

        // Changing the loader invalidates the list: these are BepInEx's builds, not MelonLoader's.
        // Reloaded in place rather than on the next open, so what is on screen is never about a
        // loader the reader has already moved away from.
        loaderPicker.SelectionChanged += async (_, _) =>
        {
            loaded = false;
            if (expander.IsExpanded) await LoadAsync();
        };

        // Only what is CHOSEN here counts — null means "nobody picked one", NOT "use the pinned
        // archive".
        //
        // 🔴 It used to mean the second, and that is how the tool installed something other than
        // what it announced: the card names the resolved build (6.0.0-be.785) beside the loader,
        // this expander is folded by default, so every ordinary install fell back to the pinned
        // 6.0.0-pre.2. The receipt then said pre.2 and the binaries read be.697, while the screen
        // had said 785. The caller now keeps the plan's own resolved build when nothing is picked.
        _chosenBuild = () => expander.IsExpanded
            ? (builds.SelectedItem as ComboBoxItem)?.Tag as LoaderBuild
            : null;

        return expander;
    }

    private Control LoaderSection(GameReport report)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(SectionTitle("Mod loader"));

        var running = _running.IsRunning(report.Game);
        var standing = report.LoaderStanding;

        // Which loader is offered first is an ordering, not a decision made for the user: some
        // games work with one and not another for reasons no probe can see.
        ComboBox? loaderPicker = null;

        // ⚠ Cleared before anything can set it, and BuildChooser reinstates it below. A build
        // picked on the previous game's card must not follow the reader here: the loaders differ,
        // and installing "the build chosen for another game" is a fault nobody thinks to look for.
        _chosenBuild = () => null;

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

            // 🔴 **The way to take it over, asked once and never assumed.**
            //
            // Refusing to touch somebody else's loader is right as a default and wrong as a wall:
            // a version months behind was reported and then left, with no way to say "this one is
            // mine to manage". This is that way — per game, unticked every time until somebody
            // ticks it, and stored as a preference rather than read as a modifier on a button.
            //
            // ⚠ It changes WHO MAY ACT, never what is done. Updating writes the loader's own files
            // over themselves; other mods' assemblies, configs and data are not ours and are not
            // touched, exactly as on a loader we installed.
            //
            // ⚠ And it is what makes the update visible at all: LoaderUpdateOffered reads it, so
            // until it is ticked the row and the card stay quiet about a newer version being out.
            if (!installed.InstalledByUs)
            {
                var preference = _preferences.Read(report.Game.Path);

                var adopt = new CheckBox
                {
                    Content = $"Let UnityGameTranslator Manager update {installed.Display} in this game",
                    IsChecked = preference.AdoptLoader,
                    FontSize = 12,
                    Margin = new Avalonia.Thickness(0, 4, 0, 0),
                };

                ToolTip.SetTip(adopt,
                    "Installs and updates this loader from here, in this game only. Other mods "
                    + "keep their files — only the loader's own are replaced.");

                adopt.IsCheckedChanged += async (_, _) =>
                {
                    var current = _preferences.Read(report.Game.Path);
                    current.AdoptLoader = adopt.IsChecked == true;
                    _preferences.Set(report.Game.Path, current);

                    // Redrawn rather than left: ticking it changes the verb on the button beside
                    // it and what the row says about this game, and a card that keeps showing the
                    // refusal it has just lifted is a card contradicting itself.
                    await ShowSelectedAsync();
                };

                panel.Children.Add(adopt);

                if (preference.AdoptLoader && report.LoaderStanding is { UpdateAvailable: true } newer)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = $"{newer.Installed} → {newer.Available} available.",
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness(24, 0, 0, 0),
                        Foreground = Brush("StatusInfo"),
                    });
                }
            }
        }
        else if (report.EligibleLoaders.Count > 0)
        {
            loaderPicker = new ComboBox { Width = 260 };
            foreach (var loader in report.EligibleLoaders)
            {
                // 🔴 No "(recommended)", and no word in its place. We recommend nothing: the order
                // comes from an integer in the catalog whose only documentation is "higher wins",
                // and calling that a recommendation claims a judgement nobody made.
                //
                // ⚠ "(default)" was considered and is worse, for a reason that does not show: the
                // word promises a SETTING, so the reader goes looking for where it is configured —
                // and being honest would then mean building one per case, Mono against IL2CPP,
                // x86 against x64, on top of the BepInEx 6 channel. The line above the control
                // already says "we would use"; the suffix was redundant and opened that door.
                // ⚠ **No version where the version depends on a channel we have not resolved.**
                // loader.Version is what the catalog PINS — 6.0.0-pre.2 for BepInEx 6 — and
                // printing it beside a game set to Bleeding Edge stated the opposite of what
                // installing would do. Resolving here would ask two publishers on every card
                // drawn; naming the loader and letting "Use another build" answer costs nothing
                // and cannot be wrong.
                // The resolved version when the background pass has brought it in, the pinned one
                // when the loader has no channel to be wrong about, and the bare name in between.
                var channel = loader.Id.StartsWith("bepinex6", StringComparison.OrdinalIgnoreCase)
                    ? _settings.Current.BepInEx6Channel
                    : null;

                var version = LoaderBuildResolver.Known(loader, channel)?.Version
                              ?? (loader.Sources.Count > 1 ? null : loader.Version);

                loaderPicker.Items.Add(new ComboBoxItem
                {
                    Content = version is null ? loader.Display : $"{loader.Display} {version}",
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
            panel.Children.Add(BuildChooser(report, loaderPicker));
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

            remove.Click += async (_, _) => await RunUninstallAsync(report, fromLoaderSection: true);
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

        // 🔴 **Beside the uninstall, never inside it.** This game had files of its own where ours
        // went, and they are still held aside. Putting them back is the opposite motion to
        // removing ours, and folding the two into one button produced the worst possible result:
        // somebody asked for a clean game, got its previous loader written back in the same
        // breath, saw a loader still detected and concluded the uninstall had failed.
        //
        // 🔴 **Only when it would DO something, and counting what it would do.** Offered on the
        // stored count, this appeared on every installed game — where every one of those paths is
        // still occupied, so restoring writes nothing — under a label reading "(98)". Ninety-eight
        // announced changes, zero real ones, next to a verb vague enough to read as a threat. It
        // now appears only where the files are actually missing, which in practice means after an
        // uninstall, and the number is what would be written.
        if (UninstallEngine.RestorableFiles(report.Game) is { Count: > 0 } missing)
        {
            // ⚠ Two words. It was "Put back what was here before", then "Restore this game's own
            // files" — a sentence either way. "Restore" is the verb every program has used for
            // thirty years; "files" is what tells it apart from "Restore local…", the translation
            // button. The count and the reason belong in the tooltip, which is where somebody
            // looks once the label has told them which button this is.
            var putBack = ScopeMark.Marked(EditSide.Local, $"Restore files ({missing.Count})",
                                           enabled: !running);

            ToolTip.SetTip(putBack,
                $"{missing.Count} file(s) this game had before UnityGameTranslator Manager "
                + "replaced them are missing — its previous mod loader, most often. This writes "
                + "them back. Nothing is deleted: anything already in place is left alone.");

            putBack.Click += async (_, _) => await RunPutBackAsync(report);
            buttons.Children.Add(putBack);
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
        // looking pressed and the next Space goes nowhere. Three things react to a change here: the
        // list of differences, the form of this game's own settings, and the band at the bottom
        // that says what one click would do.
        var driftHost = new StackPanel { Spacing = 4 };
        var ownHost = new StackPanel { Spacing = 4 };
        var hotkeyHost = new StackPanel { Spacing = 4 };

        void Refresh()
        {
            driftHost.Children.Clear();
            foreach (var control in ConfigDrift(report, preference))
                driftHost.Children.Add(control);

            // ⚠ Present ONLY while the box is unticked, because that is the only state in which it
            // means anything: ticked, every one of these fields is answered by the defaults, and a
            // form full of values nobody may change here would be an invitation with no door.
            ownHost.Children.Clear();
            ownHost.IsVisible = !preference.UsesModDefaults(GameConfig(report));

            if (ownHost.IsVisible)
            {
                foreach (var control in OwnModSettings(report, preference, Refresh))
                    ownHost.Children.Add(control);
            }

            hotkeyHost.Children.Clear();
            foreach (var control in HotkeyDecision(report, preference, Refresh))
                hotkeyHost.Children.Add(control);

            ShowActionBar(report);
        }

        // ⚠ Read through the game rather than from the stored answer. Null in the file means
        // "nobody has decided", and the answer is then taken from the game itself: one that is
        // already configured starts UNTICKED, so the first one-click cannot quietly overwrite a
        // set-up somebody made inside the mod. See GamePreference.UsesModDefaults.
        // ⚠ The source is NAMED. "Use my mod defaults here" said neither whose nor where: a machine
        // owns nothing, this one carries games belonging to different people, and "here" is the
        // whole card. Mod defaults is a screen with that title on it.
        var applyDefaults = new CheckBox
        {
            Content = "Use Mod defaults in this game",
            IsChecked = preference.UsesModDefaults(GameConfig(report)),
            FontSize = 12,
        };

        ToolTip.SetTip(applyDefaults,
            "Ticked, this game is set up with the values from Mod defaults. Unticked, it keeps "
            + "settings of its own, starting from what it already holds.");

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

        yield return ownHost;

        // 🔴 **The hotkey question sits HERE — governed by the box above, beside the differences,
        // and outside both.** It belongs to "Use Mod defaults in this game" exactly as the list of
        // differences does; it is not one of this game's own settings, so it is not in that form.
        //
        // ⚠ And it is NOT inside the differences callout. That callout holds MODIFICATIONS — what
        // applying would change. This is an OPTION: it decides whether one of those modifications
        // happens at all. Wrapping a control in the banner that reports consequences makes the
        // control read as one of them. The difference itself does appear in that callout, in the
        // same shape as every other line — which was the whole point of moving its rendering.
        yield return hotkeyHost;

        // The first fill, which also settles whether the form above starts out on screen.
        Refresh();
    }

    /// <summary>
    /// This game's own mod settings, folded away until somebody wants them.
    ///
    /// ⚠ **Folded, and that is not timidity.** This tab already carries the loader, the mod and the
    /// translations; twenty-five fields unrolled underneath them would bury all three, and most
    /// visits to this card are not about changing a setting. The header says how many answers this
    /// game holds, so the fold never hides a fact — only a form.
    ///
    /// ⚠ Present only while the box above is unticked. Ticked, every field here is answered by the
    /// defaults, and a form nobody may change would be an invitation with no door behind it.
    /// </summary>
    private IEnumerable<Control> OwnModSettings(GameReport report, GamePreference preference,
                                                Action refresh)
    {
        var snapshot = GameConfig(report);

        var pinned = LanguagePinnedTo(report, preference);
        var form = new GameModSettingsForm(_platform, _settings.Current, snapshot, preference.Mod,
                                           pinned.Language, pinned.Published);

        form.Applied += async () =>
        {
            // ⚠ Emptied rather than stored empty. A game that answers nothing of its own must come
            // back as "nothing decided here", not as an object full of nulls — the two read the
            // same on screen and only the first lets a later default reach this game.
            //
            // ⚠ Kept as well as written, and both are needed. Written, because a brick whose verb
            // does not reach the game is not a brick — that was the hole. Kept, because these can
            // be answered before there is anywhere to write them (no loader yet), and because a
            // game reinstalled from scratch should get them back rather than silently lose them.
            preference.Mod = form.Draft.IsEmpty ? null : form.Draft.Copy();
            _preferences.Set(report.Game.Path, preference);

            await ApplyOwnSettingsAsync(report, preference);

            // The differences block and the band below both describe what would be written, which
            // is exactly what has just changed.
            refresh();
        };

        form.OpenDefaults += async () => await OpenSettingsAsync();

        var answered = preference.Mod?.Count ?? 0;

        yield return new Expander
        {
            Header = new TextBlock
            {
                Text = answered switch
                {
                    0 => "This game's own settings",
                    1 => "This game's own settings — 1 set for this game",
                    _ => $"This game's own settings — {answered} set for this game",
                },
                FontSize = 12,
                Foreground = Brush("TextSecondary"),
            },
            Content = form.Build(),
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };
    }

    /// <summary>
    /// Writes THIS GAME's own settings into its config.json — the verb of that brick.
    ///
    /// 🔴 It exists because a brick whose verb never reaches the game is not a brick. The form used
    /// to store its answers and stop there, which left them waiting on some other button to notice
    /// them; with the one-click reading the box (and so writing Mod defaults or nothing at all),
    /// they would simply never have arrived.
    ///
    /// ⚠ Silent on a game with no loader: there is nowhere to write yet. The answers are kept, and
    /// the next install lays them down — which is the whole reason they are stored as well as
    /// written.
    /// </summary>
    private async Task ApplyOwnSettingsAsync(GameReport report, GamePreference preference)
    {
        if (InstalledDescriptor(report) is not { } descriptor) return;
        if (_running.IsRunning(report.Game)) return;

        Busy(true, "Applying this game's settings...");

        // The values the form shows, resolved by the Core — this game's answers first, what it
        // already holds next, Mod defaults last.
        var settings = SettingsFor(report, preference);

        var result = new GameConfigWriter().Apply(
            report.Game.Path, descriptor, settings,
            TargetFor(report, descriptor, settings), perGame: preference);

        Busy(false, "Ready.");

        if (!result.Written)
        {
            await MessageAsync("Nothing was changed",
                $"This game's settings could not be written ({result.Failure}).");
        }
    }

    /// <summary>
    /// What this game's config.json holds today, in the terms this tool reasons in.
    ///
    /// Re-read rather than remembered, like everything else on this card: a value cached at the
    /// last scan is a claim about a file the player may have changed by playing since — which is
    /// the recurring root cause this project has paid for more than once (reconcile from the real
    /// state, never from the transition that was supposed to have produced it).
    /// </summary>
    private GameConfigSnapshot GameConfig(GameReport report) =>
        GameConfigWriter.Read(report.Game.Path, InstalledDescriptor(report));

    /// <summary>
    /// The settings this game would actually be written with: its own where it has any, what it
    /// already holds next, the defaults last.
    ///
    /// 🔴 Never resolved on a screen. This is one call into the Core so that the window, the action
    /// bar, the confirmation and the CLI cannot answer "what will this game be configured with"
    /// four times and disagree once.
    /// </summary>
    private InstallerSettings SettingsFor(GameReport report, GamePreference preference) =>
        ModSettingsResolver.Resolve(_settings.Current, preference, GameConfig(report));

    /// <summary>
    /// The language THIS game is to be set to, from the settings it is actually written with.
    ///
    /// ⚠ Not <see cref="SettingsStore.ResolveTargetLanguage"/>, which answers for the person. That
    /// one still rules everywhere the question is "which of my games are playable" — a fact about
    /// them, across every game. Here the question is what goes into one config.json, and a game
    /// given a language of its own must not be written with somebody's global answer.
    /// </summary>
    private string TargetFor(GameReport report, LoaderDescriptor descriptor,
                             InstallerSettings settings) =>
        GameLanguages.TargetFor(report, descriptor,
            GameLanguages.Resolve(settings.TargetLanguage, _platform.SystemLanguage()));

    /// <summary>
    /// The language this game will keep whatever anybody picks, or null when the picker decides.
    ///
    /// 🔴 A game already holding a translation keeps that translation's language — see
    /// GameLanguages, which explains at length why: the target of a file is not a preference, it is
    /// what the file IS. The rule is right; what was missing was anybody SAYING so. Without this,
    /// the language row of a game's own settings can be changed, stored, applied, and produce
    /// nothing at all — the exact shape of "a setting silently without effect" this program refuses
    /// to leave behind anywhere else.
    /// </summary>
    private (string? Language, bool Published) LanguagePinnedTo(GameReport report,
                                                                GamePreference preference)
    {
        if (InstalledDescriptor(report) is not { } descriptor) return (null, false);

        // 🔴 **Pinned by the FACT, not by a disagreement.** This asked whether the imposed language
        // happened to differ from the person's own preference, and unlocked the picker when the
        // two matched — so on a game whose published translation was French, somebody whose
        // default is also French got an editable language field on a translation that can never
        // change language. Worse on somebody else's translation, where nothing about it is theirs
        // to move.
        //
        // What made it invisible: ApplyOwnSettingsAsync writes TargetFor() regardless, so the
        // pick was silently discarded. A control that changes nothing is worse than no control —
        // it is a promise the product does not keep.
        //
        // The rule is the mod's, so the two agree: TranslatorCore.AreLanguagesLocked is
        // "something of this lineage is published", full stop.
        if (report.MatchingOnline is { TargetLanguage: { Length: > 0 } published })
            return (published, true);

        // Not published, but a file is being built here. TargetFor keeps its target for a reason —
        // retargeting mid-work orphans every line already captured — so the picker must not claim
        // otherwise. Said differently from the case above: this one IS changeable, in the game.
        if (report.LocalTranslation is not null)
        {
            var (_, target) = LocalTranslationProbe.ReadLanguages(report.Game.Path, descriptor);
            if (target is { Length: > 0 }) return (target, false);
        }

        return (null, false);
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
        // That was backwards: what this game holds against what would be written is precisely the
        // information somebody needs in order to decide whether to tick it. Hiding it left the
        // choice to be made blind, and an unticked box looking like a game with nothing to settle.
        if (!_settings.Current.Reviewed) yield break;

        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) yield break;

        var snapshot = GameConfig(report);
        var ticked = preference.UsesModDefaults(snapshot);

        // ⚠ Said rather than left blank, and it was left blank. A game the mod has never run in
        // has no config.json, so there is nothing to compare and the whole block vanished — on the
        // one game where the most is about to be written. Silence there is indistinguishable from
        // "nothing will happen".
        if (!snapshot.Exists)
        {
            yield return Callout(
                "This game has no configuration yet. One will be created, from "
                + (ticked ? "Mod defaults." : "this game's own settings."),
                "CalloutInfoBg", "StatusInfo");
            yield break;
        }

        // 🔴 **The hotkey is NOT in this list, and that is the whole point of the two blocks.**
        // This callout says what the box directly above it writes. The hotkey is governed by a box
        // of its own, further down, and it has a comparison callout of its own beneath that box.
        // Showing it here would put a line under a control that does not command it — which is
        // exactly what somebody would then click, and nothing would happen.
        //
        // ⚠ It is still counted by SettingsWouldChangeAnything and by the one-click's figure: it IS
        // a modification, and the split is about WHERE it is shown, not about whether it happens.
        var differences = Differences(report, preference)
            .Where(d => d.Key != GameConfigWriter.HotkeyKey)
            .ToList();

        // Nothing to settle. Said in one quiet line rather than in nothing at all: an empty space
        // where a warning sometimes appears reads as a block that failed to draw, and it is the
        // answer somebody unticking the box is hoping for.
        if (differences.Count == 0)
        {
            yield return new TextBlock
            {
                Text = ticked
                    ? "This game already matches Mod defaults."
                    : "This game already matches its own settings.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
                Foreground = Brush("TextMuted"),
            };
            yield break;
        }

        var writes = differences.Count(d => d.Writes);

        var body = new StackPanel { Spacing = 4 };

        body.Children.Add(new TextBlock
        {
            // ⚠ The sentence NAMES what it will be written with, because that is now a real
            // question: Mod defaults and this game's own settings are two different sources, and
            // the reader cannot tell which is in force from the values alone.
            Text = (writes == 1 ? "One setting here differs from " : $"{writes} settings here differ from ")
                 + (ticked ? "Mod defaults:" : "this game's own settings:"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        foreach (var difference in differences)
        {
            // ⚠ Two sentences, not one with a flag. A line that WILL be written is an announcement
            // — the arrow says something is about to move. A line that will not is a comparison
            // offered so a decision can be taken, and dressing it with the same arrow would promise
            // a change that is not coming.
            body.Children.Add(new TextBlock
            {
                // ⚠ The kept line names Mod defaults rather than saying "yours". Only the hotkey
                // produces a non-writing difference, and its replacement can only ever come from
                // Mod defaults — there is deliberately no per-game hotkey. See GameModOverrides.
                Text = difference.Writes
                    ? $"• {difference.Label}: {difference.InGame} → {difference.Ours}"
                    : $"• {difference.Label}: {difference.InGame} — kept. "
                      + $"Mod defaults uses {difference.Ours}.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });

            // Indented under its own line rather than appended to it: this is a caveat about ONE
            // setting, and folded into the row it would read as part of the value.
            //
            // ⚠ Only where it applies. The hotkey's caveat is about replacing the key you chose in
            // the game — printing it beside a line that is deliberately keeping that key would say
            // the opposite of what is happening.
            if (difference.Note is { } note && difference.Writes)
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

        // ⚠ Offered whether or not the box above is ticked. Unticking never meant "I may no longer
        // apply anything here": keeping the control IS the reason somebody unticks, and taking the
        // button away takes that control with it.
        //
        // Its words follow the source, because they are two different acts: writing what everybody
        // gets, or writing what this game alone was given.
        // 🔴 **ONE button, ONE function, whatever the box says: put Mod defaults onto this game's
        // configuration.** It was relabelled by the box for a while, which made it two buttons
        // wearing one name — and left nobody able to say what a click would write. The box decides
        // whether an install does this on its own; it does not change what this button is.
        if (writes > 0)
        {
            var apply = new Button
            {
                Content = "Apply Mod defaults to this game",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = !_running.IsRunning(report.Game),
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            };

            apply.Click += async (_, _) => await ApplyDefaultsAsync(report, descriptor, preference);
            body.Children.Add(apply);
        }

        // 🔴 **Unticked is the CAUTIOUS case, and the colours said the opposite.** Ticked means
        // "set this game up from Mod defaults" — applying them is the thing that was asked for, so
        // it is ordinary. Unticked means "do not use Mod defaults here": pushing them in anyway is
        // the act to think twice about, and it is the one that was painted as routine.
        var (background, edge) = ticked
            ? ("CalloutInfoBg", "StatusInfo")          // asked for: Mod defaults belong here
            : ("CalloutWarningBg", "StatusWarning");   // refused: applying goes against the box

        var notice = Callout(body, background, edge);
        ((Border)notice).Margin = new Avalonia.Thickness(0, 8, 0, 0);
        yield return notice;
    }

    /// <summary>
    /// The in-game hotkey: a brick of its own, with its state, its question, its capture and its
    /// verb.
    ///
    /// 🔴 **Same shape and same ORDER as the block above: the box, the callout of what it writes,
    /// then the setting of one's own.** The box above governs the settings and carries their list;
    /// this one governs the key and carries its own. Folded into the list above, this line sat
    /// under a control that does not command it — so ticking that box would leave it stubbornly
    /// unchanged, with nothing on screen to explain why. And laid out in a different order from its
    /// twin, it made the reader relearn the layout halfway down a section they had just understood.
    ///
    /// ⚠ Asked about this game, and nowhere else. Inside it, the mod captured the key against the
    /// real keyboard, which is the only measurement that exists — so the question is never "do I
    /// replace hotkeys" but "do I replace THIS one", unanswerable without both keys in front of
    /// you. That is why it left the defaults screen, and it is not going back.
    ///
    /// 🔴 **Never suppressed by anything in the settings form.** It was, for a day: a hotkey row
    /// there hid this box, on the argument that naming a key was already the answer. That got it
    /// backwards — it let a form decide, out of sight of the key being replaced, exactly what this
    /// box exists to keep in sight. The key is settable again, but HERE, beside the question.
    /// </summary>
    private IEnumerable<Control> HotkeyDecision(GameReport report, GamePreference preference,
                                                Action refresh)
    {
        if (!_settings.Current.Reviewed) yield break;

        var descriptor = InstalledDescriptor(report);

        // Nothing installed to write into. The brick has no state to report and no verb to offer,
        // which is exactly what the loader and mod cards do in the same situation.
        if (descriptor is null) yield break;

        var inGame = GameConfig(report).InGameHotkey;

        // ⚠ Read from the same comparison that feeds the block above — one source, so the two can
        // never disagree about this key. Null means there is nothing to REPORT: the game already
        // agrees, or the key that would be written is one that cannot travel between games. The
        // capture below is offered either way; being settled is not a reason to take the control
        // away.
        var difference = Differences(report, preference)
            .FirstOrDefault(d => d.Key == GameConfigWriter.HotkeyKey);

        // ⚠ The box only where there is something to DECIDE. A game with no key of its own has
        // nothing to protect: the key is written outright, and a box asking permission to replace
        // a key that does not exist would be a question about nothing.
        if (inGame is not null)
        {
            // ⚠ "with mine" named nobody. The key that would replace it comes from Mod defaults,
            // which is a screen with a title — and on a machine whose games may belong to different
            // people, a first person is not merely vague, it claims something.
            var replace = new CheckBox
            {
                Content = "Replace this game's key with the one in Mod defaults",
                IsChecked = preference.ReplaceHotkey,
                FontSize = 12,
                Margin = new Avalonia.Thickness(0, 12, 0, 0),
            };

            replace.IsCheckedChanged += (_, _) =>
            {
                preference.ReplaceHotkey = replace.IsChecked == true;
                _preferences.Set(report.Game.Path, preference);

                // ⚠ Posted, unlike the box governing the whole section. That one is a sibling of
                // the blocks a redraw empties and survives it; this one lives inside such a block,
                // so calling the redraw from its own event destroys the box mid-handler and takes
                // the keyboard focus with it — it is left looking pressed and the next Space goes
                // nowhere.
                Avalonia.Threading.Dispatcher.UIThread.Post(refresh);
            };

            yield return replace;
        }

        // 🔴 **Same order as the block above: control, then the callout of what it writes, then the
        // setting of one's own.** This block had the last two the other way round for no reason at
        // all, which is the kind of inconsistency that makes a screen feel arbitrary — the reader
        // has to relearn the layout halfway down a section they had just understood.
        if (difference is not null)
        {
            var state = new StackPanel { Spacing = 4 };

            state.Children.Add(new TextBlock
            {
                // The same sentence shape as every other difference, which is what "the same
                // feeling" asked for: it IS one key of one config.json, and it should read like one.
                Text = difference.Writes
                    ? $"• {difference.Label}: {difference.InGame} → {difference.Ours}"
                    : $"• {difference.Label}: {difference.InGame} — kept. "
                      + $"Mod defaults uses {difference.Ours}.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });

            // Same rule as the block above: orange only when something somebody chose is about to
            // be replaced.
            var reported = difference.Writes
                ? Callout(state, "CalloutWarningBg", "StatusWarning")
                : Callout(state, "CalloutInfoBg", "StatusInfo");

            ((Border)reported).Margin = new Avalonia.Thickness(0, 6, 0, 0);
            yield return reported;
        }

        // 🔴 **The same capture as Mod defaults, for THIS game.** The brick would be incomplete
        // without it: one could see both keys and take the other one, but not choose a third — and
        // a key is precisely the setting most likely to need to differ from one game to the next.
        // The control is the shared HotkeyEditor, so the refusals it enforces (a key Unity cannot
        // name, a key that means something else in another game) are the same ones Mod defaults
        // enforces, in the same words.
        var takesDefault = preference.ReplaceHotkey && inGame is not null;

        // 🔴 **It shows what THIS GAME uses — the field says so, and every other field on this card
        // is filled the same way.** It was seeded from Mod defaults for a while, so a field titled
        // "Key for this game" displayed a key the game does not use, on the one screen whose whole
        // promise is to show what the game holds.
        //
        // ⚠ warnOnArrival: false is what made that honest. A game's key is very often a character
        // key — captured in the game, against the keyboard as that game reads it — and the editor
        // used to greet the reader by declaring their own working choice unusable. It is only
        // unusable FROM HERE, which matters when choosing a new one and not before.
        var editor = new HotkeyEditor(
            preference.Mod?.SettingsHotkey ?? inGame ?? _settings.Current.SettingsHotkey,
            Brush("TextMuted"), Brush("StatusWarning"), warnOnArrival: false);

        // 🔴 **Held, not stored.** Every keystroke used to land in preference.Mod.SettingsHotkey
        // and be written to the preferences file straight away — so a key merely tried out was
        // remembered, counted in "N set for this game", and carried into the next install by
        // somebody who never confirmed it. The block already had a verb; what it lacked was
        // anything to press it FOR.
        //
        // The button below is that verb, and it now does both halves: remember the key, and write
        // it into the game. Nothing before it.
        var draftKey = preference.Mod?.SettingsHotkey;

        Button? write = null;

        editor.Changed += () =>
        {
            draftKey = editor.Value;
            RefreshHotkeyApply();
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };

        row.Children.Add(new TextBlock
        {
            Text = "Key for this game",
            Width = 120,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextMuted"),
        });

        row.Children.Add(editor.Row);

        // ⚠ Greyed rather than hidden, and never without words. Ticking the box above means "take
        // the key from Mod defaults", which is an answer to this very question — leaving the
        // capture live would offer a third key that would then not be written.
        editor.Row.IsEnabled = !takesDefault;

        ToolTip.SetTip(editor.Row, takesDefault
            ? "Untick the box above to choose a key here instead."
            : "Only keys every game detects the same way can be set from here.");

        yield return row;
        yield return editor.Problem;

        // Its own verb, like every other brick: one key, written on its own. Going through the
        // settings apply would write the language, the backend and the update preferences in the
        // same breath, which is not what a button that changes a shortcut may do.
        //
        // ⚠ "Apply (N)", the same as every other block. It was "Apply this key to the game" — a
        // sentence on a button, which is the one thing a label must never be here: this interface
        // ships in no language but English, so every extra word is read in somebody's fourth.
        //
        // ⚠ And the count is NOT dropped because this block holds one setting. The count is the
        // convention: a reader learns "Apply (N)" once and recognises it everywhere. An exception
        // made for being obvious is an exception somebody has to notice.
        write = ScopeMark.Marked(EditSide.Local, "Apply", enabled: false);
        write.FontSize = 12;
        write.HorizontalAlignment = HorizontalAlignment.Left;
        write.Margin = new Avalonia.Thickness(120, 4, 0, 0);

        write.Click += async (_, _) =>
        {
            if (draftKey is not { } chosen) return;

            Busy(true, "Applying the key...");

            var result = new GameConfigWriter().ApplyOne(
                report.Game.Path, descriptor, GameConfigWriter.HotkeyKey, chosen, "in-game hotkey");

            Busy(false, "Ready.");

            if (!result.Written)
            {
                await MessageAsync("Nothing was changed",
                    $"The key could not be written ({result.Failure}).");
                return;
            }

            // ⚠ Remembered only now, and only because it reached the game. A key stored without
            // having been written is a key the next install would carry on somebody's behalf.
            preference.Mod ??= new GameModOverrides();
            preference.Mod.SettingsHotkey = chosen;
            _preferences.Set(report.Game.Path, preference);

            await ShowSelectedAsync();
        };

        RefreshHotkeyApply();
        yield return write;

        void RefreshHotkeyApply()
        {
            if (write is null) return;

            var pending = draftKey is { } key && !string.Equals(key, inGame, StringComparison.Ordinal);

            // ⚠ SetLabel, never Content: the button holds its scope marks beside the text.
            ScopeMark.SetLabel(write, pending ? "Apply (1)" : "Apply");
            write.IsEnabled = pending && !_running.IsRunning(report.Game) && !takesDefault;

            ToolTip.SetTip(write, !pending
                ? "This game already uses that key."
                : takesDefault
                    ? "Untick the box above to choose a key here instead."
                    : _running.IsRunning(report.Game)
                        ? $"{report.Game.Name} is running, so its files are locked."
                        : "Writes this key into the game, and remembers it for a later install.");
        }

        // 🔴 **THE REASON — one line, and it has to be TRUE.** It was a paragraph, then it was
        // "a key can mean something different in each game", which is not a thing a key does: a key
        // has no intent. Two facts make the replacement worth asking about, and both are ordinary:
        // the same physical key is not detected identically by every game, and the game may already
        // have bound that key to something of its own.
        yield return new TextBlock
        {
            Text = "The same key is not detected the same way in every game, and this game may "
                 + "already use it for something else.",
            FontSize = 11,
            Margin = new Avalonia.Thickness(120, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        };

    }

    /// <summary>
    /// Where this game's configuration stands against what would be written into it.
    ///
    /// One composition, read by the card, by the band under it and by the confirmation — three
    /// places that were each free to ask the question their own way, which is how two of them end
    /// up answering it differently.
    /// </summary>
    private IReadOnlyList<ConfigDifference> Differences(GameReport report, GamePreference preference)
    {
        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) return Array.Empty<ConfigDifference>();

        // 🔴 **Against Mod defaults, always.** This list answers one question — what would applying
        // Mod defaults change here — and the button under it does exactly that. Comparing against
        // the per-game resolution instead made the list mean something different depending on a
        // checkbox, which is how a button ends up unable to say what it writes.
        //
        // This game's OWN settings are a different brick: the form shows them, and its own verb
        // writes them.
        var settings = _settings.Current;

        return new GameConfigWriter().Compare(
            report.Game.Path, descriptor, settings,
            TargetFor(report, descriptor, settings), preference);
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
            var note = new TextBlock
            {
                Text = instead,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
            };

            // ⚠ The newer version is stated even here, and it did not used to be: this branch
            // REPLACED the standing line, so a loader somebody else installed showed its version
            // and nothing about the catalog knowing a newer one. Fact first, then the reason we
            // leave it alone — in that order, because the reason only makes sense once you know
            // what is being left alone.
            if (standing is { UpdateAvailable: true })
            {
                var available = new TextBlock
                {
                    Text = $"{standing.Available} is available.",
                    FontSize = 12,
                    Foreground = Brush("StatusInfo"),
                };

                return new StackPanel { Spacing = 2, Children = { available, note } };
            }

            return note;
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

        // Both numbers known, neither rankable — two publication lines of the same version. Said
        // plainly instead of "Up to date", which is what it used to fall through to: reassurance
        // is the worst thing to offer when the honest answer is that nobody can tell.
        if (standing.NotComparable)
        {
            return new TextBlock
            {
                Text = $"{standing.Available} is published on the channel you chose. It is a "
                     + $"different line from {standing.Installed}, so neither is newer.",
                FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            };
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
            return "Mod defaults has not been filled in yet, so there is nothing to configure this game with.";

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
                Text = "This game is fully set up. Nothing left for one click to do.",
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(settled, 0);
            done.Children.Add(settled);

            if (PlayButton(report.Game, small: false, report) is { } start)
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
                    Content = "Open Mod defaults",
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
                    ? string.Join("  ·  ", steps.Select(step => step.Text))
                    : "This game is installed and up to date.",
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
                // ⚠ "here" named nothing — the box, the card, the game? The rule is to name the
                // thing: what gets replaced is the translation this game currently runs.
                Content = replaces
                    ? "and replace the one this game runs"
                    : "with a translation",
                IsChecked = _takeTranslation,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush(replaces ? "StatusWarning" : "TextSecondary"),
            };

            ToolTip.SetTip(withTranslation, replaces
                ? "Not ticked on purpose. " + TranslationOffers.Caution(offer)
                  + " Tick it to take the community one anyway — you will be asked again, with "
                  + "what is at stake spelled out, and a copy is kept aside either way."
                // ⚠ Says what unticking DOES — skip the download — not what it feels like. The
                // previous wording, "untick to start from a blank sheet", promised a reset this
                // box has never performed: it decides whether a translation comes down with the
                // install, and on a game that already holds one, unticking leaves it exactly
                // where it is.
                : "Takes the best-ranked translation published in your language. Untick to install "
                  + "without one and build your own as you play.");

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
            Content = OneClickVerb(steps),
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
        if (PlayButton(report.Game, small: false, report) is { } play) right.Children.Add(play);

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
    /// The kind of act a one-click step is, apart from the sentence describing it.
    ///
    /// ⚠ Exists so the BUTTON can be named from the very list it is about to run. It was labelled
    /// "OneClick Set Up this Game" always — on a game already set up whose only pending act was an
    /// update, and on one where nothing was pending at all. Deriving the label from the same list
    /// the sentence beside it comes from is what makes the two incapable of disagreeing.
    /// </summary>
    private enum OneClickAct
    {
        InstallLoader, UpdateLoader, InstallMod, UpdateMod,
        ApplySettings, TakeTranslation, UpdateTranslation, ReplaceTranslation,
    }

    /// <summary>One act, and the sentence shown for it.</summary>
    private sealed record OneClickStep(OneClickAct Act, string Text);

    /// <summary>
    /// What one click would actually do here, in order, and nothing it would not.
    ///
    /// Recomputed rather than remembered, so the sentence cannot describe a state the game left
    /// behind two rescans ago.
    /// </summary>
    private IEnumerable<OneClickStep> OneClickSteps(GameReport report, GamePreference preference)
    {
        if (report.InstalledLoader is null && report.RecommendedLoader is { } loader)
            yield return new(OneClickAct.InstallLoader, $"install {loader.Display}");
        else if (report.LoaderUpdateOffered)
            yield return new(OneClickAct.UpdateLoader,
                             $"update the loader to {report.LoaderStanding!.Available}");

        if (report.InstalledPluginVersion is null)
            yield return new(OneClickAct.InstallMod, "install the mod");
        else if (report.PluginStanding is { UpdateAvailable: true } pluginStanding)
            yield return new(OneClickAct.UpdateMod, $"update the mod to {pluginStanding.Available}");

        // ⚠ Only when it would actually change something. This step used to be listed whenever the
        // box was ticked — which it is by default — so the list was NEVER empty, "nothing left for
        // one click to do" was unreachable on any game, and the button stayed lit offering to
        // rewrite a config.json with the values already in it.
        //
        // ⚠ Conditional on the box, because the one-click applies the preference and decides
        // nothing of its own. Unticked, this game's configuration is left alone — its own settings
        // are written by their own button, which is what every other brick on this card does too.
        if (preference.UsesModDefaults(GameConfig(report)) && _settings.Current.Reviewed
            && SettingsWouldChangeAnything(report, preference))
        {
            yield return new(OneClickAct.ApplySettings, SettingsStepText(report, preference));
        }

        if (!_takeTranslation || PickTranslation(report) is not { } chosen) yield break;

        // Worded by what it would DO, not by what exists. The three are different acts and the
        // person is about to authorise one of them with a single click.
        yield return TranslationOffers.For(report, chosen) switch
        {
            TranslationOffer.ReplacesWork => new(OneClickAct.ReplaceTranslation,
                "replace the translation here, losing what was never uploaded (it will ask first)"),
            TranslationOffer.ReplacesChoice => new(OneClickAct.ReplaceTranslation,
                "swap the translation here for another one (it will ask first)"),
            TranslationOffer.FreeToTake when report.LocalTranslation is not null =>
                new(OneClickAct.UpdateTranslation, "update the translation"),
            _ => new(OneClickAct.TakeTranslation,
                $"take the {chosen.TargetLanguage ?? Languages.NameOf(_settings.ResolveTargetLanguage())} "
                + $"translation by {People.MentionOf(chosen.Author, _settings.Current.ApiUser)}"),
        };
    }

    /// <summary>
    /// The words for the settings step: WHERE the values come from, and how many move.
    ///
    /// ⚠ "apply your settings" said neither, and both matter now. A reader cannot tell Mod defaults
    /// from this game's own answers by looking at the result, so the sentence has to NAME which is
    /// in force — and a figure turns a promise into something checkable before the click rather
    /// than after it.
    ///
    /// No figure on a game with no configuration yet: nothing is CHANGING there, everything is
    /// being created, and "12 changes" would count a file into existence.
    /// </summary>
    private string SettingsStepText(GameReport report, GamePreference preference)
    {
        // Only ever reached with the box ticked, so there is one source to name and no branch.
        var changes = Differences(report, preference).Count(d => d.Writes);

        return changes > 0 ? $"apply Mod defaults ({changes} changes)" : "apply Mod defaults";
    }

    /// <summary>
    /// Whether writing the defaults into this game would change anything at all.
    ///
    /// Three answers, and the first two are yes for the same reason: there is no configuration
    /// here yet, so applying the defaults creates one. Only a game that HAS a config can be found
    /// to already agree with them — which is exactly what <see cref="GameConfigWriter.Compare"/>
    /// answers, and what the list of differences on this card already shows.
    /// </summary>
    private bool SettingsWouldChangeAnything(GameReport report, GamePreference preference)
    {
        var descriptor = InstalledDescriptor(report);

        // No loader yet: nothing has been written anywhere, and the install will write it.
        if (descriptor is null) return true;

        if (!LocalTranslationProbe.HasConfig(report.Game.Path, descriptor)) return true;

        // ⚠ Only the lines that WRITE. A difference shown so a decision can be taken — the hotkey
        // this game is deliberately keeping — is not work for the button: counting it would light
        // a one-click whose settings step then changed nothing, which reads as a failure rather
        // than as "there was nothing to do". See ConfigDifference.Writes.
        return Differences(report, preference).Any(d => d.Writes);
    }

    /// <summary>
    /// The button's own words, taken from the acts it is about to perform.
    ///
    /// ⚠ Named after the DOMINANT act, in the order somebody would describe the job themselves:
    /// putting something in place outranks updating it, updating the tool outranks fetching a
    /// translation, and settings come last because they are the one act nobody would call a
    /// set-up. An empty list says so instead of pretending — the button is disabled beneath it.
    /// </summary>
    private static string OneClickVerb(IReadOnlyList<OneClickStep> steps)
    {
        if (steps.Count == 0) return "Nothing to OneClick";

        if (steps.Any(s => s.Act is OneClickAct.InstallLoader or OneClickAct.InstallMod))
            return "OneClick Set Up this Game";

        if (steps.Any(s => s.Act is OneClickAct.UpdateLoader or OneClickAct.UpdateMod))
            return "OneClick Update this Game";

        if (steps.Any(s => s.Act is OneClickAct.UpdateTranslation))
            return "OneClick Update the Translation";

        if (steps.Any(s => s.Act is OneClickAct.TakeTranslation or OneClickAct.ReplaceTranslation))
            return "OneClick Get the Translation";

        return "OneClick Apply your Settings";
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

        var steps = OneClickSteps(report, preference).ToList();

        // ⚠ One block per step rather than one paragraph, so the settings step can carry its own
        // detail. It used to be a single joined string, and "apply your settings" was therefore a
        // sentence with nothing behind it: the one act about to rewrite a file the player has been
        // living in was the only one whose consequences could not be read before agreeing to them.
        foreach (var step in steps)
        {
            body.Children.Add(new TextBlock
            {
                Text = "• " + step.Text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
            });

            if (step.Act is OneClickAct.ApplySettings)
                foreach (var detail in SettingsDetail(report, preference)) body.Children.Add(detail);
        }

        // ⚠ Said, not omitted. With settings of its own that already match, this game produces no
        // settings step at all — and an absence reads as an oversight rather than as a decision.
        // It is exactly the reassurance somebody who unticked the box is looking for before they
        // press a button called "Set it up".
        if (!steps.Any(s => s.Act is OneClickAct.ApplySettings)
            && !preference.UsesModDefaults(GameConfig(report)))
        {
            body.Children.Add(new TextBlock
            {
                Text = "This game keeps its own settings — nothing in its configuration changes.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });
        }

        if (translation is not null && report.LocalTranslation is { } local)
        {
            foreach (var warning in ReplacementWarnings(report, local, translation))
                body.Children.Add(warning);
        }

        if (!await ConfirmAsync($"Set up {report.Game.Name}?", body, "Set it up")) return;

        // ⚠ Read BEFORE anything is written: applying the settings makes this game "configured",
        // and what we need to know afterwards is what it was beforehand.
        var configBefore = GameConfig(report);

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

            RememberDefaultsWereWritten(report, plan, configBefore);

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
    /// Records, once the settings have actually been written, that this game follows Mod defaults.
    ///
    /// 🔴 **Without this, a game set up from here stops following the defaults the instant it is
    /// set up.** <see cref="GamePreference.UsesModDefaults"/> answers "nobody decided" by asking
    /// the game — and a game we have just configured answers "I am configured", so both boxes on
    /// the Set-up tab fall back to unticked with the defaults still sitting in the file. The guard
    /// they implement protects a configuration made INSIDE the mod, against a first click that
    /// would overwrite it. It was never meant to protect the values this tool has just written.
    ///
    /// ⚠ Only where all three hold, and each one is a real case:
    /// the config was written at all (a loader-only install writes nothing and decides nothing);
    /// nobody has answered the question yet (an explicit "no" must survive an install);
    /// and it was the defaults that went in (a game keeping its own settings keeps its answer too).
    ///
    /// ⚠ The hotkey follows the same reasoning, and only when the game had none: the key now in
    /// that file comes from Mod defaults, so the honest answer to "replace this game's key" is yes
    /// — there was no key of its own to protect. Where a game did carry one, we left it alone and
    /// the answer stays no.
    /// </summary>
    private void RememberDefaultsWereWritten(GameReport report, InstallPlan plan,
                                             GameConfigSnapshot before)
    {
        if (plan.Settings is null || plan.TargetLanguage is null) return;

        var preference = _preferences.Read(report.Game.Path);
        if (preference.ApplyModDefaults is not null) return;
        if (!preference.UsesModDefaults(before)) return;

        preference.ApplyModDefaults = true;
        if (before.InGameHotkey is null) preference.ReplaceHotkey = true;
        _preferences.Set(report.Game.Path, preference);
    }

    /// <summary>
    /// The settings step, line by line, folded away until somebody asks.
    ///
    /// ⚠ Folded on purpose. The confirmation sizes itself to its content, at a fixed 560 wide and
    /// not resizable, so a game diverging on a dozen settings would push the buttons off the
    /// bottom of the screen — a dialog nobody can answer. Reachable in one click, never in the way.
    ///
    /// Empty when there is nothing to list: on a game with no config.json yet, everything is being
    /// created rather than changed, and the step's own words already say so.
    /// </summary>
    private IEnumerable<Control> SettingsDetail(GameReport report, GamePreference preference)
    {
        var writing = Differences(report, preference).Where(d => d.Writes).ToList();
        if (writing.Count == 0) yield break;

        var lines = new StackPanel { Spacing = 2 };

        foreach (var difference in writing)
        {
            lines.Children.Add(new TextBlock
            {
                Text = $"{difference.Label}: {difference.InGame} → {difference.Ours}",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });
        }

        yield return new Expander
        {
            Header = new TextBlock
            {
                Text = "what changes",
                FontSize = 11,
                Foreground = Brush("TextMuted"),
            },
            Content = lines,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(14, 0, 0, 0),
        };
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

        var result = new TranslationInstaller(_platform)
            .Install(report.Game, loader, json, translation.FileHash,
                     // Whose work is being put in place — it is what the backup row will read.
                     People.MentionOf(translation.Author, _settings.Current.ApiUser));

        if (!result.Written)
            return $"The translation could not be written ({result.Failure}). Everything else is in place.";

        // Remembered so the card can say which one this game runs, and so a later one-click does
        // not silently pick a different translation than the one already in place.
        var preference = _preferences.Read(report.Game.Path);
        preference.TranslationId = translation.Id;
        _preferences.Set(report.Game.Path, preference);

        // ⚠ Names the place somebody can act from, not a folder on disk. "It is in
        // .ugt/removed/translations-20260817.json" is an instruction to open a file manager;
        // "Backups" is a button they have already seen on this card.
        var message = "The translation is in place.";
        if (result.BackupPath is not null)
            message += " What was here is kept under Backups.";

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
        // 🔴 **The box, and nothing else.** An install and the one-click apply the PREFERENCE — they
        // invent nothing. Ticked, this game is set up from Mod defaults; unticked, its own
        // configuration is left exactly as it is, and the settings it holds are applied by their own
        // button. That is what the box has always meant, and taking it out of this line was me
        // letting the one-click decide.
        var writeSettings = settings && preference.UsesModDefaults(GameConfig(report))
                            && _settings.Current.Reviewed;

        // ⚠ Per game, because that is where the risk is taken: putting a pre-release plugin in one
        // game to test a fix is a different decision from putting it in all of them. Read from the
        // game's own configuration whether or not the settings are being written — which build gets
        // installed is not the same question as which values get written.
        var resolved = SettingsFor(report, preference);

        var plan = new InstallEngine(_platform, _catalog)
        {
            // The stream this window announces in the picker, so the plan installs what was shown.
            BepInEx6Channel = _settings.Current.BepInEx6Channel,
        }.Plan(
            report,
            resolved.Channel == "beta" ? ReleaseChannel.Beta : ReleaseChannel.Stable,
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
                                       // ⚠ Offered, not merely available: a newer loader we did
                                       // not install is reported and never written.
                                       || report.LoaderUpdateOffered),
            InstallPlugin = plugin,

            // Which BUILD of that loader: the one somebody picked by hand, and otherwise the one
            // Plan() resolved for the chosen channel — the very build this card names.
            //
            // ⚠ The `?? plan.Build` is the whole point. Written as `_chosenBuild()` alone, a folded
            // "Use another build" expander — the state every ordinary install is in — erased the
            // resolved build and fell back to the catalogue's pinned archive. The card announced
            // one version and the installer wrote another.
            //
            // Still null when nothing was resolved at all (offline, publisher silent), and the
            // engine then uses the pinned archives — which is also what the card announces then,
            // so the two still agree.
            Build = _chosenBuild() ?? plan.Build,
        };
    }

    /// <summary>
    /// The mod sitting where the loader does not look first — and what settles it.
    ///
    /// ⚠ TWO faults share that description, and the right answer is opposite. Telling them apart
    /// is the whole of this method:
    ///
    ///   · a copy in the documented place TOO — a DUPLICATE. Both loaders read the shared folder
    ///     before its subfolders, so the OLDER assembly runs: updates land correctly and change
    ///     nothing anybody can see. Removing the stray settles it.
    ///
    ///   · nothing in the documented place — a MISPLACED install, where there is nothing to
    ///     remove. Deleting the only assembly in the game would uninstall the mod while claiming
    ///     to repair it. This offered exactly that until the two cases were separated.
    ///
    /// ⚠ And repairing is not just moving a DLL. Under BepInEx the mod keeps its files beside its
    /// own assembly (userdata_dir == plugin_dir), so a copy that ran from plugins/ wrote the
    /// translation there. The move takes them along, or the mod restarts on an empty folder with
    /// the work still on disk, two levels up, invisible.
    /// </summary>
    private IEnumerable<Control> DuplicatePluginNotice(GameReport report)
    {
        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) yield break;

        var strays = report.StrayPluginDirectories;
        if (strays.Count == 0) yield break;

        var home = Path.Combine(report.Game.Path,
            descriptor.PluginDir.Replace('/', Path.DirectorySeparatorChar));

        // Read from the report, which the CLI reads too — the disk is consulted once, in the
        // inventory, and every screen tells the same story about it.
        var duplicate = report.PluginInPlace;

        var body = new StackPanel { Spacing = 4 };

        body.Children.Add(new TextBlock
        {
            Text = duplicate
                ? strays.Count == 1
                    ? $"The mod is installed twice — here and in {strays[0]}/."
                    : $"The mod is installed more than once — here and in {string.Join(", ", strays)}."
                : $"The mod is installed in {strays[0]}/ instead of {descriptor.PluginDir}/.",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("StatusWarning"),
        });

        body.Children.Add(new TextBlock
        {
            // ⚠ Says which copy is MANAGED, never which one runs. That second claim was written
            // here as fact, deduced from scan order and measured nowhere: loaders arbitrate two
            // assemblies carrying the same plugin id by their own rules, which differ between
            // loaders and between versions of one.
            Text = duplicate
                ? $"This tool only ever updates the one in {descriptor.PluginDir}/. Which of the two "
                  + "the loader actually runs is its own decision, so an update can install "
                  + "correctly and change nothing you can see."
                : "It was not put there by this tool. Depending on the loader and its version, a "
                  + "copy outside the documented folder may load late or not at all — and this is "
                  + "not where an update or a removal looks first.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        });

        var fix = new Button
        {
            Content = duplicate
                ? strays.Count == 1 ? "Remove the other copy" : "Remove the other copies"
                : "Put it back where it belongs",
            FontSize = 12,
            Classes = { "primary" },
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = !_running.IsRunning(report.Game),
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };

        fix.Click += async (_, _) =>
        {
            if (duplicate) await RemoveStrayCopiesAsync(report, descriptor, strays);
            else await RepairMisplacedInstallAsync(report, descriptor, strays[0], home);
        };

        body.Children.Add(fix);
        yield return Callout(body, "CalloutWarningBg", "StatusWarning");
    }

    /// <summary>
    /// Deletes the copies that are not where they belong, the good one staying put.
    ///
    /// ⚠ Asks first. It deletes a file inside somebody's game, and it did it on a single click —
    /// the one act on this card with no way back sat behind less ceremony than picking a language.
    /// </summary>
    private async Task RemoveStrayCopiesAsync(GameReport report, LoaderDescriptor descriptor,
                                              IReadOnlyList<string> strays)
    {
        var listed = string.Join(Environment.NewLine,
            strays.Select(s => $"- {s}/{LocalTranslationProbe.PluginAssemblyName}"));

        var confirmed = await ConfirmAsync(
            strays.Count == 1 ? "Remove the other copy?" : "Remove the other copies?",
            "This deletes:" + Environment.NewLine + listed + Environment.NewLine + Environment.NewLine
            + $"The mod stays installed in {descriptor.PluginDir}/, and that is the version this "
            + "game will run. Nothing else in those folders is touched.",
            strays.Count == 1 ? "Remove the copy" : "Remove the copies");

        if (!confirmed) return;

        // Our assembly by name, nothing else in those folders — the same rule the installer
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
                ? $"The extra copy in {string.Join(", ", removed)} is gone. This game now runs the "
                  + "one version installed here."
                : "Some copies could not be removed:" + Environment.NewLine
                  + string.Join(Environment.NewLine, failed.Select(f => "- " + f)));

        await ShowSelectedAsync();
    }

    /// <summary>
    /// Reinstalls the mod where the loader reads it, taking the files it wrote along.
    ///
    /// ⚠ Files FIRST, install second, and the order is the point: landing them before the
    /// installer runs means it meets a folder that already holds this person's settings and
    /// translation, so it behaves exactly as it does on any update — keeping what it must keep.
    /// Moving them afterwards would overwrite what it had just written.
    /// </summary>
    private async Task RepairMisplacedInstallAsync(GameReport report, LoaderDescriptor descriptor,
                                                   string stray, string home)
    {
        var from = Path.Combine(report.Game.Path, stray.Replace('/', Path.DirectorySeparatorChar));

        // Only under BepInEx does anything travel: MelonLoader keeps its data in UserData/, which
        // the assembly's location never affected.
        var carry = string.Equals(descriptor.UserDataDir, descriptor.PluginDir, StringComparison.OrdinalIgnoreCase)
            ? UserDataInventory.RecognisedDataIn(from)
            : Array.Empty<string>();

        var body = $"The mod moves from {stray}/ to {descriptor.PluginDir}/, where the loader reads it.";

        if (carry.Count > 0)
        {
            body += Environment.NewLine + Environment.NewLine
                  + "Your files move with it, because the mod keeps them beside itself:"
                  + Environment.NewLine
                  + string.Join(Environment.NewLine, carry.Select(c => "- " + c))
                  + Environment.NewLine + Environment.NewLine
                  + "Left behind, the mod would start over on an empty folder.";
        }

        if (!await ConfirmAsync("Put the mod back where it belongs?", body, "Put it back")) return;

        Directory.CreateDirectory(home);

        var stuck = new List<string>();

        foreach (var name in carry)
        {
            var source = Path.Combine(from, name);
            var target = Path.Combine(home, name);

            try
            {
                // Never over something already there: that file is the mod's current state, and
                // this one is from an installation that was not being read.
                if (File.Exists(target) || Directory.Exists(target))
                {
                    stuck.Add($"{name} (already present)");
                    continue;
                }

                if (Directory.Exists(source)) Directory.Move(source, target);
                else File.Move(source, target);
            }
            catch (Exception ex)
            {
                stuck.Add($"{name} ({ex.Message})");
            }
        }

        if (stuck.Count > 0)
        {
            await MessageAsync("Some files stayed behind",
                "These were left in " + stray + "/ and the mod will not read them there:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, stuck.Select(f => "- " + f))
                + Environment.NewLine + Environment.NewLine
                + "Move them by hand if you need them. Nothing was deleted.");
        }

        // The installer writes the documented folder and clears the stray copy on its way.
        await RunModInstallAsync(report);
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
    /// <summary>What this game already says about itself, or null when it says nothing.</summary>
    private string? InGameContext(GameReport report, LoaderDescriptor? descriptor) =>
        GameConfigWriter.InGameValue(report.Game.Path, descriptor, GameConfigWriter.GameContextKey);

    private IEnumerable<Control> GameContextField(GameReport report, GamePreference preference,
                                                  PlanDraft draft, PlanApply applyBar)
    {
        // ⚠ Pre-filled from the game when it already carries one — somebody may well have written
        // it from inside the mod's options while playing. An empty box over an answer that exists
        // invites retyping it, and cannot be told apart from "nothing was ever asked".
        var inGameContext = draft.Context;

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
            Text = inGameContext ?? "",
            Watermark = "Genre, tone, setting - a sentence is enough",
            FontSize = 12,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 84,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };

        // ⚠ Read on every keystroke into the DRAFT, and written by nothing here. It used to save
        // itself on LostFocus — no Apply, on a setting that lands in the game's config.json — and
        // then offered a "Save this into the game" button of its own beside it, so the same answer
        // had two ways of reaching the file and neither was the one the rest of the card uses.
        context.TextChanged += (_, _) =>
        {
            draft.Context = string.IsNullOrWhiteSpace(context.Text) ? null : context.Text.Trim();
            applyBar.Refresh();
        };

        yield return context;

        yield return new TextBlock
        {
            Text = InGameContext(report, InstalledDescriptor(report)) is { } written
                ? $"This game currently says: {written}"
                : "This game has nothing written for it yet.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
            Foreground = Brush("TextMuted"),
        };
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

        // 🔴 **Held, not written.** This wrote straight to disk on every click — the only pair of
        // mod settings in the tool that did, and a plain breach of the rule the rest of it keeps:
        // nothing reaches a game until Apply is pressed. It also made the switch below it
        // meaningless, since there was never a moment where an answer was pending.
        var draft = new PlanDraft(
            StartTranslation: preference.StartTranslation ?? settings.EnableAi,
            GameContext: preference.GameContext ?? InGameContext(report, descriptor));

        var applyBar = PlanApplyBar(report, preference, draft, refresh);

        start.IsCheckedChanged += (_, _) =>
        {
            draft.StartTranslation = start.IsChecked == true;
            applyBar.Refresh();
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
        if (backend is null)
        {
            // ⚠ The bar goes out even here: the switch above it is still answerable, and an answer
            // with nowhere to be confirmed is the fault this whole change is about.
            yield return applyBar.View;
            yield break;
        }

        foreach (var control in GameContextField(report, preference, draft, applyBar))
            yield return control;

        // ⚠ Last, and once. It settles both settings of this block — the switch and the
        // description — exactly as the form below settles the settings IT holds. One block, one
        // Apply, in the same place and with the same words: a reader who has learnt the pattern
        // once should not have to learn it again three inches lower.
        yield return applyBar.View;
    }

    /// <summary>
    /// The Apply of the plan block: same control, same words and same place as the one under
    /// "This game's own settings", because it does the same kind of thing to the same file.
    ///
    /// ⚠ A block that holds settings owns exactly one Apply. Two bricks with two buttons is a
    /// grammar somebody learns once; a bespoke "Save this into the game" beside one field and
    /// silent writes beside another is three behaviours for one idea.
    /// </summary>
    private sealed class PlanApply
    {
        public required Control View { get; init; }
        public required Action Refresh { get; init; }
    }

    private PlanApply PlanApplyBar(GameReport report, GamePreference preference,
                                   PlanDraft draft, Action refresh)
    {
        // Local: this writes into THIS game's config.json and sends nothing anywhere — the same
        // mark the settings form carries, for the same reason.
        var apply = ScopeMark.Marked(EditSide.Local, "Apply", enabled: false);
        apply.Classes.Add("primary");
        apply.FontSize = 12;

        void Redraw()
        {
            var count = draft.Pending;

            // ⚠ SetLabel, never Content: the button holds its scope marks beside the text.
            ScopeMark.SetLabel(apply, count > 0 ? $"Apply ({count})" : "Apply");
            apply.IsEnabled = count > 0 && !_running.IsRunning(report.Game);

            ToolTip.SetTip(apply, count > 0
                ? $"Writes these {count} setting(s) into the game."
                : "Nothing has been changed here.");
        }

        apply.Click += async (_, _) =>
        {
            preference.StartTranslation = draft.Start;
            preference.GameContext = draft.Context;
            _preferences.Set(report.Game.Path, preference);

            await ApplyOwnSettingsAsync(report, preference);
            refresh();
        };

        Redraw();

        return new PlanApply
        {
            View = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
                Children = { apply },
            },
            Refresh = Redraw,
        };
    }

    /// <summary>The two answers this block holds while they wait for Apply.</summary>
    private sealed class PlanDraft
    {
        public PlanDraft(bool StartTranslation, string? GameContext)
        {
            Start = StartTranslation;
            Context = GameContext;
            _start = StartTranslation;
            _context = GameContext;
        }

        private readonly bool _start;
        private readonly string? _context;

        public bool Start { get; set; }
        public string? Context { get; set; }

        public bool StartTranslation { set => Start = value; }

        /// <summary>How many of the two differ from what the game holds. Never a count of fields.</summary>
        public int Pending =>
            (Start != _start ? 1 : 0)
            + (!string.Equals(Context ?? "", _context ?? "", StringComparison.Ordinal) ? 1 : 0);
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
        var author = People.MentionOf(picked.Author, _settings.Current.ApiUser);

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

        // ⚠ **The same act wears the same word on both tabs.** Home says Apply (1) — the norm for
        // every pending change in this program — so this one cannot say "Replace it with this
        // one..." for the identical click: somebody who saw both would have no way to tell whether
        // they are two steps or one.
        //
        // The other two labels stay: without a deliberate choice there is nothing "pending" to
        // apply, and naming the act is then the clearest thing to do.
        var deliberate = preference.TranslationId == picked.Id;

        var actLabel = offer switch
            {
                TranslationOffer.ReplacesWork or TranslationOffer.ReplacesChoice when deliberate
                    => "Apply (1)",
                TranslationOffer.ReplacesWork or TranslationOffer.ReplacesChoice
                    => "Replace it with this one...",
                _ when deliberate => "Apply (1)",
                _ when report.LocalTranslation is not null => "Update the translation",
                _ => "Download this translation",
            };

        // Same act as Apply (1) on the other tab, so it wears the same mark: a file written into
        // this game, nothing sent anywhere.
        var act = ScopeMark.Marked(EditSide.Local, actLabel, !_running.IsRunning(report.Game));
        act.Classes.Add("primary");
        act.HorizontalAlignment = HorizontalAlignment.Left;
        act.Margin = new Avalonia.Thickness(0, 6, 0, 0);

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
            // ⚠ Adopted counts as ours here — that IS the permission, and refusing to act on it
            // afterwards would make the tick a decoration. See GamePreference.AdoptLoader.
            if (!installed.InstalledByUs && !report.LoaderAdopted) return null;

            // Same three verbs as the mod's section, in the same order, for the same reasons.
            // "Reinstall" is what puts back a loader whose files were damaged — reachable only by
            // removing everything first, until now.
            return report.LoaderUpdateOffered
                ? $"Update the loader to {report.LoaderStanding!.Available}"
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
        Busy(true, "Applying Mod defaults...");

        // 🔴 **Mod defaults, and nothing else — this button has ONE function.** It writes the
        // defaults onto this game's configuration, whatever the box says; the box decides whether
        // an install does it unasked, not what this does when pressed. Passing the per-game
        // resolution here made it write something different depending on a checkbox, under a label
        // that promised the defaults — which is why nobody could say what a click would do.
        //
        // This game's OWN settings are a different brick with a different verb: the form applies
        // those (ApplyOwnSettingsAsync).
        var settings = _settings.Current;
        var target = TargetFor(report, descriptor, settings);

        var result = new GameConfigWriter()
            .Apply(report.Game.Path, descriptor, settings, target, perGame: preference);

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

        // Same reading as the one-click, for the same reason: the answer to "does this game follow
        // Mod defaults" is about to become unreadable from the game itself.
        var configBefore = GameConfig(report);

        Busy(true, "Starting...");
        engine.Status += OnEngineStatus;

        var outcome = await engine.ApplyAsync(plan);

        engine.Status -= OnEngineStatus;
        Busy(false, outcome.Success ? "Done." : "Failed.");

        if (outcome.Success) RememberDefaultsWereWritten(report, plan, configBefore);

        await MessageAsync(outcome.Success ? "Installed" : "Nothing was changed", outcome.Message);
        await ShowSelectedAsync();
    }

    /// <param name="fromLoaderSection">
    /// Which button was pressed. The dialogue answers the gesture rather than describing the same
    /// removal twice.
    ///
    /// 🔴 Pressing Uninstall under MOD LOADER and being asked "also remove the mod loader?" reads
    /// as the tool not having noticed. What is implicit is what was clicked; what has to be asked
    /// is everything else. So the two entry points swap which half is settled and which is offered.
    ///
    /// ⚠ From the loader, removing the mod is **not optional**: a plugin whose loader is gone is a
    /// file no program will ever read again, and leaving it behind would report success over a
    /// game left half undone. The box is shown ticked and disabled, so the fact is stated rather
    /// than silently applied.
    /// </param>
    /// <summary>
    /// Takes this game's translation out of the way so another can be started.
    ///
    /// ⚠ **Set aside, never deleted.** The folder it goes to is the one every replaced translation
    /// already uses, and the button beside this one brings it back. A removal nobody can undo would
    /// be a strange thing to offer for "I want to try something else".
    ///
    /// ⚠ What is at stake is stated as a FACT, not as "are you sure": lines that were never
    /// published exist nowhere else, and the person deciding needs the number.
    /// </summary>
    private async Task RemoveTranslationAsync(GameReport report, LoaderDescriptor? descriptor)
    {
        if (descriptor is null) return;

        var lines = report.LocalTranslation?.EntryCount ?? 0;
        var mine = _lineages.For(report.LocalTranslation?.Uuid);

        // Three situations, and the difference is what somebody stands to lose. Published work
        // comes back with its role; unpublished work comes back only from the copy set aside.
        var stake = mine is not null
            ? $"It is published under your account, so you can take it back at any time — with your "
              + (mine.IsMain ? "Main." : "contribution.")
            : report.MatchingOnline is not null
                ? "It came from the community and can be downloaded again."
                : "🔴 It has never been published, so the copy set aside here is the only one left.";

        var unpublished = report.LocalTranslation?.ChangedSinceAncestor;
        var changed = unpublished is > 0
            ? $" {unpublished} line(s) differ from what was last synced."
            : "";

        if (!await ConfirmAsync($"Remove the local translation from {report.Game.Name}?",
                $"{lines} line(s) will be moved out of the game.{changed} {stake}"
                + Environment.NewLine + Environment.NewLine
                + $"A copy is kept aside — the last {TranslationInstaller.BackupsKept} are, and "
                + "Restore local brings them back.",
                "Remove it")) return;

        Busy(true, "Removing the translation...");
        var done = new TranslationInstaller(_platform).Remove(report.Game, descriptor);
        Busy(false, "Ready.");

        if (!done.Written)
        {
            await MessageAsync("Nothing was removed", done.Failure ?? "The file could not be moved.");
            return;
        }

        await ShowSelectedAsync();
    }

    /// <summary>Puts a set-aside translation back, choosing between them when there are several.</summary>
    /// <summary>
    /// This translation as it stood at earlier moments, in one window.
    ///
    /// 🔴 **The same two lists the mod shows, in the same words.** They do not live equally long —
    /// an automatic copy ages out on its own, a saved one stays until somebody removes it — and
    /// two rows that look alike but do not survive alike is how people lose what they thought was
    /// kept. Everything said here comes from Backups; only the drawing belongs to this window.
    ///
    /// ⚠ Every act refuses while the game is running, like every write on this card: the mod
    /// rewrites the file from memory on its own timer, so a copy put back now simply disappears.
    /// </summary>
    /// <summary>
    /// Opens this game's translation history.
    ///
    /// ⚠ The window is its own class, built like Settings and Mod defaults — see BackupsWindow.
    /// It lived here as a hand-made dialog for a while and read as a different program: same
    /// product, another designer.
    /// </summary>
    private async Task ShowBackupsAsync(GameReport report, LoaderDescriptor descriptor)
    {
        var window = new BackupsWindow(report.Game, descriptor, _running.IsRunning(report.Game));
        await window.ShowDialog(this);

        // Only when something was written. The card behind shows the line count and the sync
        // verdict, and a restore moves both — but redrawing it for a window somebody merely
        // looked at is work nobody asked for.
        if (window.Touched) await RereadAsync(report.Game, redraw: true);

        await ShowSelectedAsync();
    }

    private async Task RunUninstallAsync(GameReport report, bool fromLoaderSection = false)
    {
        var engine = new UninstallEngine(_platform, _catalog);
        var available = engine.Available(report.Game);
        var foreign = engine.ForeignMods(report.Game);

        var loaderName = report.InstalledLoader?.Display ?? "the mod loader";

        var loaderBox = new CheckBox
        {
            Content = fromLoaderSection
                ? $"Remove {loaderName}"
                : $"Also remove {loaderName}",
            IsEnabled = available.RemoveLoader && !fromLoaderSection,
            IsChecked = fromLoaderSection && available.RemoveLoader,
        };

        var modBox = new CheckBox
        {
            Content = "Also remove the mod",
            IsEnabled = false,
            IsChecked = true,
            IsVisible = fromLoaderSection,
        };
        ToolTip.SetTip(modBox,
            $"Required: with {loaderName} gone, nothing would ever load the mod again.");

        // Off by default, and deliberately worded so nobody deletes months of work by reflex.
        var dataBox = new CheckBox
        {
            Content = "Also remove settings and translations (a copy is kept aside)",
            IsChecked = false,
        };

        var content = new StackPanel { Spacing = 10 };

        // ⚠ Says what goes, not what the tool is about to do to a "plugin". The previous wording —
        // "The plugin will be removed. Files you changed since installing are left alone." —
        // promised something the box below contradicts the moment it is ticked.
        content.Children.Add(new TextBlock
        {
            Text = fromLoaderSection
                ? $"{loaderName} and the mod will both be removed. Nothing else in this game is "
                  + "touched, and files you changed since installing are left alone."
                : "The mod will be removed. Nothing else in this game is touched, and files you "
                  + "changed since installing are left alone.",
            TextWrapping = TextWrapping.Wrap,
        });

        // Order follows the gesture: what is settled first, what is offered after. Reversing it
        // between the two entry points is what made the same screen read as two different ones.
        if (fromLoaderSection)
        {
            content.Children.Add(loaderBox);
            content.Children.Add(modBox);
        }
        else
        {
            content.Children.Add(loaderBox);
        }

        // ⑤ Ticking a box shows what it takes, the same way the data box opens its list. A count
        // and a couple of names is enough here: these are the loader's own files, all written by
        // us, and nobody is going to keep three of twenty-two.
        var loaderFiles = ReceiptStore.Read(report.Game.Path)?.Loader?.Files ?? new();
        if (loaderFiles.Count > 0)
        {
            var names = loaderFiles.Take(3).Select(f => f.Path.Replace('\\', '/'));
            var loaderSummary = new TextBlock
            {
                Text = $"{loaderFiles.Count} file(s): {string.Join(", ", names)}"
                     + (loaderFiles.Count > 3 ? $", and {loaderFiles.Count - 3} more" : ""),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                Margin = new Avalonia.Thickness(24, -4, 0, 0),
                IsVisible = loaderBox.IsChecked == true,
            };

            loaderBox.IsCheckedChanged += (_, _) => loaderSummary.IsVisible = loaderBox.IsChecked == true;
            content.Children.Add(loaderSummary);
        }

        // ⚠ Named, right under the box they refuse. A greyed control with the reason in a tooltip
        // is a reason nobody reads — and "other mods still use it" without saying WHICH reads as
        // an excuse. This is also the answer to "what did it find?", asked by the person who is
        // certain there is nothing else in that game.
        if (foreign.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"{loaderName} stays: {foreign.Count} other mod(s) need it — "
                     + string.Join(", ", foreign.Take(6))
                     + (foreign.Count > 6 ? $", and {foreign.Count - 6} more" : "")
                     + ". They are never touched.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusWarning"),
                Margin = new Avalonia.Thickness(24, -4, 0, 0),
            });
        }
        else if (!available.RemoveLoader)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"{loaderName} stays: it was already in this game before "
                     + "UnityGameTranslator Manager was used here, so it is not ours to remove.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
                Margin = new Avalonia.Thickness(24, -4, 0, 0),
            });
        }

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

        // ② The title NAMES what goes. "Uninstall from All Will Fall?" left the reader to supply
        // the object of the sentence, on the one screen where being wrong about it costs files.
        var title = fromLoaderSection
            ? $"Uninstall the mod loader from {report.Game.Name}"
            : $"Uninstall the mod from {report.Game.Name}";

        if (!await ConfirmAsync(title, content, "Uninstall")) return;

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

    /// <summary>
    /// Writes back the files this game had before we replaced them.
    ///
    /// ⚠ Named in the confirmation, not counted. "Put back 23 files?" answers nothing — what the
    /// reader needs is what comes back and what it means, which is almost always a mod loader
    /// reappearing where they may have just removed one.
    /// </summary>
    private async Task RunPutBackAsync(GameReport report)
    {
        // ⚠ The same list the button counted: what is MISSING, never what is stored.
        var aside = UninstallEngine.RestorableFiles(report.Game);
        if (aside.Count == 0)
        {
            await MessageAsync("Nothing to put back",
                "Every file this game had before is already in place.");
            return;
        }

        var shown = string.Join(Environment.NewLine, aside.Take(12).Select(f => "• " + f));
        if (aside.Count > 12) shown += Environment.NewLine + $"• …and {aside.Count - 12} more";

        // ⚠ "Nothing is deleted" first, in its own sentence. A confirmation naming two hundred
        // files invites exactly one question — what am I about to lose — and the answer is
        // nothing: this only fills the gaps its own uninstall left.
        var body = "Nothing is deleted. These files were here before UnityGameTranslator Manager "
                 + $"replaced them, and are missing from {report.Game.Name} now — writing them "
                 + "back restores the mod loader it came with, so it will be detected again."
                 + Environment.NewLine + Environment.NewLine + shown
                 + Environment.NewLine + Environment.NewLine
                 + "Anything already in place is left exactly as it is.";

        if (!await ConfirmAsync($"Put back what {report.Game.Name} had before?", body, "Put them back"))
            return;

        Busy(true, "Putting back...");
        var outcome = new UninstallEngine(_platform, _catalog).PutBackWhatWasHere(report.Game);
        Busy(false, "Ready.");

        var message = outcome.Message;
        if (outcome.PutBack.Count > 0)
            message += Environment.NewLine + Environment.NewLine +
                       string.Join(Environment.NewLine, outcome.PutBack.Select(f => "• " + f));

        await MessageAsync("Put back", message);
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

        // The card surface, not a bare outline: inside a themed dialog an unfilled rectangle
        // reads as a control that failed to render.
        var view = new Border
        {
            Background = Brush("SurfaceCard"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(14, 12),
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

    private void Status(string message)
    {
        StatusText.Text = message;

        // ⚠ Mirrored into the scanning panel while it is up, rather than each phase of the scan
        // setting the two separately. The status bar is at the far bottom of a wide window and the
        // gear is in the middle of it: somebody watching the mark has no reason to be looking
        // anywhere else, and a step reported only down there is a step they do not see.
        //
        // Wired here so a phase added later is carried without anybody remembering to. The field is
        // null the rest of the time, which is what keeps this from reaching any other screen.
        if (_scanGear is not null) _scanGear.Detail = message;
    }

    private Task<bool> ConfirmAsync(string title, string body, string confirmLabel) =>
        ConfirmAsync(title, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap }, confirmLabel);

    /// <summary>
    /// A modal confirmation. Written by hand rather than pulled from a dialog package: one
    /// window type is not worth a dependency that would also have to be kept current.
    /// </summary>
    private async Task<bool> ConfirmAsync(string title, Control body, string confirmLabel)
    {
        var result = false;

        // ⚠ Dressed like ConfirmationWindow, because it IS the same act asked in a richer form —
        // and it was not. This one built a bare Window: no surface from the theme, no title inside
        // the content, no primary button. So the plainest questions in this app arrived themed and
        // the most consequential one — "uninstall from this game", with a list of files about to
        // be deleted — arrived looking like a debug dialog.
        var confirm = new Button { Content = confirmLabel, Classes = { "primary" } };

        // IsCancel AND IsDefault on Cancel, exactly as ConfirmationWindow argues: Escape closes
        // it, Enter closes it, and the destructive answer is only ever reached by aiming at it.
        // This dialog had Enter on the confirm button, so the two paths to the same decision
        // disagreed on what a reflex keypress means.
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, confirm },
        };

        var layout = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(24) };

        layout.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextPrimary"),
        });

        layout.Children.Add(body);
        layout.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MinHeight = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("SurfaceBase"),
            Content = new ScrollViewer { Content = layout },
        };

        confirm.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task MessageAsync(string title, string body)
    {
        var ok = new Button
        {
            Content = "Close",
            IsDefault = true,
            IsCancel = true,
            Classes = { "primary" },
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var layout = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(24) };

        layout.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextPrimary"),
        });

        layout.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        layout.Children.Add(ok);

        var dialog = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("SurfaceBase"),
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
