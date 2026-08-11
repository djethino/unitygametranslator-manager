using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>
/// The places this tool was told to look in, and the one screen where they are added and taken
/// back out.
///
/// ⚠ Adding used to live in the toolbar and managing behind a second button beside it, which split
/// one subject across two controls: the list said "use Add a folder…" and pointed at something
/// outside itself. They are the same thing — where do you keep your games — so they are one window,
/// and the toolbar is one button shorter for it.
///
/// **Nothing here waits for an Apply, and that is deliberate.** The window it replaced had Cancel
/// and Apply, which was honest only as long as adding was somewhere else: a folder is added by
/// picking it in the system dialog, on disk, before this window hears about it — so a Cancel beside
/// it would have claimed to undo something already done. Both actions take effect at once and
/// removal offers Undo for as long as the window is open, which is the promise that can actually be
/// kept. The one thing deferred is the rescan, once, on close: re-reading every drive after each
/// click would make managing three folders a three-minute job.
/// </summary>
public sealed class FoldersWindow : Window
{
    private readonly CustomFolders _folders;
    private readonly IReadOnlyList<GameInstall> _games;

    private readonly List<Entry> _entries = new();
    private StackPanel _list = null!;
    private TextBlock _status = null!;

    /// <summary>One remembered folder as this window sees it.</summary>
    private sealed class Entry
    {
        public required string Path { get; init; }

        /// <summary>
        /// How many of the games in the list came from here. Counted from what the main window
        /// already found rather than by walking the folder again: the answer is on screen behind
        /// this one, and re-reading a drive to say "4" would stall the window on the machines
        /// where the number matters most.
        /// </summary>
        public required int Games { get; set; }

        public bool Removed { get; set; }
    }

    /// <summary>True when the list of folders changed, so the caller knows to look again.</summary>
    public bool Changed { get; private set; }

    /// <summary>
    /// A game found while adding a folder, so the caller can land on it once it has rescanned.
    /// Adding a folder is somebody saying "my game is in here" — ending up looking at it is the
    /// answer to that, and hunting for it in a list of eighty is not.
    /// </summary>
    public string? FirstGameFound { get; private set; }

    public FoldersWindow(CustomFolders folders, IReadOnlyList<GameInstall> games)
    {
        _folders = folders;
        _games = games;

        Title = "Folders you added";
        Width = 720;
        Height = 560;
        MinWidth = 520;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("SurfaceBase");

        foreach (var folder in _folders.All)
            _entries.Add(new Entry { Path = folder, Games = CountGamesIn(folder) });

        Content = Build();
    }

    /// <summary>
    /// ⚠ Three bands, and which one scrolls is the whole point.
    ///
    /// The list is the only thing here whose length is not ours to decide — somebody with fifteen
    /// drives has fifteen rows — so it is the only thing that scrolls, inside the card, against the
    /// card's own edge. Everything else holds its place: the sentence that says what this window is,
    /// the heading, **Add a folder…**, and Close.
    ///
    /// The first shape put one scroller around the lot. It reads fine with three folders and falls
    /// apart with twenty: the button you came here to press leaves the screen, and the way back is
    /// to scroll up past everything you were reading. An action does not scroll away from its own
    /// list — and a scrollbar down the side of the window says "this whole page is long", which is
    /// not true of anything here except the list.
    /// </summary>
    private Control Build()
    {
        var intro = new TextBlock
        {
            Text = "Steam, Epic and GOG are found on their own — you never have to add those. This "
                 + "is for everything else: a game installed by hand, a second drive, a library "
                 + "kept somewhere of your own choosing.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 16),
        };

        _list = new StackPanel { Spacing = 8 };

        var card = Card(FolderSection());

        // Auto for the sentence, star for the card: the card takes whatever height is left, which is
        // what gives the list inside it something definite to scroll within. Left to size itself it
        // would grow with its contents and hand the scrolling back to the window.
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(24),
        };

        Grid.SetRow(intro, 0);
        Grid.SetRow(card, 1);
        layout.Children.Add(intro);
        layout.Children.Add(card);

        RefreshList();

        _status = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        };

        // One button, and it says what it does. There is nothing to cancel — see the note on the
        // class — so offering it would be offering a way back that does not exist.
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, Classes = { "primary" } };
        close.Click += (_, _) => Close();

        var bar = new Border
        {
            Background = Brush("SurfaceBar"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 12),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { _status, close },
            },
        };

        Grid.SetColumn(_status, 0);
        Grid.SetColumn(close, 1);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(layout);

        return root;
    }

    /// <summary>
    /// The heading, the way to add one, and the list — in that order, in one frame.
    ///
    /// The button stays in the same place whether the list is empty or full. Putting a second,
    /// larger one in the middle of the empty state would read better on first launch and worse
    /// every time after: the same act would then have two buttons, and the reader has to work out
    /// how they differ before pressing either.
    /// </summary>
    private Control FolderSection()
    {
        var add = Glyphs.Button(Glyphs.FolderPlus("TextPrimary"), "Add a folder...");
        add.Classes.Add("primary");
        add.VerticalAlignment = VerticalAlignment.Center;
        add.Click += async (_, _) => await AddAsync(add);

        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock
        {
            Text = "Extra places to look",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextPrimary"),
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Each one is searched two levels deep, which is where repacked games usually sit.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        });

        var top = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 14),
        };
        Grid.SetColumn(heading, 0);
        Grid.SetColumn(add, 1);
        top.Children.Add(heading);
        top.Children.Add(add);

        // The scrollbar lives here, against the inside edge of the card, rather than down the side
        // of the window: it belongs to the list, and putting it where the list is says so without a
        // word. Horizontal scrolling is off — a long path wraps, it does not run off to the right,
        // where reading it would mean scrolling every row back and forth.
        var scroller = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,

            // Room for the bar so that its arrival does not reflow every row it overlaps.
            Padding = new Thickness(0, 0, 6, 0),
            Margin = new Thickness(0, 0, -6, 0),
        };

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(top, 0);
        Grid.SetRow(scroller, 1);
        body.Children.Add(top);
        body.Children.Add(scroller);
        return body;
    }

    // ---------------------------------------------------------------- the list

    private void RefreshList()
    {
        _list.Children.Clear();

        if (_entries.Count == 0)
        {
            // Centred in the space the card gives it. Rows start at the top because that is where a
            // list starts; a single "nothing here" pinned to the top of a tall empty card reads as
            // content that failed to load rather than as an answer.
            _list.VerticalAlignment = VerticalAlignment.Center;
            _list.Children.Add(Empty());
            return;
        }

        _list.VerticalAlignment = VerticalAlignment.Top;
        foreach (var entry in _entries) _list.Children.Add(Row(entry));
    }

    /// <summary>
    /// What stands in for the list when there is nothing in it — a state most people will be in,
    /// since the launchers cover most libraries on their own.
    ///
    /// It says the list being empty is normal rather than leaving a blank where content should be.
    /// </summary>
    private Control Empty() => new Border
    {
        Background = Brush("SurfaceInput"),
        BorderBrush = Brush("BorderSubtle"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 22),
        Child = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new Panel
                {
                    Children = { Glyphs.Folder() },
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Opacity = 0.6,
                },
                new TextBlock
                {
                    Text = "Nothing added yet",
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    TextAlignment = TextAlignment.Center,
                    Foreground = Brush("TextSecondary"),
                },
                new TextBlock
                {
                    Text = "Which is usually right: if all your games come from Steam, Epic or GOG, "
                         + "they are already in the list. Add a folder only when one of them is not.",
                    FontSize = 11,
                    MaxWidth = 380,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Foreground = Brush("TextMuted"),
                },
            },
        },
    };

    /// <summary>
    /// One folder: what it is called, where it is, and what came out of it.
    ///
    /// The name is pulled out of the path and shown on its own line because that is what somebody
    /// recognises their own folder by; the full path stays underneath, since two folders can share
    /// a name and only the path settles which is which.
    /// </summary>
    private Control Row(Entry entry)
    {
        var name = System.IO.Path.GetFileName(entry.Path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

        if (string.IsNullOrEmpty(name)) name = entry.Path;   // a drive root has no last segment

        var missing = _folders.IsMissing(entry.Path);

        var title = new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextPrimary"),
            TextDecorations = entry.Removed ? TextDecorations.Strikethrough : null,
        };

        var path = new TextBlock
        {
            Text = entry.Path,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuted"),
        };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(path);
        text.Children.Add(State(entry, missing));

        var glyph = new Panel
        {
            Children = { Glyphs.Folder(missing ? "StatusWarning" : "TextMuted") },
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 12, 0),
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(text, 1);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(buttons, 2);

        if (entry.Removed)
        {
            var undo = new Button { Content = "Undo", FontSize = 11 };
            undo.Click += (_, _) =>
            {
                _folders.Add(entry.Path);
                entry.Removed = false;
                Changed = true;
                Say($"{name} is back in the list.");
                RefreshList();
            };

            buttons.Children.Add(undo);
        }
        else
        {
            var open = Glyphs.Button(Glyphs.Folder(), "Open", 11);
            open.IsEnabled = !missing;
            ToolTip.SetTip(open, missing
                ? "This folder is not on the machine right now."
                : "Open this folder");
            open.Click += (_, _) => Shell.OpenFolder(entry.Path);

            var remove = Glyphs.Button(Glyphs.Trash(), "Remove", 11);
            ToolTip.SetTip(remove, "Stop looking in this folder. Nothing in it is touched.");
            remove.Click += (_, _) =>
            {
                _folders.Remove(entry.Path);
                entry.Removed = true;
                Changed = true;
                Say($"{name} will no longer be searched. Undo is beside it until you close.");
                RefreshList();
            };

            buttons.Children.Add(open);
            buttons.Children.Add(remove);
        }

        row.Children.Add(glyph);
        row.Children.Add(text);
        row.Children.Add(buttons);

        return new Border
        {
            Background = Brush("SurfaceInput"),
            BorderBrush = Brush(missing && !entry.Removed ? "StatusWarning" : "BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),

            // Faded rather than gone: a row that vanishes takes its Undo with it, and the person
            // who clicked Remove by mistake has nothing left to aim at.
            Opacity = entry.Removed ? 0.5 : 1,
            Child = row,
        };
    }

    /// <summary>The one line that says what this folder is currently worth.</summary>
    private TextBlock State(Entry entry, bool missing)
    {
        var (text, colour) = entry.Removed
            ? ("Will be forgotten when you close this window.", "TextMuted")
            : missing
                // Never dropped on its own: an unplugged drive is not a decision to forget what
                // somebody asked us to remember.
                ? ("Not on this machine right now — an unplugged drive or a disconnected share "
                   + "looks like this. It is kept, and searched again when it comes back.",
                   "StatusWarning")
                : entry.Games switch
                {
                    0 => ("No Unity game found in here.", "TextMuted"),
                    1 => ("1 game in your list comes from here.", "StatusSuccess"),
                    _ => ($"{entry.Games} games in your list come from here.", "StatusSuccess"),
                };

        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(colour),
            Margin = new Thickness(0, 2, 0, 0),
        };
    }

    // ---------------------------------------------------------------- adding

    private async Task AddAsync(Button trigger)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where are the games?",
            AllowMultiple = false,
        });

        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;

        path = System.IO.Path.GetFullPath(path);

        if (_entries.Any(e => !e.Removed
                              && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            Say("That folder is already in the list.");
            return;
        }

        trigger.IsEnabled = false;
        Say("Looking through it...");

        // Off the UI thread: a folder on a slow or sleeping drive takes seconds, and a window that
        // stops repainting while it waits looks like one that has crashed.
        var found = await Task.Run(() =>
            StoreScanner.ScanFolder(path, GameStore.Manual, maxDepth: 2).ToList());

        // Remembered whatever came of it. A folder with nothing in it today is a folder somebody
        // is about to install a game into, and dropping it would make them add it twice.
        _folders.Add(path);
        Changed = true;

        // A folder taken back out and added again is the same folder, not a second one.
        var existing = _entries.FirstOrDefault(
            e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Removed = false;
            existing.Games = found.Count;
        }
        else
        {
            _entries.Add(new Entry { Path = path, Games = found.Count });
        }

        FirstGameFound ??= found.FirstOrDefault()?.Path;

        Say(found.Count switch
        {
            0 => "No Unity game in there yet. The folder was added anyway, and will be searched "
                 + "again every time this tool starts.",
            1 => $"Found {found[0].Name}.",
            _ => $"Found {found.Count} games.",
        });

        trigger.IsEnabled = true;
        RefreshList();
    }

    // ---------------------------------------------------------------- helpers

    private void Say(string message) => _status.Text = message;

    /// <summary>
    /// Games from the main list that sit inside this folder.
    ///
    /// Compared as paths with a separator on the end, so "E:\Games" does not claim the contents of
    /// "E:\GamesOld".
    /// </summary>
    private int CountGamesIn(string folder)
    {
        var root = folder.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                                  System.IO.Path.AltDirectorySeparatorChar)
                 + System.IO.Path.DirectorySeparatorChar;

        return _games.Count(game =>
            game.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || string.Equals(game.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                                               System.IO.Path.AltDirectorySeparatorChar),
                             folder.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                                            System.IO.Path.AltDirectorySeparatorChar),
                             StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The site's card, the same one the rest of the tool is built from.</summary>
    private static Control Card(Control content) => new Border
    {
        Background = Brush("SurfaceCard"),
        BorderBrush = Brush("BorderSubtle"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        Child = content,
    };

    /// <summary>Through Palette, which will not let an unknown key pass unnoticed.</summary>
    private static IBrush? Brush(string key) => Palette.Of(key);
}
