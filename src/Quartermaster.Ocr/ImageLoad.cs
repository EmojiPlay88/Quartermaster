using System.Drawing;
using System.Drawing.Imaging;

namespace Quartermaster.Ocr;

/// <summary>Loads scans/photos for the scan pane: EXIF orientation applied, multi-page TIFF first page only.</summary>
public static class ImageLoad
{
    private const int OrientationTag = 0x0112;

    public static Bitmap Load(string path) => Load(path, out _);

    /// <summary>Loads the first frame; <paramref name="pageCount"/> is the number of pages (TIFF) so the UI can note it.</summary>
    public static Bitmap Load(string path, out int pageCount)
    {
        // Read into memory so the file is not kept locked and the stream outlives Image.
        using var ms = new MemoryStream(File.ReadAllBytes(path));
        using var img = Image.FromStream(ms);
        pageCount = 1;
        if (img.FrameDimensionsList.Contains(FrameDimension.Page.Guid))
        {
            pageCount = Math.Max(1, img.GetFrameCount(FrameDimension.Page));
            img.SelectActiveFrame(FrameDimension.Page, 0);
        }
        var rotate = OrientationOf(img);
        var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
        bmp.SetResolution(img.HorizontalResolution, img.VerticalResolution);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.DrawImage(img, 0, 0, img.Width, img.Height);
        }
        if (rotate != RotateFlipType.RotateNoneFlipNone) bmp.RotateFlip(rotate);
        return bmp;
    }

    internal static RotateFlipType OrientationOf(Image img)
    {
        if (!img.PropertyIdList.Contains(OrientationTag)) return RotateFlipType.RotateNoneFlipNone;
        var item = img.GetPropertyItem(OrientationTag);
        if (item?.Value is not { Length: > 0 } v) return RotateFlipType.RotateNoneFlipNone;
        int o = v.Length >= 2 ? BitConverter.ToUInt16(v, 0) : v[0];
        return ForOrientation(o);
    }

    internal static RotateFlipType ForOrientation(int orientation) => orientation switch
    {
        2 => RotateFlipType.RotateNoneFlipX,
        3 => RotateFlipType.Rotate180FlipNone,
        4 => RotateFlipType.Rotate180FlipX,
        5 => RotateFlipType.Rotate90FlipX,
        6 => RotateFlipType.Rotate90FlipNone,
        7 => RotateFlipType.Rotate270FlipX,
        8 => RotateFlipType.Rotate270FlipNone,
        _ => RotateFlipType.RotateNoneFlipNone,
    };
}
