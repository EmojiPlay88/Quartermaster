using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Quartermaster.Ocr.Tests;

public sealed class RenderAndLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-ocr-tests-" + Guid.NewGuid().ToString("N"));

    public RenderAndLoadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool IsRed(Color c) => c.R > 180 && c.G < 90 && c.B < 90;
    private static bool IsWhite(Color c) => c.R > 220 && c.G > 220 && c.B > 220;

    /// <summary>Stored 200×100 with a red block top-left; EXIF orientation 6 = display rotated 90° clockwise.</summary>
    [Fact]
    public void ImageLoad_ExifRotated_Upright()
    {
        string path = Path.Combine(_dir, "photo.jpg");
        using (var bmp = new Bitmap(200, 100))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.FillRectangle(Brushes.Red, 0, 0, 40, 40);
            }
            var prop = (PropertyItem)RuntimeHelpers.GetUninitializedObject(typeof(PropertyItem));
            prop.Id = 0x0112;
            prop.Type = 3; // SHORT
            prop.Len = 2;
            prop.Value = BitConverter.GetBytes((ushort)6);
            bmp.SetPropertyItem(prop);
            bmp.Save(path, ImageFormat.Jpeg);
        }

        using var loaded = ImageLoad.Load(path);

        Assert.Equal(100, loaded.Width);
        Assert.Equal(200, loaded.Height);
        Assert.True(IsRed(loaded.GetPixel(80, 20)), "red block should be top-right after rotation");
        Assert.True(IsWhite(loaded.GetPixel(20, 20)));
        Assert.True(IsWhite(loaded.GetPixel(80, 180)));
    }

    [Fact]
    public void ImageLoad_MultiPageTiff_FirstPageOnly()
    {
        string path = Path.Combine(_dir, "scan.tif");
        using (var p1 = new Bitmap(120, 80))
        using (var p2 = new Bitmap(60, 90))
        {
            using (var g = Graphics.FromImage(p1)) g.Clear(Color.Red);
            using (var g = Graphics.FromImage(p2)) g.Clear(Color.Blue);
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/tiff");
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
            p1.Save(path, codec, ps);
            ps.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
            p1.SaveAdd(p2, ps);
            ps.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);
            p1.SaveAdd(ps);
        }

        using var loaded = ImageLoad.Load(path, out int pages);

        Assert.Equal(2, pages);
        Assert.Equal(120, loaded.Width);
        Assert.Equal(80, loaded.Height);
        Assert.True(IsRed(loaded.GetPixel(60, 40)));
    }

    [Fact]
    public void ImageLoad_DoesNotLockFile()
    {
        string path = Path.Combine(_dir, "plain.png");
        using (var bmp = new Bitmap(10, 10)) bmp.Save(path, ImageFormat.Png);
        using var loaded = ImageLoad.Load(path);
        File.Delete(path);
        Assert.Equal(10, loaded.Width);
    }

    private string LetterPdf(int pages = 1)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (int i = 0; i < pages; i++)
        {
            var page = builder.AddPage(PageSize.Letter);
            page.AddText($"Invented page {i + 1}", 12, new PdfPoint(72, 700), font);
        }
        string path = Path.Combine(_dir, "invented.pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    [Fact]
    public async Task PdfRender_GeneratedPdf_PageSizeAt200Dpi()
    {
        string pdf = LetterPdf(pages: 2);

        Assert.Equal(2, PdfPageRender.PageCount(pdf));
        using var bmp = await PdfPageRender.RenderAsync(pdf, 1);

        // US Letter 8.5 × 11 in at 200 DPI.
        Assert.InRange(bmp.Width, 1699, 1701);
        Assert.InRange(bmp.Height, 2199, 2201);
        Assert.Equal(200f, bmp.HorizontalResolution, 1);
        // Text was drawn: some dark pixels near (72pt, 92pt from top) → (200, 256) px.
        bool dark = false;
        for (int x = 200; x < 500 && !dark; x += 2)
            for (int y = 225; y < 270 && !dark; y += 2)
                dark = bmp.GetPixel(x, y).GetBrightness() < 0.5f;
        Assert.True(dark, "rendered text expected near the top-left");
    }

    [Fact]
    public async Task PdfRender_PageOutOfRange_Throws()
    {
        string pdf = LetterPdf();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PdfPageRender.RenderAsync(pdf, 2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PdfPageRender.RenderAsync(pdf, 0));
    }
}
