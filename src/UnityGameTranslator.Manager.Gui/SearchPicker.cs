using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// A picker for a list too long to scan: it looks like a dropdown, and it opens onto a search field.
///
/// 🔴 **Why not a ComboBox.** Two things a ComboBox could not give these lists. There are about a
/// hundred and eighty languages, and finding one meant dragging a scrollbar past all of them — the
/// mod solved that years ago with a search field, and the tool beside it had none. And its dropdown
/// did not answer the wheel, which on a list that long is not a detail: it is the only way through.
/// Here the list is an ordinary ListBox in an ordinary tree, so the wheel works because nothing had
/// to be arranged for it.
///
/// ⚠ **It carries the ComboBox's names on purpose** — Items, SelectedItem, ItemTemplate,
/// SelectionChanged — so the screens that fill and read it did not have to be rewritten around a
/// new vocabulary. What changed is the control, not the way anything talks to it.
///
/// ⚠ The closed face BUILDS its own copy of the selected row from the template. A control belongs
/// to one place in the tree: handing the list's row to the face as well would make it vanish from
/// whichever of the two drew second — the trap the language pickers already carry a note about.
/// </summary>
public sealed class SearchPicker : UserControl
{
    private readonly Button _face = new();
    private readonly ContentControl _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Popup _popup;
    private readonly TextBox _search;
    private readonly ListBox _list = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<object> _items = new();

    /// <summary>Everything on offer. Filled like a ComboBox's, and filtered by what is typed.</summary>
    public IList<object> Items => _items;

    public IDataTemplate? ItemTemplate { get; set; }

    /// <summary>Raised when somebody picks a row — never when the list is refilled.</summary>
    public event EventHandler? SelectionChanged;

    private object? _selected;

    public object? SelectedItem
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;

            _selected = value;
            ShowFace();
        }
    }

    /// <summary>
    /// What a row reads as, for searching. Set by whoever fills the list, since only they know
    /// which part of an item is the words somebody would type.
    /// </summary>
    public Func<object, string>? TextOf { get; set; }

    public SearchPicker()
    {
        _face.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _face.Padding = new Thickness(12, 6);

        // The chevron says "this opens" — the one thing the face borrows from a dropdown, because
        // that is the shape everybody already reads as one.
        var chevron = new TextBlock
        {
            Text = "⌄",
            FontSize = 14,
            Margin = new Thickness(8, -4, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Palette.Of("TextMuted"),
        };

        var face = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(chevron, 1);
        face.Children.Add(_label);
        face.Children.Add(chevron);
        _face.Content = face;

        _search = new TextBox
        {
            Watermark = "Search",
            Margin = new Thickness(8, 8, 8, 6),
        };

        _list.MaxHeight = 260;
        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);

        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_search, Dock.Top);
        panel.Children.Add(_search);
        panel.Children.Add(_list);

        _popup = new Popup
        {
            PlacementTarget = _face,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,

            Child = new Border
            {
                Background = Palette.Of("SurfaceCard"),
                BorderBrush = Palette.Of("BorderStrong"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = panel,
            },
        };

        _face.Click += (_, _) => Open();

        // ⚠ Filtering rebuilds the rows, so the selection in the list is meaningless afterwards —
        // this listens for a CLICK on a row instead, and reads what was clicked.
        _list.SelectionChanged += (_, _) =>
        {
            if (!_popup.IsOpen || _list.SelectedItem is not { } row) return;

            _selected = row;
            ShowFace();
            _popup.IsOpen = false;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };

        _search.TextChanged += (_, _) => Refill();

        // 🔴 **A row that is no longer on offer stops being the answer.** A ComboBox does this for
        // free — clearing its items clears its selection — and a picker that did not would go on
        // naming a model in its closed face while the list behind it was empty, which is exactly
        // what the AI screen shows while it is looking for a server.
        //
        // ⚠ Silent: a list being refilled is not somebody choosing, so no SelectionChanged. Whoever
        // refilled it says what the answer is now, and every caller here already does.
        _items.CollectionChanged += (_, _) =>
        {
            if (_selected is null || _items.Contains(_selected)) return;

            _selected = null;
            ShowFace();
        };

        // Typing belongs to the search field wherever the pointer is, and the two keys everybody
        // tries first must work: Escape closes without choosing, Down walks into the list.
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _popup.IsOpen = false; e.Handled = true; }
            else if (e.Key == Key.Down && _list.ItemCount > 0)
            {
                _list.Focus();
                _list.SelectedIndex = 0;
                e.Handled = true;
            }
        };

        Content = _face;
    }

    private void Open()
    {
        _search.Text = "";
        Refill();

        _popup.IsOpen = true;

        // ⚠ Posted: the popup's tree is not there to take focus until it has been laid out, and a
        // search field that needs clicking before it accepts a letter is a search field nobody uses.
        Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Loaded);
    }

    private void Refill()
    {
        var needle = _search.Text?.Trim() ?? "";

        // ⚠ A copy either way, never the live collection: handed straight to the ListBox it would
        // become its ItemsSource, and the next Items.Clear() would empty the list under the popup.
        var rows = _items
            .Where(item => needle.Length == 0
                           || Reads(item).Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _list.ItemTemplate = ItemTemplate;
        _list.ItemsSource = rows;

        // The row in force, so opening the list lands on it rather than at the top of a hundred and
        // eighty. ⚠ Only while nothing has been typed: under a filter it is usually gone, and
        // scrolling to nothing reads as the search having failed.
        if (needle.Length == 0 && _selected is not null && rows.Contains(_selected))
            _list.ScrollIntoView(_selected);
    }

    private string Reads(object item) => TextOf?.Invoke(item) ?? item.ToString() ?? "";

    /// <summary>
    /// Draws the row in force on the closed face — its own copy, built from the template.
    /// </summary>
    private void ShowFace()
    {
        if (_selected is null) { _label.Content = null; return; }

        _label.Content = ItemTemplate?.Build(_selected) ?? new TextBlock
        {
            Text = Reads(_selected),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Puts the list back on screen after it was refilled, without raising a choice.</summary>
    public void Reselect(object? item)
    {
        _selected = item;
        ShowFace();
    }
}
