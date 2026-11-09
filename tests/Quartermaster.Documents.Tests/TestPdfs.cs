using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Quartermaster.Documents.Tests;

/// <summary>Invented PDFs built at test time.</summary>
internal static class TestPdfs
{
    internal static byte[] Pdf(params string[][] pages)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var pg = b.AddPage(PageSize.A4);
            double y = 800;
            foreach (var l in lines)
            {
                pg.AddText(l, 10, new PdfPoint(40, y), font);
                y -= 14;
            }
        }
        return b.Build();
    }

    /// <summary>One 200 x 100 pt page with the given /Rotate, hand-written with correct xref offsets.</summary>
    internal static byte[] RotatedPdf(int rotate)
    {
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Rotate {rotate} >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
