using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>Thread-safe LRU of images bounded by estimated pixel bytes (4 per pixel).</summary>
internal sealed class LruImageCache(long capBytes)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, SKImage Image, long Bytes)>> _map = new();
    private readonly LinkedList<(string Key, SKImage Image, long Bytes)> _order = new();
    private long _bytes;

    public long Bytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _map.Count; } }

    public SKImage? TryGet(string key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var n)) return null;
            _order.Remove(n);
            _order.AddFirst(n);
            return n.Value.Image;
        }
    }

    public void Put(string key, SKImage image)
    {
        var bytes = (long)image.Width * image.Height * 4;
        lock (_gate)
        {
            if (_map.Remove(key, out var old)) { _order.Remove(old); _bytes -= old.Value.Bytes; }
            _map[key] = _order.AddFirst((key, image, bytes));
            _bytes += bytes;
            // Evicted images are left to the finalizer: a caller may still be drawing one.
            while (_bytes > capBytes && _order.Count > 1)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
            }
        }
    }
}
