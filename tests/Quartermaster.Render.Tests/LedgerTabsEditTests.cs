using Quartermaster.Core;

namespace Quartermaster.Render.Tests;

public class LedgerTabsEditTests
{
    private static UserLedgerTab Tab(string name, string? filter = null) => new(name, filter, Array.Empty<string>(), false, null);

    [Fact]
    public void ValidList_NoProblems()
    {
        Assert.Empty(LedgerTabsEdit.Problems(new[] { Tab("Supply TEST", "Fuel < 5"), Tab("Other TEST") }));
        Assert.Empty(LedgerTabsEdit.Problems(Array.Empty<UserLedgerTab>()));
    }

    [Fact]
    public void BlankName_IsAProblem()
    {
        var p = LedgerTabsEdit.Problems(new[] { Tab("   ") });
        Assert.Single(p);
        Assert.Contains("name", p[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateName_CaseInsensitive_IsAProblem()
    {
        var p = LedgerTabsEdit.Problems(new[] { Tab("Supply TEST"), Tab(" supply test ") });
        Assert.Single(p);
        Assert.Contains("Supply TEST", p[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrokenFilter_MessageContainsParseError()
    {
        var err = LedgerTabs.FilterError("Fuel <");
        Assert.NotNull(err);
        var p = LedgerTabsEdit.Problems(new[] { Tab("Broken TEST", "Fuel <") });
        Assert.Single(p);
        Assert.Contains(err!, p[0]);
        Assert.Contains("Broken TEST", p[0]);
    }
}
