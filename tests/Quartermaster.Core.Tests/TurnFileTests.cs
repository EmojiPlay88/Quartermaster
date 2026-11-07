using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class TurnFileTests
{
    static GameSession NewGame()
    {
        var st = new GameState { GameId = "game-1" }; st.MapOrder.Add("m1");
        return new GameSession(st, "mod");
    }

    /// <summary>Red plays two commands, sends (turn base marked), then plays three more.</summary>
    static GameSession TwoTurns()
    {
        var g = NewGame();
        g.Execute(new AddPiece("r1", "t", "", "m1", new PointD(1, 1), null));
        g.Execute(new AddPiece("b1", "t", "", "m1", new PointD(5, 5), null));
        g.MarkTurnBase();
        g.Execute(new MovePieces(["r1"], "m1", new PointD(2, 2), null));
        g.Execute(new RollDice("Red attack", 2, 6, [3, 5]));
        g.Execute(new SetNotes("Red was here"));
        return g;
    }

    static string Json(GameState s) => JsonSerializer.Serialize(s, QuartermasterJson.Options);

    static string TempPath(string ext) => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ext);

    static byte[] Gzip(string text)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var b = Encoding.UTF8.GetBytes(text);
            gz.Write(b, 0, b.Length);
        }
        return ms.ToArray();
    }

    static string Gunzip(byte[] gz)
    {
        using var src = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var r = new StreamReader(src, Encoding.UTF8);
        return r.ReadToEnd();
    }

    [Fact]
    public void RoundTrip_BytesAndFile()
    {
        var t = TurnFiles.Create(TwoTurns(), "red", "Ann", "blue");
        var back = TurnFiles.FromBytes(TurnFiles.ToBytes(t));
        Assert.Equal(1, back.Version);
        Assert.Equal(("mod", "game-1", "red", "Ann", "blue"), (back.ModuleSha256, back.GameId, back.FromPlayerId, back.FromName, back.ToPlayerId));
        Assert.Equal(t.CreatedUtc, back.CreatedUtc);
        Assert.Equal(Json(t.Start), Json(back.Start));
        Assert.Equal(t.StartHash, back.StartHash);
        Assert.Equal(t.EndHash, back.EndHash);
        Assert.Equal(StateHash.Of(back.Start), back.StartHash);
        Assert.Equal(t.Commands.Count, back.Commands.Count);
        Assert.IsType<RollDice>(back.Commands[1]);
        Assert.Equal([3, 5], ((RollDice)back.Commands[1]).Results);

        var path = TempPath(".qturn");
        try
        {
            TurnFiles.Write(path, t);
            var fromFile = TurnFiles.Read(path);
            Assert.Equal(t.EndHash, fromFile.EndHash);
            Assert.Equal(Json(t.Start), Json(fromFile.Start));
            Assert.Contains("\"$t\"", Gunzip(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Create_StartIsStateAtTurnBase_CommandsAfter()
    {
        var g = TwoTurns();
        var before = Json(g.State);
        int logBefore = g.Log.Count, turnBase = g.TurnBase;
        var t = TurnFiles.Create(g, "red", "Ann", null);

        Assert.Equal(2, turnBase);
        Assert.Equal(g.Log.Skip(2), t.Commands);
        var expectStart = g.Genesis.DeepClone();
        expectStart.Apply(g.Log[0]); expectStart.Apply(g.Log[1]);
        Assert.Equal(Json(expectStart), Json(t.Start));
        Assert.Equal(StateHash.Of(expectStart), t.StartHash);
        Assert.Equal(StateHash.Of(g.State), t.EndHash);
        Assert.Null(t.ToPlayerId);
        Assert.Equal(new PointD(1, 1), t.Start.Pieces["r1"].Pos);
        // Create does not change the session.
        Assert.Equal(before, Json(g.State));
        Assert.Equal(logBefore, g.Log.Count);
        Assert.Equal(turnBase, g.TurnBase);
        Assert.True(g.CanUndo);
    }

    [Fact]
    public void Create_NothingPlayed_Throws_ButAfterSendResendsCurrentGame()
    {
        var g = NewGame();
        Assert.Equal("Nothing to send yet.", Assert.Throws<InvalidOperationException>(() => TurnFiles.Create(g, "red", "Ann", null)).Message);
        g.Execute(new SetNotes("x"));
        g.MarkTurnBase();
        var t = TurnFiles.Create(g, "red", "Ann", null);
        Assert.Empty(t.Commands);
        Assert.Equal(t.StartHash, t.EndHash);
    }

    [Fact]
    public void TurnBase_FollowsUndoAndRebase()
    {
        var g = TwoTurns();
        while (g.CanUndo) g.Undo();
        Assert.Equal(0, g.TurnBase);
        while (g.CanRedo) g.Redo();
        g.MarkTurnBase();
        Assert.Equal(g.Log.Count, g.TurnBase);
        g.Rebase();
        Assert.Equal(0, g.TurnBase);
    }

    [Fact]
    public void Replay_EqualsSenderEndState_AndEndHashMatches()
    {
        var g = TwoTurns();
        var t = TurnFiles.FromBytes(TurnFiles.ToBytes(TurnFiles.Create(g, "red", "Ann", "blue")));
        var steps = new List<(GameCommand, int)>();
        var r = TurnFiles.Replay(t, "mod", (c, i) => steps.Add((c, i)));
        Assert.Equal(Json(g.State), Json(r.State));
        Assert.Equal(t.EndHash, StateHash.Of(r.State));
        Assert.Equal(t.Commands.Select((c, i) => (c, i)), steps);
        Assert.Equal("mod", r.ModuleSha256);
        // The turn file itself is not changed by replaying it.
        Assert.Equal(t.StartHash, StateHash.Of(t.Start));
    }

    [Fact]
    public void Replay_NoUndoPastReplay()
    {
        var t = TurnFiles.Create(TwoTurns(), "red", "Ann", "blue");
        var r = TurnFiles.Replay(t, "mod");
        Assert.False(r.CanUndo);
        Assert.False(r.CanRedo);
        Assert.Empty(r.Log);
        Assert.Equal(0, r.TurnBase);
        Assert.Equal(Json(r.State), Json(r.Genesis));
        // Blue's own turn then goes back to Red.
        r.Execute(new MovePieces(["b1"], "m1", new PointD(4, 4), null));
        var back = TurnFiles.Create(r, "blue", "Bo", "red");
        Assert.Equal(t.EndHash, back.StartHash);
        Assert.Single(back.Commands);
    }

    [Fact]
    public void Read_Garbage_Oversized_WrongVersion_TooManyCommands_Throw_InvalidData()
    {
        var good = JsonNode.Parse(Gunzip(TurnFiles.ToBytes(TurnFiles.Create(TwoTurns(), "red", "Ann", "blue"))))!.AsObject();

        Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes([1, 2, 3, 4]));
        Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes([]));
        Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes(Gzip("{not json")));
        Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes(Gzip("null")));

        var big = new byte[TurnFiles.MaxPlain + 1];
        Array.Fill(big, (byte)' ');
        byte[] bomb;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(big, 0, big.Length);
            bomb = ms.ToArray();
        }
        Assert.Contains("too large", Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes(bomb)).Message);

        var v2 = good.DeepClone().AsObject(); v2["version"] = 2;
        Assert.Equal("Unsupported turn file version 2.", Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes(Gzip(v2.ToJsonString()))).Message);

        var many = good.DeepClone().AsObject();
        var arr = new JsonArray();
        for (int i = 0; i <= TurnFiles.MaxCommands; i++) arr.Add(JsonNode.Parse("{\"$t\":\"setNotes\",\"text\":\"\"}"));
        many["commands"] = arr;
        Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes(Gzip(many.ToJsonString())));

        var noStart = good.DeepClone().AsObject(); noStart.Remove("start");
        Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes(Gzip(noStart.ToJsonString())));
        var badStart = good.DeepClone().AsObject(); badStart["start"]!.AsObject()["stacks"] = null;
        Assert.StartsWith("Malformed turn file:", Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes(Gzip(badStart.ToJsonString()))).Message);
        var nullCmd = good.DeepClone().AsObject(); nullCmd["commands"]!.AsArray().Add(null);
        Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes(Gzip(nullCmd.ToJsonString())));
        var unknownCmd = good.DeepClone().AsObject(); unknownCmd["commands"]!.AsArray().Add(JsonNode.Parse("{\"$t\":\"nope\"}"));
        Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes(Gzip(unknownCmd.ToJsonString())));

        var path = TempPath(".qturn");
        try
        {
            File.WriteAllText(path, "garbage");
            Assert.Throws<InvalidDataException>(() => TurnFiles.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TurnBase_PersistsInSave_OlderSaveLoadsWithZero_AndKeepsHistory()
    {
        var path = TempPath(".qsave");
        try
        {
            var g = TwoTurns();
            NativeSave.Write(path, g);
            Assert.Contains("\"turnBase\":2", File.ReadAllText(path));
            var back = NativeSave.Read(path);
            Assert.Equal(2, back.TurnBase);
            Assert.True(back.CanUndo);
            Assert.Equal(TurnFiles.Create(g, "red", "Ann", null).StartHash, TurnFiles.Create(back, "red", "Ann", null).StartHash);

            // A save without the field (every save before turn files) loads with 0 and keeps undo history.
            var fresh = NewGame();
            fresh.Execute(new SetNotes("a"));
            fresh.Execute(new SetNotes("b"));
            NativeSave.Write(path, fresh);
            Assert.DoesNotContain("turnBase", File.ReadAllText(path));
            var old = NativeSave.Read(path);
            Assert.Equal(0, old.TurnBase);
            Assert.True(old.CanUndo);
            Assert.Equal(2, old.Log.Count);
            Assert.False(old.RebasedOnLoad);

            // Out of range values are clamped to the log.
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["turnBase"] = 99;
            File.WriteAllText(path, node.ToJsonString());
            Assert.Equal(2, NativeSave.Read(path).TurnBase);
            node["turnBase"] = -5;
            File.WriteAllText(path, node.ToJsonString());
            Assert.Equal(0, NativeSave.Read(path).TurnBase);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{\"$t\":\"removePiece\",\"pieceId\":null}")]
    [InlineData("{\"$t\":\"removePiece\"}")]
    [InlineData("{\"$t\":\"movePieces\",\"pieceIds\":[\"r1\",null],\"mapId\":\"m1\",\"pos\":{\"x\":1,\"y\":1}}")]
    [InlineData("{\"$t\":\"movePieces\",\"pieceIds\":null,\"mapId\":\"m1\",\"pos\":{\"x\":1,\"y\":1}}")]
    [InlineData("{\"$t\":\"movePieces\",\"pieceIds\":[\"r1\"],\"pos\":{\"x\":1,\"y\":1}}")]
    [InlineData("{\"$t\":\"addPiece\",\"typeString\":\"t\",\"stateString\":\"\",\"mapId\":\"m1\",\"pos\":{\"x\":1,\"y\":1}}")]
    [InlineData("{\"$t\":\"addPiece\",\"pieceId\":\"x\",\"stateString\":\"\",\"mapId\":\"m1\",\"pos\":{\"x\":1,\"y\":1}}")]
    [InlineData("{\"$t\":\"setTraitState\",\"stateIndex\":0,\"newState\":\"s\"}")]
    [InlineData("{\"$t\":\"setStackExpanded\",\"expanded\":true}")]
    [InlineData("{\"$t\":\"toggleTrail\"}")]
    public void Read_CommandWithNullId_Throws_InvalidData(string command)
    {
        var good = JsonNode.Parse(Gunzip(TurnFiles.ToBytes(TurnFiles.Create(TwoTurns(), "red", "Ann", "blue"))))!.AsObject();
        good["commands"]!.AsArray().Add(JsonNode.Parse(command));
        Assert.StartsWith("Malformed turn file:", Assert.Throws<InvalidDataException>(() => TurnFiles.FromBytes(Gzip(good.ToJsonString()))).Message);
    }

    [Fact]
    public void Reducer_NullIds_AreIgnored_NotThrown()
    {
        var g = TwoTurns();
        var before = Json(g.State);
        var s = g.State;
        s.Apply(new RemovePiece(null!));
        s.Apply(new MovePieces(["r1", null!], null!, new PointD(9, 9), null));
        s.Apply(new MovePieces(null!, "m1", new PointD(9, 9), null));
        s.Apply(new AddPiece(null!, "t", "", "m1", new PointD(0, 0), null));
        s.Apply(new AddPiece("x", null!, "", "m1", new PointD(0, 0), null));
        s.Apply(new AddPiece("x", "t", "", null!, new PointD(0, 0), null));
        s.Apply(new SetTraitState(null!, 0, "s"));
        s.Apply(new SetStackExpanded(null!, true));
        s.Apply(new ToggleTrail(null!));
        Assert.Equal(before, Json(s));
        s.Apply(new MovePieces(["r1", null!], "m1", new PointD(9, 9), null));
        Assert.Equal(new PointD(9, 9), s.Pieces["r1"].Pos);
    }

    [Fact]
    public void HugeStateIndex_IsIgnored_InReducerAndReplay()
    {
        var g = NewGame();
        g.Execute(new AddPiece("r1", "t", "", "m1", new PointD(1, 1), null));
        g.Execute(new SetTraitState("r1", GameState.MaxTraitStates - 1, "ok"));
        Assert.Equal(GameState.MaxTraitStates, g.State.Pieces["r1"].TraitStates.Count);
        g.Execute(new SetTraitState("r1", 3, "still ok"));
        Assert.Equal("still ok", g.State.Pieces["r1"].TraitStates[3]);
        g.Execute(new SetTraitState("r1", GameState.MaxTraitStates, "no"));
        g.Execute(new SetTraitState("r1", int.MaxValue, "no"));
        Assert.Equal(GameState.MaxTraitStates, g.State.Pieces["r1"].TraitStates.Count);

        g.MarkTurnBase();
        g.Execute(new SetTraitState("r1", int.MaxValue - 1, "hostile"));
        var t = TurnFiles.FromBytes(TurnFiles.ToBytes(TurnFiles.Create(g, "red", "Ann", "blue")));
        var r = TurnFiles.Replay(t, "mod");
        Assert.Equal(GameState.MaxTraitStates, r.State.Pieces["r1"].TraitStates.Count);
        Assert.Equal(t.EndHash, StateHash.Of(r.State));
    }

    [Fact]
    public void Replay_CommandThatThrows_Is_InvalidData()
    {
        // A start state that skipped validation (a piece without trait states) makes the reducer throw on SetTraitState.
        var g = NewGame();
        g.Execute(new AddPiece("r1", "t", "", "m1", new PointD(1, 1), null));
        var t = TurnFiles.Create(g, "red", "Ann", "blue");
        var start = g.State.DeepClone();
        start.Pieces["r1"].TraitStates = null!;
        var bad = t with { Start = start, Commands = [new SetTraitState("r1", 0, "s")] };
        var ex = Assert.Throws<InvalidDataException>(() => TurnFiles.Replay(bad, "mod"));
        Assert.StartsWith("Malformed turn file:", ex.Message);
    }

    [Fact]
    public void ToBytes_TooLarge_Refuses()
    {
        var t = TurnFiles.Create(TwoTurns(), "red", "Ann", "blue") with { Commands = [new SetNotes(new string('a', (int)TurnFiles.MaxPlain))] };
        Assert.Equal("This turn is too large to send.", Assert.Throws<InvalidOperationException>(() => TurnFiles.ToBytes(t)).Message);
    }

    [Fact]
    public void Dice_InCommands_ReplayDoesNotReroll()
    {
        var g = TwoTurns();
        var t = TurnFiles.FromBytes(TurnFiles.ToBytes(TurnFiles.Create(g, "red", "Ann", "blue")));
        var r = TurnFiles.Replay(t, "mod");
        Assert.Equal([3, 5], r.State.DiceHistory.Single().Results);
        Assert.Equal(JsonSerializer.Serialize(g.State.DiceHistory, QuartermasterJson.Options),
            JsonSerializer.Serialize(r.State.DiceHistory, QuartermasterJson.Options));
    }
}
