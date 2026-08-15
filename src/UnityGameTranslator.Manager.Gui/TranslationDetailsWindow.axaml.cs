using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>What was said about a published translation, next to the file itself.</summary>
/// <param name="Saved">False when the window was closed without agreeing.</param>
/// <param name="Notes">The description, empty to clear it. Never null once saved.</param>
/// <param name="ResourcesUrl">The link, empty to clear it. Never null once saved.</param>
/// <param name="Finished">The author's declaration. Meaningless on a branch — see the window.</param>
public readonly record struct TranslationDetails(bool Saved, string Notes, string ResourcesUrl,
                                                 bool Finished);

/// <summary>
/// The things said ABOUT a translation rather than in it: what it is, where to find the fonts or
/// images it needs, and whether its author calls it finished.
///
/// 🔴 **Reachable with nothing to publish, and that is the point.** These are exactly the edits
/// that come after the work — a clearer description, a link that moved, a translation its author
/// now considers done. Tying them to an upload meant they could only be made by having something
/// else to send.
///
/// ⚠ **A contribution does not declare itself finished.** A branch inherits its Main's status, the
/// server enforces it, and the other two products say so in these words. What is shown here is the
/// sentence, not a switch that would be discarded on arrival — the ecosystem rule: the same fact
/// reads the same way in all three.
///
/// ⚠ **Everything else on a branch IS editable**, and deliberately so. Proposing a better
/// description, or the link to the font pack the contribution needs, is contributing.
/// </summary>
public sealed class TranslationDetailsWindow : Window
{
    /// <summary>Matches the endpoint's own limits, so a refusal is never the first feedback.</summary>
    private const int NotesLimit = 1000;
    private const int UrlLimit = 2048;

    private readonly TextBox _notes;
    private readonly TextBox _url;
    private readonly CheckBox? _finished;
    private readonly TextBlock _complaint;
    private readonly Button _save;

    private bool _saved;

    private TranslationDetailsWindow(string heading, string notes, string url,
                                     bool finished, bool onABranch)
    {
        Title = "Translation details";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = this.FindResource("SurfaceBase") as IBrush;

        var layout = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        layout.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.FindResource("TextPrimary") as IBrush,
        });

        layout.Children.Add(Label("Description"));
        _notes = new TextBox
        {
            Text = notes,
            MaxLength = NotesLimit,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            Watermark = "What this translation covers, what it does not, who it is for.",
        };
        layout.Children.Add(_notes);

        layout.Children.Add(Label("Link to fonts or images"));
        _url = new TextBox
        {
            Text = url,
            MaxLength = UrlLimit,
            Watermark = "https://…",
        };
        layout.Children.Add(_url);

        layout.Children.Add(new TextBlock
        {
            Text = "Optional. Some translations need a font or replacement images that cannot "
                 + "travel inside the file; this is where players are told to find them.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.FindResource("TextMuted") as IBrush,
        });

        if (onABranch)
        {
            // ⚠ The mod's words, to the letter. Two products explaining one server rule differently
            // is how somebody concludes they behave differently.
            layout.Children.Add(new TextBlock
            {
                Text = "Whether this is finished is the Main's to say — your contribution "
                     + "inherits it.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = this.FindResource("TextMuted") as IBrush,
            });
        }
        else
        {
            _finished = new CheckBox
            {
                Content = "This translation is finished",
                IsChecked = finished,
                Foreground = this.FindResource("TextPrimary") as IBrush,
            };
            layout.Children.Add(_finished);
        }

        // Said above the buttons rather than after a refusal: a URL the site will reject is worth
        // knowing about while the field is still in front of you.
        _complaint = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Foreground = this.FindResource("StatusWarning") as IBrush,
        };
        layout.Children.Add(_complaint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);

        _save = new Button { Content = "Save", Classes = { "primary" } };
        _save.Click += (_, _) =>
        {
            if (!Acceptable()) return;
            _saved = true;
            Close();
        };
        buttons.Children.Add(_save);

        layout.Children.Add(buttons);

        // Re-judged as it is typed, so the refusal disappears the moment it stops being true.
        _url.TextChanged += (_, _) => Acceptable();

        Content = layout;
    }

    /// <summary>
    /// Whether what is in the fields can be sent, saying why when it cannot.
    ///
    /// ⚠ Only the link can be wrong here, and only in one way the server would refuse outright:
    /// something that is not an http(s) address. Anything stricter would be this window inventing
    /// a rule the site does not have.
    /// </summary>
    private bool Acceptable()
    {
        var url = _url.Text?.Trim() ?? "";

        var ok = url.Length == 0
                 || (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                     && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps));

        _complaint.Text = ok ? "" : "The link has to be a full web address, starting with https://";
        _complaint.IsVisible = !ok;
        _save.IsEnabled = ok;
        return ok;
    }

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        Foreground = this.FindResource("TextSecondary") as IBrush,
    };

    /// <summary>
    /// Opens the window on what the server currently holds, and returns what the author decided.
    ///
    /// ⚠ The values passed in must come from the SERVER's answer, not from anything remembered
    /// here: this window's whole output is sent back as the new truth, so opening it on a stale
    /// description would quietly restore it.
    /// </summary>
    public static async Task<TranslationDetails> EditAsync(
        Window owner, string heading, string? notes, string? resourcesUrl,
        bool finished, bool onABranch)
    {
        var window = new TranslationDetailsWindow(heading, notes ?? "", resourcesUrl ?? "",
                                                  finished, onABranch);
        await window.ShowDialog(owner);

        return new TranslationDetails(
            window._saved,
            window._notes.Text?.Trim() ?? "",
            window._url.Text?.Trim() ?? "",
            window._finished?.IsChecked == true);
    }
}
