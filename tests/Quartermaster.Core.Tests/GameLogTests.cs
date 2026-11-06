using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class GameLogTests
{
    static GameState Make() { var s = new GameState(); s.MapOrder.Add("m1"); return s; }
    static AddPiece Add(string id, string? logText = null, DateTimeOffset? at = null, string? who = null) =>
        new(id, "piece;;;a.png;" + id, "", "m1", new PointD(1, 2), null, logText, at, who);

    [Fact]
    public void AddPiece_LogsPlace_WithLogTextOrFallback()
    {
        var s = Make();
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
        s.Apply(Add("a", "Placed Tiger", at, "Ann"));
        s.Apply(Add("b"));
        Assert.Equal(new LogEntry(1, LogKind.Place, "Placed Tiger", "Ann", "a", "m1", at), s.Log[0]);
        Assert.Equal("piece b placed", s.Log[1].Text);
        Assert.Equal(DateTimeOffset.UnixEpoch, s.Log[1].At);
        Assert.Equal(2, s.Log[1].Seq);
    }

    [Fact]
    public void MovePieces_LogsMove()
    {
        var s = Make(); s.Apply(Add("a")); s.Apply(Add("b"));
        s.Apply(new MovePieces(["a", "b"], "m1", new PointD(5, 5), null));
        s.Apply(new MovePieces(["a"], "m1", new PointD(6, 6), null, "Moved A"));
        Assert.Equal(LogKind.Move, s.Log[2].Kind);
        Assert.Equal("2 piece(s) moved", s.Log[2].Text);
        Assert.Equal("a", s.Log[2].PieceId);
        Assert.Equal("Moved A", s.Log[3].Text);
        s.Apply(new MovePieces(null!, "m1", new PointD(0, 0), null));
    }

    [Fact]
    public void RemovePiece_LogsDelete()
    {
        var s = Make(); s.Apply(Add("a"));
        s.Apply(new RemovePiece("a"));
        Assert.Equal(LogKind.Delete, s.Log[1].Kind);
        Assert.Equal("piece a removed", s.Log[1].Text);
        Assert.Equal("a", s.Log[1].PieceId);
        s.Apply(Add("c")); s.Apply(new RemovePiece("c", "Gone"));
        Assert.Equal("Gone", s.Log[3].Text);
    }

    [Fact]
    public void RollDice_LogText_Numbers_Modifier_FaceTexts()
    {
        var s = Make();
        s.Apply(new RollDice("Attack", 2, 6, [4, 3], "2d6+1", null, 1));
        s.Apply(new RollDice("Attack", 1, 6, [4]));
        s.Apply(new RollDice("Attack", 2, 6, [4, 3], null, null, -2));
        s.Apply(new RollDice("Weather", 1, 6, [2], "Weather die", ["Clear"]));
        s.Apply(new RollDice("X", 2, 6, null!));
        Assert.All(s.Log, e => Assert.Equal(LogKind.Dice, e.Kind));
        Assert.Equal("Attack: 2d6+1 = 4 + 3 + 1 = 8", s.Log[0].Text);
        Assert.Equal("Attack: 1d6 = 4", s.Log[1].Text);
        Assert.Equal("Attack: 2d6 = 4 + 3 − 2 = 5", s.Log[2].Text);
        Assert.Equal("Weather: Weather die = Clear", s.Log[3].Text);
        Assert.Equal(5, s.DiceHistory.Count);
    }

    [Fact]
    public void LedgerDelta_Formula_UsesWorking()
    {
        var s = Make();
        s.Apply(new LedgerDelta("p:a", "fuel", 100, 73, "formula", "f1", null, null, "100 - 27 = 73"));
        s.Apply(new LedgerDelta("p:a", "fuel", 73, 70.5, "formula", "f1", null, null, null));
        Assert.Equal(LogKind.Formula, s.Log[0].Kind);
        Assert.Equal("100 - 27 = 73", s.Log[0].Text);
        Assert.Equal("fuel 73 → 70.5", s.Log[1].Text);
    }

    [Fact]
    public void LedgerDelta_Manual_AppendsNote()
    {
        var s = Make();
        s.Apply(new LedgerDelta("p:a", "fuel", 10, 12.345, "manual", null, null, "resupply", null));
        s.Apply(new LedgerDelta("p:a", "fuel", 12.345, 1, "manual", null, null, null, null));
        Assert.Equal(LogKind.Ledger, s.Log[0].Kind);
        Assert.Equal("fuel 10 → 12.35 (note: resupply)", s.Log[0].Text);
        Assert.Equal("fuel 12.35 → 1", s.Log[1].Text);
        s.Apply(new SeedLedger([new LedgerSeed("p:a", "x", 1)]));
        Assert.Equal(2, s.Log.Count);
    }

    [Fact]
    public void SetNotes_And_SetTurn_UpdateStateAndLog()
    {
        var s = Make();
        Assert.Equal("", s.Notes);
        Assert.Equal(new TurnState(1, null), s.Turn);
        s.Apply(new SetNotes("hello"));
        s.Apply(new SetTurn(3, "Combat"));
        s.Apply(new SetTurn(4, null));
        Assert.Equal("hello", s.Notes);
        Assert.Equal(new TurnState(4, null), s.Turn);
        Assert.Equal(LogKind.Note, s.Log[0].Kind);
        Assert.Equal("notes updated", s.Log[0].Text);
        Assert.Equal(LogKind.Turn, s.Log[1].Kind);
        Assert.Equal("Turn 3 · Combat", s.Log[1].Text);
        Assert.Equal("Turn 4", s.Log[2].Text);
    }

    [Fact]
    public void Seq_IsCountPlusOne_AndReplayIdentical()
    {
        var at = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(-5));
        GameCommand[] cmds =
        [
            Add("a", null, at, "Bob"), Add("b"),
            new MovePieces(["a"], "m1", new PointD(3, 3), null, null, at),
            new RollDice("R", 1, 6, [5], null, null, 0, at, "Bob"),
            new SetNotes("n", at), new SetTurn(2, "P", at),
            new LedgerDelta("p:a", "f", 1, 2, "manual", null, null, "n", null, at),
            new RemovePiece("b"),
        ];
        var s1 = Make(); var s2 = Make();
        foreach (var c in cmds) { s1.Apply(c); s2.Apply(c); }
        for (var i = 0; i < s1.Log.Count; i++) Assert.Equal(i + 1, s1.Log[i].Seq);
        Assert.Equal(8, s1.Log.Count);
        var j1 = JsonSerializer.Serialize(s1, QuartermasterJson.Options);
        Assert.Equal(j1, JsonSerializer.Serialize(s2, QuartermasterJson.Options));
        var back = JsonSerializer.Deserialize<GameState>(j1, QuartermasterJson.Options)!;
        Assert.Equal(at, back.Log[0].At);
        Assert.Equal(s1.Log[0], back.Log[0]);
    }

    [Fact]
    public void Commands_JsonDiscriminators_SetNotes_SetTurn()
    {
        var n = JsonSerializer.Serialize<GameCommand>(new SetNotes("x"), QuartermasterJson.Options);
        var t = JsonSerializer.Serialize<GameCommand>(new SetTurn(2, "P"), QuartermasterJson.Options);
        Assert.Contains("\"$t\":\"setNotes\"", n);
        Assert.Contains("\"$t\":\"setTurn\"", t);
        Assert.Equal(new SetTurn(2, "P"), JsonSerializer.Deserialize<GameCommand>(t, QuartermasterJson.Options));
    }
}
