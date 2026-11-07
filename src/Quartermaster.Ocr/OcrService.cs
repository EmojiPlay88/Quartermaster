using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using OcrWord = Quartermaster.Render.Ocr.OcrWord;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Quartermaster.Ocr;

/// <summary>Thrown when Windows has no OCR engine for the requested language (no OCR language installed).</summary>
public sealed class OcrUnavailableException(string message) : Exception(message);

/// <summary>Windows.Media.Ocr wrapper: crops, grayscales and rescales a region, then maps word boxes back to image pixels.</summary>
public sealed class OcrService
{
    public const string NoLanguageMessage =
        "Windows has no OCR language installed. Add one in Settings → Time & language → Language (Optical character recognition).";

    /// <summary>When the native reading's median word height is below this (image px), a ~2× upscaled re-read is tried.</summary>
    internal const double UpscaleBelowWordHeight = 20;

    private readonly Func<string, OcrEngine?> _engineFactory;

    public OcrService() : this(CreateEngine) { }

    /// <summary>For tests: inject an engine factory (returning null simulates "no language installed").</summary>
    internal OcrService(Func<string, OcrEngine?> engineFactory) => _engineFactory = engineFactory;

    /// <summary>BCP-47 tags of the installed OCR languages (empty when none, or when the API is unavailable).</summary>
    public static IReadOnlyList<string> Languages()
    {
        try { return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList(); }
        catch (Exception) { return []; }
    }

    /// <summary>Profile language if installed, else en-US if installed, else the first installed; null when none.</summary>
    public static string? DefaultLanguage()
    {
        var langs = Languages();
        if (langs.Count == 0) return null;
        try
        {
            var profile = OcrEngine.TryCreateFromUserProfileLanguages()?.RecognizerLanguage.LanguageTag;
            if (profile != null && langs.Contains(profile, StringComparer.OrdinalIgnoreCase)) return profile;
        }
        catch (Exception) { /* fall through */ }
        return langs.FirstOrDefault(l => l.Equals("en-US", StringComparison.OrdinalIgnoreCase)) ?? langs[0];
    }

    /// <summary>True when an engine can be created for the default language.</summary>
    public bool Available => DefaultLanguage() is string lang && _engineFactory(lang) != null;

    private static OcrEngine? CreateEngine(string language)
    {
        try { return OcrEngine.TryCreateFromLanguage(new Language(language)); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Reads the words in <paramref name="region"/> (image pixels; null = whole image). Word boxes are returned in
    /// the original image's pixel coordinates. Reads at native scale (grayscale; scaled down to the engine maximum
    /// if needed); when no words were found or their median height is under 20 px, re-reads upscaled ~2× (capped)
    /// and keeps the reading with more recognised characters (ties keep the native one).
    /// The bitmap is only touched before the first await.
    /// </summary>
    public async Task<IReadOnlyList<OcrWord>> ReadAsync(Bitmap image, Rectangle? region, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        var engine = _engineFactory(language) ?? throw new OcrUnavailableException(NoLanguageMessage);

        var bounds = new Rectangle(0, 0, image.Width, image.Height);
        var r = region is Rectangle given ? Rectangle.Intersect(given, bounds) : bounds;
        if (r.Width <= 0 || r.Height <= 0) return [];

        int max = (int)OcrEngine.MaxImageDimension;
        double native = NativeScale(r.Width, r.Height, max);
        double up = UpscaleFactor(r.Width, r.Height, max);
        // Private 1:1 grayscale copy so the caller's bitmap is not used after the first await.
        using var gray = GrayCrop(image, r);

        var first = await RecognizeAsync(engine, gray, native, r, ct).ConfigureAwait(false);
        if (up <= native + 1e-6 || (first.Count > 0 && MedianHeight(first) >= UpscaleBelowWordHeight))
            return first;

        var second = await RecognizeAsync(engine, gray, up, r, ct).ConfigureAwait(false);
        return CharCount(second) > CharCount(first) ? second : first;
    }

    private static int CharCount(IReadOnlyList<OcrWord> words) => words.Sum(w => w.Text.Trim().Length);

    internal static double MedianHeight(IReadOnlyList<OcrWord> words)
    {
        var hs = words.Select(w => w.H).OrderBy(h => h).ToList();
        if (hs.Count == 0) return 0;
        return hs.Count % 2 == 1 ? hs[hs.Count / 2] : (hs[hs.Count / 2 - 1] + hs[hs.Count / 2]) / 2;
    }

    private static async Task<IReadOnlyList<OcrWord>> RecognizeAsync(
        OcrEngine engine, Bitmap gray, double scale, Rectangle r, CancellationToken ct)
    {
        byte[] png = ScaledPng(gray, scale);
        using var ms = new MemoryStream(png);
        using var ras = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct).ConfigureAwait(false);
        using var soft = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(ct).ConfigureAwait(false);
        var result = await engine.RecognizeAsync(soft).AsTask(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        double sx = (double)soft.PixelWidth / r.Width, sy = (double)soft.PixelHeight / r.Height;
        var words = new List<OcrWord>();
        foreach (var line in result.Lines)
            foreach (var w in line.Words)
            {
                var b = w.BoundingRect;
                words.Add(new OcrWord(w.Text, r.X + b.X / sx, r.Y + b.Y / sy, b.Width / sx, b.Height / sy));
            }
        return words;
    }

    /// <summary>1, or smaller so the longest side fits the engine maximum.</summary>
    internal static double NativeScale(int width, int height, int maxDimension)
    {
        int longest = Math.Max(width, height);
        return maxDimension > 0 && longest > maxDimension ? (double)maxDimension / longest : 1.0;
    }

    /// <summary>~2×, capped so the longest side does not exceed the engine maximum.</summary>
    internal static double UpscaleFactor(int width, int height, int maxDimension)
    {
        int longest = Math.Max(width, height);
        return maxDimension > 0 ? Math.Min(2.0, (double)maxDimension / longest) : 2.0;
    }

    private static readonly ColorMatrix Gray = new(
    [
        [0.299f, 0.299f, 0.299f, 0, 0],
        [0.587f, 0.587f, 0.587f, 0, 0],
        [0.114f, 0.114f, 0.114f, 0, 0],
        [0, 0, 0, 1, 0],
        [0, 0, 0, 0, 1],
    ]);

    private static Bitmap GrayCrop(Bitmap image, Rectangle r)
    {
        var dst = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        using var attrs = new ImageAttributes();
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        attrs.SetColorMatrix(Gray);
        g.DrawImage(image, new Rectangle(0, 0, r.Width, r.Height), r.X, r.Y, r.Width, r.Height, GraphicsUnit.Pixel, attrs);
        return dst;
    }

    private static byte[] ScaledPng(Bitmap gray, double scale)
    {
        using var ms = new MemoryStream();
        if (Math.Abs(scale - 1.0) < 1e-9)
        {
            gray.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        int w = Math.Max(1, (int)Math.Floor(gray.Width * scale));
        int h = Math.Max(1, (int)Math.Floor(gray.Height * scale));
        using var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        using (var attrs = new ImageAttributes())
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges
            g.DrawImage(gray, new Rectangle(0, 0, w, h), 0, 0, gray.Width, gray.Height, GraphicsUnit.Pixel, attrs);
        }
        dst.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
