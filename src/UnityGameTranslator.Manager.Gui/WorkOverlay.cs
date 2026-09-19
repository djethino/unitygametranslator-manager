using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UnityGameTranslator.Manager.Gui;

/// <summary>What a step of a piece of work has come to.</summary>
public enum WorkStepState
{
    Waiting,
    Running,
    Done,
    Failed,
}

/// <summary>
/// The window darkened, the gear turning in the middle, and what is being applied — for as long as
/// something is being written into a game.
///
/// 🔴 **Because an install used to be a four-pixel stripe at the bottom of the window**, next to a
/// line of status text, while the window itself froze. Reported in those terms: *"je n'ai aucun
/// indicateur visuel et le manager freeze un moment"*. The stripe was not where the eye was, and
/// nothing stopped a second click on a card whose files were half in place.
///
/// ⚠ **Blocking is the point here, where <see cref="SpinningGear"/> on its own never blocks.** A
/// gear beside a list of results says "more is coming, read on". This covers acts that WRITE a
/// game — loader, mod, translation — and nothing on the card behind is true until they end: its
/// buttons would act on a game in the middle of changing. The veil takes the pointer, and disables
/// what it covers so the keyboard cannot reach it either.
///
/// ⚠ **It does not make the work asynchronous.** A window whose thread is busy draws nothing, veil
/// included. Whoever shows this runs the work off the interface thread — see
/// analyse/attente-et-asynchrone.md, the trap this program has already fallen into twice.
///
/// ⚠ **The steps stay after the work ends**, turned into ticks and a cross, until the result has
/// been read: the outcome window opens OVER the veil, so what happened and in which step can be
/// seen next to what is said about it. The gear stops at that moment — a mark still turning behind
/// "Nothing was changed" would say the program is still working when it has finished.
/// </summary>
public sealed class WorkOverlay : Border
{
    private readonly StackPanel _card;
    private readonly StackPanel _steps;

    /// <summary>The turning mark while working, a still heading once the work has ended.</summary>
    private readonly ContentControl _head;

    private SpinningGear? _gear;
    private readonly List<(ContentControl Mark, TextBlock Text)> _rows = new();
    private readonly List<WorkStepState> _states = new();

    public WorkOverlay()
    {
        // Dark, and the same dark in both themes: the veil says "not now" about everything under
        // it, which a tint drawn from the palette would not say as plainly on a light theme.
        Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        IsVisible = false;
        Opacity = 0;

        // Takes the pointer across the whole window: a click on the veil must not fall through to
        // a card under it.
        IsHitTestVisible = true;

        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(160),
                Easing = new CubicEaseOut(),
            },
        };

        _head = new ContentControl { HorizontalAlignment = HorizontalAlignment.Center };

        _steps = new StackPanel { Spacing = 6, IsVisible = false };

        _card = new StackPanel { Spacing = 18, Children = { _head, _steps } };

        Child = new Border
        {
            Background = Palette.Of("SurfaceCard"),
            BorderBrush = Palette.Of("BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(32, 26),
            MinWidth = 360,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _card,
        };
    }

    /// <summary>Whether the veil is up.</summary>
    public bool IsShown => IsVisible;

    /// <summary>
    /// The veil of <paramref name="window"/>, laid over its content the first time it is asked for.
    ///
    /// ⚠ For windows that build their content in code. The main window declares its own in XAML;
    /// these get the same one without each having to rearrange its layout to make room for it.
    /// Called once the window's Content is set, so the content can be wrapped.
    /// </summary>
    public static WorkOverlay On(Window window)
    {
        if (window.Content is Grid { Tag: WorkOverlay existing }) return existing;

        var overlay = new WorkOverlay();
        var content = window.Content as Control;

        // Detached before being re-parented: a control belongs to one place in the tree.
        window.Content = null;

        var host = new Grid { Tag = overlay };
        if (content is not null) host.Children.Add(content);
        host.Children.Add(overlay);

        window.Content = host;
        return overlay;
    }

    /// <summary>What <see cref="Show"/> disabled, so <see cref="Hide"/> gives back exactly that.</summary>
    private readonly List<Control> _disabled = new();

    /// <summary>
    /// Puts the veil up, the gear turning under <paramref name="caption"/>.
    /// </summary>
    /// <param name="caption">What is being applied, in the words of the act — it does not move.</param>
    /// <param name="steps">
    /// The steps the act is made of, when it has several. Listed from the start, all waiting, so
    /// what is left can be read as well as what is done.
    /// </param>
    public void Show(string caption, IEnumerable<string>? steps = null)
    {
        _gear = new SpinningGear(caption, size: 64, stacked: true) { Margin = new Thickness(0) };
        _head.Content = _gear;

        _steps.Children.Clear();
        _rows.Clear();
        _states.Clear();

        foreach (var step in steps ?? Enumerable.Empty<string>())
        {
            var text = new TextBlock
            {
                Text = step,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var mark = new ContentControl
            {
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(mark, 0);
            Grid.SetColumn(text, 1);
            text.Margin = new Thickness(10, 0, 0, 0);
            row.Children.Add(mark);
            row.Children.Add(text);

            _steps.Children.Add(row);
            _rows.Add((mark, text));
            _states.Add(WorkStepState.Waiting);
            Paint(_rows.Count - 1);
        }

        _steps.IsVisible = _rows.Count > 0;

        // ⚠ What the veil covers is disabled as well: the veil takes the pointer, but Tab and Enter
        // would still reach a button under it. Only what was enabled is touched, so Hide cannot
        // enable something that was disabled for a reason of its own.
        if (_disabled.Count == 0 && Parent is Panel host)
        {
            foreach (var sibling in host.Children)
            {
                if (ReferenceEquals(sibling, this) || !sibling.IsEnabled) continue;
                sibling.IsEnabled = false;
                _disabled.Add(sibling);
            }
        }

        IsVisible = true;
        Opacity = 1;
    }

    /// <summary>Where the work has got to — the line under the gear. Ignored once the work has ended.</summary>
    public void Report(string detail)
    {
        if (_gear is not null) _gear.Detail = detail;
    }

    /// <summary>
    /// Step <paramref name="index"/> has started. The one running before it is done: steps run
    /// in order, so starting the next is what finishing the previous one means.
    /// </summary>
    public void Begin(int index)
    {
        if (index < 0 || index >= _states.Count) return;

        for (var i = 0; i < _states.Count; i++)
        {
            if (_states[i] == WorkStepState.Running && i != index) Set(i, WorkStepState.Done);
        }

        Set(index, WorkStepState.Running);
    }

    /// <summary>
    /// The work has ended. The gear stops and a still heading says how; the steps settle —
    /// the one running is done or failed, those never reached stay waiting, since they did not
    /// happen.
    /// </summary>
    public void Finish(bool success, string heading)
    {
        for (var i = 0; i < _states.Count; i++)
        {
            if (_states[i] == WorkStepState.Running)
                Set(i, success ? WorkStepState.Done : WorkStepState.Failed);
        }

        _gear = null;
        _head.Content = new TextBlock
        {
            Text = heading,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Palette.Of(success ? "TextPrimary" : "StatusError"),
        };
    }

    /// <summary>Takes the veil down and forgets the work.</summary>
    public void Hide()
    {
        // ⚠ The gear leaves the tree here, which is what stops its animation — see SpinningGear.
        _gear = null;
        _head.Content = null;
        _steps.Children.Clear();
        _rows.Clear();
        _states.Clear();

        foreach (var control in _disabled) control.IsEnabled = true;
        _disabled.Clear();

        Opacity = 0;
        IsVisible = false;
    }

    private void Set(int index, WorkStepState state)
    {
        _states[index] = state;
        Paint(index);
    }

    private void Paint(int index)
    {
        var (mark, text) = _rows[index];

        switch (_states[index])
        {
            case WorkStepState.Waiting:
                mark.Content = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = Palette.Of("TextMuted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                text.Foreground = Palette.Of("TextMuted");
                text.FontWeight = FontWeight.Normal;
                break;

            case WorkStepState.Running:
                // The same gear, small: one mark for "working" across the whole program.
                mark.Content = new SpinningGear(string.Empty, size: 16) { Margin = new Thickness(0) };
                text.Foreground = Palette.Of("TextPrimary");
                text.FontWeight = FontWeight.SemiBold;
                break;

            case WorkStepState.Done:
                mark.Content = Glyphs.Check("StatusSuccess");
                text.Foreground = Palette.Of("TextSecondary");
                text.FontWeight = FontWeight.Normal;
                break;

            case WorkStepState.Failed:
                mark.Content = Glyphs.Cross("StatusError");
                text.Foreground = Palette.Of("StatusError");
                text.FontWeight = FontWeight.SemiBold;
                break;
        }
    }
}
