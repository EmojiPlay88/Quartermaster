using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Quartermaster.Render;
using SkiaSharp;

namespace Quartermaster.Desktop.Map;

/// <summary>
/// The hover popup of a stack (WinForms CounterDetailPopup), drawn from the shared <see cref="CounterPopupLayout"/> in the window's overlay
/// layer. As in WinForms it is pointer-transparent except over its ledger value lines, which show their breakdown on hover; a click or drag
/// anywhere else on it reaches the map underneath. Drawn at device resolution (Avalonia applies the render scaling).
/// </summary>
sealed class CounterPopup : Control, ICustomHitTest
{
    readonly BreakdownTip _tip;
    readonly Action<Exception> _failed;

    /// <param name="failed">Told (on the render thread) when replaying the drawing fails.</param>
    public CounterPopup(CounterPopupLayout layout, BreakdownTip tip, Action<Exception> failed)
    {
        Layout = layout;
        _tip = tip;
        _failed = failed;
        Width = layout.Width;
        Height = layout.Height;
        ClipToBounds = true;
    }

    public CounterPopupLayout Layout { get; }

    /// <summary>True when the popup has ledger lines that can be hovered (it then takes pointer input over them).</summary>
    public bool Interactive => Layout.HotLines.Count > 0;

    public event Action? PointerEnteredPopup;
    public event Action? PointerLeftPopup;

    static Rect LineRect(CounterPopupHotLine l) => new(l.X, l.Top, l.Width, l.Height);

    /// <summary>Only the ledger lines take the pointer; everywhere else the popup is click-through (WinForms WM_NCHITTEST → HTTRANSPARENT).</summary>
    public bool HitTest(Point point)
    {
        foreach (var l in Layout.HotLines) if (LineRect(l).Contains(point)) return true;
        return false;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        PointerEnteredPopup?.Invoke();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _tip.Hide();
        PointerLeftPopup?.Invoke();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        HoverAt(e.GetPosition(this));
    }

    /// <summary>Shows the breakdown of the ledger line under <paramref name="p"/> (popup coordinates), else hides it.</summary>
    internal void HoverAt(Point p)
    {
        foreach (var l in Layout.HotLines)
        {
            var r = LineRect(l);
            if (r.Contains(p)) { _tip.Show(this, new Point(r.Right + 6, r.Top), l.Tip); return; }
        }
        _tip.Hide();
    }

    public override void Render(DrawingContext context)
    {
        var w = (float)Layout.Width;
        var h = (float)Layout.Height;
        var scale = (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        using var recorder = new SKPictureRecorder();
        var c = recorder.BeginRecording(new SKRect(0, 0, w, h));
        using (var back = new SKPaint { Color = CounterPopupLayout.Background }) c.DrawRect(new SKRect(0, 0, w, h), back);
        Layout.DrawContent(c, 1f, scale);
        // The WinForms edge: one device pixel, black, along the inside of the bounds.
        using var edge = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 0 };
        var half = 0.5f / scale;
        c.DrawRect(new SKRect(half, half, w - half, h - half), edge);
        context.Custom(new SkiaDrawOperation(recorder.EndRecording(), new Rect(0, 0, w, h), _failed));
    }
}
