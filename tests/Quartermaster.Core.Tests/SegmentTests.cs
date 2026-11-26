using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Core.Tests;

/// <summary>B5 #6/#34: <see cref="PhasePosition.Inner"/> in saves and <c>segment()</c> in formulas, phase conditions and unit filters.</summary>
public class SegmentTests
{
    private static readonly JsonSerializerOptions O = QuartermasterJson.Options;

    [Fact]
    public void Inner_omitted_when_zero()
    {
        var json = JsonSerializer.Serialize(new PhasePosition(2, 1, "n-move", 1), O);
        Assert.Equal("{\"turn\":2,\"repeat\":1,\"phaseId\":\"n-move\",\"phaseIndex\":1}", json);
    }

    [Fact]
    public void Inner_round_trips()
    {
        var pos = new PhasePosition(2, 1, "n-combat", 3) { Inner = 4 };
        var json = JsonSerializer.Serialize(pos, O);
        Assert.Equal("{\"turn\":2,\"repeat\":1,\"phaseId\":\"n-combat\",\"phaseIndex\":3,\"inner\":4}", json);
        Assert.Equal(pos, JsonSerializer.Deserialize<PhasePosition>(json, O));
        var s = new GameState { Position = pos, EndedAt = pos, StartCursor = new StartCursor(pos, 1) };
        var back = s.DeepClone();
        Assert.Equal(4, back.Position!.Inner);
        Assert.Equal(pos, back.EndedAt);
        Assert.Equal(pos, back.StartCursor!.At);
    }

    [Fact]
    public void Position_equality_includes_inner()
    {
        var a = new PhasePosition(1, 1, "n-combat", 3) { Inner = 1 };
        Assert.Equal(a, new PhasePosition(1, 1, "n-combat", 3) { Inner = 1 });
        Assert.NotEqual(a, new PhasePosition(1, 1, "n-combat", 3) { Inner = 2 });
        Assert.NotEqual(a, new PhasePosition(1, 1, "n-combat", 3));
        Assert.Equal(1, (a with { Turn = 2 }).Inner);
    }

    [Fact]
    public void Save_without_b5_members_keeps_bytes()
    {
        // A save as master wrote it: a position, an ended-at position and a start cursor, none with "inner".
        const string master = "{\"pieces\":{},\"stacks\":{},\"mapOrder\":[],\"nextStackSeq\":0,\"ledgers\":{},\"ledgerHistory\":[],"
            + "\"diceHistory\":[],\"log\":[],\"notes\":\"\",\"players\":[],\"gameId\":\"0f3c\",\"turn\":{\"turn\":1,\"phase\":null},"
            + "\"position\":{\"turn\":2,\"repeat\":2,\"phaseId\":\"F\",\"phaseIndex\":2},"
            + "\"endedAt\":{\"turn\":2,\"repeat\":2,\"phaseId\":\"F\",\"phaseIndex\":2},"
            + "\"startCursor\":{\"at\":{\"turn\":2,\"repeat\":2,\"phaseId\":\"F\",\"phaseIndex\":2},\"next\":1},"
            + "\"difficulty\":\"SemiAuto\",\"ticked\":[],\"acknowledged\":[],\"pending\":[],\"hiddenAlerts\":[]}";
        var s = JsonSerializer.Deserialize<GameState>(master, O)!;
        Assert.Equal(0, s.Position!.Inner);
        var again = JsonSerializer.Serialize(s, O);
        Assert.Equal(master, again);
        Assert.DoesNotContain("inner", again);
    }

    private static PieceInstance Unit(GameState s)
    {
        var p = new PieceInstance { Id = "r1", TypeString = "piece;;;unit.svg;Relief barge 1", MapId = "Main Map", TraitStates = new() { "" } };
        s.Pieces[p.Id] = p;
        return p;
    }

    [Fact]
    public void Segment_in_formula_reads_inner()
    {
        var pack = RunnerFx.Pack(f => f with { Text = "segment() * 10 + stage()", Bindings = new(), Outputs = new() });
        var s = RunnerFx.State();
        s.Position = new PhasePosition(1, 2, "n-combat", 0) { Inner = 3 };
        var t = RunnerFx.Runner(pack).Run(s, RunnerFx.Req(piece: null));
        Assert.Null(t.Error);
        Assert.Equal(32, t.Result!.Value.Number);
    }

    [Fact]
    public void Segment_in_phase_condition_reads_candidate()
    {
        var pack = new RulesPack
        {
            Properties = new() { new PropertyDef { Id = "round", Name = "Round", Scope = PropertyScope.Holders, Expr = "segment() * 100" } },
            Phases = new() { new PhaseDef { Id = "n-combat", Name = "North Combat", Condition = "segment() < 3 and Round == segment() * 100" } },
        };
        var s = new GameState { Position = new PhasePosition(1, 1, "n-combat", 0) { Inner = 7 } };
        var at = new PhasePosition(1, 1, "n-combat", 0) { Inner = 2 };
        Assert.True(PhaseConditions.Runs(pack, s, pack.Phases[0], at, out var err));
        Assert.Null(err);
        Assert.False(PhaseConditions.Runs(pack, s, pack.Phases[0], at with { Inner = 3 }, out err));
        Assert.Null(err);
        Assert.Equal(200, PhaseConditions.Evaluate(pack, s, "Round", at, null).Number);
        Assert.Throws<Quartermaster.Rules.Expr.ParseError>(() => PhaseConditions.Evaluate(pack, s, "((", at, null));
    }

    [Fact]
    public void Segment_in_unit_filter()
    {
        var s = RunnerFx.State();
        var p = Unit(s);
        s.Position = new PhasePosition(1, 1, "n-combat", 0) { Inner = 2 };
        Assert.True(PieceFilter.Matches("segment() == 2", p, s, RunnerFx.Model, RunnerFx.Pack()));
        Assert.False(PieceFilter.Matches("segment() == 1", p, s, RunnerFx.Model, RunnerFx.Pack()));
        // a filter without a piece (an aggregate's own context) reads it too
        Assert.True(PieceFilter.Matches("countof(\"segment() == 2\") >= 1", p, s, RunnerFx.Model, RunnerFx.Pack()));
    }

    [Fact]
    public void Segment_zero_outside_inner_block()
    {
        var pack = RunnerFx.Pack(f => f with { Text = "segment() + 5", Bindings = new(), Outputs = new() });
        var s = RunnerFx.State();
        var p = Unit(s);
        Assert.Equal(5, RunnerFx.Runner(pack).Run(s, RunnerFx.Req(piece: null)).Result!.Value.Number);
        Assert.True(PieceFilter.Matches("segment() == 0", p, s, RunnerFx.Model, RunnerFx.Pack()));
        s.Position = new PhasePosition(1, 2, "n-move", 0);
        Assert.Equal(5, RunnerFx.Runner(pack).Run(s, RunnerFx.Req(piece: null)).Result!.Value.Number);
        Assert.True(PieceFilter.Matches("segment() == 0", p, s, RunnerFx.Model, RunnerFx.Pack()));
        var phase = new PhaseDef { Id = "n-move", Name = "North Movement", Condition = "segment() == 0" };
        Assert.True(PhaseConditions.Runs(new RulesPack { Phases = new() { phase } }, s, phase, s.Position, out _));
    }
}
