using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using OcrWord = Quartermaster.Render.Ocr.OcrWord;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Quartermaster.Ocr;

/// <summary>Thrown when Windows has no OCR engine for the requested language (none installed, or not that one).</summary>
public sealed class OcrUnavailableException(string message) : Exception(message);

/// <summary>Windows.Media.Ocr wrapper: crops, grayscales and rescales a region, then maps word boxes back to image pixels.</summary>
public sealed class OcrService
{
    public const string NoLanguageMessage =
        "Windows has no OCR language installed. Add one in Settings → Time & language → Language (Optical character recognition).";

    public const string TooLargeMessage = "This image is too large to read.";

    public static string LanguageNotInstalledMessage(string tag) => $"The OCR language {tag} isn't installed.";

    /// <summary>When the native reading's median word height is below this (image px), a ~2× upscaled re-read is tried.</summary>
    internal const double UpscaleBelowWordHeight = 20;

    private readonly Func<string, OcrEngine?> _engineFactory;
    private readonly Func<IReadOnlyList<string>> _languages;

    public OcrService() : this(CreateEngine, Languages) { }

    /// <summary>For tests: inject the engine factory and the installed-language list.</summary>
    internal OcrService(Func<string, OcrEngine?> engineFactory, Func<IReadOnlyList<string>> languages)
    {
        _engineFactory = engineFactory;
        _languages = languages;
    }

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

    /// <summary>True when an OCR language is installed and an engine can be created for the default one.</summary>
    public bool Available => _languages().Count > 0 && DefaultLanguage() is string lang && _engineFactory(lang) != null;

    private static OcrEngine? CreateEngine(string language)
    {
        try { return OcrEngine.TryCreateFromLanguage(new Language(language)); }
        catch (Exception) { return null; } // malformed tag → treated as not installed
    }

    /// <summary>
    /// Reads the words in <paramref name="region"/> (image pixels; null = whole image). Word boxes are returned in
    /// the original image's pixel coordinates. Reads at native scale (grayscale; scaled down to the engine maximum
    /// if needed); when no words were found or their median height is under 20 px, re-reads upscaled ~2× (capped)
    /// and keeps the reading with more recognised characters (ties keep the native one).
    /// The bitmap is only touched before the first await.
    /// </summary>
    /// <exception cref="OcrUnavailableException">No OCR language installed, or not <paramref name="language"/>.</exception>
    /// <exception cref="InvalidDataException">GDI+ could not allocate/draw the region (<see cref="TooLargeMessage"/>).</exception>
    public async Task<IReadOnlyList<OcrWord>> ReadAsync(Bitmap image, Rectangle? region, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        if (_languages().Count == 0) throw new OcrUnavailableException(NoLanguageMessage);
        var engine = _engineFactory(language) ?? throw new OcrUnavailableException(LanguageNotInstalledMessage(language));

        var bounds = new Rectangle(0, 0, image.Width, image.Height);
        var r = region is Rectangle given ? Rectangle.Intersect(given, bounds) : bounds;
        if (r.Width <= 0 || r.Height <= 0) return [];

        int max = (int)OcrEngine.MaxImageDimension;
        double native = NativeScale(r.Width, r.Height, max);
        double up = UpscaleFactor(r.Width, r.Height, max);
        // One step from the caller's bitmap straight to the native-scale grayscale image (no full-res copy when
        // downscaling). It is private, so the caller's bitmap is not used after the first await.
        using var gray = Guard(() => Draw(image, r, native, grayscale: true));

        var first = await RecognizeAsync(engine, gray, r, ct).ConfigureAwait(false);
        // up > native only when native == 1 (the region already fits), so the upscale starts from a 1:1 image.
        if (up <= native + 1e-6 || (first.Count > 0 && MedianHeight(first) >= UpscaleBelowWordHeight))
            return first;

        using var big = Guard(() => Draw(gray, new Rectangle(0, 0, gray.Width, gray.Height), up, grayscale: false));
        var second = await RecognizeAsync(engine, big, r, ct).ConfigureAwait(false);
        return CharCount(second) > CharCount(first) ? second : first;
    }

    internal static T Guard<T>(Func<T> gdi)
    {
        try { return gdi(); }
        catch (Exception e) when (e is OutOfMemoryException or ArgumentException)
        {
            throw new InvalidDataException(TooLargeMessage, e);
        }
    }

    private static int CharCount(IReadOnlyList<OcrWord> words) => words.Sum(w => w.Text.Trim().Length);

    internal static double MedianHeight(IReadOnlyList<OcrWord> words)
    {
        var hs = words.Select(w => w.H).OrderBy(h => h).ToList();
        if (hs.Count == 0) return 0;
        return hs.Count % 2 == 1 ? hs[hs.Count / 2] : (hs[hs.Count / 2 - 1] + hs[hs.Count / 2]) / 2;
    }

    /// <summary>OCR on <paramref name="prepared"/>, an image of region <paramref name="r"/> at some scale.</summary>
    private static async Task<IReadOnlyList<OcrWord>> RecognizeAsync(OcrEngine engine, Bitmap prepared, Rectangle r, CancellationToken ct)
    {
        byte[] png = Guard(() =>
        {
            using var ms = new MemoryStream();
            prepared.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        });
        using var pngStream = new MemoryStream(png);
        using var ras = pngStream.AsRandomAccessStream();
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

    /// <summary>Draws <paramref name="r"/> of <paramref name="src"/> at <paramref name="scale"/> onto white.</summary>
    private static Bitmap Draw(Bitmap src, Rectangle r, double scale, bool grayscale)
    {
        int w = Math.Max(1, (int)Math.Floor(r.Width * scale));
        int h = Math.Max(1, (int)Math.Floor(r.Height * scale));
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(dst);
            using var attrs = new ImageAttributes();
            g.Clear(Color.White);
            bool oneToOne = w == r.Width && h == r.Height;
            g.InterpolationMode = oneToOne ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = oneToOne ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            if (grayscale) attrs.SetColorMatrix(Gray);
            attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges
            g.DrawImage(src, new Rectangle(0, 0, w, h), r.X, r.Y, r.Width, r.Height, GraphicsUnit.Pixel, attrs);
            return dst;
        }
        catch
        {
            dst.Dispose();
            throw;
        }
    }
}
