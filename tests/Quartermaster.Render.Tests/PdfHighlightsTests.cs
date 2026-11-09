using System.Text;
using Quartermaster.Render.Search;
using Quartermaster.Vassal;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Quartermaster.Render.Tests;

/// <summary>Search-hit boxes from the text layer of invented PDFs built at test time.</summary>
public class PdfHighlightsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-hl-" + Guid.NewGuid().ToString("N"));

    public PdfHighlightsTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private PdfHighlights Open(byte[] bytes)
    {
        var p = Path.Combine(_tmp, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(p, bytes);
        return PdfHighlights.Open(p);
    }

    private static byte[] Pdf(params string[][] pages) => RulebookSearchTests.Pdf(pages);

    /// <summary>Hand-written one-page PDF (Helvetica 20 pt "Zebra" with its origin at 100,200 on a 400x600 page) with /Rotate.</summary>
    private static byte[] RotatedPdf(int rotate)
    {
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Rotate {rotate} /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            null!, // stream
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        const string content = "BT /F1 20 Tf 100 200 Td (Zebra) Tj ET";
        objs[3] = $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
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

    private static readonly byte[] Rules = Pdf(
        new[] { "Supply lines run from the depot. Supply is key and resupply is not.", "Second line." },
        new[] { "Nothing relevant here." });

    [Fact]
    public void Finds_every_occurrence_on_page()
    {
        using var h = Open(Rules);
        Assert.Equal(2, h.Find(1, "supply").Count);
        Assert.Empty(h.Find(2, "supply"));
    }

    [Fact]
    public void Word_start_matching_like_search()
    {
        using var h = Open(Pdf(new[] { "Supply and resupply." }));
        var boxes = h.Find(1, "supp");
        Assert.Single(boxes);
        Assert.True(boxes[0].X < 0.2); // the first word, not "resupply"
    }

    [Fact]
    public void Case_and_diacritics_insensitive()
    {
        using var h = Open(Pdf(new[] { "The Cafe Regime applies." }));
        Assert.Single(h.Find(1, "CAFÉ"));
        Assert.Single(h.Find(1, "régime"));
    }

    [Fact]
    public void Hit_across_line_break_gives_two_boxes()
    {
        using var h = Open(Pdf(new[] { "The combat", "table decides." }));
        var boxes = h.Find(1, "combat table");
        Assert.Equal(2, boxes.Count);
        Assert.True(boxes[1].Y > boxes[0].Y);
    }

    [Fact]
    public void Boxes_are_page_fractions_top_left_origin()
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        var pg = b.AddPage(PageSize.A4); // 595 x 842
        pg.AddText("Zebra", 20, new PdfPoint(100, 600), font);
        using var h = Open(b.Build());
        var r = h.Find(1, "zebra").Single();
        var w = 595.0; var ph = 842.0;
        // baseline y=600, glyphs span roughly 600-4 .. 600+15 (20 pt Helvetica)
        Assert.InRange(r.X, 100 / w - 0.02, 100 / w + 0.02);
        Assert.InRange(r.Y, (ph - 600 - 16) / ph - 0.02, (ph - 600 - 16) / ph + 0.02);
        Assert.InRange(r.W, 0.07, 0.11);
        Assert.InRange(r.H, 0.015, 0.04);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Rotated_page_boxes_map_to_rendered_orientation(int rotate)
    {
        using var h = Open(RotatedPdf(rotate));
        var r = h.Find(1, "zebra").Single();
        // Unrotated: word spans x 100..~155, y 200..215 (bottom-left origin) on 400x600.
        double cx = (r.X + r.W / 2), cy = (r.Y + r.H / 2);
        double u = 127.0 / 400, v = 1 - 207.0 / 600; // unrotated fractions, top-left origin
        (double ex, double ey, bool wide) = rotate switch
        {
            90 => (1 - v, u, false),
            180 => (1 - u, 1 - v, true),
            270 => (v, 1 - u, false),
            _ => (u, v, true),
        };
        Assert.InRange(cx, ex - 0.03, ex + 0.03);
        Assert.InRange(cy, ey - 0.03, ey + 0.03);
        // Displayed dimensions: width in pixels is 400 (0/180) or 600 (90/270); text runs horizontally only for 0/180.
        // Displayed size is 400x600 (0/180) or 600x400 (90/270); the word is ~52 x 14 pt along the text direction.
        double pw = rotate % 180 == 0 ? 400 : 600, ph = rotate % 180 == 0 ? 600 : 400;
        Assert.InRange(r.W * pw, wide ? 40 : 8, wide ? 60 : 20);
        Assert.InRange(r.H * ph, wide ? 8 : 40, wide ? 20 : 60);
    }

    [Fact]
    public void Page_without_text_has_no_hits()
    {
        var b = new PdfDocumentBuilder();
        b.AddPage(PageSize.A4);
        using var h = Open(b.Build());
        Assert.Empty(h.Find(1, "anything"));
    }

    [Fact]
    public void Term_not_on_page_has_no_hits()
    {
        using var h = Open(Rules);
        Assert.Empty(h.Find(1, "dragon"));
    }

    [Fact]
    public void Page_out_of_range_has_no_hits()
    {
        using var h = Open(Rules);
        Assert.Empty(h.Find(0, "supply"));
        Assert.Empty(h.Find(3, "supply"));
        Assert.Empty(h.Find(-1, "supply"));
    }

    [Fact]
    public void Null_or_blank_term_has_no_hits()
    {
        using var h = Open(Rules);
        Assert.Empty(h.Find(1, null));
        Assert.Empty(h.Find(1, ""));
        Assert.Empty(h.Find(1, "   "));
    }
}
