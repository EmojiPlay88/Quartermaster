using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Quartermaster.App;

/// <summary>Smoke only: draws an invented bordered table (Segoe UI 11 pt at 200 DPI) with a title line above it.</summary>
static class SmokeOcrTable
{
    /// <summary>Writes the PNG into <paramref name="folder"/>; returns its path, the table's region (image px) and the expected cells.</summary>
    public static (string Path, Rectangle Region, string[][] Expected) Write(string folder)
    {
        string[] header = ["Roll", "Clear", "Woods", "Town", "Hills"];
        const int rows = 5, cw = 190, ch = 66, left = 70, top = 170;
        var rng = new Random(2611);
        var expected = new string[rows + 1][];
        expected[0] = header;
        for (var r = 1; r <= rows; r++)
            // Single-digit keys: the first OCR pass skips lone characters; the empty-cell re-read must bring them back.
            expected[r] = new[] { r.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                .Concat(Enumerable.Range(0, header.Length - 1).Select(_ => rng.Next(10, 100).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        int w = left * 2 + cw * header.Length, h = top + ch * (rows + 1) + 80;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        bmp.SetResolution(200, 200);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var title = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point);
            using var font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
            using var bold = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("Invented smoke chart", title, Brushes.Black, left, 50);
            for (var r = 0; r <= rows; r++)
                for (var c = 0; c < header.Length; c++)
                    g.DrawString(expected[r][c], r == 0 ? bold : font, Brushes.Black, new RectangleF(left + c * cw, top + r * ch, cw, ch), fmt);
            using var pen = new Pen(Color.Black, 2);
            for (var r = 0; r <= rows + 1; r++) g.DrawLine(pen, left, top + r * ch, left + header.Length * cw, top + r * ch);
            for (var c = 0; c <= header.Length; c++) g.DrawLine(pen, left + c * cw, top, left + c * cw, top + (rows + 1) * ch);
        }
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "invented-table.png");
        bmp.Save(path, ImageFormat.Png);
        var region = new Rectangle(left - 20, top - 20, cw * header.Length + 40, ch * (rows + 1) + 40);
        return (path, region, expected);
    }
}
