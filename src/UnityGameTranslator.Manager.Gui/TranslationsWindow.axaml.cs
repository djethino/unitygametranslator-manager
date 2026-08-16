using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UnityGameTranslator.Manager.Core.Api;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Install;
using UnityGameTranslator.Manager.Core.Model;
using UnityGameTranslator.Manager.Core.Platform;
using UnityGameTranslator.Manager.Core.Settings;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The community translations for one game, and the one place to take one.
///
/// A list rather than a best pick, and that is a decision rather than a shortcut: several
/// translations can exist for the same game AND the same target language, differing only by the
/// language they were translated FROM. No automatic choice is defensible when the thing that
/// separates two entries is invisible unless you show it.
///
/// ⚠ **The order comes from the server and is not touched.** Translation::ranking_score already
/// normalises by the best score of the game — otherwise, in a catalogue whose top score is a
/// single vote, one self-vote outranks everything nobody thought to vote for — and it excludes
/// branches. Re-sorting here would produce a different order from the website for the same data:
/// two truths, and the reader in the middle. The mod's algorithm is not copied either; three
/// copies drift faster than two.
///
/// Cards show what the mod's own list shows, in the same order of importance: the verdict first,
/// the size second. "Has anyone read this" is what separates two translations; the line count only
/// qualifies it.
/// </summary>
public sealed class TranslationsWindow : Window
{
    private readonly GameReport _report;

    /// <summary>
    /// Where this game keeps its translation, resolved by the caller from the catalog.
    ///
    /// Passed in rather than worked out here: a detected loader carries its plugin folder, but the
    /// folder a translation lives in is a separate entry in the catalog and only the caller holds
    /// it. Guessing that they are the same would write the file where the mod never looks.
    /// </summary>
    private readonly LoaderDescriptor _loader;
    private readonly CatalogApiClient _api = new();

    private readonly SettingsStore _settings;

    /// <summary>
    /// Every lineage this account takes part in — not just the one the local file belongs to.
    ///
    /// GameReport.MyPosition answers for the installed file alone, which is enough on the game card
    /// and wrong here: this list shows every translation of the game, and one of them may be a
    /// published translation of yours under a lineage you do not currently have on disk. Asked per
    /// card, from the same single fetch.
    /// </summary>
    private readonly AccountLineages _lineages;

    private StackPanel _list = null!;
    private TextBlock _status = null!;
    private ComboBox _target = null!;
    private ComboBox _source = null!;
    private SpinningGear _searching = null!;

    /// <summary>Everything published for this game, whatever the languages. Feeds the pickers.</summary>
    private IReadOnlyList<OnlineTranslation> _everything = Array.Empty<OnlineTranslation>();

    /// <summary>True when something was written, so the caller can refresh the game card.</summary>
    public bool Changed { get; private set; }

    /// <summary>
    /// Where a choice made here is remembered — which is all this window does with it.
    ///
    /// ⚠ Nothing is written into a game from here, and that is the rule rather than a limitation
    /// of the moment. Choosing needs the translations side by side; acting needs what the game
    /// already carries, which only its card knows. This window used to do both, so the same button
    /// meant "select" or "download now" depending on the state of the game behind it, and a
    /// replacement was weighed in two places with two sets of warnings.
    /// </summary>
    private readonly GamePreferences _preferences;

    /// <summary>
    /// Opened with every language shown, rather than filtered on the one this game uses.
    ///
    /// Asked for by the card, which offers two doors into this same list: the everyday one, and
    /// this — for a game with nothing in your language, where the answer is somebody else's.
    /// </summary>
    private readonly bool _anyLanguage;

    public TranslationsWindow(GameReport report, LoaderDescriptor loader, SettingsStore settings,
                              AccountLineages lineages, GamePreferences preferences,
                              bool anyLanguage = false)
    {
        _anyLanguage = anyLanguage;
        _report = report;
        _loader = loader;
        _settings = settings;
        _lineages = lineages;
        _preferences = preferences;

        Title = $"Translations for {report.Game.Name}";
        Width = 860;
        Height = 720;
        MinWidth = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.FindResource("SurfaceBase") as IBrush;

        Content = Build();
    }

    private Control Build()
    {
        var layout = new StackPanel { Spacing = 12, Margin = new Thickness(24) };

        _status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        };
        layout.Children.Add(_status);

        layout.Children.Add(LocalState());
        layout.Children.Add(Filters());

        _searching = new SpinningGear("Asking the site...") { IsVisible = false };
        layout.Children.Add(_searching);

        _list = new StackPanel { Spacing = 10 };
        layout.Children.Add(_list);

        // What the game scan already found, shown at once. The pickers are built from it, so they
        // only ever offer languages this game actually has — a list of every language on earth
        // would be a list of ways to get nothing.
        _everything = Collect();
        ApplyDefaults();
        ShowTranslations(_everything);

        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();

        var bar = new Border
        {
            Background = Brush("SurfaceBar"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 12),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { close },
            },
        };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = layout });

        return root;
    }

    /// <summary>
    /// What is already in the game, stated before the list rather than after.
    ///
    /// It is the thing that decides whether taking one of these is a gain or a loss, so it belongs
    /// above them, not as a footnote under a row of tempting buttons.
    /// </summary>
    private Control LocalState()
    {
        var local = _report.LocalTranslation;

        // Same reason as on the game card: the pair is what makes the list below comparable.
        var languages = LocalTranslationProbe.DescribeLanguages(_report.Game.Path, _loader);
        var pair = languages is null ? "" : $" ({languages})";

        var text = local is null
            ? "Nothing is installed for this game yet, so taking one costs you nothing."
            : local.EntryCount < 0
                ? "There is a translation file here, but it could not be read. Taking another one "
                  + "will move it aside rather than delete it."
                : local.LocalChanges > 0
                    ? $"You already have {local.EntryCount} lines here{pair}, and {local.LocalChanges} of "
                      + "them have not been uploaded anywhere. Taking another translation replaces "
                      + "the file — your copy is kept aside, but the mod is where you merge the two."
                    : $"You already have {local.EntryCount} lines here{pair}, with nothing waiting to be "
                      + "uploaded.";

        var card = new StackPanel { Spacing = 4 };

        card.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(local?.LocalChanges > 0 ? "StatusWarning" : "TextSecondary"),
        });

        // Where the account stands in this very lineage, in the same words the game card uses —
        // it changes what "replacing this file" means. Overwriting a stranger's translation costs
        // a download; overwriting the Main one publishes costs the only copy of work other people
        // are contributing to.
        if (_report.MyPosition is { } position)
        {
            card.Children.Add(new TextBlock
            {
                Text = position.Describe(_report.MatchingOnline?.Author),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(position.IsMain ? "StatusSuccess" : "StatusWarning"),
            });

            if (position.MainMissing == true)
            {
                card.Children.Add(new TextBlock
                {
                    Text = LineagePosition.OrphanNote,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.9,
                    Foreground = Brush("StatusWarning"),
                });
            }
        }

        return new Border
        {
            Background = Brush("SurfaceCard"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Child = card,
        };
    }

    /// <summary>
    /// The two pickers, in the website's own shape: target language and source language, each
    /// with an "any".
    ///
    /// Both matter, and the second is not a refinement: a game can ship in several regional
    /// editions whose in-game text is not the same language, so "French" alone does not say which
    /// original a translation was made from — and taking one made from a text your copy does not
    /// contain gives a file that matches nothing.
    ///
    /// The target starts on the language configured in Mod defaults rather than on "any". Someone
    /// opening this screen wants what they can play, not a catalogue; the "any" entry is there for
    /// the case that actually happens — no translation in your language, and English will do.
    /// </summary>
    private Control Filters()
    {
        _target = new ComboBox { Width = 220 };
        _source = new ComboBox { Width = 220 };

        _target.SelectionChanged += async (_, _) => await SearchAsync();
        _source.SelectionChanged += async (_, _) => await SearchAsync();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        row.Children.Add(Labelled("Into", _target));
        row.Children.Add(Labelled("From", _source));

        return row;
    }

    private static Control Labelled(string label, Control control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Brush("TextMuted"),
        });
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>Everything the scan found for this game, matching translation first.</summary>
    private IReadOnlyList<OnlineTranslation> Collect()
    {
        var all = new List<OnlineTranslation>();
        if (_report.MatchingOnline is { } mine) all.Add(mine);
        all.AddRange(_report.AlternativeOnline);
        return all;
    }

    /// <summary>
    /// Fills both pickers from the languages this game has, and preselects the configured one.
    ///
    /// ⚠ When nothing exists in that language the filter falls back to "any" and says so. Leaving
    /// it selected would show an empty screen for a game that does have translations, and an empty
    /// screen reads as "nothing here", not as "nothing in French".
    /// </summary>
    private void ApplyDefaults()
    {
        Fill(_target, _everything.Select(t => t.TargetLanguage));
        Fill(_source, _everything.Select(t => t.SourceLanguage));

        // What this game is ALREADY doing wins over the global default, and that is the point:
        // someone who took an English translation for a Japanese game — because none existed in
        // their own language — must land back on the list that contains it. Opening on the global
        // default would hide the very translation they are running and read as "it is gone".
        var (gameSource, gameTarget) = LocalTranslationProbe.ReadLanguages(_report.Game.Path, _loader);

        // The lineage in use, when the site knows it, is the most precise answer of all: it names
        // both languages of the exact file installed, which a config can only approximate.
        var installedSource = _report.MatchingOnline?.SourceLanguage;
        var installedTarget = _report.MatchingOnline?.TargetLanguage;

        var target = installedTarget ?? gameTarget
                     ?? Languages.NameOf(_settings.ResolveTargetLanguage());

        var source = installedSource ?? gameSource;

        var hasTarget = !_anyLanguage && _everything.Any(t =>
            string.Equals(t.TargetLanguage, target, StringComparison.OrdinalIgnoreCase));

        Select(_target, hasTarget ? target : null);

        // Only kept when it would still leave something to look at: a source filter inherited
        // from an installed file, matching nothing on the server, would empty the screen for a
        // reason nobody could see.
        var hasSource = source is not null && _everything.Any(t =>
            string.Equals(t.SourceLanguage, source, StringComparison.OrdinalIgnoreCase));

        Select(_source, hasSource ? source : null);

        if (!hasTarget && _everything.Count > 0)
        {
            _status.Text = $"Nothing in {target} for this game yet, so every language is shown. "
                         + "Taking one in another language is a normal thing to do — the screen "
                         + "will offer to point the game at it.";
        }
    }

    /// <summary>
    /// ⚠ These filters carry the language NAME as their value, not a code — they are built from
    /// what this game actually has published, and the translations name their languages. Hence the
    /// (name, name) pair below rather than a lookup: the code would be an extra thing to resolve
    /// and to get wrong, for a picker whose value never leaves this window.
    /// </summary>
    private static void Fill(ComboBox box, IEnumerable<string?> languages)
    {
        LanguageMark.Fill(box,
            languages
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                .Select(l => (l, l)),
            new LanguageChoice("", null, "Any language"));
    }

    /// <summary>The language a filter is on, or null for "any".</summary>
    private static string? Chosen(ComboBox box)
    {
        var code = (box.SelectedItem as LanguageChoice)?.Code;
        return string.IsNullOrEmpty(code) ? null : code;
    }

    private static void Select(ComboBox box, string? language)
    {
        // "" is the "any language" entry; null asks for it.
        var wanted = language ?? "";

        foreach (var item in box.Items.OfType<LanguageChoice>())
        {
            if (string.Equals(item.Code, wanted, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }

        box.SelectedItem = box.Items.Count > 0 ? box.Items[0] : null;
    }

    /// <summary>
    /// Asks the server again with the chosen languages.
    ///
    /// Filtered by the server, not here, because the search returns at most fifty results after
    /// ranking: on a heavily translated game, filtering a top-fifty taken across all languages
    /// could hide French ones that never made that list.
    /// </summary>
    private async Task SearchAsync()
    {
        // ⚠ "" is the "any language" entry, and the API wants null for it — an empty string would
        // be sent as a filter on a language nobody is called.
        var target = Chosen(_target);
        var source = Chosen(_source);

        _searching.IsVisible = true;
        _list.Children.Clear();

        var found = _report.Game.SteamAppId is { } steamId
            ? await _api.SearchBySteamIdAsync(steamId, target, source, ApiToken())
            : await _api.SearchByNameAsync(_report.Game.Name, target, source, ApiToken());

        _searching.IsVisible = false;

        if (_api.LastError is not null)
        {
            _status.Text = _api.LastError;
            return;
        }

        ShowTranslations(found);
    }

    /// <summary>
    /// What is on screen right now, kept so a selection can redraw the cards without asking the
    /// server again — every card carries the current choice in its own button, so one of them
    /// changing means all of them have to.
    /// </summary>
    private IReadOnlyList<OnlineTranslation> _shown = Array.Empty<OnlineTranslation>();

    private void Redraw() => ShowTranslations(_shown);

    private void ShowTranslations(IReadOnlyList<OnlineTranslation> all)
    {
        _shown = all;
        _list.Children.Clear();

        if (all.Count == 0)
        {
            _status.Text = _report.OnlineSearchError is not null
                ? $"Could not reach the community site ({_report.OnlineSearchError})."
                : "Nobody has published a translation for this game yet — the mod builds one as you "
                  + "play, and you can be the first to share it.";
            return;
        }

        _status.Text = $"{all.Count} translation(s) for this game, in the order the site ranks them.";

        foreach (var translation in all) _list.Children.Add(Card(translation, all));
    }

    /// <summary>
    /// One translation, laid out as the mod lays it out: languages, then who and what is notable,
    /// then the measures, then what it is made of.
    /// </summary>
    private Control Card(OnlineTranslation translation, IReadOnlyList<OnlineTranslation> all)
    {
        var installed = _report.LocalTranslation?.Uuid is { } localUuid
                        && string.Equals(localUuid, translation.Uuid, StringComparison.OrdinalIgnoreCase);

        var body = new StackPanel { Spacing = 4 };

        // The pair of languages is what a reader scans for; the author is context. Source matters
        // as much as target here — it is sometimes the only thing separating two entries.
        //
        // ⚠ Flags AND words, never one or the other. A flag is faster to find in a list and cannot
        // always name the language on its own — ten Indian languages share one — so the words stay
        // and the pictures lead.
        var pair = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Each flag beside the language it names, once: "GB English -> FR French". Naming them
        // twice — two flags, then the two names — is what this replaces.
        pair.Children.Add(LanguageMark.Named(translation.SourceLanguage,
                                             translation.SourceLanguage ?? "?"));
        pair.Children.Add(new TextBlock
        {
            Text = "→",
            FontSize = 14,
            Foreground = Brush("TextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        pair.Children.Add(LanguageMark.Named(translation.TargetLanguage,
                                             translation.TargetLanguage ?? "?"));

        body.Children.Add(pair);

        // Where this account stands on THIS card, which "installed" does not answer: a file can sit
        // in the game without being one's own, and one's own can be published without being on this
        // machine at all.
        //
        // Matched on the site id, not on the lineage: a fork keeps the uuid of the work it came
        // from and is published too, so two cards here can share a lineage while belonging to two
        // different people. Matching on the uuid alone would hand somebody else's fork your name.
        var lineage = _lineages.For(translation.Uuid);
        var isYours = lineage is { SiteId: > 0 } && lineage.SiteId == translation.Id;

        // A branch of this lineage is never IN this list — branches are not published — so a
        // position that is not this card, on this card's lineage, is a contribution you made to it.
        var contributesHere = !isYours && lineage is { IsMain: false };

        // Badges work by being rare, and each says something written nowhere else on the card.
        var by = $"by {translation.Author ?? "unknown"}";
        if (isYours) by += "  ·  yours";
        if (contributesHere) by += "  ·  you have a branch of this";
        if (IsNew(translation)) by += "  ·  new";
        if (IsFurthest(translation, all)) by += "  ·  goes furthest";
        if (installed) by += "  ·  installed";

        body.Children.Add(new TextBlock
        {
            Text = by,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(isYours || contributesHere ? "StatusSuccess"
                               : installed ? "Accent"
                               : "TextSecondary"),
        });

        // Said on the card that carries them, rather than only on the game card: a Main owner
        // scrolling a list of six translations should not have to work out which one has people
        // waiting behind it.
        if (isYours && lineage is { BranchesCount: > 0 })
        {
            body.Children.Add(new TextBlock
            {
                Text = lineage.Describe(),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("StatusSuccess"),
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = Details(translation),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
        });

        if (QualityBar.HasSomethingToShow(translation))
        {
            // ⚠ The same strip as the game's page, from the same rules — otherwise the same file
            // gets two descriptions depending on which window it was opened in.
            body.Children.Add(TranslationBadges.ForOnline(translation, installed));
            body.Children.Add(new QualityBar(translation) { Margin = new Thickness(0, 4, 0, 2) });
            if (QualityBar.Legend(translation) is { } legend) body.Children.Add(legend);
        }

        if (!string.IsNullOrWhiteSpace(translation.Notes))
        {
            body.Children.Add(new TextBlock
            {
                Text = translation.Notes,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyle.Italic,
                Foreground = Brush("TextMuted"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        // ⚠ This window SELECTS. It does not write, and it used to — "Use this one" downloaded on
        // the spot whenever a mod happened to be installed, so the same screen meant two different
        // things depending on the state of the game behind it, and taking a translation happened
        // in two places with two sets of warnings.
        //
        // One rule now: choosing is done where translations can be compared, acting is done on the
        // game's card next to everything else that acts. The card is also the only place that can
        // weigh a replacement against what the game already carries.
        var selectedId = _preferences.Read(_report.Game.Path).TranslationId;
        var chosen = selectedId == translation.Id;

        // 🔴 **Two things can be taken from a lineage you contribute to, and only one was offered.**
        // The Main is what this card shows; your branch is your own work on it, published, never
        // listed here because branches are not public. Without this the only way back to your own
        // contribution was to publish over it or to fetch it from the website by hand.
        var ownBranchId = contributesHere && lineage is { SiteId: > 0 } ? lineage.SiteId : (int?)null;
        var branchChosen = ownBranchId is { } branch && selectedId == branch;

        var take = new Button
        {
            // Named only where there is something to tell it apart from. On every other card the
            // plain verb is right — inventing "Select the Main" everywhere would raise a question
            // about a distinction that does not exist there.
            Content = chosen ? "Selected"
                    : ownBranchId is not null ? "Select the Main"
                    : "Select",
            FontSize = 12,
            Classes = { "primary" },
            IsEnabled = !chosen,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var outcome = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        if (chosen)
        {
            Show(outcome, installed
                ? "This is the one in the game."
                : "Chosen. The game's card is where you install it.", "StatusSuccess");
        }

        take.Click += (_, _) => Select(translation.Id, outcome,
            "Selected. Setting this game up will bring it down.");

        body.Children.Add(take);

        if (ownBranchId is { } mine)
        {
            var takeMine = new Button
            {
                Content = branchChosen ? "Your contribution is selected" : "Select your contribution",
                FontSize = 12,
                IsEnabled = !branchChosen,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
            };

            ToolTip.SetTip(takeMine,
                "Your own work on this lineage, as you published it. The Main above is what its "
                + "owner has kept; the two differ by whatever has not been merged yet.");

            takeMine.Click += (_, _) => Select(mine, outcome,
                "Your contribution is selected. Setting this game up will bring it down.");

            body.Children.Add(takeMine);
        }

        body.Children.Add(outcome);

        return new Border
        {
            Background = Brush("SurfaceCard"),
            BorderBrush = Brush(installed ? "AccentEdge" : "BorderSubtle"),
            BorderThickness = new Thickness(installed ? 2 : 1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Child = body,
        };
    }

    /// <summary>
    /// The measures, verdict first. Completeness answers "is it finished", coverage answers "how
    /// much of the game does it reach", and the line count only qualifies both.
    /// </summary>
    private static string Details(OnlineTranslation translation)
    {
        var parts = new List<string>();

        if (translation.Completeness is { } completeness)
            parts.Add($"{completeness * 100:F0}% translated");

        if (translation.ReviewCoverage is { } review && review > 0)
            parts.Add($"{review * 100:F0}% reviewed by a human");

        parts.Add($"{translation.LineCount} lines");

        // Coverage is relative to the other translations of this game, so it is worded as a
        // comparison rather than as a share of the game — the game's real size is unknowable.
        if (translation.GameCoverage is { } coverage && coverage < 0.999)
            parts.Add($"reaches {coverage * 100:F0}% of what the best one reaches");

        if (translation.DownloadCount > 0) parts.Add($"{translation.DownloadCount} downloads");

        // content_updated_at, never updated_at: a vote or a download moves the latter, which would
        // dress an abandoned file up as freshly maintained.
        if (translation.ContentUpdatedAt is { } date)
            parts.Add($"last changed {date.LocalDateTime:yyyy-MM-dd}");

        return string.Join("  ·  ", parts);
    }

    /// <summary>Published within the last week — the website's reckoning, and the mod's.</summary>
    private static bool IsNew(OnlineTranslation translation) =>
        translation.CreatedAt is { } created
        && (DateTimeOffset.UtcNow - created).TotalDays <= 7;

    /// <summary>
    /// Nobody has gone further with this game.
    ///
    /// Silent when it has no rival in the list: being furthest alone is a race of one, and saying
    /// so would dress a lack of competition up as an achievement. Same rule as the mod.
    /// </summary>
    private static bool IsFurthest(OnlineTranslation translation, IReadOnlyList<OnlineTranslation> all)
    {
        if (translation.GameCoverage is not { } coverage || coverage < 0.999) return false;

        return all.Count(other => other.Id != translation.Id) > 0;
    }

    /// <summary>
    /// Remembers which translation this game should be set up with, and writes nothing.
    ///
    /// No confirmation, and none is owed: nothing on disk changes, and choosing another card
    /// undoes it. The moment something IS at stake — a file already in the game — is the moment
    /// the one-click asks, with the file in front of it to describe.
    /// </summary>
    private void Select(int translationId, TextBlock outcome, string message)
    {
        var preference = _preferences.Read(_report.Game.Path);
        preference.TranslationId = translationId;
        preference.InstallTranslation = true;
        _preferences.Set(_report.Game.Path, preference);

        Changed = true;

        Show(outcome, message, "StatusSuccess");

        // The other cards carry the old selection in their own buttons, so the list is redrawn
        // rather than left with two of them claiming to be the chosen one.
        //
        // ⚠ The outcome line above is written before this, and this replaces the card that holds
        // it. Deliberate: the redrawn card says "Selected" on the button and repeats the sentence
        // from the preference, so nothing is lost — and leaving the old cards stale would be worse
        // than losing a line of text.
        Redraw();
    }

    /// <summary>
    /// The site token, when signed in.
    ///
    /// Sent on every download rather than only for branches: the endpoint is public for anything
    /// published and resolves the caller when a token is present, so passing it costs nothing and
    /// is what lets the author of a branch fetch their own work.
    /// </summary>
    private string? ApiToken() => _settings.Current.ApiToken;

    private static void Show(TextBlock block, string text, string colour)
    {
        block.Text = text;
        block.Foreground = Brush(colour);
        block.IsVisible = true;
    }

    /// <summary>Through Palette, which will not let an unknown key pass unnoticed.</summary>
    private static IBrush? Brush(string key) => Palette.Of(key);
}
