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

    public TranslationsWindow(GameReport report, LoaderDescriptor loader, SettingsStore settings,
                              AccountLineages lineages)
    {
        _report = report;
        _loader = loader;
        _settings = settings;
        _lineages = lineages;

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

        var hasTarget = _everything.Any(t =>
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

    private static void Fill(ComboBox box, IEnumerable<string?> languages)
    {
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = "Any language", Tag = null });

        foreach (var language in languages
                     .Where(l => !string.IsNullOrWhiteSpace(l))
                     .Select(l => l!)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(l => l, StringComparer.OrdinalIgnoreCase))
        {
            box.Items.Add(new ComboBoxItem { Content = language, Tag = language });
        }
    }

    private static void Select(ComboBox box, string? language)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag as string, language, StringComparison.OrdinalIgnoreCase))
            ?? box.Items.OfType<ComboBoxItem>().First();
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
        var target = (_target.SelectedItem as ComboBoxItem)?.Tag as string;
        var source = (_source.SelectedItem as ComboBoxItem)?.Tag as string;

        _searching.IsVisible = true;
        _list.Children.Clear();

        var found = _report.Game.SteamAppId is { } steamId
            ? await _api.SearchBySteamIdAsync(steamId, target, source)
            : await _api.SearchByNameAsync(_report.Game.Name, target, source);

        _searching.IsVisible = false;

        if (_api.LastError is not null)
        {
            _status.Text = _api.LastError;
            return;
        }

        ShowTranslations(found);
    }

    private void ShowTranslations(IReadOnlyList<OnlineTranslation> all)
    {
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
        body.Children.Add(new TextBlock
        {
            Text = $"{translation.SourceLanguage ?? "?"} → {translation.TargetLanguage ?? "?"}",
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Foreground = Brush("TextPrimary"),
        });

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

        var take = new Button
        {
            Content = installed ? "Download again" : "Use this one",
            FontSize = 12,
            Classes = { "primary" },
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var outcome = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        take.Click += async (_, _) => await TakeAsync(translation, take, outcome);

        body.Children.Add(take);
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
    /// Takes a translation: asks first when something stands to be replaced, then writes.
    ///
    /// The confirmation NAMES what is at stake rather than asking "are you sure?". Someone who is
    /// told "42 lines you have not uploaded anywhere" can decide; someone asked "are you sure"
    /// can only guess, and will click yes.
    /// </summary>
    private async Task TakeAsync(OnlineTranslation translation, Button button, TextBlock outcome)
    {
        if (!await ConfirmReplacementAsync(translation)) return;

        button.IsEnabled = false;
        button.Content = "Downloading...";

        // The token is sent when we have one: the endpoint is public for anything published, and
        // only a branch needs the caller identified. Today there is none — signing in is not built
        // yet — so this is simply null and public translations work regardless.
        var json = await _api.DownloadAsync(translation.Id, ApiToken());

        if (json is null)
        {
            button.IsEnabled = true;
            button.Content = "Use this one";
            Show(outcome, _api.LastError ?? "The download failed.", "StatusError");
            return;
        }

        var result = new TranslationInstaller()
            .Install(_report.Game.Path, _loader, json, translation.FileHash);

        button.IsEnabled = true;
        button.Content = "Download again";

        if (!result.Written)
        {
            Show(outcome, result.Failure ?? "It could not be written.", "StatusError");
            return;
        }

        Changed = true;

        // The file is in place; the game may still be aimed elsewhere. Asked after the download
        // rather than before, because it only matters once the file exists — and because saying
        // no must leave a working translation behind, not a cancelled operation.
        await OfferToAlignGameAsync(translation);

        var message = "Installed. The game will use it next time you launch it.";
        if (result.BackupPath is not null)
        {
            message += $" Your previous file was kept in {TranslationInstaller.BackupFolderName}/"
                     + $"{Path.GetFileName(result.BackupPath)}.";
        }

        Show(outcome, message, "StatusSuccess");
    }

    /// <summary>
    /// Asks before replacing, in terms of what is actually lost. True when we may go ahead.
    ///
    /// Nothing installed means nothing to ask about. And when the file that is there is provably
    /// the server's own, still untouched, the wording changes — but the backup does not: that
    /// proof rests on metadata that is sometimes missing.
    /// </summary>
    private async Task<bool> ConfirmReplacementAsync(OnlineTranslation translation)
    {
        var local = _report.LocalTranslation;
        if (local is null) return true;

        var recoverable = TranslationInstaller.LooksRecoverableOnline(local, translation);

        var what = local.LocalChanges > 0
            ? $"You have {local.LocalChanges} line(s) here that exist nowhere else — they have "
              + "never been uploaded. Replacing this file puts them out of the game's reach."
            : recoverable
                ? "The file you have is the one already published, unchanged, so you can take it "
                  + "again whenever you like."
                : $"You have {local.EntryCount} line(s) here. They will be replaced.";

        var keep = "A copy is kept in the removed folder either way, so nothing is deleted.";

        // Offered only when there is something to lose. Someone with no unsent work does not need
        // to be told about merging, and a caveat shown to everyone is a caveat nobody reads.
        var merge = local.LocalChanges > 0
            ? Environment.NewLine + Environment.NewLine
              + "To keep your work AND take this one, do it from the mod instead: it holds the "
              + "original version and the screens to settle line by line. This tool never merges."
            : "";

        return await ConfirmationWindow.AskAsync(this,
            "Replace the translation in this game?",
            what + " " + keep + merge,
            confirm: "Replace it");
    }

    /// <summary>
    /// Points the game at the language of the translation just taken, with permission.
    ///
    /// This is the case that actually happens: no translation in your language for a Japanese or
    /// Chinese game, so you take the English one. Without this the file lands in a game still set
    /// to French — the mod would ignore what you just installed and carry on translating into a
    /// language nobody provided, and nothing on screen would explain why.
    ///
    /// ⚠ Asked, never done silently. The target language is also what the mod uses to decide what
    /// to translate as you play, so changing it has consequences beyond this file — and someone
    /// running two games in two languages has a reason we cannot guess.
    /// </summary>
    private async Task OfferToAlignGameAsync(OnlineTranslation translation)
    {
        var taken = translation.TargetLanguage;
        if (string.IsNullOrWhiteSpace(taken)) return;

        // What the GAME is set to, not what this tool defaults to: they are allowed to differ, and
        // this one is what the mod will act on.
        var configured = LocalTranslationProbe.ReadTargetLanguage(_report.Game.Path, _loader);

        // No config yet means the install path will write our own default, which already matches
        // what this screen was filtered by. Nothing to reconcile.
        if (configured is null) return;
        if (string.Equals(configured, taken, StringComparison.OrdinalIgnoreCase)) return;

        var agreed = await ConfirmationWindow.AskAsync(this,
            $"Point the game at {taken}?",
            $"This game is set to {configured}, and the translation you just took is in {taken}. "
            + $"Left as it is, the mod will keep working towards {configured} and will not use the "
            + $"file you just installed."
            + Environment.NewLine + Environment.NewLine
            + $"Switching only changes this game. Your default stays {Languages.NameOf(_settings.ResolveTargetLanguage())}.",
            confirm: $"Use {taken} for this game");

        if (!agreed) return;

        var settings = _settings.Current;
        var previous = settings.TargetLanguage;

        try
        {
            // Written through the same merge as everything else, so the game keeps its token, its
            // secrets and every key we do not know about.
            settings.TargetLanguage = Languages.CodeOf(taken) ?? previous;
            new GameConfigWriter().Apply(_report.Game.Path, _loader, settings);
        }
        finally
        {
            // The global default is untouched: this was a decision about one game, and leaving it
            // changed would apply it to the next install without anyone asking.
            settings.TargetLanguage = previous;
        }
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
