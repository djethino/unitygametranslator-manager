using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Common;
using static UnityGameTranslator.Common.EditScope;

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

    /// <summary>
    /// A second choice of the same nature, when publishing carries two declarations rather than
    /// one: whether the work is finished, and whether it takes contributions.
    ///
    /// ⚠ Two and no more. A confirmation that grows a list of settings has stopped being a
    /// confirmation — anything further belongs on the screen that publishes, not in the box that
    /// asks whether to.
    /// </summary>
    private CheckBox? _second;

    private bool _secondChosen;

    /// <param name="scope">
    /// Where agreeing to this would WRITE. Drawn by <see cref="ScopeMark.Marked"/> — inside the
    /// confirming button, exactly as on the page.
    ///
    /// 🔴 **This box is the last screen before the act, and it was the one without the mark.** The
    /// button that opens it says where it writes; the window that commits it said nothing — so the
    /// question "am I about to touch the site?" was asked out loud by somebody standing in front of
    /// a merge. Null on a box that writes nothing, which is most of them.
    /// </param>
    private ConfirmationWindow(string title, string body, string confirm, bool question = true,
                               string? optionLabel = null, bool optionChecked = false,
                               string? secondLabel = null, bool secondChecked = false,
                               string? secondHint = null, EditSide? scope = null)
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

        if (secondLabel is not null)
        {
            _second = new CheckBox
            {
                Content = secondLabel,
                IsChecked = secondChecked,
                Foreground = this.FindResource("TextPrimary") as IBrush,
            };
            layout.Children.Add(_second);

            // One line saying what the word means, because "contribution" is the only term here a
            // reader can meet for the first time — and this box is where they meet it.
            if (secondHint is not null)
            {
                layout.Children.Add(new TextBlock
                {
                    Text = secondHint,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24, -8, 0, 0),
                    Foreground = this.FindResource("TextMuted") as IBrush,
                });
            }
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
        //
        // ⚠ **The mark goes INSIDE this button, through ScopeMark.Marked — the module every other
        // button in this program uses.** A first attempt put it loose at the left of the row, which
        // placed it beside Cancel: the one control that writes nothing was the one wearing the sign
        // saying where the writing goes.
        var go = scope is { } side
            ? ScopeMark.Marked(side, confirm)
            : new Button { Content = confirm };

        go.Classes.Add("primary");
        go.IsDefault = !question;
        go.IsCancel = !question;
        go.Click += (_, _) =>
        {
            _confirmed = true;
            _optionChosen = _option?.IsChecked == true;
            _secondChosen = _second?.IsChecked == true;
            Close();
        };

        buttons.Children.Add(go);
        layout.Children.Add(buttons);

        Content = layout;
    }

    /// <summary>Shows the question and returns true only when the person aimed at the answer.</summary>
    public static async Task<bool> AskAsync(Window owner, string title, string body, string confirm,
                                            EditSide? scope = null)
    {
        var window = new ConfirmationWindow(title, body, confirm, scope: scope);
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
    /// The same question with the TWO declarations publishing carries: whether the work is
    /// finished, and whether it takes contributions.
    ///
    /// 🔴 Asked here rather than only on the details screen. A first publication is the one moment
    /// somebody is thinking about what they are putting out, and a decision they can only find
    /// afterwards is a decision taken for them — which is exactly how every translation published
    /// from this window would have ended up refusing its first contributor.
    /// </summary>
    public static async Task<(bool Agreed, bool Option, bool Second)> AskAsync(
        Window owner, string title, string body, string confirm,
        string optionLabel, bool optionChecked,
        string secondLabel, bool secondChecked, string? secondHint = null)
    {
        var window = new ConfirmationWindow(title, body, confirm,
                                            optionLabel: optionLabel, optionChecked: optionChecked,
                                            secondLabel: secondLabel, secondChecked: secondChecked,
                                            secondHint: secondHint);
        await window.ShowDialog(owner);
        return (window._confirmed,
                window._confirmed && window._optionChosen,
                window._confirmed && window._secondChosen);
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
