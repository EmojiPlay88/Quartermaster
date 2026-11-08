using Quartermaster.Core;
using Quartermaster.Render;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Quartermaster.App;

/// <summary>
/// Lazy counter bitmaps for a virtual grid: <see cref="Get"/> returns the bitmap when it is ready (else null, a placeholder is drawn) and queues
/// a render on a worker; the rows nearest the visible range go first. When a render finishes the owner is asked to repaint. All members except the
/// worker run on the UI thread; the returned bitmaps belong to this object.
/// </summary>
sealed class CounterCellImages : IDisposable
{
    const int BitmapCap = 600;
    readonly CounterImages _src;
    readonly SKSizeI _size;
    readonly Control _owner;
    readonly Action _repaint;
    readonly Dictionary<string, Bitmap> _ready = new(StringComparer.Ordinal);
    readonly object _gate = new();
    readonly Dictionary<string, (PieceInstance Piece, int Row)> _pending = new(StringComparer.Ordinal);
    int _first, _last, _generation;
    bool _working, _disposed;

    public CounterCellImages(CounterImages src, Size size, Control owner, Action repaint)
    {
        _src = src; _size = new SKSizeI(size.Width, size.Height); _owner = owner; _repaint = repaint;
    }

    /// <summary>Tells the queue which rows are on screen so they are rendered first.</summary>
    public void SetVisible(int first, int last)
    {
        lock (_gate) { _first = first; _last = Math.Max(first, last); }
    }

    /// <summary>Forget everything (the game or view changed): pending work is dropped, bitmaps freed.</summary>
    public void Reset()
    {
        lock (_gate) { _pending.Clear(); _generation++; }
        foreach (var b in _ready.Values) b.Dispose();
        _ready.Clear();
    }

    /// <summary>The bitmap for the piece, or null while it is still being drawn (or cannot be). The piece is copied, never kept.</summary>
    public Bitmap? Get(PieceInstance piece, int row)
    {
        if (_disposed) return null;
        var key = CounterImages.Key(piece, _size);
        if (_ready.TryGetValue(key, out var bmp)) return bmp;
        if (_src.TryGetCached(piece, _size) is { } img) return Store(key, img);
        lock (_gate)
        {
            _pending[key] = (new PieceInstance { Id = piece.Id, TypeString = piece.TypeString, TraitStates = new List<string>(piece.TraitStates), Moved = piece.Moved }, row);
            if (_working) return null;
            _working = true;
        }
        Task.Run(Work);
        return null;
    }

    Bitmap? Store(string key, SKImage img)
    {
        try
        {
            if (_ready.Count >= BitmapCap) { foreach (var b in _ready.Values) b.Dispose(); _ready.Clear(); }
            return _ready[key] = img.ToBitmap();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    void Work()
    {
        while (true)
        {
            string key; PieceInstance piece; int gen;
            lock (_gate)
            {
                if (_pending.Count == 0 || _disposed) { _working = false; return; }
                gen = _generation;
                int Dist(int r) => r < _first ? _first - r : r > _last ? r - _last : 0;
                key = _pending.MinBy(kv => Dist(kv.Value.Row)).Key;
                piece = _pending[key].Piece;
                _pending.Remove(key);
            }
            var img = _src.Render(piece, _size);   // cached and shared in CounterImages: never disposed here
            if (img == null) continue;
            try
            {
                if (_owner.IsHandleCreated && !_owner.IsDisposed)
                    _owner.BeginInvoke(() =>
                    {
                        if (_disposed || gen != _generation) return;
                        Store(key, img);
                        _repaint();
                    });
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate) _pending.Clear();
        foreach (var b in _ready.Values) b.Dispose();
        _ready.Clear();
    }
}
