using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Phases;

/// <summary>
/// The tracker strip under the toolbar (WinForms PhaseStrip): "Turn n", the block text, one chip per phase (done greyed, current highlighted,
/// with the acting side) and the pass / relocation notice at the right. Chips wrap onto up to 3 rows, beyond that the strip scrolls.
/// Before the game starts it shows the Start game button; with no phases only the no-phases text.
/// </summary>
public sealed class PhaseStrip : Border
{
    const int MaxRows = 3, RowH = 24;

    readonly Button _start = new() { Content = PhaseTexts.StartGameText, Margin = new Thickness(4, 2, 4, 2), Focusable = false, IsVisible = false };
    readonly WrapPanel _flow = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _notice = new() { FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), IsVisible = false };
    readonly List<(string Text, ChipState? State, string? Notes)> _items = new();

    public PhaseStrip()
    {
        Name = "PhaseStrip";
        BorderThickness = new Thickness(0, 0, 0, 1);
        this.Bind(BackgroundProperty, this.GetResourceObservable("Classic.FaceBrush"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Classic.BorderBrush"));
        _notice.Bind(TextBlock.ForegroundProperty, _notice.GetResourceObservable("Classic.HighlightBrush"));
        _start.Click += (_, _) => StartRequested?.Invoke();
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = MaxRows * RowH + 4,
            Content = _flow,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(scroll);
        Grid.SetColumn(_notice, 1);
        grid.Children.Add(_notice);
        Child = grid;
        IsVisible = false;
    }

    public event Action? StartRequested;

    /// <summary>False while the game may not change (a received turn replaying): Start game is shown greyed.</summary>
    public bool StartEnabled { get => _start.IsEnabled; set => _start.IsEnabled = value; }

    public bool StartVisible => _start.IsVisible;
    public string StartText => _start.Content as string ?? "";

    /// <summary>Texts of the items shown, in order (tests).</summary>
    public IReadOnlyList<string> ItemTexts => _items.Select(i => i.Text).ToList();

    /// <summary>The state of each item: null for a plain label (tests).</summary>
    public IReadOnlyList<ChipState?> States => _items.Select(i => i.State).ToList();

    public string? Notice => _notice.IsVisible ? _notice.Text : null;

    /// <summary>Notes tooltip of the chip showing this text, or ending with " — " plus it (tests).</summary>
    public string? NotesFor(string chipText) =>
        _items.FirstOrDefault(i => i.Text == chipText || i.Text.EndsWith(" — " + chipText, StringComparison.Ordinal)).Notes;

    /// <summary>The host's "refresh now" (its debounce flush); set by the main window.</summary>
    internal Action? FlushHook { get; set; }

    /// <summary>Runs the pending refresh at once (tests, right after a click).</summary>
    public void Flush() => FlushHook?.Invoke();

    /// <summary>Simulates the click on Start game (tests).</summary>
    public void ClickStart()
    {
        if (_start.IsVisible && _start.IsEnabled) _start.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    public void SetView(PhaseView v)
    {
        _items.Clear();
        _flow.Children.Clear();
        _start.IsVisible = v.BeforeStart && !v.NoPhases;
        if (_start.IsVisible) _flow.Children.Add(_start);
        foreach (var chip in PhaseTexts.Strip(v))
        {
            _items.Add((chip.Text, chip.State, chip.Tooltip));
            _flow.Children.Add(Make(chip));
        }
        _notice.Text = v.Notice;
        _notice.IsVisible = !string.IsNullOrEmpty(v.Notice);
    }

    Control Make(StripChip chip)
    {
        var text = new TextBlock { Text = chip.Text, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (chip.State is not { } state)
        {
            text.Margin = new Thickness(4, 0, 4, 0);
            var label = new Border { Margin = new Thickness(2), MinHeight = RowH - 4, Child = text };
            return label;
        }
        var b = new Border
        {
            Margin = new Thickness(2),
            Padding = new Thickness(6, 2),
            BorderThickness = new Thickness(1),
            Child = text,
        };
        switch (state)
        {
            case ChipState.Current:
                text.FontWeight = FontWeight.Bold;
                b.Bind(Border.BackgroundProperty, b.GetResourceObservable("Classic.HighlightBrush"));
                b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("Classic.HighlightBrush"));
                text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("Classic.HighlightTextBrush"));
                break;
            case ChipState.Done:
                b.Bind(Border.BackgroundProperty, b.GetResourceObservable("Classic.FaceBrush"));
                b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("Classic.FaceBrush"));
                text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("Classic.GrayTextBrush"));
                break;
            default:
                b.Bind(Border.BackgroundProperty, b.GetResourceObservable("Classic.WindowBrush"));
                b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("Classic.BorderBrush"));
                text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("Classic.TextBrush"));
                break;
        }
        if (!string.IsNullOrWhiteSpace(chip.Tooltip)) ToolTip.SetTip(b, chip.Tooltip);
        return b;
    }
}
