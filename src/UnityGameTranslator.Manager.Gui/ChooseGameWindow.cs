using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// Change on a game's card: which game this is, confirmed with the site — the publication's own
/// block (<see cref="GamePickerBlock"/>), nothing else.
///
/// 🔴 **What it decides is kept in the game** (`config.json`, `game_choice`, user 2026-10-05): the
/// first publication sends it, and Community searches with it when the detection finds nothing.
/// Only an answer of the site's list is a choice — a game nobody can identify is none.
///
/// 🔴 **Validated by Apply (1), like every other choice in this program** (manager-ui.md §1, and
/// the user's words of 2026-10-05: "pourquoi ce n'est pas sur apply comme tout le reste ?"). It
/// was "Select" and wrote at once — a third way of validating, beside Apply (N) and the quick
/// actions. Apply stays greyed and without a count while the game picked is the one already
/// confirmed; closing the window keeps nothing.
/// </summary>
internal sealed class ChooseGameWindow : Window
{
    private readonly GamePickerBlock _picker;

    /// <summary>The game already confirmed in this game: picking it again is nothing to apply.</summary>
    private readonly GameChoice? _current;
    private readonly Button _select;
    private readonly TextBlock _complaint;
    private bool _chosen;

    private ChooseGameWindow(GameToConfirm game)
    {
        _current = game.Confirmed;
        Title = "Game";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = this.FindResource("SurfaceBase") as IBrush;

        var layout = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        layout.Children.Add(new TextBlock
        {
            Text = "Which game is this?",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = this.FindResource("TextPrimary") as IBrush,
        });

        // What the answer is for, in one sentence: the fact, not the mechanism.
        layout.Children.Add(new TextBlock
        {
            Text = "Used to publish this game's translation and to find translations shared for it.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = this.FindResource("TextSecondary") as IBrush,
        });

        _picker = new GamePickerBlock(game, askAdult: false,
                                      key => this.FindResource(key) as IBrush, () => Judge());
        foreach (var control in _picker.Controls) layout.Children.Add(control);

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

        _select = new Button { Content = "Apply", Classes = { "primary" } };
        _select.Click += (_, _) =>
        {
            if (!Judge()) return;
            _chosen = true;
            Close();
        };
        buttons.Children.Add(_select);
        layout.Children.Add(buttons);

        Content = layout;
        Judge();

        Avalonia.Threading.Dispatcher.UIThread.Post(async () => await _picker.StartAsync());
    }

    /// <summary>
    /// Whether there is something to apply — a game picked, which can be kept, and not the one
    /// already confirmed — saying why when it cannot be kept.
    /// </summary>
    private bool Judge()
    {
        var complaint = _picker.Complaint;
        _complaint.Text = complaint ?? "";
        _complaint.IsVisible = complaint is not null;

        // By the game, not by the row's source: the game already confirmed, clicked again on
        // another of its rows, is nothing to apply (Common.GameChoices.Holds).
        bool differs = complaint is null && _picker.Confirmed?.Pick is not null && !_picker.Holds(_current);

        _select.Content = differs ? "Apply (1)" : "Apply";
        _select.IsEnabled = differs;
        return differs;
    }

    /// <summary>The game chosen, or null when the window was closed without choosing.</summary>
    public static async Task<GameChoice?> AskAsync(Window owner, GameToConfirm game)
    {
        var window = new ChooseGameWindow(game);
        await window.ShowDialog(owner);

        return window._chosen && window._picker.Confirmed is { Pick: { } pick } confirmed
            ? new GameChoice(pick.Source, pick.Id, confirmed.Name)
            : null;
    }
}
