using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The shapes every window uses to put words on screen — one copy, so a card, a note or a warning
/// looks the same wherever it appears.
///
/// 🔴 **Why one file.** Each window used to carry its own Card, Note and Row, and the copies had
/// drifted: Mod defaults gave a card's intro 12px in the secondary grey, the tool's settings 11px in
/// the muted one, with different padding. A reader learns what a shape means once and then stops
/// reading it — which only works if the shape means the same thing on every screen.
///
/// 🔴 **Colour goes through <see cref="Tone"/>, never a brush name.** A note used to take its colour
/// as a string, so a warning could be written in the grey of an explanation and nothing would
/// notice. Picking a tone is picking what the text engages — see Tone.cs — and a warning is never
/// grey. The grammar, with what each shape is for: analyse/manager-passe-textes.md §2.
/// </summary>
internal static class Ui
{
    /// <summary>Through Palette, which will not let an unknown key pass unnoticed.</summary>
    public static IBrush? Brush(string key) => Palette.Of(key);

    /// <summary>The text colour of a tone. Neutral is the muted grey of an explanation.</summary>
    public static string TextColour(Tone tone) => tone switch
    {
        Tone.Error => "StatusError",
        Tone.Warning => "StatusWarning",
        Tone.Info => "StatusInfo",
        Tone.Success => "StatusSuccess",
        _ => "TextMuted",
    };

    /// <summary>
    /// A titled card: the site's gray-800 block with its title, an optional one-sentence intro saying
    /// what the card is for, then its content.
    /// </summary>
    public static Control Card(string title, string? intro, Control content)
    {
        var body = new StackPanel { Spacing = 10 };

        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
        });

        if (intro is not null) body.Children.Add(Intro(intro));

        body.Children.Add(content);

        return Card(body);
    }

    /// <summary>The card's block alone, around content that brings its own heading.</summary>
    public static Control Card(Control content) => new Border
    {
        Background = Brush("SurfaceCard"),
        BorderBrush = Brush("BorderSubtle"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(18, 15),
        Child = content,
    };

    /// <summary>What a card or a window is for, under its title. One sentence, two at most.</summary>
    public static TextBlock Intro(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("TextSecondary"),
    };

    /// <summary>
    /// A line under a control: what it does (Neutral), or what to watch out for (Warning, and then
    /// amber — never the grey of an explanation).
    /// </summary>
    public static TextBlock Note(string text, Tone tone = Tone.Neutral) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(TextColour(tone)),
    };

    /// <summary>
    /// Rewrites a live status line — a test, a search, a download — in the colour of what it now
    /// says: Neutral while something is happening, Success when it worked, Warning when it did not
    /// but nothing is broken, Error when it cannot work.
    /// </summary>
    public static void Say(TextBlock line, string text, Tone tone = Tone.Neutral)
    {
        line.Text = text;
        line.Foreground = Brush(TextColour(tone));
        line.IsVisible = text.Length > 0;
    }

    /// <summary>A labelled row of a form: the label in a fixed column, then the controls.</summary>
    public static Control Row(string label, params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 130,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextMuted"),
        });
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    /// <summary>
    /// A notice that stands on its own and asks the reader to stop: tinted in its tone, with a
    /// coloured rule down the left edge.
    /// </summary>
    public static Control Callout(string text, Tone tone) =>
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
    /// ⚠ One shape for every notice, and it had drifted into three: the blockers used this, the
    /// configuration differences built their own Border with a full outline and a different radius,
    /// and the newest warnings were dressed as plain cards, which made a problem look like a section.
    /// A notice is recognised by its edge before it is read; three edges mean nothing is recognised.
    ///
    /// The left rule, not a box: it reads as a margin note against the cards it sits between, and an
    /// outlined rectangle inside another outlined rectangle reads as a dialog.
    /// </summary>
    public static Control Callout(Control content, Tone tone) => new Border
    {
        Background = Brush(Tones.CalloutBackground(tone)),
        BorderBrush = Brush(Tones.Edge(tone)),
        BorderThickness = new Thickness(3, 0, 0, 0),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(12, 9),
        Child = content,
    };
}
