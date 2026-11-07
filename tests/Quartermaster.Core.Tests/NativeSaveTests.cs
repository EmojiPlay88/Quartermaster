using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class NativeSaveTests
{
    [Fact]
    public void NativeSave_RoundTrip_PreservesStateAndLog()
    {
        var st = new GameState { GameId = "g" }; st.MapOrder.Add("m1");
        var sess = new GameSession(st, "abc123");
        int fired = 0; sess.Changed += _ => fired++;
        sess.Execute(new AddPiece("z", "t", "", "m1", new PointD(1.5, 2), null));
        sess.Execute(new AddPiece("a", "t", "", "m1", new PointD(3, 4), null));
        sess.Execute(new RemovePiece("z"));
        sess.Execute(new AddPiece("m", "t", "", "m1", new PointD(3, 4), null));
        sess.Execute(new MovePieces(["a", "m"], "m1", new PointD(7, 8), null));
        sess.Execute(new SetTraitState("a", 1, "s"));
        sess.Execute(new SetStackExpanded(sess.State.Pieces["a"].StackId!, true));
        sess.Execute(new ToggleTrail("a"));
        sess.Execute(new ClearMoved("m1"));
        Assert.Equal(9, fired);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            NativeSave.Write(path, sess);
            var back = NativeSave.Read(path);
            Assert.Equal(JsonSerializer.Serialize(sess.State, QuartermasterJson.Options), JsonSerializer.Serialize(back.State, QuartermasterJson.Options));
            Assert.Equal(["a", "m"], back.State.Pieces.Keys);
            Assert.Equal(sess.Log.Count, back.Log.Count);
            Assert.Equal(sess.Log[4].GetType(), back.Log[4].GetType());
            Assert.Equal(["a", "m"], ((MovePieces)back.Log[4]).PieceIds);
            Assert.Equal("abc123", back.ModuleSha256);
            Assert.Contains("\"$t\"", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeSave_Read_WrongFormatVersion_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            File.WriteAllText(path, "{\"format\":8,\"moduleSha256\":\"x\",\"snapshot\":{},\"log\":[]}");
            var ex = Assert.Throws<InvalidDataException>(() => NativeSave.Read(path));
            Assert.Contains("7", ex.Message);
            File.WriteAllText(path, "{not json");
            Assert.IsType<JsonException>(Assert.Throws<InvalidDataException>(() => NativeSave.Read(path)).InnerException);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeSave_Format3_RoundTrip()
    {
        var st = new GameState { GameId = "g" }; st.MapOrder.Add("m1");
        var sess = new GameSession(st, "h");
        var at = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(1));
        sess.Execute(new AddPiece("a", "t", "", "m1", new PointD(1, 2), null, null, at, "Ann"));
        sess.Execute(new SetNotes("remember", at));
        sess.Execute(new SetTurn(5, "Supply", at));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            NativeSave.Write(path, sess);
            Assert.Equal(7, NativeSave.FormatVersion);
            var back = NativeSave.Read(path);
            Assert.Equal(3, back.State.Log.Count);
            Assert.Equal(sess.State.Log, back.State.Log);
            Assert.Equal(at, back.State.Log[0].At);
            Assert.Equal("remember", back.State.Notes);
            Assert.Equal(new TurnState(5, "Supply"), back.State.Turn);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeSave_ReadsFormat2_EmptyLogNotesTurn1()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            File.WriteAllText(path, "{\"format\":2,\"moduleSha256\":\"x\",\"snapshot\":{\"pieces\":{},\"stacks\":{},\"mapOrder\":[]},\"log\":[]}");
            var s = NativeSave.Read(path);
            Assert.Empty(s.State.Log);
            Assert.Equal("", s.State.Notes);
            Assert.Equal(new TurnState(1, null), s.State.Turn);
            File.WriteAllText(path, "{\"format\":3,\"moduleSha256\":\"x\",\"snapshot\":{\"pieces\":{},\"stacks\":{},\"mapOrder\":[],\"notes\":null,\"log\":[{\"seq\":1,\"kind\":\"weird\",\"text\":\"t\",\"at\":\"2026-01-01T00:00:00+00:00\"}]},\"log\":[]}");
            s = NativeSave.Read(path);
            Assert.Equal("", s.State.Notes);
            Assert.Equal("weird", s.State.Log[0].Kind);
            File.WriteAllText(path, "{\"format\":3,\"moduleSha256\":\"x\",\"snapshot\":{\"pieces\":{},\"stacks\":{},\"mapOrder\":[],\"log\":null},\"log\":[]}");
            Assert.Throws<InvalidDataException>(() => NativeSave.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeSave_RoundTrip_NestedTraitStates()
    {
        var (type, state, expected) = GameStateTests.NestedPiece();
        var st = new GameState { GameId = "g" }; st.MapOrder.Add("m1");
        var sess = new GameSession(st, "h");
        sess.Execute(new AddPiece("a", type, state, "m1", new PointD(0, 0), null));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            NativeSave.Write(path, sess);
            Assert.Equal(expected, NativeSave.Read(path).State.Pieces["a"].TraitStates);
        }
        finally { File.Delete(path); }
    }

    private static void AssertRejected(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            File.WriteAllText(path, json);
            Assert.Throws<InvalidDataException>(() => NativeSave.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeSave_NullSnapshot_IsInvalidData() =>
        AssertRejected("{\"format\":1,\"moduleSha256\":\"x\",\"snapshot\":null,\"log\":[]}");

    [Fact]
    public void NativeSave_NullLogOrNullTraitStates_IsInvalidData()
    {
        AssertRejected("{\"format\":1,\"moduleSha256\":\"x\",\"snapshot\":{\"pieces\":{},\"stacks\":{},\"mapOrder\":[]},\"log\":null}");
        AssertRejected("{\"format\":1,\"moduleSha256\":\"x\",\"snapshot\":{\"pieces\":{\"a\":{\"id\":\"a\",\"traitStates\":null}},\"stacks\":{},\"mapOrder\":[]},\"log\":[]}");
    }

    [Fact]
    public void NativeSave_LogEntryMissingTypeDiscriminator_IsInvalidData() =>
        AssertRejected("{\"format\":1,\"moduleSha256\":\"x\",\"snapshot\":{\"pieces\":{},\"stacks\":{},\"mapOrder\":[]},\"log\":[{\"pieceId\":\"a\"}]}");

    // ---- format 5 ----
    static string Json(GameState s) => JsonSerializer.Serialize(s, QuartermasterJson.Options);
    static string Tmp() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");

    static GameSession Played()
    {
        var st = new GameState { GameId = "g" }; st.MapOrder.Add("m1");
        var g = new GameSession(st, "sha");
        using (g.BeginAction()) g.Execute(new AddPiece("a", "piece;;;a.png;a", "", "m1", new PointD(1, 2), null));
        using (g.BeginAction())
        {
            g.Execute(new AdvancePhase(new PhasePosition(1, 1, "a", 0), new PhasePosition(1, 1, "b", 1), "Alpha", "Supply", null, null));
            g.Execute(new SetNotes("n", null));
        }
        using (g.BeginAction()) g.Execute(new MovePieces(["a"], "m1", new PointD(5, 5), null));
        return g;
    }

    [Fact]
    public void Format5_RoundTrip_FullUndoToGameStart()
    {
        var g = Played();
        var path = Tmp();
        try
        {
            NativeSave.Write(path, g);
            var back = NativeSave.Read(path);
            Assert.False(back.RebasedOnLoad);
            Assert.Null(back.LoadNotice);
            Assert.Equal(g.Actions, back.Actions);
            Assert.Equal(g.Checkpoints, back.Checkpoints);
            Assert.Equal(Json(g.State), Json(back.State));
            Assert.True(back.CanUndo);
            back.RewindTo(0);
            Assert.Equal(Json(g.Genesis), Json(back.State));
            Assert.Empty(back.State.Pieces);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OlderSave_SummaryLinesWithoutFormulaId_KeepsHistory()
    {
        // Calc summaries written by the reducer (batch resolve, phase advance past a pending run) are now stamped LogKind.NoWorking;
        // a save made before that has no formulaId on them and must still pass the replay check.
        var st = new GameState { GameId = "g" }; st.MapOrder.Add("m1");
        var g = new GameSession(st, "sha");
        using (g.BeginAction())
        {
            g.Execute(new AddPiece("a", "piece;;;a.png;a", "", "m1", new PointD(1, 2), null));
            g.Execute(new AddPiece("b", "piece;;;a.png;b", "", "m1", new PointD(3, 2), null));
        }
        var units = new[] { new PendingUnit("a", "a", null, false), new PendingUnit("b", "b", null, false) };
        using (g.BeginAction())
            g.Execute(new QueueRuns(new[] { new PendingRun("r1", "f1", "Supply", "start", units, true), new PendingRun("r2", "f2", "Water", "start", units, true) }));
        using (g.BeginAction()) g.Execute(new ResolveRun("r1", new string?[] { "a", "b" }));
        using (g.BeginAction())
            g.Execute(new AdvancePhase(new PhasePosition(1, 1, "a", 0), new PhasePosition(1, 1, "b", 1), "Alpha", "Supply", null, null));
        var stamped = g.State.Log.Count(l => l.FormulaId == LogKind.NoWorking);
        Assert.True(stamped >= 2, $"only {stamped} stamped summary lines");

        var path = Tmp();
        try
        {
            NativeSave.Write(path, g);
            var text = File.ReadAllText(path);
            var old = text.Replace(",\"formulaId\":\"-\"", "");
            Assert.NotEqual(text, old);
            Assert.DoesNotContain("formulaId\":\"-", old);
            File.WriteAllText(path, old);
            var back = NativeSave.Read(path);
            Assert.False(back.RebasedOnLoad);
            Assert.Null(back.LoadNotice);
            Assert.True(back.CanUndo);
            Assert.Equal(g.Actions, back.Actions);
            Assert.Equal(stamped, back.State.Log.Count(l => l.FormulaId == LogKind.NoWorking));   // the replayed state is kept
            back.RewindTo(0);
            Assert.Empty(back.State.Pending);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Format4File_LoadsWithFloorAtLoadPoint()
    {
        var path = Tmp();
        try
        {
            File.WriteAllText(path, "{\"format\":4,\"moduleSha256\":\"x\",\"snapshot\":{\"pieces\":{},\"stacks\":{},\"mapOrder\":[\"m1\"]},\"log\":[]}");
            var s = NativeSave.Read(path);
            Assert.False(s.CanUndo);
            Assert.Null(s.LoadNotice);
            Assert.True(s.RebasedOnLoad);
            var loaded = Json(s.State);
            s.Execute(new AddPiece("a", "piece;;;a.png;a", "", "m1", new PointD(1, 2), null));
            NativeSave.Write(path, s);
            Assert.Contains("\"format\":7", File.ReadAllText(path).Replace(" ", ""));
            var back = NativeSave.Read(path);
            Assert.Single(back.Log);
            Assert.Equal(loaded, Json(back.Genesis));
            Assert.Equal(Json(s.State), Json(back.State));
            Assert.Null(back.LoadNotice);
        }
        finally { File.Delete(path); }
    }

    static void Tamper(string path, Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(node);
        File.WriteAllText(path, node.ToJsonString());
    }

    static void AssertRebased(string path, GameSession original)
    {
        var s = NativeSave.Read(path);
        Assert.False(s.CanUndo);
        Assert.True(s.RebasedOnLoad);
        Assert.Equal("Undo history in this save did not match the game; undo starts from here.", s.LoadNotice);
        Assert.Equal(Json(original.State), Json(s.State));
        Assert.Empty(s.Log);
    }

    [Fact]
    public void TamperedLog_RemovedEntry_RebasesWithNotice()
    {
        var g = Played(); var path = Tmp();
        try
        {
            NativeSave.Write(path, g);
            Tamper(path, o => o["log"]!.AsArray().RemoveAt(0));
            AssertRebased(path, g);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReplayMismatch_PassesStructure_RebasesWithNotice()
    {
        var g = Played(); var path = Tmp();
        try
        {
            NativeSave.Write(path, g);
            // Structure intact (marks, checkpoints, genesis) but the stored snapshot no longer equals genesis + log.
            Tamper(path, o => o["snapshot"]!["notes"] = "tampered");
            var s = NativeSave.Read(path);
            Assert.False(s.CanUndo);
            Assert.True(s.RebasedOnLoad);
            Assert.Equal("Undo history in this save did not match the game; undo starts from here.", s.LoadNotice);
            Assert.Equal("replay mismatch", s.LoadDiagnostic);
            Assert.Equal("tampered", s.State.Notes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ActionMarkPastLog_RebasesWithNotice()
    {
        var g = Played(); var path = Tmp();
        try
        {
            NativeSave.Write(path, g);
            Tamper(path, o => o["actions"]!.AsArray()[1]!["start"] = 99);
            AssertRebased(path, g);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BadCheckpointsOrGenesis_RebaseWithNotice()
    {
        var g = Played(); var path = Tmp();
        try
        {
            NativeSave.Write(path, g);
            Tamper(path, o => o["checkpoints"]!.AsArray()[0]!["logIndex"] = 1);
            AssertRebased(path, g);
            NativeSave.Write(path, g);
            Tamper(path, o => o["checkpoints"]!.AsArray()[1]!["logIndex"] = 99);
            AssertRebased(path, g);
            NativeSave.Write(path, g);
            Tamper(path, o => o["genesis"]!["pieces"] = null);
            AssertRebased(path, g);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Write_UsesTempThenMove_NoTmpLeft()
    {
        var g = Played(); var path = Tmp();
        try
        {
            File.WriteAllText(path, "old");
            NativeSave.Write(path, g);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Contains("\"genesis\"", File.ReadAllText(path));
            Assert.Equal(Json(g.State), Json(NativeSave.Read(path).State));
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }
}
