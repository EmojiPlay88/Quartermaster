using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Core.Tests;

public class PhaseConditionTests
{
    const string Guard = "Every phase was skipped; check the phase conditions";

    /// <summary>Invented pack: P1..P4, block P2-P3 count 2, global Flag (Default 0), piece-only Grit.</summary>
    static RulesPack Pack(string? c1 = null, string? c2 = null, string? c3 = null, string? c4 = null) => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "flag", Name = "Flag", Scope = PropertyScope.Holders, Default = 0 },
            new PropertyDef { Id = "grit", Name = "Grit", Scope = PropertyScope.Pieces, Default = 5 },
        },
        Phases = new()
        {
            new PhaseDef { Id = "P1", Name = "Phase One", Condition = c1 },
            new PhaseDef { Id = "P2", Name = "Phase Two", Condition = c2 },
            new PhaseDef { Id = "P3", Name = "Phase Three", Condition = c3 },
            new PhaseDef { Id = "P4", Name = "Phase Four", Condition = c4 },
        },
        Blocks = new() { new PhaseBlock { FirstPhaseId = "P2", LastPhaseId = "P3", Count = 2 } },
    };

    static PhasePosition P(RulesPack pk, int turn, int repeat, string id) => new(turn, repeat, id, pk.Phases.FindIndex(p => p.Id == id));
    static SkippedPhase Sk(string id, string name, int turn, int repeat) => new(id, name, turn, repeat);

    [Fact]
    public void Empty_condition_runs()
    {
        var pk = Pack(c2: "  ");
        var s = new GameState();
        Assert.True(PhaseConditions.Runs(pk, s, pk.Phases[0], P(pk, 1, 1, "P1"), out var err));
        Assert.Null(err);
        var step = PhaseConditions.Next(pk, s, P(pk, 1, 1, "P1"))!;
        Assert.Equal(P(pk, 1, 1, "P2"), step.To);
        Assert.Empty(step.Skipped);
        Assert.Null(step.Notice);
    }

    [Fact]
    public void False_condition_is_skipped()
    {
        var pk = Pack(c2: "Flag == 1");
        var step = PhaseConditions.Next(pk, new GameState(), P(pk, 1, 1, "P1"))!;
        Assert.Equal(P(pk, 1, 1, "P3"), step.To);
        Assert.Equal(new[] { Sk("P2", "Phase Two", 1, 1) }, step.Skipped);
        Assert.Null(step.Notice);
    }

    [Fact]
    public void Condition_reads_value_set_in_previous_phase()
    {
        var pk = Pack(c2: "Flag == 1");
        var s = new GameState();
        s.Apply(new LedgerDelta(LedgerKey.Holder(HolderDef.GlobalId), "flag", 0, 1, "manual", null, null, null, null));
        var step = PhaseConditions.Next(pk, s, P(pk, 1, 1, "P1"))!;
        Assert.Equal(P(pk, 1, 1, "P2"), step.To);
        Assert.Empty(step.Skipped);
    }

    [Fact]
    public void Skip_last_phase_of_block_moves_to_next_repeat()
    {
        var pk = Pack(c3: "false");
        var step = PhaseConditions.Next(pk, new GameState(), P(pk, 1, 1, "P2"))!;
        Assert.Equal(P(pk, 1, 2, "P2"), step.To);
        Assert.Equal(new[] { Sk("P3", "Phase Three", 1, 1) }, step.Skipped);
    }

    [Fact]
    public void Skip_whole_repeat_counts_it()
    {
        var pk = Pack(c2: "stage() == 2", c3: "stage() == 2");
        var step = PhaseConditions.Next(pk, new GameState(), P(pk, 1, 1, "P1"))!;
        Assert.Equal(P(pk, 1, 2, "P2"), step.To);
        Assert.Equal(new[] { Sk("P2", "Phase Two", 1, 1), Sk("P3", "Phase Three", 1, 1) }, step.Skipped);

        var all = Pack(c2: "false", c3: "false");
        var step2 = PhaseConditions.Next(all, new GameState(), P(all, 1, 1, "P1"))!;
        Assert.Equal(P(all, 1, 1, "P4"), step2.To);
        Assert.Equal(new[] { Sk("P2", "Phase Two", 1, 1), Sk("P3", "Phase Three", 1, 1), Sk("P2", "Phase Two", 1, 2), Sk("P3", "Phase Three", 1, 2) },
            step2.Skipped);
    }

    [Fact]
    public void Skip_last_phase_wraps_turn()
    {
        var pk = Pack(c4: "false");
        var step = PhaseConditions.Next(pk, new GameState(), P(pk, 1, 2, "P3"))!;
        Assert.Equal(P(pk, 2, 1, "P1"), step.To);
        Assert.Equal(Sequencer.Next(pk, P(pk, 1, 1, "P4")), step.To);
        Assert.Equal(new[] { Sk("P4", "Phase Four", 1, 1) }, step.Skipped);
    }

    [Fact]
    public void Turn_and_stage_are_the_candidates()
    {
        var pk = Pack(c1: "turn() == 1", c2: "stage() == 2");
        var s = new GameState();
        var a = PhaseConditions.Next(pk, s, P(pk, 1, 1, "P1"))!;
        Assert.Equal(P(pk, 1, 1, "P3"), a.To);
        Assert.Equal(new[] { Sk("P2", "Phase Two", 1, 1) }, a.Skipped);
        var b = PhaseConditions.Next(pk, s, a.To)!;
        Assert.Equal(P(pk, 1, 2, "P2"), b.To);
        Assert.Empty(b.Skipped);
        var c = PhaseConditions.Next(pk, s, P(pk, 1, 1, "P4"))!;
        Assert.Equal(new[] { Sk("P1", "Phase One", 2, 1), Sk("P2", "Phase Two", 2, 1) }, c.Skipped);
        Assert.Equal(P(pk, 2, 1, "P3"), c.To);
    }

    [Fact]
    public void All_skipped_stops_with_guard()
    {
        var pk = Pack("false", "false", "false", "false");
        var s = new GameState();
        var step = PhaseConditions.Next(pk, s, P(pk, 1, 1, "P1"))!;
        Assert.Equal(P(pk, 1, 1, "P2"), step.To);
        Assert.Empty(step.Skipped);
        Assert.Equal(Guard, step.Notice);
        var first = PhaseConditions.First(pk, s)!;
        Assert.Equal(P(pk, 1, 1, "P1"), first.To);
        Assert.Empty(first.Skipped);
        Assert.Equal(Guard, first.Notice);
    }

    [Fact]
    public void Guard_from_a_later_repeat_tests_the_whole_turn()
    {
        var pk = Pack("false", "false", "stage() == 1", "false");
        var step = PhaseConditions.Next(pk, new GameState(), P(pk, 1, 2, "P2"))!;
        Assert.Equal(P(pk, 2, 1, "P3"), step.To);
        Assert.Null(step.Notice);
        Assert.Equal(new[] { Sk("P3", "Phase Three", 1, 2), Sk("P4", "Phase Four", 1, 1), Sk("P1", "Phase One", 2, 1), Sk("P2", "Phase Two", 2, 1) },
            step.Skipped);

        var none = Pack("false", "false", "false", "false");
        var g = PhaseConditions.Next(none, new GameState(), P(none, 1, 2, "P2"))!;
        Assert.Equal(P(none, 1, 2, "P3"), g.To);
        Assert.Equal(Guard, g.Notice);
    }

    [Fact]
    public void Error_condition_is_not_skipped()
    {
        var pk = Pack(c2: "1/0 > 0");
        var s = new GameState();
        Assert.True(PhaseConditions.Runs(pk, s, pk.Phases[1], P(pk, 1, 1, "P2"), out var err));
        Assert.False(string.IsNullOrEmpty(err));
        var step = PhaseConditions.Next(pk, s, P(pk, 1, 1, "P1"))!;
        Assert.Equal(P(pk, 1, 1, "P2"), step.To);
        Assert.Empty(step.Skipped);
        Assert.Equal($"Condition of Phase Two failed: {err}; phase not skipped", step.Notice);
    }

    [Fact]
    public void Parse_error_and_unknown_or_piece_names_are_errors()
    {
        var s = new GameState();
        foreach (var c in new[] { "Flag ==", "Nope == 1", "Grit == 5" })
        {
            var pk = Pack(c2: c);
            Assert.True(PhaseConditions.Runs(pk, s, pk.Phases[1], P(pk, 1, 1, "P2"), out var err));
            Assert.False(string.IsNullOrEmpty(err), c);
            var step = PhaseConditions.Next(pk, s, P(pk, 1, 1, "P1"))!;
            Assert.Equal("P2", step.To.PhaseId);
            Assert.StartsWith("Condition of Phase Two failed: ", step.Notice);
        }
    }

    [Fact]
    public void Piece_function_in_condition_errors()
    {
        var s = new GameState();
        foreach (var c in new[] { "name() == \"x\"", "marker(\"x\") == 1", "has(Flag)", "dice(1, 6) > 0" })
        {
            var pk = Pack(c2: c);
            var step = PhaseConditions.Next(pk, s, P(pk, 1, 1, "P1"))!;
            Assert.Equal(P(pk, 1, 1, "P2"), step.To);
            Assert.NotNull(step.Notice);
            Assert.StartsWith("Condition of Phase Two failed: ", step.Notice);
            Assert.EndsWith("; phase not skipped", step.Notice);
        }
        var named = Pack(c2: "name() == \"x\"");
        PhaseConditions.Runs(named, s, named.Phases[1], P(named, 1, 1, "P2"), out var e);
        Assert.Contains("not available in a phase condition", e);
    }

    [Fact]
    public void Mod_works_in_condition()
    {
        var pk = Pack(c1: "mod(turn(), 2) == 1");
        var step = PhaseConditions.Next(pk, new GameState(), P(pk, 1, 1, "P4"))!;
        Assert.Equal(P(pk, 2, 1, "P2"), step.To);
    }

    [Fact]
    public void First_skips_too()
    {
        var pk = Pack(c1: "turn() == 2");
        var step = PhaseConditions.First(pk, new GameState())!;
        Assert.Equal(P(pk, 1, 1, "P2"), step.To);
        Assert.Equal(new[] { Sk("P1", "Phase One", 1, 1) }, step.Skipped);
        var plain = PhaseConditions.First(Pack(), new GameState())!;
        Assert.Equal(Sequencer.First(Pack()), plain.To);
    }

    [Fact]
    public void No_phases_gives_null()
    {
        var s = new GameState();
        Assert.Null(PhaseConditions.First(new RulesPack(), s));
        Assert.Null(PhaseConditions.Next(new RulesPack(), s, new PhasePosition(1, 1, "x", 0)));
    }

    [Fact]
    public void Advance_logs_skips_and_records_them()
    {
        var pk = Pack();
        var s = new GameState();
        s.Apply(new AdvancePhase(null, P(pk, 1, 1, "P1"), "", "Phase One"));
        Assert.Null(s.SkippedPhases);
        s.Apply(new AdvancePhase(P(pk, 1, 1, "P1"), P(pk, 1, 1, "P3"), "Phase One", "Phase Three", Skipped: new[] { Sk("P2", "Phase Two", 1, 1) }));
        Assert.Equal("Skipped Phase Two (condition false)", s.Log[^2].Text);
        Assert.Equal(LogKind.Turn, s.Log[^2].Kind);
        Assert.Equal("Turn 1 · Phase One → Phase Three", s.Log[^1].Text);
        Assert.Equal(new[] { Sk("P2", "Phase Two", 1, 1) }, s.SkippedPhases);

        s.Apply(new AdvancePhase(P(pk, 1, 1, "P3"), P(pk, 1, 2, "P3"), "Phase Three", "Phase Three", Skipped: new[] { Sk("P2", "Phase Two", 1, 2) }));
        Assert.Equal(new[] { Sk("P2", "Phase Two", 1, 1), Sk("P2", "Phase Two", 1, 2) }, s.SkippedPhases);

        // Wrapping: the skip belongs to turn 1, the new turn starts clean.
        s.Apply(new AdvancePhase(P(pk, 1, 2, "P3"), P(pk, 2, 1, "P1"), "Phase Three", "Phase One", Skipped: new[] { Sk("P4", "Phase Four", 1, 1) }));
        Assert.Equal("Skipped Phase Four (condition false)", s.Log[^2].Text);
        Assert.Null(s.SkippedPhases);

        s.Apply(new AdvancePhase(P(pk, 2, 1, "P1"), P(pk, 3, 1, "P2"), "Phase One", "Phase Two", Skipped: new[] { Sk("P1", "Phase One", 3, 1) }));
        Assert.Equal(new[] { Sk("P1", "Phase One", 3, 1) }, s.SkippedPhases);
        s.Apply(new AdvancePhase(P(pk, 3, 1, "P2"), P(pk, 4, 1, "P1"), "Phase Two", "Phase One"));
        Assert.Null(s.SkippedPhases);
    }

    [Fact]
    public void Replayed_advance_ignores_current_conditions()
    {
        var pk = Pack(c2: "false");
        var s = new GameState();
        s.Apply(new AdvancePhase(P(pk, 1, 1, "P1"), P(pk, 1, 1, "P2"), "Phase One", "Phase Two"));
        Assert.Equal(P(pk, 1, 1, "P2"), s.Position);
        Assert.Null(s.SkippedPhases);
    }

    [Fact]
    public void Save_round_trip_keeps_skips_and_omits_them_when_absent()
    {
        var o = QuartermasterJson.Options;
        var s = new GameState { SkippedPhases = new() { Sk("P2", "Phase Two", 1, 1) } };
        var back = JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(s, o), o)!;
        Assert.Equal(s.SkippedPhases, back.SkippedPhases);

        GameCommand cmd = new AdvancePhase(null, new PhasePosition(1, 1, "P3", 2), "", "Phase Three", Skipped: new[] { Sk("P1", "Phase One", 1, 1) });
        var cb = (AdvancePhase)JsonSerializer.Deserialize<GameCommand>(JsonSerializer.Serialize(cmd, o), o)!;
        Assert.Equal(((AdvancePhase)cmd).Skipped, cb.Skipped);

        Assert.DoesNotContain("skippedPhases", JsonSerializer.Serialize(new GameState(), o), StringComparison.OrdinalIgnoreCase);
        GameCommand plain = new AdvancePhase(null, new PhasePosition(1, 1, "P1", 0), "", "Phase One");
        var pj = JsonSerializer.Serialize(plain, o);
        Assert.DoesNotContain("skipped", pj, StringComparison.OrdinalIgnoreCase);
        Assert.Null(((AdvancePhase)JsonSerializer.Deserialize<GameCommand>(pj, o)!).Skipped);
    }

    [Fact]
    public void Validated_state_drops_broken_skip_entries()
    {
        var s = new GameState { SkippedPhases = new() { null!, new SkippedPhase(null!, "x", 1, 1) } };
        NativeSave.ValidateState(s);
        Assert.Null(s.SkippedPhases);
        var t = new GameState { SkippedPhases = new() { null!, Sk("P2", "Phase Two", 1, 1) } };
        NativeSave.ValidateState(t);
        Assert.Equal(new[] { Sk("P2", "Phase Two", 1, 1) }, t.SkippedPhases);
    }
}
