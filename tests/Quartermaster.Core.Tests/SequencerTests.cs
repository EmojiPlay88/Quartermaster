using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class SequencerTests
{
    private static RulesPack Pack(params PhaseBlock[] blocks) => new()
    {
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "name() == \"Red One\" or name() == \"Red Two\"" },
        },
        Phases = new()
        {
            new PhaseDef { Id = "W", Name = "Weather" },
            new PhaseDef { Id = "S", Name = "Supply" },
            new PhaseDef { Id = "M", Name = "Movement", SideId = "red" },
            new PhaseDef { Id = "C", Name = "Combat" },
            new PhaseDef { Id = "E", Name = "End" },
        },
        Blocks = blocks.ToList(),
    };

    private static PhaseBlock MC(int count = 2, string label = "") =>
        new() { FirstPhaseId = "M", LastPhaseId = "C", Count = count, Label = label };

    private static PhasePosition P(RulesPack pk, int turn, int repeat, string id) =>
        new(turn, repeat, id, pk.Phases.FindIndex(p => p.Id == id));

    [Fact]
    public void First_And_Next_LinearAndWrap()
    {
        var pk = Pack();
        var cur = Sequencer.First(pk)!;
        Assert.Equal(new PhasePosition(1, 1, "W", 0), cur);
        var ids = new List<string>();
        for (var i = 0; i < 5; i++) { cur = Sequencer.Next(pk, cur)!; ids.Add($"{cur.Turn}{cur.PhaseId}"); }
        Assert.Equal(new[] { "1S", "1M", "1C", "1E", "2W" }, ids);
        Assert.Null(Sequencer.First(new RulesPack()));
        Assert.Null(Sequencer.Next(new RulesPack(), cur));
    }

    [Fact]
    public void Next_Block_RepeatsThenLeaves()
    {
        var pk = Pack(MC());
        var cur = P(pk, 1, 1, "M");
        var seq = new List<string>();
        for (var i = 0; i < 4; i++) { cur = Sequencer.Next(pk, cur)!; seq.Add($"{cur.PhaseId}{cur.Repeat}"); }
        Assert.Equal(new[] { "C1", "M2", "C2", "E1" }, seq);
    }

    [Fact]
    public void Next_InvalidBlocksIgnored_Terminates()
    {
        var pk = Pack(
            MC(2),
            new PhaseBlock { FirstPhaseId = "C", LastPhaseId = "E", Count = 3 },   // overlaps C
            new PhaseBlock { FirstPhaseId = "E", LastPhaseId = "S", Count = 3 },   // reversed
            new PhaseBlock { FirstPhaseId = "W", LastPhaseId = "S", Count = 0 },   // count 0
            new PhaseBlock { FirstPhaseId = "W", LastPhaseId = "zzz", Count = 3 }); // unknown id
        Assert.Single(Sequencer.ValidBlocks(pk));
        var cur = Sequencer.First(pk)!;
        for (var i = 0; i < 20; i++)
        {
            var n = Sequencer.Next(pk, cur)!;
            Assert.True(n.PhaseIndex >= 0 && n.PhaseIndex < 5);
            Assert.Equal(pk.Phases[n.PhaseIndex].Id, n.PhaseId);
            Assert.True(n.Repeat >= 1 && n.Repeat <= 2);
            Assert.True(n.Turn >= cur.Turn);
            cur = n;
        }
        Assert.True(cur.Turn >= 3);
    }

    [Fact]
    public void Resolve_CurrentPhaseDeleted_ClampsToOldIndex()
    {
        var pk = Pack();
        pk.Phases.RemoveAt(2);
        var (pos, notice) = Sequencer.Resolve(pk, new PhasePosition(3, 2, "M", 2));
        Assert.Equal(new PhasePosition(3, 1, "C", 2), pos);
        Assert.Equal("Phase \"M\" no longer exists; continuing at \"Combat\"", notice);
        pk.Phases.RemoveRange(2, 2);
        var (pos2, _) = Sequencer.Resolve(pk, new PhasePosition(3, 1, "C", 3));
        Assert.Equal("S", pos2!.PhaseId);
        Assert.Equal(1, pos2.PhaseIndex);
    }

    [Fact]
    public void Resolve_RepeatAboveCount_Clamped()
    {
        var pk = Pack(MC(2));
        var (pos, notice) = Sequencer.Resolve(pk, new PhasePosition(1, 5, "C", 0));
        Assert.Null(notice);
        Assert.Equal(new PhasePosition(1, 2, "C", 3), pos);
        var (pos2, _) = Sequencer.Resolve(pk, new PhasePosition(1, 2, "E", 4));
        Assert.Equal(1, pos2!.Repeat);
    }

    [Fact]
    public void Resolve_NoPhases_ReturnsNull()
    {
        var (pos, notice) = Sequencer.Resolve(new RulesPack(), new PhasePosition(1, 1, "W", 0));
        Assert.Null(pos);
        Assert.Null(notice);
    }

    [Fact]
    public void StageText_InsideBlock()
    {
        var pk = Pack(MC(2));
        Assert.Equal("Stage 2 of 2", Sequencer.StageText(pk, P(pk, 1, 2, "C")));
        Assert.Null(Sequencer.StageText(pk, P(pk, 1, 1, "E")));
        var pk2 = Pack(MC(2, "Round"));
        Assert.Equal("Round 2 of 2", Sequencer.StageText(pk2, P(pk2, 1, 2, "M")));
    }

    [Fact]
    public void UnitsFor_SideFilterAndEntryFilterAndAppliesTo()
    {
        var pk = Pack();
        var s = new GameState();
        void Add(string id, string name, string side) =>
            s.Pieces[id] = new PieceInstance
            {
                Id = id, MapId = "Main Map", TraitStates = new() { side, "" },
                TypeString = SequenceEncoder.Encode(new[] { "mark;" + SequenceEncoder.Encode(new[] { "Side" }, ','), "piece;;;unit.svg;" + name }, '\t'),
            };
        Add("a", "Red One", "A");
        Add("b", "Red Two", "B");
        Add("c", "Blue One", "A");
        var phase = pk.Phases[2];
        Assert.Equal(new[] { "a", "b" }, Sequencer.UnitsFor(pk, s, phase, null, null).Select(p => p.Id));
        Assert.Equal(new[] { "a" }, Sequencer.UnitsFor(pk, s, phase, null, "marker(\"Side\") == \"A\"").Select(p => p.Id));
        var f = new FormulaDef { Id = "f", Name = "F", Text = "1", AppliesTo = "marker(\"Side\") == \"B\"" };
        Assert.Equal(new[] { "b" }, Sequencer.UnitsFor(pk, s, phase, f, null).Select(p => p.Id));
        // phase without a side: all pieces
        Assert.Equal(3, Sequencer.UnitsFor(pk, s, pk.Phases[0], null, null).Count);
        // unknown side id: no side filter
        Assert.Equal(3, Sequencer.UnitsFor(pk, s, phase with { SideId = "gone" }, null, null).Count);
        // bad entry filter matches nothing
        Assert.Empty(Sequencer.UnitsFor(pk, s, phase, null, "(("));
    }

    [Fact]
    public void Compile_BadExpression_MatchesNothing()
    {
        var s = RunnerFx.State();
        var p = s.Pieces["u1"];
        var pack = RunnerFx.Pack();
        Assert.False(PieceFilter.Compile("((", s, pack)(p));
        Assert.False(PieceFilter.Compile("Missing + 1", s, pack)(p));
        Assert.True(PieceFilter.Compile(null, s, pack)(p));
        Assert.True(PieceFilter.Compile("  ", s, pack)(p));
        Assert.True(PieceFilter.Compile("name() == \"Rifle\"", s, pack)(p));
    }
}
