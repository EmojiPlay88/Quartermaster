using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class ChipLayoutTests
{
    [Fact]
    public void ChipsReadLeftToRightInInputOrderAndAreRightAligned()
    {
        var r = ChipLayout.Place(new[] { 50, 60, 70 }, 30, 1000, 4, 100);
        Assert.Equal(0, r.Overflow);
        Assert.Equal(new[] { 812, 866, 930 }, r.Xs);
        Assert.Equal(1000, r.Xs[2] + 70);
        Assert.Equal(-1, r.OverflowX);
    }

    [Fact]
    public void OverflowChipIsRightmostAndBannerKeepsItsMinimum()
    {
        var r = ChipLayout.Place(new[] { 100, 100, 100, 100 }, 30, 400, 4, 150);
        Assert.True(r.Overflow > 0);
        Assert.Equal(4 - r.Overflow, r.Xs.Count);
        Assert.True(r.Xs.Count > 0);
        Assert.True(r.OverflowX > r.Xs[^1]);
        Assert.Equal(400, r.OverflowX + 30);
        Assert.True(r.BannerRight >= 150);
    }

    [Fact]
    public void NothingFitsGivesOnlyTheOverflowChip()
    {
        var r = ChipLayout.Place(new[] { 500 }, 30, 300, 4, 200);
        Assert.Empty(r.Xs);
        Assert.Equal(1, r.Overflow);
        Assert.Equal(270, r.OverflowX);
    }
}
