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
    /// <summary>Longest rendered side in pixels (GPU texture limit).</summary>
    public const int MaxSide = 16384;

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

    /// <remarks>Blocks on the shared PDF worker thread; call off the UI thread.</remarks>
    /// <exception cref="InvalidDataException">Corrupt, encrypted or empty PDF.</exception>
    public static PdfPages Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        SizeD[] sizes;
        try
        {
            sizes = Worker.Run(() =>
            {
#pragma warning disable CA1416 // see RenderCore
                var raw = Conversion.GetPageSizes(bytes);
#pragma warning restore CA1416
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
        lock (_gate) ObjectDisposedException.ThrowIf(_isDisposed, this);
        int index = Clamp(page) - 1;
        var size = _sizes[index];
        double useDpi = ClampDpi(size, dpi);
        var (w, h) = PixelSize(size, useDpi);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        var task = Worker.Run(() =>
        {
            lock (_gate) _active++;
            try
            {
                return RenderCore(index, useDpi, w, h);
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                    Monitor.PulseAll(_gate);
                }
            }
        }, linked.Token);
        // Observe our own outcome so a render nobody awaits never becomes an unobserved task exception; awaiters still see it.
        _ = task.ContinueWith(static (t, state) => { _ = t.Exception; ((CancellationTokenSource)state!).Dispose(); }, linked,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private SKBitmap RenderCore(int index, double useDpi, int w, int h)
    {
        {
            var options = new RenderOptions(Dpi: (int)Math.Max(1, Math.Round(useDpi)), Width: w, Height: h, WithAnnotations: true,
                BackgroundColor: SKColors.White);
#pragma warning disable CA1416 // PDFtoImage is annotated for platforms (mobile, browser) this project never runs on.
            return Conversion.ToImage(_bytes, index, options: options);
#pragma warning restore CA1416
        }
    }

    /// <summary>The DPI to render at: <paramref name="dpi"/>, lowered when the page would exceed <see cref="MaxPixels"/> or a side
    /// would exceed <see cref="MaxSide"/>.</summary>
    public static double ClampDpi(SizeD pagePoints, double dpi)
    {
        if (!double.IsFinite(dpi) || dpi <= 0) dpi = 72;
        if (Fits(pagePoints, dpi)) return dpi;
        double w = pagePoints.W * dpi / 72.0, h = pagePoints.H * dpi / 72.0;
        double lowered = dpi * Math.Min(Math.Sqrt((double)MaxPixels / (w * h)), MaxSide / Math.Max(w, h));
        // Rounding the sides up can overshoot by a few pixels; nudge down until it fits.
        while (!Fits(pagePoints, lowered)) lowered *= 0.99999;
        return lowered;
    }

    private static bool Fits(SizeD points, double dpi)
    {
        var (w, h) = PixelSize(points, dpi);
        return (long)w * h <= MaxPixels && w <= MaxSide && h <= MaxSide;
    }

    private static (int W, int H) PixelSize(SizeD points, double dpi) =>
        (Math.Max(1, (int)Math.Ceiling(points.W * dpi / 72.0 - 1e-6)), Math.Max(1, (int)Math.Ceiling(points.H * dpi / 72.0 - 1e-6)));

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
            // Cancelling completes the task at once, even while the item still waits behind other documents' work.
            var reg = ct.CanBeCanceled ? ct.Register(static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(), tcs) : default;
            Queue.Add(() =>
            {
                try
                {
                    if (tcs.Task.IsCompleted) return; // cancelled while queued
                    T result;
                    try { result = func(); }
                    catch (Exception ex) { tcs.TrySetException(ex); return; }
                    if (!tcs.TrySetResult(result) && result is IDisposable d) d.Dispose();
                }
                finally { reg.Dispose(); }
            });
            return tcs.Task;
        }
    }
}
