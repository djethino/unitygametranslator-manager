using Avalonia.Controls;
using Avalonia.Layout;
using UnityGameTranslator.Common;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The badge strip, drawn.
///
/// ⚠ **Everything decided is in <see cref="Badges"/>** — which chips, in which order, in what
/// words, and how loudly. This file only turns that into Avalonia controls, exactly as the mod
/// turns the same answers into uGUI. Anything decided here would be a decision the mod does not
/// share, and a strip that differs between two windows onto the same file is worse than none.
/// </summary>
public static class TranslationBadges
{
    /// <summary>
    /// A tone, in this program's palette.
    ///
    /// ⚠ Only keys that exist in ThemeResources. Naming one that does not renders in magenta and
    /// nothing else — which is exactly how "Your file" shipped unreadable, on a background that
    /// was never a colour anybody chose.
    /// </summary>
    public static string ToneKey(BadgeTone tone) => tone switch
    {
        BadgeTone.Good => "StatusSuccess",
        BadgeTone.Notice => "StatusInfo",
        BadgeTone.Attention => "StatusWarning",
        BadgeTone.Wrong => "StatusError",
        BadgeTone.Quiet => "TextMuted",
        _ => "TextSecondary",
    };

    private static Control Chip(Badge badge)
    {
        var chip = new Border
        {
            Background = Palette.Of("SurfaceInput"),
            BorderBrush = Palette.Of("BorderSubtle"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(3),
            Padding = new Avalonia.Thickness(7, 2),
            Child = new TextBlock
            {
                Text = badge.Text,
                FontSize = 11,
                Foreground = Palette.Of(ToneKey(badge.Tone)),
            },
        };

        ToolTip.SetTip(chip, badge.Tip);
        return chip;
    }

    private static Control Strip(System.Collections.Generic.List<Badge> badges)
    {
        // Wraps, because the strip is as long as the file is interesting and the window is as
        // narrow as somebody made it.
        var strip = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 5,
            LineSpacing = 5,
            Margin = new Avalonia.Thickness(0, 8, 0, 2),
        };

        foreach (var badge in badges) strip.Children.Add(Chip(badge));
        return strip;
    }

    /// <summary>The file on this machine, on a game's page.</summary>
    public static Control ForLocal(GameReport report, TagCounts counts)
    {
        var mine = report.MyPosition;
        var online = report.MatchingOnline;

        return Strip(Badges.For(
            publication: Publications.Of(hereOnDisk: true, onTheSite: mine is not null),
            isMain: mine?.IsMain,
            branchesWaiting: mine?.BranchesCount,
            mainMissing: mine?.MainMissing == true,
            sync: report.Sync,
            stage: counts.Stage,
            completeness: counts.Completeness,

            // ⚠ Votes and downloads are the SITE's tally, so they are read from the published
            // entry — never from the local file, which has none and would report zero as if it
            // were a result.
            votes: mine is not null && online is not null ? online.VoteCount : 0,
            downloads: mine is not null && online is not null ? online.DownloadCount : 0,

            // ⚠ The author's own word, from the published entry — a local file carries no such
            // declaration, and null shows nothing rather than inventing "still writing".
            finished: online?.Status is { } status
                ? string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase)
                : null,

            // The Main's other declaration. A local file carries no such thing, so it is read from
            // the site — this account's own row first, which is the lineage's own answer even when
            // the community search brought nothing back.
            acceptsContributions: mine?.AcceptsBranches ?? online?.AcceptsBranches));
    }

    /// <summary>A published translation, in the community list.</summary>
    public static Control ForOnline(OnlineTranslation translation, bool installed)
    {
        var counts = TagCounts.From(translation);

        return Strip(Badges.For(
            publication: Publications.Of(hereOnDisk: installed, onTheSite: true),

            // ⚠ Not said here. This window lists what OTHER people published; "Main" would be read
            // as a claim about the reader's own standing, which is a different question and one
            // this screen has no answer to.
            isMain: null,
            branchesWaiting: null,
            mainMissing: false,
            sync: null,
            stage: counts.Stage,
            completeness: translation.Completeness ?? counts.Completeness,
            votes: translation.VoteCount,
            downloads: translation.DownloadCount,

            // Its author's own word, which is as much part of "what this is" as the measurements
            // beside it — and the one thing here that nobody can work out from the file.
            finished: translation.Status is { } status
                ? string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase)
                : null,

            // Part of what a translation IS, and the one thing a would-be contributor has to know
            // before writing a line into it.
            acceptsContributions: translation.AcceptsBranches));
    }
}
