using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render.Tests;

public class GeometryTests
{
    [Fact]
    public void Viewport_ZoomAt_KeepsAnchor()
    {
        var vp = new Viewport { Pan = new SKPoint(100, 50) };
        var anchor = new SKPoint(320, 200);
        var before = vp.ToMap(anchor);
        vp.ZoomAt(anchor, 1.7f);
        var after = vp.ToMap(anchor);
        Assert.Equal(before.X, after.X, 1e-3);
        Assert.Equal(before.Y, after.Y, 1e-3);
        Assert.Equal(1.7f, vp.Zoom, 1e-4f);
    }

    [Fact]
    public void Viewport_ClampsZoom()
    {
        var vp = new Viewport();
        vp.ZoomAt(new SKPoint(0, 0), 100f);
        Assert.Equal(4f, vp.Zoom);
        vp.ZoomAt(new SKPoint(0, 0), 0.0001f);
        Assert.Equal(0.25f, vp.Zoom);
        vp.Zoom = 99;
        Assert.Equal(4f, vp.Zoom);
    }

    [Fact]
    public void Viewport_ToScreenToMap_RoundTripsAndVisibleRect()
    {
        var vp = new Viewport { Zoom = 2f, Pan = new SKPoint(10, 20) };
        var s = vp.ToScreen(new PointD(15, 30));
        Assert.Equal(10f, s.X); Assert.Equal(20f, s.Y);
        Assert.Equal(new PointD(15, 30), vp.ToMap(s));
        Assert.Equal(new RectD(10, 20, 50, 25), vp.VisibleMapRect(new SKSize(100, 50)));
    }

    [Fact]
    public void StackLayout_CollapsedAndExpanded()
    {
        var m = new StackMetricsDef(6, 18, 2, 4, false);
        Assert.Equal(new[] { new SKPoint(0, 0), new SKPoint(2, -4), new SKPoint(4, -8) }, StackLayout.Offsets(3, false, m));
        Assert.Equal(new[] { new SKPoint(0, 0), new SKPoint(6, -18), new SKPoint(12, -36) }, StackLayout.Offsets(3, true, m));
        Assert.All(StackLayout.Offsets(3, true, m with { Disabled = true }), p => Assert.Equal(new SKPoint(0, 0), p));
        Assert.Empty(StackLayout.Offsets(0, true, m));
    }
}
