using System.Drawing;

namespace Quartermaster.Render;

/// <summary>Pure placement rule for restoring a saved window rectangle onto whatever monitors exist now.</summary>
public static class ScreenFit
{
    public const int MinVisible = 64;
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
        return r;
    }

    static Rectangle Centre(Size size, Rectangle area)
    {
        var w = Math.Min(size.Width, area.Width);
        var h = Math.Min(size.Height, area.Height);
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }
}
