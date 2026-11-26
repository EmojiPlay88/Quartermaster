namespace Quartermaster.Rules.Tests;

public class TurnStructureValidatorTests
{
    private static PhaseDef Ph(string id, string? side = null) => new() { Id = id, Name = id.ToUpperInvariant(), SideId = side };
    private static PhaseBlock Blk(string first, string last, int count, string label) =>
        new() { FirstPhaseId = first, LastPhaseId = last, Count = count, Label = label };

    private static RulesPack Base() => new()
    {
        Id = "pk", Name = "Riverland",
        Properties = new()
        {
            new PropertyDef { Id = "p1", Name = "Supply", Scope = PropertyScope.Holders },
            new PropertyDef { Id = "p2", Name = "Strength", Scope = PropertyScope.Pieces },
        },
        Sides = new() { new SideDef { Id = "north", Name = "Northern League", UnitFilter = "has(Strength)" }, new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "has(Strength)" } },
        Phases = new() { Ph("a", "north"), Ph("b", "south"), Ph("c", "north") },
        Formulas = new() { new FormulaDef { Id = "f1", Name = "Muster", Text = "1" } },
    };

    private static List<RulesIssue> V(RulesPack p, params string[] maps) =>
        RulesValidator.Validate(p, maps.Length == 0 ? null : maps).ToList();
    private static List<string> Errs(RulesPack p, params string[] maps) =>
        V(p, maps).Where(i => i.Severity == RulesIssueSeverity.Error).Select(i => i.Message).ToList();
    private static List<string> Warns(RulesPack p, params string[] maps) =>
        V(p, maps).Where(i => i.Severity == RulesIssueSeverity.Warning).Select(i => i.Message).ToList();

    // ---- blocks ----

    [Fact] public void Count_zero_is_valid() =>
        Assert.Empty(V(Base() with { Blocks = new() { Blk("a", "b", 0, "Round") } }));

    [Fact] public void Negative_count_error() =>
        Assert.Contains("Block \"Round\": count cannot be negative", Errs(Base() with { Blocks = new() { Blk("a", "b", -1, "Round") } }));

    [Fact] public void Nested_one_level_ok() =>
        Assert.Empty(V(Base() with { Blocks = new() { Blk("a", "c", 2, "Outer"), Blk("b", "c", 3, "Inner") } }));

    [Fact] public void Nested_two_levels_error()
    {
        var p = Base() with { Phases = new() { Ph("a"), Ph("b"), Ph("c"), Ph("d") },
            Blocks = new() { Blk("a", "d", 2, "Outer"), Blk("a", "c", 2, "Mid"), Blk("b", "b", 2, "Core") } };
        var errs = Errs(p);
        Assert.Contains("Block \"Core\" is inside block \"Mid\", which is itself inside another block; blocks nest one level only", errs);
        Assert.DoesNotContain(errs, m => m.Contains("\"Mid\" is inside"));
    }

    [Fact] public void Same_phases_error() =>
        Assert.Contains("Block \"One\" covers the same phases as block \"Two\"",
            Errs(Base() with { Blocks = new() { Blk("a", "b", 2, "One"), Blk("a", "b", 3, "Two") } }));

    [Fact] public void Partial_overlap_still_error() =>
        Assert.Contains("Block \"One\" overlaps block \"Two\"", Errs(Base() with { Blocks = new() { Blk("a", "b", 2, "One"), Blk("b", "c", 2, "Two") } }));

    [Fact] public void Two_open_blocks_same_last_phase_error() =>
        Assert.Contains("Blocks \"Outer\" and \"Inner\" both end at phase \"C\" and both repeat until a player stops; give one of them a count",
            Errs(Base() with { Blocks = new() { Blk("a", "c", 0, "Outer"), Blk("b", "c", 0, "Inner") } }));

    [Fact] public void Open_inner_in_fixed_outer_same_last_ok() =>
        Assert.Empty(V(Base() with { Blocks = new() { Blk("a", "c", 2, "Outer"), Blk("b", "c", 0, "Inner") } }));

    // ---- reactors ----

    [Fact] public void Reactor_unknown_side() =>
        Assert.Contains("Phase \"A\": reacting side \"west\" does not exist",
            Errs(Base() with { Phases = new() { Ph("a", "north") with { Reactors = new() { "west" } } } }));

    [Fact] public void Reactors_on_sideless_phase_warns() =>
        Assert.Contains("Phase \"A\" has reacting sides but no side of its own; every side may already act in it",
            Warns(Base() with { Phases = new() { Ph("a") with { Reactors = new() { "south" } } } }));

    [Fact] public void Own_side_as_reactor_warns() =>
        Assert.Contains("Phase \"A\" lists its own side as reacting",
            Warns(Base() with { Phases = new() { Ph("a", "north") with { Reactors = new() { "north", "south" } } } }));

    // ---- game end ----

    [Fact] public void Last_turn_below_one() => Assert.Contains("The last turn must be at least 1", Errs(Base() with { LastTurn = 0 }));
    [Fact] public void Last_stage_below_one() => Assert.Contains("The last stage must be at least 1", Errs(Base() with { LastTurn = 3, LastStage = 0 }));
    [Fact] public void Last_stage_without_turn() => Assert.Contains("A last stage needs a last turn", Errs(Base() with { LastStage = 2 }));

    [Fact] public void Last_stage_without_blocks_warns()
    {
        Assert.Contains("The last stage is set but no block repeats", Warns(Base() with { LastTurn = 3, LastStage = 2 }));
        Assert.DoesNotContain("The last stage is set but no block repeats",
            Warns(Base() with { LastTurn = 3, LastStage = 2, Blocks = new() { Blk("a", "b", 2, "Round") } }));
    }

    [Fact] public void Last_stage_with_only_broken_blocks_warns()
    {
        // A negative count or a block whose phases are missing or reversed does not repeat: only valid blocks count.
        foreach (var b in new[] { Blk("a", "b", -1, "Round"), Blk("a", "zz", 2, "Round"), Blk("c", "a", 2, "Round") })
            Assert.Contains("The last stage is set but no block repeats", Warns(Base() with { LastTurn = 3, LastStage = 2, Blocks = new() { b } }));
        Assert.DoesNotContain("The last stage is set but no block repeats",
            Warns(Base() with { LastTurn = 3, LastStage = 2, Blocks = new() { Blk("a", "b", 0, "Round") } }));   // count 0 repeats
    }

    private static VictoryCondition Vc(string id, string cond = "Supply > 3") => new() { Id = id, Condition = cond };
    private static RulesPack WithVictory(params VictoryCondition[] v) => Base() with { Victory = v.ToList() };

    [Fact] public void Victory_no_id() => Assert.Contains("Victory condition 1: has no id", Errs(WithVictory(Vc(""))));
    [Fact] public void Victory_duplicate_id() => Assert.Contains("Victory condition 2: id \"v\" is used twice", Errs(WithVictory(Vc("v"), Vc("v"))));
    [Fact] public void Victory_empty_condition() => Assert.Contains("Victory condition 1: has no condition", Errs(WithVictory(Vc("v", " "))));

    [Fact] public void Victory_condition_parse_error()
    {
        var m = Assert.Single(Errs(WithVictory(Vc("v", "Supply > >"))));
        Assert.StartsWith("Victory condition 1: condition ", m);
    }

    [Fact] public void Victory_condition_reads_piece_function() =>
        Assert.Contains("Victory condition 1: condition may not use name()", Errs(WithVictory(Vc("v", "name() == \"x\""))));

    [Fact] public void Victory_unknown_side() =>
        Assert.Contains("Victory condition 1: side \"west\" does not exist", Errs(WithVictory(Vc("v") with { SideId = "west" })));

    [Fact] public void Victory_unknown_phase() =>
        Assert.Contains("Victory condition 1: phase \"zz\" does not exist", Errs(WithVictory(Vc("v") with { PhaseIds = new() { "a", "zz" } })));

    [Fact] public void Victory_segment_allowed() =>
        Assert.Empty(V(WithVictory(Vc("v", "segment() > 2 and turn() > 1") with { SideId = "north", PhaseIds = new() { "a" } })));

    // ---- schedule ----

    private static ScheduleRow Row(string id = "r") => new()
    {
        Id = id, Turn = 2, PhaseId = "a", Piece = "has(Strength)", ZoneMapId = "reserve",
    };
    private static RulesPack WithRows(params ScheduleRow[] rows) => Base() with { Schedule = rows.ToList() };
    private static List<string> RowErrs(ScheduleRow r, params string[] maps) => Errs(WithRows(r), maps.Length == 0 ? new[] { "reserve" } : maps);

    [Fact] public void Schedule_no_id() => Assert.Contains("Schedule row 1: has no id", RowErrs(Row("")));
    [Fact] public void Schedule_duplicate_id() => Assert.Contains("Schedule row 2: id \"r\" is used twice", Errs(WithRows(Row(), Row()), "reserve"));
    [Fact] public void Schedule_turn_below_one() => Assert.Contains("Schedule row 1: turn must be at least 1", RowErrs(Row() with { Turn = 0 }));
    [Fact] public void Schedule_stage_below_one() => Assert.Contains("Schedule row 1: stage must be at least 1", RowErrs(Row() with { Stage = 0 }));
    [Fact] public void Schedule_no_phase() => Assert.Contains("Schedule row 1: has no phase", RowErrs(Row() with { PhaseId = "" }));
    [Fact] public void Schedule_unknown_phase() => Assert.Contains("Schedule row 1: phase \"zz\" does not exist", RowErrs(Row() with { PhaseId = "zz" }));

    [Fact] public void Schedule_stage_outside_block_warns()
    {
        Assert.Contains("Schedule row 1: has a stage, but phase \"C\" is not in a block",
            Warns(WithRows(Row() with { Stage = 1, PhaseId = "c" }) with { Blocks = new() { Blk("a", "b", 2, "Round") } }, "reserve"));
        Assert.DoesNotContain(Warns(WithRows(Row() with { Stage = 1, PhaseId = "a" }) with { Blocks = new() { Blk("a", "b", 2, "Round") } }, "reserve"),
            m => m.Contains("is not in a block"));
    }

    [Fact] public void Schedule_no_filter() => Assert.Contains("Schedule row 1: has no counter filter", RowErrs(Row() with { Piece = " " }));

    [Fact] public void Schedule_filter_parse_error() =>
        Assert.Contains(RowErrs(Row() with { Piece = "Strength > >" }), m => m.StartsWith("Schedule row 1: counters ") && !m.Contains("unknown"));

    [Fact] public void Schedule_filter_unknown_property() =>
        Assert.Contains("Schedule row 1: counters refers to unknown property \"Morale\"", RowErrs(Row() with { Piece = "Morale > 1" }));

    [Fact] public void Schedule_filter_runs_refused()
    {
        Assert.Equal(new[] { "Schedule row 1: counters runs() is not available in a schedule row" }, RowErrs(Row() with { Piece = "runs(\"f1\") == 0" }));
        Assert.Equal(new[] { "Schedule row 1: counters row() is not available in a schedule row" }, RowErrs(Row() with { Piece = "row() == 1" }));
        Assert.Equal(new[] { "Schedule row 1: counters dice() is not available in a schedule row" }, RowErrs(Row() with { Piece = "dice(1, 6) > 3" }));
    }

    [Fact] public void Schedule_arrive_needs_zone() =>
        Assert.Contains("Schedule row 1: needs a zone map to arrive from", RowErrs(Row() with { ZoneMapId = null }));

    [Fact] public void Schedule_return_needs_zone()
    {
        Assert.Contains("Schedule row 1: needs a zone map to arrive from", RowErrs(Row() with { Action = ScheduleAction.Return, ZoneMapId = null }));
        Assert.DoesNotContain("Schedule row 1: needs a zone map to arrive from", RowErrs(Row() with { Action = ScheduleAction.Return }));
    }

    [Fact] public void Schedule_unknown_zone_map() =>
        Assert.Contains("Schedule row 1: zone map \"reserve\" was not found in the module", RowErrs(Row(), "other"));

    [Fact] public void Schedule_unknown_formula() =>
        Assert.Contains("Schedule row 1: formula \"zz\" does not exist", RowErrs(Row() with { FormulaId = "zz" }));

    [Fact] public void Schedule_delete_with_formula() =>
        Assert.Contains("Schedule row 1: deletes its counters, so it cannot run a formula on them; give it a zone map",
            RowErrs(Row() with { Action = ScheduleAction.Leave, ZoneMapId = null, FormulaId = "f1" }));

    [Fact] public void Schedule_after_last_turn_warns() =>
        Assert.Contains("Schedule row 1: comes after the last turn", Warns(WithRows(Row() with { Turn = 5 }) with { LastTurn = 4 }, "reserve"));

    [Fact] public void Schedule_issue_id_is_row_id_or_pack_id()
    {
        Assert.Equal("r", Assert.Single(V(WithRows(Row() with { Turn = 0 }), "reserve")).EntityId);
        Assert.Equal("pk", Assert.Single(V(WithRows(Row("") with { Turn = 1 }), "reserve")).EntityId);
    }

    [Fact] public void Valid_b5_pack_has_no_issues()
    {
        var p = Base() with
        {
            Phases = new() { Ph("a", "north") with { Reactors = new() { "south" } }, Ph("b", "south"), Ph("c", "north") },
            Blocks = new() { Blk("a", "c", 0, "Round"), Blk("a", "b", 2, "Impulse") },
            LastTurn = 6, LastStage = 3,
            Victory = new() { new VictoryCondition { Id = "victory-1", Condition = "Supply > 3", SideId = "north", Message = "Held", PhaseIds = new() { "c" } } },
            Schedule = new()
            {
                Row("sched-1") with { Stage = 1, FormulaId = "f1", Label = "Arrivals" },
                Row("sched-2") with { Action = ScheduleAction.Leave, ZoneMapId = null },
            },
        };
        Assert.Empty(V(p, "reserve"));
    }
}
