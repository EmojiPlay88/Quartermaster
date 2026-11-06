using Svg.Skia;
using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>Format sniffing and decoding shared by <see cref="ImageSource"/> and <see cref="BoardTiler"/>.</summary>
internal static class ImageDecoder
{
    public static bool LooksLikeSvg(byte[] d)
    {
        var i = 0;
        if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) i = 3;
        while (i < d.Length && d[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        if (i >= d.Length || d[i] != (byte)'<') return false;
        var head = System.Text.Encoding.UTF8.GetString(d, i, Math.Min(d.Length - i, 2048));
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Loads an SVG picture and its natural size, or null when it cannot be parsed.</summary>
    public static (SKPicture Picture, SKSize Size)? LoadSvg(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var svg = new SKSvg();
            var pic = svg.Load(ms);
            if (pic == null) return null;
            var r = pic.CullRect;
            return r.Width > 0 && r.Height > 0 ? (pic, new SKSize(r.Width, r.Height)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static SKBitmap? RenderSvg(SKPicture pic, SKSize size, float scale)
    {
        var w = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
        var h = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        var bmp = new SKBitmap(w, h);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        c.Scale(w / size.Width, h / size.Height);
        c.Translate(-pic.CullRect.Left, -pic.CullRect.Top);
        c.DrawPicture(pic);
        return bmp;
    }

    /// <summary>Decodes any supported raster or SVG (scale 1) to a bitmap; null on failure.</summary>
    public static SKBitmap? Decode(byte[] data)
    {
        try
        {
            if (LooksLikeSvg(data))
            {
                var svg = LoadSvg(data);
                if (svg is not { } s) return null;
                using (s.Picture) return RenderSvg(s.Picture, s.Size, 1f);
            }
            return SKBitmap.Decode(data);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static SKImage Placeholder()
    {
        using var bmp = new SKBitmap(64, 64);
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                bmp.SetPixel(x, y, ((x / 8) + (y / 8)) % 2 == 0 ? new SKColor(255, 0, 255) : SKColors.Black);
        return SKImage.FromBitmap(bmp);
    }
}
