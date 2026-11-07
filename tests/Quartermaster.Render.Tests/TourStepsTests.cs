using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class TourStepsTests
{
    [Fact]
    public void Tour_HasEightSteps_InOrder()
    {
        var ids = TourSteps.All.Select(s => s.Id).ToArray();
        Assert.Equal(new[] { "moduleTree", "map", "phaseStrip", "phasePanel", "alertBar", "outliner", "ledger", "undo" }, ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(TourSteps.All, s => { Assert.False(string.IsNullOrWhiteSpace(s.TargetId)); Assert.False(string.IsNullOrWhiteSpace(s.Title)); });
    }

    [Fact]
    public void Tour_Visible_SkipsMissingTargets()
    {
        var hidden = new HashSet<string> { "PhaseStrip", "PhasePanel" };
        var vis = TourSteps.Visible(t => !hidden.Contains(t));
        Assert.Equal(6, vis.Count);
        Assert.DoesNotContain(vis, s => hidden.Contains(s.TargetId));
        Assert.Empty(TourSteps.Visible(_ => false));
        Assert.Equal(TourSteps.All.Count, TourSteps.Visible(_ => true).Count);
    }

    [Fact]
    public void Tour_Text_IsShort()
    {
        foreach (var s in TourSteps.All)
        {
            Assert.InRange(s.Text.Length, 1, 300);
            var sentences = s.Text.Split(new[] { ". ", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.InRange(sentences, 1, 3);
        }
    }
}
