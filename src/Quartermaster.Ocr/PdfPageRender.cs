using System.Drawing;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Quartermaster.Ocr;

/// <summary>Renders PDF pages to bitmaps with Windows.Data.Pdf (used by "Scan this page").</summary>
public static class PdfPageRender
{
    public const string CantOpenMessage = "This PDF can't be opened as a scan.";

    /// <summary>Rendered pages are kept at or under this many pixels (the effective DPI is lowered).</summary>
    internal const long MaxPixels = 60_000_000;

    /// <summary>
    /// Renders 1-based <paramref name="page"/> at <paramref name="dpi"/>, lowered if needed to stay ≤ 60 megapixels
    /// (the bitmap's resolution is the effective DPI).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">No such page.</exception>
    /// <exception cref="FileNotFoundException">The file is missing.</exception>
    /// <exception cref="InvalidDataException">Corrupt, encrypted or not a PDF (<see cref="CantOpenMessage"/>).</exception>
    public static async Task<Bitmap> RenderAsync(string pdfPath, int page, int dpi = 200, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 1);
        var doc = await LoadAsync(pdfPath, ct).ConfigureAwait(false);
        if (page < 1 || page > doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(page), page, $"The PDF has {doc.PageCount} page(s).");
        return await RenderPageAsync(doc, page, dpi, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the PDF once and renders 1-based <paramref name="page"/>, clamped to its pages, at <paramref name="dpi"/> (≤ 60 MP as in
    /// <see cref="RenderAsync"/>). Returns the bitmap, the page count and the page rendered.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file is missing.</exception>
    /// <exception cref="InvalidDataException">Corrupt, encrypted, not a PDF, or no pages (<see cref="CantOpenMessage"/>).</exception>
    public static async Task<(Bitmap Bitmap, int PageCount, int Page)> RenderClampedAsync(string pdfPath, int page, int dpi = 200, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 1);
        var doc = await LoadAsync(pdfPath, ct).ConfigureAwait(false);
        var count = (int)doc.PageCount;
        if (count < 1) throw new InvalidDataException(CantOpenMessage);
        page = Math.Clamp(page, 1, count);
        return (await RenderPageAsync(doc, page, dpi, ct).ConfigureAwait(false), count, page);
    }

    private static async Task<Bitmap> RenderPageAsync(PdfDocument doc, int page, int dpi, CancellationToken ct)
    {
        try
        {
            using var p = doc.GetPage((uint)(page - 1));
            var size = p.Size; // DIPs (1/96 in), after the page's rotation
            var (width, height, effectiveDpi) = OutputSize(size.Width, size.Height, dpi);
            using var stream = new InMemoryRandomAccessStream();
            await p.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = width, DestinationHeight = height })
                .AsTask(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            stream.Seek(0);
            using var net = stream.AsStreamForRead();
            using var decoded = new Bitmap(net);
            var bmp = new Bitmap(decoded); // detach from the stream
            bmp.SetResolution((float)effectiveDpi, (float)effectiveDpi);
            return bmp;
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            throw new InvalidDataException(CantOpenMessage, e);
        }
    }

    /// <summary>Pixel size at <paramref name="dpi"/> for a page of the given size in DIPs, clamped to ≤ 60 MP.</summary>
    internal static (uint Width, uint Height, double Dpi) OutputSize(double widthDips, double heightDips, int dpi)
    {
        double eff = dpi;
        double w = widthDips / 96.0 * eff, h = heightDips / 96.0 * eff;
        if (w * h > MaxPixels)
        {
            double f = Math.Sqrt(MaxPixels / (w * h));
            eff *= f;
            w = widthDips / 96.0 * eff;
            h = heightDips / 96.0 * eff;
        }
        uint W = (uint)Math.Max(1, Math.Floor(w)), H = (uint)Math.Max(1, Math.Floor(h));
        // Round to nearest when that still fits (the usual case: exact page sizes like 1700×2200).
        uint rw = (uint)Math.Max(1, Math.Round(w)), rh = (uint)Math.Max(1, Math.Round(h));
        if ((long)rw * rh <= MaxPixels) (W, H) = (rw, rh);
        return (W, H, eff);
    }

    /// <exception cref="FileNotFoundException">The file is missing.</exception>
    /// <exception cref="InvalidDataException">Corrupt, encrypted or not a PDF.</exception>
    public static async Task<int> PageCountAsync(string pdfPath, CancellationToken ct = default) =>
        (int)(await LoadAsync(pdfPath, ct).ConfigureAwait(false)).PageCount;

    private static async Task<PdfDocument> LoadAsync(string pdfPath, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath)).AsTask(ct).ConfigureAwait(false);
        try
        {
            return await PdfDocument.LoadFromFileAsync(file).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            throw new InvalidDataException(CantOpenMessage, e);
        }
    }

    private static bool IsUnreadable(Exception e) =>
        e is not (OperationCanceledException or FileNotFoundException or InvalidDataException);
}
