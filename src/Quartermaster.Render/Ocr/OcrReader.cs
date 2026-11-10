using SkiaSharp;

namespace Quartermaster.Render.Ocr;

/// <summary>
/// Engine-neutral OCR front end on SkiaSharp: crops, grayscales and rescales a region for an <see cref="IOcrRecognizer"/>,
/// then maps its word boxes back to the original image's pixels.
/// </summary>
public sealed class OcrReader(IOcrRecognizer engine)
{
    public const string TooLargeMessage = "This image is too large to read.";

    public static string LanguageNotInstalledMessage(string tag) => $"The OCR language {tag} isn't installed.";

    /// <summary>When the native reading's median word height is below this (image px), a ~2x upscaled re-read is tried.</summary>
    internal const double UpscaleBelowWordHeight = 20;

    /// <summary>Upscale factor of the per-cell reads.</summary>
    internal const double CellScale = 3.0;

    private void CheckLanguage(string language)
    {
        if (engine.Languages.Count == 0) throw new OcrUnavailableException(engine.UnavailableMessage);
        if (!engine.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            throw new OcrUnavailableException(LanguageNotInstalledMessage(language));
    }

    /// <summary>
    /// Reads the words in <paramref name="region"/> (image pixels; null = whole image). Boxes are in the original image's
    /// pixels. Reads at native scale (grayscale; scaled down to the engine maximum if needed); when no words were found or
    /// their median height is under 20 px, re-reads upscaled ~2x (capped) and keeps the reading with more characters
    /// (ties keep the native one). The caller's bitmap is only touched before the first await.
    /// </summary>
    public async Task<IReadOnlyList<OcrWord>> ReadAsync(SKBitmap image, SKRectI? region, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        CheckLanguage(language);

        var bounds = new SKRectI(0, 0, image.Width, image.Height);
        var r = region is SKRectI given ? Intersect(given, bounds) : bounds;
        if (r.Width <= 0 || r.Height <= 0) return [];

        int max = engine.MaxImageDimension;
        double native = NativeScale(r.Width, r.Height, max);
        double up = UpscaleFactor(r.Width, r.Height, max);
        using var gray = Guard(() => Draw(image, r, native, grayscale: true));

        var first = await RecognizeAsync(gray, OcrLayout.Block, r, language, ct).ConfigureAwait(false);
        // up > native only when native == 1 (the region already fits), so the upscale starts from a 1:1 image.
        if (up <= native + 1e-6 || (first.Count > 0 && MedianHeight(first) >= UpscaleBelowWordHeight))
            return first;

        using var big = Guard(() => Draw(gray, new SKRectI(0, 0, gray.Width, gray.Height), up, grayscale: false));
        var second = await RecognizeAsync(big, OcrLayout.Block, r, language, ct).ConfigureAwait(false);
        return CharCount(second) > CharCount(first) ? second : first;
    }

    /// <summary>
    /// Reads each box (image px) on its own: cropped, grayscaled, ink-trimmed, upscaled x3 and padded with white, read as a
    /// single line. Blank cells are skipped (null). The cell's words are joined by a space; the box is their union in image px.
    /// The caller's bitmap is only touched before the first await.
    /// </summary>
    public async Task<IReadOnlyList<OcrWord?>> ReadCellsAsync(SKBitmap image, IReadOnlyList<SKRect> boxes, double padding, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        var result = new OcrWord?[boxes.Count];
        if (boxes.Count == 0) return result;
        CheckLanguage(language);
        int max = engine.MaxImageDimension;
        if (max <= 0) max = 10000;

        var bounds = new SKRectI(0, 0, image.Width, image.Height);
        int pad = Math.Max(4, (int)Math.Round(Math.Max(0, padding) * CellScale));
        var cells = new List<(int Index, SKRectI Src, double Scale, SKRectI Ink, SKBitmap Padded)>();
        try
        {
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                var src = Intersect(new SKRectI((int)Math.Floor(b.Left), (int)Math.Floor(b.Top), (int)Math.Ceiling(b.Right), (int)Math.Ceiling(b.Bottom)), bounds);
                if (src.Width <= 0 || src.Height <= 0) continue;
                double scale = Math.Min(CellScale, (double)(max - 2 * pad) / Math.Max(src.Width, src.Height));
                if (scale <= 0) continue;
                using var tile = Guard(() => Draw(image, src, scale, grayscale: true));
                var ink = InkBounds(tile);
                if (ink.IsEmpty) continue; // a blank cell: nothing to read
                var padded = Guard(() =>
                {
                    var bmp = NewBitmap(ink.Width + 2 * pad, ink.Height + 2 * pad);
                    try
                    {
                        using var canvas = new SKCanvas(bmp);
                        canvas.Clear(SKColors.White);
                        using var img = SKImage.FromBitmap(tile);
                        canvas.DrawImage(img, new SKRect(ink.Left, ink.Top, ink.Right, ink.Bottom),
                            new SKRect(pad, pad, pad + ink.Width, pad + ink.Height),
                            new SKSamplingOptions(SKFilterMode.Nearest), null);
                        return bmp;
                    }
                    catch { bmp.Dispose(); throw; }
                });
                cells.Add((i, src, scale, ink, padded));
            }

            foreach (var (index, src, scale, ink, padded) in cells)
            {
                var words = await engine.RecognizeAsync(padded, OcrLayout.SingleLine, language, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var ws = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
                if (ws.Count == 0) continue;
                double l = ws.Min(w => w.X), t = ws.Min(w => w.Y), rr = ws.Max(w => w.X + w.W), bb = ws.Max(w => w.Y + w.H);
                result[index] = new OcrWord(string.Join(" ", ws.OrderBy(w => w.X).Select(w => w.Text.Trim())),
                    src.Left + (l - pad + ink.Left) / scale, src.Top + (t - pad + ink.Top) / scale, (rr - l) / scale, (bb - t) / scale);
            }
        }
        finally
        {
            foreach (var c in cells) c.Padded.Dispose();
        }
        return result;
    }

    /// <summary>OCR on <paramref name="prepared"/>, an image of region <paramref name="r"/> at some scale; boxes mapped to image px.</summary>
    private async Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap prepared, OcrLayout layout, SKRectI r, string language, CancellationToken ct)
    {
        var raw = await engine.RecognizeAsync(prepared, layout, language, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        double sx = (double)prepared.Width / r.Width, sy = (double)prepared.Height / r.Height;
        var words = new List<OcrWord>(raw.Count);
        foreach (var w in raw)
            words.Add(new OcrWord(w.Text, r.Left + w.X / sx, r.Top + w.Y / sy, w.W / sx, w.H / sy));
        return words;
    }

    private static SKRectI Intersect(SKRectI a, SKRectI b)
    {
        int l = Math.Max(a.Left, b.Left), t = Math.Max(a.Top, b.Top), r = Math.Min(a.Right, b.Right), bt = Math.Min(a.Bottom, b.Bottom);
        return r <= l || bt <= t ? new SKRectI(0, 0, 0, 0) : new SKRectI(l, t, r, bt);
    }

    internal static T Guard<T>(Func<T> draw)
    {
        try { return draw(); }
        catch (Exception e) when (e is OutOfMemoryException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException(TooLargeMessage, e);
        }
    }

    private static SKBitmap NewBitmap(int w, int h)
    {
        var bmp = new SKBitmap();
        if (!bmp.TryAllocPixels(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            bmp.Dispose();
            throw new OutOfMemoryException();
        }
        return bmp;
    }

    /// <summary>Bounding box of the dark pixels (luminance under 128) of a grayscale tile; empty when the tile is blank.</summary>
    internal static SKRectI InkBounds(SKBitmap tile)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < tile.Height; y++)
            for (int x = 0; x < tile.Width; x++)
            {
                var c = tile.GetPixel(x, y);
                if ((c.Blue * 114 + c.Green * 587 + c.Red * 299) / 1000 >= 128) continue;
                if (x < l) l = x;
                if (x > r) r = x;
                if (y < t) t = y;
                b = y;
            }
        return r < 0 ? SKRectI.Empty : new SKRectI(l, t, r + 1, b + 1);
    }

    internal static int CharCount(IReadOnlyList<OcrWord> words) => words.Sum(w => w.Text.Trim().Length);

    internal static double MedianHeight(IReadOnlyList<OcrWord> words)
    {
        var hs = words.Select(w => w.H).OrderBy(h => h).ToList();
        if (hs.Count == 0) return 0;
        return hs.Count % 2 == 1 ? hs[hs.Count / 2] : (hs[hs.Count / 2 - 1] + hs[hs.Count / 2]) / 2;
    }

    /// <summary>1, or smaller so the longest side fits the engine maximum.</summary>
    internal static double NativeScale(int width, int height, int maxDimension)
    {
        int longest = Math.Max(width, height);
        return maxDimension > 0 && longest > maxDimension ? (double)maxDimension / longest : 1.0;
    }

    /// <summary>~2x, capped so the longest side does not exceed the engine maximum.</summary>
    internal static double UpscaleFactor(int width, int height, int maxDimension)
    {
        int longest = Math.Max(width, height);
        return maxDimension > 0 ? Math.Min(2.0, (double)maxDimension / longest) : 2.0;
    }

    private static readonly float[] GrayMatrix =
    [
        0.299f, 0.587f, 0.114f, 0, 0,
        0.299f, 0.587f, 0.114f, 0, 0,
        0.299f, 0.587f, 0.114f, 0, 0,
        0, 0, 0, 1, 0,
    ];

    /// <summary>Draws <paramref name="r"/> of <paramref name="src"/> at <paramref name="scale"/> onto white.</summary>
    private static SKBitmap Draw(SKBitmap src, SKRectI r, double scale, bool grayscale)
    {
        int w = Math.Max(1, (int)Math.Floor(r.Width * scale));
        int h = Math.Max(1, (int)Math.Floor(r.Height * scale));
        var dst = NewBitmap(w, h);
        try
        {
            using var canvas = new SKCanvas(dst);
            canvas.Clear(SKColors.White);
            bool oneToOne = w == r.Width && h == r.Height;
            var sampling = oneToOne ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKCubicResampler.Mitchell);
            using var paint = new SKPaint();
            using var filter = grayscale ? SKColorFilter.CreateColorMatrix(GrayMatrix) : null;
            paint.ColorFilter = filter;
            using var img = SKImage.FromBitmap(src);
            canvas.DrawImage(img, new SKRect(r.Left, r.Top, r.Right, r.Bottom), new SKRect(0, 0, w, h), sampling, paint);
            return dst;
        }
        catch
        {
            dst.Dispose();
            throw;
        }
    }
}
