using System.Text;
using Quartermaster.Vassal;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Quartermaster.Render.Search;

/// <summary>
/// Search-hit boxes for one PDF, from its text layer (PdfPig glyph rectangles). <see cref="Find"/> matches a term with the same
/// rules as rulebook search (accents/case ignored, hits start at a word start) and returns one box per line segment of each hit,
/// as fractions of the rendered page (0..1, origin top-left, in the page's displayed orientation after /Rotate).
/// Thread-safe; per-page letter data is cached. Nothing here throws for a bad page.
/// </summary>
public sealed class PdfHighlights : IDisposable
{
    private readonly object _lock = new();
    private readonly PdfDocument _doc;
    private readonly Dictionary<int, PageData?> _cache = new();
    private bool _disposed;

    private PdfHighlights(PdfDocument doc) => _doc = doc;

    /// <summary>Opens a PDF (the file is read into memory, so it is not locked). Throws like PdfPig for an unreadable file.</summary>
    public static PdfHighlights Open(string path) => new(PdfDocument.Open(File.ReadAllBytes(path)));

    private readonly record struct Glyph(double Left, double Bottom, double Right, double Top, double Across);

    /// <summary>A page's text (letters joined with inserted spaces), a map from text char to glyph index (-1 = inserted space).</summary>
    private sealed record PageData(string Norm, int[] NormToGlyph, Glyph[] Glyphs, double X0, double Y0, double W, double H);

    /// <summary>Boxes of every hit of <paramref name="term"/> on 1-based <paramref name="page"/>; empty when there are none.</summary>
    public IReadOnlyList<RectD> Find(int page, string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) return Array.Empty<RectD>();
        var needle = TextNormaliser.Normalise(term).Trim();
        if (needle.Length == 0) return Array.Empty<RectD>();
        lock (_lock)
        {
            if (_disposed || page < 1 || page > _doc.NumberOfPages) return Array.Empty<RectD>();
            if (!_cache.TryGetValue(page, out var data))
            {
                data = Load(page);
                _cache[page] = data;
            }
            if (data is null || data.Norm.Length == 0) return Array.Empty<RectD>();

            var boxes = new List<RectD>();
            var at = TextNormaliser.IndexOfWordStart(data.Norm, needle, 0);
            while (at >= 0)
            {
                AddBoxes(data, at, needle.Length, boxes);
                at = TextNormaliser.IndexOfWordStart(data.Norm, needle, at + needle.Length);
            }
            return boxes;
        }
    }

    private PageData? Load(int pageNumber)
    {
        try
        {
            var page = _doc.GetPage(pageNumber);
            // PdfPig already puts letters in the displayed orientation (after /Rotate): Width/Height are the displayed size, the
            // origin is bottom-left. Only an unrotated page can have a crop box that does not start at 0,0.
            double w = page.Width, h = page.Height;
            if (w <= 0 || h <= 0) return null;
            var rot = ((page.Rotation.Value % 360) + 360) % 360;
            double x0 = 0, y0 = 0;
            if (rot == 0) { var box = page.CropBox.Bounds; x0 = box.Left; y0 = box.Bottom; }

            var raw = new StringBuilder();
            var rawToGlyph = new List<int>();
            var glyphs = new List<Glyph>();
            Letter? prev = null;
            foreach (var l in page.Letters)
            {
                var value = l.Value;
                if (string.IsNullOrEmpty(value)) continue;
                var blank = string.IsNullOrWhiteSpace(value);
                if (prev is not null && !blank && NeedsSpace(prev, l))
                {
                    raw.Append(' ');
                    rawToGlyph.Add(-1);
                }
                if (blank)
                {
                    raw.Append(' ');
                    rawToGlyph.Add(-1);
                }
                else
                {
                    var r = l.BoundingBox;
                    glyphs.Add(new Glyph(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top), Across(l)));
                    foreach (var c in value)
                    {
                        raw.Append(c);
                        rawToGlyph.Add(glyphs.Count - 1);
                    }
                }
                prev = l;
            }
            var (norm, map) = TextNormaliser.NormaliseWithMap(raw.ToString(), collapseSpaces: true);
            var normToGlyph = new int[map.Length];
            for (var i = 0; i < map.Length; i++) normToGlyph[i] = rawToGlyph[map[i]];
            return new PageData(norm, normToGlyph, glyphs.ToArray(), x0, y0, w, h);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null; // a broken page has no hits
        }
    }

    private static bool Vertical(Letter l) => l.TextOrientation is TextOrientation.Rotate90 or TextOrientation.Rotate270;

    /// <summary>The baseline's position across the text direction (y for horizontal text, x for vertical).</summary>
    private static double Across(Letter l) => Vertical(l) ? l.StartBaseLine.X : l.StartBaseLine.Y;

    private static bool NeedsSpace(Letter prev, Letter cur)
    {
        var size = Math.Max(prev.FontSize, 1);
        if (Math.Abs(Across(cur) - Across(prev)) > 0.5 * size) return true; // baseline change (new line)
        // Gap along the text direction (up for Rotate270, down for Rotate90, left for Rotate180, else right).
        var gap = cur.TextOrientation switch
        {
            TextOrientation.Rotate90 => prev.EndBaseLine.Y - cur.StartBaseLine.Y,
            TextOrientation.Rotate270 => cur.StartBaseLine.Y - prev.EndBaseLine.Y,
            TextOrientation.Rotate180 => prev.EndBaseLine.X - cur.StartBaseLine.X,
            _ => cur.StartBaseLine.X - prev.EndBaseLine.X,
        };
        return gap > 0.3 * size;
    }

    private static void AddBoxes(PageData d, int start, int length, List<RectD> boxes)
    {
        // One box per run of glyphs on one baseline.
        double l = 0, b = 0, r = 0, t = 0, baseline = 0;
        var open = false;
        void Flush()
        {
            if (open) boxes.Add(ToFraction(d, l, b, r, t));
            open = false;
        }
        for (var i = start; i < start + length && i < d.NormToGlyph.Length; i++)
        {
            var gi = d.NormToGlyph[i];
            if (gi < 0) continue;
            var g = d.Glyphs[gi];
            var height = Math.Max(g.Top - g.Bottom, 1);
            if (open && Math.Abs(g.Across - baseline) > 0.5 * height) Flush();
            if (!open)
            {
                (l, b, r, t, baseline, open) = (g.Left, g.Bottom, g.Right, g.Top, g.Across, true);
                continue;
            }
            l = Math.Min(l, g.Left); b = Math.Min(b, g.Bottom); r = Math.Max(r, g.Right); t = Math.Max(t, g.Top);
        }
        Flush();
    }

    /// <summary>Letter-space rectangle (origin bottom-left, displayed orientation) to a fraction box of the page (origin top-left).</summary>
    private static RectD ToFraction(PageData d, double l, double b, double r, double t)
    {
        double u0 = (l - d.X0) / d.W, u1 = (r - d.X0) / d.W;   // left..right
        double v0 = 1 - (t - d.Y0) / d.H, v1 = 1 - (b - d.Y0) / d.H; // top..bottom
        double x0 = Math.Clamp(u0, 0, 1), y0 = Math.Clamp(v0, 0, 1), x1 = Math.Clamp(u1, 0, 1), y1 = Math.Clamp(v1, 0, 1);
        return new RectD(x0, y0, x1 - x0, y1 - y0);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear();
            _doc.Dispose();
        }
    }
}
