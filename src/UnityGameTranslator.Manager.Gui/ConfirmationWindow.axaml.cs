using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// A question with consequences spelled out, and two answers.
///
/// Deliberately not a yes/no box. "Are you sure?" is a question nobody can answer usefully: the
/// person asking already knows what is at stake and the person answering does not, so they click
/// yes. Every caller here passes what stands to be lost, in figures — "42 lines that exist nowhere
/// else" — because that is the only form in which the answer means anything.
///
/// Cancel is the default. Someone hitting Enter to make a dialog go away must not thereby agree to
/// replace their own work.
/// </summary>
public sealed class ConfirmationWindow : Window
{
    private bool _confirmed;

    /// <summary>
    /// A choice made ALONGSIDE the answer, when it belongs to the same act.
    ///
    /// ⚠ Not a second dialog and not a third button: "publish" and "say it is finished" are one
    /// decision taken at one moment, and splitting them would ask twice about one intention. Null
    /// when the question has no such choice, which is nearly always.
    /// </summary>
    private CheckBox? _option;

    /// <summary>Whether the option was ticked. False when there was none.</summary>
    private bool _optionChosen;

    private ConfirmationWindow(string title, string body, string confirm, bool question = true,
                               string? optionLabel = null, bool optionChecked = false)
    {
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = this.FindResource("SurfaceBase") as IBrush;

        var layout = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        layout.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.FindResource("TextPrimary") as IBrush,
        });

        layout.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.FindResource("TextSecondary") as IBrush,
        });

        if (optionLabel is not null)
        {
            _option = new CheckBox
            {
                Content = optionLabel,
                IsChecked = optionChecked,
                Foreground = this.FindResource("TextPrimary") as IBrush,
            };
            layout.Children.Add(_option);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (question)
        {
            // IsCancel and IsDefault both on Cancel: Escape closes it, and so does Enter. The
            // destructive answer is only ever reached by aiming at it.
            var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
        }

        // ⚠ On a statement there is nothing to aim at, so this one IS the default and the escape:
        // making somebody hunt for the way out of a message that only reports something would be
        // the mirror of the rule above, applied where it protects nobody.
        var go = new Button
        {
            Content = confirm,
            Classes = { "primary" },
            IsDefault = !question,
            IsCancel = !question,
        };
        go.Click += (_, _) =>
        {
            _confirmed = true;
            _optionChosen = _option?.IsChecked == true;
            Close();
        };

        buttons.Children.Add(go);
        layout.Children.Add(buttons);

        Content = layout;
    }

    /// <summary>Shows the question and returns true only when the person aimed at the answer.</summary>
    public static async Task<bool> AskAsync(Window owner, string title, string body, string confirm)
    {
        var window = new ConfirmationWindow(title, body, confirm);
        await window.ShowDialog(owner);
        return window._confirmed;
    }

    /// <summary>
    /// The same question, carrying one choice that belongs to the same act.
    ///
    /// ⚠ The option's value is only meaningful when the answer is yes: reading it after a cancel
    /// would act on a decision somebody backed out of.
    /// </summary>
    public static async Task<(bool Agreed, bool Option)> AskAsync(
        Window owner, string title, string body, string confirm,
        string optionLabel, bool optionChecked)
    {
        var window = new ConfirmationWindow(title, body, confirm,
                                            optionLabel: optionLabel, optionChecked: optionChecked);
        await window.ShowDialog(owner);
        return (window._confirmed, window._confirmed && window._optionChosen);
    }

    /// <summary>
    /// States something and waits to be acknowledged. One button, because there is no choice to
    /// make — an outcome reported through a Cancel/OK pair invites somebody to look for the
    /// difference between them.
    /// </summary>
    public static async Task TellAsync(Window owner, string title, string body)
    {
        var window = new ConfirmationWindow(title, body, "Close", question: false);
        await window.ShowDialog(owner);
    }
}
