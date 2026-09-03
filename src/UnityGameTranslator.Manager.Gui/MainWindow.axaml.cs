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
    /// <summary>
    /// Who is asked whether a newer mod exists.
    ///
    /// 🔴 **Eight seconds, not thirty.** The shared release client waits thirty, which is right for
    /// DOWNLOADING a build and absurd for a line on a card: opening the first game took twelve
    /// seconds on a slow answer, for a nicety. The download path keeps its own client and its own
    /// patience — this one only decides whether a sentence can be written.
    /// </summary>
    private readonly PluginReleases _releases =
        new(GitHubReleaseClient.ForMod(
            Core.Net.Http.Create(TimeSpan.FromSeconds(8))));

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
    /// can publish.
    ///
    /// 🔴 **The SERVER is held with the name, and dropping it was the defect.** This used to keep
    /// the name alone — "only the name is ever held", said as though it were obvious — so the card
    /// compared that name with this tool's own and wrote "(you)" whenever the two matched. Two
    /// sites can carry the same user name and mean two different people, which is precisely why
    /// <see cref="ServerIdentity"/> checks the server FIRST. The card, holding half the fact, could
    /// not: a game linked on one instance and a tool pointed at another agreed on screen, in green,
    /// with a tooltip promising it "can publish" — while every tab refused every act.
    ///
    /// ⚠ The pair is what makes that impossible to write again: there is no bare name here to
    /// compare, so a comparison by name does not compile. That is the guard, not this comment —
    /// the previous one said the right thing and was read past.
    /// </summary>
    private readonly Dictionary<string, (string? User, string? Server)> _accounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The rows currently in the list, by game path, with what each was saying when it was built.
    ///
    /// ⚠ These belong to the ListBox. They are here to be UPDATED in place, never to be handed
    /// back through a new ItemsSource: an item carries its visual parent, and reusing one across
    /// two sources leaves the virtualising panel unable to anchor it — the window then goes down
    /// with no message at all.
    /// </summary>
    private readonly Dictionary<string, (RowFacts Facts, ListBoxItem Item)> _rows =
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

        // 🔴 A window closed while a browser editor is open used to leave that session running.
        // The follower lives in a detached task, so the process exiting simply took it — the site
        // kept the session for its whole inactivity window, the page still open on it went on
        // accepting saves, and nothing on this machine was ever going to fetch them.
        Closing += OnClosingWithEditorOpen;

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
            _openBlocks.Clear();

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

        // ⚠ The give at the end of a scroll is NOT hooked up here any more: it is declared once, on
        // every ScrollViewer in the program, by a style in App.axaml. Two lines here were two
        // windows out of nine that had it.

        Loaded += async (_, _) =>
        {
            // 🔴 Before the scan and before anything is asked of anybody. The scan itself sends
            // nothing, but the window that follows it does, and a question asked afterwards would
            // be asked about something already done.
            await AskAboutGoingOnlineAsync();

            // After the answer, before the scan: the first thing drawn is already right, rather
            // than a bar that says one thing and corrects itself once the list arrives.
            RefreshOnlineIndicator();

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

        ShowUpdateNotice(result);
    }

    /// <summary>
    /// Puts the toolbar notice in step with an answer about tool updates — whichever answer, and
    /// from wherever it came.
    ///
    /// 🔴 **Written from the STATE, not from the event.** It used to be a switch inside the startup
    /// check, and "up to date" did nothing at all — which is fine the first time, when the slot is
    /// empty, and wrong every time after: a failure notice put there at launch survived a later
    /// check that succeeded, and stayed for the whole session. So this method clears as
    /// deliberately as it writes.
    ///
    /// The same shape of defect this project keeps meeting — see the Settings window's LastCheck,
    /// which is what lets the answer travel back here at all.
    /// </summary>
    /// <summary>
    /// The most recent answer about this tool's own updates, from wherever it came — the check at
    /// startup, or a "Check now" pressed in the Settings window.
    ///
    /// 🔴 Kept because TWO screens render from it and neither owned it: the toolbar notice, written
    /// once at launch, and the Updates card in Settings, which was handed whatever the caller
    /// happened to hold. Both went stale after a re-check, in opposite ways — one kept saying the
    /// check had failed, the other showed nothing at all.
    /// </summary>
    private SelfUpdateCheck? _lastToolCheck;

    private void ShowUpdateNotice(SelfUpdateCheck result)
    {
        // One place records it, and it is the one place every answer passes through.
        _lastToolCheck = result;

        switch (result.State)
        {
            case SelfUpdateState.Available when result.Offer is not null:
                ShowUpdateNotice($"Update available: {result.Offer.NewVersion}",
                    "Open Settings to see what changed and install it.",
                    primary: true, result);
                break;

            case SelfUpdateState.UpToDate:
                // Nothing to say, and saying nothing means REMOVING whatever was there. An empty
                // slot is the honest rendering of "this tool is current".
                UpdateSlot.Content = null;
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

    /// <summary>
    /// The reader of games, rebuilt from whatever catalogue is in force.
    ///
    /// ⚠ Its own method because the catalogue can change under it: the window starts on the copy
    /// this machine already had and asks the publisher afterwards, so a newer one lands while the
    /// list is on screen and everything derived from it has to be made again.
    /// </summary>
    private void BuildInventory()
    {
        _inventory = new GameInventory(_platform, _catalog, new CatalogApiClient(),
                                       _settings.Current.ApiToken)
        {
            Lineages = _lineages,

            // 🔴 What lets a ROW be built from the same report the card is built from. Without it
            // the list had to build its own — nine fields out of twenty-three — and every field
            // added afterwards had to be copied across by hand. See GameInventory.Online.
            Online = _online,

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

    }

    /// <summary>
    /// Asks the publisher for a newer catalogue once the window is up, and redraws if one arrives.
    ///
    /// ⚠ Silent when nothing changes, and silent on failure: not having today's catalogue costs a
    /// loader version nobody has asked about yet, and the copy in force is always a real one.
    /// </summary>
    private async Task RefreshCatalogAsync()
    {
        if (!_settings.Current.OnlineMode) return;

        var fresh = await Task.Run(() => new CatalogProvider(_platform).Get());

        // Only when it actually reached somebody. Cache and Embedded are what we already started
        // from, so applying them again would redraw the whole list to change nothing.
        if (fresh.Source is CatalogSource.Cache or CatalogSource.Embedded) return;

        _catalog = fresh.Document;
        BuildInventory();

        await RepublishAsync();
    }

    private async Task ScanAsync()
    {
        Busy(true, "Looking for your games...");
        ShowScanning();

        _sweep?.Cancel();

        // 🔴 **The catalogue is read off this machine, and asked for over the network afterwards.**
        // CatalogProvider tries GitHub, then the site mirror, and only then falls back to the cache
        // it already has — which is right for a CLI with nothing else to do, and wrong here: it put
        // two network timeouts in front of the first thing anybody sees. Measured on this machine,
        // "manager scan" takes 0.4s offline and 6.3s online, and the sweep of the drives is 0.2s of
        // that. The whole wait was one HTTP request nobody was waiting for.
        //
        // ⚠ There is always something to start from: the cache from last time, or the copy compiled
        // into this binary. A loader published this morning is the only thing missing, for a few
        // seconds, on a screen that is not yet showing loaders.
        var result = await Task.Run(() => new CatalogProvider(_platform).Get(offline: true));
        _catalog = result.Document;
        // Asked once and shared by every report built from here — see PluginReleases. Forgotten
        // first because reaching this method IS the gesture that means "look again": a rescan
        // that re-read the drives and kept yesterday's idea of the newest plugin would be a
        // refresh button that refreshes some things.
        _releases.Forget();

        // ⚠ The token goes with the search, and only so the answer carries this account's own vote.
        // Without it every arrow drew neutral whatever somebody had chosen, so a second click
        // withdrew the vote they meant to confirm.
        BuildInventory();

        // There is a folder list to show from here on — see the note where it is switched off.
        FoldersButton.IsEnabled = true;

        ToolTip.SetTip(FoldersButton, FoldersTip);

        Status($"Catalog: {_catalog.Loaders.Count} loaders ({result.Source}). Scanning your drives...");

        // ⚠ **Ten seconds of nothing is what makes ten seconds feel long.** The gear has a second
        // line for exactly this — see SpinningGear.Detail, which exists so the caption can stay
        // still while something under it moves. Posted, because the sweep reports from its own
        // thread.
        _inventory.Report = where => Dispatcher.UIThread.Post(() =>
        {
            if (_scanGear is not null) _scanGear.Detail = where;
        });

        var found = await Task.Run(() => _inventory.ScanAll());

        _inventory.Report = null;

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

        BuildFilterBar();
        RepublishRows();

        // ⚠ The overview rather than RepublishAsync, and deliberately: a rescan lands on the
        // summary. Restoring a card for whatever happened to be selected before would answer a
        // question nobody asked, on a list that has just been rebuilt from the drives.
        ShowOverview();
        Busy(false, "Ready.");

        WarmInBackground();
    }

    /// <summary>
    /// Every answer that comes from somewhere other than this machine, asked after the first paint.
    ///
    /// 🔴 **Block on the disk, never on the network.** These were once awaited between the scan and
    /// the first paint — ten seconds of patience holding back a list already read off the disk and
    /// with nothing to learn from them. What the window IS comes from the machine; what somebody
    /// else has to say about it sharpens the rows afterwards.
    ///
    /// 🔴 **Every warmer here ends with <see cref="RepublishAsync"/>, and that is the whole
    /// contract.** A row is drawn from what was known when it was drawn; an answer arriving later
    /// changes nothing on screen unless somebody republishes. Three separate defects have been this
    /// exact omission, each found by a person clicking things one at a time to make the list tell
    /// the truth:
    ///   · the loader tag that appeared only on a SELECTED game (see WarmLoaderBuildsAsync);
    ///   · the sync verdict missing from the rows;
    ///   · the mod update nobody was told about until they had clicked every game in the library.
    ///
    /// ⚠ **It used to be two calls to make in the right order, written here as a rule.** A rule in
    /// a comment is obeyed by whoever reads it, and thirteen places had to: some did one of the two,
    /// some left the card out. One call now does the whole thing, and doing it twice costs a redraw
    /// and breaks nothing.
    ///
    /// ⚠ **So this list is the place to add a source, and the only one.** A lookup started anywhere
    /// else is a lookup nobody republishes for — which does not fail, does not log, and shows a
    /// stale screen to somebody who has no reason to doubt it. Adding a warmer is one line here plus
    /// a method that ends like its neighbours.
    ///
    /// ⚠ Nothing lies in the meantime: each source distinguishes "not asked yet" from "nothing to
    /// report", which is why those properties exist at all.
    /// </summary>
    /// <summary>
    /// Puts the question once, on the first launch that has never had an answer.
    ///
    /// ⚠ Awaited, and it is the only thing on this path that is: everything else about the first
    /// paint exists to avoid making somebody wait. But the answer decides whether the calls that
    /// follow may happen at all, so there is nothing to overlap it with.
    ///
    /// ⚠ A window closed without choosing leaves the flag false and writes nothing — so the tool
    /// stays offline for this session and asks again next time. Dismissing a question is not
    /// answering it.
    /// </summary>
    private async Task AskAboutGoingOnlineAsync()
    {
        if (_settings.Current.OnlineAsked) return;

        var answer = await FirstRunWindow.AskAsync(this);
        if (answer is not { } online) return;

        var settings = _settings.Current;
        settings.OnlineAsked = true;
        settings.OnlineMode = online;
        _settings.Save(settings);
    }

    /// <summary>
    /// Says, permanently, whether this tool may ask anybody anything.
    ///
    /// 🔴 **A program that decides on its own whether to reach the network owes an answer to
    /// "which one am I in?" without being asked.** Nothing said it: the switch was three clicks
    /// away in a settings window, under a name that spoke of catalogues, and a library showing no
    /// community translations looked identical whether nobody had published any or this tool had
    /// simply never asked.
    ///
    /// ### The colours are the ones the notices use, and grey is not a downgrade
    ///
    /// ⚠ **Offline by choice is NOT red.** Red means nothing here can work; offline, everything
    /// except lookups still does — games are found, the mod installs, what is on this machine is
    /// managed. Painting a legitimate choice as a fault is how a colour scheme stops being read.
    /// Amber is kept for the one case that is genuinely unresolved: the question was closed rather
    /// than answered, so the tool is offline by default and not by decision.
    /// </summary>
    private void RefreshOnlineIndicator()
    {
        var settings = _settings.Current;

        var (label, colour, why) = !settings.OnlineAsked
            ? ("Offline", "StatusWarning",
               "Nobody has answered yet whether this tool may use the internet, so it does not. "
               + "The question comes back at the next launch, or answer it now with \"Work online\" "
               + "in the tool's settings.")
            : settings.OnlineMode
                ? ("Online", "StatusSuccess",
                   "This tool asks the site whether a translation exists for the games found here, "
                   + "sending their names or Steam ids, and checks which loaders and versions have "
                   + "been published. Turn it off with \"Work online\" in the tool's settings.")
                : ("Offline", "StatusNeutral",
                   "This tool asks nobody anything. It still finds your games, installs the mod and "
                   + "manages what is already on this machine. Turn it on with \"Work online\" in "
                   + "the tool's settings.");

        OnlineLabel.Text = label;
        OnlineDot.Fill = Brush(colour);
        ToolTip.SetTip(OnlineIndicator, why);
    }

    private void WarmInBackground()
    {
        // 🔴 The gate, and it belongs here rather than in each warmer. Every source below already
        // refuses when OnlineMode is off; none of them knows the difference between "answered no"
        // and "never asked", and the second must behave like the first until somebody has said
        // otherwise — including when the question was closed rather than answered.
        if (!_settings.Current.OnlineAsked) return;

        _ = FillLineagesAsync();

        // ⚠ Before the loader builds, which measure themselves against the catalogue it refreshes.
        _ = RefreshCatalogAsync();

        _ = WarmLoaderBuildsAsync();

        _ = WarmPluginReleaseAsync();

        StartOnlineSweep();
    }

    /// <summary>
    /// Asks the site which lineages belong to this account, and sharpens the rows when it answers.
    ///
    /// ⚠ One call for the whole library, so it is worth making early — just not worth waiting for.
    /// The filter "My translations" and the count of contributions waiting both read it, and both
    /// already know how to say "not asked yet" rather than "none".
    /// </summary>
    /// <summary>
    /// The token to ask the site with — none when this tool is not allowed to ask anything.
    ///
    /// 🔴 **The lineage lookup was the one network call that ignored the switch.** Every other
    /// warmer opens with `if (!OnlineMode) return`; this one went straight to `/me/translations`,
    /// so somebody signed in who had turned the community catalog OFF still had their account
    /// queried at every launch. The settings screen says in as many words that off means this tool
    /// never asks the site anything, and it was not true.
    ///
    /// ⚠ Written as one named property rather than a guard copied into the three call sites — the
    /// warmer, the sign-in/out refresh and the game card. Three copies is three places to forget,
    /// and forgetting is exactly what happened here.
    ///
    /// ⚠ Passing null rather than skipping the call: EnsureAsync(null) clears the index, which is
    /// the honest state — "we do not know whose these are" — where leaving a previous answer in
    /// place would keep claiming roles read before the switch was turned off.
    /// </summary>
    private string? ApiTokenForLookups =>
        _settings.Current.OnlineMode ? _settings.Current.ApiToken : null;

    private async Task FillLineagesAsync()
    {
        await _lineages.EnsureAsync(ApiTokenForLookups);

        // 🔴 The site refused the token: it was revoked from the account, or it expired. Keeping it
        // would leave this tool claiming to be signed in with a credential that no longer opens
        // anything — and the person who cut it from their account would see nothing change here.
        //
        // ⚠ Forgotten locally and not handed back: there is nothing left to revoke, and asking
        // would only tell a site that has already decided.
        if (_lineages.TokenRefused)
        {
            var settings = _settings.Current;
            settings.ApiToken = null;
            settings.ApiUser = null;
            settings.ApiTokenServer = null;
            _settings.Save(settings);
        }

        // Redrawn because the answer changes what a row says — the same reason the loader builds
        // redraw, and the same shape.
        BuildFilterBar();
        await RepublishAsync();
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
                    RepublishRows();

                    Status($"Checking community translations... {progress}/{ids.Count}");
                });
            }, token);

            if (!token.IsCancellationRequested)
                await Dispatcher.UIThread.InvokeAsync(() => Status("Ready."));
        }, token);
    }

    /// <summary>
    /// Makes the whole window say what is currently known. **The one way to do that.**
    ///
    /// 🔴 **It replaces a sequence copied by hand into thirteen places.** The rule was written down
    /// — "every warmer ends with RecomputeSituations() and RefreshList()" — and a written rule is
    /// obeyed by whoever reads it. Some sites did one of the two, some forgot the card, and the
    /// comment that stated the rule also listed three defects that were this exact omission: a
    /// loader tag, a sync verdict and a mod update, each of them a fact this window held and did not
    /// show until somebody clicked every game in their library.
    ///
    /// ⚠ **Reconciles from the state; it is not a transition.** Whatever changed and wherever it
    /// came from, this makes the screen match what is known now — so a source that answers late has
    /// exactly one thing to call, and calling it twice costs a redraw and breaks nothing.
    ///
    /// ⚠ The filter bar is deliberately NOT here: it answers which LENSES exist, which changes when
    /// somebody signs in or a game starts, not when an answer arrives about a game. Its three
    /// callers say so themselves.
    /// </summary>
    private async Task RepublishAsync()
    {
        RepublishRows();

        // Whatever is on the right, which is the overview when no game is selected. The card was
        // the half most often forgotten by the hand-written version, and the overview was never
        // refreshed by any of them.
        await ShowWhateverIsOnTheRightAsync();
    }

    /// <summary>
    /// The same reconciliation, stopping short of the card.
    ///
    /// ⚠ **One caller, and it has a reason rather than an excuse**: the community sweep answers
    /// once per game, so up to forty times in a few seconds. Rebuilding the card that often would
    /// throw away a loader picked in a dropdown while somebody was looking at it — the same reason
    /// the running-games clock only redraws the card when the game it is about has started or
    /// stopped.
    /// </summary>
    private void RepublishRows()
    {
        RecomputeSituations();

        // Contents rather than a rebuild wherever membership cannot move — which is everywhere
        // except under a filter, where learning something about a game can put it in or out of the
        // visible set. Rebuilding drops the selection and restores it, and doing that on every
        // answer is what used to make the list flash.
        //
        // 🔴 **And only when there are rows to update at all.** RefreshRowContents walks the rows
        // the list already holds: it can change what one SAYS, never bring one into being. With
        // none it walks nothing, builds nothing, and says nothing about it.
        //
        // ⚠ That is not a hypothetical — it shipped. A scan clears the rows and then asks for this,
        // so the first paint showed an EMPTY list under a subtitle reading "58 Unity games found",
        // and clicking any lens filled it, because a lens is the one case that rebuilds. The old
        // code was safe by accident: this choice lived in the community sweep, which only ever runs
        // once a list exists.
        if (_lens == Lens.All && _rows.Count > 0) RefreshRowContents();
        else RefreshList();
    }

    /// <summary>
    /// Rebuilds every row's situation from what is currently known. Cheap: it reads the caches,
    /// it does not go looking again.
    ///
    /// ⚠ Call <see cref="RepublishAsync"/> instead unless you are one of the two methods above:
    /// this only refreshes what is REMEMBERED, and nothing on screen changes until somebody redraws.
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
            if (account.User is not null) _accounts[game.Path] = account;
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
    private (GameSituationInfo Situation, bool Mine, (string? User, string? Server) Account)
        ReadSituation(GameInstall game)
    {
        // 🔴 **The same report the card is built from — there is no longer a second builder.**
        // This method used to assemble its own GameReport, filling nine of its twenty-three fields,
        // and every field added to the real one afterwards had to be copied here by hand. Three
        // defects were that copy being forgotten; a fourth was MyPosition, never copied at all, so
        // "contribution frozen" and its two neighbours had no path to a row while the card showed
        // them. Nothing is copied now, so nothing can be missed.
        //
        // ⚠ Asks nobody: BuildReport reads the disk and the community cache and makes no request.
        // That is what allows this to run for every game on every answer the sweep brings back.
        var report = _inventory.BuildReport(game);

        // "We asked and nobody has published anything" is only true if we asked. A tool that may
        // ask nobody has legitimately finished asking, which is the second half of this.
        var checkedOnline = report.OnlineChecked || !_settings.Current.OnlineMode;

        // ⚠ Only when the account's lineages have actually been read. Unknown and none look
        // identical from here, and announcing "nobody is waiting" on that basis would be a guess
        // dressed as a fact — the reason AccountLineages exposes Known at all.
        var waiting = _lineages.Known
            ? report.MyPosition?.BranchesWithWork ?? report.MyPosition?.BranchesCount
            : null;

        var situation = SituationReader.Read(report, _settings.ResolveTargetLanguage(),
                                             checkedOnline, waiting, _settings.Current.ApiUser);

        // Both RETURNED rather than recorded, for the reason given above this method.
        return (situation, report.MyPosition is not null, report.SiteAccount);
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
        await RepublishAsync();
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
    /// <param name="openWith">
    /// The language to open the list on, when a button named one. Null lets the window fall back
    /// to the language this game runs — see TranslationsWindow._openWith.
    /// </param>
    private async Task OpenTranslationsAsync(GameReport report, bool anyLanguage = false,
                                             string? openWith = null)
    {
        var loaderId = report.InstalledLoader?.Id ?? report.RecommendedLoader?.Id;
        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == loaderId);

        if (descriptor is null)
        {
            Status("No loader is set up for this game yet, so there is nowhere to put a translation.");
            return;
        }

        var window = new TranslationsWindow(report, descriptor, _settings, _lineages,
                                            ChosenTranslation(report.Game.Path), anyLanguage, openWith);
        await window.ShowDialog(this);

        // Only when a choice was actually made: re-reading the game on every close would rescan for
        // nothing each time somebody just looked.
        if (!window.Changed) return;

        // ⚠ Held, not saved. The window used to write it to disk itself, which is how a choice
        // outlived the session that made it — see _pendingTranslation.
        if (window.ChosenTranslation is { } picked) _pendingTranslation[report.Game.Path] = picked;

        await RepublishAsync();
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
        // 🔴 **The LATEST answer, not the one this door was opened with.**
        //
        // `found` is whatever the caller had: the startup result for the toolbar notice, and
        // nothing at all for the Settings button beside it. Both go stale the moment somebody
        // presses "Check now" inside — so closing that window and opening it again showed either
        // an empty Updates card or the answer from launch, and the person had to ask a third time
        // to be told what they had just been told twice.
        //
        // Same defect as the notice below, one door further in, and the same cure: keep the state,
        // render from it.
        var window = new ToolSettingsWindow(_platform, _settings, found ?? _lastToolCheck, _catalog);
        await window.ShowDialog(this);

        // 🔴 **The update notice is RECONCILED from the last answer, not left where startup put it.**
        //
        // It is written once by LookForToolUpdateAsync, from a check that may have failed — and a
        // failure there is common by design: a firewall, an antivirus or a company proxy produces
        // exactly it. Someone who then opens this window, presses "Check now" and is told the tool
        // is up to date used to go back to a main window still saying "Couldn't check for updates",
        // for the rest of the session, with nothing left on screen that could correct it.
        //
        // ⚠ The answer is carried out of that window rather than fetched again: asking GitHub a
        // second time would spend a request to learn what we already know — and would fail on its
        // own for the very people this repairs.
        if (window.LastCheck is { } rechecked) ShowUpdateNotice(rechecked);


        // Redrawn whatever was saved: signing in and out both happen in that window, and the
        // header would otherwise keep claiming the opposite until the next launch.
        ShowAccount();

        // Same reason, for the switch that window carries: leaving the status bar on its previous
        // answer would have it contradict the box somebody has just ticked.
        RefreshOnlineIndicator();

        // The roles belong to whoever was signed in. Keeping them after a sign-out would leave a
        // card claiming "you are the Main here" to nobody in particular, and after a switch of
        // account it would claim it for the wrong person.
        _lineages.Forget();
        await _lineages.EnsureAsync(ApiTokenForLookups);

        // "My translations" appears on signing in and goes away on signing out, so the bar has to
        // be rebuilt — and the list with it, since what belongs to whom has just changed.
        if (_lens == Lens.Mine && !_settings.Current.SignedIn) _lens = Lens.All;

        BuildFilterBar();

        // ⚠ The strip above the summary answers questions about this program — where it lives,
        // which channel it follows — and every one of them can have changed in that window. Redrawn
        // even when nothing was saved: installing or removing the tool happens immediately, with no
        // Apply of its own. RepublishAsync covers it, since it refreshes whatever is on the right.
        await RepublishAsync();

        if (!window.Saved) return;

        // Signing in or changing the proxy both change what the community lookup can answer, so
        // what is on screen has to be asked again rather than left as it was.
        await RetryOnlineAsync();
    }

    /// <summary>
    /// Puts the header picker back in step with what was just saved.
    ///
    /// 🔴 **It did nothing at all, and nothing said so.** It walked the entries as ComboBoxItems
    /// while the list has always held LanguageChoice, so the match never fired: changing the
    /// language in the settings window left the header still showing the previous one, until
    /// something else happened to rebuild it.
    ///
    /// ⚠ Reselect, through the shared helper: putting a picker back where the settings just put it
    /// is not somebody choosing, and raising a choice here would save the answer a second time —
    /// and, through the handler below, redraw the whole window for it.
    /// </summary>
    private void SyncLanguageBox() =>
        ModSettingControls.Select(LanguageBox, _settings.Current.TargetLanguage);

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

        // ⚠ Filled through LanguageMark like every other language list in this product — the same
        // order, the same template, the same "follow the system" entry first. It used to build its
        // own, three lines from the shared one, which is how a list ends up differing from itself.
        LanguageMark.Fill(LanguageBox, Languages.All(),
                          new LanguageChoice("auto", autoName, autoLabel));

        ModSettingControls.Select(LanguageBox, _settings.Current.TargetLanguage);

        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (LanguageBox.SelectedItem is not LanguageChoice { Code: var code }) return;
            if (code == _settings.Current.TargetLanguage) return;

            var updated = _settings.Current;
            updated.TargetLanguage = code;
            updated.Reviewed = true;
            _settings.Save(updated);

            // 🔴 **The list AND whatever is open on the right.** The language is the context for
            // every row and for every line of the card — which translation is "in your language",
            // which target would be written, what the differences with Mod defaults are. Refreshing
            // the list alone left the card showing an answer computed against the language before
            // last: a game whose card read "Breton -> English" while the picker said French.
            //
            // Same call as OpenSettingsAsync, and for the same reason: this changes the same
            // setting that window changes, so it cannot redraw less than that window does.
            _ = RepublishAsync();
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

                // ⚠ **Here and not inside RefreshList.** The same method rebuilds the list on every
                // keystroke in the search box and on every answer the site brings back; playing
                // this there would strobe while somebody types and twitch on its own for the first
                // few seconds. A lens is a change SOMEBODY ASKED FOR, which is the whole condition
                // for moving at all — see Motion.
                Motion.Arrive(GameList);
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
                var facts = FactsFor(game);
                var item = BuildListItem(game, facts);
                _rows[game.Path] = (facts, item);
                return item;
            })
            .ToList();

        // 🔴 **The guard goes up BEFORE the assignment, not after it.** Replacing ItemsSource drops
        // the selection, which raises SelectionChanged on its own — unguarded, and one line too
        // early to be caught. The handler then did what a real click does: back to the This game
        // tab, folds shut. So refreshing the list threw somebody out of the Set up tab they were
        // reading, and they had to click back in and scroll down again to see the very change they
        // had just asked for.
        //
        // ⚠ It looked guarded, and the comment below says why it has to be — the guard was simply
        // covering the restore and not the loss that precedes it. Both are bookkeeping; neither is
        // a choice the player made.
        _restoringSelection = true;

        GameList.ItemsSource = items;

        // Restoring the selection is bookkeeping, not a choice the player made. Left unguarded it
        // raised SelectionChanged, which rebuilt the whole card on the right — fifty-three times
        // during the opening sweep, which is what made it flash.
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
            // ⚠ The shared comparison, not a hand-written one. This loop used to ask only whether
            // this game had started or stopped — the right question, asked in a second place — and
            // it wrote the new content back WITHOUT the facts that went with it, leaving each row
            // and the record of what it was saying out of step. RefreshRowContents answers the same
            // question against everything a row draws from, and keeps the two together.
            RefreshRowContents();
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
        // ⚠ Held before the re-read, because the guard below has to compare them and the lines that
        // follow overwrite what they are read from.
        var before = FactsFor(game);
        var mineBefore = _mine.Contains(game.Path);

        var (now, mine, account) = await Task.Run(() => ReadSituation(game));

        _situations[game.Path] = now;
        if (mine) _mine.Add(game.Path); else _mine.Remove(game.Path);

        // Signing in happens INSIDE the game, so this is one of the few things that can change
        // while somebody plays — which is exactly when this re-read runs.
        if (account.User is not null) _accounts[game.Path] = account;
        else _accounts.Remove(game.Path);
        _watchedStamps[game.Path] = TranslationFileStamp(game);

        var facts = FactsFor(game);

        // Nothing said differently means nothing to redraw. A game can save its file without any of
        // it reaching this window — a setting changed in the mod, say.
        //
        // ⚠ Unless the caller knows something the game does not say. See the parameter.
        //
        // 🔴 **The shared comparison, so this cannot fall behind what a row draws from again.** This
        // guard used to list the fields it cared about by hand — headline, detail, pending, account,
        // lineage — which is a list that has to be kept in step with a drawing method four hundred
        // lines away. It had already been wrong once: the account was read three lines up, written
        // into the dictionary the row draws from, and left out of the comparison, so signing out
        // inside a game changed everything this window knew and redrew none of it.
        //
        // ⚠ The lineage stays a separate term, and it is not an oversight: it decides MEMBERSHIP of
        // the "Mine" filter, not what the row says — so it belongs beside this question rather than
        // inside RowFacts.
        if (!redraw && facts == before && mineBefore == mine) return;

        if (_rows.TryGetValue(game.Path, out var row) && row.Item.Tag is GameInstall shown)
        {
            row.Item.Content = BuildRowContent(shown, facts);
            _rows[game.Path] = (facts, row.Item);
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

            var folder = UserDataInventory.DataFolder(game.Path, descriptor);
            if (folder is null) return default;

            var path = System.IO.Path.Combine(folder, LocalTranslationProbe.TranslationFileName);

            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Everything a row is drawn from — and therefore everything a change to it must be noticed in.
    ///
    /// 🔴 **This exists because the two halves used to be written separately, and drifted.** A row
    /// was drawn from five sources and compared on ONE of them: the signature was the situation's
    /// text, so signing in, signing out, a game opening, or a game's own account changing left the
    /// row exactly as it was — no error, no log, a screen quietly telling somebody something that
    /// had stopped being true. That is the "I signed in and the list did not notice" defect, and it
    /// was also why the corner kept naming a previous account.
    ///
    /// ⚠ **A record, so the comparison is the compiler's and not a string somebody maintains.**
    /// Every member here has structural equality already — GameSituationInfo is a record, the
    /// account is a tuple, the rest are values — so `==` answers "would this row look different"
    /// exactly. Adding a source to a row means adding it HERE, and then nothing can forget it: the
    /// drawing method takes this and only this.
    /// </summary>
    private sealed record RowFacts(
        GameSituationInfo? Situation,
        bool Running,
        (string? User, string? Server) Account,
        ServerStandingKind Standing);

    /// <summary>
    /// What this game's row would say right now.
    ///
    /// ⚠ The standing is asked of <see cref="ServerIdentity"/> here rather than recomputed while
    /// drawing: it is the authority every tab of the card already obeys, and the row comparing
    /// names by itself is what let the list say "(you)" in green about somebody else's account.
    /// </summary>
    private RowFacts FactsFor(GameInstall game)
    {
        _accounts.TryGetValue(game.Path, out var account);

        return new RowFacts(
            _situations.TryGetValue(game.Path, out var situation) ? situation : null,
            _running.IsRunning(game),
            account,
            ServerIdentity.For(_settings.Current, account, BuildInfo.ApiBaseUrl).Kind);
    }

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
            if (entry.Item.Tag is not GameInstall game) continue;

            // Structural equality on RowFacts: "nothing this row draws from has moved". It used to
            // be a string holding the situation alone, which is why four of the five sources could
            // change without the row noticing.
            var facts = FactsFor(game);
            if (facts == entry.Facts) continue;

            entry.Item.Content = BuildRowContent(game, facts);
            _rows[path] = (facts, entry.Item);
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
    private ListBoxItem BuildListItem(GameInstall game, RowFacts facts) =>
        new() { Tag = game, Content = BuildRowContent(game, facts) };

    /// <summary>
    /// What a row shows, separate from the row itself.
    ///
    /// Split apart so the sweep can replace what a row SAYS without replacing the row: a
    /// ListBoxItem belongs to the list that holds it, and handing the same instance back through a
    /// new ItemsSource leaves it with a stale visual parent — the virtualising panel then fails to
    /// anchor it and the window goes down without a word. Measured, not deduced: that was this
    /// morning's crash.
    ///
    /// 🔴 **Everything that can change is in <paramref name="facts"/>, and nothing here reads the
    /// window's own state.** That is the guard, and it is the one the previous version lacked: a row
    /// that helps itself to a field the comparison does not cover is a row that stops updating,
    /// silently. What is read from <paramref name="game"/> — its name, its icon, how it launches —
    /// cannot change without a rescan, which rebuilds every row anyway.
    /// </summary>
    private Control BuildRowContent(GameInstall game, RowFacts facts)
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
        if (facts.Running)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Running now",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("StatusWarning"),
            });
        }

        if (facts.Situation is { } situation)
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

            return WithAccountMark(game, row, facts);
        }

        return WithAccountMark(game, body, facts);
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
    private Control WithAccountMark(GameInstall game, Control content, RowFacts facts)
    {
        var account = facts.Account;

        var play = PlayButton(game, small: true, running: facts.Running);
        if (account.User is null && play is null) return content;

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

        if (account.User is not null)
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
            // 🔴 **Asked of the authority, never recomputed here.** This line read
            // `People.IsYou(account, _settings.Current.ApiUser)` — a comparison of NAMES — while
            // every tab of the same card asks ServerIdentity, which compares the SERVER first. A
            // game linked on one instance under "@name", with this tool pointed at another where
            // the account is also called "@name", therefore read "(you)" in green here and was
            // refused every act three clicks away. One fact, two calculations, opposite answers,
            // in one screen.
            //
            // ⚠ The refusal was right and nothing could be written — the guard is on the act. What
            // broke was the only thing an indicator is for: an indicator that contradicts itself is
            // not believed when it finally tells the truth.
            // ⚠ Taken from the facts this row was compared on, not asked again here. Asking twice
            // is how the two halves drift: the answer that decides whether the row is REDRAWN and
            // the answer it draws WITH must be the same one.
            var yours = facts.Standing is ServerStandingKind.Mine;

            // ⚠ Named, never a possessive: a name from another site is factually another person,
            // whatever it spells. See the naming rule — replace it with a proper noun, and if you
            // cannot say which one to write, do not write one.
            var elsewhere = facts.Standing is ServerStandingKind.OtherServer;

            // 🔴 **A bare name does not say what role it plays.** This corner showed "@somebody"
            // and the card's own line shows "by @somebody-else" — the account the GAME is signed
            // in as, and whoever PUBLISHED the translation it runs. Two names, two meanings, one
            // card, and nothing but a tooltip between them: the project's own author read one as
            // the other. Its own tiny line rather than "signed in as @x" on one row, because a
            // long account name would then trim the NAME instead of the label.
            // Their own stack, spacing 0: a label and its value are one thing, and the corner's
            // 4px gap between siblings would read as two.
            var named = new StackPanel
            {
                Spacing = 0,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            };

            named.Children.Add(new TextBlock
            {
                Text = "signed in as",
                FontSize = 9,
                Foreground = Brush("TextMuted"),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            });

            var mark = new TextBlock
            {
                // ⚠ The word "(you)", not the colour, carries the answer — see People.Mention.
                // On another site the name is followed by where it lives, because the name alone
                // would be read as the person at the keyboard.
                Text = elsewhere
                    ? People.Mention(account.User) + " — another site"
                    : People.Mention(account.User, yours),
                FontSize = 10,
                Foreground = Brush(yours ? "StatusSuccess" : "StatusWarning"),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                MaxWidth = 130,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            // ⚠ Three states, three sentences, and the middle one is the one that used to be
            // missing: a name that matches on another site is NOT the person at the keyboard, and
            // saying "not as the account this tool is using" about an identical spelling would
            // read as a bug rather than as a fact.
            ToolTip.SetTip(mark, elsewhere
                ? $"This game is signed in to a different site, as {People.Mention(account.User)}. "
                  + "That is another site's account even when the name is spelled the same, so it "
                  + "is not the one this tool is using. Nothing here will write to it."
                : yours
                ? $"This game is signed in to the site as {People.Mention(account.User, true)} — the "
                  + "account this tool is using. It can publish and contribute from inside the game."
                : $"This game is signed in to the site as {People.Mention(account.User)}, not as the "
                  + "account this tool is using. Nothing here will write to it: play it and look "
                  + "at it, and sign in inside the game to change that.");

            named.Children.Add(mark);
            corner.Children.Add(named);
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
    /// <param name="running">
    /// Whether this game is open. Handed in rather than read here, because a ROW must draw from the
    /// same facts it was compared on — a button deciding for itself is a button that can disagree
    /// with the row it sits in. The card passes what it just read; there is nothing to compare
    /// there, it is rebuilt whole.
    /// </param>
    private Button? PlayButton(GameInstall game, bool small, bool running, GameReport? report = null)
    {
        if (running) return null;
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

        // ⚠ The frame goes with it, and its row collapses. Left behind, the overview and the
        // scanning gear would draw under a previous game's name and tabs.
        CardHead.Children.Clear();
        CardHead.IsVisible = false;

        // ⚠ And the report it was drawn from: a tab click reads it, and one held past the card
        // would redraw a page about a game nobody is looking at.
        _shownReport = null;

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
    ///
    /// 🔴 **It states a SETTING, never the reader's activity.** The second banner described what
    /// somebody was doing — "you are playing with…" — to a person who had just launched a tool and
    /// was not playing. A banner on the first screen has no idea what anybody is doing; what it can
    /// say is what Mod defaults holds and what follows from it.
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
                async () => await OpenSettingsAsync(),
                // Amber: nothing is broken and every game can still be set up — but each one is
                // being set up from a guess, and the one-click path stays off until this is
                // answered. A decision is waiting, which is exactly what this colour means here.
                tone: Tone.Warning);
        }

        if (settings.TranslationBackend != "none") return null;

        // 🔴 **A SETTING and what follows from it — not a description of the reader.** This opened
        // with "You are playing with what the community has published", said to somebody who has
        // just launched a tool and is not playing anything. Nothing named where the fact came from
        // (Mod defaults), "which is the whole point of it" pointed at nothing, and the consequence
        // — a game with no community translation stays in its own language — was never stated. So
        // the one thing worth knowing was learnt later, on a game, by pressing a button.
        //
        // ⚠ Still an invitation, not a warning: playing with what people publish is a complete way
        // to use this. What changed is that it now says what it means, names its source, and leads
        // where the answer is given.
        // 🔴 **The setting, in the words it is written in on its own screen.** A title that
        // paraphrases makes the reader work out which control it is talking about. Quoting it
        // — the screen's name, then the value it holds — costs two pairs of quotation marks and
        // removes the guess, which matters most for the people who read this in their fourth
        // language.
        return Banner(
            "\"Mod defaults\" is set to \"Community translations only\"",
            // ⚠ **Named in the order they cost.** An AI on your own machine is free if the machine
            // can run one, and it is what this product is for; writing the lines yourself costs
            // nothing at all. Google, DeepL and the online models come last and are said to be paid
            // for by the reader — supported, and not the point. Putting "Captures only" first, as
            // this did, offered the longest road before the short one.
            "So a game gets a translation only if somebody published one in your language. Other "
            + "games stay in their own language. Open Mod defaults to change that: an AI on your "
            + "own machine costs nothing if the machine can run one, and \"Captures only\" costs "
            + "nothing at all — the mod collects the game's text and you write the lines yourself "
            + "in its editor. Google, DeepL and online AI work too, on your own key.",
            "Open Mod defaults",
            async () => await OpenSettingsAsync(),
            ("See the games on the site",
             () => { OpenUrl(BuildInfo.WebsiteBaseUrl); return Task.CompletedTask; }),
            // Blue, and deliberately not amber: this is a setting doing what it was set to do.
            // What it changes is which games come out translated, so it belongs beside the list it
            // explains — but nothing here is wrong, and painting a valid choice as a problem is how
            // a colour scheme stops being read.
            tone: Tone.Info);
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

        // 🔴 **Silent when there is nothing published AT ALL and nothing started here.** This
        // banner exists for a gap in ONE language — "translations exist, none in yours" — and the
        // card below already says "no translation has been published for this game yet" when the
        // gap is total. Both fired together on an untouched game, so the same fact arrived twice
        // in two registers, one of them narrower than the truth. The invitation to publish, which
        // is the other half of this banner, is not affected: it needs lines here to be worth
        // reading, and that is exactly what `started` tests.
        if (report.OnlineTranslations.Count == 0 && report.LocalTranslation is null) return null;

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
    /// <param name="second">
    /// A second way out, when the answer genuinely has two — offered beside the first, quieter.
    ///
    /// ⚠ Two doors and no more. A banner that grows a row of buttons has stopped being a notice;
    /// anything further belongs on the screen it leads to.
    /// </param>
    private Control Banner(string title, string body, string action, Func<Task> onClick,
                           (string Label, Func<Task> Click)? second = null,
                           Tone tone = Tone.Neutral)
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

        // ⚠ The second is outlined, never filled: one filled button per notice, so the eye is told
        // which way is the answer and which is the detour. Same rule as the game cards.
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        if (second is { } other)
        {
            var alternate = new Button
            {
                Content = other.Label,
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            alternate.Click += async (_, _) => await other.Click();
            buttons.Children.Add(alternate);
        }

        button.Margin = default;
        buttons.Children.Add(button);
        buttons.Margin = new Avalonia.Thickness(14, 0, 0, 0);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(text);
        row.Children.Add(buttons);

        return OverviewBox(row, tone);
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
    ///
    /// 🔴 **The tone is what makes this a notice rather than a card.** Every block in this strip was
    /// painted `SurfaceCard` over `BorderSubtle` — the dress of an ordinary card — so a question
    /// waiting for an answer looked exactly like a section of the page it interrupts. The default
    /// stays neutral, because some of these really are plain rows of information.
    ///
    /// ⚠ The colour goes all the way round here, where a callout carries a rule down its left side.
    /// That is not drift: a banner is a band across the top of the page and a callout is a note in
    /// the margin between cards, and an outlined rectangle sitting inside another outlined
    /// rectangle reads as a dialog — which is the reason the callouts have no outline to begin with.
    /// </summary>
    private Control OverviewBox(Control child, Tone tone = Tone.Neutral) => new Border
    {
        Background = Brush(Tones.BannerBackground(tone)),
        BorderBrush = Brush(Tones.Edge(tone)),
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

        // Blue: an offer, and one that costs nothing to decline — the body says so itself. Amber
        // would claim something is wrong with running the downloaded file, which is a perfectly
        // good way to use this program.
        return OverviewBox(row, Tone.Info);
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
            // ⚠ Short, and about the thing rather than about the reader. "You are running a loose
            // copy, not the installed one" asks somebody to hold two ideas and a negation before
            // the first comma; "loose" is also a word few non-native readers meet. The sentence
            // underneath names both copies and both folders, which is where the detail belongs.
            Text = canUpdate
                ? $"This manager is newer than the installed one ({running} against {installed.Version})"
                : "This manager is not the installed one",
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

        // Amber: everything works, and that is the trap. Settings changed here, and updates made
        // here, land on the copy about to be closed rather than on the one in the menu — the plainest
        // case of "it works, but not the way it looks".
        //
        // ⚠ The same fact about the MOD — installed twice, and the loader picks one — is amber in
        // DuplicatePluginNotice. One fact, one colour, whichever product is saying it.
        return OverviewBox(row, Tone.Warning);
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

        // Amber and not red: the program runs, the games are untouched, and one button puts the
        // missing pieces back. Red is for what nothing on the screen can fix.
        return OverviewBox(row, Tone.Warning);
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

        // 🔴 **Where somebody scrolled to is a place they put themselves in.** This method is the
        // one redraw of the card and everything calls it, so every act — applying a setting,
        // changing the language, a version resolving in the background — threw the reader back to
        // the top. Reading the differences half way down the Set up tab and changing the language
        // meant scrolling down again to see the very line that had just changed.
        //
        // ⚠ Only on a redraw of the SAME card. A different game is a different page, and landing
        // on it half way down would be the tool remembering something nobody asked it to.
        var offset = _selected?.Path == game.Path ? DetailScroll.Offset : default;

        _selected = game;

        ClearDetail();
        DetailPanel.Children.Add(new TextBlock { Text = game.Name, FontSize = 20, FontWeight = FontWeight.SemiBold });
        DetailPanel.Children.Add(new TextBlock { Text = "Reading...", Opacity = 0.6 });

        Busy(true, $"Reading {game.Name}...");

        // 🔴 **Both at once.** One call for the whole library, and only the first selection pays
        // for it — but it used to be awaited BEFORE the report, and the report makes a call of its
        // own. Two waits end to end, on the one card somebody opens first, for two answers that
        // have nothing to say to each other.
        //
        // A failure is recorded rather than raised: not knowing one's role costs a line on a card,
        // and must never stand between someone and installing the mod.
        var lineages = _lineages.EnsureAsync(ApiTokenForLookups);
        var building = _inventory.BuildReportAsync(game);

        await Task.WhenAll(lineages, building);

        var report = await building;
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

        // ⚠ Posted, and at Loaded: the content has only just been added, so it has no height yet
        // and an offset set now would be clamped straight back to zero.
        if (offset != default)
        {
            Dispatcher.UIThread.Post(() => DetailScroll.Offset = offset,
                                     DispatcherPriority.Loaded);
        }
    }

    /// <summary>Puts what a freshly built report says onto this game's row in the list.</summary>
    private void RefreshRowFrom(GameReport report)
    {
        var game = report.Game;

        var waiting = _lineages.Known
            ? report.MyPosition?.BranchesWithWork ?? report.MyPosition?.BranchesCount
            : null;

        _situations[game.Path] = SituationReader.Read(
            report, _settings.ResolveTargetLanguage(),
            onlineChecked: report.OnlineChecked || !_settings.Current.OnlineMode,
            branchesWaiting: waiting,
            signedInAs: _settings.Current.ApiUser);

        // 🔴 **The account and the lineage too, and they were simply missing here.** This report was
        // built from the disk and from the site moments ago and carries both — while the two
        // collections the ROW draws from were left on an older reading. That is the third place the
        // same omission was made, and the reason a corner went on naming a previous account beside
        // a card already showing the current one.
        if (report.SiteAccount.User is not null) _accounts[game.Path] = report.SiteAccount;
        else _accounts.Remove(game.Path);

        if (report.MyPosition is not null) _mine.Add(game.Path); else _mine.Remove(game.Path);

        if (_rows.TryGetValue(game.Path, out var row) && row.Item.Tag is GameInstall shown)
        {
            var facts = FactsFor(game);
            row.Item.Content = BuildRowContent(shown, facts);
            _rows[game.Path] = (facts, row.Item);
        }
    }

    /// <summary>
    /// The card last drawn, so switching tabs can redraw a page without building a report again.
    ///
    /// ⚠ Cleared with the card — see <see cref="ClearDetail"/>. Held beyond it, a tab click on the
    /// overview, or after a game was forgotten, would redraw a page about a game nobody is looking
    /// at.
    /// </summary>
    private GameReport? _shownReport;

    private void RenderReport(GameReport report)
    {
        var game = report.Game;
        ClearDetail();

        _shownReport = report;

        // Back to filling the panel from the top: a report is a document, and a centred document
        // that grows past the viewport starts scrolled to its middle.
        DetailPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        DetailPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        DetailPanel.MaxWidth = double.PositiveInfinity;

        // The strip above belongs to the overview: it answers questions about this program, and a
        // game's card is not the place to be asked them. Its row collapses, so the card gets the
        // height back rather than keeping an empty band.
        OverviewTop.IsVisible = false;

        // 🔴 **Everything from here to the rail is the FRAME, and it does not scroll.** It is what
        // both tabs share and neither owns: the way back, the game's name, a notice true of the
        // whole card, and the tabs themselves. Kept in the scroller it went out of view three
        // screens into "Set up" — leaving nothing on screen to say which tab one was in, or to
        // offer the other — and it was thrown away and rebuilt on every tab click.
        CardHead.IsVisible = true;

        CardHead.Children.Add(BackToOverview(report));
        CardHead.Children.Add(Header(report));

        // 🔴 **Above the tabs, and the same banner the overview shows.** Nothing can be set up in
        // any game until this is answered, and the only place saying so was the band at the very
        // bottom of the card. Somebody who walked past it on the overview then met a greyed
        // OneClick with the reason a screen-height below the eye — no way forward, and the honest
        // conclusion is that the program does not work.
        //
        // ⚠ Not a second wording. It is the notice from the overview, repeated verbatim: seeing the
        // same sentence again is recognising it, seeing a new one is being told a new thing.
        //
        // ⚠ Above the tabs rather than inside one, because it is true of both halves and of every
        // game — reading goes top to bottom, and this is the first thing there is to say.
        //
        // ⚠ And only while it blocks THIS game. Unticking the box answers the question for this
        // game, so the banner would be telling somebody to go and decide something they have just
        // decided — above a OneClick that is lit and ready. On the overview it stays unconditional:
        // there it is a fact about the tool, not a refusal about one game.
        if (!_settings.Current.Reviewed && NeedsModDefaults(report))
            CardHead.Children.Add(WhatGoesIntoGames()!);

        if (BeTheFirstBanner(report) is { } invitation) CardHead.Children.Add(invitation);

        // A blocker belongs to both halves: "this game cannot be modded" IS the answer somebody
        // came for, and hiding it behind a tab would let Home offer a translation for a game that
        // can never run one.
        foreach (var blocker in report.Blockers)
            CardHead.Children.Add(Callout(blocker, Tone.Error));

        // ⚠ **Last in the frame, and that is the whole point of a rail.** It is the line the pages
        // hang from, so nothing of the frame may come between it and the page it introduces.
        // Placed after the technical card once, the tabs sat below a screenful of paths and engine
        // versions — somebody had to scroll to discover the card even had two halves.
        CardHead.Children.Add(TabStrip());

        // ⚠ Settled BEFORE the tabs split, and it used to live in the Setup branch alone. The bar
        // reads it, and the bar now exists on Home too: left where it was, a game opened on Home
        // would have been offered the answer computed for whichever game was looked at last.
        //
        // ⚠ **TranslationWaiting, not PickTranslation** — the one place that answers "what would go
        // into this game and is not there". It is silent once the game runs that very translation,
        // even with the server ahead: bringing a newer copy of the file already here is the
        // workbench's act, weighed against what was never uploaded, and an install option that did
        // it too was a second way to the same write.
        var offer = TranslationOffers.For(report, TranslationWaiting(report));

        // ⚠ **Naming one IS asking for it**, so a pending choice ticks the box whatever the stored
        // answer says. The translations window used to obtain this by writing InstallTranslation to
        // disk as it selected — which then stayed true for every later launch, on a game where
        // nobody had asked for anything.
        _takeTranslation = TranslationOffers.MayDefaultToYes(offer)
                           && (ChosenTranslation(report.Game.Path) is not null
                               || _preferences.Read(report.Game.Path).InstallTranslation);

        ShowTabBody(report);

        // ⚠ The bar belongs to EVERY tab, where it used to be written into each branch — and was
        // for a while missing from Home, on the argument that Home offers one way forward at a
        // time. What settles it is Play: wanting to start the game has nothing to do with which tab
        // is open. A bar that appears and disappears between tabs also changes the height of the
        // content on every switch.
        //
        // The competition that argument feared is real, and it is answered in GameHome: while this
        // bar has something to do, the buttons in the body drop to the outlined register.
        ShowActionBar(report);
    }

    /// <summary>
    /// Puts one page into the scroller, and nothing else.
    ///
    /// 🔴 **This is what a tab click costs now, and it used to cost a rebuilt card.** Switching
    /// tabs called ShowSelectedAsync, which asks the site for a fresh report and then draws the
    /// whole thing again — so a change concerning only what sits under the rail threw away the
    /// name, the banner and the rail itself, after a network round trip. The frame is untouched
    /// here: it belongs to the game, not to the page.
    ///
    /// ⚠ Back to the top, deliberately. Another page is another document, and landing half way down
    /// it because that is where the previous one was left is the tool remembering something nobody
    /// asked it to. The offset is only ever restored for a redraw of the SAME page — see
    /// ShowSelectedAsync.
    ///
    /// ⚠ _takeTranslation is NOT recomputed here: it is the answer somebody may have ticked, and
    /// working out the safe default again on a tab click would untick it under their hand. It is
    /// settled in RenderReport and nowhere else, which is what its own note says.
    /// </summary>
    private void ShowTabBody(GameReport report)
    {
        DetailPanel.Children.Clear();

        foreach (var control in PageFor(_gameTab).Body(report))
            DetailPanel.Children.Add(control);

        DetailScroll.Offset = default;

        // The page arrives rather than appearing — the same motion the game list plays when its
        // filter changes, because it says the same thing: what you were looking at has been
        // replaced. See Motion.Arrive, which holds the durations and the reasoning.
        Motion.Arrive(DetailPanel);
    }

    /// <summary>
    /// The Set up half: what this game is made of, and the three things that can be installed in it.
    /// </summary>
    private IEnumerable<Control> GameSetup(GameReport report)
    {
        // ⚠ Paths, engine version, architecture: the technical answer, and it opens the SET UP
        // half rather than the card. It was the first thing on every game — before knowing whether
        // a translation even existed — which is the wrong first question for almost everybody.
        yield return Card(Facts(report));

        foreach (var warning in report.Warnings)
            yield return Callout(warning, Tone.Warning);

        // Three cards for three subjects, where there used to be one called "Actions".
        //
        // The loader and the mod are published by different people on different days and are
        // installed by separate steps; folding them into one block meant their versions could not
        // both be shown, and the single button had to pretend they moved together. Each card now
        // carries its own version, its own verb, and nothing that belongs to the other.
        yield return Card(LoaderSection(report));
        yield return Card(ModSection(report));
        yield return Card(Translations(report));
    }

    /// <summary>
    /// The answer somebody came for, before any machinery: what this game has, what exists for it,
    /// and the one thing to do next.
    /// </summary>
    private IEnumerable<Control> GameHome(GameReport report)
    {
        var target = _settings.ResolveTargetLanguage();

        // 🔴 **Who published what this game runs — named, never "yours".** This read
        // MyTranslationHere, which answers a deliberately different question: does the account THIS
        // GAME is signed in as own the translation. True on a game somebody else set up, so the
        // card said "your own translation" to a reader who has nothing to do with it — a possessive
        // whose referent is a different person, on a machine that legitimately carries several
        // people's games. The test the naming rule gives: replace it with a proper noun; if you
        // cannot say which one to write, the possessive is false. Here we can.
        var installedBy = report.MatchingOnline?.Author;
        var mine = People.IsYou(installedBy, _settings.Current.ApiUser);

        // ⚠ ONE filled button on screen at a time, and the bar has first claim on it: it is the
        // fixed place, in the same spot on both tabs, and what it runs is the whole job rather
        // than a step of it. These open a list to choose from — a refinement of what one click
        // would take by itself — so they step down to the outlined register while there is
        // anything for that click to do.
        //
        // Not a constant: on a game with nothing left to install, choosing a translation IS the
        // act of this screen, and it takes the fill back. Same reading either way — the loudest
        // thing on the card is the thing to do next.
        var barActs = OneClickSteps(report, EffectivePreference(report)).Any();
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
                // ⚠ Three answers, not two. Naming the author is what a reader needs before doing
                // anything here, and it is the one the card never gave: "already has a translation
                // installed" said nothing about whose it was, so somebody had to open the game to
                // find out. The last case stays for a translation nobody published — there is
                // genuinely no name to write.
                Text = mine
                    ? "This game is running your own translation."
                    : installedBy is { Length: > 0 }
                        ? $"This game is running {People.Mention(installedBy)}'s translation."
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
        else
        {
            // 🔴 **The way back, on the tab people actually open.** Everything above lives inside
            // "this game holds a translation", so removing one took the whole card away — and with
            // it the only door to the copies that removal had just taken. Set up still showed it,
            // but nobody goes to Set up to undo something they did here.
            //
            // ⚠ Only when there IS something to put back: a door to an empty room is worse than no
            // door, and with no translation there is nothing to back up either.
            var loaderId = report.InstalledLoader?.Id ?? report.RecommendedLoader?.Id;
            var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == loaderId);

            if (descriptor is not null)
            {
                var kept = TranslationBackupStore.List(report.Game.Path, descriptor);

                if (kept.Count > 0)
                {
                    var standing = ServerIdentity.For(_settings.Current, report.SiteAccount,
                                                      BuildInfo.ApiBaseUrl);

                    var body = new StackPanel { Spacing = 4 };

                    body.Children.Add(new TextBlock
                    {
                        Text = "This game holds no translation.",
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush("TextPrimary"),
                    });

                    body.Children.Add(new TextBlock
                    {
                        Text = kept.Count == 1
                            ? "One copy is kept — you can put it back."
                            : $"{kept.Count} copies are kept — you can put one back.",
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush("TextSecondary"),
                    });

                    var row = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Avalonia.Thickness(0, 6, 0, 0),
                    };

                    row.Children.Add(BackupsButton(report, descriptor, standing, kept));
                    body.Children.Add(row);

                    yield return Card(body);
                }
            }
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
            // ⚠ Said by NothingPublishedYet below, and nowhere else. A heading here read "Nothing
            // has been published for this game yet" and that method then added "No translation has
            // been published for this game yet" — one fact, twice, three lines apart, in two
            // wordings. The card above says it a third time in its own register.
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

            // 🔴 **The language this game RUNS is a second answer, not a replacement for yours.**
            // The window opens on the installed translation's language unless told otherwise, so a
            // game running an English translation opened on English under a button reading "Choose
            // one in French". Choosing another language for one game is a deliberate act — those
            // are the ones somebody wants to see again — and it does not cancel the default.
            //
            // ⚠ Two buttons only when the two differ, and the game's own comes first: this card is
            // about this game. When they are the same there is one answer and one button.
            var loaderHere = report.InstalledLoader?.Id ?? report.RecommendedLoader?.Id;
            var here = _catalog.Loaders.FirstOrDefault(l => l.Id == loaderHere);

            var gameLanguage = report.MatchingOnline?.TargetLanguage
                               ?? (here is null
                                   ? null
                                   : LocalTranslationProbe.ReadTargetLanguage(report.Game.Path, here));

            var myLanguage = Languages.NameOf(target);

            var elsewhereInGameLanguage = gameLanguage is { Length: > 0 }
                                          && !Languages.Matches(gameLanguage, target)
                                          && report.OnlineTranslations.Any(
                                              t => Languages.Matches(t.TargetLanguage, gameLanguage));

            if (elsewhereInGameLanguage)
            {
                var itsOwn = new Button
                {
                    Content = $"Choose one in {gameLanguage}",
                    FontSize = 12,
                    Classes = { bodyLead },
                };

                itsOwn.Click += async (_, _) =>
                    await OpenTranslationsAsync(report, openWith: gameLanguage);

                buttons.Children.Add(itsOwn);
            }

            if (inMyLanguage.Count > 0)
            {
                var mineFirst = new Button
                {
                    Content = $"Choose one in {myLanguage}",
                    FontSize = 12,

                    // Second when this game already runs another language: that one is the state
                    // in front of the reader, and one filled button per row.
                    Classes = { elsewhereInGameLanguage ? "" : bodyLead },
                };

                // ⚠ Named, never left to the window: saying a language on a button and letting the
                // window pick a different one is the defect this whole block answers.
                mineFirst.Click += async (_, _) => await OpenTranslationsAsync(report, openWith: myLanguage);
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
            // ⚠ The same words as the other tab, from one place. They were written twice, in two
            // registers, for a fact that does not change with the tab it is read on.
            foreach (var control in NothingPublishedYet(report)) question.Children.Add(control);
        }

        // ⚠ No card when there is nothing to put in one. On a refused game with nothing published,
        // everything above declines to speak — and a Card() around an empty panel is not nothing,
        // it is a bordered box with a blank inside, which reads as a defect rather than as silence.
        if (question.Children.Count > 0) yield return Card(question);

        // ── What setting this game up would actually produce ──────────────────────────────────
        //
        // 🔴 **The one-click was offered with nothing behind it, and said nothing about that.** On
        // a game with no published translation, with Mod defaults set to take community work and
        // no translator configured, "OneClick Set Up this Game" installs the loader, the mod and
        // the defaults — real work, correctly offered — and then the game runs with nothing to
        // read. The card described what the mod CAN do and never joined the two facts.
        //
        // ⚠ **A warning, not an error.** Nothing is wrong and nothing is refused: playing with
        // what the community publishes is the ordinary way to use this, and the capture and the
        // in-game editor still work. What changes is what pressing the button will get you, which
        // is exactly what the warning tone is for elsewhere in this window.
        //
        // ⚠ And it carries the way out. Saying "there is nothing to translate with" without the
        // door to set one up is the dead end this project refuses everywhere else.
        // ⚠ **Only where setting the game up is possible at all.** On a game that cannot take the
        // mod — a stripped runtime, an anti-cheat, a refusal already stated in red at the top of
        // this card — Mod defaults changes nothing, and offering it is a second voice saying
        // something about a game whose answer is already no. This warning is for a game that WOULD
        // work and has nothing to work with.
        if (report.Game.IsModdable
            && report.Blockers.Count == 0
            && report.OnlineTranslations.Count == 0
            && TranslationBackendLabel(_settings.Current) is null)
        {
            var empty = new StackPanel { Spacing = 4 };

            empty.Children.Add(new TextBlock
            {
                Text = "There is nothing to translate this game with yet.",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextPrimary"),
            });

            empty.Children.Add(new TextBlock
            {
                // ⚠ Same order as the banner and the picker: what costs nothing first. An AI on
                // the machine is the short road when the machine allows it; writing the lines
                // yourself always works.
                Text = "\"Mod defaults\" is set to \"Community translations only\", and this game "
                     + "has none. Pick an AI on your own machine — free if it can run one — or "
                     + "\"Captures only\" and write the lines yourself in the mod's editor.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
            });

            var open = new Button
            {
                Content = "Open Mod defaults",
                FontSize = 12,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };

            ToolTip.SetTip(open, "Where the translator is chosen — your own AI, Google or DeepL "
                                 + "with your key. It applies to every game that follows the "
                                 + "defaults.");

            open.Click += async (_, _) => await OpenSettingsAsync();
            empty.Children.Add(open);

            yield return Callout(empty, Tone.Warning);
        }

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
        //
        // ⚠ **Nothing is next on a game that cannot take the mod.** The refusal is already stated
        // in red at the top of this card; following it with "Needs the mod loader and the mod." and
        // a lit "Set this game up" is a task list for work this program has just said it will not
        // do — the same fault as the warning above, and it reads as a program contradicting itself.
        //
        // ⚠ Nobody is stranded by its absence: the tab strip is above, and Set up is where a
        // verdict that can be overridden is argued (see ModdabilityProbe.CanBeOverridden).
        if (!report.Game.IsModdable || report.Blockers.Count > 0) yield break;

        var next = new StackPanel { Spacing = 6 };
        var installed = report.InstalledPluginVersion is not null;

        // What Set up would have to do, said here rather than found there. "Up to date" is worth
        // as much as a pending update: it is the answer to "do I need to go and look".
        var pending = new List<string>();
        // ⚠ Same two words as the rest of this card: "Needs the loader and the mod" left the
        // reader to work out that the two are different things.
        if (report.InstalledLoader is null) pending.Add("the mod loader");
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
            // ⚠ "mod loader", the same two words the line below uses and the same the game list
            // uses. "The loader" alone assumed the reader knows which loader is meant, and the two
            // lines of this card were then naming one thing two ways.
            Text = pending.Count == 0
                ? (loaderTheirs
                    ? "The mod is installed and up to date."
                    : "The mod loader and the mod are installed and up to date.")
                : $"Needs {string.Join(" and ", pending)}.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(pending.Count == 0 && !loaderTheirs ? "StatusSuccess" : "TextSecondary"),
        });

        if (loaderTheirs && report.LoaderStanding is { } theirs)
        {
            // 🔴 **Say WHAT this is about, right after a line that said the mod is fine.**
            //
            // It opened on the loader's own name — "BepInEx 6 (IL2CPP) 6.0.0-be.755 → …" — under
            // "The mod is installed and up to date." Anyone who does not already know that BepInEx
            // is the thing loading mods reads two sentences contradicting each other about the
            // same object. Naming the category costs two words and removes the whole ambiguity.
            //
            // ⚠ "from here" is gone too: a deictic pointing at nothing (this window? this
            // machine?). The thing that did or did not install it has a name, and it is the same
            // name the game list uses — see SituationReader, "not managed by UGT".
            next.Children.Add(new TextBlock
            {
                Text = $"Mod loader — {report.InstalledLoader!.Display} {theirs.Installed} → {theirs.Available} "
                     + "is out. UnityGameTranslator did not install it, so updating it has to be "
                     + "allowed first — in Set up.",
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

        // 🔴 **A THIRD door to TakeSelectedTranslationAsync, and it had neither the mark nor the
        // guard.** That method carries no account check of its own — every caller's BUTTON holds
        // it, which is what the workbench's Apply says in as many words two hundred lines below:
        // "Taking a translation OVERWRITES this game's file, so it obeys the account rule."
        //
        // This one replaces the very same file with only "the game is not running" in front of it,
        // so on a game set up under somebody else's account it stayed live while Apply, Edit,
        // Merge, Publish and Remove were all greyed. Same family as the two doors of 2026-07-26:
        // sharing the function is not sharing the act, and the conditions live with the control.
        //
        // ⚠ The guard applies to the SINGLE case only. With several, this button opens the list and
        // writes nothing — greying it would block looking, which nothing here has any reason to do.
        var standing = ServerIdentity.For(_settings.Current, report.SiteAccount, BuildInfo.ApiBaseUrl);
        var mayTake = !_running.IsRunning(report.Game) && standing.CanWriteLocally;

        // ⚠ Marked, like every other action that puts a file into a game. Local: this restores the
        // published translation onto this machine, and the site keeps whatever it held.
        var take = mine.Count == 1
            ? ScopeMark.Marked(EditSide.Local, "Restore my published translation", mayTake)
            : new Button
            {
                Content = "Choose which one to install",
                FontSize = 12,
                IsEnabled = !_running.IsRunning(report.Game),
            };

        take.HorizontalAlignment = HorizontalAlignment.Left;
        take.Margin = new Avalonia.Thickness(0, 4, 0, 0);

        // No greyed control without words — the rule this program holds everywhere.
        if (mine.Count == 1 && !mayTake)
        {
            ToolTip.SetTip(take, _running.IsRunning(report.Game)
                ? "This game is open. The mod rewrites its translation file from memory while it "
                  + "runs, so anything written now would be replaced without warning."
                : standing.Reason);
        }

        take.Click += async (_, _) =>
        {
            // More than one: the choice needs what separates them, which is the list's job.
            if (mine.Count > 1)
            {
                await OpenTranslationsAsync(report);
                return;
            }

            // ⚠ Held for the session like any other answer given before an act — see
            // _pendingTranslation. Written to disk it would go on asking to be restored long after
            // it had been, on every launch.
            _pendingTranslation[report.Game.Path] = mine[0].Id;

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
        yield return Callout(body, Tone.Info);
    }

    /// <summary>
    /// The translation this game would receive and has not got — chosen from the list, or proposed
    /// by ranking — and null when there is nothing to put there.
    ///
    /// 🔴 **Proposals count too, and that is a change.** It used to answer for deliberate choices
    /// only, which meant the OTHER tab needed a second button for the proposed one — a second door
    /// to <see cref="TakeSelectedTranslationAsync"/>, with its own label and its own guards, and it
    /// was the door that had no account check. One act, one entry point: the difference between a
    /// choice and a proposal belongs in the WORDS, not in a separate control.
    ///
    /// 🔴 **Silent when this game already runs that very translation**, whether or not the server
    /// has moved since. Bringing down a newer version of the file already here is the workbench's
    /// act — "Download what changed online…", which weighs the merge and carries its own scope mark.
    /// Offering it here as well put two buttons three inches apart doing one thing.
    /// </summary>
    /// ⚠ The rule itself lives in <see cref="TranslationChoice"/>, where it can be checked. What
    /// belongs here is only where its three answers come FROM — and getting that wrong is what the
    /// separation is for: the intention is held for the session, the installed id is read off disk,
    /// and the two used to be the same field.
    private OnlineTranslation? TranslationWaiting(GameReport report) =>
        TranslationChoice.Waiting(
            report,
            _settings.ResolveTargetLanguage(),
            chosen: ChosenTranslation(report.Game.Path),
            installed: _preferences.Read(report.Game.Path).InstalledTranslationId);

    /// <summary>Whether the waiting translation was named by somebody, rather than ranked for them.</summary>
    private bool WasChosenDeliberately(GameReport report, OnlineTranslation picked) =>
        ChosenTranslation(report.Game.Path) == picked.Id;

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

        var offer = TranslationOffers.For(report, picked);
        var deliberate = WasChosenDeliberately(report, picked);

        // ⚠ **"Apply (N)" only for something somebody chose.** It is the norm for a PENDING change,
        // and a proposal nobody made is not pending — so the act is named instead. The three labels
        // came from the second entry point this replaces, where they were already right.
        var label = (offer, deliberate) switch
        {
            (_, true) => "Apply (1)",
            (TranslationOffer.ReplacesWork or TranslationOffer.ReplacesChoice, _)
                => "Replace it with this one...",
            (_, _) when report.LocalTranslation is not null => "Update the translation",
            _ => "Download this translation",
        };

        // 🔴 **Taking a translation OVERWRITES this game's file, so it obeys the account rule.**
        //
        // The guards here were "the mod is installed" and "the game is not running". On a game set
        // up under somebody else's account every control of the workbench is greyed — Edit, Publish,
        // Merge, Remove — and this one, which replaces the very same file, stayed live.
        var standing = ServerIdentity.For(_settings.Current, report.SiteAccount, BuildInfo.ApiBaseUrl);
        var ready = report.InstalledPluginVersion is not null
                    && !_running.IsRunning(report.Game)
                    && standing.CanWriteLocally;

        // ⚠ Marked like every other action that writes. Apply is not a lesser verb because it
        // carries a count: it puts a file into a game, and where a write lands is the first thing
        // this interface promises to say. Local — the site holds whatever it held before.
        var apply = ScopeMark.Marked(EditSide.Local, label, ready);
        apply.Classes.Add("primary");

        // No greyed control without words — the rule this program holds everywhere.
        ToolTip.SetTip(apply, report.InstalledPluginVersion is null
            ? "The mod is not installed in this game yet, so there is nowhere to put a translation."
            : _running.IsRunning(report.Game)
                ? "This game is open. The mod rewrites its translation file from memory while it "
                  + "runs, so anything written now would be replaced without warning."
                : standing.Reason
                  ?? $"Puts {picked.SourceLanguage} → {picked.TargetLanguage} by "
                     + $"{People.MentionOf(picked.Author, _settings.Current.ApiUser)} into this game.");

        var replacing = offer is TranslationOffer.ReplacesWork or TranslationOffer.ReplacesChoice;

        apply.Click += async (_, _) => await TakeSelectedTranslationAsync(report, picked, replacing);
        row.Children.Add(apply);

        // 🔴 **There is no Undo here, and there should not be.** One stood beside Apply and forgot
        // the stored choice. It read as "put the translation back" and did nothing of the sort —
        // nothing had been written yet — and its real effect was invisible: with the chosen
        // translation also the best-ranked, which is the ordinary case, the row redrew identically
        // and the only thing that changed was the button disappearing.
        //
        // ⚠ What it protected is covered without it. Changing one's mind is picking another from
        // the list, one click away; and with no choice stored the one-click proposing the
        // best-ranked is what happens anyway, so "forget my choice" was a state nobody needs to
        // reach by name. Putting a translation BACK is Backups, which acts on files.
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
        var author = People.MentionOf(picked.Author, _settings.Current.ApiUser);

        // 🔴 **A choice and a proposal are not announced the same way.** Presenting a pick made by
        // ranking as "Chosen" is how somebody ends up with a translation they never agreed to — and
        // the rule behind the proposal is worth naming, because it is a defensible rule rather than
        // a coin toss. The way to overrule it is named too: the list is one button away.
        var deliberate = WasChosenDeliberately(report, picked);

        lines.Children.Add(new TextBlock
        {
            Text = deliberate
                ? $"Chosen: {picked.SourceLanguage} → {picked.TargetLanguage} by {author}{size}. "
                  + "Not in the game yet."
                : $"Chosen for you: the best-ranked one in {picked.TargetLanguage ?? "your language"}, "
                  + $"by {author}{size}. Open the list to pick another.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(deliberate ? "StatusInfo" : "TextMuted"),
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

        // 🔴 **The list too, and that is the whole point of the tag being there.** This said "only
        // the card: the list rows carry no build version" — true when it was written, false since
        // ReadSituation learnt to compute LoaderStanding for the rows. What it produced was a tag
        // that appeared only once its game had been SELECTED, because selecting is what drew the
        // card that warmed this cache: an at-a-glance badge you had to click to glance at.
        //
        // ⚠ Recompute before refresh: the rows are drawn from _situations, so refreshing without
        // re-reading redraws the same stale answer.
        await RepublishAsync();
    }

    /// <summary>
    /// Asks GitHub which mod release is newest, once, and redraws when it answers.
    ///
    /// 🔴 **The twin of <see cref="WarmLoaderBuildsAsync"/>, and its absence produced the very
    /// defect written up there: "an at-a-glance badge you had to click to glance at".** ReadSituation
    /// fills PluginStanding from PluginReleases.Known, which answers nothing until somebody has
    /// asked — and the only caller that ever asked was BuildReportAsync, i.e. selecting a game. So
    /// the list said nothing about any game until each one had been clicked, one by one, and even
    /// then the other rows stayed quiet because nothing recomputed them afterwards.
    ///
    /// ⚠ ScanAsync calls Forget() so that a rescan asks again. Forgetting without re-asking is what
    /// left Known() empty for the whole session: the question has to be posed here.
    ///
    /// ⚠ Behind the same two settings the rows read, or the list would announce something the card
    /// refuses to.
    /// </summary>
    private async Task WarmPluginReleaseAsync()
    {
        if (!_settings.Current.OnlineMode || !_settings.Current.CheckContentUpdates) return;

        try
        {
            await _releases.LatestAsync(
                string.Equals(_settings.Current.Channel, "beta", StringComparison.OrdinalIgnoreCase)
                    ? ReleaseChannel.Beta
                    : ReleaseChannel.Stable).ConfigureAwait(true);
        }
        catch
        {
            // A blocked request leaves Known() null, which every reader already treats as "not
            // known yet" rather than "up to date" — the distinction PluginReleases exists to keep.
            return;
        }

        await RepublishAsync();
    }

    /// <summary>Which page of a game's card is showing. Home first, always — see TabStrip.</summary>
    private enum GameTab { Home, Setup }

    private GameTab _gameTab = GameTab.Home;

    /// <summary>
    /// One page of a game's card: what the tab is called, and what it puts on the panel.
    /// </summary>
    /// <param name="Body">
    /// Yields the controls, and adds none itself — so a page cannot quietly touch the panel around
    /// it, and the frame (header, tabs, blockers, the action bar) stays the same on every one.
    /// </param>
    private sealed record GameTabPage(GameTab Tab, string Label,
                                      Func<GameReport, IEnumerable<Control>> Body);

    /// <summary>
    /// The pages, in the order they are offered. **Adding one is adding a line here.**
    ///
    /// 🔴 Written as a list because more are coming, and because the two that exist were spelled out
    /// in four places: the enum, the array the strip walked, a ternary picking the label, and an
    /// `if` in RenderReport choosing the body. Four places is four chances for a third page to be
    /// half-added — offered by the strip and drawing nothing, or drawing something no tab reaches.
    ///
    /// ⚠ Order is reading order, and Home stays first: it answers "where does this game stand",
    /// which is what somebody clicking a game came to find out. Set up is what they do next.
    /// </summary>
    private IReadOnlyList<GameTabPage> GameTabs => new[]
    {
        new GameTabPage(GameTab.Home, "This game", GameHome),
        new GameTabPage(GameTab.Setup, "Set up", GameSetup),
    };

    /// <summary>
    /// The page for a tab — the first one when the tab is not among them, which is what makes an
    /// enum value with no page a wrong-looking screen rather than an empty one.
    /// </summary>
    private GameTabPage PageFor(GameTab tab) =>
        GameTabs.FirstOrDefault(page => page.Tab == tab) ?? GameTabs[0];

    /// <summary>
    /// Which folded blocks of the current card are open.
    ///
    /// 🔴 **A card is redrawn constantly, and every redraw used to fold everything shut.** Apply a
    /// setting, change the language, let a version resolve in the background — the block being
    /// worked in closed under the person working in it, and they had to find it and reopen it. A
    /// fold is a place somebody put themselves in, not a property of the freshly built controls.
    ///
    /// ⚠ Cleared with the tab, and for the same reason: it is a place in ONE card. Carried across a
    /// click in the list it would open somebody else's machinery on a game nobody has looked at.
    /// </summary>
    private readonly HashSet<string> _openBlocks = new(StringComparer.Ordinal);

    /// <summary>Opens it where it was left, and remembers where it is left.</summary>
    private Expander Remembering(Expander expander, string key)
    {
        expander.IsExpanded = _openBlocks.Contains(key);

        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property != Expander.IsExpandedProperty) return;

            if (expander.IsExpanded) _openBlocks.Add(key);
            else _openBlocks.Remove(key);
        };

        return expander;
    }

    /// <summary>
    /// The tabs, and the only place that switches between them.
    ///
    /// ⚠ Reset to Home on every game, deliberately: the tab is a place in ONE game's card, not a
    /// preference about the tool. Carrying "Set up" across a click in the list would drop somebody
    /// into the machinery of a game they have not yet looked at.
    ///
    /// ⚠ Walks <see cref="GameTabs"/> and names nothing itself. A strip that spelled its own tabs
    /// out could offer one that draws nothing, or leave out one that draws.
    /// </summary>
    private Control TabStrip()
    {
        // ⚠ Tabs sit side by side, where buttons are spaced apart. The gap is half the argument:
        // things that touch read as one set of places, things kept apart read as separate acts.
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        foreach (var page in GameTabs)
        {
            var button = new Button
            {
                Content = page.Label,

                // ⚠ **"tab", never "primary".** Wearing the primary class made the open tab
                // pixel-identical to the one-click a few inches below — one saying "you are here",
                // the other "this writes into your game". See Button.tab in App.axaml.
                Classes = { "tab" },
            };

            button.Classes.Set("selected", page.Tab == _gameTab);

            var chosen = page.Tab;
            button.Click += (_, _) =>
            {
                if (_gameTab == chosen) return;
                _gameTab = chosen;

                // The mark moves on the buttons already on screen, exactly as the filter chips do.
                // Rebuilding the strip would replace the control under the pointer that just
                // pressed it, which loses its hover.
                foreach (var other in strip.Children.OfType<Button>())
                    other.Classes.Set("selected", ReferenceEquals(other, button));

                // 🔴 **Only the page, and no report is built.** This awaited ShowSelectedAsync,
                // which asks the site for a fresh report before drawing the whole card again — a
                // network round trip and a full rebuild to change what sits under the rail. What is
                // on screen was drawn from a report we still hold.
                if (_shownReport is { } shown) ShowTabBody(shown);
            };

            strip.Children.Add(button);
        }

        // The rail the open tab's mark sits on. It is what turns two underlined words into a set of
        // places: without it the mark reads as decoration on one item rather than as a position
        // among several.
        // ⚠ No bottom margin: the page below brings its own top margin, and adding one here would
        // push the rail away from the page it introduces — which is the one thing it must touch.
        return new Border
        {
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            Child = strip,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
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
            // Normalised like DataFolder is, so the two compare as paths and not as spellings.
            var pluginDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(game.Path,
                descriptor.PluginDir.Replace('/', System.IO.Path.DirectorySeparatorChar)));

            var dataDir = UserDataInventory.DataFolder(game.Path, descriptor);

            if (System.IO.Directory.Exists(pluginDir)) text.Children.Add(FolderRow(pluginDir, "the mod"));

            // Only when it is genuinely another place.
            if (dataDir is not null
                && !string.Equals(pluginDir, dataDir, StringComparison.OrdinalIgnoreCase)
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
    /// <summary>
    /// What the contributions are holding: the group, then a chip per quality.
    ///
    /// 🔴 **The same four letters the website draws as coloured squares**, which arrived here as
    /// grey prose inside a sentence. They are what says whether an evening is worth it — nine
    /// lines written by hand is not the proposition nine machine lines are.
    ///
    /// ⚠ Order, labels and which zeros are left out all come from the socle
    /// (<see cref="Common.Contributions.KindsOfWork"/>), which composes the printed sentence from
    /// the very same pieces. This only decides how a piece looks.
    /// </summary>
    private IEnumerable<Control> ContributionChips(LineagePosition position)
    {
        var kinds = position.Kinds;
        if (kinds.Length == 0) yield break;

        var row = new WrapPanel { Margin = new Avalonia.Thickness(0, 2, 0, 0) };

        row.Children.Add(new TextBlock
        {
            Text = position.ToReview + ":",
            FontSize = 12,
            // ⚠ White, not the muted grey: the line above is the one asking for something and keeps
            // the amber, while this one ANSWERS "what is in it". A fact read beside a call to action
            // must not compete with it — nor look like a footnote. Same choice in the mod's status
            // card, because it is the same sentence.
            Foreground = Brush("TextPrimary"),
            Margin = new Avalonia.Thickness(0, 0, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });

        for (int k = 0; k < kinds.Length; k++)
        {
            row.Children.Add(new TextBlock
            {
                // The separator the sentence uses between groups, so the two read alike.
                Text = (k > 0 ? "· " : "") + kinds[k].Total + " " + kinds[k].Label,
                FontSize = 12,
                Foreground = Brush("TextPrimary"),
                Margin = new Avalonia.Thickness(k > 0 ? 6 : 0, 0, 4, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });

            foreach (var piece in kinds[k].Tally.Counted())
            {
                row.Children.Add(new Border
                {
                    Background = Brush("Chip" + piece.Letter),
                    // ⚠ Qualified: `Theme` alone resolves to Avalonia's ControlTheme here.
                    CornerRadius = new Avalonia.CornerRadius(Common.Theme.ChipRadius),
                    Padding = new Avalonia.Thickness(5, 1, 5, 1),
                    Margin = new Avalonia.Thickness(0, 0, 3, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = piece.Letter,
                        FontSize = 11,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        Foreground = Brush("ChipLetter"),
                    },
                });

                row.Children.Add(new TextBlock
                {
                    Text = piece.Count.ToString(),
                    FontSize = 12,
                    Foreground = Brush("TextPrimary"),
                    Margin = new Avalonia.Thickness(0, 0, 6, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
            }
        }

        yield return row;
    }

    /// <summary>
    /// Where this account stands in the lineage of the file this game holds — and, when the two
    /// are not the same person's, whose is whose.
    ///
    /// 🔴 **The card mixes two subjects and used to name only one.** Everything else in this panel
    /// is about the copy in the game folder; <see cref="GameReport.MyPosition"/> is about the
    /// signed-in account on the site this window talks to, matched on uuid alone
    /// (GameInventory.BuildReport). On an ordinary game they are the same thing and saying so would
    /// be noise. On a game belonging to somebody else they are not, and the card said:
    ///
    ///   · every local control greyed, with the refusal at the top of the workbench;
    ///   · "This translation is yours, and there is work waiting", in amber, with a live button.
    ///
    /// Both true. Read together, a bug — and "This translation" points at the file in front of the
    /// reader, which is precisely the one thing that is not theirs.
    ///
    /// ⚠ **Not a dev-only case, which is how it was first read.** Two ordinary routes reach it:
    /// somebody who downloaded a Main this account leads, on a shared computer (OtherAccount — the
    /// case ServerIdentity exists for); and a game pointed at a self-hosted instance
    /// (OtherServer, via the mod's api_base_url override). It is at its most confusing when both
    /// accounts carry the SAME NAME and differ only by server: every label the card already has
    /// then says the same word on both sides.
    ///
    /// ⚠ **Nothing is hidden and nothing is greyed here.** The count is a true fact about this
    /// account, and reviewing writes to the site this window is signed into — never to the game.
    /// What was missing was the subject, so the subject is what gets added.
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

        // ⚠ The same reading every other control on this card makes, from the same call. A second
        // way of working out whose game this is would be a second answer waiting to disagree.
        var standing = ServerIdentity.For(_settings.Current, report.SiteAccount, BuildInfo.ApiBaseUrl);

        // The subject, said once, and only when the two subjects differ — the ordinary game says
        // nothing, exactly as ServerStanding.Reason stays null when there is nothing to explain.
        //
        // ⚠ A heading rather than the inline "On this machine: …" three lines up, because what
        // follows is a GROUP — a sentence that wraps, the contribution chips, and a button — where
        // that one labels a single short value. Same grammar, the shape a group takes.
        if (!standing.CanWriteLocally)
        {
            yield return new TextBlock
            {
                Text = "On your account",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("TextMuted"),
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            };
        }

        if (position.IsMain)
        {
            // What is waiting, not how many contribute — same rule as Describe() reads.
            var waiting = position.BranchesWithWork ?? position.BranchesCount ?? 0;

            // 🔴 **The colour follows the sentence.** Describe() says one of two things — "this is
            // yours" or "this is yours, and there is work waiting" — and both came out green.
            // Green reads as "nothing to do", which is the opposite of the second one: an owner
            // with contributions to go through was being reassured by the very line telling them
            // otherwise. The mod had the mirror-image fault, muting the same sentence to grey.
            // ⚠ withKinds: false — the qualities are DRAWN just below, as the chips the website
            // uses, instead of being spelt out in the sentence. Everything else it says is
            // unchanged, and a caller that can only print still gets the whole thing.
            yield return new TextBlock
            {
                Text = position.Describe(withKinds: false),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(waiting > 0 ? "StatusWarning" : "StatusSuccess"),
                Margin = new Avalonia.Thickness(0, 2, 0, 0),
            };

            foreach (var row in ContributionChips(position)) yield return row;

            // Reviewing happens on the site — merging a contribution means reading both files side
            // by side, which is a screen, not a line on a card. Offered only when there is
            // something to review: a button that leads to an empty page is worse than no button.
            if (waiting > 0)
            {
                // 🔴 **Marked, like every other action that writes — and this was the one that was
                // not.** Of the five buttons on this window built without a scope mark, four open a
                // folder, launch the game or go Home: they write nothing, so there is nothing to
                // mark. This one leads to a merge, and where a write lands is the first thing this
                // interface promises to say.
                //
                // ⚠ Server, and the socle names this very action as its example: "work done with no
                // game and no manager on the other end — merging a contribution from the website".
                // The published translation carries the result; the file in this game does not
                // move, which is also the answer to why this button stays live on a game nothing
                // else here may touch.
                var review = ScopeMark.Marked(EditSide.Server, "Review them on the site");
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

        // 🔴 **Choosing and applying share a line — the same shape as the other tab, and it was not.**
        //
        // This card used to read: a sentence naming what would be taken, a button to open the list,
        // the workbench's six buttons, and THEN the verb that applies the choice. Two halves of one
        // gesture with a third thing wedged between them — and the verb was a second door into
        // TakeSelectedTranslationAsync, the one door with no account check on it.
        //
        // ⚠ The note goes ABOVE the row: what is waiting has to be readable before the verb that
        // acts on it. Somebody whose eye lands on the button and has to look elsewhere to learn
        // what it applies has already been asked to press something unnamed.
        var offered = report.OnlineTranslations.Count;

        if (offered == 0)
        {
            foreach (var control in NothingPublishedYet(report)) panel.Children.Add(control);
        }
        else
        {
            if (PendingTranslationNote(report) is { } waitingNote) panel.Children.Add(waitingNote);

            // One button rather than a list of names: choosing between translations needs what they
            // are made of, who reviewed them and which language they came FROM — none of which fits
            // on a line here, and all of which decides the choice.
            var browse = new Button
            {
                Content = offered == 1 ? "See the translation" : $"See the {offered} translations",
                FontSize = 12,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };

            browse.Click += async (_, _) => await OpenTranslationsAsync(report);

            var choiceRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            };

            Grid.SetColumn(browse, 0);
            choiceRow.Children.Add(browse);

            if (PendingTranslationActions(report) is { } waiting)
            {
                Grid.SetColumn(waiting, 2);
                choiceRow.Children.Add(waiting);
            }

            panel.Children.Add(choiceRow);
        }

        foreach (var control in TranslationWorkbench(report)) panel.Children.Add(control);
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

        // ⚠ **Whose work this is, said out loud and not only in a tooltip.** The strip above can
        // say "Not yours", and a chip is two words: the name of the person who leads the lineage is
        // what turns that into something actionable. The mod has shown this line for months; this
        // screen showed nothing, so a player looking at a game carrying somebody else's translation
        // had no way to know whose it was without opening the game.
        //
        // Only when it IS somebody else's: on your own work there is nobody to credit.
        if (report.MyPosition is null && report.MatchingOnline?.Author is { Length: > 0 } author)
        {
            yield return new TextBlock
            {
                Text = "Based on the translation of "
                     + People.MentionOf(author, _settings.Current.ApiUser),
                FontSize = 12,
                Foreground = Palette.Of("TextMuted"),
                Margin = new Avalonia.Thickness(0, 2, 0, 0),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
        }

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
    /// Close the browser session before letting the window go.
    ///
    /// 🔴 **The wait is not politeness, it is the drain.** CloseAsync fetches what the browser
    /// saved and writes it into the game BEFORE deleting the session — saves made since the last
    /// tick exist in the session and nowhere else. Letting the window close immediately would
    /// destroy work the site told somebody was saved, which is the exact defect that drain was
    /// written to fix.
    ///
    /// ⚠ Bounded, and it closes either way. A follower that died without tidying up must not be
    /// able to hold a window open; the session then expires on the site on its own, which is the
    /// same outcome a crash already has.
    ///
    /// ⚠ Not a substitute for <see cref="EditSessionRunner.Resume"/>: a kill or a power cut never
    /// reaches this handler, and picking the session back up at the next start is what covers
    /// those.
    /// </summary>
    private async void OnClosingWithEditorOpen(object? sender, WindowClosingEventArgs e)
    {
        if (_editSession is null || _closingForReal) return;

        e.Cancel = true;

        await StopLocalEditorAsync();

        for (var waited = 0; waited < 30 && _editSession is not null; waited++)
            await Task.Delay(100);

        _closingForReal = true;
        Close();
    }

    /// <summary>Set once the editor has been given its chance to drain, so Close() goes through.</summary>
    private bool _closingForReal;

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

            var folder = UserDataInventory.DataFolder(report.Game.Path, descriptor);
            if (folder is null)
            {
                await ConfirmationWindow.TellAsync(this, "Could not read this game's translation",
                    UserDataInventory.OutsideGameRefusal);
                return;
            }

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

            // 🔴 **Not a merge when there is nothing of yours to settle.** With nothing kept and
            // nothing in conflict, this is taking the published version — and calling it a merge
            // asked somebody to arbitrate a disagreement that does not exist. A plain player who
            // downloaded a community translation and never touched it is the ordinary case, and
            // the word made an ordinary update read as something that could cost them work.
            //
            // ⚠ The scope mark says where it writes, and it says the thing the reader asked out
            // loud: nothing reaches the site. It is on the button that opened this window and was
            // missing from the window that commits the act.
            var takingTheirs = merge.Summary.NothingOfYoursAtStake;

            if (!await ConfirmationWindow.AskAsync(this,
                    takingTheirs ? "Update from the published version?" : "Merge with the published version?",
                    summary + "\n\nYour current file is kept aside before anything is written.",
                    takingTheirs ? "Update" : "Merge",
                    EditScope.SideAfter(onThisMachine: true, yourPublishedCopy: false)))
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
                // ⚠ Names the place somebody can act from, not a folder on disk: Backups is a
                // button they have already seen, and it now holds the only copy taken.
                + (result.KeptPrevious ? "\n\nWhat was here is kept under Backups." : ""));

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
            + (result.KeptPrevious ? "\n\nWhat was here is kept under Backups." : ""));

        await RereadAsync(report.Game);
    }

    /// <summary>Lines, counted so no language has to decode a stray s.</summary>
    private static string Lines(int count) =>
        count == 1 ? "1 line" : $"{Composition.Amount(count)} lines";

    /// <summary>
    /// What this would do to the file in the game, in figures somebody can judge before agreeing.
    ///
    /// 🔴 **Every line here is about THIS file, and nothing else belongs.** It used to end with
    /// "N removed on both sides" — a phrase describing keys that no longer exist anywhere, printed
    /// for a count that also included real deletions from the file in front of the reader. On the
    /// case that prompted this, thirteen of somebody's lines were deleted under a sentence saying
    /// they were already gone from both sides.
    ///
    /// ⚠ Removals lead when there are any. Taking a line and losing one are not the same news, and
    /// the one that loses work is the one somebody must not have to hunt for in a list.
    ///
    /// 🔴 **Four words each, and no subordinate clause.** The replacement first read "13 line(s)
    /// removed from this file — the published version dropped them and nothing here had changed
    /// them", which is a paragraph in somebody's fourth language for a fact they can act on in
    /// three words. Why the publisher dropped them changes nothing about the decision: the decision
    /// is whether losing thirteen lines is acceptable.
    /// </summary>
    private static string Describe(MergeSummary summary, bool blind)
    {
        var parts = new List<string>();

        // ⚠ "from this game", not "here": the window is about one game, and a deictic is the thing
        // that stops meaning anything the moment somebody reads it out of order.
        if (summary.RemovedHere > 0)
            parts.Add($"{Lines(summary.RemovedHere)} deleted from this game");

        if (summary.TakenFromServer > 0)
            parts.Add($"{Lines(summary.TakenFromServer)} taken from the published version");
        if (summary.KeptHere > 0) parts.Add($"{Lines(summary.KeptHere)} of yours kept");
        if (summary.Conflicts > 0) parts.Add($"{Lines(summary.Conflicts)} in conflict");

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

        var folder = UserDataInventory.DataFolder(report.Game.Path, descriptor);
        if (folder is null)
        {
            await ConfirmationWindow.TellAsync(this, "The file could not be read",
                UserDataInventory.OutsideGameRefusal);
            return;
        }

        var path = Path.Combine(folder, LocalTranslationProbe.TranslationFileName);

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
            // ⚠ **Sent to the mod, not to the website.** Forking is something the mod does — it is
            // where the file lives and where the button is — and this tool has no such action of
            // its own. Four messages here used to point at the site, which is a place a fork can
            // also be made from but not the one anybody reaches for.
            await ConfirmationWindow.TellAsync(this, "This translation is solo work",
                $"{People.MentionOf(lineage.MainOwner, standing.SignedInAs)} works alone on this one and does not take "
                + "contributions.\n\nYour lines are safe. Open the game and use Fork in the mod to "
                + "publish your own version of it.");
            return;
        }

        // A branch whose Main has closed since: the same wall, reached from the other side.
        if (lineage.BranchFrozen)
        {
            await ConfirmationWindow.TellAsync(this, "This contribution is frozen",
                "The translation you contribute to no longer accepts contributions, so this can "
                + "no longer be sent.\n\nYour lines are safe. Open the game and use Fork in the mod "
                + "to carry on with them.");
            return;
        }

        // 🔴 **The two remaining ways this lineage ends, refused here rather than by the server.**
        // Both were shown as a note on the card and neither stopped the send: somebody read the
        // warning, worked anyway, pressed publish and watched an upload run to be told no. The
        // note is worth nothing if the door it describes stays open.
        //
        // ⚠ Two messages, one wall. Which of them applies decides whether the translation they
        // were building on is still published — the first thing anybody asks next.
        if (lineage.MainMissing == true)
        {
            await ConfirmationWindow.TellAsync(this, "There is nothing left to contribute to",
                "The translation this contributes to has been removed by its author.\n\nYour lines "
                + "are safe, and your copy is now the only one. Open the game and use Fork in the "
                + "mod to publish it as your own version.");
            return;
        }

        if (lineage.MainAbandoned == true)
        {
            await ConfirmationWindow.TellAsync(this, "Nobody can review this contribution",
                "The account that owned the translation you contribute to has been deleted, so no "
                + "contribution will ever be read.\n\nThe translation itself is still published and "
                + "still works. Your lines are safe: open the game and use Fork in the mod to "
                + "publish them as your own version.");
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
                + "no longer be sent or described.\n\nYour lines are safe. Open the game and use "
                + "Fork in the mod to carry on with them.");
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

    /// <summary>
    /// The door to this translation's own history — one button, built once.
    ///
    /// ⚠ Two callers on purpose: the workbench, and the state where there is no translation at all.
    /// The second is where somebody needs it most, and it is the one that used to be missing.
    /// </summary>
    private Button BackupsButton(GameReport report, LoaderDescriptor descriptor,
                                 ServerStanding standing,
                                 IReadOnlyList<BackupEntry>? known = null)
    {
        var kept = known ?? TranslationBackupStore.List(report.Game.Path, descriptor);

        var back = ScopeMark.Marked(EditSide.Local, "Backups…", standing.CanWriteLocally);

        ToolTip.SetTip(back, kept.Count == 0
            ? "Back this translation up before you try something, and come back to it."
            : $"{Backups.SavedCount(kept)} of your own, "
              + $"{kept.Count - Backups.SavedCount(kept)} taken automatically.");

        back.Click += async (_, _) => await ShowBackupsAsync(report, descriptor);
        return back;
    }

    private IEnumerable<Control> TranslationWorkbench(GameReport report, bool heading = true)
    {
        var loaderId = report.InstalledLoader?.Id ?? report.RecommendedLoader?.Id;
        var descriptor = _catalog.Loaders.FirstOrDefault(l => l.Id == loaderId);
        if (descriptor is null) yield break;

        var standing = ServerIdentity.For(_settings.Current, report.SiteAccount, BuildInfo.ApiBaseUrl);

        // 🔴 **The way back survives having nothing to work on.** Everything below acts on a
        // translation, so with none there is nothing to show — except the copies, which is exactly
        // the state in which somebody needs them: removing the translation is what took the last
        // one. The whole block used to end here, so the backups taken by that very act became
        // unreachable, and the only route left was a file manager.
        //
        // ⚠ Mirror of the reason written over the Backups button below: the way IN must be visible
        // before the risk, and the way BACK after it.
        if (report.LocalTranslation is null)
        {
            var left = TranslationBackupStore.List(report.Game.Path, descriptor);
            if (left.Count == 0) yield break;

            yield return new TextBlock
            {
                Text = "This game holds no translation. "
                       + (left.Count == 1 ? "One copy is kept." : $"{left.Count} copies are kept."),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 12, 0, 0),
                Foreground = Brush("TextSecondary"),
            };

            yield return BackupsButton(report, descriptor, standing, left);
            yield break;
        }

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

        // 🔴 **Why nothing here can be used — FIRST, before anything else on this card.**
        //
        // It used to sit under the buttons, and the eye never reached it: the card opened on
        // "Update available", then a sentence about the published version having moved, then a row
        // of greyed buttons. Reported by somebody who stopped there, having understood neither the
        // message nor the greying, and never scrolled to the line that explained both.
        //
        // ⚠ The refusal governs everything below it, so it is read before them. Same reasoning as
        // the two lines it replaced in order: a reason printed after its consequences is a reason
        // nobody reads.
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
        if (descriptor is not null) actions.Children.Add(BackupsButton(report, descriptor, standing));

        yield return actions;

        // ⚠ **The account refusals moved to the TOP of this card** — they govern every control
        // below, and printed here they were read after their own consequences. What stays is the
        // one explanation that belongs beside the buttons: nothing to send, or nothing yet to send
        // it from.
        //
        // ⚠ **The guard is spelled out because the `else` that carried it is gone.** This used to
        // be the third arm of an if/else chain, so it could only run when neither account refusal
        // did. Dropping the condition would stack two reasons on one card and leave somebody
        // fixing the second while the first still stands.
        if (standing.CanWriteLocally
            && standing.Reason is null
            && (nothingYet ?? nothingToSend) is { } why)
        {
            // ⚠ The empty file comes FIRST: it governs two buttons where the sync reason governs
            // one, and a game with no line cannot be in any sync state worth explaining.
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

        // 🔴 **Which run owns the list.** Two loads could overlap and BOTH filled it: the list was
        // emptied before the await and filled after, so `Clear · Clear · +4 · +4` left every build
        // twice, one block after the other. Reported on a BepInEx 5 card, 8 entries for 4 builds.
        //
        // ⚠ **A "already running, go away" guard would be wrong here.** Changing the loader while a
        // request is in flight has to REPLACE it — refusing the second load would leave BepInEx's
        // builds on screen under MelonLoader's name, which is worse than a duplicate. So the last
        // caller wins: it takes the number, and any older run drops its answer on the way back.
        //
        // ⚠ The list is cleared AFTER the await, not before. Emptying first is what let two runs
        // stack, and it also blanked the list for the length of a network call for nothing.
        var generation = 0;

        // 🔴 **Which loader is already being asked about — so we ask ONCE per gesture.**
        //
        // Two triggers can fire for a single opening, and each one is a request to a publisher.
        // GitHub allows sixty an hour per address, unauthenticated: sending two where one answers
        // spends somebody's quota to draw the same four lines twice. The list-level fix below
        // (generation) stops the DUPLICATE; this stops the second REQUEST, which is the part that
        // costs something outside this window.
        //
        // ⚠ Same loader only. A different one has to replace what is on screen — see the note on
        // generation — so it goes through, and the older answer is dropped on its way back.
        LoaderDescriptor? asking = null;

        async Task LoadAsync()
        {
            if (_chosenLoader() is not { } loader) return;

            if (asking is not null
                && string.Equals(asking.Id, loader.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var mine = ++generation;
            asking = loader;

            builds.IsEnabled = false;
            note.Text = $"Asking what {loader.Display} currently offers...";

            var channel = loader.Id.StartsWith("bepinex6", StringComparison.OrdinalIgnoreCase)
                ? _settings.Current.BepInEx6Channel
                : null;

            IReadOnlyList<LoaderBuild> found;
            try
            {
                found = await new LoaderBuildResolver()
                    .ResolveAsync(loader, channel, count: 5).ConfigureAwait(true);
            }
            finally
            {
                // 🔴 **In a finally, or a throw locks this loader out for good.** The flag is what
                // stops a second request; released only on the way through, an exception would
                // leave it set and every later attempt would short-circuit on a load that is not
                // running. The expander would then never fill again, with nothing on screen saying
                // why. ResolveAsync catches its own failures today — this must not depend on that.
                //
                // ⚠ Only the run that still owns the generation clears it. A superseded run must
                // not, or the newer one would be let through a second time.
                if (mine == generation) asking = null;
            }

            // Somebody asked again while this was in flight — their answer is the one to show.
            if (mine != generation) return;

            builds.Items.Clear();

            foreach (var build in found)
            {
                builds.Items.Add(new ComboBoxItem { Content = build.Describe(), Tag = build });
            }

            builds.SelectedIndex = 0;
            builds.IsEnabled = found.Count > 1;

            // ⚠ **`loaded` only when the answer came from the publisher.** A pinned fallback means
            // the source could not be reached; marking that as loaded would freeze the catalogue's
            // entry in place for the life of the card, and reopening the expander would keep
            // showing it long after the network came back.
            note.Text = found[0].IsPinnedFallback
                ? $"Could not reach the place {loader.Display} is published, so only the build "
                  + "recorded in the catalog is available. It may be far behind."
                : $"From {found[0].SourceLabel}. The newest is used unless another is picked in this list.";

            loaded = !found[0].IsPinnedFallback;
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
            // ⚠ Removing is refused on somebody else's game, and the loader is where that bites
            // hardest: the receipt naming what we installed sits in the GAME folder, shared by the
            // whole computer, so this button was perfectly willing to take away what another
            // account had put there.
            var remove = new Button { Content = "Uninstall..." };
            remove.IsEnabled = !running && MaySetUp(report, remove);

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

            panel.Children.Add(Remembering(new Expander
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
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            }, "foreign-loader"));
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
    /// 🔴 **May this window change how this game is SET UP — its configuration, its in-game key,
    /// what is installed in it?**
    ///
    /// The account rule, asked about the other half of the game folder. It already guarded the
    /// translation; everything else was open, and the ground is the same: a game's config.json, its
    /// loader and its plugin are ONE set of files for the whole computer, and the receipt of what
    /// was installed lives in the game folder rather than here — so the person who set it up is not
    /// necessarily the person in front of this window. Each Windows account keeps its own Manager
    /// choices in its own folder; none of them owns the game.
    ///
    /// ⚠ **Installing and updating are deliberately NOT refused by this.** Putting our own software
    /// in place, or a newer build of it, takes nothing away from anybody — and leaving a shared
    /// machine unable to update the mod because the wrong person is signed in would be a refusal
    /// nobody benefits from. What such an install must not do is WRITE SETTINGS, which is a
    /// different switch: <see cref="BuildPlan"/>'s <c>settings</c> parameter, already used by the
    /// loader button for exactly this reason.
    ///
    /// ⚠ It also carries the refusal to the control, because a greyed button with no words is the
    /// thing this program refuses everywhere else.
    /// </summary>
    private bool MaySetUp(GameReport report, Control? explain = null)
    {
        var standing = ServerIdentity.For(_settings.Current, report.SiteAccount, BuildInfo.ApiBaseUrl);
        if (standing.CanWriteLocally) return true;

        if (explain is not null && standing.SetupRefusal is { } why) ToolTip.SetTip(explain, why);
        return false;
    }

    /// <summary>
    /// The same answer as <see cref="MaySetUp"/>, for the one caller that cannot be handed a
    /// control: the settings form owns its own Apply and is told why rather than shown a button.
    /// </summary>
    private string? SetupRefusal(GameReport report) =>
        ServerIdentity.For(_settings.Current, report.SiteAccount, BuildInfo.ApiBaseUrl) is
            { CanWriteLocally: false } standing
            ? standing.SetupRefusal
            : null;

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
            var uninstall = new Button { Content = "Uninstall..." };
            uninstall.IsEnabled = !running && MaySetUp(report, uninstall);
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
                                           enabled: false);

            ToolTip.SetTip(putBack,
                $"{missing.Count} file(s) this game had before UnityGameTranslator Manager "
                + "replaced them are missing — its previous mod loader, most often. This writes "
                + "them back. Nothing is deleted: anything already in place is left alone.");

            // ⚠ Set AFTER the tooltip above, so a refusal replaces the explanation rather than
            // being replaced by it. Writing another account's mod loader back into their game is
            // changing their setup as surely as removing ours would.
            putBack.IsEnabled = !running && MaySetUp(report, putBack);

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
        // ⚠ With the pending choice laid over it, and on a copy — see PreferenceWithPending. The
        // handlers below mutate this object and hand the result to _pendingWay; nothing here
        // reaches the file.
        var preference = PreferenceWithPending(report.Game.Path);

        yield return new Border
        {
            Height = 1,
            Background = Brush("BorderSubtle"),
            Margin = new Avalonia.Thickness(0, 10, 0, 4),
        };

        // 🔴 **It names what it is.** The heading read "In this game" — which names nothing: the
        // whole card is about this game. Somebody meeting this screen for the first time had to
        // work out from the controls what the section was even for, and the one thing they came to
        // do is set the mod up.
        yield return new TextBlock
        {
            Text = "Mod settings",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextSecondary"),
        };

        yield return new TextBlock
        {
            Text = "What the mod uses in this game.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
            Foreground = Brush("TextMuted"),
        };

        // 🔴 **The whole section is read-only on a game set up under another account**, and it says
        // so once, here, above everything it governs — the three ways, the differences with Mod
        // defaults, the key, and the form. Each control below is greyed as well: a section whose
        // radios still move and whose boxes still tick, above an Apply that refuses, is the dead
        // end this program refuses everywhere.
        //
        // ⚠ Said in this card rather than borrowed from the translation card above. They are two
        // cards answering two questions, and a reader looking at the settings should not have to
        // have read the other one.
        var mayChange = MaySetUp(report);

        if (!mayChange && SetupRefusal(report) is { } refusal)
        {
            yield return new TextBlock
            {
                Text = refusal,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
                Foreground = Brush("StatusWarning"),
            };
        }

        // ⚠ Only the parts that depend on these controls are rebuilt, never the whole card.
        //
        // Redrawing the section from inside one of its own checkboxes destroys that checkbox while
        // its event is still running, and takes the keyboard focus with it — the box is left
        // looking pressed and the next Space goes nowhere. Three things react to a change here: the
        // list of differences, the form of this game's own settings, and the band at the bottom
        // that says what one click would do.
        // 🔴 **Indented under the way they belong to.** With one tickbox, a block underneath was
        // unmistakably its consequence. With three radios, the same block sitting under the group
        // belongs to none of them in particular — and the question "what is this attached to?" has
        // no answer on the screen. Each goes under its own line, set in.
        var driftHost = new StackPanel
        {
            Spacing = 4,
            Margin = new Avalonia.Thickness(24, 2, 0, 6),
        };

        var ownHost = new StackPanel
        {
            Spacing = 4,
            Margin = new Avalonia.Thickness(24, 2, 0, 0),
        };
        var hotkeyHost = new StackPanel { Spacing = 4 };

        void Refresh()
        {
            driftHost.Children.Clear();
            foreach (var control in ConfigDrift(report, preference))
                driftHost.Children.Add(control);

            // ⚠ Present ONLY under "Settings of its own", because that is the only way in which it
            // means anything. Under Mod defaults every field is answered by that screen; under
            // "Let the mod ask" they are answered in the game. A form full of values nobody may
            // change here would be an invitation with no door behind it.
            //
            // ⚠ Keyed on the chosen way and no longer on "the box is unticked": unticked now covers
            // TWO ways, and only one of them owns this form.
            var snapshotHere = GameConfig(report);

            ownHost.Children.Clear();
            ownHost.IsVisible = SetupWayOf(preference, snapshotHere, _settings.Current.Reviewed,
                                           firstTime: !snapshotHere.FirstRunCompleted)
                                == SetupWay.Custom;

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

        // 🔴 **Three ways to set a game up, and they are exclusive — so they are radios.**
        //
        // They were two independent boxes ("Use Mod defaults in this game", plus a wizard tick), a
        // shape that can express states meaning nothing while hiding the one question somebody
        // actually has on a fresh install: where do this game's settings come from. Three answers,
        // one of which is always true:
        //
        // · **Mod defaults** — the values from that screen. Unavailable until it has been filled
        //   in, and greyed rather than absent: it is the ordinary answer, so its absence needs a
        //   reason and the reason is one hover away;
        // · **Let the mod ask** — nothing is decided here and the mod runs its own setup in the
        //   game. The honest default on a machine nobody has configured, and the reason a first
        //   launch is never a dead end;
        // · **Settings of its own** — the form below.
        //
        // ⚠ Stored in the two fields that already existed rather than a third: ApplyModDefaults
        // says whether the defaults are taken, LetWizardAsk whether the latch stays open. A third
        // field would be a second truth about one decision.
        var snapshotNow = GameConfig(report);
        var reviewedNow = _settings.Current.Reviewed;
        var firstTime = !snapshotNow.FirstRunCompleted;

        var source = new StackPanel { Spacing = 2 };

        // 🔴 **Three words on the label, the consequence underneath, and neither is a hover.**
        // "Settings of its own" is not English anybody reads at speed, and a reason parked in a
        // tooltip is a reason nobody meets — least of all somebody deciding whether this program is
        // worth another thirty seconds. Every line here is what the mod DOES, said the way the
        // three products say things: plain, short, and in the fourth language of most readers.
        RadioButton Way(string label, string says, bool chosen, bool enabled, Action pick)
        {
            // Where this game's settings come from is a decision about this game's config.json, so
            // it is refused on somebody else's game like everything else in this section — and the
            // label greys with the control rather than staying lit above a dead radio.
            enabled = enabled && mayChange;

            var text = new StackPanel { Spacing = 1 };

            text.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = Brush(enabled ? "TextPrimary" : "TextMuted"),
            });

            text.Children.Add(new TextBlock
            {
                Text = says,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            });

            var button = new RadioButton
            {
                Content = text,
                GroupName = "setup-" + report.Game.Path,
                IsChecked = chosen,
                IsEnabled = enabled,
                FontSize = 12,
            };

            if (!mayChange) MaySetUp(report, button);

            // ⚠ Held for the session, never written here: choosing a way decides nothing until
            // something acts on it. It is applied to the preference in memory too, so everything
            // the card asks next — the differences, the form, the bar — answers from the choice
            // just made rather than from the one on disk.
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked != true) return;

                pick();
                _pendingWay[report.Game.Path] = (preference.ApplyModDefaults,
                                                 preference.LetWizardAsk);
                Refresh();
            };

            return button;
        }

        var chosenWay = SetupWayOf(preference, snapshotNow, reviewedNow, firstTime);

        source.Children.Add(Way(
            "Use Mod defaults",
            reviewedNow
                ? "The same settings as your other games."
                : "Mod defaults has not been filled in yet.",
            chosenWay == SetupWay.ModDefaults,
            reviewedNow,
            () => { preference.ApplyModDefaults = true; preference.LetWizardAsk = false; }));

        // ⚠ Under this one whichever way is chosen, and that is deliberate: what this game holds
        // against what that way would write is precisely what somebody needs in order to pick it.
        // Hidden until chosen, the choice would be made blind.
        source.Children.Add(driftHost);

        // ⚠ Only while the mod has never finished its own setup here. Afterwards the latch is
        // closed and no tick reopens it — the button below does, by name.
        if (firstTime)
        {
            source.Children.Add(Way(
                "Set it up in the game",
                "The mod shows its Setup when the game starts.",
                chosenWay == SetupWay.Wizard,
                enabled: true,
                () => { preference.ApplyModDefaults = false; preference.LetWizardAsk = true; }));
        }

        source.Children.Add(Way(
            "Set it up here",
            "Choose the settings below.",
            chosenWay == SetupWay.Custom,
            enabled: true,
            () => { preference.ApplyModDefaults = false; preference.LetWizardAsk = false; }));

        source.Children.Add(ownHost);

        yield return source;

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

        // 🔴 **After the whole chain, on ONE row, because they are ACTS and not settings.**
        //
        // These two sat between the radios and the blocks the radios govern — the differences with
        // Mod defaults, and this game's own form — cutting a chain that reads as one thing: the way
        // chosen, then what that way changes. It is the second time in a day; the rule is written
        // in .claude/rules/name-things-in-ui.md and the fix is to read the neighbour before adding.
        //
        // ⚠ And side by side, not one per line. Two buttons stacked take two lines to say what a
        // row says in one, and the vertical stack made them read as two more options in the list
        // above rather than as two things one can do.
        // ⚠ **To the right, away from Install and Uninstall.** Those sit left-aligned a little
        // above, and a third pair on the same edge reads as more of the same row — one of them
        // being "Uninstall...", which is not a neighbour to be mistaken for. Right is also where
        // this product already puts the act that closes a block, every Apply included.
        var acts = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };

        // 🔴 **The way back into the mod's own setup, once the latch is closed.** A key the two
        // programs read differently, a translator that turns out not to work, a game somebody
        // wants to start over: the wizard answers all three and there was no way to ask for it.
        if (!firstTime && snapshotNow.Exists)
        {
            // 🔴 **It names the screen and where the screen is.** "Run the mod's setup again" left
            // both to be guessed: what setup, and whose — this window has settings of its own and a
            // reader has no way to tell that the thing being reopened belongs to the other program.
            // The mod titles that screen "Unity Game Translator - Setup", so it is called Setup,
            // and it happens in the game. Both facts are on the button.
            var again = new Button
            {
                Content = "Show Setup in the game",
                FontSize = 12,
            };

            ToolTip.SetTip(again,
                $"The mod opens its Setup screen the next time {report.Game.Name} starts, and asks "
                + "its own questions there. Nothing is changed now.");

            // ⚠ It edits this game's configuration — one key, but a shared file, and the person who
            // meets that wizard next is whoever launches the game, not whoever pressed this.
            again.IsEnabled = MaySetUp(report, again);

            again.Click += async (_, _) =>
            {
                if (InstalledDescriptor(report) is not { } loader) return;

                // 🔴 **Asked, because it edits this game's configuration.** It is one key and it
                // writes nothing else, but somebody pressing it is entitled to know that a file is
                // being changed and which one — the rule every act on this card follows.
                var go = await ConfirmAsync(
                    "Show Setup in the game?",
                    $"The mod opens its Setup screen the next time {report.Game.Name} starts, and "
                    + "what you answer there is written into the game.\n\nThe only change made "
                    + "now is to this game's configuration, which stops saying it has already been "
                    + "set up. Its settings, its translation and its key stay exactly as they are.",
                    "Show Setup");

                if (!go) return;

                // ⚠ Null REMOVES the key, which is how the mod reads "never answered". Writing
                // false would be a claim of its own; removing puts the game back where it started.
                var done = new GameConfigWriter().ApplyOne(
                    report.Game.Path, loader, "first_run_completed", null, "first-run setup");

                await MessageAsync(
                    done.Written ? "It will ask again" : "Nothing was changed",
                    done.Written
                        ? $"{report.Game.Name} opens the mod's Setup the next time it starts."
                        : $"The game's configuration could not be written ({done.Failure}).");

                await ShowSelectedAsync();
            };

            acts.Children.Add(again);
        }

        // 🔴 **The other half of the circle: a game already set up can SEED Mod defaults.** Without
        // it, somebody who configured a game inside the mod and never filled the defaults in had to
        // type the same answers a second time to get anywhere on their other games — with the
        // values sitting right there on the screen.
        if (!reviewedNow && snapshotNow.IsConfigured)
        {
            var seed = new Button
            {
                Content = "Use as Mod defaults",
                FontSize = 12,
                Classes = { "primary" },
            };

            ToolTip.SetTip(seed,
                $"Fills Mod defaults in with what {report.Game.Name} already holds, so the other "
                + "games can be set up from it. Nothing in this game changes.");

            seed.Click += async (_, _) =>
            {
                var seeded = ModSettingsResolver.Resolve(
                    _settings.Current, new GamePreference(), snapshotNow);

                seeded.Reviewed = true;
                _settings.Save(seeded);

                await MessageAsync("Mod defaults filled in",
                    $"Mod defaults now holds what {report.Game.Name} was set up with. Open it to "
                    + "check it over — nothing in this game was changed.");

                SyncLanguageBox();
                await RepublishAsync();
            };

            acts.Children.Add(seed);
        }


        if (acts.Children.Count > 0) yield return acts;

        // The first fill, which also settles whether the form above starts out on screen.
        Refresh();
    }

    /// <summary>Where one game's settings come from. Exactly one is true at a time.</summary>
    private enum SetupWay
    {
        /// <summary>The values from the Mod defaults screen.</summary>
        ModDefaults,

        /// <summary>Nothing decided here; the mod runs its own setup inside the game.</summary>
        Wizard,

        /// <summary>The answers in this game's own form.</summary>
        Custom,
    }

    /// <summary>
    /// Which of the three is in force, resolved the way the rest of the card resolves things.
    ///
    /// ⚠ **Undecided is not a fourth state, it is a reading of the game.** A game already carrying a
    /// configuration keeps its own — the rule UsesModDefaults has always held, and the reason the
    /// first one-click cannot quietly overwrite a set-up somebody made inside the mod. A game with
    /// nothing yet follows the defaults when there are any, and asks in the game when there are not.
    ///
    /// ⚠ And once the mod has finished its own setup, Wizard is not on offer: the latch is closed
    /// and no tick here reopens it. A stored answer saying otherwise reads as Custom, which is what
    /// it amounts to — this game answers for itself.
    /// </summary>
    private static SetupWay SetupWayOf(GamePreference preference, GameConfigSnapshot snapshot,
                                       bool reviewed, bool firstTime)
    {
        if (preference.ApplyModDefaults == true && reviewed) return SetupWay.ModDefaults;

        // 🔴 **`false` is a decision and is taken at its word.** It is never a default — nobody
        // reaches it without choosing it, which is exactly why the schema-2 migration undoes a
        // stored `true` and leaves `false` alone.
        //
        // ⚠ I second-guessed it here for one commit, on the theory that an old `false` should not
        // greet somebody with an empty form. The theory cost the form: choosing "Set it up here"
        // writes false/false, this read it back as stale, and the selection sprang back to the
        // wizard — so the one way to open the settings of a game could never be taken. A stored
        // answer that the reader can change with one click needs no rescuing.
        if (preference.ApplyModDefaults == false)
            return preference.LetWizardAsk && firstTime ? SetupWay.Wizard : SetupWay.Custom;

        // Nobody has decided for this game.
        if (snapshot.IsConfigured) return SetupWay.Custom;

        return reviewed ? SetupWay.ModDefaults : SetupWay.Wizard;
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

        // ⚠ What is pending wins over what is stored: it is the newer answer, and it is why the
        // form survives this card being redrawn under somebody who is still filling it in.
        var stored = _pendingMod.TryGetValue(report.Game.Path, out var held) ? held : preference.Mod;

        var form = new GameModSettingsForm(_platform, _settings.Current, snapshot, stored,
                                           pinned.Language, pinned.Published,
                                           installed: snapshot.IsConfigured,
                                           refusal: SetupRefusal(report));

        // 🔴 **Stored as they are typed, and only where nothing else would store them.** Before the
        // mod is installed there is no Apply — see the note on the form's `installed` — so this is
        // what keeps the answers. Without it they lived in a form object that the next redraw of
        // this card threw away without a word, which is how somebody sets a language, presses the
        // one-click, and gets a game in a language they never chose.
        //
        // ⚠ No refresh() here. The person is still in the form; rebuilding the card would destroy
        // the control under their cursor mid-edit.
        //
        // ⚠ **Held, not saved.** Nothing here has been validated: writing it to disk would have the
        // program come back tomorrow with answers somebody typed and walked away from.
        form.Recorded += () =>
        {
            _pendingMod[report.Game.Path] = form.Draft.Copy();

            // ⚠ The BAR only, never refresh(). The bar lives in its own container and its steps are
            // computed from what is pending, so it can be redrawn while somebody is still typing;
            // rebuilding the card would destroy the control under their cursor mid-edit. Without
            // this the answer was held and nothing on screen said so until the next redraw.
            ShowActionBar(report);
        };

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
            SaveAnswer(report.Game.Path, p => p.Mod = preference.Mod?.Copy());

            await ApplyOwnSettingsAsync(report, preference);

            // The differences block and the band below both describe what would be written, which
            // is exactly what has just changed.
            refresh();
        };

        form.OpenDefaults += async () => await OpenSettingsAsync();

        var answered = preference.Mod?.Count ?? 0;

        yield return Remembering(new Expander
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
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        }, "own-settings");
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
            TargetFor(report, descriptor, settings),
            skipWizard: !LetsWizardAsk(report, preference), perGame: preference);

        Busy(false, "Ready.");

        if (!result.Written)
        {
            await MessageAsync("Nothing was changed",
                $"This game's settings could not be written ({result.Failure}).");
            return;
        }

        // They are in the file now, so the file answers for them from here on.
        ForgetWrittenAnswers(report);
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
        // ⚠ Said rather than left blank. With no Mod defaults there is nothing to compare against,
        // and an empty space reads as a game with nothing to settle — which is the opposite of what
        // it means. One line, and it names what is missing.
        if (!_settings.Current.Reviewed)
        {
            yield return new TextBlock
            {
                Text = "Nothing to compare with yet: Mod defaults has not been filled in.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            };

            yield break;
        }

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
                Tone.Info);
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
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            };

            // ⚠ Mod defaults are this Windows account's answers; the game's configuration is shared
            // by the whole computer. Writing one into the other on somebody else's game is the
            // plainest form of the thing this rule exists to stop.
            apply.IsEnabled = !_running.IsRunning(report.Game) && MaySetUp(report, apply);

            apply.Click += async (_, _) => await ApplyDefaultsAsync(report, descriptor, preference);
            body.Children.Add(apply);
        }

        // 🔴 **Unticked is the CAUTIOUS case, and the colours said the opposite.** Ticked means
        // "set this game up from Mod defaults" — applying them is the thing that was asked for, so
        // it is ordinary. Unticked means "do not use Mod defaults here": pushing them in anyway is
        // the act to think twice about, and it is the one that was painted as routine.
        var tone = ticked
            ? Tone.Info        // asked for: Mod defaults belong here
            : Tone.Warning;    // refused: applying goes against the box

        var notice = Callout(body, tone);
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
        // 🔴 **No guard on Mod defaults here, and there was one.** This block does two things: it
        // offers to take the key from Mod defaults, and it lets somebody set a key for THIS game.
        // Only the first needs Mod defaults — a replacement needs a second key to offer. Setting one
        // needs nothing at all, and the `yield break` at the top of this method took both away: on a
        // machine whose Mod defaults had never been filled in there was NO WAY ANYWHERE to choose a
        // hotkey, on the first run, which is exactly when somebody wants to.
        //
        // The two halves are guarded separately below.
        var reviewed = _settings.Current.Reviewed;

        // 🔴 **Offered with nothing installed too, and this guard was the last thing hiding it.**
        // "Nothing installed to write into" is true and it is not a reason to take the question
        // away: choosing a shortcut is precisely what somebody does while setting a game up, and
        // the answer has somewhere to go — the same session-held answers the settings form uses
        // before there is a file, laid down by the install.
        var descriptor = InstalledDescriptor(report);
        var installed = descriptor is not null;

        var inGame = installed ? GameConfig(report).InGameHotkey : null;

        // ⚠ Read from the same comparison that feeds the block above — one source, so the two can
        // never disagree about this key. Null means there is nothing to REPORT: the game already
        // agrees, or the key that would be written is one that cannot travel between games. The
        // capture below is offered either way; being settled is not a reason to take the control
        // away.
        // ⚠ Null without Mod defaults, rather than a comparison against a key nobody chose: the
        // second term would be the program's own guess, and reporting "kept — Mod defaults uses X"
        // about a guess states a decision that was never taken.
        var difference = reviewed
            ? Differences(report, preference)
                .FirstOrDefault(d => d.Key == GameConfigWriter.HotkeyKey)
            : null;

        // ⚠ The box only where there is something to DECIDE. A game with no key of its own has
        // nothing to protect: the key is written outright, and a box asking permission to replace
        // a key that does not exist would be a question about nothing.
        if (inGame is not null && reviewed)
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

            // What it decides is written into this game's config.json by the next install, so it is
            // refused on somebody else's game like the rest of this section.
            replace.IsEnabled = MaySetUp(report, replace);

            replace.IsCheckedChanged += (_, _) =>
            {
                preference.ReplaceHotkey = replace.IsChecked == true;
                SaveAnswer(report.Game.Path, p => p.ReplaceHotkey = preference.ReplaceHotkey);

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
                ? Callout(state, Tone.Warning)
                : Callout(state, Tone.Info);

            ((Border)reported).Margin = new Avalonia.Thickness(0, 6, 0, 0);
            yield return reported;
        }

        // 🔴 **The same capture as Mod defaults, for THIS game.** The brick would be incomplete
        // without it: one could see both keys and take the other one, but not choose a third — and
        // a key is precisely the setting most likely to need to differ from one game to the next.
        // The control is the shared HotkeyEditor, so the refusals it enforces (a key Unity cannot
        // name, a key that means something else in another game) are the same ones Mod defaults
        // enforces, in the same words.
        // ⚠ **And `reviewed`, which is not padding.** This locks the editor below and its tooltip
        // says "untick the box above" — with Mod defaults unfilled there IS no box above, so a
        // ReplaceHotkey left true from an earlier setup would leave the capture disabled pointing at
        // a control that is not on the screen. A condition split in one place has to be followed
        // into everything that read it.
        var takesDefault = reviewed && preference.ReplaceHotkey && inGame is not null;

        // 🔴 **It shows what THIS GAME uses — the field says so, and every other field on this card
        // is filled the same way.** It was seeded from Mod defaults for a while, so a field titled
        // "Key for this game" displayed a key the game does not use, on the one screen whose whole
        // promise is to show what the game holds.
        //
        // ⚠ warnOnArrival: false is what made that honest. A game's key is very often a character
        // key — captured in the game, against the keyboard as that game reads it — and the editor
        // used to greet the reader by declaring their own working choice unusable. It is only
        // unusable FROM HERE, which matters when choosing a new one and not before.
        // 🔴 **Held, not stored.** Every keystroke used to land in preference.Mod.SettingsHotkey and
        // be written to the preferences file straight away — so a key merely tried out was
        // remembered, counted in "N set for this game", and carried into the next install by
        // somebody who never confirmed it.
        //
        // ⚠ Seeded from the session-held answers first, so a key captured before the mod is
        // installed survives this card being redrawn under the person capturing it.
        var draftKey = (_pendingMod.TryGetValue(report.Game.Path, out var pendingKeys)
                            ? pendingKeys.SettingsHotkey : null)
                       ?? preference.Mod?.SettingsHotkey;

        var editor = new HotkeyEditor(
            draftKey ?? inGame ?? _settings.Current.SettingsHotkey,
            Brush("TextMuted"), Brush("StatusWarning"), warnOnArrival: false);

        // 🔴 **Held, not stored.** Every keystroke used to land in preference.Mod.SettingsHotkey
        // and be written to the preferences file straight away — so a key merely tried out was
        // remembered, counted in "N set for this game", and carried into the next install by
        // somebody who never confirmed it. The block already had a verb; what it lacked was
        // anything to press it FOR.
        //
        // The button below is that verb, and it now does both halves: remember the key, and write
        // it into the game. Nothing before it.

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
        ToolTip.SetTip(editor.Row, takesDefault
            ? "Untick the box above to choose a key here instead."
            : "Only keys every game detects the same way can be set from here.");

        // Last, so the account refusal has the final word on both — capturing a key one may not
        // write is the same dead end as ticking a box one may not apply.
        editor.Row.IsEnabled = !takesDefault && MaySetUp(report, editor.Row);

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
                report.Game.Path, descriptor!, GameConfigWriter.HotkeyKey, chosen, "in-game hotkey");

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

            SaveAnswer(report.Game.Path, p =>
            {
                p.Mod ??= new GameModOverrides();
                p.Mod.SettingsHotkey = chosen;
            });

            await ShowSelectedAsync();
        };

        RefreshHotkeyApply();

        // 🔴 **No Apply where there is nothing to apply to** — the rule the settings form follows
        // three inches above. With no config.json the verb has no object, so the key is recorded as
        // it is captured and the install lays it down, and the line below says so instead.
        if (installed)
        {
            yield return write;
        }
        else
        {
            editor.Changed += () =>
            {
                if (editor.Value is not { } key) return;

                var held = _pendingMod.TryGetValue(report.Game.Path, out var kept)
                    ? kept
                    : new GameModOverrides();

                held.SettingsHotkey = key;
                _pendingMod[report.Game.Path] = held;
            };

            yield return new TextBlock
            {
                Text = "Written into the game when the mod is installed.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(120, 4, 0, 0),
                Foreground = Brush("TextMuted"),
            };
        }

        void RefreshHotkeyApply()
        {
            if (write is null) return;

            var pending = draftKey is { } key && !string.Equals(key, inGame, StringComparison.Ordinal);

            // ⚠ SetLabel, never Content: the button holds its scope marks beside the text.
            ScopeMark.SetLabel(write, pending ? "Apply (1)" : "Apply");

            ToolTip.SetTip(write, !pending
                ? "This game already uses that key."
                : takesDefault
                    ? "Untick the box above to choose a key here instead."
                    : _running.IsRunning(report.Game)
                        ? $"{report.Game.Name} is running, so its files are locked."
                        : "Writes this key into the game, and remembers it for a later install.");

            // Last, so it has the final word on the tooltip as well as on the state: this writes
            // into a config.json shared by every account on this computer.
            var mine = MaySetUp(report, write);
            write.IsEnabled = pending && !_running.IsRunning(report.Game) && !takesDefault && mine;
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
    /// Whether anything answered on this card is still waiting to be written.
    ///
    /// ⚠ The three held drafts, and nothing else: what has been APPLIED is in the game's file and
    /// is not "waiting" — undoing it would mean writing something else, which is a different act
    /// with a different button.
    /// </summary>
    private bool PendingAnswers(GameReport report) =>
        _pendingMod.ContainsKey(report.Game.Path)
        || _pendingPlan.ContainsKey(report.Game.Path)
        || _pendingWay.ContainsKey(report.Game.Path)
        || _pendingTranslation.ContainsKey(report.Game.Path);

    /// <summary>
    /// Drops them all, in one gesture.
    ///
    /// ⚠ Held drafts only. The stored preference is left exactly as it is: it holds what was
    /// decided earlier and applied, and a button called Undo must not reach further back than the
    /// answers it is showing.
    /// </summary>
    private void ForgetPendingAnswers(GameReport report)
    {
        _pendingMod.Remove(report.Game.Path);
        _pendingPlan.Remove(report.Game.Path);
        _pendingWay.Remove(report.Game.Path);
        _pendingTranslation.Remove(report.Game.Path);
    }

    /// <summary>
    /// Whether writing this game's configuration is part of the job.
    ///
    /// 🔴 **ONE answer, for the list that promises the step and the plan that performs it.** The
    /// rule lived twice, and the copies drifted: BuildPlan wrote a game's own answers (`hasOwn`),
    /// while the step list only ever considered Mod defaults. Somebody changed a field under "set
    /// it up here", the block's Apply lit up, and the one-click said the game was fully set up —
    /// then would have written those very answers had anything else made it appear.
    /// </summary>
    private bool WouldWriteSettings(GameReport report, GamePreference preference)
    {
        var config = GameConfig(report);

        // Answers of its own are written whatever the box says — the Reviewed guard is about not
        // deciding FOR somebody, and a game they answered themselves is not that.
        //
        // ⚠ **All of them, not just the settings form.** "Translate while I play" and "what is this
        // game about" are answered in their own block and land in their own fields; testing only
        // `Mod` left those two out, so changing them lit their own Apply and nothing else. The
        // hotkey is the same kind of answer, asked in a third block again.
        //
        // ⚠ Saying "there is material" is not saying "there is work": these fields survive being
        // written, unlike Mod. SettingsWouldChangeAnything is what compares them with the file, so
        // an answer already in place produces no step.
        if (preference.Mod is { IsEmpty: false }) return true;
        if (preference.StartTranslation is not null) return true;
        if (!string.IsNullOrWhiteSpace(preference.GameContext)) return true;
        if (preference.ReplaceHotkey) return true;

        return _settings.Current.Reviewed
               && (!config.IsConfigured || preference.UsesModDefaults(config));
    }

    /// <summary>
    /// What would change in this game if the settings were written NOW — measured against the
    /// values that would actually go in.
    ///
    /// ⚠ Deliberately not <see cref="Differences"/>, and the two must not be merged. That one
    /// answers "what would applying Mod defaults change here", always, because the block under it
    /// says exactly that and a list whose meaning moves with a checkbox cannot be read. This one
    /// answers "is there work for the button", which is a different question the moment a game
    /// stops following the defaults.
    ///
    /// ⚠ The same resolution the writer uses (<see cref="SettingsFor"/>), so what is counted here
    /// is what ApplyOwnSettingsAsync and the plan would put in the file — not an estimate of it.
    /// On a game that follows Mod defaults the resolver returns them, so the two calls agree.
    /// </summary>
    private IReadOnlyList<ConfigDifference> WrittenDifferences(GameReport report,
                                                              GamePreference preference)
    {
        var descriptor = InstalledDescriptor(report);
        if (descriptor is null) return Array.Empty<ConfigDifference>();

        var settings = SettingsFor(report, preference);

        return new GameConfigWriter().Compare(
            report.Game.Path, descriptor, settings,
            TargetFor(report, descriptor, settings), preference);
    }

    /// <summary>
    /// What happens about Mod defaults, and the two ways out — in the bar, which both tabs show.
    ///
    /// 🔴 **The bar is the only part of a card that Home and Set up have in common**, so anything
    /// about how this game will be configured has to be sayable from it. It was not: the message
    /// and its button existed only while the one-click was BLOCKED, so choosing "let the mod ask"
    /// or "set it up here" cleared the block and took the explanation with it. Somebody on Home
    /// then saw a lit button and nothing about what it would set the game up with.
    ///
    /// ⚠ Nothing at all once Mod defaults has been filled in. This is the cold start and no more.
    ///
    /// ⚠ Two ways out because there are two, and one of them is not this window: filling the
    /// defaults in serves every game, answering on Set up serves this one. Neither is the right
    /// answer for everybody, so neither is the only button.
    /// </summary>
    /// <param name="blocking">
    /// Whether the one-click is refusing. It changes the sentence and nothing else: refused, it
    /// says what is missing; allowed, it says what will happen instead.
    /// </param>
    private IEnumerable<Control> ModDefaultsWayOut(GameReport report, bool blocking)
    {
        if (_settings.Current.Reviewed) yield break;

        var preference = _preferences.Read(report.Game.Path);
        var snapshot = GameConfig(report);

        var way = SetupWayOf(preference, snapshot, reviewed: false,
                             firstTime: !snapshot.FirstRunCompleted);

        if (!blocking)
        {
            yield return new TextBlock
            {
                Text = way == SetupWay.Wizard
                    ? "Mod defaults is empty, so the mod asks in the game."
                    : "Mod defaults is empty. This game uses its own settings.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextMuted"),
            };
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        };

        // 🔴 Primary, like every other button that is the way forward. It sat flat and transparent
        // beside a greyed OneClick — the one control on the screen that could be pressed, dressed
        // as the least important thing on it.
        var open = new Button
        {
            Content = "Open Mod defaults",
            FontSize = 12,
            Classes = { "primary" },
        };

        ToolTip.SetTip(open, "Answer once, and every game can be set up from it.");
        open.Click += async (_, _) => await OpenSettingsAsync();
        buttons.Children.Add(open);

        // ⚠ Only from Home. On Set up it would scroll somebody to a block they are looking at.
        if (_gameTab == GameTab.Home)
        {
            var here = new Button { Content = "Set up this game", FontSize = 12 };

            ToolTip.SetTip(here, "Answer for this game alone, on the Set up tab.");

            here.Click += async (_, _) =>
            {
                _gameTab = GameTab.Setup;
                await ShowSelectedAsync();
            };

            buttons.Children.Add(here);
        }

        yield return buttons;
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
            // 🔴 **A way to ask again, beside the sentence saying it failed.** There was none: the
            // answer is cached, so the only ways out were re-selecting the game or restarting the
            // program — neither of which anybody guesses, and a reader who does not guess is left
            // with a refusal and nothing to do about it. Forget() exists precisely for this.
            var said = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };

            said.Children.Add(new TextBlock
            {
                Text = $"Could not check for a newer version ({failure}).",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("StatusWarning"),
            });

            var retry = new Button { Content = "Try again", FontSize = 11 };

            ToolTip.SetTip(retry, "Asks the publisher again. Nothing is installed or changed.");

            retry.Click += async (_, _) =>
            {
                // ⚠ The failure is remembered like the answer would be, so it has to be dropped
                // before asking — otherwise this button re-reads the same refusal.
                _releases.Forget();
                await ShowSelectedAsync();
            };

            said.Children.Add(retry);

            return said;
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
    /// <summary>
    /// Whether setting THIS game up would draw on Mod defaults at all.
    ///
    /// ⚠ The box, resolved the way the box resolves — untouched on an unconfigured game means yes,
    /// which is why a fresh library is still told to fill the defaults in. Unticked means no, and
    /// no is an answer: it is not a game waiting for something, it is a game that said it does not
    /// want it.
    /// </summary>
    private bool NeedsModDefaults(GameReport report) =>
        PreferenceWithPending(report.Game.Path).UsesModDefaults(GameConfig(report));

    /// <summary>
    /// Whether the mod still asks its own questions in this game after we write to it.
    ///
    /// ⚠ Two ways to reach it, and the second is not a preference: somebody may ask for it on this
    /// game, and a machine whose Mod defaults have never been filled in gets it regardless — what
    /// would be written then is the program's own guesses, and the wizard is the only thing that
    /// will ever correct them. Same rule as InstallEngine.Plan, which cannot call this.
    /// </summary>
    private bool LetsWizardAsk(GameReport report, GamePreference preference) =>
        preference.LetWizardAsk || !_settings.Current.Reviewed;

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
        // ⚠ Short, and short is the point. This read "Mod defaults has not been filled in yet, so
        // there is nothing to configure this game with." — a subordinate clause explaining a
        // consequence, in the fourth language of most of the people reading it, sitting above the
        // button that fixes it. The eye takes in what is short; what is long it skips, and skipping
        // this one leaves somebody in front of a greyed button with no way forward.
        //
        // The reason lives in the banner at the top of the card, which has the room for it.
        //
        // 🔴 **Only where the defaults would actually be used.** This refused unconditionally, so
        // unticking "Use Mod defaults in this game" — saying in as many words that this game does
        // not want them — left the OneClick greyed for a prerequisite the game had just opted out
        // of. Nothing was missing at that point: the loader and the mod go in, no setting is
        // written, and the mod's own first-run wizard asks inside the game, which is the fallback
        // this whole guard exists to protect.
        if (!_settings.Current.Reviewed && NeedsModDefaults(report))
            return "Mod defaults comes first.";

        // 🔴 **Nothing to translate with, so nothing to set up for.** Community translations are
        // the chosen source, this game has none, and no translator is named: the one click would
        // install the loader, the mod and the settings, every step would succeed, and the game
        // would run in its own language. Four green ticks and a promise nobody kept.
        //
        // ⚠ **This became a refusal the day translating by hand got a NAME.** Greying it before
        // would have closed the one path this product says needs no AI and no account — somebody
        // starting a translation themselves needs exactly this install. "Captures only" is that
        // answer, said out loud, and it reads as a translator here: choose it and the button comes
        // back.
        //
        // ⚠ Only where the defaults are actually used, like the guard above: a game keeping its
        // own configuration may name a translator this does not read.
        if (report.OnlineTranslations.Count == 0
            && NeedsModDefaults(report)
            && TranslationBackendLabel(_settings.Current) is null)
        {
            return "Nothing to translate this game with yet.";
        }

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

        // 🔴 **Two objects, and conflating them cost somebody their answers.** This method both
        // DESCRIBES what the one-click would do and WRITES one field further down
        // (`InstallTranslation`, saved on the spot). Reading a copy for the description and then
        // saving that copy put a snapshot taken before the person's last change back over the
        // stored preference: ticking "set it up here" was undone by the next redraw, silently.
        //
        // So: the live object to write, a copy to describe. They are not interchangeable, and the
        // one that goes to Set must be the one Read handed back.
        var preference = _preferences.Read(report.Game.Path);

        // ⚠ Pending answers included: the bar describes what the one-click would do, and running it
        // reads the pending ones too. Describing an act and performing it must read the same thing.
        var described = EffectivePreference(report);
        var blocked = WhyNotReady(report);

        var body = new StackPanel { Spacing = 8 };

        // What it is about to do, listed before it does it. The same courtesy the install
        // confirmation already extends — here it is permanent, so the button never has to be
        // pressed to find out what it means.
        var steps = OneClickSteps(report, described).ToList();

        // ⚠ An unticked box makes the step list empty, so "nothing left to do" cannot be decided
        // on that list alone: on a game already up to date, holding a translation with unpublished
        // work, every step is absent precisely BECAUSE there is an offer standing — and taking the
        // shortcut here left the box that offers it unreachable.
        //
        // ⚠ Read exactly as the box itself is, or the two disagree and the bar says "there is
        // still something on offer" under a row where no box is drawn — leaving a game that IS
        // fully set up unable to say so.
        var offered = MaySetUp(report)
                      && TranslationOffers.For(report, TranslationWaiting(report))
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

            if (PlayButton(report.Game, small: false, _running.IsRunning(report.Game), report) is { } start)
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

            foreach (var control in ModDefaultsWayOut(report, blocking: true))
                explanation.Children.Add(control);
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

            // ⚠ **After the steps, because it qualifies them.** Unblocked means a way was chosen —
            // the mod asks in the game, or this game answers for itself — and this is the case that
            // was silent: the message and its button lived only while the one-click was refusing,
            // so choosing a way cleared the block and took the explanation with it.
            //
            // ⚠ And the bar is the one part of a card that BOTH tabs show. Somebody arriving on
            // Home saw a lit button and nothing about what it would configure the game with, the
            // answer being on a tab they had not opened.
            foreach (var control in ModDefaultsWayOut(report, blocking: false))
                explanation.Children.Add(control);
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
        // ⚠ Absent when there is nothing for it to do — nothing published to take, the file here
        // already IS the one that would be taken, or this game is set up under another account. A
        // ticked box that re-downloads the same bytes reads as an action, an unticked one reads as
        // something being withheld, and either of them on somebody else's game offers a write that
        // every other control on the card refuses.
        //
        // 🔴 **Not conditioned on the box's own value**, which would be a trap: unticked, the step
        // disappears, and a box that hid itself could never be ticked again. It is conditioned on
        // what exists regardless of the answer.
        var offer = TranslationOffers.For(report, TranslationWaiting(report));

        if (MaySetUp(report) && offer is not (TranslationOffer.None or TranslationOffer.AlreadyInPlace))
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

        // 🔴 **The way back, beside the way forward.** Every block on this card offers Undo next to
        // its own Apply; the bar, which gathers what all of them are waiting to write, offered only
        // the going. Somebody who changed three things across three blocks and thought better of it
        // had to find each block and undo it there — and the blocks are folded away by default.
        //
        // ⚠ Present only while something is waiting, like the counters it mirrors: a control that
        // can undo nothing is not reassurance, it is furniture.
        //
        // ⚠ "Undo", the word this program already uses beside a pending change — never "Cancel",
        // which in a window means "close without doing anything" and would be read as leaving.
        if (PendingAnswers(report))
        {
            var undo = new Button { Content = "Undo", MinWidth = 90 };

            ToolTip.SetTip(undo, "Forgets the answers given on this card and not yet applied. "
                                 + "Nothing already written into the game is touched.");

            undo.Click += async (_, _) =>
            {
                ForgetPendingAnswers(report);

                // The whole card: these answers are shown by the blocks that hold them, not only
                // by this bar. Safe here — unlike inside a form, nobody is typing in the bar.
                await ShowSelectedAsync();
            };

            right.Children.Add(undo);
        }

        right.Children.Add(go);

        // After the set-up button, not before it: the order on this bar is the order of the two
        // acts. Present even when there is nothing left to set up — a card whose every job is done
        // is exactly the one somebody opened in order to go and play.
        if (PlayButton(report.Game, small: false, _running.IsRunning(report.Game), report) is { } play)
            right.Children.Add(play);

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
        // 🔴 **Asked once, for the two steps that write into somebody else's game.** Installing is
        // not among them — see MaySetUp, which says why the line is drawn there.
        var mayChangeThisGame = MaySetUp(report);

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
        // nothing of its own. Unticked, an already-configured game is left alone — its own settings
        // are written by their own button, which is what every other brick on this card does too.
        //
        // ⚠ **Except on a game with no configuration at all**, where the box protects a file that
        // does not exist and its own settings have no button either: theirs is silent with nowhere
        // to write. Left as it was, the one path a first-time player takes wrote nothing anywhere.
        // Same condition as BuildPlan, and it has to stay the same one — this list is the promise
        // and that method is the act.
        //
        // 🔴 **And never on a game set up under another account, whatever the box says.** The box
        // answers "should this game follow Mod defaults" — a preference held per Windows account,
        // about a config.json shared by the whole computer. Ticked by default, it was enough on its
        // own to rewrite somebody else's language, model and key from this account's answers.
        // 🔴 **Restored after being removed, and the removal is worth recording.** The condition
        // was dropped so that a game answering for itself would also get its settings written by
        // the one-click. That is a real gap — but this step is not the way to fill it, and the
        // proof is one comment above `Differences`: that list compares against **Mod defaults,
        // always**, deliberately, so that the button under it can say what it writes. Widening the
        // step made a list that only ever speaks of Mod defaults drive a step on a game that had
        // explicitly refused them: "apply Mod defaults (1 change)" appeared under "set it up here",
        // offering to write a value nobody had asked for.
        //
        // ⚠ The gap is real and is NOT closed here: a game's own answers are a different brick,
        // written by the form's own Apply. Giving the one-click a step of its own for them needs a
        // second comparison — against the per-game resolution rather than against Mod defaults —
        // which is exactly what the comment above `Differences` warns must not be conflated with
        // this one. See TODO.md.
        if (mayChangeThisGame
            && WouldWriteSettings(report, preference)
            && SettingsWouldChangeAnything(report, preference))
        {
            yield return new(OneClickAct.ApplySettings, SettingsStepText(report, preference));
        }

        // 🔴 **The one-click writes the translation file too, so it obeys the account rule.**
        //
        // Every control that replaces this file is greyed on a game set up under another account —
        // the workbench's six, and the swap button beside the picked translation. This list was the
        // way round all of them: one click, and somebody else's translation is overwritten from the
        // bar at the bottom of the window. Same defect as the swap button, one screen further out.
        //
        // ⚠ Only the translation step is dropped. Installing the loader or the mod puts OUR software
        // in place and takes nothing away from anybody; what must not happen is writing over the
        // work or the settings another user of this computer put there.
        if (!mayChangeThisGame || !_takeTranslation
            || TranslationWaiting(report) is not { } chosen) yield break;

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
        var changes = WrittenDifferences(report, preference).Count(d => d.Writes);

        // ⚠ **Two sources when there are two, because "apply Mod defaults" was only half true.** A
        // game that answered for itself is set up from the defaults EXCEPT where it answered, and
        // announcing the defaults alone told somebody their two answers were about to be ignored —
        // which, before they were laid down at install, they had been.
        //
        // ⚠ This used to be reachable only with the box ticked, hence the single source. It is now
        // reached on a game with no configuration whatever the box says.
        var own = preference.Mod?.Count ?? 0;

        // ⚠ **What is actually going in, named.** A game that does not follow Mod defaults is
        // written from its own answers and from what it already holds — announcing "Mod defaults"
        // there offered to write the one thing its owner had refused.
        var source = !preference.UsesModDefaults(GameConfig(report))
            ? (own == 0 ? "the settings this game keeps"
                        : $"the {own} set for this game")
            : own == 0
            ? "Mod defaults"
            : $"Mod defaults, with {own} set for this game";

        return changes > 0 ? $"apply {source} ({changes} changes)" : $"apply {source}";
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
        //
        // ⚠ Against what would ACTUALLY be written, which is not always Mod defaults — see
        // WrittenDifferences. On a game following the defaults the two are the same call.
        return WrittenDifferences(report, preference).Any(d => d.Writes);
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
    /// ⚠ The rule is <see cref="TranslationChoice.Pick"/>; this only says where its answers come
    /// from. The choice is the PENDING one — the stored id says which translation this game was set
    /// up WITH, and reading that as a request re-offered it for ever.
    /// </summary>
    private OnlineTranslation? PickTranslation(GameReport report) =>
        TranslationChoice.Pick(report, _settings.ResolveTargetLanguage(),
                               ChosenTranslation(report.Game.Path));

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

        // ⚠ The same reading as the list of steps and the box beside the button. It was
        // PickTranslation here and TranslationWaiting there, which is how a bar promising three
        // acts performs four — and the fourth writes a file.
        var translation = _takeTranslation && MaySetUp(report) ? TranslationWaiting(report) : null;

        // 🔴 **A copy for the question, the real thing only once it is answered.** The list below
        // has to name what is pending — a step reading "apply Mod defaults" while two answers wait
        // beside it is the confirmation lying about the act it is confirming. But promoting them
        // here would write unvalidated answers to disk for somebody who then presses Cancel.
        var shown = preference.Copy();
        ValidateInto(report, shown, save: false);

        // Everything at stake, gathered and asked once.
        var body = new StackPanel { Spacing = 10 };

        var steps = OneClickSteps(report, shown).ToList();

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

        // ⚠ Laid over the preference so the plan is built from what was just confirmed — but on a
        // copy, and not written: an install that fails has validated nothing. The disk is written
        // where the outcome is known, below.
        preference = preference.Copy();
        ValidateInto(report, preference, save: false);

        Busy(true, "Starting...");

        var engine = new InstallEngine(_platform, _catalog);
        engine.Status += OnEngineStatus;

        try
        {
            // Same switch as the mod's own button, and for the same reason: on a game set up by
            // another account the loader and the plugin go in, its configuration does not.
            var plan = BuildPlan(report, preference, loader: true, plugin: true,
                                 settings: MaySetUp(report));

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

            // The answers reached a game, so they stop being pending.
            ValidatePending(report, _preferences.Read(report.Game.Path));
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
    /// <summary>
    /// Drops this game's remembered answers once they are in its config.json.
    ///
    /// 🔴 **The config.json IS the storage, and a second copy of it goes stale.** These answers
    /// exist for one reason: to be carried until there is a file to write them into. Kept after
    /// that, they became a rival source of truth — and the resolver reads them FIRST, so the stale
    /// one won. Change the language inside the mod and the card went on showing the language the
    /// Manager remembered, marked "set for this game", with Apply lit and offering to write it back
    /// over what the player had just chosen.
    ///
    /// ⚠ Nothing is lost by dropping them. With no answer of its own, the resolver falls through to
    /// what the game holds — which is where they were just written. What changes is that the game
    /// is asked every time instead of being remembered once, so a change made in the mod is picked
    /// up by construction rather than by somebody thinking to reconcile it.
    ///
    /// ⚠ Only once the file exists: a write that failed must not take the answers with it.
    ///
    /// ⚠ The rest of the preference stays. It holds what the mod knows nothing about — the box, the
    /// chosen translation, whether this loader is ours to manage. That is the line: anything the
    /// config.json carries belongs to the config.json.
    /// </summary>
    private void ForgetWrittenAnswers(GameReport report)
    {
        var preference = _preferences.Read(report.Game.Path);
        if (preference.Mod is null) return;
        if (!GameConfig(report).IsConfigured) return;

        preference.Mod = null;
        _preferences.Set(report.Game.Path, preference);

        // ⚠ The held draft goes with them. It is the same answers one step earlier in the journey,
        // and leaving it behind would have the one-click go on offering to write what is already
        // in the file — the stale rival source this method exists to remove, by another door.
        _pendingMod.Remove(report.Game.Path);
    }

    private void RememberDefaultsWereWritten(GameReport report, InstallPlan plan,
                                             GameConfigSnapshot before)
    {
        // Both install paths come through here, so it is where the answers stop being remembered.
        ForgetWrittenAnswers(report);

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

        yield return Remembering(new Expander
        {
            Header = new TextBlock
            {
                Text = "what changes",
                FontSize = 11,
                Foreground = Brush("TextMuted"),
            },
            Content = lines,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(14, 0, 0, 0),
        }, "what-changes");
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

        // 🔴 **The language switch, said HERE and not asked afterwards.** Taking a translation in
        // another language IS deciding to play this game in that language — there is no sensible
        // way to want the file in one language and the lines the mod adds in another. So it is
        // stated among the consequences of this confirmation, where somebody who chose the wrong
        // row can still cancel, and it happens with the write.
        //
        // ⚠ It replaced a question asked after the file was already written, whose stated reason
        // was false: it claimed the mod would not use the installed file, and the mod reads the
        // file whatever the language setting says.
        if (LanguageSwitchOnTaking(report, taking) is { } switching)
        {
            yield return new TextBlock
            {
                Text = $"This game is set to {switching.From} and this translation is in "
                     + $"{switching.To}. It will be set to {switching.To} — this game only, your "
                     + "default does not move.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary"),
                FontSize = 12,
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
                     People.MentionOf(translation.Author, _settings.Current.ApiUser),

                     // ⚠ And WHICH translation it is. Without it a mod with nobody signed in has
                     // no id to ask about, so it can learn nothing about the file this just wrote.
                     translation.Id);

        if (!result.Written)
            return $"The translation could not be written ({result.Failure}). Everything else is in place.";

        // Remembered so the card can say which one this game runs, and so a later one-click does
        // not silently pick a different translation than the one already in place.
        //
        // ⚠ Written HERE and nowhere else: this is the only moment the file is actually in the
        // game, which is what the field states. Choosing one is a separate, pending answer — see
        // _pendingTranslation.
        SaveAnswer(report.Game.Path, stored => stored.InstalledTranslationId = translation.Id);

        // The intention has been carried out, so it stops being pending. Cleared on success only:
        // a failed install leaves the choice standing, which is what somebody would expect.
        _pendingTranslation.Remove(report.Game.Path);

        // ⚠ Names the place somebody can act from, not a folder on disk. "It is in
        // .ugt/removed/translations-20260817.json" is an instruction to open a file manager;
        // "Backups" is a button they have already seen on this card.
        var message = "The translation is in place.";
        if (result.KeptPrevious)
            message += " What was here is kept under Backups.";

        return message;
    }

    /// <summary>
    /// Answers given on a game with nothing installed, waiting for the act that validates them.
    ///
    /// 🔴 **In memory, and nowhere else.** Before the mod is installed there is no Apply — the verb
    /// has no object — so these answers have to survive a card being redrawn, a tab being changed
    /// and a rescan, or they vanish under the person still filling the form in. What they must NOT
    /// survive is closing the program: nothing was validated, and finding half-typed answers
    /// waiting on the next launch is the tool deciding something nobody decided.
    ///
    /// ⚠ Not parked on the GamePreference either, even unsaved: Read hands back the live stored
    /// object, so any unrelated Set — picking a translation, ticking a box — would write them to
    /// disk as a side effect of something else entirely.
    ///
    /// Promoted by <see cref="ValidatePending"/>, called by the three acts that write them.
    /// </summary>
    private readonly Dictionary<string, GameModOverrides> _pendingMod =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same, for the two answers the plan block holds.</summary>
    private readonly Dictionary<string, (bool Start, string? Context)> _pendingPlan =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The community translation named for a game and not yet applied, by game path.
    ///
    /// 🔴 **It was written to disk on the click, and that is the defect this replaces.** Choosing a
    /// card in the translations window saved the id into game-preferences.json immediately — no
    /// Apply, nothing on screen saying a choice was waiting, and it survived the program closing.
    /// So a translation looked at one evening went on being offered weeks later, over work done in
    /// the game since; and because the stored field also meant "the one installed here", a game
    /// whose local file had diverged was told to install the translation it already had.
    ///
    /// ⚠ Same rule as every other answer given before an act: held for the session, promoted by
    /// whatever carries it out — <see cref="TakeTranslationAsync"/>, which writes
    /// <see cref="GamePreference.InstalledTranslationId"/> only once the file is actually in place.
    /// A failed install therefore keeps the choice, which is what somebody would expect.
    /// </summary>
    private readonly Dictionary<string, int> _pendingTranslation =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The translation somebody named for this game and has not applied — never what is installed.
    ///
    /// ⚠ The two are different questions and used to share one field. What is IN the game is read
    /// from the game (`report.MatchingOnline`, the local file's lineage) or, once installed by us,
    /// from <see cref="GamePreference.InstalledTranslationId"/>.
    /// </summary>
    private int? ChosenTranslation(string gamePath) =>
        _pendingTranslation.TryGetValue(gamePath, out var id) ? id : null;

    /// <summary>
    /// The way chosen for a game — Mod defaults, the mod's Setup, or its own settings — before
    /// anything has acted on it.
    ///
    /// 🔴 **Clicking a radio decided nothing yet, and it was written to disk on the click.** So a
    /// choice tried out on a Tuesday was still in force on a Wednesday, on a game nobody had
    /// installed, deciding what the one-click would configure it with — and the only sign of it was
    /// a radio somebody had to think to look at. What made it dangerous is that the answer had
    /// since become wrong: Mod defaults was filled in between the two, and the game still said
    /// "Set it up in the game".
    ///
    /// ⚠ Same rule as every other answer given before an act: held for the session, promoted by
    /// whatever validates it. See <see cref="ValidateInto"/>.
    /// </summary>
    private readonly Dictionary<string, (bool? Defaults, bool Wizard)> _pendingWay =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Writes ONE answer to disk, on the stored preference rather than on a copy.
    ///
    /// 🔴 **The card reasons on a copy carrying unvalidated choices** — see PreferenceWithPending —
    /// so a handler inside it that saved its own object would file those choices away as a side
    /// effect of confirming something else entirely. Applying a hotkey is not a decision about
    /// where this game's settings come from.
    /// </summary>
    private void SaveAnswer(string gamePath, Action<GamePreference> change)
    {
        var stored = _preferences.Read(gamePath);
        change(stored);
        _preferences.Set(gamePath, stored);
    }

    /// <summary>
    /// A copy of what this game answers, with anything still pending laid over it.
    ///
    /// ⚠ A COPY, always. GamePreferences.Read hands back the stored object, so overlaying onto it
    /// would put unvalidated answers into the next unrelated Set — which is the whole thing being
    /// avoided here.
    /// </summary>
    private GamePreference PreferenceWithPending(string gamePath)
    {
        var preference = _preferences.Read(gamePath).Copy();

        // 🔴 **A way stored by a session that never acted does not survive it.** Holding the choice
        // in memory stops NEW ones being written; it does nothing about the ones already on disk,
        // and those are the dangerous kind — a way tried out weeks ago, still deciding what an
        // install will do, on an answer that may since have become wrong.
        //
        // ⚠ "Never acted" is read from the game, not from the file: no configuration and no
        // answers of its own means nothing was ever written here by anybody, so nothing validated
        // the choice. A game that HAS been set up keeps what it says, because there the choice was
        // carried out.
        // ⚠ The receipt is the proof: it is written by an install and by nothing else. A game
        // configured from inside the mod without one still lands on Custom, because a null answer
        // on a configured game reads that way already — so nothing is lost by not asking twice.
        if (ReceiptStore.Read(gamePath) is null && preference.Mod is null)
        {
            preference.ApplyModDefaults = null;
            preference.LetWizardAsk = false;
        }

        if (_pendingWay.TryGetValue(gamePath, out var way))
        {
            preference.ApplyModDefaults = way.Defaults;
            preference.LetWizardAsk = way.Wizard;
        }

        if (_pendingMod.TryGetValue(gamePath, out var mod))
            preference.Mod = mod.IsEmpty ? null : mod.Copy();

        if (_pendingPlan.TryGetValue(gamePath, out var plan))
        {
            preference.StartTranslation = plan.Start;
            preference.GameContext = plan.Context;
        }

        return preference;
    }

    /// <summary>
    /// Moves whatever is pending for this game onto the preference and saves it, because the act
    /// about to run IS the validation.
    ///
    /// ⚠ Called BEFORE the plan is built, never after: the plan reads the preference, so promoting
    /// afterwards would install with the old values and store the new ones — the same fault this
    /// whole area was carrying, one step further along.
    /// </summary>
    private void ValidatePending(GameReport report, GamePreference preference) =>
        ValidateInto(report, preference, save: true);

    /// <summary>
    /// The preference as it stands RIGHT NOW: what is stored, plus every answer given on this card
    /// and not yet applied.
    ///
    /// 🔴 **Describing the one-click and running it must read the same thing, and they did not.**
    /// The bar and its list of steps read the STORED preference while RunOneClickAsync read the
    /// pending answers too. Somebody who changed this game's own settings therefore saw a bar that
    /// had not noticed, and had to scroll to find the block's own Apply — the one control that had
    /// registered the change.
    ///
    /// ⚠ A copy, and never saved. What is pending has been ANSWERED, not validated: storing it here
    /// would keep answers somebody typed and walked away from. It is promoted for real by
    /// <see cref="ValidatePending"/>, once, when the act is actually run.
    /// </summary>
    private GamePreference EffectivePreference(GameReport report)
    {
        var preference = _preferences.Read(report.Game.Path).Copy();
        ValidateInto(report, preference, save: false);
        return preference;
    }

    /// <param name="save">
    /// False to merely SHOW what is pending — a confirmation has to name it, and somebody who then
    /// presses Cancel must not find it stored. The caller passes a copy in that case, so the
    /// pending stays pending and the real preference is untouched.
    /// </param>
    private void ValidateInto(GameReport report, GamePreference preference, bool save)
    {
        var path = report.Game.Path;
        var moved = false;

        if (_pendingMod.TryGetValue(path, out var mod))
        {
            preference.Mod = mod.IsEmpty ? null : mod.Copy();
            if (save) _pendingMod.Remove(path);
            moved = true;
        }

        if (_pendingPlan.TryGetValue(path, out var plan))
        {
            preference.StartTranslation = plan.Start;
            preference.GameContext = plan.Context;
            if (save) _pendingPlan.Remove(path);
            moved = true;
        }

        if (_pendingWay.TryGetValue(path, out var way))
        {
            preference.ApplyModDefaults = way.Defaults;
            preference.LetWizardAsk = way.Wizard;
            if (save) _pendingWay.Remove(path);
            moved = true;
        }

        if (moved && save) _preferences.Set(path, preference);
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
        // 🔴 **The box, on a game that HAS a configuration — and nothing at all on a game that has
        // none.** An install and the one-click apply the PREFERENCE and invent nothing; ticked, this
        // game is set up from Mod defaults; unticked, its own configuration is left exactly as it is.
        //
        // ⚠ What that line got wrong is the third case. The box's own rule is "a game that is
        // already configured keeps its own configuration" — it exists to protect a FILE. On a first
        // install there is no file, so unticking protected nothing and merely meant "write nothing":
        // the game came up with no config.json at all, the mod fell back to the system language, and
        // the answers typed into this game's own settings were never laid down by anybody. The
        // comment on ApplyOwnSettingsAsync promised the next install would lay them down; the next
        // install skipped settings entirely, and the two comments had been contradicting each other
        // in two files.
        //
        // ⚠ And writing then contradicts nothing: the resolver reads own answers, THEN what the game
        // holds, THEN the defaults — so the form already shows a complete set of values on an
        // unconfigured game, and writing less than the screen shows is the lie, not the reverse.
        // ⚠ Still needed below to choose WHICH values go in; whether they go in at all is
        // WouldWriteSettings' answer.
        var usesDefaults = preference.UsesModDefaults(GameConfig(report));

        // 🔴 **One rule, asked once.** Answers of its own are written even with Mod defaults
        // untouched — the Reviewed guard is about not deciding FOR somebody, and a game they
        // answered themselves is not that. That reasoning now lives in WouldWriteSettings, which
        // the step list asks too: they were two copies and they drifted, so the act wrote a game's
        // own answers while the promise never mentioned them.
        //
        // ⚠ The wizard stays open in that case, decided in the plan: the unanswered fields are
        // still guesses, and the wizard is the only thing that will ever correct them.
        var writeSettings = settings && WouldWriteSettings(report, preference);

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
            // 🔴 **Which set of values, and it is not always the defaults.** `Intended` reads
            // `settings.TranslationBackend`, `settings.AiModel` and the rest straight off whatever
            // it is handed — only the hotkey, the context and "start translating" ever consult the
            // per-game answers. So handing it Mod defaults on a game that answered for itself wrote
            // Mod defaults, silently, no matter what the form showed.
            //
            // Ticked, the defaults ARE the answer and the resolver must not be used: it would fall
            // back to what the game already holds, and ticking the box means overwriting that.
            // Unticked — reachable here only on a game with no configuration at all — the resolver
            // gives own answers over defaults, which is exactly what the form displays.
            writeSettings ? (usesDefaults ? _settings.Current : resolved) : null,
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
        yield return Callout(body, Tone.Warning);
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

        yield return Callout(body, Tone.Warning);
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
        // 🔴 **The plan sees the pending answers; the disk only sees them if it worked.** They were
        // promoted before the plan, on the reasoning that the plan reads the preference — true, and
        // it made a FAILED install keep them anyway. An install that put nothing in a game has
        // validated nothing, and coming back to find the answers filed away is the same fault this
        // whole mechanism exists to prevent, one step further along.
        //
        // ⚠ On a copy, so nothing unrelated can persist them: Read hands back the stored object.
        var preference = _preferences.Read(report.Game.Path).Copy();
        ValidateInto(report, preference, save: false);

        // The loader still comes along when there is none — a plugin without one loads in no game,
        // and refusing here would mean the mod's own button could not work on a fresh game.
        //
        // 🔴 **On somebody else's game the files go in and the settings do not.** Updating the mod
        // is a service to whoever plays it; rewriting their language, their model and their key
        // while doing so is not, and it happened silently — an update wrote the whole configuration
        // from THIS account's answers. `settings: false` is the switch the loader button has used
        // since the day an "install the loader" wrote a config.json it had no business writing.
        var plan = BuildPlan(report, preference,
            loader: report.InstalledLoader is null, plugin: true,
            settings: MaySetUp(report));

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
        // 🔴 **The comment above promised the file and the code read the preference.** Everything
        // it says — a stored answer is a claim about a game somebody may have changed since — was
        // true of this very line: a game translating on disk was reported as merely "using" a
        // translation, because nobody had ticked the box in THIS window. Same defect as the
        // translate-while-playing box below, which that same comment names.
        var translating = preference.StartTranslation
                          ?? GameConfig(report).AutoTranslate
                          ?? _settings.Current.EnableAi;

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

        // Same rule as the box above it: this sentence is written into the game's configuration,
        // so it is read on somebody else's game and not typed into.
        context.IsEnabled = MaySetUp(report, context);

        // ⚠ Read on every keystroke into the DRAFT, and written by nothing here. It used to save
        // itself on LostFocus — no Apply, on a setting that lands in the game's config.json — and
        // then offered a "Save this into the game" button of its own beside it, so the same answer
        // had two ways of reaching the file and neither was the one the rest of the card uses.
        context.TextChanged += (_, _) =>
        {
            draft.Context = string.IsNullOrWhiteSpace(context.Text) ? null : context.Text.Trim();
            applyBar.Refresh();
        };

        // ⚠ On focus rather than on every keystroke: Record writes a file, and a game
        // description is a paragraph. Pressing any button - the one-click included - takes focus
        // away from here first, so what was typed is stored before the act that reads it runs.
        context.LostFocus += (_, _) => applyBar.Record();

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

        // 🔴 **What this game actually does, not what the tool would do to it.**
        //
        // It read `preference.StartTranslation ?? settings.EnableAi` — this window's own choice,
        // then this window's default — and never once looked at the game's config.json. Measured on
        // 2026-08-20: a game holding `enable_ai: true` showed the box UNTICKED, because nobody had
        // ever ticked it HERE. Somebody reading that concludes the game translates nothing, and
        // applying would have switched off what was working.
        //
        // ⚠ The middle source is the one that was missing, and the code next to it says so:
        // GameConfigWriter.Read carries `enable_ai` into the snapshot precisely because "it is
        // written from the preference and never read back — fine for writing and wrong for
        // describing". This is a describing screen.
        //
        // ⚠ NOT through ModSettingsResolver: enable_ai is deliberately absent from that chain
        // (GameModOverrides carries "no enable_ai and no game_context", both being per-game
        // measurements rather than values this tool may be told to write). Resolving it there
        // would make it a writable setting, which is a different decision from showing it.
        var inGameTranslates = GameConfig(report).AutoTranslate;
        var translatesNow = preference.StartTranslation ?? inGameTranslates ?? settings.EnableAi;

        var start = new CheckBox
        {
            Content = "Translate while I play",
            IsChecked = backend is not null && translatesNow,
            FontSize = 12,
        };

        // 🔴 **The CONTROL, not only the Apply below it.** Both of these land in this game's
        // config.json, which the whole computer shares, so on somebody else's game they are read
        // and not written. Greying the button alone would let somebody tick, type, and then meet a
        // refusal — the dead end this program refuses everywhere else. Ticked or filled in, they go
        // on showing what the game holds, which is what somebody came here to find out.
        var mayChange = MaySetUp(report, start);
        start.IsEnabled = backend is not null && mayChange;

        // 🔴 **Held, not written.** This wrote straight to disk on every click — the only pair of
        // mod settings in the tool that did, and a plain breach of the rule the rest of it keeps:
        // nothing reaches a game until Apply is pressed. It also made the switch below it
        // meaningless, since there was never a moment where an answer was pending.
        // ⚠ From the same value as the box above: a draft starting elsewhere would report a change
        // the moment the section is opened, or miss the one somebody makes.
        var draft = new PlanDraft(
            StartTranslation: translatesNow,
            GameContext: preference.GameContext ?? InGameContext(report, descriptor));

        var applyBar = PlanApplyBar(report, preference, draft, refresh);

        start.IsCheckedChanged += (_, _) =>
        {
            draft.StartTranslation = start.IsChecked == true;
            applyBar.Refresh();
            applyBar.Record();
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

        /// <summary>Keeps the counter honest. Called on every answer, including every keystroke.</summary>
        public required Action Refresh { get; init; }

        /// <summary>
        /// Stores the answers where no Apply will ever store them — before the mod is installed.
        ///
        /// ⚠ Separate from <see cref="Refresh"/> precisely because Refresh runs per keystroke and
        /// this one writes a file. A text field calls it when focus leaves; a tick, immediately.
        /// Does nothing once there is a config, where Apply is what stores them.
        /// </summary>
        public required Action Record { get; init; }
    }

    private PlanApply PlanApplyBar(GameReport report, GamePreference preference,
                                   PlanDraft draft, Action refresh)
    {
        // Local: this writes into THIS game's config.json and sends nothing anywhere — the same
        // mark the settings form carries, for the same reason.
        // 🔴 **No Apply before the mod is installed, and the answers are kept as they are given.**
        // Same reasoning as the settings form beside it: Apply means "write this into the game",
        // and there is no file yet. Pressing it did nothing visible while quietly being the only
        // thing that stored these two answers, so somebody who set them and pressed the one-click
        // instead lost them — and the one-click then installed without them.
        if (!GameConfig(report).IsConfigured)
        {
            // ⚠ Held, not saved — see _pendingPlan. Nothing here has been validated yet.
            void Keep() => _pendingPlan[report.Game.Path] = (draft.Start, draft.Context);

            return new PlanApply
            {
                View = new TextBlock
                {
                    Text = "Written into the game when the mod is installed.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = Brush("TextMuted"),
                },

                // Nothing to redraw: there is no counter and no button to light.
                Refresh = () => { },
                Record = Keep,
            };
        }

        var apply = ScopeMark.Marked(EditSide.Local, "Apply", enabled: false);
        apply.Classes.Add("primary");
        apply.FontSize = 12;

        void Redraw()
        {
            var count = draft.Pending;

            // ⚠ SetLabel, never Content: the button holds its scope marks beside the text.
            ScopeMark.SetLabel(apply, count > 0 ? $"Apply ({count})" : "Apply");

            ToolTip.SetTip(apply, count > 0
                ? $"Writes these {count} setting(s) into the game."
                : "Nothing has been changed here.");

            // Last, so the refusal replaces the tooltip above rather than the reverse.
            var mine = MaySetUp(report, apply);
            apply.IsEnabled = count > 0 && !_running.IsRunning(report.Game) && mine;
        }

        apply.Click += async (_, _) =>
        {
            preference.StartTranslation = draft.Start;
            preference.GameContext = draft.Context;
            _preferences.Set(report.Game.Path, preference);

            // In the file now, so the held copy has nothing left to say.
            _pendingPlan.Remove(report.Game.Path);

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

            // 🔴 **Held here too, and it was not.** The reasoning was that Apply stores these
            // together with the write, as one decision — true of STORING, and this does not store:
            // it hands them to the window, which keeps them in memory and writes nothing. The
            // silence meant "translate while I play" and "what is this game about" reached no other
            // control: their own Apply lit up while the one-click said the game was fully set up.
            // Exactly the defect the settings form had, in the block beside it.
            Record = () =>
            {
                _pendingPlan[report.Game.Path] = (draft.Start, draft.Context);

                // The bar only — it has its own container, so it can be redrawn while somebody is
                // still typing in the description box beside it.
                ShowActionBar(report);
            },
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
    /// The one thing a card about translations has to say when there are none: that this is exactly
    /// how the mod is meant to be used, and that a translation gets made by playing.
    ///
    /// ⚠ **Shared by both tabs**, which had written the same fact twice in two registers. It also
    /// used to sit at the head of the block that carried the second Apply button — see
    /// <see cref="TranslationWaiting"/> for why that button is gone: information about what exists
    /// and the verb that acts on it are two different things, and only one of them may be doubled.
    /// </summary>
    private IEnumerable<Control> NothingPublishedYet(GameReport report)
    {
        // 🔴 **Nothing at all when no translation for this game can ever exist — heading included.**
        //
        // "No translation has been published for this game yet" carries a *yet*: it describes a
        // waiting room. Against a stripped runtime, encrypted store binaries or a game that is not
        // Unity, there is none — nobody will ever run the mod there, so nobody will ever capture
        // the text, and the absence is already explained in full by the red card above.
        //
        // 🔴 **The test is ModCouldRun, NOT IsModdable, and the difference is the whole point.**
        // An anti-cheat is a warning, not a wall: the mod works, we simply refuse to be the one
        // that installs it, because the banned account would be the player's. Somebody may install
        // it by hand and publish a translation, and this card must be able to show it. Same for a
        // runtime or architecture we failed to read. Cutting on `IsModdable` silenced all six
        // refusals alike and made three of them read as impossible.
        if (!report.Game.ModCouldRun) yield break;

        yield return new TextBlock
        {
            Text = "No translation has been published for this game yet.",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
            Foreground = Brush("TextPrimary"),
        };

        // ⚠ On a game we refuse but the mod could still run on, the reservation comes BEFORE the
        // invitation, never after. Both sentences below start "play with the mod on" — true once
        // it is installed, and the button beside this card will not install it. Promising first
        // and withdrawing afterwards is the contradiction this whole card was fixed for.
        if (!report.Game.IsModdable)
        {
            yield return new TextBlock
            {
                Text = "The Manager will not set this game up — the card above says why. "
                     + "Install the mod yourself and its translation is managed from here "
                     + "like any other.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 2, 0, 0),
                Foreground = Brush("StatusWarning"),
            };
        }

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

        // ⚠ Done, not asked. Choosing a translation in another language is choosing to play this
        // game in it; the confirmation that led here said so among its consequences, which is where
        // somebody who picked the wrong row could still stop.
        AlignGameLanguage(report, descriptor, picked);

        await ShowSelectedAsync();
    }

    /// <summary>
    /// The language change taking this translation would make, or null when it makes none.
    ///
    /// 🔴 **A consequence to state, never a question to ask.** This was a dialog put up after the
    /// file was already written, and its stated reason was false: it claimed the mod would not use
    /// what had just been installed, when `target_language` is read in four places in the mod — a
    /// log line, the AI prompt, the Google language code, the DeepL one — and in none of them to
    /// load or serve the file. The file is used whatever the setting says.
    ///
    /// What is true is that lines the mod meets and the file does not hold are written in the
    /// configured language, so an English file in a game set to French fills up with French. Nobody
    /// wants one file in two languages, so there is one sensible answer — and a question with one
    /// sensible answer is a confirmation of something already decided. Choosing a translation in
    /// another language IS choosing to play this game in it.
    /// </summary>
    private (string From, string To)? LanguageSwitchOnTaking(GameReport report,
                                                             OnlineTranslation translation)
    {
        var taken = translation.TargetLanguage;
        if (string.IsNullOrWhiteSpace(taken)) return null;

        if (InstalledDescriptor(report) is not { } descriptor) return null;

        // What the GAME is set to, not what this tool defaults to: they are allowed to differ, and
        // this one is what the mod acts on.
        var configured = LocalTranslationProbe.ReadTargetLanguage(report.Game.Path, descriptor);

        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (string.Equals(configured, taken, StringComparison.OrdinalIgnoreCase)) return null;

        return (configured, taken);
    }

    /// <summary>
    /// Points the game at the language of the translation just taken.
    ///
    /// ⚠ Writes that ONE key. It used to go through Apply, which carried the backend and the
    /// update preferences along with it — a language question answered by rewriting the whole
    /// configuration.
    ///
    /// ⚠ The SOURCE language is deliberately not carried across. That field describes the person
    /// who made the translation, not the game: nothing here can read what language a game's own
    /// text is in, and writing a guess would put "translate from English" into every prompt — and,
    /// under strict_source_language, retire every line judged to be in another language.
    /// </summary>
    private void AlignGameLanguage(GameReport report, LoaderDescriptor descriptor,
                                   OnlineTranslation translation)
    {
        if (LanguageSwitchOnTaking(report, translation) is not { } switching) return;

        new GameConfigWriter().ApplyOne(report.Game.Path, descriptor,
            GameConfigWriter.TargetLanguageKey, switching.To, "language");
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

            // ⚠ Nothing to check: this answer needs no server and no key. It is a complete setup —
            // the mod captures the text and somebody writes the lines — and saying so is what
            // stops the rest of this program treating it as "no translator configured".
            "capture" => "Capturing the game's text for you to translate by hand",

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
            .Apply(report.Game.Path, descriptor, settings, target,
                   skipWizard: !LetsWizardAsk(report, preference), perGame: preference);

        Busy(false, "Ready.");

        // ⚠ Here too: writing Mod defaults puts values into the file over whatever this game
        // answered, so a remembered answer left behind would contradict the file it was just
        // overwritten in — and would win, being read first.
        if (result.Written) ForgetWrittenAnswers(report);

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

        await RepublishAsync();
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

        await RepublishAsync();
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

        if (outcome.Success)
        {
            // Now, and only now: the answers reached a game.
            ValidatePending(report, _preferences.Read(report.Game.Path));
            RememberDefaultsWereWritten(report, plan, configBefore);
        }

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
            // 🔴 **Two different acts behind one tick, and the sentence has to say which one.**
            // Leaving the backups alone means the translation is backed up one last time and the
            // history stays with the game; ticking them too means nothing survives at all. Told
            // "a copy is kept aside first" in both cases — which is what this said — somebody
            // erasing everything read a reassurance that was false for them.
            var history = chosenData.Count(UserDataInventory.IsBackup);

            var summary = history == 0
                ? $"{chosenData.Count} file(s) will be deleted from {report.Game.Name}, including "
                  + "anything captured while playing that was never uploaded.\n\nThe translation is "
                  + $"backed up one last time first, and this game's {Backups.ScreenTitle.ToLowerInvariant()} "
                  + "stay where they are."
                : $"{chosenData.Count} file(s) will be deleted from {report.Game.Name}, including "
                  + $"{history} backup file(s) and anything captured while playing that was never "
                  + "uploaded.\n\nNothing is kept aside. This cannot be undone.";

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
        // ⚠ Names the place somebody can act from, not a folder on disk. A path is an instruction
        // to open a file manager; "Backups" is a button they have already seen on this card.
        if (outcome.LastBackupTaken)
            message += Environment.NewLine + Environment.NewLine +
                       "The translation was backed up one last time. It is under Backups, with the "
                       + "fonts and images it used.";

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
                // 🔴 **Everything starts ticked EXCEPT the translation's history.** The box that
                // opens this list reads "(a copy is kept aside)", and that sentence is only true
                // while the backups stay: ticked, RemoveUserData sees the history in the list, takes
                // no last backup, and deletes it — so the promise was undone by the default sitting
                // underneath it, inside a section that opens collapsed.
                //
                // ⚠ Asked of UserDataInventory rather than judged from the group label: the same
                // question is asked in three places, and two spellings of it would disagree in the
                // direction that deletes.
                var box = new CheckBox
                {
                    IsChecked = !UserDataInventory.IsBackup(item.RelativePath),
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

            // Read from the files, never assumed: a group whose files start unticked must not show
            // a ticked header, or the section reads as "everything goes" while collapsed.
            var startingTicks = groupBoxes.Count(b => b.IsChecked == true);

            var header = new CheckBox
            {
                IsChecked = startingTicks == groupBoxes.Count ? true
                          : startingTicks == 0 ? false
                          : null,
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

    private Task<bool> ConfirmAsync(string title, string body, string confirmLabel,
                                    string? declineLabel = null) =>
        ConfirmAsync(title, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                     confirmLabel, declineLabel);

    /// <summary>
    /// A modal confirmation. Written by hand rather than pulled from a dialog package: one
    /// window type is not worth a dependency that would also have to be kept current.
    /// </summary>
    /// <param name="declineLabel">
    /// What saying no DOES, when it does something. Null keeps "Cancel", which is right whenever
    /// declining leaves the world as it was.
    ///
    /// 🔴 **It is not always a cancellation.** Asked after a translation is already installed —
    /// "point the game at English?" — declining does not undo anything: it keeps the game on the
    /// language it had, and leaves the file that was just written unused. "Cancel" told somebody
    /// they were calling something off, and the one question they asked out loud was what it would
    /// actually do.
    /// </param>
    private async Task<bool> ConfirmAsync(string title, Control body, string confirmLabel,
                                          string? declineLabel = null)
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
        var cancel = new Button { Content = declineLabel ?? "Cancel", IsCancel = true, IsDefault = true };

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
    private static Control Callout(string text, Tone tone) =>
        Callout(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("TextPrimary"),
        }, tone);

    /// <summary>
    /// The same notice, around something richer than a sentence — a list, a button, both.
    ///
    /// ⚠ One shape for every notice on this screen, and it had drifted into three: the blockers
    /// used this, the configuration differences built their own Border with a full outline and a
    /// different radius, and the newest warnings were dressed as plain cards, which made a problem
    /// look like a section. A notice is recognised by its edge before it is read; three edges mean
    /// nothing is recognised at all.
    /// </summary>
    private static Control Callout(Control content, Tone tone) => new Border
    {
        Background = Brush(Tones.CalloutBackground(tone)),
        BorderBrush = Brush(Tones.Edge(tone)),

        // The left rule, not a box: it reads as a margin note against the cards it sits between,
        // and an outlined rectangle inside another outlined rectangle reads as a dialog.
        BorderThickness = new Avalonia.Thickness(3, 0, 0, 0),
        CornerRadius = new Avalonia.CornerRadius(4),
        Padding = new Avalonia.Thickness(12, 9),
        Child = content,
    };
}
