using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using Quartermaster.Documents;
using Quartermaster.OcrWindows;
using Quartermaster.Render.Ocr;
using SkiaSharp;
using Xunit.Abstractions;

namespace Quartermaster.Ocr.Tests;

/// <summary>Runs only with QUARTERMASTER_OCR_BENCH=1 (and a Windows OCR en-US language).</summary>
public sealed class OcrBenchFactAttribute : FactAttribute
{
    public OcrBenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("QUARTERMASTER_OCR_BENCH") != "1")
            Skip = "Set QUARTERMASTER_OCR_BENCH=1 to run the OCR benchmark.";
        else if (!OcrService.Languages().Contains("en-US", StringComparer.OrdinalIgnoreCase))
            Skip = "OCR language en-US not installed.";
    }
}

/// <summary>
/// Windows.Media.Ocr (WinForms OcrService) vs Tesseract vs the 4a Windows plugin (OcrReader over WindowsRecognizer) on the same
/// invented tables (GDI-drawn, see OcrServiceTests.InventedTable).
/// </summary>
public class OcrBenchmarkTests(ITestOutputHelper output)
{
    private const int Cols = 6, Rows = 4, CellW = 200, CellH = 70, Margin = 60, Inset = 5;

    private static Bitmap Shrink(Bitmap src, double f)
    {
        var dst = new Bitmap((int)Math.Round(src.Width * f), (int)Math.Round(src.Height * f));
        dst.SetResolution(src.HorizontalResolution * (float)f, src.VerticalResolution * (float)f);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.White);
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    private static SKBitmap ToSk(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return SKBitmap.Decode(ms.ToArray());
    }

    private static RectangleF[] Boxes(double f)
    {
        var list = new List<RectangleF>();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                list.Add(new RectangleF((float)((Margin + c * CellW + Inset) * f), (float)((Margin + r * CellH + Inset) * f),
                    (float)((CellW - 2 * Inset) * f), (float)((CellH - 2 * Inset) * f)));
        return list.ToArray();
    }

    private static int Score(IReadOnlyList<OcrWord?> cells, string[][] expected)
    {
        int ok = 0;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                if (cells[r * Cols + c] is { } w && OcrCleanup.Fix(w.Text) == expected[r][c]) ok++;
        return ok;
    }

    [OcrBenchFact]
    public async Task Tesseract_vs_Windows_on_invented_tables()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Table | Windows cells | Windows ms | Tesseract cells | Tesseract ms | Windows (4a plugin) cells | Windows (4a plugin) ms |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        var win = new OcrService();
        using var tess = new TesseractRecognizer();
        var reader = new OcrReader(tess);
        using var plugin = new WindowsRecognizer();
        var pluginReader = new OcrReader(plugin);
        foreach (var (name, bordered, factor) in new[] { ("bordered, 200 DPI", true, 1.0), ("unbordered, 200 DPI", false, 1.0), ("bordered, half size", true, 0.5), ("unbordered, half size", false, 0.5) })
        {
            var (full, expected) = OcrServiceTests.InventedTable(bordered);
            using var img = factor == 1.0 ? full : Shrink(full, factor);
            if (!ReferenceEquals(img, full)) full.Dispose();
            var boxes = Boxes(factor);
            using var sk = ToSk(img);
            var skBoxes = boxes.Select(b => new SKRect(b.Left, b.Top, b.Right, b.Bottom)).ToArray();
            double pad = 4 * factor;

            await win.ReadCellsAsync(img, boxes, pad, "en-US", CancellationToken.None);
            await reader.ReadCellsAsync(sk, skBoxes, pad, "eng", CancellationToken.None);
            await pluginReader.ReadCellsAsync(sk, skBoxes, pad, "en-US", CancellationToken.None);

            var sw = Stopwatch.StartNew();
            var w = await win.ReadCellsAsync(img, boxes, pad, "en-US", CancellationToken.None);
            long wMs = sw.ElapsedMilliseconds;
            sw.Restart();
            var t = await reader.ReadCellsAsync(sk, skBoxes, pad, "eng", CancellationToken.None);
            long tMs = sw.ElapsedMilliseconds;
            sw.Restart();
            var p = await pluginReader.ReadCellsAsync(sk, skBoxes, pad, "en-US", CancellationToken.None);
            long pMs = sw.ElapsedMilliseconds;
            sb.AppendLine($"| {name} | {Score(w, expected)}/24 | {wMs} | {Score(t, expected)}/24 | {tMs} | {Score(p, expected)}/24 | {pMs} |");
        }
        output.WriteLine(sb.ToString());
        var path = Path.Combine(Path.GetTempPath(), "qm-ocr-bench.md");
        File.WriteAllText(path, sb.ToString());
        output.WriteLine("written to " + path);
    }
}
