using System.Drawing;

namespace Quartermaster.Render;

/// <summary>Pure placement rule for restoring a saved window rectangle onto whatever monitors exist now.</summary>
/// <summary>A monitor's working area (physical pixels) and its DPI.</summary>
public readonly record struct ScreenInfo(Rectangle WorkingArea, int Dpi);

public static class ScreenFit
{
    public const int MinVisible = 64;
    /// <summary>The title bar strip that must stay reachable so a window can always be dragged back.</summary>
    public const int TitleHeight = 32;
    public static readonly Size DefaultSize = new(800, 600);

    public static Rectangle Fit(Rectangle saved, IReadOnlyList<Rectangle> workingAreas, Rectangle primary)
    {
        var areas = workingAreas.Count > 0 ? workingAreas : [primary];
        if (saved.Width <= 0 || saved.Height <= 0) return Centre(DefaultSize, primary);

        Rectangle? host = null;
        long best = 0;
        foreach (var a in areas)
        {
            var i = Rectangle.Intersect(saved, a);
            if (i.Width < MinVisible || i.Height < MinVisible) continue;
            var area = (long)i.Width * i.Height;
            if (area > best) { best = area; host = a; }
        }
        if (host is not { } h) return Centre(saved.Size, primary);

        var r = saved;
        if (r.Width > h.Width) { r.Width = h.Width; r.X = h.X; }
        if (r.Height > h.Height) { r.Height = h.Height; r.Y = h.Y; }
        r.Y = Math.Clamp(r.Y, h.Top, Math.Max(h.Top, h.Bottom - TitleHeight)); // title strip stays inside the work area
        return r;
    }

    /// <summary>
    /// Restores bounds saved in physical pixels on a monitor of <paramref name="savedDpi"/> (0 = unknown). The target monitor is
    /// the one the saved rectangle overlaps most; when its DPI differs from the saved one the size is rescaled.
    /// </summary>
    public static Rectangle Restore(Rectangle saved, int savedDpi, IReadOnlyList<ScreenInfo> screens, ScreenInfo primary)
    {
        var list = screens.Count > 0 ? screens : [primary];
        ScreenInfo? target = null;
        long best = 0;
        foreach (var sc in list)
        {
            var i = Rectangle.Intersect(saved, sc.WorkingArea);
            var area = (long)i.Width * i.Height;
            if (area > best) { best = area; target = sc; }
        }
        var t = target ?? primary;
        var r = saved;
        if (savedDpi > 0 && t.Dpi > 0 && savedDpi != t.Dpi && r.Width > 0 && r.Height > 0)
            r.Size = new Size((int)Math.Round(r.Width * (double)t.Dpi / savedDpi), (int)Math.Round(r.Height * (double)t.Dpi / savedDpi));
        return Fit(r, list.Select(x => x.WorkingArea).ToArray(), primary.WorkingArea);
    }

    static Rectangle Centre(Size size, Rectangle area)
    {
        var w = Math.Min(size.Width, area.Width);
        var h = Math.Min(size.Height, area.Height);
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }
}
