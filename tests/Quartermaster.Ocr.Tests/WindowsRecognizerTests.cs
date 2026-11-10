using Quartermaster.Documents.Tests;
using Quartermaster.OcrWindows;
using Quartermaster.Render.Ocr;
using SkiaSharp;
using Xunit.Abstractions;

namespace Quartermaster.Ocr.Tests;

/// <summary>The Windows.Media.Ocr plugin recognizer on Skia-drawn invented fixtures (needs the en-US OCR language).</summary>
public class WindowsRecognizerTests(ITestOutputHelper output)
{
    private const string Lang = "en-US";

    [OcrFact]
    public async Task Reads_skia_invented_table_digits()
    {
        using var w = new WindowsRecognizer();
        var reader = new OcrReader(w);
        foreach (var bordered in new[] { true, false })
        {
            var (img, expected) = InventedTables.Table(bordered);
            using (img)
            {
                var cells = await reader.ReadCellsAsync(img, InventedTables.Cells(), 4, Lang, CancellationToken.None);
                int exact = 0;
                for (int r = 0; r < InventedTables.Rows; r++)
                    for (int c = 0; c < InventedTables.Cols; c++)
                    {
                        var got = cells[r * InventedTables.Cols + c]?.Text.Trim();
                        if (got == expected[r][c]) exact++;
                        else output.WriteLine($"{(bordered ? "bordered" : "unbordered")} r{r}c{c}: '{got}' for '{expected[r][c]}'");
                    }
                Assert.True(exact >= 23, $"{(bordered ? "bordered" : "unbordered")}: {exact}/24 exact");
            }
        }
    }

    [OcrFact]
    public async Task Single_lone_digit_cell_is_read()
    {
        using var w = new WindowsRecognizer();
        var reader = new OcrReader(w);
        foreach (var digit in new[] { "3", "7", "8" })
        {
            using var img = InventedTables.Line(digit, 30f);
            var cells = await reader.ReadCellsAsync(img, [new SKRect(0, 0, img.Width, img.Height)], 4, Lang, CancellationToken.None);
            Assert.Equal(digit, cells[0]?.Text.Trim());
            // The box is the digit's, in the image's pixels (the anchor word is not part of it).
            var box = cells[0]!.Value;
            Assert.InRange(box.X, 30, 60);
            Assert.InRange(box.W, 5, 40);
        }
    }

    [OcrFact]
    public async Task Block_reads_a_line_of_words()
    {
        using var w = new WindowsRecognizer();
        using var img = InventedTables.Line("Alpha Bravo Charlie");
        var words = await w.RecognizeAsync(img, OcrLayout.Block, Lang, CancellationToken.None);
        Assert.Equal("Alpha Bravo Charlie", string.Join(" ", words.OrderBy(x => x.X).Select(x => x.Text)));
        Assert.All(words, x => Assert.InRange(x.Y, 0, img.Height));
    }

    [OcrFact]
    public void Language_tags_and_default()
    {
        using var w = new WindowsRecognizer();
        Assert.Equal("Windows", w.Name);
        Assert.Equal(OcrService.Languages(), w.Languages);
        Assert.Contains(Lang, w.Languages, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(w.DefaultLanguage);
        Assert.Contains(w.DefaultLanguage!, w.Languages);
        Assert.Equal((int)global::Windows.Media.Ocr.OcrEngine.MaxImageDimension, w.MaxImageDimension);
        Assert.Equal(OcrService.NoLanguageMessage, w.UnavailableMessage);
    }

    [OcrFact]
    public async Task Unknown_language_is_unavailable()
    {
        using var w = new WindowsRecognizer();
        using var img = InventedTables.Line("Hello");
        var ex = await Assert.ThrowsAsync<Quartermaster.Render.Ocr.OcrUnavailableException>(
            () => w.RecognizeAsync(img, OcrLayout.Block, "xx-XX", CancellationToken.None));
        Assert.Equal(OcrReader.LanguageNotInstalledMessage("xx-XX"), ex.Message);
    }

    [OcrFact]
    public async Task Read_after_dispose_throws_object_disposed()
    {
        var w = new WindowsRecognizer();
        using var img = InventedTables.Line("Hello");
        await w.RecognizeAsync(img, OcrLayout.Block, Lang, CancellationToken.None);
        w.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => w.RecognizeAsync(img, OcrLayout.Block, Lang, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => w.RecognizeAsync(img, OcrLayout.SingleLine, Lang, CancellationToken.None));
        w.Dispose(); // twice is fine
    }

    [OcrFact]
    public async Task Queued_reads_are_serialized()
    {
        using var w = new WindowsRecognizer();
        int inside = 0, most = 0;
        w.InsideGate = async () =>
        {
            int now = Interlocked.Increment(ref inside);
            InterlockedMax(ref most, now);
            await Task.Delay(40);
            Interlocked.Decrement(ref inside);
        };
        var texts = new[] { "Alpha", "Bravo", "Charlie", "Delta" };
        var images = texts.Select(t => InventedTables.Line(t)).ToArray();
        try
        {
            var reads = images.Select(i => Task.Run(() => w.RecognizeAsync(i, OcrLayout.Block, Lang, CancellationToken.None))).ToArray();
            var results = await Task.WhenAll(reads);
            Assert.Equal(1, most);
            for (int i = 0; i < texts.Length; i++)
                Assert.Equal(texts[i], string.Join(" ", results[i].Select(x => x.Text)));
        }
        finally
        {
            foreach (var i in images) i.Dispose();
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
