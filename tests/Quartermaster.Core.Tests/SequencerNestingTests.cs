using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Core.Tests;

/// <summary>B5 #6: open-ended blocks (count 0) and one nesting level in the turn sequence. Invented Riverland phases.</summary>
public class SequencerNestingTests
{
    /// <summary>Weather, Movement, Fire, Assault, Supply, End; North moves, South fires.</summary>
    private static RulesPack Pack(params PhaseBlock[] blocks) => new()
    {
        Sides = new()
        {
            new SideDef { Id = "north", Name = "Northern League" },
            new SideDef { Id = "south", Name = "Southern Compact" },
        },
        Phases = new()
        {
            new PhaseDef { Id = "W", Name = "Weather" },
            new PhaseDef { Id = "M", Name = "Movement", SideId = "north" },
            new PhaseDef { Id = "F", Name = "Fire", SideId = "south" },
            new PhaseDef { Id = "A", Name = "Assault", SideId = "north" },
            new PhaseDef { Id = "S", Name = "Supply" },
            new PhaseDef { Id = "E", Name = "End" },
        },
        Blocks = blocks.ToList(),
    };

    private static PhaseBlock B(string first, string last, int count, string label = "") =>
        new() { FirstPhaseId = first, LastPhaseId = last, Count = count, Label = label };

    private static PhasePosition P(RulesPack pk, int turn, int repeat, string id, int inner = 0) =>
        new(turn, repeat, id, pk.Phases.FindIndex(p => p.Id == id)) { Inner = inner };

    private static string Show(PhasePosition p) => $"{p.Turn}{p.PhaseId}{p.Repeat}.{p.Inner}";

    private static List<string> Walk(RulesPack pk, PhasePosition from, int steps)
    {
        var res = new List<string>();
        var cur = from;
        for (var i = 0; i < steps; i++) { cur = Sequencer.Next(pk, cur)!; res.Add(Show(cur)); }
        return res;
    }

    [Fact]
    public void Fixed_blocks_walk_as_master()
    {
        // Two fixed blocks side by side, two turns: positions and stage texts as master produced them.
        var pk = Pack(B("M", "F", 2, "Day"), B("A", "S", 3));
        var cur = Sequencer.First(pk)!;
        var got = new List<string> { $"{cur.Turn}{cur.PhaseId}{cur.Repeat}.{cur.Inner}:{Sequencer.StageText(pk, cur)}" };
        for (var i = 0; i < 23; i++)
        {
            cur = Sequencer.Next(pk, cur)!;
            got.Add($"{cur.Turn}{cur.PhaseId}{cur.Repeat}.{cur.Inner}:{Sequencer.StageText(pk, cur)}");
        }
        string[] turn(int t) => new[]
        {
            $"{t}W1.0:", $"{t}M1.0:Day 1 of 2", $"{t}F1.0:Day 1 of 2", $"{t}M2.0:Day 2 of 2", $"{t}F2.0:Day 2 of 2",
            $"{t}A1.0:Stage 1 of 3", $"{t}S1.0:Stage 1 of 3", $"{t}A2.0:Stage 2 of 3", $"{t}S2.0:Stage 2 of 3",
            $"{t}A3.0:Stage 3 of 3", $"{t}S3.0:Stage 3 of 3", $"{t}E1.0:",
        };
        Assert.Equal(turn(1).Concat(turn(2)), got);
        Assert.Equal(2, Sequencer.ValidBlocks(pk).Count);
        Assert.Equal(12, PhaseConditions.TurnLength(pk));
    }

    [Fact]
    public void Open_block_continue_leaves()
    {
        var pk = Pack(B("M", "F", 0, "Fire round"));
        Assert.Equal(P(pk, 1, 1, "S"), Sequencer.Next(pk, P(pk, 1, 3, "A")));
        Assert.Equal(P(pk, 1, 1, "A"), Sequencer.Next(pk, P(pk, 1, 3, "F"), repeat: false));
        Assert.Equal(P(pk, 1, 1, "A"), Sequencer.Next(pk, P(pk, 1, 3, "F")));
        // Inside the block the repeat is kept (no clamp against count 0).
        Assert.Equal(P(pk, 1, 4, "F"), Sequencer.Next(pk, P(pk, 1, 4, "M")));
    }

    [Fact]
    public void Open_block_repeat_returns_to_first_with_repeat_plus_one()
    {
        var pk = Pack(B("M", "F", 0, "Fire round"));
        Assert.Equal(P(pk, 1, 4, "M"), Sequencer.Next(pk, P(pk, 1, 3, "F"), repeat: true));
        Assert.Same(pk.Blocks[0], Sequencer.RepeatChoice(pk, P(pk, 1, 3, "F")));
        Assert.Null(Sequencer.RepeatChoice(pk, P(pk, 1, 3, "M")));
        // repeat away from a block's last phase changes nothing
        Assert.Equal(P(pk, 1, 3, "F"), Sequencer.Next(pk, P(pk, 1, 3, "M"), repeat: true));
        Assert.Equal(P(pk, 1, 1, "M"), Sequencer.Next(pk, P(pk, 1, 1, "W"), repeat: true));
    }

    [Fact]
    public void Open_block_count_zero_never_throws()
    {
        var pk = Pack(B("M", "S", 0), B("F", "A", 0, "Round"));
        var cur = Sequencer.First(pk)!;
        for (var i = 0; i < 40; i++)
        {
            cur = Sequencer.Next(pk, cur, i % 3 == 0)!;
            _ = Sequencer.StageText(pk, cur);
            _ = Sequencer.Resolve(pk, cur);
            Assert.True(cur.Repeat >= 1);
        }
        Assert.Equal(P(pk, 1, 7, "F", 9), Sequencer.Resolve(pk, P(pk, 1, 7, "F", 9)).Position);
        Assert.Equal(P(pk, 1, 1, "F", 1), Sequencer.Resolve(pk, P(pk, 1, 0, "F", 0)).Position);
        Assert.Equal("Stage 7", Sequencer.StageText(pk, P(pk, 1, 7, "M")));
        Assert.Equal(6, PhaseConditions.TurnLength(pk));
        Assert.Equal(6, PhaseConditions.TurnLength(Pack(B("M", "F", 0))));
    }

    [Fact]
    public void Open_stage_text_has_no_of()
    {
        var pk = Pack(B("M", "F", 0, "Fire round"));
        Assert.Equal("Fire round 3", Sequencer.StageText(pk, P(pk, 1, 3, "F")));
        Assert.Equal("Stage 2", Sequencer.StageText(Pack(B("M", "F", 0, " ")), P(pk, 1, 2, "M")));
        Assert.Null(Sequencer.StageText(pk, P(pk, 1, 1, "S")));
    }

    [Fact]
    public void Inner_fixed_in_outer_fixed_counts()
    {
        var pk = Pack(B("M", "S", 2, "Day"), B("F", "A", 2, "Round"));
        Assert.Equal(new[]
        {
            "1M1.0", "1F1.1", "1A1.1", "1F1.2", "1A1.2", "1S1.0",
            "1M2.0", "1F2.1", "1A2.1", "1F2.2", "1A2.2", "1S2.0", "1E1.0", "2W1.0",
        }, Walk(pk, Sequencer.First(pk)!, 14));
        // the repeat flag changes nothing at fixed blocks
        Assert.Equal(P(pk, 1, 1, "F", 2), Sequencer.Next(pk, P(pk, 1, 1, "A", 1), repeat: true));
        Assert.Equal(P(pk, 1, 2, "M"), Sequencer.Next(pk, P(pk, 1, 1, "S"), repeat: true));
        Assert.Null(Sequencer.RepeatChoice(pk, P(pk, 1, 1, "A", 2)));
        // Weather, End once; Movement, Supply twice; Fire, Assault four times
        Assert.Equal(1 + 2 + 4 + 4 + 2 + 1, PhaseConditions.TurnLength(pk));
    }

    [Fact]
    public void Inner_open_in_outer_fixed_repeat_and_continue()
    {
        var pk = Pack(B("M", "S", 2, "Day"), B("F", "A", 0, "Fire round"));
        Assert.Same(pk.Blocks[1], Sequencer.RepeatChoice(pk, P(pk, 1, 1, "A", 1)));
        Assert.Equal(P(pk, 1, 1, "F", 2), Sequencer.Next(pk, P(pk, 1, 1, "A", 1), repeat: true));
        Assert.Equal(P(pk, 1, 1, "A", 2), Sequencer.Next(pk, P(pk, 1, 1, "F", 2), repeat: true));
        Assert.Equal(P(pk, 1, 1, "S"), Sequencer.Next(pk, P(pk, 1, 1, "A", 2)));
        Assert.Null(Sequencer.RepeatChoice(pk, P(pk, 1, 1, "S")));
        Assert.Equal(P(pk, 1, 2, "M"), Sequencer.Next(pk, P(pk, 1, 1, "S")));
        Assert.Equal(P(pk, 1, 2, "F", 1), Sequencer.Next(pk, P(pk, 1, 2, "M")));
        Assert.Equal(P(pk, 1, 1, "E"), Sequencer.Next(pk, P(pk, 1, 2, "S")));
    }

    [Fact]
    public void Inner_fixed_ends_with_outer_open_choice_goes_to_outer()
    {
        var pk = Pack(B("M", "A", 0, "Day"), B("F", "A", 2, "Round"));
        Assert.Null(Sequencer.RepeatChoice(pk, P(pk, 1, 1, "A", 1)));
        Assert.Equal(P(pk, 1, 1, "F", 2), Sequencer.Next(pk, P(pk, 1, 1, "A", 1), repeat: true));
        Assert.Same(pk.Blocks[0], Sequencer.RepeatChoice(pk, P(pk, 1, 1, "A", 2)));
        Assert.Equal(P(pk, 1, 2, "M"), Sequencer.Next(pk, P(pk, 1, 1, "A", 2), repeat: true));
        Assert.Equal(P(pk, 1, 1, "S"), Sequencer.Next(pk, P(pk, 1, 1, "A", 2)));
        // an outer block whose first phase is in the inner block starts it at 1
        var pk2 = Pack(B("F", "S", 0, "Day"), B("F", "A", 2, "Round"));
        Assert.Equal(P(pk2, 1, 2, "F", 1), Sequencer.Next(pk2, P(pk2, 1, 1, "S"), repeat: true));
    }

    [Fact]
    public void Entering_inner_block_sets_inner_one()
    {
        var pk = Pack(B("M", "S", 2, "Day"), B("F", "A", 0, "Fire round"));
        Assert.Equal(P(pk, 1, 2, "F", 1), Sequencer.Next(pk, P(pk, 1, 2, "M")));
        Assert.Same(pk.Blocks[1], Sequencer.InnerBlock(pk, 2));
        Assert.Same(pk.Blocks[0], Sequencer.OuterBlock(pk, 2));
        Assert.Null(Sequencer.InnerBlock(pk, 1));
        Assert.Same(pk.Blocks[0], Sequencer.OuterBlock(pk, 1));
        Assert.Null(Sequencer.OuterBlock(pk, 0));
        Assert.Null(Sequencer.InnerBlock(pk, 99));
    }

    [Fact]
    public void First_in_inner_block_inner_one()
    {
        var pk = Pack(B("W", "F", 2, "Day"), B("W", "M", 3, "Round"));
        Assert.Equal(P(pk, 1, 1, "W", 1), Sequencer.First(pk));
        Assert.Equal(P(pk, 2, 1, "W", 1), Sequencer.Next(pk, P(pk, 1, 1, "E")));
        var flat = Pack(B("W", "F", 2));
        Assert.Equal(P(flat, 1, 1, "W"), Sequencer.First(flat));
    }

    [Fact]
    public void Leaving_inner_block_inner_zero()
    {
        var pk = Pack(B("M", "S", 2, "Day"), B("F", "A", 2, "Round"));
        Assert.Equal(P(pk, 1, 1, "S"), Sequencer.Next(pk, P(pk, 1, 1, "A", 2)));
        Assert.Equal(0, Sequencer.Next(pk, P(pk, 1, 1, "A", 2))!.Inner);
    }

    [Fact]
    public void Nested_stage_text_joined()
    {
        var pk = Pack(B("M", "S", 2, "Day"), B("F", "A", 0, "Fire round"));
        Assert.Equal("Day 1 of 2, Fire round 2", Sequencer.StageText(pk, P(pk, 1, 1, "F", 2)));
        Assert.Equal("Day 1 of 2", Sequencer.StageText(pk, P(pk, 1, 1, "M")));
        var pk2 = Pack(B("M", "S", 2, "Day"), B("F", "A", 2, ""));
        Assert.Equal("Day 2 of 2, Stage 1 of 2", Sequencer.StageText(pk2, P(pk2, 1, 2, "A", 1)));
    }

    [Fact]
    public void Resolve_clamps_inner()
    {
        var pk = Pack(B("M", "S", 2, "Day"), B("F", "A", 2, "Round"));
        Assert.Equal((P(pk, 1, 2, "F", 2), (string?)null), Sequencer.Resolve(pk, P(pk, 1, 5, "F", 5)));
        Assert.Equal(P(pk, 1, 1, "F", 1), Sequencer.Resolve(pk, P(pk, 1, 1, "F", 0)).Position);
        Assert.Equal(P(pk, 1, 1, "S"), Sequencer.Resolve(pk, P(pk, 1, 1, "S", 3)).Position);
        Assert.Equal(P(pk, 1, 1, "E"), Sequencer.Resolve(pk, P(pk, 1, 2, "E", 3)).Position);
        var open = Pack(B("M", "S", 2, "Day"), B("F", "A", 0, "Round"));
        Assert.Equal(P(open, 1, 2, "A", 9), Sequencer.Resolve(open, P(open, 1, 2, "A", 9)).Position);
        // a deleted phase continues at its old index, with Inner by the block found there
        var gone = Pack(B("W", "S", 2, "Day"), B("F", "A", 2, "Round"));
        gone.Phases.RemoveAt(1);   // Movement
        var (pos, notice) = Sequencer.Resolve(gone, new PhasePosition(1, 2, "M", 1));
        Assert.Equal(new PhasePosition(1, 1, "F", 1) { Inner = 1 }, pos);
        Assert.NotNull(notice);
    }

    [Fact]
    public void Two_level_nesting_ignored_as_invalid()
    {
        var outer = B("M", "S", 2, "Day");
        var inner = B("F", "A", 2, "Round");
        var third = B("F", "F", 3, "Volley");          // inside an inner block: ignored
        var around = B("W", "E", 2, "Week");          // around an outer block that has an inner one: ignored
        var same = B("F", "A", 4, "Again");           // same phases as the inner block: ignored
        var partial = B("A", "E", 2, "Late");         // partial overlap: ignored
        var pk = Pack(outer, inner, third, around, same, partial);
        Assert.Equal(new[] { outer, inner }, Sequencer.ValidBlocks(pk));
        Assert.Equal(new[] { "1M1.0", "1F1.1", "1A1.1", "1F1.2", "1A1.2", "1S1.0", "1M2.0" }, Walk(pk, Sequencer.First(pk)!, 7));
        // pack order: an inner block listed first becomes inner once its outer block is accepted
        var pk2 = Pack(inner, outer, around);
        Assert.Equal(new[] { inner, outer }, Sequencer.ValidBlocks(pk2));
        Assert.Same(inner, Sequencer.InnerBlock(pk2, 2));
        Assert.Same(outer, Sequencer.OuterBlock(pk2, 2));
        // a block around two disjoint ones nests both
        var a = B("M", "M", 2, "A");
        var b = B("A", "A", 2, "B");
        var pk3 = Pack(a, b, B("M", "S", 2, "Day"));
        Assert.Equal(3, Sequencer.ValidBlocks(pk3).Count);
        Assert.Same(a, Sequencer.InnerBlock(pk3, 1));
        Assert.Same(b, Sequencer.InnerBlock(pk3, 3));
        Assert.Null(Sequencer.InnerBlock(pk3, 2));
        // a block around a block that is itself around another: ignored
        var pk4 = Pack(B("F", "F", 2, "x"), B("F", "A", 2, "y"), B("M", "S", 2, "z"));
        Assert.Equal(2, Sequencer.ValidBlocks(pk4).Count);
    }

    [Fact]
    public void Conditions_next_repeat_skips_false_first_phase()
    {
        var pk = Pack(B("M", "F", 0, "Fire round"));
        pk.Phases[1] = pk.Phases[1] with { Condition = "stage() == 1" };
        var s = new GameState();
        var step = PhaseConditions.Next(pk, s, P(pk, 1, 1, "F"), null, repeat: true)!;
        Assert.Equal(P(pk, 1, 2, "F"), step.To);
        Assert.Equal(new[] { new SkippedPhase("M", "Movement", 1, 2) }, step.Skipped);
        // the walk passes repeat to the first step only: Continue from there leaves
        Assert.Equal(P(pk, 1, 1, "A"), PhaseConditions.Next(pk, s, P(pk, 1, 2, "F"), null, repeat: false)!.To);
        Assert.Equal(P(pk, 1, 1, "A"), PhaseConditions.Next(pk, s, P(pk, 1, 2, "F"))!.To);
        // a skipped last phase walks on (Continue), never repeating on its own
        var pk2 = Pack(B("M", "F", 0, "Fire round"));
        pk2.Phases[2] = pk2.Phases[2] with { Condition = "stage() == 1" };
        var step2 = PhaseConditions.Next(pk2, s, P(pk2, 1, 1, "F"), null, repeat: true)!;
        Assert.Equal(P(pk2, 1, 2, "M"), step2.To);
        var step3 = PhaseConditions.Next(pk2, s, P(pk2, 1, 2, "M"))!;
        Assert.Equal(P(pk2, 1, 1, "A"), step3.To);
        Assert.Equal(new[] { new SkippedPhase("F", "Fire", 1, 2) }, step3.Skipped);
    }

    private static readonly IReadOnlySet<string> North = new HashSet<string> { "north" };
    private static readonly IReadOnlySet<string> South = new HashSet<string> { "south" };

    [Fact]
    public void MayAct_solo()
    {
        var pk = Pack();
        Assert.True(Sequencer.MayAct(pk, pk.Phases[1], null));
    }

    [Fact]
    public void MayAct_sideless_phase()
    {
        var pk = Pack();
        Assert.True(Sequencer.MayAct(pk, pk.Phases[0], South));
        Assert.True(Sequencer.MayAct(pk, pk.Phases[1] with { SideId = "gone" }, South));
        Assert.True(Sequencer.MayAct(pk, pk.Phases[0], new HashSet<string>()));
    }

    [Fact]
    public void MayAct_acting_side()
    {
        var pk = Pack();
        Assert.True(Sequencer.MayAct(pk, pk.Phases[1], North));
        Assert.True(Sequencer.MayAct(pk, pk.Phases[1], new HashSet<string> { "south", "north" }));
    }

    [Fact]
    public void MayAct_reactor_side()
    {
        var pk = Pack();
        var phase = pk.Phases[1] with { Reactors = new() { "south" } };
        Assert.True(Sequencer.MayAct(pk, phase, South));
    }

    [Fact]
    public void MayAct_other_side_false()
    {
        var pk = Pack();
        Assert.False(Sequencer.MayAct(pk, pk.Phases[1], South));
        Assert.False(Sequencer.MayAct(pk, pk.Phases[1], new HashSet<string>()));
        Assert.False(Sequencer.MayAct(pk, pk.Phases[2] with { Reactors = new() { "south" } }, North));
    }

    [Fact]
    public void MayAct_unknown_reactor_ignored()
    {
        var pk = Pack();
        var phase = pk.Phases[1] with { Reactors = new() { "east" } };
        Assert.False(Sequencer.MayAct(pk, phase, new HashSet<string> { "east" }));
        Assert.False(Sequencer.MayAct(pk, phase, South));
    }
}
