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

    private ConfirmationWindow(string title, string body, string confirm)
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

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        // IsCancel and IsDefault both on Cancel: Escape closes it, and so does Enter. The
        // destructive answer is only ever reached by aiming at it.
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => Close();

        var go = new Button { Content = confirm, Classes = { "primary" } };
        go.Click += (_, _) => { _confirmed = true; Close(); };

        buttons.Children.Add(cancel);
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
}
