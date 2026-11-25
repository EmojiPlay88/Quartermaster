using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>B2 phase pipeline (spec #32 strict order, #72 ask runs, #56 reset at phase entry). Invented PhaseFixture pack; temp roots only.</summary>
public class PhasePipelineTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-pipeline-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public PhasePipelineTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private (ModuleSession S, PhaseController C) Make(Difficulty d, Func<RulesPack, RulesPack>? tweak = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d);
        s.NewRules("t");
        s.UpdateRules(_ => { var p = PhaseFixture.Pack(s.Archive.Sha256); return tweak == null ? p : tweak(p); });
        return (s, new PhaseController(s, (c, _) => c));
    }

    private static readonly Dictionary<string, string> None = new();
    private static readonly Dictionary<string, IReadOnlyList<int>> NoRolls = new();
    private static readonly IReadOnlySet<string> RedOnly = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> BlueOnly = new HashSet<string> { "blue" };

    private static double Water(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "water") ?? 10;
    private static double? Fuel(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "fuel");
    private static double? River(ModuleSession s) => Ledger.Get(s.Game!.State, LedgerKey.Holder(HolderDef.GlobalId), "river");

    private static FormulaTrace Trace(ModuleSession s, string formulaId, string? pieceId, Dictionary<string, string>? typed = null) =>
        s.Runner!.Run(s.Game!.State, new RunRequest(formulaId, pieceId, typed ?? None, None, NoRolls, null, true));

    private static RulesPack WithPhase(RulesPack p, string id, Func<PhaseDef, PhaseDef> edit) =>
        p with { Phases = p.Phases.Select(x => x.Id == id ? edit(x) : x).ToList() };

    private static RulesPack WithProperty(RulesPack p, string id, Func<PropertyDef, PropertyDef> edit) =>
        p with { Properties = p.Properties.Select(x => x.Id == id ? edit(x) : x).ToList() };

    private static readonly FormulaDef Thirst = new()
    {
        Id = "f-thirst", Name = "Thirst", Text = "W - 1",
        Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" } },
        Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
    };

    private static readonly FormulaDef Orders = new()
    {
        Id = "f-order", Name = "Orders", Text = "N",
        Bindings = new() { new VariableBinding { Variable = "N", Kind = BindingKind.Input } },
    };

    private static readonly FormulaDef FuelCopy = new()
    {
        Id = "f-copy", Name = "Fuel check", Text = "F",
        Bindings = new() { new VariableBinding { Variable = "F", Kind = BindingKind.Unit, Property = "fuel" } },
        Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
    };

    private static readonly FormulaDef RiverRoll = new()
    {
        Id = "f-river", Name = "River level roll", Text = "R",
        Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 } },
        Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "river", Op = OutputOp.Set } },
    };

    private static RulesPack Extra(RulesPack p) => p with
    {
        Formulas = p.Formulas.Concat(new[] { Thirst, Orders, FuelCopy, RiverRoll }).ToList(),
        Properties = p.Properties.Append(new PropertyDef { Id = "river", Name = "River", Scope = PropertyScope.Holders }).ToList(),
    };

    /// <summary>Supply: Daily water for each unit, then Thirst for each unit whose water already dropped (only true after the first entry).</summary>
    private static RulesPack Ordered(RulesPack p) => WithPhase(Extra(p), "ph-supply", ph => ph with
    {
        OnStart = new()
        {
            new StartEntry { FormulaId = "f-water", ForEach = ForEachMode.Units },
            new StartEntry { FormulaId = "f-thirst", ForEach = ForEachMode.Units, UnitFilter = "Water < 10" },
        },
    });

    /// <summary>Supply: Orders (needs input, once), then Daily water for each unit.</summary>
    private static RulesPack InputFirst(RulesPack p) => WithPhase(Extra(p), "ph-supply", ph => ph with
    {
        OnStart = new()
        {
            new StartEntry { FormulaId = "f-order", ForEach = ForEachMode.Once },
            new StartEntry { FormulaId = "f-water", ForEach = ForEachMode.Units },
        },
    });

    /// <summary>Weather (no side): Red answers the River level roll, then the Weather roll runs.</summary>
    private static RulesPack Ask(RulesPack p) => WithPhase(Extra(p), "ph-weather", ph => ph with
    {
        OnStart = new()
        {
            new StartEntry { FormulaId = "f-river", ForEach = ForEachMode.Once, AskSideId = "red" },
            new StartEntry { FormulaId = "f-weather", ForEach = ForEachMode.Once },
        },
    });

    /// <summary>Applies every open unit of every pending run, first run first (a few rounds, as continued entries queue more).</summary>
    private static void AnswerAll(ModuleSession s, PhaseController c)
    {
        for (var guard = 0; guard < 20 && s.Game!.State.Pending.FirstOrDefault() is { } run; guard++)
            foreach (var u in run.Units.Where(u => !u.Applied).ToList())
                Assert.True(c.ApplyFormula(run.Id, u.PieceId, Trace(s, run.FormulaId, u.PieceId), false));
    }

    private static PhasePosition Pos(ModuleSession s) => s.Game!.State.Position!;

    // ---- strict order (#32) ----

    [Fact]
    public void Strict_order_same_result_in_all_difficulties()
    {
        foreach (var d in new[] { Difficulty.Hardcore, Difficulty.SemiAuto, Difficulty.FullyAuto })
        {
            var (s, c) = Make(d, Ordered);
            c.Start();
            AnswerAll(s, c);
            c.Advance();
            AnswerAll(s, c);
            Assert.Equal(8, Water(s, "r1"));
            Assert.Equal(8, Water(s, "r2"));
            Assert.Null(s.Game!.State.StartCursor);
        }
    }

    [Fact]
    public void SemiAuto_entry_two_units_chosen_after_answer()
    {
        var (s, c) = Make(Difficulty.SemiAuto, Ordered);
        c.Start();
        AnswerAll(s, c);
        c.Advance();
        var run = Assert.Single(s.Game!.State.Pending);
        Assert.Equal("f-water", run.FormulaId);
        Assert.Equal(new StartCursor(Pos(s), 1), s.Game.State.StartCursor);

        Assert.True(c.ApplyFormula(run.Id, "r1", Trace(s, "f-water", "r1"), false));
        Assert.Equal("f-water", Assert.Single(s.Game.State.Pending).FormulaId);   // Red 2 still waits: Thirst not picked yet
        Assert.True(c.ApplyFormula(run.Id, "r2", Trace(s, "f-water", "r2"), false));

        var next = Assert.Single(s.Game.State.Pending);
        Assert.Equal("f-thirst", next.FormulaId);
        Assert.Equal("start", next.Trigger);
        Assert.Equal(new[] { "r1", "r2" }, next.Units.Select(u => u.PieceId));
        Assert.Null(s.Game.State.StartCursor);
    }

    [Fact]
    public void FullyAuto_needs_input_stops_later_entries()
    {
        var (s, c) = Make(Difficulty.FullyAuto, InputFirst);
        c.Start();
        c.Advance();
        var run = Assert.Single(s.Game!.State.Pending);
        Assert.Equal("f-order", run.FormulaId);
        Assert.True(run.NeedsInput);
        Assert.Equal(10, Water(s, "r1"));   // Daily water waits for the answer

        Assert.True(c.ApplyFormula(run.Id, null, Trace(s, "f-order", null, new() { ["N"] = "2" }), false));
        Assert.Equal(9, Water(s, "r1"));
        Assert.Equal(9, Water(s, "r2"));
        Assert.Empty(s.Game.State.Pending);
        Assert.Null(s.Game.State.StartCursor);
    }

    [Fact]
    public void Batch_apply_all_continues_after_last_unit()
    {
        var (s, c) = Make(Difficulty.SemiAuto, Ordered);
        c.Start();
        AnswerAll(s, c);
        c.Advance();
        var run = Assert.Single(s.Game!.State.Pending);
        var (applied, failed) = MissingInputs.ApplyAll(s, c, run, None);
        Assert.Equal(2, applied);
        Assert.Empty(failed);
        var next = Assert.Single(s.Game.State.Pending);
        Assert.Equal("f-thirst", next.FormulaId);
        Assert.Equal(2, next.Units.Count);

        s.Game.Undo();   // the whole grid and what it released are one step
        Assert.Equal("f-water", Assert.Single(s.Game.State.Pending).FormulaId);
        Assert.Equal(10, Water(s, "r1"));
    }

    [Fact]
    public void Undo_answer_undoes_continued_entries()
    {
        var (s, c) = Make(Difficulty.FullyAuto, InputFirst);
        c.Start();
        c.Advance();
        var run = Assert.Single(s.Game!.State.Pending);
        var cursor = s.Game.State.StartCursor;
        Assert.NotNull(cursor);
        Assert.True(c.ApplyFormula(run.Id, null, Trace(s, "f-order", null, new() { ["N"] = "2" }), false));
        Assert.Equal(9, Water(s, "r1"));
        Assert.Equal("Orders", s.Game.UndoLabel);

        s.Game.Undo();
        Assert.Equal(10, Water(s, "r1"));
        Assert.Equal(run.Id, Assert.Single(s.Game.State.Pending).Id);
        Assert.Equal(cursor, s.Game.State.StartCursor);
    }

    [Fact]
    public void Next_phase_with_entries_left_logs_not_run()
    {
        var (s, c) = Make(Difficulty.SemiAuto, p => WithPhase(Ordered(p), "ph-supply", ph => ph with
        {
            OnStart = ph.OnStart.Append(new StartEntry { FormulaId = "f-order", ForEach = ForEachMode.Once }).ToList(),
        }));
        c.Start();
        AnswerAll(s, c);
        c.Advance();
        var mark = s.Game!.State.Log.Count;
        c.Advance();   // nothing answered
        Assert.Equal("ph-move", Pos(s).PhaseId);
        var added = s.Game.State.Log.Skip(mark).Select(l => l.Text).ToList();
        Assert.Contains("not run: Thirst, Orders", added);
        Assert.Contains("skipped: Daily water, Red 1, Red 2", added);
        Assert.True(added.IndexOf("not run: Thirst, Orders") < added.IndexOf("skipped: Daily water, Red 1, Red 2"));
        var line = s.Game.State.Log.Skip(mark).First(l => l.Text.StartsWith("not run:", StringComparison.Ordinal));
        Assert.Equal(LogKind.Calc, line.Kind);
        Assert.Equal(LogKind.NoWorking, line.FormulaId);
        Assert.Null(s.Game.State.StartCursor);
    }

    [Fact]
    public void Cursor_past_end_counts_as_done()
    {
        var (s, c) = Make(Difficulty.SemiAuto, Ordered);
        c.Start();
        AnswerAll(s, c);
        c.Advance();
        Assert.Equal(1, s.Game!.State.StartCursor!.Next);
        // A rules edit drops the second entry: the cursor now points past the end.
        s.UpdateRules(p => WithPhase(p, "ph-supply", ph => ph with { OnStart = ph.OnStart.Take(1).ToList() }));
        var mark = s.Game.State.Log.Count;
        c.Advance();
        Assert.DoesNotContain(s.Game.State.Log.Skip(mark), l => l.Text.StartsWith("not run:", StringComparison.Ordinal));

        // Answering never runs anything past the end either.
        var (s2, c2) = Make(Difficulty.SemiAuto, Ordered);
        c2.Start();
        AnswerAll(s2, c2);
        c2.Advance();
        s2.UpdateRules(p => WithPhase(p, "ph-supply", ph => ph with { OnStart = ph.OnStart.Take(1).ToList() }));
        AnswerAll(s2, c2);
        Assert.Empty(s2.Game!.State.Pending);
        Assert.Null(s2.Game.State.StartCursor);
        Assert.Equal(9, Water(s2, "r1"));
    }

    [Fact]
    public void Old_save_without_cursor_plays_as_before()
    {
        var (s, c) = Make(Difficulty.SemiAuto, Ordered);
        c.Start();
        AnswerAll(s, c);
        c.Advance();
        var g = s.Game!;
        // An old save: both start runs queued at once, no cursor.
        g.Execute(new SetStartCursor(null));
        var old = new PendingRun("old-thirst", "f-thirst", "Thirst", "start",
            new[] { new PendingUnit("r1", "Red 1", null, false) }, false);
        g.Execute(new QueueRuns(new[] { old }));
        var water = g.State.Pending.First(r => r.FormulaId == "f-water");
        foreach (var id in new[] { "r1", "r2" }) Assert.True(c.ApplyFormula(water.Id, id, Trace(s, "f-water", id), false));
        Assert.Equal("old-thirst", Assert.Single(g.State.Pending).Id);   // nothing continued, nothing doubled
        Assert.Null(g.State.StartCursor);
    }

    [Fact]
    public void StartCursor_and_ask_serialise_omitted_when_null()
    {
        var st = new GameState();
        st.Pending.Add(new PendingRun("r", "f", "F", "start", Array.Empty<PendingUnit>(), false));
        var json = JsonSerializer.Serialize(st, QuartermasterJson.Options);
        Assert.DoesNotContain("startCursor", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("askSideId", json, StringComparison.OrdinalIgnoreCase);

        st.StartCursor = new StartCursor(new PhasePosition(2, 1, "ph-supply", 1), 3);
        st.Pending[0] = st.Pending[0] with { AskSideId = "red" };
        var back = JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(st, QuartermasterJson.Options), QuartermasterJson.Options)!;
        Assert.Equal(st.StartCursor, back.StartCursor);
        Assert.Equal("red", back.Pending[0].AskSideId);

        GameCommand cmd = new SetStartCursor(st.StartCursor);
        var round = JsonSerializer.Deserialize<GameCommand>(JsonSerializer.Serialize(cmd, QuartermasterJson.Options), QuartermasterJson.Options);
        Assert.Equal(cmd, round);
        Assert.Contains("\"setStartCursor\"", JsonSerializer.Serialize(cmd, QuartermasterJson.Options));
    }

    // ---- online and hot-seat ----

    [Fact]
    public void Guest_answer_continuation_stamped_as_phase()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ask);
        s.SetViewSides(BlueOnly);   // the host holds Blue; the guest holds Red
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending);
        var mark = s.Game.Log.Count;
        using (c.ActingFor(RedOnly, "guest"))
            Assert.True(c.ApplyFormula(run.Id, null, Trace(s, "f-river", null), false));
        var added = s.Game.Log.Skip(mark).ToList();
        var river = added.OfType<LedgerDelta>().Single(d => d.PropertyId == "river");
        Assert.Equal("red", river.SideId);   // the asked side's answer
        Assert.Equal("guest", river.Who);
        var weather = added.OfType<RollDice>().Single(r => r.Purpose.Contains("Weather", StringComparison.Ordinal));
        Assert.Equal("blue", weather.SideId);   // continued as Next Phase would stamp it (the host's view), not the guest's sides
        Assert.Equal("guest", weather.Who);
    }

    [Fact]
    public void Hot_seat_continuation_raises_runs_queued()
    {
        var (s, c) = Make(Difficulty.SemiAuto, Ordered);
        c.Start();
        AnswerAll(s, c);
        s.SetViewSides(RedOnly);
        c.Advance();
        var raised = new List<IReadOnlyList<PendingRun>>();
        var labels = new List<string?>();
        c.RunsQueued += r => { raised.Add(r); labels.Add(s.Game!.UndoLabel); };
        var run = Assert.Single(s.Game!.State.Pending);
        Assert.True(c.ApplyFormula(run.Id, "r1", Trace(s, "f-water", "r1"), false));
        Assert.Empty(raised);
        Assert.True(c.ApplyFormula(run.Id, "r2", Trace(s, "f-water", "r2"), false));
        var one = Assert.Single(raised);
        Assert.Equal("f-thirst", Assert.Single(one).FormulaId);
        Assert.Equal("Daily water on Red 2", Assert.Single(labels));   // raised once the answer's action closed
    }

    // ---- ask runs (#72) ----

    [Fact]
    public void Ask_entry_queues_in_fully_auto()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ask);
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending);
        Assert.Equal("f-river", run.FormulaId);
        Assert.Equal("red", run.AskSideId);
        Assert.Null(Assert.Single(run.Units).PieceId);
        Assert.Null(River(s));
        Assert.Empty(s.Game.Log.OfType<RollDice>());   // the Weather roll after it waits
        Assert.Equal(new StartCursor(Pos(s), 1), s.Game.State.StartCursor);
    }

    [Fact]
    public void Ask_run_hidden_and_refused_for_other_side()
    {
        var (s, c) = Make(Difficulty.SemiAuto, Ask);
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending);
        Assert.Empty(PlayerView.For(s.Game.State, s.Rules, s.Model, BlueOnly).Pending);
        Assert.Equal(run.Id, Assert.Single(PlayerView.For(s.Game.State, s.Rules, s.Model, RedOnly).Pending).Id);

        var trace = Trace(s, "f-river", null);
        s.SetViewSides(BlueOnly);
        Assert.False(c.ApplyFormula(run.Id, null, trace, false));
        using (c.ActingFor(BlueOnly, "guest"))
            Assert.False(c.ApplyFormula(run.Id, null, trace, false));
        var verdict = FormulaIntentRunner.Run(s, c, BlueOnly, new FormulaIntent("f-river", null, run.Id, None, false), guest: true, who: "guest");
        Assert.False(verdict.Applied);
        Assert.Equal("That calculation belongs to a side you don't hold.", verdict.Message);
        Assert.Null(River(s));
        Assert.Single(s.Game.State.Pending);
    }

    [Fact]
    public void Ask_run_applied_by_asked_side_writes_global_stamped()
    {
        var (s, c) = Make(Difficulty.SemiAuto, Ask);
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending);
        s.SetViewSides(RedOnly);
        var mark = s.Game.Log.Count;
        Assert.True(c.ApplyFormula(run.Id, null, Trace(s, "f-river", null), false));
        Assert.Equal(3, River(s));
        var added = s.Game.Log.Skip(mark).ToList();
        Assert.Equal("red", added.OfType<RollDice>().Single().SideId);
        Assert.Equal("red", added.OfType<LedgerDelta>().Single().SideId);
        Assert.Equal("f-weather", Assert.Single(s.Game.State.Pending).FormulaId);   // the next entry runs after the answer
    }

    [Fact]
    public void Ask_run_solo_any_player()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ask);
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending);
        Assert.Null(s.ViewSides);
        Assert.True(c.ApplyFormula(run.Id, null, Trace(s, "f-river", null), false));
        Assert.Equal(3, River(s));
        Assert.Equal("red", s.Game.Log.OfType<LedgerDelta>().Single(d => d.PropertyId == "river").SideId);   // stamped for the asked side, as always
        Assert.Empty(s.Game.State.Pending);   // Weather roll applied at once (Fully automatic)
    }

    [Fact]
    public void Ask_decision_text()
    {
        var (s, c) = Make(Difficulty.SemiAuto, Ask);
        c.Start();
        var d = Assert.Single(Decisions.Compute(s.Rules!, s.Game!.State), x => x.Kind == DecisionKind.Pending);
        Assert.Equal("River level roll — Red answers", d.Text);
    }

    // ---- reset at phase entry (#56) ----

    private static void SetFuel(ModuleSession s, string id, double v) =>
        s.Game!.Execute(new LedgerDelta(LedgerKey.Piece(id), "fuel", Fuel(s, id) ?? 40, v, "manual", null, null, "test", null));

    [Fact]
    public void Reset_at_phase_entry_then_reseeds_then_entries()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => WithPhase(WithProperty(Extra(p), "fuel", x => x with { ResetAt = new() { "ph-supply" } }),
            "ph-supply", ph => ph with { OnStart = new() { new StartEntry { FormulaId = "f-copy", ForEach = ForEachMode.Units } } }));
        c.Start();
        SetFuel(s, "r1", 5);
        var mark = s.Game!.Log.Count;
        c.Advance();
        Assert.Equal(40, Fuel(s, "r1"));
        Assert.Equal(40, Water(s, "r1"));   // the entry read the re-seeded value
        var added = s.Game.Log.Skip(mark).ToList();
        var adv = added.FindIndex(x => x is AdvancePhase);
        var reset = added.FindIndex(x => x is ResetProperties);
        var seed = added.FindIndex(x => x is SeedLedger);
        var entry = added.FindIndex(x => x is LedgerDelta { Source: "formula" });
        Assert.True(adv < reset && reset < seed && seed < entry, $"{adv} {reset} {seed} {entry}");
        var rp = (ResetProperties)added[reset];
        Assert.Equal(new[] { "fuel" }, rp.PropertyIds);
        Assert.Contains(s.Game.State.Log, l => l.Text == "Supply: reset Fuel" && l.Kind == LogKind.System);
    }

    [Fact]
    public void Reset_one_undo_with_next_phase()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => WithProperty(p, "fuel", x => x with { ResetAt = new() { "ph-supply" } }));
        c.Start();
        SetFuel(s, "r1", 5);
        c.Advance();
        Assert.Equal(40, Fuel(s, "r1"));
        Assert.Equal("Next Phase", s.Game!.UndoLabel);
        s.Game.Undo();
        Assert.Equal(5, Fuel(s, "r1"));
        Assert.Equal("ph-weather", Pos(s).PhaseId);
    }

    [Fact]
    public void Skipped_phase_does_not_reset()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => WithPhase(WithProperty(p, "fuel", x => x with { ResetAt = new() { "ph-supply" } }),
            "ph-supply", ph => ph with { Condition = "1 > 2" }));
        c.Start();
        SetFuel(s, "r1", 5);
        c.Advance();
        Assert.Equal("ph-move", Pos(s).PhaseId);
        Assert.Equal(5, Fuel(s, "r1"));
        Assert.DoesNotContain(s.Game!.Log, x => x is ResetProperties);
    }

    [Fact]
    public void Start_resets_first_phase()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => WithProperty(p, "fuel", x => x with { ResetAt = new() { "ph-weather" } }));
        SetFuel(s, "r1", 5);
        c.Start();
        Assert.Equal(40, Fuel(s, "r1"));
        Assert.Contains(s.Game!.State.Log, l => l.Text == "Weather: reset Fuel");
        Assert.Equal("Next Phase", s.Game.UndoLabel);
    }

    [Fact]
    public void Block_repeat_resets_each_pass()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => WithProperty(p, "fuel", x => x with { ResetAt = new() { "ph-move" } }));
        c.Start();
        c.Advance();
        c.Advance();   // Movement, round 1
        SetFuel(s, "r1", 5);
        c.Advance();   // Combat
        Assert.Equal(5, Fuel(s, "r1"));
        c.Advance();   // Movement, round 2
        Assert.Equal(2, Pos(s).Repeat);
        Assert.Equal(40, Fuel(s, "r1"));
        Assert.Equal(2, s.Game!.Log.OfType<ResetProperties>().Count());
    }

    // ---- run stamps for runs without outputs (#71, carried from Task 2) ----

    [Fact]
    public void NoOutput_phase_run_counts_once()
    {
        var (s, c) = Make(Difficulty.FullyAuto);
        c.Start();   // Weather roll: applied automatically, no outputs
        Assert.Single(s.Game!.State.Log, l => l.AppliedRun == "f-weather");
    }

    [Fact]
    public void NoOutput_manual_run_counts_once_output_run_not_twice()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        c.Start();
        Assert.True(c.ApplyFormula(null, null, Trace(s, "f-weather", null), false));
        Assert.Single(s.Game!.State.Log, l => l.AppliedRun == "f-weather");
        Assert.True(c.ApplyFormula(null, "r1", Trace(s, "f-water", "r1"), false));
        Assert.Single(s.Game.State.Log, l => l.AppliedRun == "f-water");
    }
}
