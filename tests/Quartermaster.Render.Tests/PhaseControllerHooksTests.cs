using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>End-of-phase entries and side modes of move entries. Invented pack, formulas and numbers from PhaseFixture; temp roots only.</summary>
public class PhaseControllerHooksTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-phasehooks-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public PhaseControllerHooksTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private const string StopNotice = "Finish the end-of-phase runs, then press Next Phase again";

    private static readonly IReadOnlySet<string> RedOnly = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> BlueOnly = new HashSet<string> { "blue" };

    private (ModuleSession S, PhaseController C) Make(Difficulty d, Func<RulesPack, RulesPack>? tweak = null, GameState[]? initial = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d);
        if (initial != null) initial[0] = s.Game!.State.DeepClone();
        s.NewRules("t");
        s.UpdateRules(_ => { var p = PhaseFixture.Pack(s.Archive.Sha256); return tweak == null ? p : tweak(p); });
        return (s, new PhaseController(s, (c, _) => c));
    }

    private static RulesPack WithPhase(RulesPack p, string id, Func<PhaseDef, PhaseDef> edit) =>
        p with { Phases = p.Phases.Select(x => x.Id == id ? edit(x) : x).ToList() };

    /// <summary>Supply (Red) gets an end entry: daily water for each unit.</summary>
    private static RulesPack SupplyOnEnd(RulesPack p) => WithPhase(p, "ph-supply", ph => ph with
    {
        OnEnd = new() { new StartEntry { FormulaId = "f-water", ForEach = ForEachMode.Units } },
    });

    /// <summary>Movement (Red) fuel entry fires for <paramref name="side"/>, optionally with an entry filter.</summary>
    private static Func<RulesPack, RulesPack> MoveFires(MoveSide side, string? filter = null) => p => WithPhase(p, "ph-move", ph => ph with
    {
        OnMove = new() { new MoveEntry { FormulaId = "f-fuel", FiresFor = side, UnitFilter = filter } },
    });

    private static double Water(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "water") ?? 10;
    private static double Fuel(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "fuel") ?? double.NaN;
    private static MovePieces Move(string id, int x) => new(new[] { id }, "Main Map", new PointD(x, 50), null);
    private static string Json(GameState st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);
    private static int EndRunsQueued(ModuleSession s) => s.Game!.Log.OfType<QueueRuns>().SelectMany(q => q.Runs).Count(r => r.Trigger == "end");

    private static void ToMovement(PhaseController c)
    {
        c.Start();          // Weather
        c.Advance();        // Supply
        c.Advance();        // Movement
    }

    // ---- end-of-phase entries ----

    [Fact]
    public void OnEnd_automatic_is_one_undo_step()
    {
        var (s, c) = Make(Difficulty.FullyAuto, SupplyOnEnd);
        c.Start();
        c.Advance();        // Supply: OnStart water 10 -> 9
        Assert.Equal(9, Water(s, "r1"));
        var before = Json(s.Game!.State);
        var actions = s.Game.Actions.Count;
        var mark = s.Game.Log.Count;

        c.Advance();        // OnEnd water 9 -> 8, then on to Movement in the same press

        Assert.Equal("ph-move", s.Game.State.Position!.PhaseId);
        Assert.Equal(8, Water(s, "r1"));
        Assert.Equal(8, Water(s, "r2"));
        Assert.Null(s.Game.State.EndedAt);
        var added = s.Game.Log.Skip(mark).ToList();
        Assert.Single(added.OfType<EndPhase>());
        Assert.Single(added.OfType<AdvancePhase>());
        Assert.True(added.FindIndex(x => x is EndPhase) < added.FindIndex(x => x is AdvancePhase));
        Assert.Equal(actions + 1, s.Game.Actions.Count);
        Assert.Equal("Next Phase", s.Game.UndoLabel);

        s.Game.Undo();
        Assert.Equal(before, Json(s.Game.State));
        Assert.Equal("ph-supply", s.Game.State.Position!.PhaseId);
        Assert.Null(s.Game.State.EndedAt);
    }

    [Fact]
    public void OnEnd_queued_stops_then_advances_once()
    {
        var (s, c) = Make(Difficulty.SemiAuto, SupplyOnEnd);
        c.Start();
        c.Advance();        // Supply
        var raised = new List<IReadOnlyList<PendingRun>>();
        var boundaries = 0;
        c.RunsQueued += r => raised.Add(r);
        c.PhaseBoundary += () => boundaries++;
        var beforeFirst = Json(s.Game!.State);
        var supply = s.Game.State.Position;

        Assert.Null(c.Advance());   // first press: queues the end run and stays

        var st = s.Game.State;
        Assert.Equal(supply, st.Position);
        Assert.Equal(supply, st.EndedAt);
        Assert.Equal(StopNotice, c.View().Notice);
        var endRun = Assert.Single(st.Pending, r => r.Trigger == "end");
        Assert.Equal("f-water", endRun.FormulaId);
        Assert.Equal(new[] { "r1", "r2" }, endRun.Units.Select(u => u.PieceId));
        Assert.Equal(endRun, Assert.Single(Assert.Single(raised)));
        Assert.Equal(0, boundaries);
        Assert.Equal("Next Phase", s.Game.UndoLabel);
        var afterFirst = Json(st);
        var actions = s.Game.Actions.Count;

        c.Advance();                // second press: advances without re-running the end entries

        Assert.Equal("ph-move", s.Game.State.Position!.PhaseId);
        Assert.Null(s.Game.State.EndedAt);
        Assert.Equal(1, EndRunsQueued(s));
        Assert.Single(s.Game.Log.OfType<EndPhase>());
        Assert.Equal(actions + 1, s.Game.Actions.Count);
        Assert.Equal(1, boundaries);

        s.Game.Undo();              // undo the second press: back in the phase, end runs done this pass
        Assert.Equal(afterFirst, Json(s.Game.State));
        Assert.Equal(supply, s.Game.State.EndedAt);

        s.Game.Undo();              // undo the first press: end runs not done
        Assert.Equal(beforeFirst, Json(s.Game.State));
        Assert.Null(s.Game.State.EndedAt);
    }

    [Fact]
    public void OnEnd_stop_notice_dropped_by_undo()
    {
        var (s, c) = Make(Difficulty.SemiAuto, SupplyOnEnd);
        c.Start();
        c.Advance();        // Supply
        c.Advance();        // stops for the end run
        Assert.Equal(StopNotice, c.View().Notice);

        s.Game!.Undo();     // same position, end runs no longer done

        Assert.Null(s.Game.State.EndedAt);
        Assert.Null(c.View().Notice);
    }

    [Fact]
    public void Phase_without_OnEnd_advances_as_before()
    {
        var (s, c) = Make(Difficulty.SemiAuto, SupplyOnEnd);
        c.Start();
        var actions = s.Game!.Actions.Count;

        Assert.Equal("Pass to Red player", c.Advance());   // Weather has no end entries

        Assert.Equal("ph-supply", s.Game.State.Position!.PhaseId);
        Assert.Empty(s.Game.Log.OfType<EndPhase>());
        Assert.Null(s.Game.State.EndedAt);
        Assert.Equal(0, EndRunsQueued(s));
        Assert.Equal(actions + 1, s.Game.Actions.Count);
    }

    [Fact]
    public void Skipped_phase_runs_no_OnEnd()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p =>
        {
            p = SupplyOnEnd(p) with
            {
                Properties = p.Properties.Append(new PropertyDef { Id = "flag", Name = "Flag", Scope = PropertyScope.Holders, Default = 0 }).ToList(),
            };
            return WithPhase(p, "ph-supply", x => x with { Condition = "Flag == 1" });
        });
        c.Start();
        c.Advance();        // Weather -> Movement, Supply skipped

        Assert.Equal("ph-move", s.Game!.State.Position!.PhaseId);
        Assert.Equal(10, Water(s, "r1"));
        Assert.Empty(s.Game.Log.OfType<EndPhase>());
        Assert.Equal(0, EndRunsQueued(s));
    }

    [Fact]
    public void Replay_applies_EndPhase_as_recorded()
    {
        var initial = new GameState[1];
        var (s, c) = Make(Difficulty.SemiAuto, SupplyOnEnd, initial);
        c.Start();
        c.Advance();        // Supply
        c.Advance();        // end run queued, stays
        var stopped = Json(s.Game!.State);
        var stoppedAt = s.Game.Log.Count;
        c.Advance();        // Movement
        c.SetDifficulty(Difficulty.FullyAuto);
        for (var i = 0; i < 7; i++) c.Advance();   // Combat, Movement, Combat, End, Weather, Supply, then out of Supply (end entries applied automatically)

        var replay = initial[0].DeepClone();
        foreach (var cmd in s.Game.Log.Take(stoppedAt)) replay.Apply(cmd);
        Assert.Equal(stopped, Json(replay));
        Assert.Equal(replay.Position, replay.EndedAt);
        foreach (var cmd in s.Game.Log.Skip(stoppedAt)) replay.Apply(cmd);
        Assert.Equal(Json(s.Game.State), Json(replay));
        Assert.Equal(2, s.Game.Log.OfType<EndPhase>().Count());
        Assert.Equal("ph-move", s.Game.State.Position!.PhaseId);
    }

    // ---- move entries by side ----

    [Fact]
    public void OnMove_acting_default_unchanged()
    {
        var (s, c) = Make(Difficulty.FullyAuto, MoveFires(MoveSide.Acting));
        ToMovement(c);

        c.ExecuteMove(new[] { Move("r1", 130), Move("b1", 130) });

        Assert.Equal(36, Fuel(s, "r1"));
        Assert.Equal(40, Fuel(s, "b1"));
    }

    [Fact]
    public void OnMove_other_side_fires_for_non_phasing_piece()
    {
        var (s, c) = Make(Difficulty.FullyAuto, MoveFires(MoveSide.Other));
        ToMovement(c);

        c.ExecuteMove(new[] { Move("r1", 130), Move("b1", 130) });

        Assert.Equal(40, Fuel(s, "r1"));
        Assert.Equal(36, Fuel(s, "b1"));     // 2 cells x Clear (2)
        Assert.Equal(40, Fuel(s, "r2"));     // not moved
    }

    [Fact]
    public void OnMove_any_side_fires_for_every_moved_piece()
    {
        var (s, c) = Make(Difficulty.FullyAuto, MoveFires(MoveSide.Any));
        ToMovement(c);

        c.ExecuteMove(new[] { Move("r1", 130), Move("b1", 130) });

        Assert.Equal(36, Fuel(s, "r1"));
        Assert.Equal(36, Fuel(s, "b1"));
        Assert.Equal(40, Fuel(s, "r2"));
    }

    [Fact]
    public void OnMove_other_side_runs_for_owner()
    {
        var (s, c) = Make(Difficulty.FullyAuto, MoveFires(MoveSide.Any));
        ToMovement(c);      // Movement (Red)
        s.SetViewSides(RedOnly);
        var mark = s.Game!.Log.Count;

        c.ExecuteMove(new[] { Move("r1", 130), Move("b1", 130) });

        var added = s.Game.Log.Skip(mark).ToList();
        var deltas = added.OfType<LedgerDelta>().Where(d => d.Source == "formula").ToList();
        Assert.Equal("red", Assert.Single(deltas, d => d.Key == "p:r1").SideId);
        Assert.Equal("blue", Assert.Single(deltas, d => d.Key == "p:b1").SideId);
        Assert.All(added.OfType<AddLogLine>().Where(l => l.PieceId == "b1"), l => Assert.Equal("blue", l.SideId));
    }

    [Fact]
    public void OnMove_other_side_queued_run_belongs_to_owner()
    {
        var (s, c) = Make(Difficulty.SemiAuto, MoveFires(MoveSide.Any));
        ToMovement(c);
        s.SetViewSides(RedOnly);

        c.ExecuteMove(new[] { Move("r1", 130), Move("b1", 130) });

        var runs = s.Game!.State.Pending.Where(r => r.Trigger == "move").ToList();
        Assert.Equal(2, runs.Count);   // one run per owner
        var blueRun = Assert.Single(runs, r => r.Units.Any(u => u.PieceId == "b1"));
        Assert.Equal(new[] { "b1" }, blueRun.Units.Select(u => u.PieceId));
        var none = new Dictionary<string, string>();
        var trace = s.Runner!.Run(s.Game.State, new RunRequest("f-fuel", "b1", none, none, new Dictionary<string, IReadOnlyList<int>>(), 2, true));
        Assert.False(c.ApplyFormula(blueRun.Id, "b1", trace, true));   // Red at the table may not apply it
        s.SetViewSides(BlueOnly);
        var mark = s.Game.Log.Count;
        Assert.True(c.ApplyFormula(blueRun.Id, "b1", trace, true));
        Assert.All(s.Game.Log.Skip(mark).OfType<LedgerDelta>(), d => Assert.Equal("blue", d.SideId));
        Assert.Equal(36, Fuel(s, "b1"));
    }

    [Fact]
    public void OnMove_map_filter_excludes_board_drag()
    {
        var (s, c) = Make(Difficulty.FullyAuto, MoveFires(MoveSide.Any, "map() == \"Main\""));
        ToMovement(c);

        c.ExecuteMove(new[] { Move("r1", 130), Move("b1", 130) });   // both on "Main Map", not "Main"

        Assert.Equal(40, Fuel(s, "r1"));
        Assert.Equal(40, Fuel(s, "b1"));
        Assert.Empty(s.Game!.State.Pending);

        var (s2, c2) = Make(Difficulty.FullyAuto, MoveFires(MoveSide.Any, "map() == \"Main Map\""));
        ToMovement(c2);
        c2.ExecuteMove(new[] { Move("b1", 130) });
        Assert.Equal(36, Fuel(s2, "b1"));
    }
}
