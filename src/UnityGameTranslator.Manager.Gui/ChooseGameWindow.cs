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
/// ⚠ Nothing is written until Select: a choice not validated does not survive closing
/// (.claude/rules/manager-ui.md §1).
/// </summary>
internal sealed class ChooseGameWindow : Window
{
    private readonly GamePickerBlock _picker;
    private readonly Button _select;
    private readonly TextBlock _complaint;
    private bool _chosen;

    private ChooseGameWindow(GameToConfirm game)
    {
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

        _picker = new GamePickerBlock(game, askAdult: false, requirePick: true,
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

        _select = new Button { Content = "Select", Classes = { "primary" } };
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

    /// <summary>Whether the confirmed game can be kept, saying why when it cannot.</summary>
    private bool Judge()
    {
        var complaint = _picker.Complaint;
        _complaint.Text = complaint ?? "";
        _complaint.IsVisible = complaint is not null;
        _select.IsEnabled = complaint is null;
        return complaint is null;
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
