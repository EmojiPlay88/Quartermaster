using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>B5 task 9: the pure edits behind the Workbench Phases, End of game and Schedule tabs. Invented Riverland data.</summary>
public class TurnStructureEditTests
{
    private static RulesPack Pack(int phases = 6, params PhaseBlock[] blocks) =>
        new()
        {
            Sides = new() { new SideDef { Id = "north", Name = "Northern League" }, new SideDef { Id = "south", Name = "Southern Compact" } },
            Phases = Enumerable.Range(1, phases).Select(i => new PhaseDef { Id = "p" + i, Name = "Phase " + i }).ToList(),
            Blocks = blocks.ToList(),
        };

    private static PhaseBlock B(string a, string b, int count, string label = "") =>
        new() { FirstPhaseId = a, LastPhaseId = b, Count = count, Label = label };

    [Fact]
    public void SetReactors_empty_null()
    {
        var pack = Pack();
        var set = PhaseEdit.SetReactors(pack, "p1", new[] { "north", "south" });
        Assert.Equal(new[] { "north", "south" }, set.Phases[0].Reactors);
        var cleared = PhaseEdit.SetReactors(set, "p1", Array.Empty<string>());
        Assert.Null(cleared.Phases[0].Reactors);
        Assert.Null(PhaseEdit.SetReactors(pack, "p1", new[] { "", " " }).Phases[0].Reactors);
    }

    [Fact]
    public void RemoveSide_drops_it_from_reactors()
    {
        var pack = PhaseEdit.SetReactors(Pack(), "p1", new[] { "south" });
        var (next, _) = PhaseEdit.RemoveSide(pack, "south");
        Assert.Null(next.Phases[0].Reactors);
    }

    [Fact]
    public void MakeBlock_count_zero()
    {
        var (next, err) = PhaseEdit.MakeBlock(Pack(), 1, 2, 0, "Round");
        Assert.Null(err);
        Assert.Equal(0, next.Blocks.Single().Count);
        Assert.DoesNotContain(RulesValidator.Validate(next), i => i.Severity == RulesIssueSeverity.Error);
    }

    [Fact]
    public void MakeBlock_nested_ok()
    {
        var outer = Pack(6, B("p1", "p5", 2, "Outer"));
        var (inner, err) = PhaseEdit.MakeBlock(outer, 1, 2, 3, "Inner");
        Assert.Null(err);
        Assert.Equal(2, inner.Blocks.Count);
        Assert.DoesNotContain(RulesValidator.Validate(inner), i => i.Severity == RulesIssueSeverity.Error);
        // an outer block made around an existing block is the same nesting
        var (around, err2) = PhaseEdit.MakeBlock(Pack(6, B("p2", "p3", 2, "Inner")), 0, 4, 2, "Outer");
        Assert.Null(err2);
        Assert.Equal(2, around.Blocks.Count);
    }

    [Fact]
    public void MakeBlock_second_level_refused()
    {
        var two = Pack(8, B("p1", "p7", 2, "Outer"), B("p2", "p6", 2, "Middle"));
        Assert.Equal("Blocks nest one level only", PhaseEdit.MakeBlock(two, 3, 4, 2, "Deep").Error);
        // a block made around the inner one, inside the outer, would be three levels
        Assert.Equal("Blocks nest one level only", PhaseEdit.MakeBlock(Pack(8, B("p1", "p7", 2, "Outer"), B("p2", "p3", 2, "In")), 1, 4, 2, "Mid").Error);
    }

    [Fact]
    public void MakeBlock_partial_overlap_refused()
    {
        var pack = Pack(6, B("p2", "p4", 2, "A"));
        Assert.Equal("These phases are already in a block", PhaseEdit.MakeBlock(pack, 3, 5, 2, "B").Error);
        Assert.Equal("These phases are already in a block", PhaseEdit.MakeBlock(pack, 1, 3, 2, "Same").Error);
        Assert.Equal("Count cannot be negative", PhaseEdit.MakeBlock(pack, 0, 0, -1, "x").Error);
    }

    [Fact]
    public void MakeBlock_two_open_ended_ending_together_refused()
    {
        var pack = Pack(6, B("p1", "p4", 0, "Outer"));
        var err = PhaseEdit.MakeBlock(pack, 2, 3, 0, "Inner").Error;
        Assert.NotNull(err);
        Assert.Contains("both repeat until a player stops", err);
        Assert.Null(PhaseEdit.MakeBlock(pack, 2, 3, 2, "Inner").Error);
    }

    [Fact]
    public void GameEndEdit_last_turn_and_stage()
    {
        var pack = Pack();
        var t = GameEndEdit.WithLastTurn(pack, 12);
        Assert.Equal(12, t.LastTurn);
        var s = GameEndEdit.WithLastStage(t, 3);
        Assert.Equal(3, s.LastStage);
        Assert.Null(GameEndEdit.WithLastStage(s, null).LastStage);
        Assert.Null(GameEndEdit.WithLastTurn(t, null).LastTurn);
        Assert.Equal(1, GameEndEdit.WithLastTurn(pack, 0).LastTurn);
        Assert.Equal(999, GameEndEdit.WithLastTurn(pack, 5000).LastTurn);
    }

    [Fact]
    public void GameEndEdit_victory_add_update_move_remove_last_to_null()
    {
        var pack = Pack();
        pack = GameEndEdit.AddVictory(pack);
        pack = GameEndEdit.AddVictory(pack);
        Assert.Equal(new[] { "victory-1", "victory-2" }, pack.Victory!.Select(v => v.Id));
        pack = GameEndEdit.UpdateVictory(pack, "victory-1", v => v with { Condition = "turn() > 3", SideId = " ", Message = "", PhaseIds = new() });
        var v1 = pack.Victory![0];
        Assert.Equal("turn() > 3", v1.Condition);
        Assert.Null(v1.SideId);
        Assert.Null(v1.Message);
        Assert.Null(v1.PhaseIds);
        pack = GameEndEdit.UpdateVictory(pack, "victory-1", v => v with { SideId = "north", Message = "North wins", PhaseIds = new() { "p1" } });
        Assert.Equal("north", pack.Victory![0].SideId);
        pack = GameEndEdit.MoveVictory(pack, 0, 1);
        Assert.Equal(new[] { "victory-2", "victory-1" }, pack.Victory!.Select(v => v.Id));
        Assert.Same(pack, GameEndEdit.MoveVictory(pack, 0, -1));
        pack = GameEndEdit.RemoveVictory(pack, 0);
        pack = GameEndEdit.AddVictory(pack);   // ids never collide with a kept one
        Assert.Equal(new[] { "victory-1", "victory-2" }, pack.Victory!.Select(v => v.Id));
        pack = GameEndEdit.RemoveVictory(GameEndEdit.RemoveVictory(pack, 0), 0);
        Assert.Null(pack.Victory);
    }

    [Fact]
    public void ScheduleEdit_add_ids_update_move_remove_last_to_null()
    {
        var pack = Pack();
        pack = ScheduleEdit.Add(pack, 0);
        pack = ScheduleEdit.Add(pack, 1);
        Assert.Equal(new[] { "sched-1", "sched-2" }, pack.Schedule!.Select(r => r.Id));
        Assert.Equal("p1", pack.Schedule![0].PhaseId);
        pack = ScheduleEdit.Update(pack, "sched-1", r => r with { Piece = "Fuel > 0", ZoneMapId = " ", FormulaId = "", Label = "  ", Turn = 4 });
        var r1 = pack.Schedule![0];
        Assert.Equal(4, r1.Turn);
        Assert.Null(r1.ZoneMapId);
        Assert.Null(r1.FormulaId);
        Assert.Null(r1.Label);
        pack = ScheduleEdit.Update(pack, "sched-1", r => r with { Label = "Landing", ZoneMapId = "Reserve" });
        Assert.Equal("Landing", pack.Schedule![0].Label);
        pack = ScheduleEdit.Move(pack, 1, -1);
        Assert.Equal(new[] { "sched-2", "sched-1" }, pack.Schedule!.Select(r => r.Id));
        Assert.Same(pack, ScheduleEdit.Move(pack, 0, -1));
        pack = ScheduleEdit.Remove(pack, 0);
        pack = ScheduleEdit.Add(pack, 9);
        Assert.Equal(new[] { "sched-1", "sched-2" }, pack.Schedule!.Select(r => r.Id));
        pack = ScheduleEdit.Remove(ScheduleEdit.Remove(pack, 0), 0);
        Assert.Null(pack.Schedule);
    }
}
