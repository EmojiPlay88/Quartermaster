using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Render;
using SkiaSharp;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Lazy counter thumbnails shared by the palette, the batch grid and the ledger/outliner lists (WinForms CounterCellImages and PaletteThumbs).
/// <see cref="Get"/> returns the bitmap when it is ready, else null, and queues a render on one worker thread (through the session's
/// <see cref="CounterImages"/>); when it is done <c>ready(pieceId)</c> is called on the UI thread. Requests named by <see cref="Prioritise"/>
/// (the rows on screen) go first, the rest in the order asked. Only pieces in <see cref="ModuleSession.View"/> are ever drawn: any other id
/// returns null and queues nothing. The cache keeps at most <see cref="Cap"/> bitmaps (visible ones may stay above it); a bitmap reported by
/// <see cref="Shown"/> is never disposed, only dropped and left to the GC. All members except the worker run on the UI thread.
/// </summary>
public sealed class CounterThumbnails : IDisposable
{
    sealed class Pending(Func<Bitmap?> render, long seq)
    {
        public Func<Bitmap?> Render { get; } = render;
        public long Seq { get; } = seq;
        public List<(string Id, Action<string>? Ready)> Waiting { get; } = new();
    }

    const string KeyedPrefix = "k\u001f", PiecePrefix = "p\u001f";
    readonly ModuleSession _s;
    readonly Dictionary<string, Bitmap> _cache = new(StringComparer.Ordinal);
    readonly LinkedList<string> _order = new();
    readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _keyOfId = new(StringComparer.Ordinal);
    readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    readonly object _gate = new();
    readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    HashSet<string> _visible = new(StringComparer.Ordinal);
    long _seq;
    int _generation;
    bool _disposed;
    bool _working;
    bool _hold;
    Task? _worker;

    public CounterThumbnails(ModuleSession s, int cap = 600)
    {
        _s = s;
        Cap = cap;
    }

    /// <summary>Most bitmaps kept (WinForms CounterCellImages: 600; the palette passes its own).</summary>
    public int Cap { get; set; }

    /// <summary>The bitmaps on screen now; eviction never disposes these.</summary>
    public Func<IEnumerable<object?>>? Shown { get; set; }

    /// <summary>True once disposed (tests).</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>Bitmaps cached (tests).</summary>
    internal int Count => _cache.Count;

    /// <summary>Renders waiting (tests).</summary>
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>The thread the worker last drew on (tests).</summary>
    internal int? LastWorkerThread { get; private set; }

    /// <summary>The piece's thumbnail at <paramref name="size"/>, or null while it is drawn (then <paramref name="ready"/> follows) or when
    /// the piece is not in the viewed state (never drawn). The piece is copied, never kept.</summary>
    public Bitmap? Get(string pieceId, PixelSize size, Action<string>? ready)
    {
        if (_disposed || size.Width < 1 || size.Height < 1) return null;
        if (_s.View?.Pieces.TryGetValue(pieceId, out var piece) != true || piece == null) return null;   // hidden or gone: no picture
        var sk = new SKSizeI(size.Width, size.Height);
        var key = PiecePrefix + CounterImages.Key(piece, sk);
        _keyOfId[pieceId] = key;
        if (_cache.TryGetValue(key, out var bmp)) return bmp;
        if (_s.Counters.TryGetCached(piece, sk) is { } img && ToBitmap(img) is { } hit) { Store(key, hit); return hit; }
        var copy = new PieceInstance { Id = piece.Id, TypeString = piece.TypeString, TraitStates = new List<string>(piece.TraitStates), Moved = piece.Moved };
        var counters = _s.Counters;
        Queue(key, pieceId, () => ToBitmap(counters.Render(copy, sk)), ready);   // the SKImage is CounterImages' cached one: not ours to dispose
        return null;
    }

    /// <summary>True when the piece's thumbnail at that size is in this cache (tests; queues nothing).</summary>
    internal bool Has(string pieceId, PixelSize size) =>
        _s.View?.Pieces.TryGetValue(pieceId, out var p) == true && p != null
        && _cache.ContainsKey(PiecePrefix + CounterImages.Key(p, new SKSizeI(size.Width, size.Height)));

    /// <summary>A thumbnail that is not a piece of the game (the palette's counters, which are module definitions): cached under
    /// <paramref name="key"/> and drawn by <paramref name="render"/> on the worker; <paramref name="ready"/> gets the key.</summary>
    internal Bitmap? GetKeyed(string key, Func<Bitmap?> render, Action<string>? ready)
    {
        if (_disposed) return null;
        var k = KeyedPrefix + key;
        _keyOfId[key] = k;
        if (_cache.TryGetValue(k, out var bmp)) return bmp;
        Queue(k, key, render, ready);
        return null;
    }

    /// <summary>The cached bitmap of a <see cref="GetKeyed"/> key, without queueing anything.</summary>
    internal Bitmap? Peek(string key) => _cache.GetValueOrDefault(KeyedPrefix + key);

    /// <summary>The ids (piece ids, or keys) on screen now: drawn first and kept in the cache past its cap.</summary>
    public void Prioritise(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids.Where(i => i != null), StringComparer.Ordinal);
        lock (_gate) _visible = set;
    }

    void Queue(string key, string id, Func<Bitmap?> render, Action<string>? ready)
    {
        if (_failed.Contains(key)) return;
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var p)) _pending[key] = p = new Pending(render, _seq++);
            if (!p.Waiting.Any(w => w.Id == id && ReferenceEquals(w.Ready, ready))) p.Waiting.Add((id, ready));
            StartLocked();
        }
    }

    /// <summary>Starts the worker when there is work and none is running (under <see cref="_gate"/>).</summary>
    void StartLocked()
    {
        if (_working || _hold || _pending.Count == 0 || _disposed) return;
        _working = true;
        _worker = Task.Run(Work);
    }

    /// <summary>Tests: while true no render starts, so the order of a batch of requests can be checked.</summary>
    internal bool Hold
    {
        get { lock (_gate) return _hold; }
        set { lock (_gate) { _hold = value; StartLocked(); } }
    }

    void Work()
    {
        while (true)
        {
            string key; Pending p; int gen;
            lock (_gate)
            {
                if (_pending.Count == 0 || _disposed || _s.Disposed || _hold) { if (!_hold) _pending.Clear(); _working = false; return; }
                gen = _generation;
                var visible = _visible;
                var next = _pending.MinBy(kv => (kv.Value.Waiting.Any(w => visible.Contains(w.Id)) ? 0 : 1, kv.Value.Seq));
                key = next.Key;
                p = next.Value;
                _pending.Remove(key);
            }
            LastWorkerThread = Environment.CurrentManagedThreadId;
            Bitmap? bmp;
            try { bmp = p.Render(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { bmp = null; }
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || gen != _generation) { bmp?.Dispose(); return; }
                // A piece that cannot be drawn is not asked again until cleared (WinForms CounterCellImages). Keyed entries (the palette) and
                // renders cut short by a closing session are tried again on the next request, as the old palette worker did on every Show.
                if (bmp == null) { if (!key.StartsWith(KeyedPrefix, StringComparison.Ordinal) && !_s.Disposed) _failed.Add(key); return; }
                Store(key, bmp);
                foreach (var (id, ready) in p.Waiting)
                {
                    try { ready?.Invoke(id); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("thumbnails: " + ex); }
                }
            });
        }
    }

    /// <summary>The image as an Avalonia bitmap (PNG round trip, as the palette always did); null when there is none.</summary>
    public static Bitmap? ToBitmap(SKImage? img)
    {
        if (img == null) return null;
        try
        {
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(data.ToArray());
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>Caches a thumbnail under its internal key, evicting the oldest entries that are not visible past <see cref="Cap"/>.</summary>
    void Store(string key, Bitmap bmp)
    {
        HashSet<object>? shown = null;
        if (_cache.Remove(key, out var old) && !ReferenceEquals(old, bmp)) Release(old, ref shown);
        if (_nodes.Remove(key, out var n)) _order.Remove(n);
        _cache[key] = bmp;
        _nodes[key] = _order.AddLast(key);
        if (_cache.Count <= Cap) return;
        HashSet<string> visible;
        lock (_gate) visible = _visible;
        var keep = new HashSet<string>(visible.Select(id => _keyOfId.GetValueOrDefault(id)).OfType<string>(), StringComparer.Ordinal) { key };
        for (var node = _order.First; node != null && _cache.Count > Cap;)
        {
            var next = node.Next;
            if (!keep.Contains(node.Value))
            {
                if (_cache.Remove(node.Value, out var gone)) Release(gone, ref shown);
                _nodes.Remove(node.Value);
                _order.Remove(node);
            }
            node = next;
        }
    }

    /// <summary>Tests and the palette: caches a bitmap under a <see cref="GetKeyed"/> key.</summary>
    internal void Remember(string key, Bitmap bmp) => Store(KeyedPrefix + key, bmp);

    void Release(Bitmap b, ref HashSet<object>? shown)
    {
        shown ??= (Shown?.Invoke() ?? Enumerable.Empty<object?>()).OfType<object>().ToHashSet(ReferenceEqualityComparer.Instance);
        if (!shown.Contains(b)) b.Dispose();
    }

    /// <summary>Drops the waiting renders (a render in flight is discarded when it finishes).</summary>
    public void CancelPending()
    {
        lock (_gate) { _pending.Clear(); _generation++; }
    }

    /// <summary>Cancels the waiting renders and waits briefly for one in flight, so the module archive can be closed safely.</summary>
    public void StopWorker()
    {
        Task? w;
        lock (_gate) { _pending.Clear(); _generation++; w = _worker; }
        try { w?.Wait(2000); }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException) { }
    }

    /// <summary>Forgets everything: waiting renders and every cached bitmap (those still shown are only dropped).</summary>
    public void Clear()
    {
        CancelPending();
        HashSet<object>? shown = null;
        foreach (var b in _cache.Values) Release(b, ref shown);
        _cache.Clear();
        _order.Clear();
        _nodes.Clear();
        _keyOfId.Clear();
        _failed.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopWorker();
        Clear();
        _disposed = true;
    }
}
