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

    /// <summary>Regions shorter than this are upscaled (about 2×) before OCR.</summary>
    internal const int UpscaleBelowHeight = 600;

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
    /// the original image's pixel coordinates. The bitmap is only touched before the first await.
    /// </summary>
    public async Task<IReadOnlyList<OcrWord>> ReadAsync(Bitmap image, Rectangle? region, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        var engine = _engineFactory(language) ?? throw new OcrUnavailableException(NoLanguageMessage);

        var bounds = new Rectangle(0, 0, image.Width, image.Height);
        var r = region is Rectangle given ? Rectangle.Intersect(given, bounds) : bounds;
        if (r.Width <= 0 || r.Height <= 0) return [];

        double scale = Scale(r.Width, r.Height, (int)OcrEngine.MaxImageDimension);
        byte[] png = PrepareGrayPng(image, r, scale);

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

    /// <summary>~2× when shorter than 600 px; then capped so neither side exceeds the engine maximum.</summary>
    internal static double Scale(int width, int height, int maxDimension)
    {
        double s = height < UpscaleBelowHeight ? 2.0 : 1.0;
        int longest = Math.Max(width, height);
        if (maxDimension > 0 && longest * s > maxDimension) s = (double)maxDimension / longest;
        return s;
    }

    private static readonly ColorMatrix Gray = new(
    [
        [0.299f, 0.299f, 0.299f, 0, 0],
        [0.587f, 0.587f, 0.587f, 0, 0],
        [0.114f, 0.114f, 0.114f, 0, 0],
        [0, 0, 0, 1, 0],
        [0, 0, 0, 0, 1],
    ]);

    private static byte[] PrepareGrayPng(Bitmap image, Rectangle r, double scale)
    {
        int w = Math.Max(1, (int)Math.Floor(r.Width * scale));
        int h = Math.Max(1, (int)Math.Floor(r.Height * scale));
        using var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        using (var attrs = new ImageAttributes())
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            attrs.SetColorMatrix(Gray);
            attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the crop edges
            g.DrawImage(image, new Rectangle(0, 0, w, h), r.X, r.Y, r.Width, r.Height, GraphicsUnit.Pixel, attrs);
        }
        using var ms = new MemoryStream();
        dst.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
