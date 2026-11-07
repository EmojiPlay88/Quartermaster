using System.Drawing;
using System.Drawing.Imaging;
using System.Buffers.Binary;
using System.IO.Compression;
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

        Assert.Equal(2, await PdfPageRender.PageCountAsync(pdf));
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
    public async Task PdfRenderClamped_OneLoad_ReturnsPageCount_AndClampsPage()
    {
        string pdf = LetterPdf(pages: 2);
        var (bmp, count, page) = await PdfPageRender.RenderClampedAsync(pdf, 5, 50);
        using (bmp)
        {
            Assert.Equal(2, count);
            Assert.Equal(2, page);
            Assert.InRange(bmp.Width, 424, 426); // 8.5 in at 50 DPI
        }
        var (b0, _, p0) = await PdfPageRender.RenderClampedAsync(pdf, 0, 50);
        b0.Dispose();
        Assert.Equal(1, p0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfPageRender.RenderClampedAsync(pdf, 1, 50, cts.Token));
    }

    [Fact]
    public async Task PdfRender_PageOutOfRange_Throws()
    {
        string pdf = LetterPdf();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PdfPageRender.RenderAsync(pdf, 2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PdfPageRender.RenderAsync(pdf, 0));
    }

    private static bool IsBlue(Color c) => c.B > 180 && c.R < 90 && c.G < 90;

    /// <summary>Stored 200×100: red block top-left, blue block top-right; saved as JPEG with an EXIF orientation.</summary>
    private string OrientedJpeg(int orientation, float dpiX = 96, float dpiY = 96)
    {
        string path = Path.Combine(_dir, $"o{orientation}.jpg");
        using var bmp = new Bitmap(200, 100);
        bmp.SetResolution(dpiX, dpiY);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Red, 0, 0, 40, 40);
            g.FillRectangle(Brushes.Blue, 160, 0, 40, 40);
        }
        var prop = (PropertyItem)RuntimeHelpers.GetUninitializedObject(typeof(PropertyItem));
        prop.Id = 0x0112;
        prop.Type = 3; // SHORT
        prop.Len = 2;
        prop.Value = BitConverter.GetBytes((ushort)orientation);
        bmp.SetPropertyItem(prop);
        bmp.Save(path, ImageFormat.Jpeg);
        return path;
    }

    [Theory]
    [InlineData(1, RotateFlipType.RotateNoneFlipNone)]
    [InlineData(2, RotateFlipType.RotateNoneFlipX)]
    [InlineData(3, RotateFlipType.Rotate180FlipNone)]
    [InlineData(4, RotateFlipType.Rotate180FlipX)]
    [InlineData(5, RotateFlipType.Rotate90FlipX)]
    [InlineData(6, RotateFlipType.Rotate90FlipNone)]
    [InlineData(7, RotateFlipType.Rotate270FlipX)]
    [InlineData(8, RotateFlipType.Rotate270FlipNone)]
    [InlineData(0, RotateFlipType.RotateNoneFlipNone)]
    [InlineData(9, RotateFlipType.RotateNoneFlipNone)]
    public void ForOrientation_MapsExifToRotateFlip(int orientation, RotateFlipType expected) =>
        Assert.Equal(expected, ImageLoad.ForOrientation(orientation));

    /// <summary>EXIF 5 = transpose: displayed (x, y) = stored (y, x).</summary>
    [Fact]
    public void ImageLoad_Orientation5_Transposed()
    {
        using var loaded = ImageLoad.Load(OrientedJpeg(5));
        Assert.Equal((100, 200), (loaded.Width, loaded.Height));
        Assert.True(IsRed(loaded.GetPixel(20, 20)), "red stays top-left");
        Assert.True(IsBlue(loaded.GetPixel(20, 180)), "blue goes bottom-left");
        Assert.True(IsWhite(loaded.GetPixel(80, 20)));
        Assert.True(IsWhite(loaded.GetPixel(80, 180)));
    }

    /// <summary>EXIF 7 = transverse: displayed (x, y) = stored (W-1-y, H-1-x) — red bottom-right, blue top-right.</summary>
    [Fact]
    public void ImageLoad_Orientation7_Transversed()
    {
        using var loaded = ImageLoad.Load(OrientedJpeg(7));
        Assert.Equal((100, 200), (loaded.Width, loaded.Height));
        Assert.True(IsRed(loaded.GetPixel(80, 180)), "red goes bottom-right");
        Assert.True(IsBlue(loaded.GetPixel(80, 20)), "blue goes top-right");
        Assert.True(IsWhite(loaded.GetPixel(20, 20)));
        Assert.True(IsWhite(loaded.GetPixel(20, 180)));
    }

    [Fact]
    public void ImageLoad_QuarterTurn_SwapsResolution()
    {
        using var loaded = ImageLoad.Load(OrientedJpeg(6, dpiX: 300, dpiY: 150));
        Assert.Equal(150f, loaded.HorizontalResolution, 1);
        Assert.Equal(300f, loaded.VerticalResolution, 1);
        using var halfTurn = ImageLoad.Load(OrientedJpeg(3, dpiX: 300, dpiY: 150));
        Assert.Equal(300f, halfTurn.HorizontalResolution, 1);
        Assert.Equal(150f, halfTurn.VerticalResolution, 1);
    }

    /// <summary>Valid 8-bit grayscale PNG of the given size, all black (compresses to ~100 KB even when huge).</summary>
    private static byte[] BlackPng(int width, int height)
    {
        static void Chunk(Stream s, string type, byte[] data)
        {
            var buf = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
            s.Write(buf);
            byte[] td = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
            s.Write(td);
            BinaryPrimitives.WriteUInt32BigEndian(buf, Crc32(td));
            s.Write(buf);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth; colour type 0 = grayscale
        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width + 1]; // filter 0 + zeros
            for (int y = 0; y < height; y++) z.Write(row);
        }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void ImageLoad_SmallGeneratedPng_Loads()
    {
        string path = Path.Combine(_dir, "small.png");
        File.WriteAllBytes(path, BlackPng(30, 20));
        using var loaded = ImageLoad.Load(path);
        Assert.Equal((30, 20), (loaded.Width, loaded.Height));
    }

    [Fact]
    public void ImageLoad_Over100Megapixels_Refused()
    {
        string path = Path.Combine(_dir, "huge.png");
        File.WriteAllBytes(path, BlackPng(10_050, 10_000)); // 100.5 MP

        var ex = Assert.Throws<InvalidDataException>(() => ImageLoad.Load(path));
        Assert.Equal("This image is too large to scan (over 100 megapixels).", ex.Message);
    }

    [Fact]
    public async Task PdfRender_GarbageBytes_ThrowsCantOpen()
    {
        string path = Path.Combine(_dir, "garbage.pdf");
        File.WriteAllBytes(path, [.. System.Text.Encoding.ASCII.GetBytes("this is not a PDF at all"), 0, 1, 2, 0xFF]);

        var render = await Assert.ThrowsAsync<InvalidDataException>(() => PdfPageRender.RenderAsync(path, 1));
        Assert.Equal("This PDF can't be opened as a scan.", render.Message);
        var count = await Assert.ThrowsAsync<InvalidDataException>(() => PdfPageRender.PageCountAsync(path));
        Assert.Equal("This PDF can't be opened as a scan.", count.Message);
    }

    [Fact]
    public async Task PdfRender_MissingFile_ThrowsFileNotFound()
    {
        string path = Path.Combine(_dir, "missing.pdf");
        await Assert.ThrowsAsync<FileNotFoundException>(() => PdfPageRender.RenderAsync(path, 1));
        await Assert.ThrowsAsync<FileNotFoundException>(() => PdfPageRender.PageCountAsync(path));
    }

    [Fact]
    public async Task PdfRender_PreCancelled_ThrowsOperationCanceled()
    {
        string pdf = LetterPdf();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfPageRender.RenderAsync(pdf, 1, ct: cts.Token));
    }

    [Fact]
    public void PdfRender_OutputSize_ClampedTo60Megapixels()
    {
        // Letter (816×1056 DIPs) at 200 DPI: exact.
        Assert.Equal((1700u, 2200u, 200.0), PdfPageRender.OutputSize(816, 1056, 200));
        // A 100 × 100 in poster at 200 DPI would be 400 MP: clamped, DPI lowered, aspect kept.
        var (w, h, dpi) = PdfPageRender.OutputSize(9600, 9600, 200);
        Assert.True((long)w * h <= 60_000_000, $"{w}×{h}");
        Assert.InRange((long)w * h, 59_000_000, 60_000_000);
        Assert.Equal(w, h);
        Assert.InRange(dpi, 77.0, 78.0); // sqrt(60e6) px over 100 in
    }
}
