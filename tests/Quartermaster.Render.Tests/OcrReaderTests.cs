using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Render.Tests;

public class OcrReaderTests
{
    private sealed class Fake(int max = 10000, string[]? langs = null) : IOcrRecognizer
    {
        public string Name => "Fake";
        public int MaxImageDimension => max;
        public IReadOnlyList<string> Languages { get; } = langs ?? ["eng"];
        public string? DefaultLanguage => Languages.FirstOrDefault();
        public string UnavailableMessage => "No OCR data.";
        public List<(int W, int H, OcrLayout Layout)> Calls { get; } = [];
        public Func<int, SKBitmap, OcrLayout, IReadOnlyList<OcrWord>> Script { get; set; } = (_, _, _) => [];
        public int Delay { get; set; }

        public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct)
        {
            int n;
            lock (Calls) { Calls.Add((gray.Width, gray.Height, layout)); n = Calls.Count; }
            if (Delay > 0) await Task.Delay(Delay, ct);
            return Script(n, gray, layout);
        }
    }

    private static SKBitmap White(int w, int h)
    {
        var bmp = new SKBitmap(w, h);
        bmp.Erase(SKColors.White);
        return bmp;
    }

    [Fact]
    public void Scale_NativeFitsEngineMaximum_UpscaleIsCapped()
    {
        Assert.Equal(1.0, OcrReader.NativeScale(800, 600, 10000));
        Assert.Equal(0.5, OcrReader.NativeScale(4000, 9000, 4500), 6);
        Assert.Equal(2.0, OcrReader.UpscaleFactor(300, 100, 10000));
        Assert.Equal(1.5, OcrReader.UpscaleFactor(3000, 500, 4500), 6);
    }

    [Fact]
    public void MedianHeight_OfWords()
    {
        Assert.Equal(0, OcrReader.MedianHeight([]));
        Assert.Equal(15, OcrReader.MedianHeight([new("a", 0, 0, 5, 10), new("b", 0, 0, 5, 20)]));
        Assert.Equal(12, OcrReader.MedianHeight([new("a", 0, 0, 5, 30), new("b", 0, 0, 5, 12), new("c", 0, 0, 5, 8)]));
    }

    [Fact]
    public async Task Read_PreCancelledToken_Throws_OperationCanceled()
    {
        using var img = White(200, 100);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new OcrReader(new Fake()).ReadAsync(img, null, "eng", cts.Token));
    }

    [Fact]
    public async Task No_languages_throws_unavailable_message()
    {
        using var img = White(200, 100);
        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(
            () => new OcrReader(new Fake(langs: [])).ReadAsync(img, null, "eng", CancellationToken.None));
        Assert.Equal("No OCR data.", ex.Message);
    }

    [Theory]
    [InlineData("xx-ZZ")]
    [InlineData("not a tag!")]
    public async Task Missing_language_message_text(string tag)
    {
        using var img = White(200, 100);
        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(
            () => new OcrReader(new Fake()).ReadAsync(img, null, tag, CancellationToken.None));
        Assert.Equal($"The OCR language {tag} isn't installed.", ex.Message);
    }

    [Fact]
    public void Guard_AllocationFailures_BecomeTooLarge()
    {
        var oom = Assert.Throws<InvalidDataException>(() => OcrReader.Guard<int>(() => throw new OutOfMemoryException()));
        Assert.Equal("This image is too large to read.", oom.Message);
        var arg = Assert.Throws<InvalidDataException>(() => OcrReader.Guard<int>(() => throw new ArgumentException("x")));
        Assert.Equal("This image is too large to read.", arg.Message);
    }

    [Fact]
    public async Task Read_RegionOutsideImage_ReturnsEmpty()
    {
        using var img = White(200, 100);
        var fake = new Fake();
        var words = await new OcrReader(fake).ReadAsync(img, new SKRectI(500, 500, 510, 510), "eng", CancellationToken.None);
        Assert.Empty(words);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Read_maps_boxes_back_to_image_pixels_for_region_and_downscale()
    {
        using var img = White(400, 200);
        var fake = new Fake { Script = (_, _, _) => [new("x", 10, 20, 30, 25)] };
        var words = await new OcrReader(fake).ReadAsync(img, new SKRectI(100, 50, 300, 150), "eng", CancellationToken.None);
        var w = Assert.Single(words);
        Assert.Equal((110, 70, 30, 25), (w.X, w.Y, w.W, w.H));

        var small = new Fake(max: 100) { Script = (_, _, _) => [new("x", 10, 10, 20, 25)] };
        words = await new OcrReader(small).ReadAsync(img, new SKRectI(100, 50, 300, 150), "eng", CancellationToken.None);
        w = Assert.Single(words);
        Assert.Single(small.Calls); // downscaled: no upscale pass
        Assert.Equal((100, 50), (small.Calls[0].W, small.Calls[0].H));
        Assert.Equal((120, 70, 40, 50), (w.X, w.Y, w.W, w.H));
    }

    [Fact]
    public async Task Read_rereads_upscaled_when_words_small_and_keeps_more_characters()
    {
        using var img = White(300, 100);
        var fake = new Fake
        {
            Script = (n, _, _) => n == 1 ? [new("ab", 10, 10, 20, 10)] : [new("abcd", 20, 20, 40, 20)],
        };
        var words = await new OcrReader(fake).ReadAsync(img, null, "eng", CancellationToken.None);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal((600, 200), (fake.Calls[1].W, fake.Calls[1].H));
        var w = Assert.Single(words);
        Assert.Equal("abcd", w.Text);
        Assert.Equal((10, 10, 20, 10), (w.X, w.Y, w.W, w.H));
    }

    [Fact]
    public async Task Read_tie_keeps_native()
    {
        using var img = White(300, 100);
        var fake = new Fake { Script = (n, _, _) => [new(n == 1 ? "ab" : "cd", 10, 10, 20, 10)] };
        var words = await new OcrReader(fake).ReadAsync(img, null, "eng", CancellationToken.None);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal("ab", Assert.Single(words).Text);
    }

    [Fact]
    public async Task Read_copies_bitmap_before_await()
    {
        var img = White(300, 100);
        var fake = new Fake { Delay = 50, Script = (n, _, _) => [new("ab", 10, 10, 20, 10)] };
        var task = new OcrReader(fake).ReadAsync(img, null, "eng", CancellationToken.None);
        img.Dispose();
        var words = await task;
        Assert.Equal("ab", Assert.Single(words).Text);
    }

    private static SKBitmap WithInk()
    {
        var bmp = White(100, 100);
        using var c = new SKCanvas(bmp);
        using var p = new SKPaint { Color = SKColors.Black };
        c.DrawRect(60, 10, 10, 10, p);
        return bmp;
    }

    [Fact]
    public async Task Cells_blank_cell_is_null_and_not_sent()
    {
        using var img = WithInk();
        var fake = new Fake { Script = (_, _, _) => [new("5", 6, 6, 10, 30)] };
        var cells = await new OcrReader(fake).ReadCellsAsync(img, [new SKRect(0, 0, 20, 20), new SKRect(50, 0, 90, 40)], 2, "eng", CancellationToken.None);
        Assert.Null(cells[0]);
        Assert.NotNull(cells[1]);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task Cells_use_single_line_layout_and_union_box()
    {
        using var img = WithInk();
        // padding 2 -> white padding 6 px around the x3 ink (30x30) -> 42x42
        var fake = new Fake { Script = (_, _, _) => [new("1", 6, 6, 10, 30), new("2", 21, 6, 10, 30)] };
        var cells = await new OcrReader(fake).ReadCellsAsync(img, [new SKRect(50, 0, 90, 40)], 2, "eng", CancellationToken.None);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(OcrLayout.SingleLine, call.Layout);
        Assert.InRange(call.W, 40, 44);
        var w = Assert.NotNull(cells[0]);
        Assert.Equal("1 2", w.Text);
        Assert.Equal(60, w.X, 0.7);
        Assert.Equal(10, w.Y, 0.7);
        Assert.Equal(25.0 / 3, w.W, 0.7);
        Assert.Equal(10, w.H, 0.7);
    }
}
