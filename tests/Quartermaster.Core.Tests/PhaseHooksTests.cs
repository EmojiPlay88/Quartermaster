using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class PhaseHooksTests
{
    private static RulesPack Pack() => new()
    {
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "marker(\"Side\") == \"Red\"" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "marker(\"Side\") == \"Blue\"" },
        },
        Phases = new()
        {
            new PhaseDef { Id = "W", Name = "Weather" },
            new PhaseDef { Id = "RM", Name = "Red Move", SideId = "red" },
            new PhaseDef { Id = "BM", Name = "Blue Move", SideId = "blue" },
            new PhaseDef { Id = "E", Name = "End" },
        },
    };

    private static PhasePosition P(RulesPack pk, int turn, string id) => new(turn, 1, id, pk.Phases.FindIndex(p => p.Id == id));

    private static GameState State()
    {
        var s = new GameState();
        void Add(string id, string name, string side) =>
            s.Pieces[id] = new PieceInstance
            {
                Id = id, MapId = "Riverland", TraitStates = new() { side, "" },
                TypeString = SequenceEncoder.Encode(new[] { "mark;" + SequenceEncoder.Encode(new[] { "Side" }, ','), "piece;;;unit.svg;" + name }, '\t'),
            };
        Add("r1", "Red One", "Red");
        Add("r2", "Red Two", "Red");
        Add("b1", "Blue One", "Blue");
        Add("n1", "Bridge", "None");
        return s;
    }

    [Fact]
    public void EndPhase_sets_and_Advance_clears()
    {
        var pk = Pack();
        var g = new GameSession(new GameState(), "sha");
        g.Execute(new AdvancePhase(null, P(pk, 1, "W"), "", "Weather"));
        Assert.Null(g.State.EndedAt);
        var logCount = g.State.Log.Count;

        g.Execute(new EndPhase(P(pk, 1, "W")));
        Assert.Equal(P(pk, 1, "W"), g.State.EndedAt);
        Assert.Equal(logCount, g.State.Log.Count);   // logs nothing

        g.Execute(new AdvancePhase(P(pk, 1, "W"), P(pk, 1, "RM"), "Weather", "Red Move"));
        Assert.Null(g.State.EndedAt);

        // undo restores it
        g.Undo();
        Assert.Equal(P(pk, 1, "W"), g.State.EndedAt);
        g.Undo();
        Assert.Null(g.State.EndedAt);

        // the command round-trips as a log entry
        var o = QuartermasterJson.Options;
        GameCommand cmd = new EndPhase(P(pk, 2, "BM"));
        var json = JsonSerializer.Serialize(cmd, o);
        Assert.Contains("\"endPhase\"", json);
        Assert.Equal(cmd, JsonSerializer.Deserialize<GameCommand>(json, o));
    }

    [Fact]
    public void EndedAt_round_trips_and_is_omitted_when_null()
    {
        var pk = Pack();
        var o = QuartermasterJson.Options;
        var s = new GameState { Position = P(pk, 1, "RM"), EndedAt = P(pk, 1, "RM") };
        var back = JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(s, o), o)!;
        Assert.Equal(s.EndedAt, back.EndedAt);
        Assert.Equal(s.EndedAt, s.DeepClone().EndedAt);
        Assert.DoesNotContain("endedAt", JsonSerializer.Serialize(new GameState(), o), StringComparison.OrdinalIgnoreCase);

        // validation keeps a matching EndedAt and drops a stale one
        NativeSave.ValidateState(back);
        Assert.Equal(P(pk, 1, "RM"), back.EndedAt);
        var stale = new GameState { Position = P(pk, 1, "BM"), EndedAt = P(pk, 1, "RM") };
        NativeSave.ValidateState(stale);
        Assert.Null(stale.EndedAt);
        var noPos = new GameState { EndedAt = P(pk, 1, "RM") };
        NativeSave.ValidateState(noPos);
        Assert.Null(noPos.EndedAt);
    }

    [Fact]
    public void MovedUnitsFor_modes()
    {
        var pk = Pack();
        var s = State();
        var red = pk.Phases[1];
        IEnumerable<string> Ids(PhaseDef ph, MoveSide mode, FormulaDef? f = null, string? filter = null) =>
            Sequencer.MovedUnitsFor(pk, s, ph, f, new MoveEntry { FormulaId = "f", FiresFor = mode, UnitFilter = filter }).Select(p => p.Id);

        Assert.Equal(new[] { "r1", "r2" }, Ids(red, MoveSide.Acting));
        Assert.Equal(new[] { "b1", "n1" }, Ids(red, MoveSide.Other));   // a piece of no side counts as other
        Assert.Equal(new[] { "r1", "r2", "b1", "n1" }, Ids(red, MoveSide.Any));
        Assert.Equal(new[] { "b1" }, Ids(pk.Phases[2], MoveSide.Acting));
        Assert.Equal(new[] { "r1", "r2", "n1" }, Ids(pk.Phases[2], MoveSide.Other));

        // formula applies-to and entry filter still apply
        var f = new FormulaDef { Id = "f", Name = "F", Text = "1", AppliesTo = "name() != \"Bridge\"" };
        Assert.Equal(new[] { "b1" }, Ids(red, MoveSide.Other, f));
        Assert.Equal(new[] { "r1", "b1" }, Ids(red, MoveSide.Any, f, "name() != \"Red Two\""));
        Assert.Empty(Ids(red, MoveSide.Other, null, "(("));

        // Acting matches today's UnitsFor
        Assert.Equal(Sequencer.UnitsFor(pk, s, red, f, "name() != \"Red Two\"").Select(p => p.Id), Ids(red, MoveSide.Acting, f, "name() != \"Red Two\""));

        // a phase without a side (or with a deleted one): every mode is today's behaviour
        foreach (var ph in new[] { pk.Phases[0], red with { SideId = "gone" } })
            foreach (var mode in new[] { MoveSide.Acting, MoveSide.Other, MoveSide.Any })
                Assert.Equal(new[] { "r1", "r2", "b1", "n1" }, Ids(ph, mode));
    }

    [Fact]
    public void FormulaPhases_allowed_cases()
    {
        var pk = Pack();
        var s = new GameState { Position = P(pk, 1, "RM") };
        FormulaDef F(List<string>? phases) => new() { Id = "f", Name = "F", Text = "1", Phases = phases };

        Assert.True(FormulaPhases.Allowed(pk, s, F(null), out var r0));
        Assert.Null(r0);
        Assert.True(FormulaPhases.Allowed(pk, s, F(new()), out _));
        Assert.True(FormulaPhases.Allowed(pk, s, F(new() { "RM" }), out var r1));
        Assert.Null(r1);

        // not listed: names in pack order, duplicates once
        Assert.False(FormulaPhases.Allowed(pk, s, F(new() { "E", "BM", "W", "BM" }), out var r2));
        Assert.Equal("Only in: Weather, Blue Move, End", r2);

        // before the game starts
        Assert.False(FormulaPhases.Allowed(pk, new GameState(), F(new() { "RM" }), out var r3));
        Assert.Equal("Only in: Red Move", r3);
        Assert.True(FormulaPhases.Allowed(pk, new GameState(), F(null), out _));

        // a pack with no phases ignores the limit
        Assert.True(FormulaPhases.Allowed(new RulesPack(), new GameState(), F(new() { "RM" }), out var r4));
        Assert.Null(r4);
    }
}
