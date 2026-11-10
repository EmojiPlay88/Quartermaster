using System.Runtime.InteropServices.WindowsRuntime;
using Quartermaster.Render.Ocr;
using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using OcrResult = Windows.Media.Ocr.OcrResult;
using OcrWord = Quartermaster.Render.Ocr.OcrWord;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;

namespace Quartermaster.OcrWindows;

/// <summary>
/// Windows.Media.Ocr behind <see cref="IOcrRecognizer"/>. Loaded at run time from the ocr-windows/ plugin folder
/// (Quartermaster.Documents.OcrEngines); the cross-platform app never references it. One engine per language, calls serialized.
/// <see cref="OcrLayout.SingleLine"/> (a table cell) uses the WinForms "Cell" anchor-word trick (OcrService.ReadCellsAsync):
/// Windows OCR drops glyphs that stand alone, so the cell's ink is read after a printed anchor word and the anchor is dropped.
/// </summary>
public sealed class WindowsRecognizer : IOcrRecognizer, IDisposable
{
    /// <summary>The WinForms OcrService.NoLanguageMessage, verbatim.</summary>
    public const string NoLanguageMessage =
        "Windows has no OCR language installed. Add one in Settings → Time & language → Language (Optical character recognition).";

    private const string Anchor = "Cell";
    private const string AnchorTypeface = "Segoe UI";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, WinOcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    /// <summary>Test seam: runs inside the serialized section, before the engine is called.</summary>
    internal Func<Task>? InsideGate { get; set; }

    public WindowsRecognizer()
    {
        Languages = InstalledLanguages();
        DefaultLanguage = ProfileLanguage(Languages) ?? Languages.FirstOrDefault();
        MaxImageDimension = (int)WinOcrEngine.MaxImageDimension;
    }

    public string Name => "Windows";
    public int MaxImageDimension { get; }
    public IReadOnlyList<string> Languages { get; }
    public string? DefaultLanguage { get; }
    public string UnavailableMessage => NoLanguageMessage;

    private static IReadOnlyList<string> InstalledLanguages()
    {
        try { return WinOcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray(); }
        catch (Exception) { return []; }
    }

    private static string? ProfileLanguage(IReadOnlyList<string> languages)
    {
        if (languages.Count == 0) return null;
        try
        {
            var tag = WinOcrEngine.TryCreateFromUserProfileLanguages()?.RecognizerLanguage.LanguageTag;
            return tag != null && languages.Contains(tag, StringComparer.OrdinalIgnoreCase) ? tag : null;
        }
        catch (Exception) { return null; }
    }

    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gray);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (Languages.Count == 0) throw new OcrUnavailableException(UnavailableMessage);
        var canonical = Languages.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase))
            ?? throw new OcrUnavailableException(OcrReader.LanguageNotInstalledMessage(language));

        // Everything taken from the caller's bitmap is copied before the first await; the bitmap is not kept.
        var job = layout == OcrLayout.SingleLine ? AnchoredStrip(gray) : Plain(gray);
        if (job is null) return [];

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (InsideGate is { } hook) await hook().ConfigureAwait(false);
            var engine = Engine(canonical);
            OcrResult result;
            using (var soft = SoftwareBitmap.CreateCopyFromBuffer(job.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, job.Width, job.Height, BitmapAlphaMode.Premultiplied))
                result = await engine.RecognizeAsync(soft).AsTask(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var words = new List<OcrWord>();
            foreach (var line in result.Lines)
                foreach (var w in line.Words)
                {
                    var b = w.BoundingRect;
                    words.Add(new OcrWord(w.Text, b.X, b.Y, b.Width, b.Height));
                }
            return job.MapBack(words);
        }
        finally { _gate.Release(); }
    }

    private WinOcrEngine Engine(string language)
    {
        if (_engines.TryGetValue(language, out var e)) return e;
        WinOcrEngine? created;
        try { created = WinOcrEngine.TryCreateFromLanguage(new Language(language)); }
        catch (Exception) { created = null; } // malformed tag: treated as not installed
        return _engines[language] = created ?? throw new OcrUnavailableException(OcrReader.LanguageNotInstalledMessage(language));
    }

    /// <summary>Bgra8 premultiplied pixels for the engine and the mapping of its word boxes back to the caller's pixels.</summary>
    private sealed record Job(byte[] Pixels, int Width, int Height, Func<List<OcrWord>, IReadOnlyList<OcrWord>> MapBack);

    private static Job Plain(SKBitmap src)
    {
        using var bmp = OnWhite(src);
        return new Job(Bytes(bmp), bmp.Width, bmp.Height, words => words);
    }

    /// <summary>
    /// The single-line read of the WinForms OcrService.ReadCellsAsync, for one cell: the cell's ink follows a printed anchor word
    /// ("Cell", Segoe UI at the ink height / 0.72, baseline level with the bottom of the ink) on one white line, then a white gap of
    /// 1.6 x the ink height; the anchor word is dropped from the reading and the cell's words are mapped back to <paramref name="cell"/>'s pixels. Null when the cell is blank.
    /// </summary>
    private Job? AnchoredStrip(SKBitmap cell)
    {
        using var copy = OnWhite(cell);
        var ink = InkBounds(copy);
        if (ink.IsEmpty) return null;
        int pad = Math.Max(4, Math.Min(Math.Min(ink.Left, ink.Top), Math.Min(copy.Width - ink.Right, copy.Height - ink.Bottom)));
        double inkH = ink.Height;

        using var face = SKTypeface.FromFamilyName(AnchorTypeface) ?? SKTypeface.Default;
        using var font = new SKFont(face, (float)Math.Max(8, inkH / 0.72)) { Edging = SKFontEdging.Antialias };
        int anchorW = (int)Math.Ceiling(font.MeasureText(Anchor));
        int space = Math.Max(4, (int)Math.Round(0.6 * inkH));
        int anchorX = pad, tileX = pad + anchorW + space;
        // WinForms strips followed every cell but the last with this gap (and the next anchor); read alone, a cell gets the gap
        // back as white, or the engine invents strokes at the edge ("71" for a lone 7) - see docs/port/stage3b-ocr-benchmark.md.
        int gap = Math.Max(8, (int)Math.Round(1.6 * inkH));
        int sw = tileX + ink.Width + gap + pad;
        int sh = Math.Max((int)Math.Ceiling(font.Spacing) + 2 * pad, ink.Height + 2 * pad);
        int max = MaxImageDimension > 0 ? MaxImageDimension : 10000;
        if (sw > max || sh > max) return Plain(copy); // no room for the anchor: read the cell as it is

        int top = (sh - ink.Height) / 2; // centred on one line
        using var strip = NewBitmap(sw, sh);
        using (var canvas = new SKCanvas(strip))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        using (var img = SKImage.FromBitmap(copy))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawText(Anchor, anchorX, top + ink.Height, SKTextAlign.Left, font, paint); // baseline at the bottom of the ink
            canvas.DrawImage(img, new SKRect(ink.Left, ink.Top, ink.Right, ink.Bottom),
                new SKRect(tileX, top, tileX + ink.Width, top + ink.Height), new SKSamplingOptions(SKFilterMode.Nearest), null);
        }
        return new Job(Bytes(strip), sw, sh, words =>
        {
            var kept = new List<OcrWord>();
            foreach (var w in words)
            {
                if (string.IsNullOrWhiteSpace(w.Text)) continue;
                double cx = w.X + w.W / 2;
                if (cx < tileX) continue; // the anchor word (however it was read)
                kept.Add(new OcrWord(w.Text, w.X - tileX + ink.Left, w.Y - top + ink.Top, w.W, w.H));
            }
            return kept;
        });
    }

    /// <summary>Bounding box of the dark pixels (luminance under 128); empty when blank. Same rule as OcrReader.InkBounds.</summary>
    private static SKRectI InkBounds(SKBitmap bmp)
    {
        var span = bmp.GetPixelSpan();
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < bmp.Height; y++)
        {
            int o = y * bmp.RowBytes;
            for (int x = 0; x < bmp.Width; x++, o += 4)
            {
                if ((span[o] * 114 + span[o + 1] * 587 + span[o + 2] * 299) / 1000 >= 128) continue;
                if (x < l) l = x;
                if (x > r) r = x;
                if (y < t) t = y;
                b = y;
            }
        }
        return r < 0 ? SKRectI.Empty : new SKRectI(l, t, r + 1, b + 1);
    }

    /// <summary>A Bgra8 premultiplied copy of <paramref name="src"/> composited on white.</summary>
    private static SKBitmap OnWhite(SKBitmap src)
    {
        var bmp = NewBitmap(Math.Max(1, src.Width), Math.Max(1, src.Height));
        try
        {
            using var canvas = new SKCanvas(bmp);
            using var img = SKImage.FromBitmap(src);
            canvas.Clear(SKColors.White);
            canvas.DrawImage(img, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
            return bmp;
        }
        catch { bmp.Dispose(); throw; }
    }

    private static SKBitmap NewBitmap(int w, int h)
    {
        var bmp = new SKBitmap();
        if (!bmp.TryAllocPixels(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            bmp.Dispose();
            throw new InvalidDataException(OcrReader.TooLargeMessage);
        }
        return bmp;
    }

    /// <summary>The bitmap's Bgra8 premultiplied rows, tightly packed.</summary>
    private static byte[] Bytes(SKBitmap bmp)
    {
        int rowLen = bmp.Width * 4;
        var res = new byte[rowLen * bmp.Height];
        var span = bmp.GetPixelSpan();
        for (int y = 0; y < bmp.Height; y++)
            span.Slice(y * bmp.RowBytes, rowLen).CopyTo(res.AsSpan(y * rowLen, rowLen));
        return res;
    }

    /// <summary>Marks the recognizer disposed first; later calls throw <see cref="ObjectDisposedException"/>. A read in progress finishes.</summary>
    public void Dispose()
    {
        _disposed = true;
        if (_gate.Wait(0))
        {
            try { _engines.Clear(); }
            finally { _gate.Release(); }
        }
    }
}
