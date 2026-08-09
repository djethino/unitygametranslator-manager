using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UnityGameTranslator.Installer.Core.Api;
using UnityGameTranslator.Installer.Core.Install;
using UnityGameTranslator.Installer.Core.Model;
using UnityGameTranslator.Installer.Core.Platform;
using UnityGameTranslator.Installer.Core.Settings;

namespace UnityGameTranslator.Installer.Gui;

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

    private StackPanel _list = null!;
    private TextBlock _status = null!;

    /// <summary>True when something was written, so the caller can refresh the game card.</summary>
    public bool Changed { get; private set; }

    public TranslationsWindow(GameReport report, LoaderDescriptor loader)
    {
        _report = report;
        _loader = loader;

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

        _list = new StackPanel { Spacing = 10 };
        layout.Children.Add(_list);

        ShowTranslations();

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

        var text = local is null
            ? "Nothing is installed for this game yet, so taking one costs you nothing."
            : local.EntryCount < 0
                ? "There is a translation file here, but it could not be read. Taking another one "
                  + "will move it aside rather than delete it."
                : local.LocalChanges > 0
                    ? $"You already have {local.EntryCount} lines here, and {local.LocalChanges} of "
                      + "them have not been uploaded anywhere. Taking another translation replaces "
                      + "the file — your copy is kept aside, but the mod is where you merge the two."
                    : $"You already have {local.EntryCount} lines here, with nothing waiting to be "
                      + "uploaded.";

        return new Border
        {
            Background = Brush("SurfaceCard"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(local?.LocalChanges > 0 ? "StatusWarning" : "TextSecondary"),
            },
        };
    }

    private void ShowTranslations()
    {
        _list.Children.Clear();

        var all = new List<OnlineTranslation>();
        if (_report.MatchingOnline is { } mine) all.Add(mine);
        all.AddRange(_report.AlternativeOnline);

        if (all.Count == 0)
        {
            _status.Text = _report.OnlineSearchError is not null
                ? $"Could not reach the community site ({_report.OnlineSearchError})."
                : "Nobody has published a translation for this game yet. The mod can start one as "
                  + "you play, and you can share it afterwards.";
            return;
        }

        _status.Text = $"{all.Count} translation(s) published for this game, in the order the site "
                     + "ranks them.";

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

        // Badges work by being rare, so there are only three, and each says something written
        // nowhere else on the card.
        var by = $"by {translation.Author ?? "unknown"}";
        if (IsNew(translation)) by += "  ·  new";
        if (IsFurthest(translation, all)) by += "  ·  goes furthest";
        if (installed) by += "  ·  installed";

        body.Children.Add(new TextBlock
        {
            Text = by,
            FontSize = 12,
            Foreground = Brush(installed ? "Accent" : "TextSecondary"),
        });

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
    /// The site token, once signing in exists. Null until then, which is exactly what the public
    /// endpoints expect — so nothing here waits on that work.
    /// </summary>
    private string? ApiToken() => null;

    private static void Show(TextBlock block, string text, string colour)
    {
        block.Text = text;
        block.Foreground = Brush(colour);
        block.IsVisible = true;
    }

    private static IBrush? Brush(string key) => Application.Current?.FindResource(key) as IBrush;
}
