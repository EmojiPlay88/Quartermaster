using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Documents.Tests;

public sealed class PdfPagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-docs-tests-" + Guid.NewGuid().ToString("N"));

    public PdfPagesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private string ThreePages() => Write("three.pdf", TestPdfs.Pdf(
        ["Page one", "Invented text"], ["Page two"], ["Page three"]));

    [Fact]
    public void Page_count_and_sizes()
    {
        using var pdf = PdfPages.Open(ThreePages());
        Assert.Equal(3, pdf.PageCount);
        for (int i = 1; i <= 3; i++)
        {
            var s = pdf.PageSize(i);
            Assert.Equal(595, s.W, 1.5);   // PdfPig PageSize.A4 is 595 x 842 pt
            Assert.Equal(842, s.H, 1.5);
        }
    }

    [Fact]
    public async Task Render_page_at_72_dpi_matches_point_size()
    {
        using var pdf = PdfPages.Open(ThreePages());
        var size = pdf.PageSize(1);
        using var bmp = await pdf.RenderAsync(1, 72, CancellationToken.None);
        Assert.InRange(bmp.Width, (int)Math.Floor(size.W), (int)Math.Ceiling(size.W));
        Assert.InRange(bmp.Height, (int)Math.Floor(size.H), (int)Math.Ceiling(size.H));
    }

    [Fact]
    public async Task Render_has_white_background_and_dark_text_pixels()
    {
        using var pdf = PdfPages.Open(ThreePages());
        using var bmp = await pdf.RenderAsync(1, 150, CancellationToken.None);
        Assert.Equal(SKColors.White, bmp.GetPixel(bmp.Width - 5, bmp.Height - 5));
        bool dark = false;
        // Text sits at x = 40 pt, 800 pt from the bottom (about 42 pt from the top).
        for (int x = (int)(40 * 150 / 72.0); x < 400 && !dark; x += 2)
            for (int y = (int)(30 * 150 / 72.0); y < (int)(60 * 150 / 72.0) && !dark; y += 2)
                dark = bmp.GetPixel(x, y).Red < 100;
        Assert.True(dark, "rendered text expected near the top-left");
    }

    [Fact]
    public void Render_clamps_to_60_megapixels()
    {
        var page = new SizeD(2384, 3370);
        double dpi = PdfPages.ClampDpi(page, 1200);
        Assert.True(dpi < 1200);
        long w = (long)Math.Ceiling(page.W * dpi / 72), h = (long)Math.Ceiling(page.H * dpi / 72);
        Assert.True(w * h <= PdfPages.MaxPixels, $"{w}x{h}");
        Assert.InRange(w * h, PdfPages.MaxPixels * 0.99, PdfPages.MaxPixels);
        Assert.Equal(72.0, PdfPages.ClampDpi(new SizeD(612, 792), 72));
    }

    [Fact]
    public async Task Page_out_of_range_is_clamped()
    {
        using var pdf = PdfPages.Open(ThreePages());
        using var hi = await pdf.RenderAsync(99, 40, CancellationToken.None);
        using var lo = await pdf.RenderAsync(-4, 40, CancellationToken.None);
        Assert.True(hi.Width > 0 && lo.Width > 0);
        Assert.Equal(pdf.PageSize(3).W, pdf.PageSize(99).W);
        Assert.Equal(pdf.PageSize(1).H, pdf.PageSize(0).H);
    }

    [Fact]
    public void Open_corrupt_bytes_throws_invalid_data()
    {
        var path = Write("bad.pdf", [.. System.Text.Encoding.ASCII.GetBytes("this is not a PDF at all"), 0, 1, 2, 0xFF]);
        var ex = Assert.Throws<InvalidDataException>(() => PdfPages.Open(path));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Open_does_not_lock_file()
    {
        var path = ThreePages();
        using var pdf = PdfPages.Open(path);
        File.Delete(path);
        Assert.False(File.Exists(path));
        Assert.Equal(3, pdf.PageCount);
    }

    [Fact]
    public async Task Concurrent_renders_from_many_threads_all_succeed()
    {
        using var a = PdfPages.Open(ThreePages());
        using var b = PdfPages.Open(Write("other.pdf", TestPdfs.Pdf(["x"], ["y"])));
        var threads = Enumerable.Range(0, 8).Select(t => Task.Factory.StartNew(async () =>
        {
            var doc = t % 2 == 0 ? a : b;
            for (int i = 0; i < 4; i++)
            {
                using var bmp = await doc.RenderAsync(1 + i % 2, 60, CancellationToken.None);
                Assert.True(bmp.Width > 0);
            }
        }, TaskCreationOptions.LongRunning).Unwrap()).ToArray();
        await Task.WhenAll(threads);
    }

    [Fact]
    public async Task Dispose_waits_for_running_render()
    {
        var seen = new List<Exception>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e) { lock (seen) seen.Add(e.Exception); }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var pdf = PdfPages.Open(ThreePages());
            var tasks = Enumerable.Range(0, 6).Select(_ => pdf.RenderAsync(1, 300, CancellationToken.None)).ToArray();
            await Task.Delay(30);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            pdf.Dispose();
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5), $"Dispose took {sw.Elapsed}");
            Assert.All(tasks, t => Assert.True(t.IsCompleted, "every task is finished once Dispose returns"));
            foreach (var t in tasks)
            {
                try { (await t).Dispose(); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
            }
            await Assert.ThrowsAsync<ObjectDisposedException>(() => pdf.RenderAsync(1, 72, CancellationToken.None));
            for (int i = 0; i < 5; i++) { await Task.Delay(10); GC.Collect(); GC.WaitForPendingFinalizers(); }
            Assert.Empty(seen);
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    [Fact]
    public async Task Dispose_of_one_document_does_not_wait_behind_another_documents_queue()
    {
        using var a = PdfPages.Open(ThreePages());
        using var b = PdfPages.Open(ThreePages());
        // A queue of renders for B keeps the shared worker busy. Each bitmap is disposed as soon as it completes,
        // so only a few are alive at once (300 dpi, not 600: the queue length, not the size, is what matters).
        var slow = Enumerable.Range(0, 8).Select(async _ =>
        {
            (await b.RenderAsync(1, 300, CancellationToken.None)).Dispose();
        }).ToArray();
        var queuedForA = a.RenderAsync(1, 72, CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        a.Dispose();
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Dispose(A) waited {sw.Elapsed}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedForA);
        await Task.WhenAll(slow);
    }

    [Fact]
    public void ClampDpi_treats_non_finite_or_non_positive_dpi_safely()
    {
        var page = new SizeD(612, 792);
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -5 })
            Assert.Equal(72.0, PdfPages.ClampDpi(page, bad));
    }

    [Fact]
    public async Task Cancelled_render_nobody_awaits_is_not_unobserved()
    {
        var seen = new List<Exception>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e) { lock (seen) seen.Add(e.Exception); }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            using var pdf = PdfPages.Open(ThreePages());
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Forget(pdf, cts.Token);
            for (int i = 0; i < 10; i++) { await Task.Delay(20); GC.Collect(); GC.WaitForPendingFinalizers(); }
            Assert.Empty(seen);
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Forget(PdfPages pdf, CancellationToken ct) => _ = pdf.RenderAsync(1, 72, ct);

    [Theory]
    [InlineData(0, 200, 100)]
    [InlineData(90, 100, 200)]
    [InlineData(180, 200, 100)]
    [InlineData(270, 100, 200)]
    public void Rotated_page_size_is_swapped(int rotate, double w, double h)
    {
        using var pdf = PdfPages.Open(Write($"rot{rotate}.pdf", TestPdfs.RotatedPdf(rotate)));
        var s = pdf.PageSize(1);
        Assert.Equal(w, s.W, 0.5);
        Assert.Equal(h, s.H, 0.5);
    }

    [Fact]
    public async Task Rendered_rotated_page_has_swapped_pixels()
    {
        using var pdf = PdfPages.Open(Write("rot90.pdf", TestPdfs.RotatedPdf(90)));
        using var bmp = await pdf.RenderAsync(1, 72, CancellationToken.None);
        Assert.Equal((100, 200), (bmp.Width, bmp.Height));
    }
}
