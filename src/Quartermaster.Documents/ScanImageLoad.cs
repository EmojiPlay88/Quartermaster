using BitMiracle.LibTiff.Classic;
using SkiaSharp;

namespace Quartermaster.Documents;

/// <summary>Loads scans and photos for the scan pane: EXIF orientation applied, multi-page TIFF first page only.</summary>
public static class ScanImageLoad
{
    public const string TooLargeMessage = "This image is too large to scan (over 100 megapixels).";
    public const long MaxPixels = 100_000_000;

    public static SKBitmap Load(string path) => Load(path, out _);

    /// <summary>Loads the first frame; <paramref name="pageCount"/> is the number of pages (TIFF) so the UI can note it.</summary>
    /// <exception cref="InvalidDataException">The image is over 100 megapixels or cannot be decoded.</exception>
    public static SKBitmap Load(string path, out int pageCount)
    {
        // Read into memory so the file is not kept locked.
        var bytes = File.ReadAllBytes(path);
        if (IsTiff(bytes)) return LoadTiff(bytes, out pageCount);
        pageCount = 1;
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("This file is not a readable image.");
        var info = codec.Info;
        // The header alone gave the size: refuse before any full decode.
        if ((long)info.Width * info.Height > MaxPixels) throw new InvalidDataException(TooLargeMessage);
        var origin = codec.EncodedOrigin;
        var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException("This image could not be decoded (" + result + ").");
            var upright = origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default ? decoded : Orient(decoded, origin);
            if (!ReferenceEquals(upright, decoded)) decoded.Dispose();
            // Transparent pixels become white (the WinForms loader cleared to white), not black.
            return info.AlphaType == SKAlphaType.Opaque ? upright : OntoWhite(upright);
        }
        catch
        {
            decoded.Dispose();
            throw;
        }
    }

    private static bool IsTiff(byte[] b) => b.Length >= 4 &&
        ((b[0] == 'I' && b[1] == 'I' && b[2] == 42 && b[3] == 0) || (b[0] == 'M' && b[1] == 'M' && b[2] == 0 && b[3] == 42));

    private static SKBitmap LoadTiff(byte[] bytes, out int pageCount)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var tiff = Tiff.ClientOpen("scan", "r", ms, new TiffStream()) ?? throw new InvalidDataException("This file is not a readable image.");
        pageCount = Math.Max(1, (int)tiff.NumberOfDirectories());
        tiff.SetDirectory(0);
        int w = tiff.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() ?? 0;
        int h = tiff.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() ?? 0;
        if (w <= 0 || h <= 0) throw new InvalidDataException("This file is not a readable image.");
        if ((long)w * h > MaxPixels) throw new InvalidDataException(TooLargeMessage);
        // The Orientation tag (1-8). Out-of-range or missing means 1.
        int orientation = tiff.GetFieldDefaulted(TiffTag.ORIENTATION)?[0].ToInt() ?? 1;
        if (orientation is < 1 or > 8) orientation = 1;
        // Transient peak: the file bytes, libtiff's strip buffer, this 4-bytes-per-pixel raster and then the destination bitmap (the raster
        // is garbage once the copy ends), so a 100 MP scan briefly needs about 1 GB.
        var raster = new int[w * h];
        // Request the file's own orientation so the library does no flipping at all: the raster is in stored order, and the Orientation
        // tag is applied below while copying (the library never transposes, so 5-8 would otherwise come out sideways).
        if (!tiff.ReadRGBAImageOriented(w, h, raster, (BitMiracle.LibTiff.Classic.Orientation)orientation))
            throw new InvalidDataException("This TIFF could not be decoded.");
        bool swap = orientation >= 5;
        var bmp = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            CopyOriented(raster, w, h, orientation, bmp);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Copies the packed ABGR raster (stored order, w x h) into <paramref name="bmp"/> (Bgra8888) upright for a TIFF/EXIF orientation 1-8,
    /// compositing any transparency onto white.
    /// </summary>
    private static unsafe void CopyOriented(int[] raster, int w, int h, int orientation, SKBitmap bmp)
    {
        int dw = bmp.RowBytes / 4; // the stride in pixels
        var dst = (uint*)bmp.GetPixels();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                uint v = (uint)raster[y * w + x];
                uint r = v & 0xFF, g = (v >> 8) & 0xFF, b = (v >> 16) & 0xFF, a = (v >> 24) & 0xFF;
                if (a != 255) // composite onto white
                {
                    r = (r * a + 255 * (255 - a)) / 255; g = (g * a + 255 * (255 - a)) / 255; b = (b * a + 255 * (255 - a)) / 255;
                }
                int dx, dy;
                switch (orientation)
                {
                    case 2: dx = w - 1 - x; dy = y; break;
                    case 3: dx = w - 1 - x; dy = h - 1 - y; break;
                    case 4: dx = x; dy = h - 1 - y; break;
                    case 5: dx = y; dy = x; break;                       // transpose
                    case 6: dx = h - 1 - y; dy = x; break;               // 90 clockwise
                    case 7: dx = h - 1 - y; dy = w - 1 - x; break;       // transverse
                    case 8: dx = y; dy = w - 1 - x; break;               // 270 clockwise
                    default: dx = x; dy = y; break;
                }
                dst[dy * dw + dx] = b | (g << 8) | (r << 16) | 0xFF000000u;
            }
        }
    }

    /// <summary>Draws <paramref name="src"/> over an opaque white bitmap; disposes the source.</summary>
    private static SKBitmap OntoWhite(SKBitmap src)
    {
        var dst = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        try
        {
            using (var canvas = new SKCanvas(dst))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(src, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
            }
            src.Dispose();
            return dst;
        }
        catch
        {
            dst.Dispose();
            src.Dispose();
            throw;
        }
    }

    /// <summary>Returns the upright version of <paramref name="src"/> for an EXIF/codec origin (the source is left to the caller).</summary>
    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        int w = src.Width, h = src.Height;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            var s = src.GetPixelSpan();
            var d = dst.GetPixelSpan();
            int dw = dst.Width;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int dx, dy;
                    switch (origin)
                    {
                        case SKEncodedOrigin.TopRight: dx = w - 1 - x; dy = y; break;
                        case SKEncodedOrigin.BottomRight: dx = w - 1 - x; dy = h - 1 - y; break;
                        case SKEncodedOrigin.BottomLeft: dx = x; dy = h - 1 - y; break;
                        case SKEncodedOrigin.LeftTop: dx = y; dy = x; break;                       // transpose
                        case SKEncodedOrigin.RightTop: dx = h - 1 - y; dy = x; break;             // 90 clockwise
                        case SKEncodedOrigin.RightBottom: dx = h - 1 - y; dy = w - 1 - x; break;  // transverse
                        case SKEncodedOrigin.LeftBottom: dx = y; dy = w - 1 - x; break;           // 270 clockwise
                        default: dx = x; dy = y; break;
                    }
                    int so = (y * w + x) * 4, dO = (dy * dw + dx) * 4;
                    d[dO] = s[so]; d[dO + 1] = s[so + 1]; d[dO + 2] = s[so + 2]; d[dO + 3] = s[so + 3];
                }
            }
            return dst;
        }
        catch
        {
            dst.Dispose();
            throw;
        }
    }
}
