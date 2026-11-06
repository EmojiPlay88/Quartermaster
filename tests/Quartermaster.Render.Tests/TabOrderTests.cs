using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class TabOrderTests
{
    [Theory]
    [InlineData(3, 0, 1, 1)]
    [InlineData(3, 2, 1, 0)]
    [InlineData(3, 0, -1, 2)]
    [InlineData(3, 1, -1, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(0, 0, 1, -1)]
    [InlineData(3, -1, 1, 0)]
    [InlineData(3, -1, -1, 2)]
    public void Step_Wraps(int count, int current, int delta, int expected)
        => Assert.Equal(expected, TabOrder.Step(count, current, delta));

    [Theory]
    [InlineData(3, 1, 1)]   // closing the middle tab selects the one that slid into its place
    [InlineData(3, 2, 1)]   // closing the last selects the new last
    [InlineData(3, 0, 0)]
    [InlineData(1, 0, -1)]
    public void AfterClose_PicksNeighbour(int countBefore, int closed, int expected)
        => Assert.Equal(expected, TabOrder.AfterClose(countBefore, closed));

    [Fact]
    public void Others_ExcludesKept()
        => Assert.Equal(new[] { "a", "c" }, TabOrder.Others(new[] { "a", "b", "c" }, 1));

    [Theory]
    [InlineData(2, 1000, 160, 80, 160)]
    [InlineData(10, 800, 160, 80, 80)]
    [InlineData(5, 500, 160, 80, 100)]
    [InlineData(0, 500, 160, 80, 160)]
    public void TabWidth_Clamps(int count, int avail, int max, int min, int expected)
        => Assert.Equal(expected, TabOrder.TabWidth(count, avail, max, min));
}
