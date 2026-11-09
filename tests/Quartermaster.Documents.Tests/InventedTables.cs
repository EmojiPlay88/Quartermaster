using SkiaSharp;

namespace Quartermaster.Documents.Tests;

/// <summary>Invented OCR fixtures drawn with Skia (no real-game content).</summary>
internal static class InventedTables
{
    internal const int Cols = 6, Rows = 4, CellW = 200, CellH = 70, Margin = 60;

    internal static SKTypeface Face() => SKTypeface.FromFamilyName("DejaVu Sans") ?? SKTypeface.Default;

    /// <summary>6 x 4 two-digit numbers (cell 200 x 70, margin 60, 200 DPI, 11 pt), like OcrServiceTests.InventedTable.</summary>
    internal static (SKBitmap Image, string[][] Expected) Table(bool bordered, int seed = 7341)
    {
        var rng = new Random(seed);
        var expected = new string[Rows][];
        for (int r = 0; r < Rows; r++)
            expected[r] = Enumerable.Range(0, Cols).Select(_ => rng.Next(10, 100).ToString()).ToArray();

        var bmp = new SKBitmap(Cols * CellW + 2 * Margin, Rows * CellH + 2 * Margin);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using var face = Face();
        using var font = new SKFont(face, 11f * 200f / 72f) { Edging = SKFontEdging.Antialias };
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                string t = expected[r][c];
                float w = font.MeasureText(t);
                float x = Margin + c * CellW + (CellW - w) / 2f;
                float y = Margin + r * CellH + CellH / 2f - (font.Metrics.Ascent + font.Metrics.Descent) / 2f;
                canvas.DrawText(t, x, y, SKTextAlign.Left, font, ink);
            }
        if (bordered)
        {
            using var pen = new SKPaint { Color = SKColors.Black, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
            for (int r = 0; r <= Rows; r++) canvas.DrawLine(Margin, Margin + r * CellH, Margin + Cols * CellW, Margin + r * CellH, pen);
            for (int c = 0; c <= Cols; c++) canvas.DrawLine(Margin + c * CellW, Margin, Margin + c * CellW, Margin + Rows * CellH, pen);
        }
        return (bmp, expected);
    }

    /// <summary>The cell rectangles, row by row, inset a little so borders are outside them.</summary>
    internal static SKRect[] Cells(int inset = 5)
    {
        var list = new List<SKRect>();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                list.Add(new SKRect(Margin + c * CellW + inset, Margin + r * CellH + inset,
                    Margin + (c + 1) * CellW - inset, Margin + (r + 1) * CellH - inset));
        return list.ToArray();
    }

    /// <summary>A white page with one line of text at the given pixel size.</summary>
    internal static SKBitmap Line(string text, float px = 34f)
    {
        using var face = Face();
        using var font = new SKFont(face, px) { Edging = SKFontEdging.Antialias };
        int w = (int)font.MeasureText(text) + 80, h = (int)(px * 2.6f);
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawText(text, 40, h / 2f - (font.Metrics.Ascent + font.Metrics.Descent) / 2f, SKTextAlign.Left, font, ink);
        return bmp;
    }
}
