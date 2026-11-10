using BitMiracle.LibTiff.Classic;
using Quartermaster.Render;
using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Documents.Tests;

/// <summary>The scan cache lives under QUARTERMASTER_LAYOUT_ROOT, which these tests point at a temp folder (process-wide, so one collection).</summary>
[Collection("ScanCacheEnv")]
public sealed class ScanLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-scanloader-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldRoot = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);

    public ScanLoaderTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, Path.Combine(_dir, "appdata"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldRoot);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static byte[] PngBytes(int w = 40, int h = 30)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.SteelBlue);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static ScanLoader Loader(Func<string, Stream>? open = null) =>
        new(open ?? (_ => throw new FileNotFoundException()), new string('a', 64));

    [Fact]
    public async Task Pdf_page_renders_at_200_dpi_and_clamps_page()
    {
        var path = Write("three.pdf", TestPdfs.Pdf(["Page one"], ["Page two"], ["Page three"]));
        var src = ScanSource.File(path);
        using var loader = Loader();

        var r2 = await loader.LoadAsync(src, 2, CancellationToken.None);
        using var bmp = r2.Bitmap;
        Assert.Equal((3, 2, null), (r2.PageCount, r2.Page, r2.Note));
        // A4 is 595 x 842 pt: 200 DPI gives about 1653 x 2339 px.
        Assert.InRange(bmp.Width, 1650, 1658);
        Assert.InRange(bmp.Height, 2336, 2344);

        var last = await loader.LoadAsync(src, 99, CancellationToken.None);
        using (last.Bitmap) Assert.Equal((3, 3), (last.PageCount, last.Page));
        var first = await loader.LoadAsync(src, 0, CancellationToken.None);
        using (first.Bitmap) Assert.Equal((3, 1), (first.PageCount, first.Page));
    }

    [Fact]
    public async Task Multi_page_tiff_note()
    {
        var path = Path.Combine(_dir, "two.tif");
        using (var tiff = Tiff.Open(path, "w")!)
        {
            foreach (var (w, h) in new[] { (30, 20), (10, 10) })
            {
                tiff.SetField(TiffTag.IMAGEWIDTH, w);
                tiff.SetField(TiffTag.IMAGELENGTH, h);
                tiff.SetField(TiffTag.SAMPLESPERPIXEL, 3);
                tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
                tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
                tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                tiff.SetField(TiffTag.ROWSPERSTRIP, h);
                for (int y = 0; y < h; y++) tiff.WriteScanline(new byte[w * 3], y);
                tiff.WriteDirectory();
            }
        }
        using var loader = Loader();
        var r = await loader.LoadAsync(ScanSource.File(path), 1, CancellationToken.None);
        using (r.Bitmap)
        {
            Assert.Equal("This TIFF has 2 pages; only the first is shown.", r.Note);
            Assert.Equal((30, 20), (r.Bitmap.Width, r.Bitmap.Height));
            Assert.Equal(1, r.PageCount);
        }
    }

    [Fact]
    public async Task Module_entry_extracted_once()
    {
        int opened = 0;
        using var loader = Loader(_ => { Interlocked.Increment(ref opened); return new MemoryStream(PngBytes()); });
        var src = ScanSource.ModuleImage("images/chart.png");
        var a = loader.LoadAsync(src, 1, CancellationToken.None);
        var b = loader.LoadAsync(src, 1, CancellationToken.None);
        (await a).Bitmap.Dispose();
        (await b).Bitmap.Dispose();
        var c = await loader.LoadAsync(src, 1, CancellationToken.None);
        c.Bitmap.Dispose();
        Assert.Equal(1, loader.ExtractCount);
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task Failed_extraction_is_retried()
    {
        int calls = 0;
        using var loader = Loader(_ => ++calls == 1 ? throw new IOException("disk hiccup") : new MemoryStream(PngBytes()));
        var src = ScanSource.ModuleImage("chart.png");
        await Assert.ThrowsAsync<IOException>(() => loader.LoadAsync(src, 1, CancellationToken.None));
        var r = await loader.LoadAsync(src, 1, CancellationToken.None);
        r.Bitmap.Dispose();
        Assert.Equal(2, loader.ExtractCount);
    }

    [Fact]
    public void Failure_messages_match_winforms()
    {
        var img = ScanSource.ModuleImage("charts/a.png");
        var pdf = ScanSource.ModulePdf("book.pdf", "Core Book");
        Assert.Equal("This image is too large to scan (over 100 megapixels).",
            ScanLoader.FailureMessage(img, 1, new InvalidDataException(ScanImageLoad.TooLargeMessage)));
        Assert.Equal("charts/a.png was not found.", ScanLoader.FailureMessage(img, 1, new FileNotFoundException()));
        Assert.Equal("Core Book was not found.", ScanLoader.FailureMessage(pdf, 1, new DirectoryNotFoundException()));
        Assert.Equal("Core Book has no page 7.", ScanLoader.FailureMessage(pdf, 7, new ArgumentOutOfRangeException("page")));
        Assert.Equal("This PDF can't be opened as a scan.", ScanLoader.FailureMessage(pdf, 1, new InvalidOperationException()));
        Assert.Equal("This file can't be opened as an image.", ScanLoader.FailureMessage(img, 1, new InvalidOperationException()));
        // Out of range on an image is not a page problem.
        Assert.Equal("This file can't be opened as an image.", ScanLoader.FailureMessage(img, 1, new ArgumentOutOfRangeException("x")));
    }

    [Fact]
    public async Task Cancelled_load_disposes_bitmap()
    {
        var path = Write("one.png", PngBytes());
        using var loader = Loader();
        using var cts = new CancellationTokenSource();
        SKBitmap? seen = null;
        loader.Decoded = b => { seen = b; cts.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync(ScanSource.File(path), 1, cts.Token));
        Assert.NotNull(seen);
        Assert.Equal(IntPtr.Zero, seen!.Handle);
    }
}
