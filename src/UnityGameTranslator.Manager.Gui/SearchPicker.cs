using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UnityGameTranslator.Common;

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

        // 🔴 **Not virtualised, because the list is MEASURED to size its popup** (2026-09-25). The
        // default panel realises only some rows and estimates the others, and on a first opening its
        // estimate is wrong: "Translate with" (four entries) opened showing three behind a scrollbar,
        // and correctly the second time, once real heights were remembered. Every row realised means
        // the first layout pass states the real height. The longest list here is the languages, a
        // couple of hundred rows — nothing a plain panel cannot hold.
        _list.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());

        // 🔴 **Our own ScrollViewer, holding the list unbounded inside it.** The height belongs
        // here rather than on the list because the wheel has to be scrolled BY HAND — see
        // OnWheelWhileOpen — and doing that needs a scroller we hold a reference to. Left on the
        // list, the one doing the scrolling would be the one Avalonia builds inside its template,
        // which nothing here can reach.
        // ⚠ **Hidden, never Disabled.** Disabled does not merely hide the bar: it constrains the
        // content to the viewport's width. Inside a popup that sizes itself to its content that is
        // a circle with one solution — zero — and the list opened as an empty square in the corner.
        // ⚠ **No height here.** How tall a list may be depends on the window it opens in, so it is
        // measured when it opens — see Open. A constant would be a list that covers a small window
        // whole and wastes a large one.
        _scroll = new ScrollViewer
        {
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

        // 🔴 **How tall, and whether there is a search field at all, are both decided by the
        // CONTENT** — which is what makes one control able to be every dropdown in the program. A
        // list of four answers and a list of a hundred and eighty languages are not two designs,
        // they are the same design measured against two lists.
        //
        // ⚠ Both answers come from the socle, not from here: the mod asks the same question of the
        // same lists, and answering it twice is how the two products end up disagreeing about
        // whether four entries deserve a search box.
        //
        // 🔴 **Asked with the rows' REAL height, so asked once the list is laid out** (2026-09-25).
        // It used to be asked before opening, with a guessed 28px row — while a row of this theme is
        // well over that, and a row with a flag more still. Every short list came out too short: two
        // entries showed one and a half with a scrollbar, eight showed six with no search field,
        // because on paper they fitted. So the list opens invisible, is measured on the first layout
        // pass, sized, and only then shown — see Settle.
        _search.IsVisible = false;
        _scroll.MaxHeight = double.PositiveInfinity;
        _shell.Opacity = 0;
        _settledHeight = -1;

        _list.LayoutUpdated -= OnListLaidOut;
        _list.LayoutUpdated += OnListLaidOut;

        _popup.IsOpen = true;

        // The wheel has to be carried into a popup by hand — an Avalonia bug, not a choice. See
        // PopupWheel, which is where that lives for the whole program.
        PopupWheel.Follow(this, () => _popup.IsOpen, () => _scroll);
    }

    /// <summary>The list height the open popup was last sized for; -1 before the first sizing.</summary>
    private double _settledHeight = -1;

    /// <summary>
    /// Every layout pass of an open list: the first sizes it, the later ones re-size it if the
    /// whole list turned out taller or shorter than measured (a row whose content arrived late).
    ///
    /// ⚠ An event, not a posted guess at when layout will be done: a pass that has not reached the
    /// list yet reports nothing and is simply skipped, and the next one is what answers.
    ///
    /// ⚠ Only while nothing is typed. Filtering shrinks the list on purpose, and a popup that
    /// shrank with every letter would jump under the pointer; it keeps the size of the full list.
    /// </summary>
    private void OnListLaidOut(object? sender, EventArgs e)
    {
        if (!_popup.IsOpen) { _list.LayoutUpdated -= OnListLaidOut; return; }

        var rows = _list.ItemCount;
        var tall = _list.Bounds.Height;

        // Not laid out yet — the next pass answers.
        if (rows > 0 && tall <= 0) return;

        var first = _settledHeight < 0;
        if (!first && (!string.IsNullOrEmpty(_search.Text) || Math.Abs(tall - _settledHeight) < 0.5))
            return;

        _settledHeight = tall;
        Settle(rows, tall, first);
    }

    /// <summary>
    /// Sizes the open list from what it measured, then shows it and gives it the keyboard.
    ///
    /// ⚠ The row height handed to the socle is the list's own height divided by its rows, padding
    /// included: the rule "never taller than the list" then gives exactly the list, and "needs a
    /// search" is exactly "does not fit". A per-row figure without the padding would leave a
    /// scrollbar for the last few pixels.
    /// </summary>
    private void Settle(int rows, double tall, bool first)
    {
        var room = TopLevel.GetTopLevel(this)?.ClientSize.Height ?? 0;
        var row = rows > 0 ? tall / rows : 0;

        _scroll.MaxHeight = rows > 0 ? DropdownFit.Height(rows, row, room) : 0;
        _search.IsVisible = DropdownFit.NeedsSearch(rows, row, room);

        // A later re-sizing only corrects the height: the popup is already shown and has the
        // keyboard, and taking it back would pull focus from under somebody using the arrows.
        if (!first) return;

        _shell.Opacity = 1;

        // ⚠ The search field takes the keyboard when there is one — a search field that needs
        // clicking before it accepts a letter is a search field nobody uses. And the LIST takes it
        // when there is none, or the arrows would do nothing until somebody had clicked a row.
        // Posted: the field was just made visible and is not laid out yet.
        //
        // ⚠ The row in force is brought into view HERE too: Refill asked for it while the list had
        // no height limit yet, when there was nothing to scroll — landing at the top of a hundred
        // and eighty languages instead of on the one chosen.
        Dispatcher.UIThread.Post(() =>
        {
            if (_selected is not null && _list.ItemsSource is IList<object> shown && shown.Contains(_selected))
                _list.ScrollIntoView(_selected);

            if (_search.IsVisible) _search.Focus(); else _list.Focus();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Lets go of the wheel and of the edge, however the list was closed.
    ///
    /// 🔴 **Hung on the popup's own Closed, never on the paths that close it.** A light dismiss —
    /// a click anywhere else — closes it without passing through any of them, and a handler left
    /// behind would swallow the wheel for the entire window from then on.
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        PopupWheel.Drop(this);

        // Closed before its first layout (a click on the face twice in a row): nothing to settle.
        _list.LayoutUpdated -= OnListLaidOut;

        // ⚠ And the edge is handed back rather than left leaning. The list is torn down with the
        // popup, so nothing would ever draw the spring's return — and the next time it opened it
        // would open already leaning, from a gesture made a quarter of an hour earlier.
        ScrollBounce.Settle(_scroll);
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

        // 🔴 **Assigned only when it changes.** This handed the list a NEW template on every refill;
        // replacing a template makes Avalonia clear every row already shown, and a cleared row is
        // drawn once more with no content at all — which the default row read as `null.ToString()`.
        // The process died there without a word, on every short list opened a second time or typed
        // into (Manager issue #1, 2026-09-23).
        var template = RowTemplate;
        if (!ReferenceEquals(_list.ItemTemplate, template)) _list.ItemTemplate = template;
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
    /// How a row is drawn: the caller's template, or one of our own built ONCE and kept.
    ///
    /// 🔴 **Without a template, a row reads as what TextOf says — never as the object itself.**
    /// The ListBox otherwise prints ToString(), which for a record is every field it holds: the
    /// library-source picker showed ids, paths and whole warnings across the screen (2026-09-22).
    ///
    /// ⚠ **An empty content is a real case, not an error to hide.** Avalonia draws a row being
    /// recycled with no content; a template must draw nothing then, as the language rows already do
    /// (LanguageMark.Rows, `choice?.Name`). Read as an item, it killed the process.
    /// </summary>
    private IDataTemplate RowTemplate => ItemTemplate ?? (_defaultRow ??= new FuncDataTemplate<object?>((item, _) => new TextBlock
    {
        Text = item is null ? "" : Reads(item),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    }));

    private IDataTemplate? _defaultRow;

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
