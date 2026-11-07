using System.Drawing;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Quartermaster.Ocr;

/// <summary>Renders PDF pages to bitmaps with Windows.Data.Pdf (used by "Scan this page").</summary>
public static class PdfPageRender
{
    /// <summary>Renders 1-based <paramref name="page"/> at <paramref name="dpi"/> (PDF points are 1/72 in).</summary>
    public static async Task<Bitmap> RenderAsync(string pdfPath, int page, int dpi = 200, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 1);
        var doc = await LoadAsync(pdfPath, ct).ConfigureAwait(false);
        if (page < 1 || page > doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(page), page, $"The PDF has {doc.PageCount} page(s).");
        using var p = doc.GetPage((uint)(page - 1));
        var size = p.Size; // in DIPs (1/96 in) after the page's rotation
        uint width = (uint)Math.Max(1, Math.Round(size.Width / 96.0 * dpi));
        uint height = (uint)Math.Max(1, Math.Round(size.Height / 96.0 * dpi));
        using var stream = new InMemoryRandomAccessStream();
        await p.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = width, DestinationHeight = height })
            .AsTask(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        stream.Seek(0);
        using var net = stream.AsStreamForRead();
        using var decoded = new Bitmap(net);
        var bmp = new Bitmap(decoded); // detach from the stream
        bmp.SetResolution(dpi, dpi);
        return bmp;
    }

    public static int PageCount(string pdfPath) =>
        (int)LoadAsync(pdfPath, CancellationToken.None).GetAwaiter().GetResult().PageCount;

    private static async Task<PdfDocument> LoadAsync(string pdfPath, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath)).AsTask(ct).ConfigureAwait(false);
        return await PdfDocument.LoadFromFileAsync(file).AsTask(ct).ConfigureAwait(false);
    }
}
