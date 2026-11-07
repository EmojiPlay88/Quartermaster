using System.Drawing;
using Quartermaster.Rules.Tables;
using Quartermaster.Rules;

namespace Quartermaster.Render.Ocr;

/// <summary>Where a scan comes from: a PDF or image inside the module, or a file the user opened with "Open scan…".</summary>
public enum ScanSourceKind { ModulePdf, ModuleImage, FilePdf, FileImage }

/// <summary>
/// A scan source. <see cref="Key"/> identifies it for the session (archive entry, or full file path);
/// <see cref="RefEntry"/> is what a rule ref stores (archive entry, or the file name for opened files).
/// </summary>
public sealed record ScanSource(ScanSourceKind Kind, string Key, string Title, string RefEntry)
{
    public bool IsPdf => Kind is ScanSourceKind.ModulePdf or ScanSourceKind.FilePdf;
    public bool InModule => Kind is ScanSourceKind.ModulePdf or ScanSourceKind.ModuleImage;

    public static ScanSource ModuleImage(string entry) =>
        new(ScanSourceKind.ModuleImage, entry, entry.Replace('\\', '/'), entry);

    public static ScanSource ModulePdf(string entry, string title) =>
        new(ScanSourceKind.ModulePdf, entry, string.IsNullOrWhiteSpace(title) ? entry : title, entry);

    public static ScanSource File(string path)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        return new(ScanSessionModel.IsPdfName(full) ? ScanSourceKind.FilePdf : ScanSourceKind.FileImage, full, name, name);
    }
}

/// <summary>One OCR read: <see cref="Id"/> tells stale results apart; <see cref="Token"/> is cancelled when a newer read starts.</summary>
public readonly record struct ReadTicket(int Id, CancellationToken Token);

/// <summary>
/// Session state of the workbench scan pane, kept out of the UI so it can be tested: the opened scans, the last region per
/// (source, page) in image pixels, and the one-read-at-a-time ticket that drops stale results.
/// </summary>
public sealed class ScanSessionModel
{
    public static readonly IReadOnlyList<string> ModuleImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp"];
    public static readonly IReadOnlyList<string> ScanFileExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".pdf"];

    /// <summary>Filter for the "Open scan…" file dialog.</summary>
    public static string OpenScanFilter =>
        "Scans (" + string.Join(", ", ScanFileExtensions.Select(e => "*" + e)) + ")|" + string.Join(";", ScanFileExtensions.Select(e => "*" + e))
        + "|All files (*.*)|*.*";

    public const double MinZoom = 0.25, MaxZoom = 4.0;
    public static readonly IReadOnlyList<int> ZoomSteps = [25, 50, 75, 100, 150, 200, 300, 400];
    /// <summary>A dragged rectangle smaller than this (image px) on either side is treated as a click, not a region.</summary>
    public const int MinRegionSide = 4;

    static bool HasExt(string name, IReadOnlyList<string> exts)
    {
        var ext = Path.GetExtension(name ?? "");
        return exts.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsModuleImage(string entry) => HasExt(entry, ModuleImageExtensions);
    public static bool IsScanFile(string path) => HasExt(path, ScanFileExtensions);
    /// <summary>An image file or entry (a scan file type that is not a PDF).</summary>
    public static bool IsImageName(string path) => IsScanFile(path) && !IsPdfName(path);
    public static bool IsPdfName(string path) => string.Equals(Path.GetExtension(path ?? ""), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The module's image entries (by extension), sorted case-insensitively.</summary>
    public static IReadOnlyList<ScanSource> ModuleImages(IEnumerable<string> entries) =>
        entries.Where(e => !e.EndsWith('/') && IsModuleImage(e))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .Select(ScanSource.ModuleImage).ToList();

    // ---------- opened scans ----------

    readonly List<ScanSource> _opened = new();

    /// <summary>Files opened with "Open scan…" this session, in the order they were first opened.</summary>
    public IReadOnlyList<ScanSource> Opened => _opened;

    /// <summary>Adds an opened file (once per full path) and returns its source.</summary>
    public ScanSource AddFile(string path)
    {
        var s = ScanSource.File(path);
        var existing = _opened.FirstOrDefault(o => string.Equals(o.Key, s.Key, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        _opened.Add(s);
        return s;
    }

    /// <summary>The opened PDFs, which the rulebook search also indexes for this session.</summary>
    public IEnumerable<ScanSource> OpenedPdfs => _opened.Where(o => o.Kind == ScanSourceKind.FilePdf);

    // ---------- regions ----------

    readonly Dictionary<(string, int), (Size Image, Rectangle Region)> _regions = new();

    static (string, int) RegionKey(string source, int page) => (source.ToUpperInvariant(), page);

    /// <summary>Remembers the region (image pixels, clipped to the image) for a source page; null or empty clears it.</summary>
    public void SetRegion(string source, int page, Size image, Rectangle? region)
    {
        var key = RegionKey(source, page);
        var r = region is { } g ? Rectangle.Intersect(g, new Rectangle(Point.Empty, image)) : Rectangle.Empty;
        if (r.Width <= 0 || r.Height <= 0) _regions.Remove(key);
        else _regions[key] = (image, r);
    }

    /// <summary>The remembered region, or null; a region stored for a different image size is cleared.</summary>
    public Rectangle? GetRegion(string source, int page, Size image)
    {
        var key = RegionKey(source, page);
        if (!_regions.TryGetValue(key, out var v)) return null;
        if (v.Image != image) { _regions.Remove(key); return null; }
        return v.Region;
    }

    // ---------- reads ----------

    int _readId;
    CancellationTokenSource? _readCts;

    /// <summary>True between <see cref="BeginRead"/> and its <see cref="EndRead"/> (or a cancel).</summary>
    public bool IsReading => _readCts != null;

    /// <summary>Starts a read: the running one (if any) is cancelled and its result will be stale.</summary>
    public ReadTicket BeginRead()
    {
        CancelRead();
        _readCts = new CancellationTokenSource();
        return new ReadTicket(++_readId, _readCts.Token);
    }

    /// <summary>True when the read is the latest one and was not cancelled: only then is its result shown.</summary>
    public bool IsCurrent(ReadTicket t) => t.Id == _readId && _readCts != null && !t.Token.IsCancellationRequested;

    /// <summary>Finishes a read; a stale ticket changes nothing.</summary>
    public void EndRead(ReadTicket t)
    {
        if (t.Id != _readId || _readCts == null) return;
        _readCts.Dispose();
        _readCts = null;
    }

    /// <summary>Cancels the running read (Cancel button, another image or region). Its result is dropped.</summary>
    public void CancelRead()
    {
        if (_readCts == null) return;
        _readId++;
        try { _readCts.Cancel(); } catch (ObjectDisposedException) { }
        _readCts.Dispose();
        _readCts = null;
    }

    // ---------- import ----------

    /// <summary>The rule ref an imported table gets: the document and page; an image has no page (0).</summary>
    public static (string Entry, int Page) RuleRefFor(ScanSource source, int page) =>
        source.IsPdf ? (source.RefEntry, Math.Max(1, page)) : (source.RefEntry, 0);

    /// <summary>The note under the Paste table preview naming where the rows go.</summary>
    public static string TargetDescription(string tableName, bool newTable) =>
        newTable ? $"OK creates the new table \"{tableName}\"." : $"OK replaces the rows, columns and cells of the table \"{tableName}\".";

    /// <summary>"Scanned table", else "Scanned table 2", 3… (case-insensitive against the existing names).</summary>
    public static string SuggestTableName(IEnumerable<string> existing, string baseName = "Scanned table")
    {
        var names = new HashSet<string>(existing.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
            if (!names.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }

    /// <summary>A new table is two-dimensional when the pasted grid has more than one value column.</summary>
    public static TableKind KindFor(PastedGrid g)
    {
        var width = Math.Max(g.ColumnKeys.Count, g.Cells.Count == 0 ? 0 : g.Cells.Max(r => r.Count));
        return width > 1 ? TableKind.TwoD : TableKind.OneD;
    }

    // ---------- zoom ----------

    public static double ClampZoom(double z) => double.IsFinite(z) ? Math.Clamp(z, MinZoom, MaxZoom) : 1.0;

    /// <summary>The zoom that fits the whole image into the view (clamped to 25–400 %).</summary>
    public static double FitZoom(Size image, Size view)
    {
        if (image.Width <= 0 || image.Height <= 0 || view.Width <= 0 || view.Height <= 0) return 1.0;
        return ClampZoom(Math.Min((double)view.Width / image.Width, (double)view.Height / image.Height));
    }

    /// <summary>The next zoom step up (+1) or down (-1) from <paramref name="zoom"/>.</summary>
    public static double StepZoom(double zoom, int direction)
    {
        var pct = zoom * 100;
        if (direction > 0)
        {
            foreach (var s in ZoomSteps) if (s > pct + 0.5) return s / 100.0;
            return MaxZoom;
        }
        for (var i = ZoomSteps.Count - 1; i >= 0; i--) if (ZoomSteps[i] < pct - 0.5) return ZoomSteps[i] / 100.0;
        return MinZoom;
    }
}

/// <summary>Which part of a region the mouse is on: an edge or corner handle, the inside (move), or nothing.</summary>
public enum RegionHandle { None, Move, N, S, E, W, NE, NW, SE, SW }

/// <summary>Region drag/adjust maths in image pixels.</summary>
public static class RegionEdit
{
    /// <summary>The rectangle spanned by two drag points, clipped to the image; null when too small to be a region.</summary>
    public static Rectangle? FromDrag(Point a, Point b, Size image)
    {
        var r = Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        r = Rectangle.Intersect(r, new Rectangle(Point.Empty, image));
        return r.Width < ScanSessionModel.MinRegionSide || r.Height < ScanSessionModel.MinRegionSide ? null : r;
    }

    /// <summary>The handle under <paramref name="p"/> (image px); <paramref name="tolerance"/> is the grab distance in image px.</summary>
    public static RegionHandle HitTest(Rectangle r, PointF p, double tolerance)
    {
        if (r.Width <= 0 || r.Height <= 0) return RegionHandle.None;
        bool nearL = Math.Abs(p.X - r.Left) <= tolerance, nearR = Math.Abs(p.X - r.Right) <= tolerance;
        bool nearT = Math.Abs(p.Y - r.Top) <= tolerance, nearB = Math.Abs(p.Y - r.Bottom) <= tolerance;
        bool inX = p.X >= r.Left - tolerance && p.X <= r.Right + tolerance;
        bool inY = p.Y >= r.Top - tolerance && p.Y <= r.Bottom + tolerance;
        if (!inX || !inY) return RegionHandle.None;
        if (nearT && nearL) return RegionHandle.NW;
        if (nearT && nearR) return RegionHandle.NE;
        if (nearB && nearL) return RegionHandle.SW;
        if (nearB && nearR) return RegionHandle.SE;
        if (nearT) return RegionHandle.N;
        if (nearB) return RegionHandle.S;
        if (nearL) return RegionHandle.W;
        if (nearR) return RegionHandle.E;
        return RegionHandle.Move;
    }

    /// <summary>
    /// The region after dragging <paramref name="h"/> by (dx, dy) image px from <paramref name="start"/>: edges move (and may cross,
    /// the result is normalised), Move shifts it kept inside the image; the result is clipped to the image and at least 1 px.
    /// </summary>
    public static Rectangle Adjust(Rectangle start, RegionHandle h, int dx, int dy, Size image)
    {
        var bounds = new Rectangle(Point.Empty, image);
        if (h == RegionHandle.Move)
        {
            int x = Math.Clamp(start.X + dx, 0, Math.Max(0, image.Width - start.Width));
            int y = Math.Clamp(start.Y + dy, 0, Math.Max(0, image.Height - start.Height));
            return Rectangle.Intersect(new Rectangle(x, y, start.Width, start.Height), bounds);
        }
        int l = start.Left, t = start.Top, rr = start.Right, b = start.Bottom;
        if (h is RegionHandle.W or RegionHandle.NW or RegionHandle.SW) l += dx;
        if (h is RegionHandle.E or RegionHandle.NE or RegionHandle.SE) rr += dx;
        if (h is RegionHandle.N or RegionHandle.NW or RegionHandle.NE) t += dy;
        if (h is RegionHandle.S or RegionHandle.SW or RegionHandle.SE) b += dy;
        var r = Rectangle.FromLTRB(Math.Min(l, rr), Math.Min(t, b), Math.Max(l, rr), Math.Max(t, b));
        r = Rectangle.Intersect(r, bounds);
        if (r.Width < 1) r.Width = Math.Min(1, image.Width - r.X);
        if (r.Height < 1) r.Height = Math.Min(1, image.Height - r.Y);
        return r;
    }
}
