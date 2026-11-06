using System.Globalization;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.App;

/// <summary>Line of sight / range: a drag from one snapped grid location to another, measured in grid cells. Never touches game state.</summary>
sealed class LosTool(MapDef map)
{
    PointD? _a, _b;

    public bool Dragging { get; private set; }
    public bool HasLine => _a != null && _b != null;

    /// <summary>Starts a new line at the snapped map point.</summary>
    public void Begin(PointD p)
    {
        _a = _b = Snap(p);
        Dragging = true;
    }

    public void Update(PointD p)
    {
        if (Dragging) _b = Snap(p);
    }

    public void End() => Dragging = false;

    public void Cancel()
    {
        Dragging = false;
        _a = _b = null;
    }

    BoardDef? BoardAt(PointD p)
    {
        foreach (var b in map.Boards)
        {
            var lx = p.X - b.Offset.X;
            var ly = p.Y - b.Offset.Y;
            if (lx >= 0 && ly >= 0 && lx < b.Width && ly < b.Height) return b;
        }
        return null;
    }

    PointD Snap(PointD p)
    {
        if (BoardAt(p) is not { Grid: { } g } b) return p;
        var s = g.Snap(new PointD(p.X - b.Offset.X, p.Y - b.Offset.Y));
        return new PointD(s.X + b.Offset.X, s.Y + b.Offset.Y);
    }

    static PointD Local(BoardDef b, PointD p) => new(p.X - b.Offset.X, p.Y - b.Offset.Y);

    static string Name(BoardDef? b, PointD p)
    {
        var n = b?.Grid?.LocationName(Local(b, p)) ?? "";
        return n.Length > 0 ? n : string.Create(CultureInfo.InvariantCulture, $"({Math.Round(p.X)}, {Math.Round(p.Y)})");
    }

    /// <summary>"{locA} → {locB}: {n} hexes" (or squares, or px without a usable grid); null when there is no line.</summary>
    public string? Status()
    {
        if (_a is not { } a || _b is not { } b) return null;
        var ba = BoardAt(a);
        var bb = BoardAt(b);
        var head = $"{Name(ba, a)} → {Name(bb, b)}: ";
        var n = ba != null && bb != null ? HexDistance.Cells(ba, bb, Local(ba, a), Local(bb, b)) : null;
        if (n is { } cells)
        {
            var g = ba!.Grid;
            if (g is ZonedGrid z) g = z.GridFor(Local(ba, a), Local(ba, b));
            return head + cells.ToString(CultureInfo.InvariantCulture) + (g is HexGrid ? " hexes" : " squares");
        }
        var px = (int)Math.Round(Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)));
        return head + px.ToString(CultureInfo.InvariantCulture) + " px";
    }

    public void Draw(SKCanvas c, Viewport vp)
    {
        if (_a is not { } a || _b is not { } b) return;
        var pa = vp.ToScreen(a);
        var pb = vp.ToScreen(b);
        using var line = new SKPaint { Color = new SKColor(0xE0, 0x20, 0x20), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var dot = new SKPaint { Color = new SKColor(0xE0, 0x20, 0x20), IsAntialias = true };
        c.DrawLine(pa, pb, line);
        c.DrawCircle(pa, 5, dot);
        c.DrawCircle(pb, 5, dot);
        var text = Status();
        if (text == null) return;
        var label = text[(text.LastIndexOf(": ", StringComparison.Ordinal) + 2)..];
        using var font = new SKFont(SKTypeface.FromFamilyName("Tahoma") ?? SKTypeface.Default, 13);
        var w = font.MeasureText(label);
        var mid = new SKPoint((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
        var box = SKRect.Create(mid.X - w / 2 - 4, mid.Y - 22, w + 8, 18);
        using var back = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xE1) };
        using var edge = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke };
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        c.DrawRect(box, back);
        c.DrawRect(box, edge);
        c.DrawText(label, box.Left + 4, box.Bottom - 5, SKTextAlign.Left, font, ink);
    }
}
