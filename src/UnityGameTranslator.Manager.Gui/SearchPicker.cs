using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    private readonly ScrollViewer _scroll;
    private readonly Border _shell;
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

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);

        // 🔴 **Our own ScrollViewer, holding the list unbounded inside it.** The height belongs
        // here rather than on the list because the wheel has to be scrolled BY HAND — see
        // OnWheelWhileOpen — and doing that needs a scroller we hold a reference to. Left on the
        // list, the one doing the scrolling would be the one Avalonia builds inside its template,
        // which nothing here can reach.
        // ⚠ **Hidden, never Disabled.** Disabled does not merely hide the bar: it constrains the
        // content to the viewport's width. Inside a popup that sizes itself to its content that is
        // a circle with one solution — zero — and the list opened as an empty square in the corner.
        _scroll = new ScrollViewer
        {
            MaxHeight = 260,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list,
        };

        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_search, Dock.Top);
        panel.Children.Add(_search);
        panel.Children.Add(_scroll);

        // Held, because its width is set when the list opens: a dropdown is at least as wide as the
        // box it drops from, which is what every one of them has done for thirty years.
        _shell = new Border
        {
            Background = Palette.Of("SurfaceCard"),
            BorderBrush = Palette.Of("BorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
        };

        _popup = new Popup
        {
            PlacementTarget = _face,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = _shell,
        };

        // ⚠ No ScrollBounce.Attach here: the give is declared on every ScrollViewer by a style, and
        // in this one it is played from OnWheelWhileOpen instead — the wheel never reaches the
        // scroller inside a popup, so a handler waiting on it would never fire.
        _popup.Closed += OnClosed;

        _face.Click += (_, _) => Open();

        // 🔴 **Highlighting a row is not choosing it, and confusing the two breaks the keyboard.**
        // This listened to SelectionChanged, which fires as soon as a row is merely highlighted —
        // so pressing Down to walk into the list picked the first entry and shut the list on the
        // spot, and every arrow key after that would have chosen too. A row is committed by a click
        // on it or by Enter, and by nothing else.
        _list.PointerReleased += (_, _) => Commit();

        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _popup.IsOpen = false; e.Handled = true; }
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

        // 🔴 **The popup goes IN the tree, beside the face — it is not enough to point it at one.**
        // A Popup with no parent still opens, and everything inside it is invisible: styles and
        // resources reach a control through its tree, so a TextBox and a ListBox hanging off
        // nothing get no ControlTheme, and a control with no template draws nothing and measures
        // zero. What was left on screen was the Border, which paints itself — an empty box, exactly
        // the size of nothing.
        //
        // ⚠ A Popup in a Panel takes no room: it is not laid out inline. Being there is only about
        // what it inherits.
        Content = new Panel { Children = { _face, _popup } };
    }

    private void Open()
    {
        _search.Text = "";
        Refill();

        // ⚠ As wide as the box it drops from, and read at the moment it opens rather than fixed
        // once: the pickers are given their width by whoever builds the screen, and a settings
        // window that resizes gives them another.
        var wide = double.IsNaN(Width) ? Bounds.Width : Width;
        if (wide > 0) _shell.MinWidth = wide;

        _popup.IsOpen = true;

        // 🔴 **The wheel has to be carried in by hand, and this is an Avalonia bug, not a choice.**
        // AvaloniaUI/Avalonia#16646, open, Windows only: a wheel turned over a Popup never reaches
        // what is inside it — the event stops at the LightDismissOverlayLayer. So a list in a
        // dropdown cannot be scrolled with the wheel, which on a hundred and eighty languages is
        // not a detail but the only way through.
        //
        // ⚠ Hooked on the TOP LEVEL, not on the popup: the popup's tree is precisely where the
        // event never arrives. handledEventsToo, because the overlay has already marked it handled
        // by the time it passes here.
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            // ⚠ Taken off first: a click on the face while the list is already open dismisses it
            // and reopens in one gesture, and a second copy of this handler would scroll twice as
            // far per notch for the rest of the session.
            top.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheelWhileOpen);

            top.AddHandler(InputElement.PointerWheelChangedEvent, OnWheelWhileOpen,
                           RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        }

        // ⚠ Posted: the popup's tree is not there to take focus until it has been laid out, and a
        // search field that needs clicking before it accepts a letter is a search field nobody uses.
        Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Scrolls the open list, because nothing else will. See the note in <see cref="Open"/>.
    ///
    /// ⚠ Only while the list is open, and taken off the top level as soon as it closes — a handler
    /// left behind would eat the wheel for the whole window.
    ///
    /// ⚠ Three lines per notch, the figure Windows itself reports for a wheel detent. Scrolling by
    /// a fixed pixel count would move a different distance in this list than in every other.
    /// </summary>
    private void OnWheelWhileOpen(object? sender, PointerWheelEventArgs e)
    {
        if (!_popup.IsOpen) return;

        var reach = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        if (reach <= 0) return;

        var before = _scroll.Offset.Y;
        var moved = Math.Clamp(before - e.Delta.Y * 3 * RowHeight, 0, reach);

        _scroll.Offset = new Vector(_scroll.Offset.X, moved);

        // ⚠ The same give the rest of the program has at the end of a scroll, played from here
        // because it cannot be played from where it listens: the wheel never reaches inside a popup,
        // which is why this method exists at all. Nothing moved means the end was already reached.
        if (Math.Abs(moved - before) < 0.5) ScrollBounce.Nudge(_scroll, e.Delta.Y > 0);

        e.Handled = true;
    }

    /// <summary>About one row, used to turn a wheel notch into a distance.</summary>
    private const double RowHeight = 28;

    /// <summary>
    /// Takes the wheel handler back off, however the list was closed.
    ///
    /// 🔴 **Hung on the popup's own Closed, never on the paths that close it.** A light dismiss —
    /// a click anywhere else — closes it without passing through any of them, and a handler left
    /// behind would swallow the wheel for the entire window from then on.
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            top.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheelWhileOpen);
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

    /// <summary>
    /// Takes the highlighted row as the answer, closes, and says so.
    ///
    /// ⚠ Silent when nothing is highlighted: a click on the padding around the rows, or Enter with
    /// the list merely focused, must not close on an answer nobody gave.
    /// </summary>
    private void Commit()
    {
        if (!_popup.IsOpen || _list.SelectedItem is not { } row) return;

        _selected = row;
        ShowFace();
        _popup.IsOpen = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
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
