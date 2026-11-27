using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>B7 #10: the host refuses a guest's Next Phase while another side's ask run is open, without naming hidden data. Invented sides only.</summary>
public class B7NextPhaseGateTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b7gate-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "north" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "south" });
    private static readonly IReadOnlySet<string> North = new HashSet<string> { "north" };
    private static readonly IReadOnlySet<string> South = new HashSet<string> { "south" };

    public B7NextPhaseGateTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Sides = new()
            {
                new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"Red\")" },
                new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"Blue\")" },
            },
            Phases = p.Phases.Select(x => x.Id == "ph-weather" ? x with { Name = "Dawn" } : x.Id == "ph-supply" || x.Id == "ph-move" ? x with { SideId = "north" } : x.Id == "ph-combat" ? x with { SideId = "south" } : x).ToList(),
        };
    }

    private (ModuleSession S, PhaseController C, IntentExecutor X) Open()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        return (s, c, new IntentExecutor(s, c));
    }

    private static PendingRun Ask(string id, string side) =>
        new(id, "f-weather", "Weather roll", "start", new[] { new PendingUnit(null, "once", null, false) }, false) { AskSideId = side };

    private static void Queue(ModuleSession s, params PendingRun[] runs) => s.Game!.Execute(new QueueRuns(runs));

    [Fact]
    public void Guest_next_phase_refused_while_other_side_answers()
    {
        var (s, c, x) = Open();
        c.Start();
        Queue(s, Ask("r1", "north"));
        var pos = s.Game!.State.Position;
        var r = x.Execute("p2", South, new NextPhaseIntent());
        Assert.False(r.Ok);
        Assert.Equal("Northern League is playing Dawn; wait for your phase.", r.Message);
        Assert.Equal(pos, s.Game.State.Position);
        Assert.Single(s.Game.State.Pending);
    }

    [Fact]
    public void Guest_next_phase_allowed_when_own_side_answers()
    {
        var (s, c, x) = Open();
        c.Start();
        Queue(s, Ask("r1", "north"));
        var r = x.Execute("p1", North, new NextPhaseIntent());
        Assert.True(r.Ok, r.Message);
    }

    [Fact]
    public void Guest_next_phase_allowed_without_ask_run()
    {
        var (s, c, x) = Open();
        c.Start();
        var r = x.Execute("p2", South, new NextPhaseIntent());
        Assert.True(r.Ok, r.Message);
        Assert.Equal("ph-supply", s.Game!.State.Position!.PhaseId);
    }

    [Fact]
    public void Host_press_unchanged()
    {
        var (s, c, _) = Open();
        c.Start();
        Queue(s, Ask("r1", "north"));
        var before = s.Game!.State.Position!.PhaseId;
        c.Advance();   // the host's own press is not gated by the guest rule
        Assert.NotEqual(before, s.Game.State.Position!.PhaseId);
    }

    [Fact]
    public void Hotseat_playing_text_not_shown_when_viewer_has_own_open_ask()
    {
        var (s, c, _) = Open();
        c.Start();
        Queue(s, Ask("r1", "north"), Ask("r2", "south"));
        s.SetViewSides(South);
        Assert.Null(c.View().PlayingText);                       // South has its own open ask
        s.SetViewSides(North);
        Assert.Null(c.View().PlayingText);
        Assert.Equal("north", PhaseController.OpenAskFor(s.Game!.State, South)?.AskSideId);
        Assert.Equal("south", PhaseController.OpenAskFor(s.Game.State, North)?.AskSideId);
        Assert.Null(PhaseController.OpenAskFor(s.Game.State, new HashSet<string> { "north", "south" }));
    }

    [Fact]
    public void Hotseat_playing_text_shown_when_only_other_side_asked()
    {
        var (s, c, _) = Open();
        c.Start();
        Queue(s, Ask("r1", "north"));
        s.SetViewSides(South);
        Assert.Equal("Northern League is playing Dawn", c.View().PlayingText);
    }
}
