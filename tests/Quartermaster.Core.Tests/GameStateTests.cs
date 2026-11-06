using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class GameStateTests
{
    static GameState Make()
    {
        var s = new GameState();
        s.MapOrder.Add("m1"); s.MapOrder.Add("m2");
        return s;
    }

    static AddPiece Add(string id, string map = "m1", string? onto = null) =>
        new(id, "piece;;;a.png;" + id, "", map, new PointD(1, 2), onto);

    [Fact]
    public void Move_StackOntoExisting_AppendsOnTop()
    {
        var s = Make();
        s.Apply(Add("a")); s.Apply(Add("b"));
        var target = s.Pieces["a"].StackId!;
        s.Apply(new MovePieces(["b"], "m1", new PointD(5, 5), target));
        Assert.Equal(["a", "b"], s.Stacks[target].PieceIds);
        Assert.Equal(target, s.Pieces["b"].StackId);
        Assert.Single(s.Stacks);
    }

    [Fact]
    public void Move_LastPieceOut_DeletesStack()
    {
        var s = Make();
        s.Apply(Add("a"));
        var old = s.Pieces["a"].StackId!;
        s.Apply(new MovePieces(["a"], "m2", new PointD(3, 3), null));
        Assert.False(s.Stacks.ContainsKey(old));
        Assert.Single(s.Stacks);
        Assert.Equal("m2", s.Stacks[s.Pieces["a"].StackId!].MapId);
    }

    [Fact]
    public void Move_SetsMovedAndAppendsTrail()
    {
        var s = Make();
        s.Apply(Add("a"));
        Assert.False(s.Pieces["a"].Moved);
        Assert.Empty(s.Pieces["a"].Trail);
        s.Apply(new MovePieces(["a"], "m1", new PointD(9, 9), null));
        Assert.True(s.Pieces["a"].Moved);
        Assert.Equal([new PointD(9, 9)], s.Pieces["a"].Trail);
    }

    [Fact]
    public void ClearMoved_ClearsOnlyThatMap()
    {
        var s = Make();
        s.Apply(Add("a", "m1")); s.Apply(Add("b", "m2"));
        s.Apply(new MovePieces(["a"], "m1", new PointD(1, 1), null));
        s.Apply(new MovePieces(["b"], "m2", new PointD(1, 1), null));
        s.Apply(new ClearMoved("m1"));
        Assert.False(s.Pieces["a"].Moved);
        Assert.True(s.Pieces["b"].Moved);
    }

    [Fact]
    public void Remove_DeletesPieceAndCleansStack()
    {
        var s = Make();
        s.Apply(Add("a")); s.Apply(Add("b"));
        var sid = s.Pieces["a"].StackId!;
        s.Apply(new MovePieces(["b"], "m1", new PointD(0, 0), sid));
        s.Apply(new RemovePiece("a"));
        Assert.False(s.Pieces.ContainsKey("a"));
        Assert.Equal(["b"], s.Stacks[sid].PieceIds);
        s.Apply(new RemovePiece("b"));
        Assert.Empty(s.Stacks);
        s.Apply(new RemovePiece("nope"));
    }

    [Fact]
    public void Apply_UnknownIdsAndPadding()
    {
        var s = Make();
        s.Apply(new MovePieces(["x"], "m1", new PointD(0, 0), "bogus"));
        s.Apply(new SetTraitState("x", 0, "q"));
        s.Apply(new ToggleTrail("x"));
        s.Apply(new SetStackExpanded("x", true));
        s.Apply(Add("a"));
        s.Apply(new SetTraitState("a", 2, "z"));
        Assert.Equal(["", "", "z"], s.Pieces["a"].TraitStates);
    }

    [Fact]
    public void Apply_IsDeterministic()
    {
        var cmds = new List<GameCommand>();
        var rnd = new Random(42);
        var ids = new List<string>();
        for (int i = 0; i < 50; i++)
        {
            int k = rnd.Next(5);
            if (k == 0 || ids.Count == 0)
            {
                var id = "p" + i; ids.Add(id);
                cmds.Add(Add(id, rnd.Next(2) == 0 ? "m1" : "m2"));
            }
            else if (k == 1 || k == 2)
                cmds.Add(new MovePieces([ids[rnd.Next(ids.Count)]], "m1", new PointD(rnd.Next(100), rnd.Next(100)), null));
            else if (k == 3) cmds.Add(new RemovePiece(ids[rnd.Next(ids.Count)]));
            else cmds.Add(new SetTraitState(ids[rnd.Next(ids.Count)], rnd.Next(3), "v" + i));
        }
        var a = Make().DeepClone(); var b = Make().DeepClone();
        foreach (var c in cmds) { a.Apply(c); b.Apply(c); }
        Assert.Equal(JsonSerializer.Serialize(a, QuartermasterJson.Options), JsonSerializer.Serialize(b, QuartermasterJson.Options));
        Assert.Equal(JsonSerializer.Serialize(a, QuartermasterJson.Options), JsonSerializer.Serialize(a.DeepClone(), QuartermasterJson.Options));
    }
}
