using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Documents;

/// <summary>A loaded scan: the bitmap (the caller owns it), the PDF's page count (1 for an image), the page shown, and a note.</summary>
public sealed record ScanImage(SKBitmap Bitmap, int PageCount, int Page, string? Note);

/// <summary>
/// Loads a scan source for the workbench: a module entry is copied to the scan cache once per (archive SHA, entry) and reused, a PDF page
/// is rendered at <see cref="ScanDpi"/>, an image goes through <see cref="ScanImageLoad"/>. The work runs off the calling thread.
/// </summary>
public sealed class ScanLoader(Func<string, Stream> openEntry, string archiveSha) : IDisposable
{
    public const int ScanDpi = 200;

    private readonly object _gate = new();
    private readonly Dictionary<(string, string), Task<string>> _extracts = new();
    private int _extractRuns;
    private bool _disposed;

    /// <summary>Test seam: called with the decoded bitmap before the cancellation check.</summary>
    internal Action<SKBitmap>? Decoded { get; set; }

    /// <summary>How many module-entry extractions ran (an entry is extracted once; a failed extraction is retried).</summary>
    public int ExtractCount => Volatile.Read(ref _extractRuns);

    /// <summary>
    /// Loads <paramref name="src"/>. A PDF page is clamped to the document and the result says which page was shown. When the load is
    /// cancelled the bitmap is disposed before the <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<ScanImage> LoadAsync(ScanSource src, int page, CancellationToken ct)
    {
        var path = await PathFor(src).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        SKBitmap? bmp = null;
        try
        {
            ScanImage result;
            if (src.IsPdf)
            {
                result = await Task.Run(async () =>
                {
                    using var pdf = PdfPages.Open(path);
                    int shown = Math.Clamp(page, 1, pdf.PageCount);
                    var b = await pdf.RenderAsync(shown, ScanDpi, ct).ConfigureAwait(false);
                    return new ScanImage(b, pdf.PageCount, shown, null);
                }, ct).ConfigureAwait(false);
                bmp = result.Bitmap;
            }
            else
            {
                result = await Task.Run(() =>
                {
                    var b = ScanImageLoad.Load(path, out var count);
                    return new ScanImage(b, 1, 1, count > 1 ? $"This TIFF has {count} pages; only the first is shown." : null);
                }, ct).ConfigureAwait(false);
                bmp = result.Bitmap;
            }
            Decoded?.Invoke(bmp);
            ct.ThrowIfCancellationRequested();
            bmp = null; // handed to the caller
            return result;
        }
        finally
        {
            bmp?.Dispose();
        }
    }

    /// <summary>The message the pane shows when <see cref="LoadAsync"/> failed with <paramref name="ex"/> (same texts as the WinForms pane).</summary>
    public static string FailureMessage(ScanSource src, int page, Exception ex) => ex switch
    {
        InvalidDataException => ex.Message,
        FileNotFoundException or DirectoryNotFoundException => $"{src.Title} was not found.",
        ArgumentOutOfRangeException when src.IsPdf => $"{src.Title} has no page {page}.",
        _ when src.IsPdf => PdfPages.CantOpenAsScanMessage,
        _ => "This file can't be opened as an image.",
    };

    /// <summary>The file to load: module entries are copied to the scan cache once, opened files are used in place.</summary>
    private Task<string> PathFor(ScanSource src)
    {
        if (!src.InModule) return Task.FromResult(src.Key);
        var key = (archiveSha, src.Key);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_extracts.TryGetValue(key, out var t) && !t.IsFaulted && !t.IsCanceled && (!t.IsCompleted || File.Exists(t.Result))) return t;
            var ext = Path.GetExtension(src.Key);
            t = Task.Run(() => ScanCache.Extract(() => openEntry(src.Key), ext));
            _extracts[key] = t; // a failed extraction is retried next time
            _extractRuns++;
            return t;
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }
}
