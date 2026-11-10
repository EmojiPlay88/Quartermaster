using System.Text;
using Quartermaster.Render.Search;
using SkiaSharp;

namespace Quartermaster.Documents.Tests;

/// <summary>Search-hit boxes from the text layer must land on the ink PDFium renders, on rotated pages with an offset crop box.</summary>
public sealed class HighlightInkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-hl-ink-" + Guid.NewGuid().ToString("N"));

    public HighlightInkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Invented one-page PDF: "Zebra" in 30 pt Helvetica at user-space 120,250 on a 612 x 792 media box, optional crop box.</summary>
    private static byte[] Pdf(int rotate, string? crop)
    {
        const string content = "BT /F1 30 Tf 120 250 Td (Zebra) Tj ET";
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {(crop == null ? "" : $"/CropBox [{crop}]")} /Rotate {rotate} /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objs.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0, "50 60 562 752")]
    [InlineData(90, "50 60 562 752")]
    [InlineData(180, "50 60 562 752")]
    [InlineData(270, "50 60 562 752")]
    public async Task Highlight_boxes_cover_rendered_ink_on_rotated_cropped_pages(int rotate, string? crop)
    {
        var path = Path.Combine(_dir, $"p{rotate}-{crop?.Length}.pdf");
        File.WriteAllBytes(path, Pdf(rotate, crop));
        using var hl = PdfHighlights.Open(path);
        var box = hl.Find(1, "zebra").Single();

        using var pages = PdfPages.Open(path);
        using var bmp = await pages.RenderAsync(1, 72, CancellationToken.None);
        int w = bmp.Width, h = bmp.Height;
        int x0 = (int)Math.Floor(box.X * w), y0 = (int)Math.Floor(box.Y * h);
        int x1 = (int)Math.Ceiling((box.X + box.W) * w), y1 = (int)Math.Ceiling((box.Y + box.H) * h);

        int inside = 0, outside = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 100 || c.Green > 100 || c.Blue > 100) continue;   // not dark
                if (x >= x0 - 1 && x <= x1 && y >= y0 - 1 && y <= y1) inside++; else outside++;
            }
        Assert.True(inside > 100, $"only {inside} dark pixels inside the box");
        Assert.True(outside <= inside / 20, $"{outside} dark pixels outside the box ({inside} inside)");
    }
}
