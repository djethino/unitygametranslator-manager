using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// Which game a pack opened from the file explorer goes to, when the pack does not say so without
/// doubt (PackTargets): the games, the closest first, a search, OK and Cancel.
///
/// ⚠ Choosing writes nothing. OK opens the game's Assets tab with the pack in it, and that tab's
/// Apply (N) is what writes — the same list, the same warnings and refusals as a pack dropped there
/// by hand (user, 2026-09-28).
/// </summary>
public sealed class PackGameWindow : Window
{
    private readonly IReadOnlyList<GameInstall> _ordered;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new();
    private readonly Button _ok = new() { Content = "OK", IsEnabled = false };

    private GameInstall? _chosen;
    private bool _confirmed;

    private PackGameWindow(string packName, string? madeFor, IReadOnlyList<GameInstall> likely,
                           IReadOnlyList<GameInstall> games)
    {
        Title = "Open asset pack";
        Width = 560;
        Height = 600;
        MinWidth = 420;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Brush("SurfaceBase");

        // The closest first, then the rest by name — never a list without the ones it could mean.
        _ordered = likely.Concat(games.Where(g => !likely.Contains(g))
                                      .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                         .ToList();

        var head = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(new TextBlock
        {
            Text = packName,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Brush("TextPrimary"),
        });
        head.Children.Add(Ui.Intro(madeFor is null
            ? "This pack does not say which game it is for. Choose the game to add it to."
            : $"Made for {madeFor}. Choose the game to add it to."));
        if (likely.Count > 0) head.Children.Add(Ui.Note("Closest matches are listed first."));

        _search.Watermark = "Search";
        _search.Margin = new Thickness(0, 0, 0, 10);
        _search.TextChanged += (_, _) => Fill();

        _list.SelectionChanged += (_, _) =>
        {
            _chosen = _list.SelectedItem is ListBoxItem { Tag: GameInstall game } ? game : null;
            _ok.IsEnabled = _chosen is not null;
        };
        _list.DoubleTapped += (_, _) => { if (_chosen is not null) Confirm(); };

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(24) };
        Grid.SetRow(head, 0);
        Grid.SetRow(_search, 1);
        var card = Ui.Card(_list);
        Grid.SetRow(card, 2);
        body.Children.Add(head);
        body.Children.Add(_search);
        body.Children.Add(card);

        // Enter takes the game chosen; Escape and Cancel leave without one.
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        _ok.Classes.Add("primary");
        _ok.IsDefault = true;
        _ok.Click += (_, _) => Confirm();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, _ok },
        };

        var bar = new Border
        {
            Background = Ui.Brush("SurfaceBar"),
            BorderBrush = Ui.Brush("BorderSubtle"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 12),
            Child = buttons,
        };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(body);
        Content = root;

        Fill();

        // A closest match is selected, so Enter confirms the likely answer and nothing else.
        if (likely.Count > 0 && _list.ItemCount > 0) _list.SelectedIndex = 0;

        Opened += (_, _) => _search.Focus();
    }

    /// <summary>Only OK and a double-click answer: closing the window any other way chooses nothing.</summary>
    private void Confirm()
    {
        _confirmed = true;
        Close();
    }

    /// <summary>The games the search leaves, keeping the order and the selection when it survives.</summary>
    private void Fill()
    {
        var filter = _search.Text?.Trim() ?? "";
        var kept = _chosen;

        var items = _ordered
            .Where(g => filter.Length == 0 || g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(Row)
            .ToList();

        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(i => ReferenceEquals(i.Tag, kept));
    }

    /// <summary>
    /// The game's icon when it has one, its name, and its folder — two installs of one game differ
    /// only there.
    /// </summary>
    private static ListBoxItem Row(GameInstall game)
    {
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = game.Name, FontSize = 13, Foreground = Ui.Brush("TextPrimary") });
        text.Children.Add(new TextBlock
        {
            Text = game.Path,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Ui.Brush("TextMuted"),
        });

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        if (GameIcons.For(game.ExecutablePath) is { } icon)
        {
            row.Children.Add(new Image { Source = icon, Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0) });
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        return new ListBoxItem { Content = row, Tag = game, Padding = new Thickness(8, 6) };
    }

    /// <summary>The game chosen, or null when the window was left without one.</summary>
    public static async Task<GameInstall?> ChooseAsync(Window owner, string packName, string? madeFor,
                                                        IReadOnlyList<GameInstall> likely,
                                                        IReadOnlyList<GameInstall> games)
    {
        var window = new PackGameWindow(packName, madeFor, likely, games);
        await window.ShowDialog(owner);
        return window._confirmed ? window._chosen : null;
    }
}
