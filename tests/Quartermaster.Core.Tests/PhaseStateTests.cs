using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class PhaseStateTests
{
    static readonly DateTimeOffset At = new(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);
    static PhasePosition P(int turn, string id, int idx, int rep = 1) => new(turn, rep, id, idx);

    static PendingRun Run(string id, string name, int n, bool applied = false) =>
        new(id, "f", name, "start",
            Enumerable.Range(0, n).Select(i => new PendingUnit("u" + i, ((char)('A' + i)).ToString(), null, applied)).ToList(), false);

    [Fact]
    public void AdvancePhase_SetsPositionTurn_LogsArrowText()
    {
        var s = new GameState();
        s.Apply(new AdvancePhase(P(3, "mv", 0), P(3, "cb", 1), "Movement", "Combat"));
        Assert.Equal(P(3, "cb", 1), s.Position);
        Assert.Equal(new TurnState(3, "Combat"), s.Turn);
        Assert.Equal(LogKind.Turn, s.Log[^1].Kind);
        Assert.Equal("Turn 3 · Movement → Combat", s.Log[^1].Text);
    }

    [Fact]
    public void AdvancePhase_FromNull_LogsStartText()
    {
        var s = new GameState();
        s.Apply(new AdvancePhase(null, P(1, "a", 0), "", "Alpha"));
        Assert.Equal("Turn 1 · Alpha", s.Log[^1].Text);
        Assert.Equal(LogKind.Turn, s.Log[^1].Kind);
    }

    [Fact]
    public void AdvancePhase_DropsPending_LogsSkippedCapped()
    {
        var s = new GameState();
        s.Apply(new QueueRuns([Run("r1", "Fuel for movement", 5)]));
        s.Ticked.Add(1); s.Acknowledged.Add(2);
        s.Apply(new AdvancePhase(P(1, "a", 0), P(1, "b", 1), "A", "B"));
        Assert.Empty(s.Pending); Assert.Empty(s.Ticked); Assert.Empty(s.Acknowledged);
        Assert.Equal(3, s.Log.Count);
        Assert.Equal(LogKind.Calc, s.Log[0].Kind);
        Assert.Equal("skipped: Fuel for movement, A, B, C +2 more", s.Log[0].Text);
        Assert.Equal("Fuel for movement: 5 units, 0 applied", s.Log[1].Text);
        Assert.Equal(LogKind.Turn, s.Log[2].Kind);
    }

    [Fact]
    public void AdvancePhase_WithReason_LogsSystem()
    {
        var s = new GameState();
        s.Apply(new AdvancePhase(P(1, "a", 0), P(1, "b", 0), "A", "B", "Phase removed"));
        Assert.Equal(LogKind.System, s.Log[^1].Kind);
        Assert.Equal("Phase removed", s.Log[^1].Text);
        Assert.Equal("B", s.Turn.Phase);
    }

    [Fact]
    public void SetDifficulty_Logs()
    {
        var s = new GameState();
        Assert.Equal(Difficulty.SemiAuto, s.Difficulty);
        s.Apply(new SetDifficulty(Difficulty.FullyAuto));
        Assert.Equal(Difficulty.FullyAuto, s.Difficulty);
        Assert.Equal(LogKind.System, s.Log[^1].Kind);
        Assert.Equal("Difficulty changed to Fully automatic", s.Log[^1].Text);
        Assert.Equal("Hardcore", DifficultyText.Display(Difficulty.Hardcore));
        Assert.Equal("Semi-automatic", DifficultyText.Display(Difficulty.SemiAuto));
    }

    [Fact]
    public void TickChecklist_ToggleAndLog()
    {
        var s = new GameState();
        s.Apply(new TickChecklist(2, true, "Check supply"));
        Assert.Equal([2], s.Ticked);
        Assert.Equal("Done: Check supply", s.Log[^1].Text);
        s.Apply(new TickChecklist(2, false, "Check supply"));
        Assert.Empty(s.Ticked);
        Assert.Equal("Not done: Check supply", s.Log[^1].Text);
        Assert.Equal(LogKind.Turn, s.Log[^1].Kind);
    }

    [Fact]
    public void AcknowledgeWarning_Recorded()
    {
        var s = new GameState();
        s.Apply(new AcknowledgeWarning(1, "Low fuel"));
        s.Apply(new AcknowledgeWarning(1, "Low fuel"));
        Assert.Equal([1], s.Acknowledged);
        Assert.Equal("Acknowledged: Low fuel", s.Log[0].Text);
        Assert.Equal(LogKind.Turn, s.Log[0].Kind);
    }

    [Fact]
    public void ResolveRun_AllApplied_RemovesRunAndLogsSummary()
    {
        var s = new GameState();
        s.Apply(new QueueRuns([Run("r1", "Daily water", 3)]));
        Assert.Empty(s.Log);
        s.Apply(new ResolveRun("r1", ["u0", "u1"]));
        Assert.Single(s.Pending);
        Assert.Empty(s.Log);
        s.Apply(new ResolveRun("r1", ["u2"]));
        Assert.Empty(s.Pending);
        Assert.Equal("Daily water: 3 units, 3 applied", s.Log[^1].Text);
        Assert.Equal(LogKind.Calc, s.Log[^1].Kind);
        s.Apply(new ResolveRun("nope", null!));
        s.Apply(new QueueRuns(null!));
    }

    [Fact]
    public void LedgerDelta_TagSubject_CalcKindAndText()
    {
        var s = new GameState();
        s.Apply(new LedgerDelta("p:a", "fuel", 10, 7, "formula", "f", null, null, "10 - 3 = 7", null, null, "Fuel", "Unit A"));
        Assert.Equal(LogKind.Calc, s.Log[0].Kind);
        Assert.Equal("Unit A: 10 - 3 = 7 (Fuel)", s.Log[0].Text);
        Assert.Equal("a", s.Log[0].PieceId);
        s.Apply(new LedgerDelta("h:g", "x", 1, 2, "formula", "f", null, null, "w", null, null, null, "Subj"));
        Assert.Equal("Subj: w", s.Log[1].Text);
        Assert.Equal(LogKind.Formula, s.Log[1].Kind);
        Assert.Null(s.Log[1].PieceId);
    }

    [Fact]
    public void LogEntry_TaggedWithTurnAndPhase_AfterStart()
    {
        var s = new GameState();
        s.Apply(new SetNotes("a"));
        Assert.Null(s.Log[0].Turn); Assert.Null(s.Log[0].Phase);
        s.Apply(new AdvancePhase(null, P(2, "x", 0), "", "Alpha"));
        s.Apply(new SetNotes("b"));
        Assert.Equal(2, s.Log[^1].Turn);
        Assert.Equal("Alpha", s.Log[^1].Phase);
    }

    [Fact]
    public void Commands_JsonDiscriminators_New()
    {
        GameCommand[] cmds =
        [
            new AdvancePhase(null, P(1, "a", 0), "", "A", null, At, "x"), new SetDifficulty(Difficulty.Hardcore, At),
            new TickChecklist(1, true, "i"), new AcknowledgeWarning(0, "m"), new QueueRuns([Run("r", "F", 2)]),
            new ResolveRun("r", ["u0"]), new AddLogLine("calc", "t", "p1"),
        ];
        string[] names = ["advancePhase", "setDifficulty", "tickChecklist", "acknowledgeWarning", "queueRuns", "resolveRun", "addLogLine"];
        for (var i = 0; i < cmds.Length; i++)
        {
            var j = JsonSerializer.Serialize(cmds[i], QuartermasterJson.Options);
            Assert.Contains($"\"$t\":\"{names[i]}\"", j);
            var back = JsonSerializer.Deserialize<GameCommand>(j, QuartermasterJson.Options)!;
            Assert.Equal(cmds[i].GetType(), back.GetType());
            Assert.Equal(j, JsonSerializer.Serialize(back, QuartermasterJson.Options));
        }
    }

    [Fact]
    public void Replay_SameCommands_IdenticalJson()
    {
        GameCommand[] cmds =
        [
            new QueueRuns([Run("r", "F", 2)]), new AdvancePhase(null, P(1, "a", 0), "", "A", null, At),
            new SetDifficulty(Difficulty.Hardcore, At), new QueueRuns([Run("r2", "G", 4)]), new ResolveRun("r2", ["u0", "u1"]),
            new TickChecklist(0, true, "i", At), new AcknowledgeWarning(1, "m", At), new AddLogLine("system", "hi", null, At),
            new AdvancePhase(P(1, "a", 0), P(1, "b", 1), "A", "B", null, At),
        ];
        var a = new GameState(); var b = new GameState();
        foreach (var c in cmds) { a.Apply(c); b.Apply(c); }
        var j = JsonSerializer.Serialize(a, QuartermasterJson.Options);
        Assert.Equal(j, JsonSerializer.Serialize(b, QuartermasterJson.Options));
        Assert.Equal(j, JsonSerializer.Serialize(a.DeepClone(), QuartermasterJson.Options));
    }

    [Fact]
    public void NativeSave_Format4_RoundTrip()
    {
        var sess = new GameSession(new GameState { GameId = "g" }, "h");
        sess.Execute(new AdvancePhase(null, P(2, "a", 0, 2), "", "A", null, At));
        sess.Execute(new SetDifficulty(Difficulty.Hardcore, At));
        sess.Execute(new QueueRuns([Run("r", "F", 2)]));
        sess.Execute(new TickChecklist(1, true, "i", At));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            NativeSave.Write(path, sess);
            Assert.Equal(7, NativeSave.FormatVersion);
            var back = NativeSave.Read(path);
            Assert.Equal(P(2, "a", 0, 2), back.State.Position);
            Assert.Equal(Difficulty.Hardcore, back.State.Difficulty);
            Assert.Equal([1], back.State.Ticked);
            Assert.Single(back.State.Pending);
            Assert.Equal(2, back.State.Pending[0].Units.Count);
            Assert.Equal(sess.State.Log, back.State.Log);
            Assert.Equal(JsonSerializer.Serialize(sess.State, QuartermasterJson.Options), JsonSerializer.Serialize(back.State, QuartermasterJson.Options));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeSave_ReadsFormat3_BeforeTurn1_SemiAuto()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            File.WriteAllText(path, "{\"format\":3,\"moduleSha256\":\"x\",\"snapshot\":{\"pieces\":{},\"stacks\":{},\"mapOrder\":[]},\"log\":[]}");
            var s = NativeSave.Read(path).State;
            Assert.Null(s.Position);
            Assert.Equal(Difficulty.SemiAuto, s.Difficulty);
            Assert.Empty(s.Pending);
            File.WriteAllText(path, "{\"format\":4,\"moduleSha256\":\"x\",\"snapshot\":{\"pieces\":{},\"stacks\":{},\"mapOrder\":[],\"ticked\":null,\"acknowledged\":null,\"pending\":[{\"id\":\"r\",\"units\":null},{\"id\":\"q\",\"units\":[]}]},\"log\":[]}");
            s = NativeSave.Read(path).State;
            Assert.Empty(s.Ticked); Assert.Empty(s.Acknowledged);
            Assert.Single(s.Pending);
            Assert.Equal("q", s.Pending[0].Id);
        }
        finally { File.Delete(path); }
    }
}
