using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>Alert bar chips and match cycling. Invented pack from PhaseFixture; temp roots only.</summary>
public class AlertBarModelTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-abm-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public AlertBarModelTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private ModuleSession Make(params AlertDef[] alerts)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256) with { Alerts = alerts.ToList() });
        return s;
    }

    private static AlertDef Red(string id = "a-red", string? side = null) =>
        new() { Id = id, Name = "Red units", Condition = "startsWith(name(), \"Red\")", Message = "Red is here", Severity = AlertSeverity.Warning, SideId = side, Notes = "see rules" };

    [Fact]
    public void Alert_chip_label_and_tooltip()
    {
        var s = Make(Red());
        var m = new AlertBarModel(s);
        var chip = Assert.Single(m.Recompute(null, null, new HashSet<string>()));
        Assert.Equal("Red units (2)", chip.Label);
        Assert.Equal(2, chip.MatchCount);
        Assert.Equal(AlertSeverity.Warning, chip.Severity);
        Assert.Equal("Red is here\nRed 1\nRed 2\n\nsee rules", chip.Tooltip);
        Assert.Equal("+3", AlertBarModel.OverflowText(3));
        Assert.Equal((200, 40, 40), AlertBarModel.Accent(AlertSeverity.Critical));
        Assert.Equal((214, 140, 0), AlertBarModel.Accent(AlertSeverity.Warning));
        Assert.Equal((40, 100, 200), AlertBarModel.Accent(AlertSeverity.Info));
        Assert.Equal("Go to next match", AlertBarModel.GoToNextMatchText);
        Assert.Equal("Hide this alert until next turn", AlertBarModel.HideUntilNextTurnText);
        Assert.Equal("Click to open the turn and replay it.", AlertBarModel.NoticeTip);
    }

    [Fact]
    public void Recompute_skips_hidden_alerts()
    {
        var s = Make(Red("a1"), Red("a2") with { Name = "Other" });
        var m = new AlertBarModel(s);
        Assert.Equal(2, m.Recompute(null, null, new HashSet<string>()).Count);
        var chips = m.Recompute(null, null, new HashSet<string> { "a1" });
        Assert.Equal("a2", Assert.Single(chips).Id);
        s.Game!.Execute(new HideAlert("a2"));
        Assert.Empty(m.Recompute(null, null, new HashSet<string> { "a1" }));
    }

    [Fact]
    public void Recompute_uses_acting_side()
    {
        var s = Make(Red(side: "red"));
        var m = new AlertBarModel(s);
        Assert.Single(m.Recompute(null, "red", new HashSet<string>()));
        Assert.Empty(m.Recompute(null, "blue", new HashSet<string>()));
        Assert.Empty(m.Recompute(null, null, new HashSet<string>()));
    }

    [Fact]
    public void Next_match_cycles_and_wraps()
    {
        var s = Make(Red());
        var m = new AlertBarModel(s);
        m.Recompute(null, null, new HashSet<string>());
        var first = m.NextMatch("a-red");
        var second = m.NextMatch("a-red");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(first, m.NextMatch("a-red"));   // wraps
        Assert.Null(m.NextMatch("nope"));
        m.Reset();
        Assert.Null(m.NextMatch("a-red"));
    }
}
