using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Where the fitted map extent sits in the overview control: <see cref="Scale"/> control pixels per map unit, the extent's top-left at (OffsetX, OffsetY) pixels; (OriginX, OriginY) is the extent's top-left in map units.</summary>
public sealed record OverviewFit(double Scale, double OffsetX, double OffsetY, double OriginX = 0, double OriginY = 0);

/// <summary>UI-free geometry of the overview mini-map.</summary>
public static class OverviewGeometry
{
    public static OverviewFit Fit(SizeD extent, SizeD control, double margin = 8) => Fit(0, 0, extent, control, margin);

    public static OverviewFit Fit(RectD extent, SizeD control, double margin = 8) => Fit(extent.X, extent.Y, new SizeD(extent.W, extent.H), control, margin);

    static OverviewFit Fit(double ox, double oy, SizeD e, SizeD c, double margin)
    {
        var s = Math.Min(Math.Max(c.W - margin, 1) / e.W, Math.Max(c.H - margin, 1) / e.H);
        return new OverviewFit(s, (c.W - e.W * s) / 2, (c.H - e.H * s) / 2, ox, oy);
    }

    /// <summary>The map point under a control point, clamped to the extent.</summary>
    public static PointD ToMap(PointD controlPoint, OverviewFit fit, SizeD extent)
    {
        var x = fit.OriginX + (controlPoint.X - fit.OffsetX) / fit.Scale;
        var y = fit.OriginY + (controlPoint.Y - fit.OffsetY) / fit.Scale;
        return new PointD(Math.Clamp(x, fit.OriginX, fit.OriginX + extent.W), Math.Clamp(y, fit.OriginY, fit.OriginY + extent.H));
    }

    /// <summary>A map rectangle in control pixels.</summary>
    public static RectD ViewportRect(RectD viewportMap, OverviewFit fit) =>
        new(fit.OffsetX + (viewportMap.X - fit.OriginX) * fit.Scale, fit.OffsetY + (viewportMap.Y - fit.OriginY) * fit.Scale,
            viewportMap.W * fit.Scale, viewportMap.H * fit.Scale);

    /// <summary>The pyramid level the overview draws: the coarsest (at most 1024 px on its long side).</summary>
    public static int TileLevel(TileSet ts) => ts.Levels;
}
