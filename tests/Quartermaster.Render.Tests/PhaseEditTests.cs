using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

public class PhaseEditTests
{
    private static PhaseDef P(string id, string? side = null) => new() { Id = id, Name = "Phase " + id, SideId = side };

    private static RulesPack Pack(string[] ids, params PhaseBlock[] blocks) =>
        new() { Phases = ids.Select(i => P(i)).ToList(), Blocks = blocks.ToList() };

    private static string[] Ids(RulesPack p) => p.Phases.Select(x => x.Id).ToArray();

    [Fact]
    public void Add_Duplicate_Remove_KeepOrder()
    {
        var pack = Pack(new[] { "a", "b", "c" });
        var (p1, e1) = PhaseEdit.Add(pack, "Inserted", 1);
        Assert.Null(e1);
        Assert.Equal(4, p1.Phases.Count);
        Assert.Equal("Inserted", p1.Phases[1].Name);
        Assert.Equal(32, p1.Phases[1].Id.Length);
        Assert.Equal("a", p1.Phases[0].Id);
        Assert.Equal("b", p1.Phases[2].Id);

        var (p2, e2) = PhaseEdit.Duplicate(pack, 1);
        Assert.Null(e2);
        Assert.Equal("Phase b (copy)", p2.Phases[2].Name);
        Assert.NotEqual("b", p2.Phases[2].Id);
        Assert.Equal("b", p2.Phases[1].Id);
        Assert.Equal("c", p2.Phases[3].Id);

        var (p3, e3) = PhaseEdit.Remove(pack, 1);
        Assert.Null(e3);
        Assert.Equal(new[] { "a", "c" }, Ids(p3));
        Assert.Equal(3, pack.Phases.Count); // input untouched

        var (same, bad) = PhaseEdit.Remove(pack, 9);
        Assert.NotNull(bad);
        Assert.Same(pack, same);
    }

    [Fact]
    public void Remove_BlockEndShrinks_EmptyBlockRemoved()
    {
        var pack = Pack(new[] { "a", "b", "c", "d" }, new PhaseBlock { FirstPhaseId = "b", LastPhaseId = "c", Count = 2, Label = "x" });
        var (p1, _) = PhaseEdit.Remove(pack, 1);
        Assert.Equal("c", p1.Blocks[0].FirstPhaseId);
        Assert.Equal("c", p1.Blocks[0].LastPhaseId);
        var (p2, _) = PhaseEdit.Remove(pack, 2);
        Assert.Equal("b", p2.Blocks[0].FirstPhaseId);
        Assert.Equal("b", p2.Blocks[0].LastPhaseId);
        var (p3, _) = PhaseEdit.Remove(p1, 1);
        Assert.Empty(p3.Blocks);
    }

    [Fact]
    public void Move_BreakingBlock_Refused()
    {
        var pack = Pack(new[] { "a", "b", "c", "d" }, new PhaseBlock { FirstPhaseId = "b", LastPhaseId = "c", Count = 2, Label = "x" });
        var (same, err) = PhaseEdit.Move(pack, 1, 1); // b moves after c
        Assert.Equal("That move would put a block's first phase after its last", err);
        Assert.Same(pack, same);
        var (ok, e2) = PhaseEdit.Move(pack, 0, 1); // a swaps with b: block b..c still ordered
        Assert.Null(e2);
        Assert.Equal(new[] { "b", "a", "c", "d" }, Ids(ok));
        var (_, e3) = PhaseEdit.Move(pack, 0, -1);
        Assert.NotNull(e3);
    }

    [Fact]
    public void MakeBlock_NonContiguousOrOverlap_Refused()
    {
        var pack = Pack(new[] { "a", "b", "c", "d" });
        Assert.Equal("Select neighbouring phases", PhaseEdit.MakeBlock(pack, 2, 1, 2, "x").Error);
        Assert.Equal("Select neighbouring phases", PhaseEdit.MakeBlock(pack, 0, 9, 2, "x").Error);
        Assert.Equal("Count must be at least 1", PhaseEdit.MakeBlock(pack, 0, 1, 0, "x").Error);
        var (made, e) = PhaseEdit.MakeBlock(pack, 1, 2, 3, "Round");
        Assert.Null(e);
        Assert.Equal(new PhaseBlock { FirstPhaseId = "b", LastPhaseId = "c", Count = 3, Label = "Round" }, made.Blocks.Single());
        Assert.Equal("These phases are already in a block", PhaseEdit.MakeBlock(made, 2, 3, 2, "y").Error);
        var (gone, e2) = PhaseEdit.RemoveBlock(made, 0);
        Assert.Null(e2);
        Assert.Empty(gone.Blocks);
        Assert.NotNull(PhaseEdit.RemoveBlock(made, 5).Error);
    }

    [Fact]
    public void RemoveSide_ClearsPhaseSide()
    {
        var (p1, _) = PhaseEdit.AddSide(new RulesPack(), "Alpha");
        var sid = p1.Sides.Single().Id;
        var pack = p1 with { Phases = new List<PhaseDef> { P("a", sid), P("b", "other") } };
        var (p2, err) = PhaseEdit.RemoveSide(pack, sid);
        Assert.Null(err);
        Assert.Empty(p2.Sides);
        Assert.Null(p2.Phases[0].SideId);
        Assert.Equal("other", p2.Phases[1].SideId);
    }

    [Fact]
    public void RenameSide_Duplicate_Refused()
    {
        var (p1, _) = PhaseEdit.AddSide(new RulesPack(), "Alpha");
        var (p2, _) = PhaseEdit.AddSide(p1, "Beta");
        Assert.Equal("Another side already has this name", PhaseEdit.AddSide(p2, "ALPHA").Error);
        var beta = p2.Sides[1].Id;
        var (same, err) = PhaseEdit.RenameSide(p2, beta, "alpha");
        Assert.Equal("Another side already has this name", err);
        Assert.Same(p2, same);
        var (p3, ok) = PhaseEdit.RenameSide(p2, beta, "BETA");
        Assert.Null(ok);
        Assert.Equal("BETA", p3.Sides[1].Name);
    }

    [Fact]
    public void Move_WithAlreadyInvalidBlock_AllowsRepairingMoves()
    {
        // Block c..b is inverted (first after last): reordering must stay possible so the user can fix it.
        var pack = Pack(new[] { "a", "b", "c", "d" }, new PhaseBlock { FirstPhaseId = "c", LastPhaseId = "b", Count = 2, Label = "x" });
        var (still, e1) = PhaseEdit.Move(pack, 0, 1);     // unrelated move: block stays inverted, still allowed
        Assert.Null(e1);
        Assert.Equal(new[] { "b", "a", "c", "d" }, Ids(still));
        var (fixedPack, e2) = PhaseEdit.Move(pack, 2, -1); // c moves before b: the block becomes valid
        Assert.Null(e2);
        Assert.Equal(new[] { "a", "c", "b", "d" }, Ids(fixedPack));
    }

    [Fact]
    public void Move_WithInvalidBlock_StillRefusesBreakingAValidBlock()
    {
        var pack = Pack(new[] { "a", "b", "c", "d", "e" },
            new PhaseBlock { FirstPhaseId = "e", LastPhaseId = "d", Count = 2, Label = "bad" },
            new PhaseBlock { FirstPhaseId = "a", LastPhaseId = "b", Count = 2, Label = "ok" });
        var (same, err) = PhaseEdit.Move(pack, 0, 1);      // a after b would break the valid block
        Assert.Equal(PhaseEdit.BlockBreakMessage, err);
        Assert.Same(pack, same);
        var (_, e2) = PhaseEdit.Move(pack, 2, 1);          // c past d: fine
        Assert.Null(e2);
    }
}
