using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>
/// Where the big counter hover preview goes, in screen pixels (shared by WinForms CounterPreviewTip and the Avalonia CounterPreview):
/// 16 px below and right of the cursor; when it would run off the right of the working area it flips to the left of the cursor, and when it
/// would run off the bottom it is moved up to end at the bottom. Never left of or above the working area.
/// </summary>
public static class PreviewPlacement
{
    public const int Offset = 16;

    /// <summary>The preview's top-left for a preview of <paramref name="preview"/> pixels with the cursor at <paramref name="cursor"/>.</summary>
    public static SKPointI Place(SKPointI cursor, SKSizeI preview, SKRectI screenWorkingArea)
    {
        var area = screenWorkingArea;
        var x = cursor.X + Offset;
        var y = cursor.Y + Offset;
        if (x + preview.Width > area.Right) x = Math.Max(area.Left, cursor.X - Offset - preview.Width);
        if (y + preview.Height > area.Bottom) y = Math.Max(area.Top, area.Bottom - preview.Height);
        return new SKPointI(x, y);
    }
}
