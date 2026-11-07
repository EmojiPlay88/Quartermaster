using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Quartermaster.Render.Ocr;
using Xunit.Abstractions;

namespace Quartermaster.Ocr.Tests;

/// <summary>Runs only when Windows has an OCR language installed (en-US is used by the tests).</summary>
public sealed class OcrFactAttribute : FactAttribute
{
    public OcrFactAttribute()
    {
        var langs = OcrService.Languages();
        if (langs.Count == 0)
            Skip = "No Windows OCR language installed; OCR integration tests skipped.";
        else if (!langs.Contains("en-US", StringComparer.OrdinalIgnoreCase))
            Skip = "OCR language en-US not installed; OCR integration tests skipped.";
    }
}

public class OcrServiceTests(ITestOutputHelper output)
{
    private const string Lang = "en-US";

    private static Bitmap Blank(int w, int h, float dpi = 200)
    {
        var bmp = new Bitmap(w, h);
        bmp.SetResolution(dpi, dpi);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        return bmp;
    }

    private static Graphics Pen(Bitmap bmp)
    {
        var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.None;
        return g;
    }

    /// <summary>Invented 6-column × 4-row table of two-digit numbers (fixed seed), Segoe UI 11 pt at 200 DPI.</summary>
    private static (Bitmap Image, string[][] Expected) InventedTable(bool bordered)
    {
        const int cols = 6, rows = 4, cw = 200, ch = 70, margin = 60;
        var rng = new Random(7341);
        var expected = new string[rows][];
        for (int r = 0; r < rows; r++)
            expected[r] = Enumerable.Range(0, cols).Select(_ => rng.Next(10, 100).ToString()).ToArray();

        var bmp = Blank(cols * cw + 2 * margin, rows * ch + 2 * margin);
        using var g = Pen(bmp);
        using var font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                g.DrawString(expected[r][c], font, Brushes.Black,
                    new RectangleF(margin + c * cw, margin + r * ch, cw, ch), fmt);
        if (bordered)
        {
            using var pen = new System.Drawing.Pen(Color.Black, 2);
            for (int r = 0; r <= rows; r++) g.DrawLine(pen, margin, margin + r * ch, margin + cols * cw, margin + r * ch);
            for (int c = 0; c <= cols; c++) g.DrawLine(pen, margin + c * cw, margin, margin + c * cw, margin + rows * ch);
        }
        return (bmp, expected);
    }

    private async Task<(int Exact, int Total)> ReadTable(bool bordered)
    {
        var (img, expected) = InventedTable(bordered);
        using (img)
        {
            var words = await new OcrService().ReadAsync(img, null, Lang, CancellationToken.None);
            var grid = OcrResultModel.BuildGrid(words);
            int exact = 0, total = 0;
            for (int r = 0; r < expected.Length; r++)
                for (int c = 0; c < expected[r].Length; c++)
                {
                    total++;
                    string got = r < grid.Length && c < grid[r].Length ? OcrCleanup.Fix(grid[r][c]) : "";
                    if (got == expected[r][c]) exact++;
                    else output.WriteLine($"  miss r{r}c{c}: expected '{expected[r][c]}', got '{got}'");
                }
            output.WriteLine($"{(bordered ? "bordered" : "unbordered")}: {exact}/{total} exact; grid =");
            foreach (var row in grid) output.WriteLine("  " + string.Join(" | ", row));
            output.WriteLine("raw words: " + string.Join(" ", words.Select(w => w.Text)));
            return (exact, total);
        }
    }

    [OcrFact]
    public async Task Read_InventedTable_RecoversDigits()
    {
        var plain = await ReadTable(bordered: false);
        var boxed = await ReadTable(bordered: true);
        Assert.True(plain.Exact >= 0.95 * plain.Total, $"unbordered {plain.Exact}/{plain.Total}");
        Assert.True(boxed.Exact >= 0.95 * boxed.Total, $"bordered {boxed.Exact}/{boxed.Total}");
    }

    [OcrFact]
    public async Task Read_Region_MapsBoxesToImagePixels()
    {
        using var img = Blank(1600, 1000);
        using var font = new Font("Segoe UI", 14f, GraphicsUnit.Point);
        var at = new PointF(900, 600);
        SizeF size;
        using (var g = Pen(img))
        {
            g.DrawString("Outside", font, Brushes.Black, 100, 100);
            g.DrawString("4821", font, Brushes.Black, at);
            size = g.MeasureString("4821", font);
        }
        var region = new Rectangle(850, 550, 400, 200);

        var words = await new OcrService().ReadAsync(img, region, Lang, CancellationToken.None);

        var w = Assert.Single(words);
        Assert.Equal("4821", w.Text);
        Assert.True(w.X >= region.X && w.Y >= region.Y && w.X + w.W <= region.Right && w.Y + w.H <= region.Bottom,
            $"box {w} outside region");
        // MeasureString pads the ink by roughly 1/6 em each side; the glyph box sits inside the measured box.
        Assert.InRange(w.X, at.X, at.X + 30);
        Assert.InRange(w.Y, at.Y, at.Y + size.Height / 2);
        Assert.InRange(w.X + w.W, at.X + size.Width - 40, at.X + size.Width);
    }

    [OcrFact]
    public async Task Read_TinyRegion_Upscaled_StillReads()
    {
        // Genuinely small text: ~8 px tall digits (11 px em) in a 60×24 px region → upscaled re-read.
        using var img = Blank(400, 200, dpi: 96);
        using (var g = Pen(img))
        using (var font = new Font("Segoe UI", 11f, GraphicsUnit.Pixel))
            g.DrawString("57", font, Brushes.Black, 200, 100);
        var region = new Rectangle(192, 94, 60, 24);

        var words = await new OcrService().ReadAsync(img, region, Lang, CancellationToken.None);

        var w = Assert.Single(words);
        Assert.Equal("57", OcrCleanup.Fix(w.Text));
        Assert.True(w.H < OcrService.UpscaleBelowWordHeight, $"text should be small, was {w.H} px");
        Assert.InRange(w.X, region.X, region.Right);
        Assert.InRange(w.Y, region.Y, region.Bottom);
    }

    [Fact]
    public void Scale_NativeFitsEngineMaximum_UpscaleIsCapped()
    {
        Assert.Equal(1.0, OcrService.NativeScale(800, 600, 10000));
        Assert.Equal(0.5, OcrService.NativeScale(4000, 9000, 4500), 6);  // too big: scaled down to fit
        Assert.Equal(2.0, OcrService.UpscaleFactor(300, 100, 10000));
        Assert.Equal(1.5, OcrService.UpscaleFactor(3000, 500, 4500), 6);  // ~2× capped
    }

    [Fact]
    public void MedianHeight_OfWords()
    {
        Assert.Equal(0, OcrService.MedianHeight([]));
        Assert.Equal(15, OcrService.MedianHeight([new("a", 0, 0, 5, 10), new("b", 0, 0, 5, 20)]));
        Assert.Equal(12, OcrService.MedianHeight([new("a", 0, 0, 5, 30), new("b", 0, 0, 5, 12), new("c", 0, 0, 5, 8)]));
    }

    [Fact]
    public async Task Read_Cancelled_Throws_OperationCanceled()
    {
        using var img = Blank(200, 100);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new OcrService().ReadAsync(img, null, Lang, cts.Token));
    }

    [OcrFact]
    public async Task Read_CancelledDuringRead_Throws_OperationCanceled()
    {
        var (img, _) = InventedTable(bordered: false);
        using (img)
        {
            using var cts = new CancellationTokenSource();
            var task = new OcrService().ReadAsync(img, null, Lang, cts.Token);
            cts.Cancel();
            // The read may finish before the cancel lands; a completed read is fine, otherwise it must be a cancel.
            try { await task; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task NoLanguage_ThrowsWithCopy()
    {
        var svc = new OcrService(_ => null);
        using var img = Blank(200, 100);

        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(
            () => svc.ReadAsync(img, null, "en-US", CancellationToken.None));

        Assert.Equal("Windows has no OCR language installed. Add one in Settings → Time & language → Language (Optical character recognition).", ex.Message);
        Assert.False(svc.Available);
    }

    [OcrFact]
    public async Task Read_RegionOutsideImage_ReturnsEmpty()
    {
        using var img = Blank(200, 100);
        var words = await new OcrService().ReadAsync(img, new Rectangle(500, 500, 10, 10), Lang, CancellationToken.None);
        Assert.Empty(words);
    }

    [OcrFact]
    public void Languages_DefaultIsInstalled()
    {
        var def = OcrService.DefaultLanguage();
        Assert.NotNull(def);
        Assert.Contains(def, OcrService.Languages());
        Assert.True(new OcrService().Available);
    }
}
