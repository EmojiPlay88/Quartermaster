using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B5 #6 / #7 in the phase controller: the repeat choice at blocks that repeat until a player stops, one level of nesting, and sides that
/// react in another side's phase. Invented PhaseFixture pack (Red, Blue; Weather, Supply, Movement, Combat, End); temp roots only.
/// </summary>
public class TurnStructureControllerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-turns-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public TurnStructureControllerTests() => Directory.CreateDirectory(_tmp);

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

    private static readonly IReadOnlySet<string> RedOnly = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> BlueOnly = new HashSet<string> { "blue" };

    private static PhasePosition Pos(ModuleSession s) => s.Game!.State.Position!;
    private static double Water(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "water") ?? 10;
    private static double? Fuel(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "fuel");
    private static double? River(ModuleSession s) => Ledger.Get(s.Game!.State, LedgerKey.Holder(HolderDef.GlobalId), "river");

    private static void SetFuel(ModuleSession s, string id, double v) =>
        s.Game!.Execute(new LedgerDelta(LedgerKey.Piece(id), "fuel", Fuel(s, id) ?? 40, v, "manual", null, null, "test", null));

    private static void SetRiver(ModuleSession s, double v) =>
        s.Game!.Execute(new LedgerDelta(LedgerKey.Holder(HolderDef.GlobalId), "river", River(s) ?? 0, v, "manual", null, null, "test", null));

    private static RulesPack WithPhase(RulesPack p, string id, Func<PhaseDef, PhaseDef> edit) =>
        p with { Phases = p.Phases.Select(x => x.Id == id ? edit(x) : x).ToList() };

    /// <summary>The Round block (Movement–Combat) repeats until a player stops.</summary>
    private static RulesPack Open(RulesPack p) =>
        p with { Blocks = new() { new PhaseBlock { FirstPhaseId = "ph-move", LastPhaseId = "ph-combat", Count = 0, Label = "Round" } } };

    /// <summary>Day (Movement–Combat, 2) holding an open-ended Fire round over Combat.</summary>
    private static RulesPack Nested(RulesPack p) => p with
    {
        Blocks = new()
        {
            new PhaseBlock { FirstPhaseId = "ph-move", LastPhaseId = "ph-combat", Count = 2, Label = "Day" },
            new PhaseBlock { FirstPhaseId = "ph-combat", LastPhaseId = "ph-combat", Count = 0, Label = "Fire round" },
        },
    };

    private static RulesPack WithRiver(RulesPack p) =>
        p with { Properties = p.Properties.Append(new PropertyDef { Id = "river", Name = "River", Scope = PropertyScope.Holders, Default = 0 }).ToList() };

    private static readonly FormulaDef Segment = new()
    {
        Id = "f-seg", Name = "Round count", Text = "segment()",
        Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "river", Op = OutputOp.Set } },
    };

    private static readonly StartEntry WeatherOnce = new() { FormulaId = "f-weather", ForEach = ForEachMode.Once };

    /// <summary>Start, then Next Phase until Combat (Weather, Supply, Movement, Combat).</summary>
    private static void ToCombat(PhaseController c)
    {
        c.Start();
        c.Advance();
        c.Advance();
        c.Advance();
    }

    // ---- the question ----

    [Fact]
    public void View_asks_repeat_at_open_block_last_phase()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Open);
        c.Start();
        c.Advance();
        c.Advance();   // Movement: not the block's last phase
        Assert.Null(c.View().RepeatAsk);
        c.Advance();   // Combat
        Assert.Equal("ph-combat", Pos(s).PhaseId);
        Assert.Equal(new RepeatAsk("Round", 1), c.View().RepeatAsk);
        Assert.Equal("Round 1", c.View().StageText);
    }

    [Fact]
    public void View_no_ask_with_end_entries_pending()
    {
        var (_, c) = Make(Difficulty.SemiAuto, p => WithPhase(Open(p), "ph-combat", x => x with { OnEnd = new() { WeatherOnce } }));
        ToCombat(c);
        Assert.Null(c.View().RepeatAsk);   // the next press runs the end entries first
    }

    [Fact]
    public void View_asks_after_end_entries_ran()
    {
        var (s, c) = Make(Difficulty.SemiAuto, p => WithPhase(Open(p), "ph-combat", x => x with { OnEnd = new() { WeatherOnce } }));
        ToCombat(c);
        c.Advance();   // runs the end entries; their run is queued, so the press stops
        Assert.Equal("ph-combat", Pos(s).PhaseId);
        Assert.Equal(Pos(s), s.Game!.State.EndedAt);
        Assert.Equal(new RepeatAsk("Round", 1), c.View().RepeatAsk);
    }

    [Fact]
    public void Advance_end_entries_at_open_block_stop_for_the_choice()
    {
        // Fully automatic: the end entries apply at once; without a stop the player could never choose to repeat.
        var (s, c) = Make(Difficulty.FullyAuto, p => WithPhase(Open(p), "ph-combat", x => x with { OnEnd = new() { WeatherOnce } }));
        ToCombat(c);
        var boundaries = 0;
        c.PhaseBoundary += () => boundaries++;
        Assert.Null(c.View().RepeatAsk);
        c.Advance();
        Assert.Equal("ph-combat", Pos(s).PhaseId);
        Assert.Equal(0, boundaries);
        Assert.Equal(new RepeatAsk("Round", 1), c.View().RepeatAsk);
        c.Advance(true);   // the end entries do not run again
        Assert.Equal(("ph-move", 2), (Pos(s).PhaseId, Pos(s).Repeat));
        Assert.Single(s.Game!.Log.OfType<EndPhase>());
    }

    [Fact]
    public void Advance_repeat_with_end_entries_not_yet_run_runs_them_and_repeats()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => WithPhase(Open(p), "ph-combat", x => x with { OnEnd = new() { WeatherOnce } }));
        ToCombat(c);
        c.Advance(true);   // a caller that already chose (the harness): one press
        Assert.Equal(("ph-move", 2), (Pos(s).PhaseId, Pos(s).Repeat));
    }

    [Fact]
    public void View_no_ask_for_other_sides_viewer()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => WithPhase(Open(p), "ph-combat", x => x with { SideId = "red" }));
        ToCombat(c);
        s.SetViewSides(BlueOnly);
        Assert.NotNull(c.View().PlayingText);
        Assert.Null(c.View().RepeatAsk);
        s.SetViewSides(RedOnly);
        Assert.Equal(new RepeatAsk("Round", 1), c.View().RepeatAsk);
    }

    // ---- advancing with the choice ----

    [Fact]
    public void Advance_repeat_goes_back_with_repeat_plus_one()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Open);
        ToCombat(c);
        c.Advance(true);
        Assert.Equal(new PhasePosition(1, 2, "ph-move", 2), Pos(s));
        Assert.Equal("Round 2", c.View().StageText);
        c.Advance();
        c.Advance(true);
        Assert.Equal(new PhasePosition(1, 3, "ph-move", 2), Pos(s));
        Assert.Contains("Turn 1 · Combat → Movement", s.Game!.State.Log.Select(l => l.Text));
    }

    [Fact]
    public void Advance_continue_leaves_block()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Open);
        ToCombat(c);
        c.Advance(false);
        Assert.Equal(new PhasePosition(1, 1, "ph-end", 4), Pos(s));
        Assert.Null(c.View().StageText);
        c.Advance();
        Assert.Equal(new PhasePosition(2, 1, "ph-weather", 0), Pos(s));
    }

    [Fact]
    public void Advance_repeat_one_undo()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Open);
        ToCombat(c);
        var at = Pos(s);
        c.Advance(true);
        Assert.Equal("Next Phase", s.Game!.UndoLabel);
        s.Game.Undo();
        Assert.Equal(at, Pos(s));
        Assert.Equal(new RepeatAsk("Round", 1), c.View().RepeatAsk);
    }

    [Fact]
    public void Advance_repeat_resets_and_runs_start_entries()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p =>
        {
            var q = WithPhase(Open(p), "ph-move", x => x with { OnStart = new() { new StartEntry { FormulaId = "f-water", ForEach = ForEachMode.Units } } });
            return q with { Properties = q.Properties.Select(x => x.Id == "fuel" ? x with { ResetAt = new() { "ph-move" } } : x).ToList() };
        });
        ToCombat(c);
        Assert.Equal(8, Water(s, "r1"));   // Supply and Movement round 1
        SetFuel(s, "r1", 5);
        c.Advance(true);
        Assert.Equal(40, Fuel(s, "r1"));   // reset at the block's first phase on the repeat
        Assert.Equal(7, Water(s, "r1"));   // and its start entries ran again
        c.Advance();
        SetFuel(s, "r1", 5);
        c.Advance(true);
        Assert.Equal(40, Fuel(s, "r1"));
        Assert.Equal(6, Water(s, "r1"));
    }

    [Fact]
    public void Inner_repeat_segment_in_formula()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => WithPhase(WithRiver(Nested(p)) with { Formulas = p.Formulas.Append(Segment).ToList() },
            "ph-combat", x => x with { OnStart = new() { new StartEntry { FormulaId = "f-seg", ForEach = ForEachMode.Once } } }));
        ToCombat(c);
        Assert.Equal(new PhasePosition(1, 1, "ph-combat", 3) { Inner = 1 }, Pos(s));
        Assert.Equal(1, River(s));
        Assert.Equal("Day 1 of 2, Fire round 1", c.View().StageText);
        Assert.Equal(new RepeatAsk("Fire round", 1), c.View().RepeatAsk);

        c.Advance(true);
        Assert.Equal(new PhasePosition(1, 1, "ph-combat", 3) { Inner = 2 }, Pos(s));
        Assert.Equal(2, River(s));
        Assert.Equal("Day 1 of 2, Fire round 2", c.View().StageText);
        Assert.Equal(new RepeatAsk("Fire round", 2), c.View().RepeatAsk);

        c.Advance(false);   // the fire rounds stop; the fixed Day repeats
        Assert.Equal(new PhasePosition(1, 2, "ph-move", 2), Pos(s));
        Assert.Equal("Day 2 of 2", c.View().StageText);
        c.Advance();
        Assert.Equal(new PhasePosition(1, 2, "ph-combat", 3) { Inner = 1 }, Pos(s));
        Assert.Equal(1, River(s));
    }

    [Fact]
    public void Skipped_chips_follow_repeats_of_open_block()
    {
        // Movement is skipped in round 1 (River 0) and runs in round 2: its chip shows the latest pass (done), not the earlier skip.
        var (s, c) = Make(Difficulty.FullyAuto, p => WithPhase(WithRiver(Open(p)), "ph-move", x => x with { Condition = "River > 0" }));
        c.Start();
        c.Advance();
        c.Advance();   // Movement skipped: Combat, round 1
        Assert.Equal(new PhasePosition(1, 1, "ph-combat", 3), Pos(s));
        Assert.Equal(ChipState.Skipped, c.View().Chips.Single(x => x.Id == "ph-move").State);
        SetRiver(s, 1);
        c.Advance(true);   // Movement, round 2
        c.Advance();       // Combat, round 2
        Assert.Equal(new PhasePosition(1, 2, "ph-combat", 3), Pos(s));
        Assert.Equal(ChipState.Done, c.View().Chips.Single(x => x.Id == "ph-move").State);
    }

    [Fact]
    public void Skipped_chips_follow_inner_repeats()
    {
        // Day (2) over Movement–Combat; an open Fire round over Combat. Supply is skipped every pass; Movement runs.
        var (s, c) = Make(Difficulty.FullyAuto, p => WithPhase(Nested(p), "ph-supply", x => x with { Condition = "turn() > 5" }));
        c.Start();
        c.Advance();       // Movement (Supply skipped)
        c.Advance();       // Combat, fire round 1
        c.Advance(true);   // fire round 2
        c.Advance(true);   // fire round 3
        Assert.Equal(new PhasePosition(1, 1, "ph-combat", 3) { Inner = 3 }, Pos(s));
        var chips = c.View().Chips;
        Assert.Equal(ChipState.Skipped, chips.Single(x => x.Id == "ph-supply").State);
        Assert.Equal(ChipState.Done, chips.Single(x => x.Id == "ph-move").State);
        Assert.Equal(ChipState.Current, chips.Single(x => x.Id == "ph-combat").State);
    }

    [Fact]
    public void Fixed_pack_advance_unchanged()
    {
        // Captured from the fixed-block walk before this change (the Task 3 sequencer test pins it to master): log and positions.
        var (s, c) = Make(Difficulty.FullyAuto);
        var positions = new List<PhasePosition>();
        c.Start();
        positions.Add(Pos(s));
        for (var i = 0; i < 40 && Pos(s).Turn < 3; i++) { c.Advance(); positions.Add(Pos(s)); }
        var expected = new[]
        {
            "system|Setup \"Start\" loaded: 3 pieces",
            "turn|Turn 1 · Weather",
            "dice|Weather roll: R: 1d6 = 3",
            "calc|Weather roll: 3 = 3 (auto)",
            "turn|Turn 1 · Weather → Supply",
            "calc|Red 1: 10 − 1 = 9 → Water = 9 (auto)",
            "calc|Red 2: 10 − 1 = 9 → Water = 9 (auto)",
            "calc|Daily water: 2 units, 2 applied",
            "turn|Turn 1 · Supply → Movement",
            "turn|Turn 1 · Movement → Combat",
            "turn|Turn 1 · Combat → Movement",
            "turn|Turn 1 · Movement → Combat",
            "turn|Turn 1 · Combat → End",
            "turn|Turn 2 · End → Weather",
            "dice|Weather roll: R: 1d6 = 3",
            "calc|Weather roll: 3 = 3 (auto)",
            "turn|Turn 2 · Weather → Supply",
            "calc|Red 1: 9 − 1 = 8 → Water = 8 (auto)",
            "calc|Red 2: 9 − 1 = 8 → Water = 8 (auto)",
            "calc|Daily water: 2 units, 2 applied",
            "turn|Turn 2 · Supply → Movement",
            "turn|Turn 2 · Movement → Combat",
            "turn|Turn 2 · Combat → Movement",
            "turn|Turn 2 · Movement → Combat",
            "turn|Turn 2 · Combat → End",
            "turn|Turn 3 · End → Weather",
            "dice|Weather roll: R: 1d6 = 3",
            "calc|Weather roll: 3 = 3 (auto)",
        };
        Assert.Equal(expected, s.Game!.State.Log.Select(l => $"{l.Kind}|{l.Text}"));
        string[] walk = { "ph-weather", "ph-supply", "ph-move", "ph-combat", "ph-move", "ph-combat", "ph-end" };
        int[] repeats = { 1, 1, 1, 1, 2, 2, 1 };
        var want = new List<PhasePosition>();
        for (var t = 1; t <= 2; t++)
            for (var i = 0; i < walk.Length; i++)
                want.Add(new PhasePosition(t, repeats[i], walk[i], new[] { 0, 1, 2, 3, 2, 3, 4 }[i]));
        want.Add(new PhasePosition(3, 1, "ph-weather", 0));
        Assert.Equal(want, positions);
        Assert.All(positions, p => Assert.Equal(0, p.Inner));
    }

    [Fact]
    public void Harness_advance_repeat()
    {
        using var fx = PhaseFixture.Module(true);
        var vmod = fx.Build();
        string sha;
        using (var a = Quartermaster.Vassal.VmodArchive.Open(vmod)) sha = a.Sha256;
        var rules = Path.Combine(_tmp, "p.qrules");
        RulesPackIo.Save(Open(PhaseFixture.Pack(sha)), rules);
        using var h = PackHarness.Open(vmod, rules, Path.Combine(_tmp, "harness-cache"));
        h.NewGame(null, Difficulty.FullyAuto);
        h.Advance();   // Weather
        h.Advance();
        h.Advance();
        h.Advance();   // Combat
        Assert.Equal("Combat", h.Phase);
        h.Advance(true);
        Assert.Equal("Movement", h.Phase);
        Assert.Equal(2, h.Session.Game!.State.Position!.Repeat);
        h.Advance();
        h.Advance(false);
        Assert.Equal("End", h.Phase);
        Assert.Null(h.GameOver);
    }

    // ---- reacting sides ----

    private static RulesPack Reacting(RulesPack p) => WithPhase(p, "ph-supply", x => x with { Reactors = new() { "blue", "ghost" } });

    [Fact]
    public void Reactor_viewer_playing_text_you_may_react()
    {
        var (s, c) = Make(Difficulty.SemiAuto, Reacting);
        c.Start();
        c.Advance();   // Supply (Red), its water run queued
        s.SetViewSides(BlueOnly);
        var v = c.View();
        Assert.Equal("Red is playing Supply; you may react", v.PlayingText);
        Assert.Empty(v.Decisions);
        Assert.Empty(v.Warnings);
        Assert.Equal(new[] { "Blue" }, v.Reactors);
        Assert.False(PhaseTexts.Panel(v, "blue").NextEnabled);
        Assert.True(PhaseTexts.Panel(v, "blue").ChecklistHidden);
    }

    [Fact]
    public void Guest_reactor_viewer_playing_text_you_may_react()
    {
        var (host, c) = Make(Difficulty.FullyAuto, Reacting);
        c.Start();
        c.Advance();   // Supply (Red)
        var (guest, gc) = Make(Difficulty.FullyAuto, Reacting);
        guest.EnterGuestMode();
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, BlueOnly), BlueOnly);
        Assert.Equal("Red is playing Supply; you may react", gc.View().PlayingText);
        Assert.Null(gc.View().RepeatAsk);
    }

    [Fact]
    public void View_lists_reactors()
    {
        var (_, c) = Make(Difficulty.FullyAuto, Reacting);
        c.Start();
        Assert.Empty(c.View().Reactors);   // Weather has none
        Assert.Null(PhaseTexts.ReactingText(c.View()));
        c.Advance();
        var v = c.View();
        Assert.Equal(new[] { "Blue" }, v.Reactors);   // unknown ids are left out
        Assert.Null(v.PlayingText);                    // solo plays every side
        Assert.Equal("Reacting: Blue", PhaseTexts.ReactingText(v));
    }

    [Fact]
    public void ActorSideFor_prefers_reacting_side()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p =>
        {
            var q = p with { Sides = p.Sides.Append(new SideDef { Id = "green", Name = "Green", UnitFilter = "false" }).ToList() };
            return WithPhase(q, "ph-supply", x => x with { Reactors = new() { "green" } });
        });
        c.Start();
        Assert.Equal("blue", s.ActorSideFor(new HashSet<string> { "blue", "green" }));   // Weather: no reactors, pack order
        c.Advance();   // Supply (Red), Green reacts
        Assert.Equal("green", s.ActorSideFor(new HashSet<string> { "blue", "green" }));
        Assert.Equal("red", s.ActorSideFor(new HashSet<string> { "red", "green" }));     // the acting side first
        Assert.Equal("blue", s.ActorSideFor(BlueOnly));
    }

    [Fact]
    public void Non_reactor_viewer_unchanged()
    {
        var (s, c) = Make(Difficulty.FullyAuto, p => WithPhase(p, "ph-supply", x => x with { Reactors = new() { "ghost" } }));
        c.Start();
        c.Advance();   // Supply (Red)
        s.SetViewSides(BlueOnly);
        var v = c.View();
        Assert.Equal("Red is playing Supply", v.PlayingText);
        Assert.Empty(v.Reactors);
        s.SetViewSides(RedOnly);
        Assert.Null(c.View().PlayingText);
    }
}
