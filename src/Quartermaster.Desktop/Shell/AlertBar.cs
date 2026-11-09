using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// The bar under the phase strip (WinForms AlertBar): the "what now" banner on the left, one chip per active alert on the right (severity accent,
/// name, count). Chips that do not fit collapse into a "+N" chip. Pure presentation; the owner supplies the data and reacts to the events.
/// </summary>
public sealed class AlertBar : Border
{
    const int ChipGap = 4, MinBanner = 140, BarHeight = 24;   // as WinForms

    sealed class Strip : Panel
    {
        public AlertBar Owner = null!;
        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var c in Children) c.Measure(Size.Infinity);
            return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, BarHeight);
        }
        protected override Size ArrangeOverride(Size finalSize) => Owner.PlaceChildren(finalSize);
    }

    static readonly IBrush NoticeFill = new SolidColorBrush(Color.FromRgb(255, 244, 200));

    readonly Strip _strip = new();
    readonly TextBlock _banner = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = new Cursor(StandardCursorType.Hand) };
    readonly Border _noticeChip;
    readonly Border _overflowChip;
    readonly Border _probe;
    readonly List<(AlertChip Chip, Border Visual)> _chips = new();
    int _placed;
    string? _notice;

    public AlertBar()
    {
        Name = "AlertBar";
        BorderThickness = new Thickness(0, 0, 0, 1);
        Height = BarHeight;
        IsVisible = false;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Classic.FaceBrush"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Classic.BorderBrush"));
        _strip.Owner = this;
        _strip.ClipToBounds = true;
        Child = _strip;
        _noticeChip = MakeChip("", Color.FromRgb(40, 100, 200), NoticeFill);
        ToolTip.SetTip(_noticeChip, AlertBarModel.NoticeTip);
        _noticeChip.PointerReleased += (_, e) => { if (IsLeft(e)) NoticeClicked?.Invoke(); };
        _noticeChip.IsVisible = false;
        _overflowChip = MakeChip("", Colors.Gray);
        _overflowChip.PointerReleased += (_, e) => { if (IsLeft(e)) OverflowClicked?.Invoke(OverflowChips, _overflowChip); };
        _overflowChip.IsVisible = false;
        _probe = MakeChip("+00", Colors.Gray);
        _banner.PointerReleased += (_, e) => { if (IsLeft(e)) BannerClicked?.Invoke(); };
        _strip.Children.Add(_banner);
        _strip.Children.Add(_noticeChip);
        _strip.Children.Add(_overflowChip);
        _strip.Children.Add(_probe);
    }

    public event Action? BannerClicked;
    public event Action<AlertChip>? ChipClicked;
    /// <summary>Right-click (or Ctrl+click on macOS) on a chip: the chip and the control to anchor the menu on.</summary>
    public event Action<AlertChip, Control>? ChipContext;
    /// <summary>The "+N" chip was clicked: the alerts that did not fit, and the control to anchor the menu on.</summary>
    public event Action<IReadOnlyList<AlertChip>, Control>? OverflowClicked;
    /// <summary>The notice chip was clicked.</summary>
    public event Action? NoticeClicked;

    public string BannerText => _banner.Text ?? "";
    public string? BannerTip => ToolTip.GetTip(_banner) as string;
    public string? NoticeText => _notice;

    /// <summary>The chips that did not fit.</summary>
    public IReadOnlyList<AlertChip> OverflowChips => _chips.Skip(_placed).Select(c => c.Chip).ToList();

    /// <summary>Chip texts currently drawn (the last may be "+N"), for tests and smoke checks.</summary>
    public IReadOnlyList<string> ChipTexts
    {
        get
        {
            var t = _chips.Take(_placed).Select(c => c.Chip.Label).ToList();
            var more = _chips.Count - _placed;
            if (more > 0) t.Add(AlertBarModel.OverflowText(more));
            return t;
        }
    }

    /// <summary>Tooltip text of the chip with this id (tests).</summary>
    public string? TipOf(string id) => _chips.FirstOrDefault(c => c.Chip.Id == id).Visual is { } v ? ToolTip.GetTip(v) as string : null;

    public void SetBanner(string text, string? tooltip)
    {
        _banner.Text = text;
        ToolTip.SetTip(_banner, tooltip);
        _strip.InvalidateMeasure();
    }

    /// <summary>Shows a notice chip left of the banner; null removes it.</summary>
    public void SetNotice(string? text)
    {
        _notice = string.IsNullOrWhiteSpace(text) ? null : text;
        _noticeChip.IsVisible = _notice != null;
        if (_notice != null) ChipLabel(_noticeChip).Text = _notice;
        _strip.InvalidateMeasure();
    }

    public void SetChips(IReadOnlyList<AlertChip> chips)
    {
        foreach (var (_, v) in _chips) _strip.Children.Remove(v);
        _chips.Clear();
        _placed = 0;
        foreach (var chip in chips)
        {
            var (r, g, b) = AlertBarModel.Accent(chip.Severity);
            var visual = MakeChip(chip.Label, Color.FromRgb(r, g, b));
            ToolTip.SetTip(visual, chip.Tooltip);
            var c = chip;
            visual.PointerReleased += (_, e) =>
            {
                var ctrl = ShortcutGestures.IsMac && e.KeyModifiers.HasFlag(KeyModifiers.Control);
                if (e.InitialPressMouseButton == MouseButton.Left && !ctrl) ChipClicked?.Invoke(c);
                else if (e.InitialPressMouseButton == MouseButton.Right || (e.InitialPressMouseButton == MouseButton.Left && ctrl)) ChipContext?.Invoke(c, visual);
            };
            _chips.Add((chip, visual));
            _strip.Children.Add(visual);
        }
        _strip.InvalidateMeasure();
    }

    /// <summary>Simulates a click on the chip (tests).</summary>
    public void ClickChip(string id) { if (_chips.FirstOrDefault(c => c.Chip.Id == id).Chip is { } c) ChipClicked?.Invoke(c); }
    public void ClickBanner() => BannerClicked?.Invoke();
    public void ClickOverflow() { if (_overflowChip.IsVisible) OverflowClicked?.Invoke(OverflowChips, _overflowChip); }
    public void ClickNotice() { if (_noticeChip.IsVisible) NoticeClicked?.Invoke(); }

    static bool IsLeft(PointerReleasedEventArgs e) => e.InitialPressMouseButton == MouseButton.Left;

    static TextBlock ChipLabel(Border chip) => (TextBlock)((Grid)chip.Child!).Children[1];

    static Border MakeChip(string text, Color accent, IBrush? fill = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("4,Auto") };
        grid.Children.Add(new Border { Background = new SolidColorBrush(accent) });
        var tb = new TextBlock { Text = text, FontWeight = FontWeight.Bold, Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);
        return new Border
        {
            Child = grid, BorderThickness = new Thickness(1), Height = BarHeight - 6, Cursor = new Cursor(StandardCursorType.Hand),
            Background = fill ?? Brushes.White, BorderBrush = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
    }

    Size PlaceChildren(Size finalSize)
    {
        var h = BarHeight - 6;
        var noticeW = _noticeChip.IsVisible ? (int)Math.Ceiling(_noticeChip.DesiredSize.Width) : 0;
        var left = 4 + (noticeW > 0 ? noticeW + ChipGap : 0);
        var widths = _chips.Select(c => (int)Math.Ceiling(c.Visual.DesiredSize.Width)).ToList();
        var wMore = (int)Math.Ceiling(_probe.DesiredSize.Width);
        var r = ChipLayout.Place(widths, wMore, (int)finalSize.Width - 4, ChipGap, MinBanner + left - 4);
        _placed = r.Xs.Count;
        if (noticeW > 0) _noticeChip.Arrange(new Rect(4, 3, noticeW, h));
        for (var i = 0; i < _chips.Count; i++)
        {
            var v = _chips[i].Visual;
            // Not IsVisible=false: a hidden chip would measure 0 wide and could never be placed again; a zero-size arrange hides it.
            v.Arrange(i < _placed ? new Rect(r.Xs[i], 3, widths[i], h) : new Rect(0, 0, 0, 0));
        }
        _overflowChip.IsVisible = r.Overflow > 0;   // its width comes from the probe, so hiding it is safe
        if (r.Overflow > 0)
        {
            ChipLabel(_overflowChip).Text = AlertBarModel.OverflowText(r.Overflow);
            ToolTip.SetTip(_overflowChip, string.Join(", ", OverflowChips.Select(c => c.Label)));
            _overflowChip.Arrange(new Rect(r.OverflowX, 3, wMore, h));
        }
        _banner.Arrange(new Rect(left, 0, Math.Max(0, r.BannerRight - left), BarHeight));
        _probe.Arrange(new Rect(0, 0, 0, 0));
        return finalSize;
    }
}

/// <summary>
/// Calls <c>changed</c> on the UI thread, debounced 250 ms, whenever the game changes, is replaced, the rules pack changes or the viewer changes
/// (WinForms PhaseWatcher at the alert bar's interval). Events may arrive from any thread.
/// </summary>
public sealed class AlertWatch : IDisposable
{
    readonly ModuleSession _s;
    readonly Debouncer _debounce;
    GameSession? _attached;

    public AlertWatch(ModuleSession s, Action changed)
    {
        _s = s;
        _debounce = new Debouncer(TimeSpan.FromMilliseconds(250), changed, ex => System.Diagnostics.Trace.WriteLine("alerts: " + ex.Message));
        s.GameReplaced += OnReplaced;
        s.RulesChanged += Request;
        s.ViewChanged += Request;
        Attach();
    }

    void Attach()
    {
        if (_attached != null) { _attached.Changed -= OnChanged; _attached.Rewound -= Request; }
        _attached = _s.Game;
        if (_attached != null) { _attached.Changed += OnChanged; _attached.Rewound += Request; }
    }

    void OnReplaced() { Attach(); Request(); }
    void OnChanged(GameCommand _) => Request();
    public void Request() => _debounce.Request();
    public void Flush() => _debounce.Flush();

    public void Dispose()
    {
        _s.GameReplaced -= OnReplaced;
        _s.RulesChanged -= Request;
        _s.ViewChanged -= Request;
        if (_attached != null) { _attached.Changed -= OnChanged; _attached.Rewound -= Request; _attached = null; }
        _debounce.Dispose();
    }
}
