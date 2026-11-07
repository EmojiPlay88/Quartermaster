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

    /// <summary>Upscale factor of the empty-cell second pass.</summary>
    internal const double CellScale = 3.0;

    /// <summary>
    /// Second pass for cells the first reading left empty (Windows OCR tends to skip lone characters): each box (image px) is
    /// cropped, grayscaled and upscaled ×3; blank cells are skipped. The cells' ink is laid out in as few strips as fit the engine
    /// maximum, each after a printed anchor word ("Cell 1   Cell 2 …", <paramref name="padding"/> px source scale of white above
    /// and below), because the engine drops glyphs that stand alone; each strip is read once and the words are mapped back to
    /// their cell by x (anchor words dropped). Returns, per box, the words of its cell joined (union box in image px), or null.
    /// The bitmap is only touched before the first await.
    /// </summary>
    /// <exception cref="OcrUnavailableException">No OCR language installed, or not <paramref name="language"/>.</exception>
    /// <exception cref="InvalidDataException">GDI+ could not allocate/draw the tiles.</exception>
    public async Task<IReadOnlyList<OcrWord?>> ReadCellsAsync(Bitmap image, IReadOnlyList<RectangleF> boxes, double padding, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        var result = new OcrWord?[boxes.Count];
        if (boxes.Count == 0) return result;
        if (_languages().Count == 0) throw new OcrUnavailableException(NoLanguageMessage);
        var engine = _engineFactory(language) ?? throw new OcrUnavailableException(LanguageNotInstalledMessage(language));
        int max = (int)OcrEngine.MaxImageDimension;
        if (max <= 0) max = 10000;

        var bounds = new Rectangle(0, 0, image.Width, image.Height);
        int pad = Math.Max(4, (int)Math.Round(Math.Max(0, padding) * CellScale));
        var strips = new List<(Bitmap Strip, List<CellTile> Tiles)>();
        try
        {
            var pending = new List<(int Index, Rectangle Src, double Scale, Bitmap Tile, Rectangle Ink)>();
            try
            {
                for (int i = 0; i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    var src = Rectangle.Intersect(Rectangle.FromLTRB((int)Math.Floor(b.Left), (int)Math.Floor(b.Top), (int)Math.Ceiling(b.Right), (int)Math.Ceiling(b.Bottom)), bounds);
                    if (src.Width <= 0 || src.Height <= 0) continue;
                    double scale = Math.Min(CellScale, (double)(max - 2 * pad) / Math.Max(src.Width, src.Height));
                    if (scale <= 0) continue;
                    var tile = Guard(() => Draw(image, src, scale, grayscale: true));
                    var ink = InkBounds(tile);
                    if (ink.IsEmpty) { tile.Dispose(); continue; } // a blank cell: nothing to read
                    pending.Add((i, src, scale, tile, ink));
                }
                // Windows OCR drops glyphs that stand alone (tested: wide gaps read nothing, tight gaps merge the cells into one
                // number). So each cell's ink follows a printed anchor word, "Cell 1   Cell 2 …", read as an ordinary text line;
                // the anchors are dropped when the words are mapped back.
                double inkH = pending.Count == 0 ? 0 : Median(pending.Select(p => (double)p.Ink.Height));
                using var anchorFont = new Font("Segoe UI", (float)Math.Max(8, inkH / 0.72), FontStyle.Regular, GraphicsUnit.Pixel);
                int anchorW;
                using (var probe = new Bitmap(1, 1))
                using (var pg = Graphics.FromImage(probe))
                    anchorW = (int)Math.Ceiling(pg.MeasureString(Anchor, anchorFont, PointF.Empty, StringFormat.GenericTypographic).Width);
                int space = Math.Max(4, (int)Math.Round(0.6 * inkH)), gap = Math.Max(8, (int)Math.Round(1.6 * inkH));
                int start = 0;
                while (start < pending.Count)
                {
                    int x = pad, h = anchorFont.Height + 2 * pad, end = start;
                    var tiles = new List<CellTile>();
                    while (end < pending.Count)
                    {
                        var p = pending[end];
                        int w = anchorW + space + p.Ink.Width;
                        if (end > start && x + w + pad > max) break;
                        tiles.Add(new CellTile(p.Index, p.Src, p.Scale, x + anchorW + space, p.Ink.Width, p.Ink, x));
                        x += w + gap;
                        h = Math.Max(h, p.Ink.Height + 2 * pad);
                        end++;
                    }
                    int sw = Math.Max(1, Math.Min(max, x - gap + pad)), sh = Math.Max(1, Math.Min(max, h));
                    var first = start;
                    var strip = Guard(() =>
                    {
                        var bmp = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
                        try
                        {
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.Clear(Color.White);
                                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                                // Anchors: baseline level with the bottom of the cell's ink.
                                float ascent = anchorFont.Size * anchorFont.FontFamily.GetCellAscent(anchorFont.Style) / anchorFont.FontFamily.GetEmHeight(anchorFont.Style);
                                foreach (var tl in tiles)
                                    g.DrawString(Anchor, anchorFont, Brushes.Black, tl.AnchorX, TileTop(sh, tl) + tl.Ink.Height - ascent, StringFormat.GenericTypographic);
                                for (int k = 0; k < tiles.Count; k++)
                                {
                                    var tl = tiles[k];
                                    var top = TileTop(sh, tl);
                                    g.DrawImage(pending[first + k].Tile, new Rectangle(tl.X, top, tl.Ink.Width, tl.Ink.Height), tl.Ink, GraphicsUnit.Pixel);
                                }
                            }
                            return bmp;
                        }
                        catch
                        {
                            bmp.Dispose();
                            throw;
                        }
                    });
                    strips.Add((strip, tiles));
                    start = end;
                }
            }
            finally
            {
                foreach (var p in pending) p.Tile.Dispose();
            }

            foreach (var (strip, tiles) in strips)
            {
                var words = await RecognizeAsync(engine, strip, new Rectangle(0, 0, strip.Width, strip.Height), ct).ConfigureAwait(false);
                var perTile = new List<OcrWord>?[tiles.Count];
                foreach (var w in words)
                {
                    if (string.IsNullOrWhiteSpace(w.Text)) continue;
                    double cx = w.X + w.W / 2, best = double.MaxValue;
                    if (tiles.Any(t => cx < t.X && cx >= t.AnchorX)) continue; // an anchor word (however it was read)
                    int k = 0;
                    for (int j = 0; j < tiles.Count; j++)
                    {
                        double lo = tiles[j].X, hi = tiles[j].X + tiles[j].W;
                        double d = cx < lo ? lo - cx : cx > hi ? cx - hi : 0;
                        if (d < best) { best = d; k = j; }
                    }
                    (perTile[k] ??= new List<OcrWord>()).Add(w);
                }
                for (int j = 0; j < tiles.Count; j++)
                {
                    if (perTile[j] is not { Count: > 0 } ws) continue;
                    var tl = tiles[j];
                    double l = ws.Min(w => w.X), t = ws.Min(w => w.Y), r = ws.Max(w => w.X + w.W), b = ws.Max(w => w.Y + w.H);
                    int ty = TileTop(strip.Height, tl);
                    result[tl.Index] = new OcrWord(string.Join(" ", ws.OrderBy(w => w.X).Select(w => w.Text.Trim())),
                        tl.Src.X + (l - tl.X + tl.Ink.X) / tl.Scale, tl.Src.Y + (t - ty + tl.Ink.Y) / tl.Scale, (r - l) / tl.Scale, (b - t) / tl.Scale);
                }
            }
        }
        finally
        {
            foreach (var st in strips) st.Strip.Dispose();
        }
        return result;
    }

    private readonly record struct CellTile(int Index, Rectangle Src, double Scale, int X, int W, Rectangle Ink, int AnchorX);

    private const string Anchor = "Cell";

    private static int TileTop(int stripHeight, CellTile t) => (stripHeight - t.Ink.Height) / 2; // centred on one line

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    /// <summary>Bounding box of the dark pixels (luminance under 128) of a grayscale tile; empty when the tile is blank.</summary>
    internal static Rectangle InkBounds(Bitmap tile)
    {
        var data = tile.LockBits(new Rectangle(0, 0, tile.Width, tile.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
            var row = new byte[tile.Width * 4];
            for (int y = 0; y < tile.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < tile.Width; x++)
                {
                    int i = x * 4;
                    if ((row[i] * 114 + row[i + 1] * 587 + row[i + 2] * 299) / 1000 >= 128) continue;
                    if (x < l) l = x;
                    if (x > r) r = x;
                    if (y < t) t = y;
                    b = y;
                }
            }
            return r < 0 ? Rectangle.Empty : Rectangle.FromLTRB(l, t, r + 1, b + 1);
        }
        finally { tile.UnlockBits(data); }
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
