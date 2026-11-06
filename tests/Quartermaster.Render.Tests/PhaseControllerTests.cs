using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Invented pack, formulas and numbers from PhaseFixture; temp roots only.</summary>
public class PhaseControllerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-phase-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public PhaseControllerTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset Stamp = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private (ModuleSession S, PhaseController C) Make(Difficulty d, Func<RulesPack, RulesPack>? tweak = null, bool grid = true,
        Func<GameCommand, GameState, GameCommand>? prepare = null)
    {
        var fx = PhaseFixture.Module(grid);
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d);
        s.NewRules("t");
        s.UpdateRules(_ => { var p = PhaseFixture.Pack(s.Archive.Sha256); return tweak == null ? p : tweak(p); });
        return (s, new PhaseController(s, prepare ?? ((c, _) => c)));
    }

    private static double Water(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "water") ?? 10;
    private static double Fuel(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "fuel") ?? double.NaN;
    private static IEnumerable<string> Texts(ModuleSession s) => s.Game!.State.Log.Select(l => l.Text);

    private static MovePieces Move(string id, int x) => new(new[] { id }, "Main Map", new PointD(x, 50), null);

    private static PhaseDef PhaseOf(RulesPack p, string id) => p.Phases.First(x => x.Id == id);
    private static RulesPack WithPhase(RulesPack p, string id, Func<PhaseDef, PhaseDef> edit) =>
        p with { Phases = p.Phases.Select(x => x.Id == id ? edit(x) : x).ToList() };

    private static void ToMovement(PhaseController c)
    {
        c.Start();          // Weather
        c.Advance();        // Supply
        c.Advance();        // Movement
    }

    // ---- session difficulty ----

    [Fact]
    public void NewGame_Difficulty_IsSetWithoutLogEntry()
    {
        var (s, _) = Make(Difficulty.Hardcore);
        Assert.Equal(Difficulty.Hardcore, s.Game!.State.Difficulty);
        Assert.DoesNotContain(s.Game.Log, c => c is SetDifficulty);
        s.StartNewGame(Difficulty.FullyAuto);
        Assert.Equal(Difficulty.FullyAuto, s.Game!.State.Difficulty);
        s.StartNewGame();
        Assert.Equal(Difficulty.SemiAuto, s.Game!.State.Difficulty);
        Assert.Single(s.Game.State.Log);
    }

    // ---- start / advance ----

    [Fact]
    public void Start_EntersFirstPhase_FiresOnStart()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        var raised = new List<IReadOnlyList<PendingRun>>();
        var boundaries = 0;
        c.RunsQueued += r => raised.Add(r);
        c.PhaseBoundary += () => boundaries++;
        Assert.True(c.View().BeforeStart);

        c.Start();

        var st = s.Game!.State;
        Assert.Equal("ph-weather", st.Position!.PhaseId);
        Assert.Equal(1, st.Position.Turn);
        Assert.Contains("Turn 1 · Weather", Texts(s));
        var run = Assert.Single(st.Pending);
        Assert.Equal("f-weather", run.FormulaId);
        Assert.Equal("start", run.Trigger);
        Assert.Equal(new PendingUnit(null, "Weather roll", null, false), Assert.Single(run.Units));
        Assert.Single(raised);
        Assert.Equal(1, boundaries);
        Assert.False(c.View().BeforeStart);
    }

    [Fact]
    public void FullyAuto_FullTurnWrap_BlockTwice()
    {
        var (s, c) = Make(Difficulty.FullyAuto);
        c.Start();
        for (var i = 0; i < 30 && s.Game!.State.Position!.Turn < 2; i++) c.Advance();

        var st = s.Game!.State;
        Assert.Equal(2, st.Position!.Turn);
        Assert.Equal("ph-weather", st.Position.PhaseId);
        Assert.Equal(2, Texts(s).Count(t => t == "Turn 1 · Movement → Combat"));
        Assert.Equal(9, Water(s, "r1"));
        Assert.Equal(9, Water(s, "r2"));
        Assert.Equal(10, Water(s, "b1"));
        Assert.Empty(st.Pending);
    }

    [Fact]
    public void FullyAuto_OneLinePerUnitPlusSummary()
    {
        var (s, c) = Make(Difficulty.FullyAuto);
        c.Start();
        c.Advance();

        var calc = s.Game!.State.Log.Where(l => l.Kind == LogKind.Calc).Select(l => l.Text).ToList();
        Assert.Single(calc, t => t.StartsWith("Red 1: ") && t.EndsWith("(auto)"));
        Assert.Single(calc, t => t.StartsWith("Red 2: ") && t.EndsWith("(auto)"));
        Assert.DoesNotContain(calc, t => t.StartsWith("Blue 1"));
        Assert.Equal("Daily water: 2 units, 2 applied", calc[^1]);
    }

    [Fact]
    public void FullyAuto_InputWithoutAutoValue_QueuesNeedsInput()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => p with
        {
            Formulas = p.Formulas.Select(f => f.Id != "f-fuel" ? f : f with
            {
                Bindings = f.Bindings.Select(b => b.Variable == "Terrain" ? b with { AutoValue = null } : b).ToList(),
            }).ToList(),
        });
        var raised = new List<IReadOnlyList<PendingRun>>();
        c.RunsQueued += r => raised.Add(r);
        ToMovement(c);

        c.ExecuteMove(new[] { Move("r1", 130) });

        var run = Assert.Single(s.Game!.State.Pending);
        Assert.True(run.NeedsInput);
        Assert.Equal("move", run.Trigger);
        Assert.Equal(new PendingUnit("r1", "Red 1", 2, false), Assert.Single(run.Units));
        Assert.Equal(40, Fuel(s, "r1"));
        Assert.Contains(raised, r => r.Any(x => x.NeedsInput));
    }

    [Fact]
    public void SemiAuto_OnStartUnits_QueuesOneRunPerEntry_RaisesRunsQueued()
    {
        var (s, c) = Make(Difficulty.SemiAuto, p => WithPhase(p, "ph-supply", ph => ph with
        {
            OnStart = new() { ph.OnStart[0], ph.OnStart[0] },
        }));
        c.Start();
        var raised = new List<IReadOnlyList<PendingRun>>();
        c.RunsQueued += r => raised.Add(r);

        c.Advance();

        var pending = s.Game!.State.Pending;
        Assert.Equal(2, pending.Count);
        Assert.NotEqual(pending[0].Id, pending[1].Id);
        Assert.All(pending, r =>
        {
            Assert.Equal("f-water", r.FormulaId);
            Assert.Equal("start", r.Trigger);
            Assert.False(r.NeedsInput);
            Assert.Equal(new[] { "r1", "r2" }, r.Units.Select(u => u.PieceId));
            Assert.All(r.Units, u => Assert.False(u.Applied));
        });
        var one = Assert.Single(raised);
        Assert.Equal(2, one.Count);
        Assert.Equal(10, Water(s, "r1"));
    }

    [Fact]
    public void Hardcore_SameQueueAsSemiAuto()
    {
        static List<string> Shape(Difficulty d, PhaseControllerTests t)
        {
            var (s, c) = t.Make(d);
            c.Start();
            c.Advance();
            return s.Game!.State.Pending.Select(r =>
                $"{r.FormulaId}|{r.Trigger}|{r.NeedsInput}|{string.Join(",", r.Units.Select(u => u.PieceId + ":" + u.Label))}").ToList();
        }
        var semi = Shape(Difficulty.SemiAuto, this);
        Assert.Equal(semi, Shape(Difficulty.Hardcore, this));
        Assert.Single(semi);
    }

    [Fact]
    public void ExecuteMove_FormationDrag_OneRunWithAllUnits_MovedSet()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        ToMovement(c);

        c.ExecuteMove(new[] { Move("r1", 130), Move("r2", 180) });

        Assert.Equal(new PointD(130, 50), s.Game!.State.Pieces["r1"].Pos);
        var run = Assert.Single(s.Game.State.Pending);
        Assert.Equal("f-fuel", run.FormulaId);
        Assert.Equal("move", run.Trigger);
        Assert.Equal(new[] { new PendingUnit("r1", "Red 1", 2, false), new PendingUnit("r2", "Red 2", 3, false) }, run.Units);
        Assert.Equal(40, Fuel(s, "r1"));
    }

    [Fact]
    public void ExecuteMove_FullyAuto_AppliesFuel()
    {
        var (s, c) = Make(Difficulty.FullyAuto);
        ToMovement(c);

        c.ExecuteMove(new[] { Move("r1", 130), Move("r2", 180), Move("b1", 130) });

        Assert.Equal(36, Fuel(s, "r1"));     // 2 cells x Clear (2)
        Assert.Equal(34, Fuel(s, "r2"));     // 3 cells x 2
        Assert.Equal(40, Fuel(s, "b1"));     // Blue is not the acting side
        Assert.Empty(s.Game!.State.Pending);
        Assert.Contains(Texts(s), t => t.StartsWith("Fuel for movement: 2 units, 2 applied"));
    }

    [Fact]
    public void ExecuteMove_NoGrid_ZeroAndLogLine()
    {
        var (s, c) = Make(Difficulty.FullyAuto, grid: false);
        ToMovement(c);

        c.ExecuteMove(new[] { Move("r1", 130) });

        Assert.Contains("Red 1: moved distance counted as 0 (no grid)", Texts(s));
        Assert.Equal(40, Fuel(s, "r1"));
        Assert.Contains(s.Game!.State.LedgerHistory, h => h.Key == "p:r1" && h.PropertyId == "fuel" && h.Source == "formula");
    }

    [Fact]
    public void Advance_DropsPending_LogsSkipped()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        c.Start();
        c.Advance();

        Assert.Contains("skipped: Weather roll, Weather roll", Texts(s));
        var run = Assert.Single(s.Game!.State.Pending);
        Assert.Equal("f-water", run.FormulaId);
    }

    [Fact]
    public void Advance_SideChange_ReturnsPassNotice()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        c.Start();
        Assert.Equal("Pass to Red player", c.Advance());   // Weather (no side) -> Supply (Red)
        Assert.Null(c.Advance());                          // Supply -> Movement, still Red
        Assert.Null(c.Advance());                          // Movement -> Combat, no side
        var back = c.Advance();                            // Combat -> Movement (repeat 2)
        Assert.Equal("Pass to Red player", back);
        Assert.Equal("Pass to Red player", c.View().Notice);
        Assert.Equal(2, s.Game!.State.Position!.Repeat);
    }

    [Fact]
    public void Advance_BeforeStart_StartsGame_AndNoPhasesIsNoOp()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        Assert.Null(c.Advance());
        Assert.Equal("ph-weather", s.Game!.State.Position!.PhaseId);

        var (s2, c2) = Make(Difficulty.SemiAuto, p => p with { Phases = new(), Blocks = new() });
        Assert.True(c2.View().NoPhases);
        Assert.Null(c2.Advance());
        c2.Start();
        Assert.Null(s2.Game!.State.Position);
    }

    [Fact]
    public void SetDifficulty_PendingRunsKept()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        c.Start();
        c.SetDifficulty(Difficulty.FullyAuto);
        Assert.Equal(Difficulty.FullyAuto, s.Game!.State.Difficulty);
        Assert.Single(s.Game.State.Pending);
        var n = s.Game.Log.Count;
        c.SetDifficulty(Difficulty.FullyAuto);
        Assert.Equal(n, s.Game.Log.Count);
        Assert.Contains("Difficulty changed to Fully automatic", Texts(s));
    }

    [Fact]
    public void ApplyFormula_Calculated_TagsAndResolves()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        c.Start();
        c.Advance();
        var run = s.Game!.State.Pending.Single();
        var trace = s.Runner!.Run(s.Game.State, new RunRequest("f-water", "r1", new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>(), null, true));

        c.ApplyFormula(run.Id, "r1", trace, true);

        Assert.Equal(9, Water(s, "r1"));
        Assert.Contains(s.Game.State.Log, l => l.Text.StartsWith("Red 1: ") && l.Text.EndsWith("(auto-calculated)"));
        var left = s.Game.State.Pending.Single();
        Assert.True(left.Units.Single(u => u.PieceId == "r1").Applied);
        Assert.False(left.Units.Single(u => u.PieceId == "r2").Applied);
    }

    [Fact]
    public void ApplyFormula_Manual_NoTag()
    {
        var (s, c) = Make(Difficulty.Hardcore);
        c.Start();
        c.Advance();
        var run = s.Game!.State.Pending.Single();
        var trace = s.Runner!.Run(s.Game.State, new RunRequest("f-water", "r2", new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>(), null, true));
        c.ApplyFormula(run.Id, "r2", trace, false);
        Assert.DoesNotContain(s.Game.State.Log, l => l.Text.Contains("auto-calculated"));
        Assert.Equal(9, Water(s, "r2"));
    }

    // ---- bad data ----

    [Fact]
    public void OnStart_MissingFormula_SkippedWithLogLine()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => WithPhase(p, "ph-supply", ph => ph with
        {
            OnStart = new() { new StartEntry { FormulaId = "f-gone", ForEach = ForEachMode.Units }, ph.OnStart[0] },
        }));
        c.Start();
        c.Advance();

        Assert.Contains("Supply: formula \"f-gone\" not found — skipped", Texts(s));
        Assert.Equal(9, Water(s, "r1"));
    }

    [Fact]
    public void OnStart_BadFilter_LogsLineAndSkips()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => WithPhase(p, "ph-supply", ph => ph with
        {
            OnStart = new() { ph.OnStart[0] with { UnitFilter = "((" } },
        }));
        c.Start();
        c.Advance();

        Assert.Contains("Supply: filter \"((\" is not valid — skipped", Texts(s));
        Assert.Equal(10, Water(s, "r1"));
        Assert.Equal("ph-supply", s.Game!.State.Position!.PhaseId);
    }

    [Fact]
    public void Reconcile_AfterRulesEdit_LogsAndRelocates()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        ToMovement(c);
        Assert.Null(c.Reconcile());

        s.UpdateRules(p => p with { Phases = p.Phases.Where(x => x.Id != "ph-move").ToList() });
        var notice = c.Reconcile();

        Assert.Equal("Phase \"ph-move\" no longer exists; continuing at \"Combat\"", notice);
        Assert.Equal("ph-combat", s.Game!.State.Position!.PhaseId);
        Assert.Equal(notice, s.Game.State.Log[^1].Text);
        Assert.Null(c.Reconcile());
        Assert.Equal(notice, c.View().Notice);
    }

    [Fact]
    public void Advance_StalePhase_RelocatesThenAdvances()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        ToMovement(c);
        s.UpdateRules(p => p with { Phases = p.Phases.Where(x => x.Id != "ph-move").ToList() });

        c.Advance();

        Assert.Contains(Texts(s), t => t.Contains("no longer exists"));
        Assert.Equal("ph-end", s.Game!.State.Position!.PhaseId);   // relocated to Combat, then advanced
    }

    [Fact]
    public void Reconcile_AllPhasesDeleted_FreeForm()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        ToMovement(c);
        s.UpdateRules(p => p with { Phases = new(), Blocks = new() });
        Assert.Null(c.Reconcile());
        Assert.True(c.View().NoPhases);
        Assert.Null(c.Advance());
    }

    // ---- view / checklist / warnings ----

    [Fact]
    public void View_ReportsChipsStageNextAndDecisions()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        Assert.True(c.View().BeforeStart);
        c.Start();
        c.Advance();
        c.Advance();
        c.Advance();       // Combat, repeat 1
        var v = c.View();
        Assert.Equal("Combat", v.Current!.Name);
        Assert.Equal("Round 1 of 2", v.StageText);
        Assert.Equal("Movement", v.NextName);
        Assert.Equal(new[] { ChipState.Done, ChipState.Done, ChipState.Done, ChipState.Current, ChipState.Upcoming }, v.Chips.Select(x => x.State));
        Assert.Contains(v.Decisions, d => d.Kind == DecisionKind.Checklist);

        c.Tick(0, true);
        Assert.DoesNotContain(c.View().Decisions, d => d.Kind == DecisionKind.Checklist);
        Assert.Contains("Done: Resolve attacks", Texts(s));
    }

    [Fact]
    public void Warning_FiresAndAcknowledges()
    {
        var (s, c) = Make(Difficulty.FullyAuto);
        s.Game!.Execute(new LedgerDelta("p:r1", "fuel", 40, 0, "manual", null, null, null, null));
        for (var i = 0; i < 4; i++) { if (i == 0) c.Start(); else c.Advance(); }
        var v = c.View();
        Assert.Equal("Combat", v.Current!.Name);
        var hit = Assert.Single(v.Warnings);
        Assert.Equal(new[] { "r1" }, hit.PieceIds);
        Assert.Contains(v.Decisions, d => d.Kind == DecisionKind.Warning);

        c.Acknowledge(0);
        Assert.DoesNotContain(c.View().Decisions, d => d.Kind == DecisionKind.Warning);
        Assert.Contains("Acknowledged: Out of fuel", Texts(s));
    }

    [Fact]
    public void PrepareHook_StampsEveryCommand()
    {
        var (s, c) = Make(Difficulty.FullyAuto, prepare: (cmd, _) => cmd switch
        {
            AdvancePhase a => a with { At = Stamp, Who = "me" },
            _ => cmd,
        });
        c.Start();
        Assert.All(s.Game!.Log.OfType<AdvancePhase>(), a => Assert.Equal("me", a.Who));
        Assert.Contains(s.Game.Log, x => x is AdvancePhase);
    }

    // ---- replay ----

    [Fact]
    public void Replay_CommandLog_ReproducesState()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        var initial = s.Game!.State.DeepClone();
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        var c = new PhaseController(s, (cmd, _) => cmd);

        ToMovement(c);
        c.ExecuteMove(new[] { Move("r1", 130), Move("r2", 180) });
        c.Advance();
        c.SetDifficulty(Difficulty.SemiAuto);
        c.Advance();
        c.Tick(0, true);
        for (var i = 0; i < 5; i++) c.Advance();

        var replay = initial.DeepClone();
        foreach (var cmd in s.Game.Log) replay.Apply(cmd);
        Assert.Equal(Json(s.Game.State), Json(replay));
    }

    private static string Json(GameState st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);
}
