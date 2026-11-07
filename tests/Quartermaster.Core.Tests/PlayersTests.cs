using System.Text.Json;
using System.Text.Json.Nodes;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class PlayersTests
{
    static RulesPack Pack() => new()
    {
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red" },
            new SideDef { Id = "blue", Name = "Blue" },
        },
    };

    static PlayerDef P(string id, string name, params string[] sides) => new(id, name, sides);
    static string Json(GameState s) => JsonSerializer.Serialize(s, QuartermasterJson.Options);
    static string Tmp() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");

    [Fact]
    public void SetPlayers_ReplaysAndUndoes()
    {
        var st = new GameState(); st.MapOrder.Add("m1");
        var g = new GameSession(st, "h");
        g.Execute(new AddPiece("a", "piece;;;a.png;a", "", "m1", new PointD(1, 2), null));
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        g.Execute(new SetPlayers([P("p1", "Ann", "red"), P("p2", "Bob", "blue")], at, "Ann"));
        Assert.Equal(2, g.State.Players.Count);
        Assert.Equal("Players: Ann (red); Bob (blue)", g.State.Log[^1].Text);
        Assert.Equal(LogKind.System, g.State.Log[^1].Kind);

        var replay = g.Genesis.DeepClone();
        foreach (var c in g.Log) replay.Apply(c);
        Assert.Equal(Json(g.State), Json(replay));

        Assert.True(g.CanUndo);
        g.Undo();
        Assert.Empty(g.State.Players);
        Assert.True(Players.IsSolo(g.State));
    }

    [Fact]
    public void Effective_Solo_HoldsAllSides()
    {
        var s = new GameState();
        var eff = Players.Effective(s, Pack());
        var solo = Assert.Single(eff);
        Assert.Equal("solo", solo.Id);
        Assert.Equal("Player", solo.Name);
        Assert.Equal(["red", "blue"], solo.Sides);
        Assert.Equal("solo", Players.Holding(s, Pack(), "blue")!.Id);
        s.Players = [P("p1", "Ann", "red"), P("p2", "Bob", "blue")];
        Assert.Equal("Bob", Players.Holding(s, Pack(), "blue")!.Name);
        Assert.Null(Players.Holding(s, Pack(), "green"));
        Assert.False(Players.IsSolo(s));
    }

    [Fact]
    public void SetPlayers_Invalid_RejectedWithMessage()
    {
        var pack = Pack();
        Assert.Null(Players.Validate([], pack));
        Assert.Null(Players.Validate([P("p1", "Ann", "red")], pack));   // solo-ish: not checked
        Assert.Null(Players.Validate([P("p1", "Ann", "red"), P("p2", "Bob", "blue")], pack));
        Assert.Null(Players.Validate([P("p1", "Ann", "red", "blue"), P("p2", "Bob")], pack));

        Assert.Contains("Blue", Players.Validate([P("p1", "Ann", "red"), P("p2", "Bob")], pack));            // unheld side
        Assert.Contains("Red", Players.Validate([P("p1", "Ann", "red", "blue"), P("p2", "Bob", "red")], pack)); // double-held
        Assert.Contains("unique", Players.Validate([P("p1", "Ann", "red"), P("p2", "ann", "blue")], pack));  // duplicate names
        Assert.NotNull(Players.Validate([P("p1", "", "red"), P("p2", "Bob", "blue")], pack));              // empty name
        var seven = Enumerable.Range(1, 7).Select(i => P("p" + i, "N" + i, i == 1 ? "red" : i == 2 ? "blue" : "x")).ToList();
        Assert.Contains("6", Players.Validate(seven, pack));
    }

    [Fact]
    public void NewGame_HasGameId()
    {
        var g = new GameState { GameId = Guid.NewGuid().ToString("N") };
        Assert.Equal(32, g.GameId.Length);
        Assert.Equal("", new GameState().GameId);   // bare states carry no id until a game is created
    }

    [Fact]
    public void Format6Save_LoadsSolo_KeepsUndo_GetsGameId()
    {
        var st = new GameState(); st.MapOrder.Add("m1");
        var g = new GameSession(st, "sha");
        using (g.BeginAction()) g.Execute(new AddPiece("a", "piece;;;a.png;a", "", "m1", new PointD(1, 2), null));
        using (g.BeginAction()) g.Execute(new MovePieces(["a"], "m1", new PointD(5, 5), null));
        var path = Tmp();
        try
        {
            NativeSave.Write(path, g);
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["format"] = 6;
            node["snapshot"]!.AsObject().Remove("players");
            node["snapshot"]!.AsObject().Remove("gameId");
            node["genesis"]!.AsObject().Remove("players");
            node["genesis"]!.AsObject().Remove("gameId");
            File.WriteAllText(path, node.ToJsonString());

            var back = NativeSave.Read(path);
            Assert.False(back.RebasedOnLoad);
            Assert.Null(back.LoadNotice);
            Assert.True(back.CanUndo);
            Assert.Empty(back.State.Players);
            Assert.Equal(32, back.State.GameId.Length);
            Assert.Equal(back.State.GameId, back.Genesis.GameId);
            back.Undo();
            Assert.Single(back.State.Pieces);
            Assert.Equal((1d, 2d), (back.State.Pieces["a"].Pos.X, back.State.Pieces["a"].Pos.Y));
            back.Undo();
            Assert.Empty(back.State.Pieces);
            Assert.Equal(back.Genesis.GameId, back.State.GameId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Format7_RoundTrip()
    {
        var st = new GameState { GameId = "abc123" }; st.MapOrder.Add("m1");
        var g = new GameSession(st, "sha");
        g.Execute(new SetPlayers([P("p1", "Ann", "red"), P("p2", "Bob", "blue")]));
        var path = Tmp();
        try
        {
            NativeSave.Write(path, g);
            var back = NativeSave.Read(path);
            Assert.Equal("abc123", back.State.GameId);
            Assert.Equal(2, back.State.Players.Count);
            Assert.Equal(["blue"], back.State.Players[1].Sides);
            Assert.Equal(Json(g.State), Json(back.State));
            Assert.True(back.CanUndo);
        }
        finally { File.Delete(path); }
    }
}
