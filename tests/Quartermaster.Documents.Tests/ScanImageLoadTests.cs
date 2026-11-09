using System.Buffers.Binary;
using System.IO.Compression;
using BitMiracle.LibTiff.Classic;
using SkiaSharp;

namespace Quartermaster.Documents.Tests;

public sealed class ScanImageLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-scan-tests-" + Guid.NewGuid().ToString("N"));

    public ScanImageLoadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool IsRed(SKColor c) => c.Red > 180 && c.Green < 90 && c.Blue < 90;
    private static bool IsBlue(SKColor c) => c.Blue > 180 && c.Red < 90 && c.Green < 90;
    private static bool IsWhite(SKColor c) => c.Red > 220 && c.Green > 220 && c.Blue > 220;

    /// <summary>Stored 200x100: red block top-left, blue block top-right; saved as JPEG with an EXIF orientation segment.</summary>
    private string OrientedJpeg(int orientation, string? name = null)
    {
        string path = Path.Combine(_dir, name ?? $"o{orientation}.jpg");
        using var bmp = new SKBitmap(200, 100);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var red = new SKPaint { Color = SKColors.Red };
            using var blue = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(0, 0, 40, 40, red);
            canvas.DrawRect(160, 0, 40, 40, blue);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, 100);
        var jpeg = data.ToArray();
        // APP1 / Exif / little-endian TIFF header / one IFD0 entry (Orientation, SHORT).
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 34, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0,
            1, 0,
            0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0,
            0, 0, 0, 0,
        ];
        using var fs = File.Create(path);
        fs.Write(jpeg, 0, 2);       // SOI
        fs.Write(exif);
        fs.Write(jpeg, 2, jpeg.Length - 2);
        return path;
    }

    [Fact]
    public void Exif_rotated_is_upright()
    {
        using var loaded = ScanImageLoad.Load(OrientedJpeg(6, "photo.jpg"));
        Assert.Equal((100, 200), (loaded.Width, loaded.Height));
        Assert.True(IsRed(loaded.GetPixel(80, 20)), "red block should be top-right after rotation");
        Assert.True(IsBlue(loaded.GetPixel(80, 180)), "blue block should be bottom-right after rotation");
        Assert.True(IsWhite(loaded.GetPixel(20, 20)));
        Assert.True(IsWhite(loaded.GetPixel(20, 180)));
    }

    [Fact]
    public void Orientation_1_is_unchanged()
    {
        using var loaded = ScanImageLoad.Load(OrientedJpeg(1));
        Assert.Equal((200, 100), (loaded.Width, loaded.Height));
        Assert.True(IsRed(loaded.GetPixel(20, 20)));
        Assert.True(IsBlue(loaded.GetPixel(180, 20)));
    }

    [Fact]
    public void Orientation_2_is_mirrored()
    {
        using var loaded = ScanImageLoad.Load(OrientedJpeg(2));
        Assert.Equal((200, 100), (loaded.Width, loaded.Height));
        Assert.True(IsBlue(loaded.GetPixel(20, 20)));
        Assert.True(IsRed(loaded.GetPixel(180, 20)));
    }

    [Fact]
    public void Orientation_3_is_half_turn()
    {
        using var loaded = ScanImageLoad.Load(OrientedJpeg(3));
        Assert.Equal((200, 100), (loaded.Width, loaded.Height));
        Assert.True(IsRed(loaded.GetPixel(180, 80)));
        Assert.True(IsBlue(loaded.GetPixel(20, 80)));
    }

    [Fact]
    public void Orientation_4_is_flipped_vertically()
    {
        using var loaded = ScanImageLoad.Load(OrientedJpeg(4));
        Assert.Equal((200, 100), (loaded.Width, loaded.Height));
        Assert.True(IsRed(loaded.GetPixel(20, 80)));
        Assert.True(IsBlue(loaded.GetPixel(180, 80)));
    }

    /// <summary>EXIF 5 = transpose: displayed (x, y) = stored (y, x).</summary>
    [Fact]
    public void Orientation_5_transposed()
    {
        using var loaded = ScanImageLoad.Load(OrientedJpeg(5));
        Assert.Equal((100, 200), (loaded.Width, loaded.Height));
        Assert.True(IsRed(loaded.GetPixel(20, 20)), "red stays top-left");
        Assert.True(IsBlue(loaded.GetPixel(20, 180)), "blue goes bottom-left");
        Assert.True(IsWhite(loaded.GetPixel(80, 20)));
        Assert.True(IsWhite(loaded.GetPixel(80, 180)));
    }

    /// <summary>EXIF 7 = transverse: red bottom-right, blue top-right.</summary>
    [Fact]
    public void Orientation_7_transversed()
    {
        using var loaded = ScanImageLoad.Load(OrientedJpeg(7));
        Assert.Equal((100, 200), (loaded.Width, loaded.Height));
        Assert.True(IsRed(loaded.GetPixel(80, 180)), "red goes bottom-right");
        Assert.True(IsBlue(loaded.GetPixel(80, 20)), "blue goes top-right");
        Assert.True(IsWhite(loaded.GetPixel(20, 20)));
        Assert.True(IsWhite(loaded.GetPixel(20, 180)));
    }

    [Fact]
    public void Orientation_8_is_quarter_turn_back()
    {
        using var loaded = ScanImageLoad.Load(OrientedJpeg(8));
        Assert.Equal((100, 200), (loaded.Width, loaded.Height));
        Assert.True(IsRed(loaded.GetPixel(20, 180)), "red goes bottom-left");
        Assert.True(IsBlue(loaded.GetPixel(20, 20)), "blue goes top-left");
    }

    private static void WriteTiffPage(Tiff tiff, int w, int h, byte r, byte g, byte b)
    {
        tiff.SetField(TiffTag.IMAGEWIDTH, w);
        tiff.SetField(TiffTag.IMAGELENGTH, h);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.ORIENTATION, BitMiracle.LibTiff.Classic.Orientation.TOPLEFT);
        tiff.SetField(TiffTag.ROWSPERSTRIP, h);
        var row = new byte[w * 3];
        for (int x = 0; x < w; x++) { row[x * 3] = r; row[x * 3 + 1] = g; row[x * 3 + 2] = b; }
        for (int y = 0; y < h; y++) tiff.WriteScanline(row, y);
        tiff.WriteDirectory();
    }

    [Fact]
    public void Multi_page_tiff_first_page_only()
    {
        string path = Path.Combine(_dir, "scan.tif");
        using (var tiff = Tiff.Open(path, "w")!)
        {
            WriteTiffPage(tiff, 120, 80, 255, 0, 0);
            WriteTiffPage(tiff, 60, 90, 0, 0, 255);
        }

        using var loaded = ScanImageLoad.Load(path, out int pages);

        Assert.Equal(2, pages);
        Assert.Equal((120, 80), (loaded.Width, loaded.Height));
        Assert.True(IsRed(loaded.GetPixel(60, 40)));
    }

    [Fact]
    public void Single_page_tiff_reports_one_page_and_does_not_lock()
    {
        string path = Path.Combine(_dir, "one.tiff");
        using (var tiff = Tiff.Open(path, "w")!) WriteTiffPage(tiff, 30, 20, 0, 0, 255);
        using var loaded = ScanImageLoad.Load(path, out int pages);
        File.Delete(path);
        Assert.Equal(1, pages);
        Assert.True(IsBlue(loaded.GetPixel(5, 5)));
    }

    [Fact]
    public void Does_not_lock_file()
    {
        string path = Path.Combine(_dir, "plain.png");
        using (var bmp = new SKBitmap(10, 10))
        using (var data = bmp.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(path, data.ToArray());
        using var loaded = ScanImageLoad.Load(path, out int pages);
        File.Delete(path);
        Assert.Equal(10, loaded.Width);
        Assert.Equal(1, pages);
    }

    [Fact]
    public void Transparent_png_loads_on_white()
    {
        string path = Path.Combine(_dir, "alpha.png");
        using (var bmp = new SKBitmap(new SKImageInfo(20, 20, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.Transparent);
                using var red = new SKPaint { Color = SKColors.Red };
                canvas.DrawRect(0, 0, 10, 10, red);
            }
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        using var loaded = ScanImageLoad.Load(path);
        Assert.True(IsWhite(loaded.GetPixel(15, 15)), "transparent area must be white, not black");
        Assert.True(IsRed(loaded.GetPixel(5, 5)));
        Assert.Equal(255, loaded.GetPixel(15, 15).Alpha);
    }

    [Fact]
    public void Tiff_with_alpha_loads_on_white()
    {
        string path = Path.Combine(_dir, "alpha.tif");
        using (var tiff = Tiff.Open(path, "w")!)
        {
            tiff.SetField(TiffTag.IMAGEWIDTH, 4);
            tiff.SetField(TiffTag.IMAGELENGTH, 4);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 4);
            tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tiff.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.UNASSALPHA });
            tiff.SetField(TiffTag.ROWSPERSTRIP, 4);
            var row = new byte[16]; // left half opaque red, right half fully transparent black
            for (int x = 0; x < 2; x++) { row[x * 4] = 255; row[x * 4 + 3] = 255; }
            for (int y = 0; y < 4; y++) tiff.WriteScanline(row, y);
        }
        using var loaded = ScanImageLoad.Load(path);
        Assert.True(IsRed(loaded.GetPixel(0, 0)));
        Assert.True(IsWhite(loaded.GetPixel(3, 3)), "transparent area must be white");
    }

    [Fact]
    public void Small_generated_png_loads()
    {
        string path = Path.Combine(_dir, "small.png");
        File.WriteAllBytes(path, BlackPng(30, 20));
        using var loaded = ScanImageLoad.Load(path);
        Assert.Equal((30, 20), (loaded.Width, loaded.Height));
    }

    [Fact]
    public void Over_100_megapixels_refused_from_header()
    {
        string path = Path.Combine(_dir, "huge.png");
        File.WriteAllBytes(path, BlackPng(10_050, 10_000)); // 100.5 MP

        var ex = Assert.Throws<InvalidDataException>(() => ScanImageLoad.Load(path));
        Assert.Equal("This image is too large to scan (over 100 megapixels).", ex.Message);
        Assert.Equal(ScanImageLoad.TooLargeMessage, ex.Message);
    }

    [Fact]
    public void Garbage_is_invalid_data()
    {
        string path = Path.Combine(_dir, "junk.png");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        Assert.Throws<InvalidDataException>(() => ScanImageLoad.Load(path));
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
}
