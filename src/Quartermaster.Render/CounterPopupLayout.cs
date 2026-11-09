using Quartermaster.Core;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>
/// A ledger value line of the hover popup: where its text sits (logical, 96-dpi units, popup-relative) and the breakdown shown on hover.
/// </summary>
public sealed record CounterPopupHotLine(float X, float Top, float Width, Breakdown Tip)
{
    public float Height => CounterPopupLayout.Line;

    /// <summary>The line's rectangle in pixels of a popup rendered at <paramref name="scale"/> (the WinForms hot rectangle).</summary>
    public SKRectI PixelRect(float scale) =>
        SKRectI.Create((int)(X * scale), (int)(Top * scale), (int)Math.Ceiling(Width * scale), (int)(CounterPopupLayout.Line * scale));
}

/// <summary>
/// The hover popup of a stack (WinForms CounterDetailPopup, shared with the Avalonia app): every piece of the stack, top first and at most
/// <see cref="MaxRows"/>, at 100% with its name, properties and ledger lines (dotted underline: hover for the breakdown), then "+ n more".
/// Laid out in logical 96-dpi units; drawn with Skia. Pass the viewer's state (ModuleSession.View): the popup shows what that state holds.
/// </summary>
public sealed class CounterPopupLayout
{
    public const int MaxRows = 12;
    public const int Pad = 6;
    public const float Line = 15;
    public const float MaxWidth = 520;
    public static readonly SKColor Background = new(0xFF, 0xFF, 0xE1);

    sealed record Row(PieceInstance Piece, PieceType Type, PieceDetail Detail, SKSizeI Cell, int Height);

    readonly PieceRenderer _pieces;
    readonly List<Row> _rows;
    readonly int _extra;

    CounterPopupLayout(PieceRenderer pieces, List<Row> rows, int extra, int width, int height, List<CounterPopupHotLine> hot)
    {
        _pieces = pieces;
        _rows = rows;
        _extra = extra;
        Width = width;
        Height = height;
        HotLines = hot;
    }

    /// <summary>Logical width (96-dpi units).</summary>
    public int Width { get; }

    /// <summary>Logical height (96-dpi units).</summary>
    public int Height { get; }

    /// <summary>The ledger value lines, top to bottom.</summary>
    public IReadOnlyList<CounterPopupHotLine> HotLines { get; }

    /// <summary>The detail of each listed piece, top of the stack first.</summary>
    public IReadOnlyList<PieceDetail> Details => _rows.Select(r => r.Detail).ToList();

    /// <summary>Pieces of the stack not listed ("+ n more").</summary>
    public int More => _extra;

    /// <summary>The text lines as drawn: per piece its name, "key: value" properties and "name: value" ledger lines; then "+ n more".</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            var lines = new List<string>();
            foreach (var r in _rows)
            {
                lines.Add(r.Detail.Name);
                foreach (var (k, v) in r.Detail.Properties) lines.Add($"{k}: {v}");
                foreach (var l in r.Detail.Ledger) lines.Add(LedgerText(l.Name, l.Value));
            }
            if (_extra > 0) lines.Add($"+ {_extra} more");
            return lines;
        }
    }

    static string LedgerText(string name, double value) => $"{name}: {LedgerTable.FormatNumber(value)}";

    static SKFont Font() => new(SKTypeface.FromFamilyName("Tahoma") ?? SKTypeface.Default, 11);
    static SKFont Bold() => new(SKTypeface.FromFamilyName("Tahoma", SKFontStyle.Bold) ?? SKTypeface.Default, 11);

    public static CounterPopupLayout Build(ModuleSession s, GameState state, StackInstance stack, PieceRenderer pieces)
    {
        var picked = new List<(PieceInstance Piece, PieceType Type, PieceDetail Detail, SKSizeI Cell)>();
        foreach (var id in Enumerable.Reverse(stack.PieceIds).Take(MaxRows))
        {
            if (!state.Pieces.TryGetValue(id, out var p)) continue;
            var t = pieces.Types.Get(p.TypeString);
            var size = s.Images.SizeOf(t.Basic.Image);
            picked.Add((p, t, PieceDetails.For(p, t, state, s.Rules), new SKSizeI(Math.Clamp(size.Width, 24, 120), Math.Clamp(size.Height, 24, 120))));
        }
        var extra = stack.PieceIds.Count - picked.Count;

        using var font = Font();
        using var bold = Bold();
        float width = 120;
        var height = Pad;
        var rows = new List<Row>();
        foreach (var r in picked)
        {
            var textH = (int)((1 + r.Detail.Properties.Count + r.Detail.Ledger.Count) * Line);
            var h = Math.Max(r.Cell.Height, textH) + Pad;
            rows.Add(new Row(r.Piece, r.Type, r.Detail, r.Cell, h));
            height += h;
            var tw = bold.MeasureText(r.Detail.Name);
            foreach (var (k, v) in r.Detail.Properties) tw = Math.Max(tw, font.MeasureText($"{k}: {v}"));
            foreach (var l in r.Detail.Ledger) tw = Math.Max(tw, font.MeasureText(LedgerText(l.Name, l.Value)));
            width = Math.Max(width, Pad * 2 + r.Cell.Width + Pad + tw);
        }
        if (extra > 0) height += (int)Line + Pad;
        width = Math.Min(width, MaxWidth);

        // Ledger lines, positioned exactly as DrawContent draws them.
        var hot = new List<CounterPopupHotLine>();
        var y = (float)Pad;
        foreach (var r in rows)
        {
            var tx = Pad * 2 + r.Cell.Width;
            var ty = y + 11;   // the name line
            foreach (var _ in r.Detail.Properties) ty += Line;
            foreach (var l in r.Detail.Ledger)
            {
                ty += Line;
                var w = font.MeasureText(LedgerText(l.Name, l.Value));
                hot.Add(new CounterPopupHotLine(tx, ty - 11, w, LedgerBreakdown.For(state, s.Rules, s.Model, LedgerKey.Piece(r.Piece.Id), l.PropertyId)));
            }
            y += r.Height;
        }
        return new CounterPopupLayout(pieces, rows, extra, (int)Math.Ceiling(width), Math.Max(height, 24), hot);
    }

    /// <summary>
    /// Draws the pieces and text (no background, no border) in logical units onto <paramref name="c"/>'s current transform. Pieces are drawn
    /// at <paramref name="pieceZoom"/> and rasterised for <paramref name="deviceScale"/> device pixels per unit.
    /// </summary>
    public void DrawContent(SKCanvas c, float pieceZoom, float deviceScale = 1f)
    {
        using var font = Font();
        using var bold = Bold();
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var dash = SKPathEffect.CreateDash(new[] { 2f, 2f }, 0);
        using var dotted = new SKPaint { Color = SKColors.Gray, StrokeWidth = 1, PathEffect = dash };
        var y = (float)Pad;
        foreach (var r in _rows)
        {
            var cx = Pad + r.Cell.Width / 2f;
            _pieces.Draw(c, r.Piece, r.Type, new PointD(cx, y + r.Cell.Height / 2f), pieceZoom, PieceDrawFlags.None, deviceScale);
            var tx = Pad * 2 + r.Cell.Width;
            var ty = y + 11;
            c.DrawText(r.Detail.Name, tx, ty, SKTextAlign.Left, bold, ink);
            foreach (var (k, v) in r.Detail.Properties)
            {
                ty += Line;
                c.DrawText($"{k}: {v}", tx, ty, SKTextAlign.Left, font, ink);
            }
            foreach (var l in r.Detail.Ledger)
            {
                ty += Line;
                var text = LedgerText(l.Name, l.Value);
                c.DrawText(text, tx, ty, SKTextAlign.Left, font, ink);
                var w = font.MeasureText(text);
                c.DrawLine(tx, ty + 2, tx + w, ty + 2, dotted); // underline marks "hover for the breakdown"
            }
            y += r.Height;
        }
        if (_extra > 0) c.DrawText($"+ {_extra} more", Pad, y + 11, SKTextAlign.Left, font, ink);
    }

    /// <summary>The WinForms popup image: background, content and a 1-pixel black edge, rendered at <paramref name="scale"/> pixels per unit.</summary>
    public SKBitmap RenderBitmap(float scale)
    {
        var bmp = new SKBitmap((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale));
        using var c = new SKCanvas(bmp);
        c.Clear(Background);
        c.Scale(scale); // layout is in 96-dpi units; render at the monitor's pixel density
        DrawContent(c, scale);
        c.ResetMatrix();
        using var edge = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke };
        c.DrawRect(SKRect.Create(0, 0, bmp.Width - 1, bmp.Height - 1), edge);
        return bmp;
    }
}
