using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Invented sides Red/Blue from PhaseFixture (Combat is given to Blue here); temp roots only.</summary>
public class HotSeatTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-hotseat-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public HotSeatTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });

    private (ModuleSession S, PhaseController C, HistoryController H, HotSeat HS) Make(bool twoPlayers)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            return p with { Phases = p.Phases.Select(x => x.Id == "ph-combat" ? x with { SideId = "blue" } : x).ToList() };
        });
        if (twoPlayers) s.Game!.Execute(new SetPlayers(new[] { Alice, Bob }));
        Func<GameCommand, GameState, GameCommand> prepare = (c, _) => c;
        var phases = new PhaseController(s, prepare);
        var history = new HistoryController(s, phases, prepare);
        var hs = new HotSeat(s, phases);
        hs.Watch(history);
        return (s, phases, history, hs);
    }

    private static List<string> Track(HotSeat hs)
    {
        var l = new List<string>();
        hs.HandoverRequired += p => l.Add(p.Id);
        return l;
    }

    [Fact]
    public void Solo_NeverHandsOver()
    {
        var (s, c, _, hs) = Make(false);
        var raised = Track(hs);
        Assert.False(hs.Active);
        c.Start(); c.Advance(); c.Advance(); c.Advance();
        Assert.Empty(raised);
        Assert.Null(hs.HandoverDue());
        Assert.Null(s.ViewSides);
    }

    [Fact]
    public void NextPhase_ToOtherSide_RequiresHandover()
    {
        var (_, c, _, hs) = Make(true);
        c.Start(); c.Advance();           // Supply (red)
        hs.TakeOver(Alice);
        var raised = Track(hs);
        c.Advance();                      // Movement (red)
        c.Advance();                      // Combat (blue)
        Assert.Equal(new[] { "p2" }, raised);
        Assert.Equal("p2", hs.HandoverDue()?.Id);
    }

    [Fact]
    public void SameSideNextPhase_NoHandover()
    {
        var (_, c, _, hs) = Make(true);
        c.Start(); c.Advance();
        hs.TakeOver(Alice);
        var raised = Track(hs);
        c.Advance();                      // Supply -> Movement, both red
        Assert.Empty(raised);
        Assert.Null(hs.HandoverDue());
    }

    [Fact]
    public void TakeOver_SetsViewSides()
    {
        var (s, _, _, hs) = Make(true);
        var changed = 0;
        hs.CurrentChanged += () => changed++;
        hs.TakeOver(Bob);
        Assert.Equal("p2", hs.Current?.Id);
        Assert.Equal(1, changed);
        Assert.Equal(new[] { "blue" }, s.ViewSides!.ToArray());
        Assert.NotSame(s.Game!.State, s.View);
    }

    [Fact]
    public void TakeOver_Solo_ClearsViewSides()
    {
        var (s, _, _, hs) = Make(false);
        s.SetViewSides(new HashSet<string> { "red" });
        hs.TakeOver(new PlayerDef("solo", "Player", new[] { "red", "blue" }));
        Assert.Null(s.ViewSides);
    }

    [Fact]
    public void Rewind_IntoOtherSidesPhase_RequiresHandover()
    {
        var (_, c, h, hs) = Make(true);
        c.Start(); c.Advance(); c.Advance(); c.Advance();   // Combat (blue)
        hs.TakeOver(Bob);
        c.Advance();                                         // End (no side)
        var raised = Track(hs);
        h.Undo();                                            // back into Combat: Bob still current
        Assert.Empty(raised);
        h.Undo(); h.Undo();                                  // back into Movement (red)
        Assert.Equal(new[] { "p1" }, raised.Distinct().ToArray());
        Assert.Equal("p1", hs.HandoverDue()?.Id);
    }

    [Fact]
    public void Load_TwoPlayers_HandoverForActingSide()
    {
        var (s, c, _, _) = Make(true);
        c.Start(); c.Advance();                              // Supply (red)
        var path = Path.Combine(_tmp, "g.qmsave");
        s.SaveNative(path);

        var (s2, _, _, hs2) = Make(false);
        var raised = Track(hs2);
        var seenInReplaced = new List<GameState?>();
        s2.GameReplaced += () => seenInReplaced.Add(s2.View);
        s2.SetViewSides(new HashSet<string> { "gone-side" });
        s2.LoadNative(path);

        Assert.Equal(new[] { "p1" }, raised);
        Assert.Null(hs2.Current);
        Assert.Equal(new[] { "red" }, s2.ViewSides!.ToArray());
        Assert.DoesNotContain("gone-side", s2.ViewSides!);
        var seen = Assert.Single(seenInReplaced);
        Assert.NotNull(seen);
        Assert.NotSame(s2.Game!.State, seen);
    }

    [Fact]
    public void Load_TwoPlayers_NoPhases_FirstPlayer()
    {
        var (s, _, _, _) = Make(true);
        var path = Path.Combine(_tmp, "g2.qmsave");
        s.SaveNative(path);
        var (s2, _, _, hs2) = Make(false);
        var raised = Track(hs2);
        s2.LoadNative(path);
        Assert.Equal(new[] { "p1" }, raised);
    }

    [Fact]
    public void Load_Solo_ClearsEverything()
    {
        var (s, _, _, hs) = Make(true);
        hs.TakeOver(Bob);
        var path = Path.Combine(_tmp, "solo.qmsave");
        var (solo, _, _, _) = Make(false);
        solo.SaveNative(path);
        var raised = Track(hs);
        s.LoadNative(path);
        Assert.Empty(raised);
        Assert.Null(hs.Current);
        Assert.Null(s.ViewSides);
    }
}
