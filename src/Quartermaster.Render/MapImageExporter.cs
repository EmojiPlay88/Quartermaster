using Quartermaster.Core;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>Renders a whole map to an offscreen bitmap through <see cref="MapRenderer.Draw"/>, in horizontal strips so progress and cancel work.</summary>
public static class MapImageExporter
{
    public const int DefaultMaxLongSide = 8192;

    /// <summary>The scale actually used: <paramref name="zoom"/> reduced so neither side of the map exceeds <paramref name="maxLongSide"/>.</summary>
    public static double EffectiveZoom(MapDef map, double zoom, int maxLongSide = DefaultMaxLongSide)
    {
        var e = FitView.Extent(map);
        var z = double.IsFinite(zoom) && zoom > 0 ? zoom : 1;
        return Math.Min(z, Math.Max(1, maxLongSide) / Math.Max(e.W, e.H));
    }

    /// <summary>Output size in pixels for the effective zoom (each side at least 1).</summary>
    public static (int Width, int Height) OutputSize(MapDef map, double zoom, int maxLongSide = DefaultMaxLongSide)
    {
        var e = FitView.Extent(map);
        var z = EffectiveZoom(map, zoom, maxLongSide);
        return (Math.Max(1, (int)Math.Round(e.W * z)), Math.Max(1, (int)Math.Round(e.H * z)));
    }

    /// <summary>Draws the whole map. The caller owns (and must dispose) the bitmap. Pieces are drawn without selection or hover.</summary>
    public static SKBitmap Render(MapDef map, IReadOnlyDictionary<string, TileSet> tiles, GameState s, ModuleModel model, MapRenderer r, double zoom,
        int maxLongSide = DefaultMaxLongSide, bool showGrid = false, IProgress<double>? progress = null, CancellationToken ct = default, int stripHeight = 1024)
    {
        var e = FitView.Extent(map);
        var z = EffectiveZoom(map, zoom, maxLongSide);
        if (z < Viewport.MinZoom) throw new NotSupportedException("The map is too large to export at this size.");
        var (w, h) = OutputSize(map, zoom, maxLongSide);
        var bmp = new SKBitmap(w, h);
        try
        {
            using var canvas = new SKCanvas(bmp);
            var opt = new MapDrawOptions(showGrid, new HashSet<string>(), null);
            var vp = new Viewport { Zoom = (float)z };
            var strip = Math.Max(1, stripHeight);
            for (var y = 0; y < h; y += strip)
            {
                ct.ThrowIfCancellationRequested();
                var sh = Math.Min(strip, h - y);
                vp.Pan = new SKPoint((float)e.X, (float)(e.Y + y / (double)vp.Zoom));
                var save = canvas.Save();
                canvas.ClipRect(new SKRect(0, y, w, y + sh));
                canvas.Translate(0, y);
                r.Draw(canvas, map, tiles, s, model, vp, new SKSize(w, sh), opt);
                canvas.RestoreToCount(save);
                progress?.Report((y + sh) / (double)h);
            }
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    /// <summary>Encodes as PNG into a temp file beside <paramref name="path"/> and moves it over the target. Throws on failure; never leaves the temp file behind.</summary>
    public static void SavePng(SKBitmap bmp, string path)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var img = SKImage.FromBitmap(bmp))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.Create(tmp))
                data.SaveTo(fs);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }
}
