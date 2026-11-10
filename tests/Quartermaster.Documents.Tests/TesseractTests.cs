using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Documents.Tests;

public class TesseractTests
{
    [Fact]
    public void Languages_include_eng_from_bundled_folder()
    {
        using var t = new TesseractRecognizer();
        Assert.Equal("Tesseract", t.Name);
        Assert.Contains("eng", t.Languages);
        Assert.Equal("eng", t.DefaultLanguage);
        Assert.Equal(10000, t.MaxImageDimension);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reads_invented_table_digits(bool bordered)
    {
        using var t = new TesseractRecognizer();
        var reader = new OcrReader(t);
        var (img, expected) = InventedTables.Table(bordered);
        using (img)
        {
            var cells = await reader.ReadCellsAsync(img, InventedTables.Cells(), 4, "eng", CancellationToken.None);
            int exact = 0;
            for (int r = 0; r < InventedTables.Rows; r++)
                for (int c = 0; c < InventedTables.Cols; c++)
                {
                    var got = cells[r * InventedTables.Cols + c]?.Text.Trim();
                    if (got == expected[r][c]) exact++;
                }
            Assert.True(exact >= 22, $"{(bordered ? "bordered" : "unbordered")}: {exact}/24 exact");
        }
    }

    [Fact]
    public async Task Reads_a_line_of_words()
    {
        const string text = "The quick brown fox jumps over the lazy dog";
        using var t = new TesseractRecognizer();
        using var img = InventedTables.Line(text);
        var words = await new OcrReader(t).ReadAsync(img, null, "eng", CancellationToken.None);
        var got = words.Select(w => w.Text.Trim()).ToList();
        var want = text.Split(' ');
        int hit = want.Count(w => got.Contains(w));
        Assert.True(hit >= 0.9 * want.Length, $"{hit}/{want.Length}: {string.Join(" ", got)}");
        Assert.All(words, w => Assert.True(w.W > 0 && w.H > 0 && w.X >= 0 && w.X + w.W <= img.Width + 1));
    }

    [Fact]
    public async Task Missing_folder_gives_unavailable_message()
    {
        string none = Path.Combine(Path.GetTempPath(), "qm-no-tessdata-" + Guid.NewGuid().ToString("N"));
        using var t = new TesseractRecognizer(none);
        Assert.Empty(t.Languages);
        Assert.Null(t.DefaultLanguage);
        Assert.Equal("No OCR language data was found. Reinstall Quartermaster to restore English.", t.UnavailableMessage);
        using var img = InventedTables.Line("Hello");
        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(() => new OcrReader(t).ReadAsync(img, null, "eng", CancellationToken.None));
        Assert.Equal(t.UnavailableMessage, ex.Message);
        Assert.False(Directory.Exists(none));
    }

    [Fact]
    public async Task Parallel_reads_are_serialized_and_succeed()
    {
        using var t = new TesseractRecognizer();
        var reader = new OcrReader(t);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var img = InventedTables.Line("Alpha Bravo Charlie");
            return await reader.ReadAsync(img, null, "eng", CancellationToken.None);
        })).ToArray();
        var all = await Task.WhenAll(tasks);
        Assert.All(all, w => Assert.Contains(w, x => x.Text.Contains("Bravo")));
    }

    [Theory]
    [InlineData("1/2")]
    [InlineData("(2)")]
    public async Task Cells_with_real_symbols_keep_them(string text)
    {
        using var t = new TesseractRecognizer();
        using var img = InventedTables.Line(text, 60f);
        var cells = await new OcrReader(t).ReadCellsAsync(img, [new SKRect(0, 0, img.Width, img.Height)], 4, "eng", CancellationToken.None);
        Assert.Equal(text, cells[0]?.Text.Trim());
    }

    [Fact]
    public async Task Non_ascii_tessdata_path_works()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qm-tessdé-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "tessdata", "eng.traineddata"), Path.Combine(dir, "eng.traineddata"));
            using var t = new TesseractRecognizer(dir);
            using var img = InventedTables.Line("Alpha Bravo Charlie");
            var words = await new OcrReader(t).ReadAsync(img, null, "eng", CancellationToken.None);
            Assert.Contains(words, w => w.Text.Contains("Bravo"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Language_matches_case_insensitively()
    {
        using var t = new TesseractRecognizer();
        using var img = InventedTables.Line("Alpha Bravo Charlie");
        var words = await t.RecognizeAsync(img, OcrLayout.Block, "ENG", CancellationToken.None);
        Assert.Contains(words, w => w.Text.Contains("Bravo"));
    }

    [Fact]
    public async Task Native_load_failure_becomes_unavailable()
    {
        using var t = new TesseractRecognizer(null, () => throw new DllNotFoundException("libtesseract missing"));
        using var img = InventedTables.Line("Hello");
        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(() => t.RecognizeAsync(img, OcrLayout.Block, "eng", CancellationToken.None));
        Assert.Equal("OCR could not be started on this computer: libtesseract missing", ex.Message);
    }

    [Fact]
    public async Task Read_after_timed_out_dispose_throws_object_disposed()
    {
        var t = new TesseractRecognizer { DisposeWait = TimeSpan.FromMilliseconds(50) };
        using var img = InventedTables.Line("Hello");
        var hold = await t.HoldGateAsync(); // a "running read" that outlasts Dispose
        t.Dispose();                        // times out waiting for the gate, but must still refuse later reads
        hold.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => t.RecognizeAsync(img, OcrLayout.Block, "eng", CancellationToken.None));
    }

    [Fact]
    public async Task Queued_reads_hold_no_pool_threads()
    {
        using var t = new TesseractRecognizer();
        using var img = InventedTables.Line("Alpha", 30f);
        var hold = await t.HoldGateAsync();
        int before = ThreadPool.ThreadCount;
        var reads = Enumerable.Range(0, 64).Select(_ => t.RecognizeAsync(img, OcrLayout.Block, "eng", CancellationToken.None)).ToArray();
        await Task.Delay(400);
        int grown = ThreadPool.ThreadCount - before;
        Assert.True(grown <= 4, $"pool grew by {grown}");
        var fresh = Task.Run(() => 1);
        Assert.Same(fresh, await Task.WhenAny(fresh, Task.Delay(500)));
        Assert.All(reads, r => Assert.False(r.IsCompleted));
        hold.Dispose();
        var all = await Task.WhenAll(reads);
        Assert.Equal(64, all.Length);
    }

    [Fact]
    public async Task Cancelled_queued_read_leaves_queue()
    {
        using var t = new TesseractRecognizer();
        using var img = InventedTables.Line("Alpha", 30f);
        var hold = await t.HoldGateAsync();
        using var cts = new CancellationTokenSource();
        var queued = t.RecognizeAsync(img, OcrLayout.Block, "eng", cts.Token);
        Assert.False(queued.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        hold.Dispose();
        var words = await t.RecognizeAsync(img, OcrLayout.Block, "eng", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(words, w => w.Text.Contains("Alpha"));
    }
}
