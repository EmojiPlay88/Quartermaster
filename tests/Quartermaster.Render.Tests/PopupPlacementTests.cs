using System.Drawing;
using Quartermaster.Render;
using Xunit;

namespace Quartermaster.Render.Tests;

public class PopupPlacementTests
{
    static readonly Rectangle Area = new(0, 0, 1920, 1040);

    static void AssertClear(Point cursor, Size size, Point at)
    {
        Assert.False(new Rectangle(at, size).Contains(cursor), $"popup {new Rectangle(at, size)} covers the cursor {cursor}");
    }

    [Fact]
    public void Middle_of_the_screen_goes_below_right()
    {
        var at = PopupPlacement.Place(new Point(500, 400), new Size(200, 150), Area);
        Assert.Equal(new Point(516, 416), at);
    }

    [Fact]
    public void Right_edge_flips_to_the_left_of_the_cursor()
    {
        var c = new Point(1850, 400);
        var s = new Size(200, 150);
        var at = PopupPlacement.Place(c, s, Area);
        Assert.Equal(new Point(1850 - 16 - 200, 416), at);
        AssertClear(c, s, at);
    }

    [Fact]
    public void Bottom_edge_flips_above_the_cursor()
    {
        var c = new Point(500, 1000);
        var s = new Size(200, 150);
        var at = PopupPlacement.Place(c, s, Area);
        Assert.Equal(new Point(516, 1000 - 16 - 150), at);
        AssertClear(c, s, at);
    }

    [Fact]
    public void Bottom_right_corner_flips_both_ways()
    {
        var c = new Point(1900, 1030);
        var s = new Size(300, 200);
        var at = PopupPlacement.Place(c, s, Area);
        Assert.Equal(new Point(1900 - 16 - 300, 1030 - 16 - 200), at);
        AssertClear(c, s, at);
    }

    [Fact]
    public void Wider_than_either_side_stays_clear_vertically()
    {
        var c = new Point(960, 300);
        var s = new Size(1800, 200);
        var at = PopupPlacement.Place(c, s, Area);
        Assert.Equal(316, at.Y);
        Assert.True(at.X >= Area.Left && at.X + s.Width <= Area.Right);
        AssertClear(c, s, at);
    }

    [Fact]
    public void Too_big_on_both_axes_never_covers_the_cursor()
    {
        var c = new Point(960, 900);
        var s = new Size(1800, 1000);
        var at = PopupPlacement.Place(c, s, Area);
        Assert.Equal(900 - 16 - 1000, at.Y); // more room above
        AssertClear(c, s, at);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1919, 0)]
    [InlineData(0, 1039)]
    [InlineData(1919, 1039)]
    [InlineData(1700, 900)]
    [InlineData(1750, 20)]
    public void Edges_and_corners_never_cover_the_cursor(int x, int y)
    {
        var c = new Point(x, y);
        foreach (var s in new[] { new Size(120, 24), new Size(520, 400), new Size(520, 1000) })
            AssertClear(c, s, PopupPlacement.Place(c, s, Area));
    }

    [Fact]
    public void Offset_monitor_area_is_respected()
    {
        var area = new Rectangle(1920, 0, 1280, 1024);
        var c = new Point(3150, 50);
        var s = new Size(200, 100);
        var at = PopupPlacement.Place(c, s, area);
        Assert.Equal(new Point(3150 - 16 - 200, 66), at);
    }
}
