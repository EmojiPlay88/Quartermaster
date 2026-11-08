using Quartermaster.Core;
using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>
/// Cached counter images (a piece as the map draws it, fitted into a box) for ledger rows, the batch grid, the outliner and
/// hover previews. <see cref="Render"/> is safe to call from worker threads.
/// </summary>
public sealed class CounterImages(ImageSource images, long maxBytes = 64L << 20)
{
    public static readonly SKSizeI RowSize = new(64, 48);

    private readonly PieceRenderer _pieces = new(images);
    private readonly LruImageCache _cache = new(maxBytes);
    private readonly object _gate = new();
    private int _generation;

    /// <summary>Cache key: piece type string, trait states, moved flag and target size.</summary>
    public static string Key(PieceInstance p, SKSizeI size) =>
        p.TypeString + "\u001f" + string.Join("\u001e", p.TraitStates) + "\u001f" + (p.Moved ? "m" : "") + "\u001f" + size.Width + "x" + size.Height;

    /// <summary>Hover preview size: long side max(200, 2 x native long side), capped to 80% of the screen on both axes, aspect kept.</summary>
    public static SKSizeI PreviewSize(SKRect nativeBounds, SKSizeI screen)
    {
        double w = Math.Max(1, nativeBounds.Width), h = Math.Max(1, nativeBounds.Height);
        var nativeLong = Math.Max(w, h);
        var target = Math.Max(200.0, 2 * nativeLong);
        var f = target / nativeLong;
        var pw = w * f;
        var ph = h * f;
        if (screen.Width > 0) { var cap = 0.8 * screen.Width; if (pw > cap) { ph *= cap / pw; pw = cap; } }
        if (screen.Height > 0) { var cap = 0.8 * screen.Height; if (ph > cap) { pw *= cap / ph; ph = cap; } }
        return new SKSizeI(Math.Max(1, (int)Math.Round(pw)), Math.Max(1, (int)Math.Round(ph)));
    }

    public SKImage? TryGetCached(PieceInstance p, SKSizeI size) => _cache.TryGet(Key(p, size));

    /// <summary>The counter fitted into <paramref name="size"/> (transparent background), cached; null when it cannot be drawn.</summary>
    public SKImage? Render(PieceInstance p, SKSizeI size)
    {
        if (p == null || size.Width < 1 || size.Height < 1) return null;
        try
        {
            var key = Key(p, size);
            if (_cache.TryGet(key) is { } hit) return hit;
            lock (_gate)
            {
                if (_cache.TryGet(key) is { } again) return again;
                var gen = _generation;
                var type = _pieces.Types.Get(p.TypeString);
                var b = PieceGeometry.LocalBounds(type, p, images);
                if (b.Width < 1 || b.Height < 1) b = SKRect.Create(-16, -16, 32, 32);
                var scale = (float)Math.Min((size.Width - 4) / (double)b.Width, (size.Height - 4) / (double)b.Height);
                if (size == RowSize) scale = Math.Min(1.5f, scale);
                if (scale <= 0) return null;
                // Draw at the target scale rather than upscaling a 1x sprite, so large previews stay sharp.
                using var sprite = _pieces.RenderSprite(p, scale, PieceDrawFlags.None);
                var src = SKRect.Create(sprite.Half + b.Left * scale, sprite.Half + b.Top * scale, b.Width * scale, b.Height * scale);
                var dst = SKRect.Create((size.Width - src.Width) / 2f, (size.Height - src.Height) / 2f, src.Width, src.Height);
                using var surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height));
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawImage(sprite.Image, src, dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                var img = surface.Snapshot();
                if (gen == _generation) _cache.Put(key, img);
                return img;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public void Clear()
    {
        lock (_gate) { _generation++; _cache.Clear(); }
    }
}
