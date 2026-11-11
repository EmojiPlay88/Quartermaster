using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Shell;

/// <summary>Which side of its target the tour balloon sits on.</summary>
public enum TourSide { Below, Above, Right, Left }

/// <summary>
/// The interface tour balloon (WinForms TourOverlay): a small borderless window owned by the main window, in the classic look (tooltip
/// yellow, black text) with a pointer toward the current step's target rectangle. Back / Next / Skip tour; Esc skips. Steps whose target
/// is not on screen are skipped. Targets come in the owner's window coordinates (DIPs).
/// </summary>
public sealed class TourOverlay : Window
{
    const double BodyW = 330, Arrow = 12, Pad = 10, ArrowHalf = 8;
    static readonly IBrush Fill = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xE1));   // SystemColors.Info
    static readonly IBrush Ink = Brushes.Black;                                                    // SystemColors.InfoText

    readonly IReadOnlyList<TourStep> _steps;
    readonly Func<string, Rect?> _resolve;
    readonly Action<bool> _finished;
    readonly TextBlock _title = new() { FontWeight = FontWeight.Bold, Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, Foreground = Ink, Margin = new Thickness(0, Pad / 2, 0, 0) };
    readonly DockPanel _body = new() { Width = BodyW - 2 * Pad, LastChildFill = false, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Chrome _chrome = new();
    int _index;
    bool _done;

    internal Button BackButton { get; } = new() { Content = MenuText.FromWinForms("< &Back") };
    internal Button NextButton { get; } = new() { Content = MenuText.FromWinForms("&Next >"), IsDefault = true };
    internal Button SkipButton { get; } = new() { Content = MenuText.FromWinForms("&Skip tour") };

    /// <summary>The balloon body's width (DIPs).</summary>
    internal double BodyWidth => BodyW;

    /// <summary>The side the balloon sits on for the current step.</summary>
    internal TourSide Side { get; private set; } = TourSide.Below;

    /// <summary>Steps whose target is on screen right now.</summary>
    public static IReadOnlyList<TourStep> VisibleSteps(Func<string, Rect?> resolve) => TourSteps.Visible(id => resolve(id) is { Width: > 0, Height: > 0 });

    /// <param name="steps">The steps to walk (usually <see cref="VisibleSteps"/>).</param>
    /// <param name="resolve">A target id's rectangle in the owner's window coordinates, or null when it is not on screen.</param>
    /// <param name="finished">Called once when the tour ends: true when skipped (Skip tour, Esc, closed from outside), false for Done.</param>
    public TourOverlay(IReadOnlyList<TourStep> steps, Func<string, Rect?> resolve, Action<bool> finished)
    {
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _finished = finished ?? throw new ArgumentNullException(nameof(finished));
        Name = "TourOverlay";
        Title = "Interface Tour";
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Foreground = Ink;

        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, Pad, 0, 0) };
        DockPanel.SetDock(SkipButton, Dock.Left);
        DockPanel.SetDock(NextButton, Dock.Right);
        DockPanel.SetDock(BackButton, Dock.Right);
        BackButton.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(SkipButton);
        buttons.Children.Add(NextButton);
        buttons.Children.Add(BackButton);
        foreach (var c in new Control[] { _title, _text, buttons }) { DockPanel.SetDock(c, Dock.Top); _body.Children.Add(c); }
        Content = new Panel { Children = { _chrome, _body } };

        Opened += (_, _) => ApplyTransparencyFallback(ActualTransparencyLevel);

        BackButton.Click += (_, _) => Go(_index - 1);
        NextButton.Click += (_, _) => { if (_index >= _steps.Count - 1) Finish(false); else Go(_index + 1); };
        SkipButton.Click += (_, _) => Finish(true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty) ApplyTransparencyFallback(ActualTransparencyLevel);
    }

    /// <summary>
    /// Without a compositor (<paramref name="actual"/> is None) a transparent window paints its see-through parts black: the arrow strip
    /// and the corners then get the control colour instead. Also used by the headless screenshot, whose frame has no alpha.
    /// </summary>
    internal void ApplyTransparencyFallback(WindowTransparencyLevel actual)
    {
        if (actual != WindowTransparencyLevel.None) return;
        Background = this.TryFindResource("Classic.FaceBrush", ActualThemeVariant, out var b) && b is IBrush face ? face : SystemFace;
    }

    static readonly IBrush SystemFace = new ImmutableSolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));   // SystemColors.Control

    public int Index => _index;
    public int Count => _steps.Count;
    public string CurrentTitle => _steps.Count > 0 ? $"{_steps[_index].Title}  ({_index + 1} of {_steps.Count})" : "";
    public bool IsDone => _done;

    /// <summary>Shows step <paramref name="index"/> (clamped); steps without a visible target are passed over in the direction of travel.
    /// Running off the end finishes the tour (as Done); running off the start keeps the current step.</summary>
    public void Go(int index)
    {
        if (_done) return;
        if (_steps.Count == 0) { Finish(false); return; }
        var dir = index >= _index ? 1 : -1;
        index = Math.Clamp(index, 0, _steps.Count - 1);
        while (index >= 0 && index < _steps.Count && _resolve(_steps[index].TargetId) is not { Width: > 0, Height: > 0 }) index += dir;
        if (index < 0 || index >= _steps.Count) { if (dir > 0) Finish(false); return; }
        _index = index;
        Relayout();
    }

    /// <summary>
    /// Where the balloon window goes for a target (all in screen DIPs): its top-left (the window includes the arrow strip on the target
    /// side) and the side. A wide target (width ≥ 2 × height) tries Below, Above, Right, Left; any other Right, Left, Below, Above; the
    /// first side with room wins, else the first preference, clamped into the screen.
    /// </summary>
    /// <param name="balloon">The balloon body's size (without the arrow).</param>
    public static (Point topLeft, TourSide side) Place(Rect target, Size balloon, Rect screen)
    {
        var l = Layout(target, balloon, screen);
        return (l.Window.TopLeft, l.Side);
    }

    /// <summary>The full placement: the window rectangle, the side, the arrow tip and the body rectangle (both relative to the window).</summary>
    static (Rect Window, TourSide Side, Point Tip, Rect Body) Layout(Rect target, Size balloon, Rect wa)
    {
        double bodyW = balloon.Width, bodyH = balloon.Height;
        var prefer = target.Width >= target.Height * 2
            ? new[] { TourSide.Below, TourSide.Above, TourSide.Right, TourSide.Left }
            : new[] { TourSide.Right, TourSide.Left, TourSide.Below, TourSide.Above };
        var side = prefer[0];
        foreach (var s in prefer)
        {
            var need = s is TourSide.Below or TourSide.Above ? bodyH + Arrow : bodyW + Arrow;
            var room = s switch
            {
                TourSide.Below => wa.Bottom - target.Bottom,
                TourSide.Above => target.Top - wa.Top,
                TourSide.Right => wa.Right - target.Right,
                _ => target.Left - wa.Left,
            };
            if (room >= need) { side = s; break; }
        }

        // Pointer tip: the nearest edge of the target, near its centre (at most 160 / 120 in), kept on the screen.
        var cx = Math.Clamp(target.Left + Math.Min(target.Width / 2, 160), wa.Left + Pad, Math.Max(wa.Left + Pad, wa.Right - Pad));
        var cy = Math.Clamp(target.Top + Math.Min(target.Height / 2, 120), wa.Top + Pad, Math.Max(wa.Top + Pad, wa.Bottom - Pad));
        Point tip; double bx, by;
        switch (side)
        {
            case TourSide.Below: tip = new Point(cx, target.Bottom); bx = tip.X - bodyW / 4; by = tip.Y + Arrow; break;
            case TourSide.Above: tip = new Point(cx, target.Top); bx = tip.X - bodyW / 4; by = tip.Y - Arrow - bodyH; break;
            case TourSide.Right: tip = new Point(target.Right, cy); bx = tip.X + Arrow; by = tip.Y - bodyH / 4; break;
            default: tip = new Point(target.Left, cy); bx = tip.X - Arrow - bodyW; by = tip.Y - bodyH / 4; break;
        }
        bx = Math.Clamp(bx, wa.Left, Math.Max(wa.Left, wa.Right - bodyW));
        by = Math.Clamp(by, wa.Top, Math.Max(wa.Top, wa.Bottom - bodyH));

        Rect window, body;
        switch (side)
        {
            case TourSide.Below: window = new Rect(bx, by - Arrow, bodyW, bodyH + Arrow); body = new Rect(0, Arrow, bodyW, bodyH); break;
            case TourSide.Above: window = new Rect(bx, by, bodyW, bodyH + Arrow); body = new Rect(0, 0, bodyW, bodyH); break;
            case TourSide.Right: window = new Rect(bx - Arrow, by, bodyW + Arrow, bodyH); body = new Rect(Arrow, 0, bodyW, bodyH); break;
            default: window = new Rect(bx, by, bodyW + Arrow, bodyH); body = new Rect(0, 0, bodyW, bodyH); break;
        }
        return (window, side, new Point(tip.X - window.X, tip.Y - window.Y), body);
    }

    void Relayout()
    {
        var step = _steps[_index];
        _title.Text = CurrentTitle;
        _text.Text = step.Text;
        BackButton.IsEnabled = _index > 0;
        NextButton.Content = MenuText.FromWinForms(_index >= _steps.Count - 1 ? "&Done" : "&Next >");

        _body.Measure(new Size(BodyW - 2 * Pad, double.PositiveInfinity));
        var bodyH = Math.Ceiling(_body.DesiredSize.Height) + 2 * Pad;

        // Window coordinates of the owner → screen DIPs; the owner's screen (or, headless, the owner itself) bounds the balloon.
        var scaling = Owner?.RenderScaling ?? RenderScaling;
        var origin = Owner is { } o ? ToDips(o.PointToScreen(default), scaling) : default;
        var target = _resolve(step.TargetId)!.Value.Translate(new Vector(origin.X, origin.Y));
        var screen = Owner is { } owner && owner.Screens.ScreenFromWindow(owner) is { } scr
            ? new Rect(scr.WorkingArea.X / scaling, scr.WorkingArea.Y / scaling, scr.WorkingArea.Width / scaling, scr.WorkingArea.Height / scaling)
            : Owner != null ? new Rect(origin, Owner.ClientSize) : new Rect(0, 0, 1920, 1080);
        var l = Layout(target, new Size(BodyW, bodyH), screen);
        Side = l.Side;
        Width = l.Window.Width;
        Height = l.Window.Height;
        Position = PixelPoint.FromPoint(l.Window.TopLeft, scaling);
        _body.Margin = new Thickness(l.Body.X + Pad, l.Body.Y + Pad, 0, 0);
        _chrome.Set(l.Body, l.Tip, l.Side);
    }

    static Point ToDips(PixelPoint p, double scaling) => new(p.X / scaling, p.Y / scaling);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Finish(true); return; }
        base.OnKeyDown(e);
    }

    void Finish(bool skipped)
    {
        if (_done) return;
        _done = true;
        _finished(skipped);
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && !_done) { _done = true; _finished(true); }   // closed from outside (the owner closing): counts as skipped
    }

    /// <summary>The balloon itself: the body rectangle and the triangle toward the tip, filled tooltip yellow with a black outline.</summary>
    sealed class Chrome : Control
    {
        Rect _body;
        Point _tip;
        TourSide _side;

        public void Set(Rect body, Point tip, TourSide side)
        {
            _body = body;
            _tip = tip;
            _side = side;
            InvalidateVisual();
        }

        public override void Render(DrawingContext g)
        {
            if (_body.Width <= 0) return;
            const double h = ArrowHalf;
            Point a, b;
            if (_side is TourSide.Below or TourSide.Above)
            {
                var x = Math.Clamp(_tip.X, _body.Left + h + 4, _body.Right - h - 4);
                var y = _side == TourSide.Below ? _body.Top : _body.Bottom;
                a = new Point(x - h, y);
                b = new Point(x + h, y);
            }
            else
            {
                var y = Math.Clamp(_tip.Y, _body.Top + h + 4, _body.Bottom - h - 4);
                var x = _side == TourSide.Right ? _body.Left : _body.Right;
                a = new Point(x, y - h);
                b = new Point(x, y + h);
            }
            var pen = new Pen(Ink, 1);
            var triangle = new PolylineGeometry(new[] { _tip, a, b }, true);
            g.DrawRectangle(Fill, null, _body);
            g.DrawGeometry(Fill, null, triangle);
            g.DrawRectangle(null, pen, _body.Deflate(0.5));
            g.DrawLine(pen, _tip, a);
            g.DrawLine(pen, _tip, b);
            // Close the gap: the body border where the triangle joins is painted over in the fill colour.
            var join = new Pen(Fill, 1.5);
            if (_side is TourSide.Below or TourSide.Above)
            {
                var y = _side == TourSide.Below ? _body.Top + 0.5 : _body.Bottom - 0.5;
                g.DrawLine(join, new Point(a.X + 1, y), new Point(b.X - 1, y));
            }
            else
            {
                var x = _side == TourSide.Right ? _body.Left + 0.5 : _body.Right - 0.5;
                g.DrawLine(join, new Point(x, a.Y + 1), new Point(x, b.Y - 1));
            }
        }
    }
}
