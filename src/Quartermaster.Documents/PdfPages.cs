using System.Collections.Concurrent;
using PDFtoImage;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Documents;

/// <summary>
/// A PDF opened for page pictures (PDFium through PDFtoImage). The file is read into memory, so nothing stays locked.
/// PDFium is not thread-safe: every call from every instance runs on one process-wide dedicated worker thread.
/// </summary>
public sealed class PdfPages : IDisposable
{
    /// <summary>A render is lowered in DPI until width x height fits this many pixels.</summary>
    public const long MaxPixels = 60_000_000;

    /// <summary>Message the viewer shows when a PDF cannot be used as a scan (the WinForms PdfPageRender message).</summary>
    public const string CantOpenAsScanMessage = "This PDF can't be opened as a scan.";

    private const int DisposeWaitMs = 2000;

    private readonly byte[] _bytes;
    private readonly SizeD[] _sizes;
    private readonly CancellationTokenSource _disposed = new();
    private readonly object _gate = new();
    private int _active;
    private bool _isDisposed;

    private PdfPages(byte[] bytes, SizeD[] sizes)
    {
        _bytes = bytes;
        _sizes = sizes;
    }

    public int PageCount => _sizes.Length;

    /// <exception cref="InvalidDataException">Corrupt, encrypted or empty PDF.</exception>
    public static PdfPages Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        SizeD[] sizes;
        try
        {
            sizes = Worker.Run(() =>
            {
                var raw = Conversion.GetPageSizes(bytes);
                var list = new SizeD[raw.Count];
                for (int i = 0; i < list.Length; i++) list[i] = new SizeD(raw[i].Width, raw[i].Height);
                return list;
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException(string.IsNullOrWhiteSpace(ex.Message) ? CantOpenAsScanMessage : ex.Message, ex);
        }
        if (sizes.Length == 0) throw new InvalidDataException("The PDF has no pages.");
        return new PdfPages(bytes, sizes);
    }

    /// <summary>Size in points of a 1-based page (clamped to the valid range), after /Rotate.</summary>
    public SizeD PageSize(int page) => _sizes[Clamp(page) - 1];

    /// <summary>
    /// Renders a 1-based page (clamped) on a white background with annotations. The DPI is lowered when needed so the bitmap stays within
    /// <see cref="MaxPixels"/>. The caller owns the bitmap.
    /// </summary>
    public Task<SKBitmap> RenderAsync(int page, double dpi, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _active++;
        }
        int index = Clamp(page) - 1;
        var size = _sizes[index];
        double useDpi = ClampDpi(size, dpi);
        var (w, h) = PixelSize(size, useDpi);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        var task = Worker.Run(() =>
        {
            var options = new RenderOptions(Dpi: (int)Math.Max(1, Math.Round(useDpi)), Width: w, Height: h, WithAnnotations: true,
                BackgroundColor: SKColors.White);
            return Conversion.ToImage(_bytes, index, options: options);
        }, linked.Token);
        _ = Finish(task, linked);
        return task;
    }

    private async Task Finish(Task<SKBitmap> task, CancellationTokenSource linked)
    {
        try
        {
            // Observe our own outcome so a render nobody awaits never becomes an unobserved task exception.
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            linked.Dispose();
            lock (_gate)
            {
                _active--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>The DPI to render at: <paramref name="dpi"/>, lowered when the page would exceed <see cref="MaxPixels"/>.</summary>
    public static double ClampDpi(SizeD pagePoints, double dpi)
    {
        if (!(dpi > 0)) dpi = 72;
        if (PixelCount(pagePoints, dpi) <= MaxPixels) return dpi;
        double lowered = dpi * Math.Sqrt((double)MaxPixels / ((pagePoints.W * dpi / 72.0) * (pagePoints.H * dpi / 72.0)));
        // Rounding the sides up can overshoot by a few pixels; nudge down until it fits.
        while (PixelCount(pagePoints, lowered) > MaxPixels) lowered *= 0.99999;
        return lowered;
    }

    private static (int W, int H) PixelSize(SizeD points, double dpi) =>
        (Math.Max(1, (int)Math.Ceiling(points.W * dpi / 72.0 - 1e-6)), Math.Max(1, (int)Math.Ceiling(points.H * dpi / 72.0 - 1e-6)));

    private static long PixelCount(SizeD points, double dpi)
    {
        var (w, h) = PixelSize(points, dpi);
        return (long)w * h;
    }

    private int Clamp(int page) => Math.Clamp(page, 1, _sizes.Length);

    /// <summary>Cancels queued renders and waits up to 2 s for the running one.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }
        _disposed.Cancel();
        lock (_gate)
        {
            var deadline = Environment.TickCount64 + DisposeWaitMs;
            while (_active > 0)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0) break;
                Monitor.Wait(_gate, (int)left);
            }
        }
        // _disposed is deliberately not disposed: a render finishing after the 2 s wait may still look at its token.
    }

    /// <summary>The one thread that talks to PDFium for the whole process.</summary>
    private static class Worker
    {
        private static readonly BlockingCollection<Action> Queue = new();

        static Worker()
        {
            new Thread(Loop) { IsBackground = true, Name = "Quartermaster PDFium" }.Start();
        }

        private static void Loop()
        {
            foreach (var work in Queue.GetConsumingEnumerable()) work();
        }

        public static Task<T> Run<T>(Func<T> func, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Add(() =>
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                try
                {
                    var result = func();
                    if (ct.IsCancellationRequested && result is IDisposable d)
                    {
                        d.Dispose();
                        tcs.TrySetCanceled(ct);
                    }
                    else tcs.TrySetResult(result);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }
    }
}
