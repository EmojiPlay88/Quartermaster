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

    private static string? PhaseName(PhaseController c) => c.View().Current?.Name;

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
        c.Start(); c.Advance(); c.Advance(); c.Advance();   // Combat (blue), round 1
        hs.TakeOver(Bob);
        c.Advance();                                         // Movement (red), round 2
        hs.TakeOver(Alice);
        var raised = Track(hs);
        h.Undo();                                            // back into Combat (blue)
        Assert.Equal(new[] { "p2" }, raised);
        Assert.Equal("Combat", PhaseName(c));
        hs.TakeOver(Bob);
        h.Undo();                                            // back into Movement (red)
        Assert.Equal(new[] { "p2", "p1" }, raised);
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
        Assert.Empty(s2.ViewSides!);
        Assert.DoesNotContain("gone-side", s2.ViewSides!);
        var seen = Assert.Single(seenInReplaced);
        Assert.NotNull(seen);
        Assert.NotSame(s2.Game!.State, seen);
    }

    [Fact]
    public void EarlyGameReplacedHandler_SeesPublicOnlyView()
    {
        var (s, c, _, _) = Make(true);
        c.Start();
        var path = Path.Combine(_tmp, "e.qmsave");
        s.SaveNative(path);
        var (s2, _, _, _) = Make(false);   // previous game solo: raw view before the load
        var fx = PhaseFixture.Module();
        var seen = new List<GameState?>();
        // A second session whose handler is registered BEFORE HotSeat exists.
        _fixtures.Add(fx);
        var s3 = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "c-early"), Path.Combine(_tmp, "r-early"));
        _sessions.Add(s3);
        s3.LoadSetup(s3.Model.Setups[0], Difficulty.FullyAuto);
        s3.NewRules("t");
        s3.UpdateRules(_ => PhaseFixture.Pack(s3.Archive.Sha256));
        s3.GameReplaced += () => seen.Add(s3.View);
        _ = new HotSeat(s3, new PhaseController(s3, (cmd, _) => cmd));
        s3.LoadNative(path);
        var v = Assert.Single(seen);
        Assert.NotNull(v);
        Assert.NotSame(s3.Game!.State, v);
        Assert.Empty(s3.ViewSides!);
    }

    [Fact]
    public void TakeOver_NonDuePlayer_SetsTheirSides_NoHandover()
    {
        var (s, c, _, hs) = Make(true);
        c.Start(); c.Advance();           // Supply (red): Alice due
        var raised = Track(hs);
        hs.TakeOver(Bob);                 // deliberate swap
        Assert.Empty(raised);
        Assert.Equal(new[] { "blue" }, s.ViewSides!.ToArray());
    }

    [Fact]
    public void ManyUndoSteps_SameDuePlayer_RaiseOnce_AndViewEmptyUntilTakeOver()
    {
        var (s, c, h, hs) = Make(true);
        c.Start(); c.Advance(); c.Advance(); c.Advance();
        hs.TakeOver(Bob);
        var raised = Track(hs);
        h.Undo(); h.Undo(); h.Undo();
        Assert.Equal(new[] { "p1" }, raised);
        Assert.Empty(s.ViewSides!);
        hs.TakeOver(Alice);
        Assert.Equal(new[] { "red" }, s.ViewSides!.ToArray());
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

    private static List<int> TrackCancel(HotSeat hs)
    {
        var l = new List<int>();
        hs.HandoverCancelled += () => l.Add(1);
        return l;
    }

    [Fact]
    public void Rewind_BackIntoCurrentsPhase_CancelsPendingHandover()
    {
        var (s, c, h, hs) = Make(true);
        c.Start(); c.Advance(); c.Advance();                 // Movement (red)
        hs.TakeOver(Alice);
        var raised = Track(hs);
        var cancelled = TrackCancel(hs);
        c.Advance();                                          // Combat (blue): Bob due
        Assert.Equal(new[] { "p2" }, raised);
        Assert.Empty(s.ViewSides!);
        Assert.Equal(Bob.Id, hs.Pending?.Id);
        h.Undo();                                             // back into Movement (red): Alice still at the screen
        Assert.Single(cancelled);
        Assert.Null(hs.HandoverDue());
        Assert.Null(hs.Pending);
        Assert.Equal(new[] { "red" }, s.ViewSides!.ToArray());
        Assert.Equal("p1", hs.Current?.Id);
        c.Advance();                                          // Combat again: dedupe was reset
        Assert.Equal(new[] { "p2", "p2" }, raised);
    }

    [Fact]
    public void SetPlayers_SoloToTwo_MidGame_HandsOverToActingPlayer()
    {
        var (s, c, _, hs) = Make(false);
        c.Start(); c.Advance(); c.Advance(); c.Advance();   // Combat (blue)
        var raised = Track(hs);
        Assert.Null(s.ViewSides);
        s.Game!.Execute(new SetPlayers(new[] { Alice, Bob }));
        Assert.True(hs.Active);
        Assert.Empty(s.ViewSides!);
        Assert.Equal(new[] { "p2" }, raised);
        Assert.Null(hs.Current);
    }

    [Fact]
    public void SetPlayers_SoloToTwo_NoActingSide_HandsOverToFirstPlayer()
    {
        var (s, _, _, hs) = Make(false);
        var raised = Track(hs);
        s.Game!.Execute(new SetPlayers(new[] { Alice, Bob }));
        Assert.Equal(new[] { "p1" }, raised);
        Assert.Empty(s.ViewSides!);
    }

    [Fact]
    public void SetPlayers_BackToSolo_ClearsViewAndDismisses()
    {
        var (s, c, _, hs) = Make(true);
        c.Start(); c.Advance();
        hs.TakeOver(Alice);
        c.Advance(); c.Advance();                            // Combat (blue): Bob pending
        var cancelled = TrackCancel(hs);
        Assert.NotNull(hs.Pending);
        s.Game!.Execute(new SetPlayers(Array.Empty<PlayerDef>()));
        Assert.False(hs.Active);
        Assert.Null(s.ViewSides);
        Assert.Null(hs.Pending);
        Assert.Null(hs.Current);
        Assert.Single(cancelled);
    }

    [Fact]
    public void Undo_SetPlayers_BackToSolo_ClearsView()
    {
        var (s, _, h, hs) = Make(false);
        s.Game!.Execute(new SetPlayers(new[] { Alice, Bob }));
        hs.TakeOver(Alice);
        h.Undo();
        Assert.False(hs.Active);
        Assert.Null(s.ViewSides);
    }

    [Fact]
    public void SetPlayers_ChangesCurrentsSides_ViewFollows()
    {
        var (s, _, _, hs) = Make(true);
        hs.TakeOver(Alice);
        s.Game!.Execute(new SetPlayers(new[] { Alice with { Sides = new[] { "blue" } }, Bob with { Sides = new[] { "red" } } }));
        Assert.Equal(new[] { "blue" }, s.ViewSides!.ToArray());
        Assert.Equal(new[] { "blue" }, hs.Current!.Sides.ToArray());
    }

    [Fact]
    public void PlayerListEdit_ClearsStaleHandover_RaisesCancelled()
    {
        var (s, c, _, hs) = Make(true);
        c.Start(); c.Advance();                              // Supply (red)
        hs.TakeOver(Alice);
        var raised = Track(hs);
        var cancelled = TrackCancel(hs);
        s.Game!.Execute(new SetPlayers(new[] { Alice with { Sides = new[] { "blue" } }, Bob with { Sides = new[] { "red" } } }));
        Assert.Equal(new[] { "p2" }, raised);                // red went to Bob: Bob is due
        Assert.Empty(s.ViewSides!);
        s.Game!.Execute(new SetPlayers(new[] { Alice, Bob })); // red back to Alice: nothing to hand over
        Assert.Single(cancelled);
        Assert.Null(hs.Pending);
        Assert.Null(hs.HandoverDue());
        Assert.Equal(new[] { "red" }, s.ViewSides!.ToArray());
        Assert.Equal("p1", hs.Current?.Id);
    }

    [Fact]
    public void PlayerListEdit_RemovesCurrentPlayer_HandsOverToNewHolder()
    {
        var (s, c, _, hs) = Make(true);
        c.Start(); c.Advance();                              // Supply (red)
        hs.TakeOver(Alice);
        var raised = Track(hs);
        var carol = new PlayerDef("p3", "Carol", new[] { "red" });
        s.Game!.Execute(new SetPlayers(new[] { carol, Bob }));
        Assert.Null(hs.Current);
        Assert.Equal(new[] { "p3" }, raised);
        Assert.Empty(s.ViewSides!);
    }

    [Fact]
    public void Rewind_ToNoActingSide_KeepsPendingHandover()
    {
        var (s, c, h, hs) = Make(true);
        c.Start();                                           // first phase: no acting side
        hs.TakeOver(Bob);
        var raised = Track(hs);
        var cancelled = TrackCancel(hs);
        c.Advance();                                         // Supply (red): Alice due
        Assert.Equal(new[] { "p1" }, raised);
        h.Undo();                                            // back where no side acts
        Assert.Empty(cancelled);
        Assert.Equal("p1", hs.Pending?.Id);
        Assert.Empty(s.ViewSides!);
    }

    [Fact]
    public void NewGame_WithPlayers_PartOfGenesis_NotUndoable()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "c-new"), Path.Combine(_tmp, "r-new"));
        _sessions.Add(s);
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        Func<GameCommand, GameState, GameCommand> prepare = (cmd, _) => cmd;
        var phases = new PhaseController(s, prepare);
        var history = new HistoryController(s, phases, prepare);
        var hs = new HotSeat(s, phases);
        hs.Watch(history);
        var raised = Track(hs);
        s.StartNewGame(Difficulty.FullyAuto, new[] { Alice, Bob });
        Assert.Equal(new[] { "p1", "p2" }, s.Game!.Genesis.Players.Select(p => p.Id).ToArray());
        Assert.False(history.CanUndo);
        Assert.Equal(new[] { "p1" }, raised);
        Assert.Empty(s.ViewSides!);
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        Assert.Equal(2, s.Game!.Genesis.Players.Count);
        Assert.False(history.CanUndo);
    }
    [Fact]
    public void RulesApplied_PhaseSideEdited_HandsOverThenCancels()
    {
        var (s, c, _, hs) = Make(true);
        c.Start(); c.Advance();           // Supply (red)
        hs.TakeOver(Alice);
        var raised = Track(hs);
        var cancels = 0;
        hs.HandoverCancelled += () => cancels++;
        s.UpdateRules(p => p with { Phases = p.Phases.Select(x => x.Id == "ph-supply" ? x with { SideId = "blue" } : x).ToList() });
        Assert.Equal(new[] { "p2" }, raised);
        Assert.Equal("p2", hs.Pending?.Id);
        Assert.Empty(s.ViewSides!);
        s.UpdateRules(p => p with { Phases = p.Phases.Select(x => x.Id == "ph-supply" ? x with { SideId = "red" } : x).ToList() });
        Assert.Equal(1, cancels);
        Assert.Null(hs.Pending);
        Assert.Equal(new[] { "red" }, s.ViewSides!.ToArray());
    }

    [Fact]
    public void RelocationAdvance_OutsideAnAction_RechecksHandover()
    {
        var (s, c, _, hs) = Make(true);
        c.Start(); c.Advance();           // Supply (red)
        hs.TakeOver(Alice);
        var raised = Track(hs);
        var at = s.Game!.State.Position!;
        using (s.Game.BeginAction("Next Phase"))   // inside an action (as in Next Phase): left to the boundary
            s.Game.Execute(new AdvancePhase(at, at with { PhaseId = "ph-combat", PhaseIndex = 3 }, "Supply", "Combat", "Supply was removed"));
        Assert.Empty(raised);
        s.Game.Undo();
        s.Game.Execute(new AdvancePhase(at, at with { PhaseId = "ph-combat", PhaseIndex = 3 }, "Supply", "Combat", "Supply was removed"));
        Assert.Equal(new[] { "p2" }, raised);
        Assert.Empty(s.ViewSides!);
    }
}
