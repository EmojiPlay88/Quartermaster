using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Quartermaster.Core;

namespace Quartermaster.Desktop.Map;

/// <summary>
/// One shared tip for ledger breakdowns, as the WinForms BreakdownTip: monospace text (so the amounts line up) in the classic tooltip colours
/// (SystemColors.Info background, WindowFrame border, 4-pixel padding). Lives in the window's overlay layer, takes no pointer input and is
/// kept inside the window. Use <see cref="Show"/> on hover and <see cref="Hide"/> on leave. Never throws.
/// </summary>
sealed class BreakdownTip
{
    const double Pad = 4;
    static readonly IBrush Info = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xE1));
    static readonly IBrush Frame = new SolidColorBrush(Color.FromRgb(0x64, 0x64, 0x64));
    /// <summary>Consolas where installed, else Lucida Console, else the platform's monospace face (WinForms 8.25 pt = 11 px).</summary>
    static readonly FontFamily Mono = new("Consolas, Lucida Console, Menlo, DejaVu Sans Mono, monospace");

    Border? _shown;
    OverlayLayer? _layer;

    /// <summary>The text shown now; null when hidden.</summary>
    public string? Text { get; private set; }

    /// <summary>The tip control while shown (tests).</summary>
    public Control? Shown => _shown;

    /// <summary>Shows the breakdown with its top-left at <paramref name="at"/> (coordinates of <paramref name="owner"/>).</summary>
    public void Show(Visual owner, Point at, Breakdown b)
    {
        try
        {
            Hide();
            if (OverlayLayer.GetOverlayLayer(owner) is not { } layer || owner.TranslatePoint(at, layer) is not { } p) return;
            var text = LedgerBreakdown.Format(b);
            var tip = new Border
            {
                Background = Info,
                BorderBrush = Frame,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(Pad - 1),   // text at Pad from the edge, Pad * 2 larger than the text (WinForms Measure)
                IsHitTestVisible = false,
                Child = new TextBlock { Text = text, FontFamily = Mono, FontSize = 11, Foreground = Brushes.Black },
            };
            tip.Measure(Size.Infinity);
            var size = tip.DesiredSize;
            var room = WindowArea(layer);
            // Like a native tooltip, it stays on screen: moved back inside the window when it would run off it.
            Canvas.SetLeft(tip, Math.Max(room.X, Math.Min(p.X, room.Right - size.Width)));
            Canvas.SetTop(tip, Math.Max(room.Y, Math.Min(p.Y, room.Bottom - size.Height)));
            layer.Children.Add(tip);
            _shown = tip;
            _layer = layer;
            Text = text;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Hide(); }
    }

    /// <summary>The window's client area in <paramref name="layer"/> coordinates (the layer itself may not be laid out yet).</summary>
    internal static Rect WindowArea(OverlayLayer layer)
    {
        if (TopLevel.GetTopLevel(layer) is not { } top) return new Rect(layer.Bounds.Size);
        var origin = top.TranslatePoint(default, layer) ?? default;
        return new Rect(origin, top.ClientSize);
    }

    public void Hide()
    {
        try { if (_shown != null) _layer?.Children.Remove(_shown); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _shown = null;
        _layer = null;
        Text = null;
    }
}
