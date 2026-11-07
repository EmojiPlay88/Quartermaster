using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class CheckpointTests
{
    static GameSession NewSession()
    {
        var s = new GameState();
        s.MapOrder.Add("m1");
        return new GameSession(s, "sha");
    }

    static string Json(GameState s) => JsonSerializer.Serialize(s, QuartermasterJson.Options);
    static AddPiece Add(string id) => new(id, "piece;;;a.png;" + id, "", "m1", new PointD(1, 2), null);
    static MovePieces Move(string id, double x = 5) => new([id], "m1", new PointD(x, 5), null);
    static RollDice Roll() => new("test roll", 2, 6, [3, 4]);
    static LedgerDelta Delta(string key, string prop, double a, double b) => new(key, prop, a, b, "manual", null, null, null, null);
    static PhasePosition P(int turn, string id, int idx) => new(turn, 1, id, idx);
    static AdvancePhase Adv(string to, string? reason = null, DateTimeOffset? at = null) =>
        new(P(1, "a", 0), P(1, "b", 1), "Alpha", to, reason, at);

    [Fact]
    public void GameStart_IsCheckpointZero()
    {
        var g = NewSession();
        var c = Assert.Single(g.Checkpoints);
        Assert.Equal(new Checkpoint(0, g.Genesis.Turn.Turn, g.Genesis.Position, "game start", null), c);
    }

    [Fact]
    public void AdvancePhaseAction_AddsCheckpointAtActionEnd()
    {
        var g = NewSession();
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        using (g.BeginAction())
        {
            g.Execute(Adv("Supply A", null, at));
            g.Execute(Delta("k1", "fuel", 1, 2));
            g.Execute(Delta("k2", "fuel", 1, 2));
            Assert.Single(g.Checkpoints); // not until the action closes
        }
        Assert.Equal(2, g.Checkpoints.Count);
        var c = g.Checkpoints[1];
        Assert.Equal(3, c.LogIndex);
        Assert.Equal("Supply A", c.Label);
        Assert.Equal(at, c.At);
        Assert.Equal(P(1, "b", 1), c.Position);
    }

    [Fact]
    public void AdvancePhase_OneCommandAction_AddsCheckpoint()
    {
        var g = NewSession();
        g.Execute(Adv("Beta"));
        Assert.Equal(1, g.Checkpoints[1].LogIndex);
    }

    [Fact]
    public void ReconcileAdvance_WithReason_IsNotCheckpoint()
    {
        var g = NewSession();
        g.Execute(Adv("Beta", "reconcile"));
        Assert.Single(g.Checkpoints);
    }

    [Fact]
    public void SetTurn_IsCheckpoint()
    {
        var g = NewSession();
        g.Execute(new SetTurn(4, null));
        var c = g.Checkpoints[1];
        Assert.Equal("Turn 4", c.Label);
        Assert.Equal(4, c.Turn);
        Assert.Equal(1, c.LogIndex);
    }

    [Fact]
    public void RewindTo_DropsLaterCheckpoints_KeepsTarget()
    {
        var g = NewSession();
        g.Execute(Adv("One"));
        g.Execute(Add("a"));
        g.Execute(Adv("Two"));
        g.Execute(Add("b"));
        g.Execute(Adv("Three"));
        Assert.Equal(new[] { 0, 1, 3, 5 }, g.Checkpoints.Select(c => c.LogIndex));
        g.RewindTo(3);
        Assert.Equal(new[] { 0, 1, 3 }, g.Checkpoints.Select(c => c.LogIndex));
    }

    [Fact]
    public void RewindToCheckpoint_KeepsOnEntryResults()
    {
        var g = NewSession();
        g.Execute(new SeedLedger([new LedgerSeed("k1", "fuel", 10)]));
        using (g.BeginAction())
        {
            g.Execute(Adv("Supply A"));
            g.Execute(Delta("k1", "fuel", 10, 7));
        }
        var expected = Json(g.State);
        var cp = g.Checkpoints[1].LogIndex;
        g.Execute(Delta("k1", "fuel", 7, 3));
        g.Execute(Add("x"));
        g.RewindTo(cp);
        Assert.Equal(expected, Json(g.State));
        Assert.Equal(2, g.Checkpoints.Count);
    }

    [Fact]
    public void Preview_CountsActionsDiceLedgerPieces()
    {
        var g = NewSession();
        g.Execute(Add("p1")); g.Execute(Add("p2")); g.Execute(Add("p3")); g.Execute(Add("p4"));
        g.Execute(new SeedLedger([new LedgerSeed("a", "fuel", 1)]));
        var at = g.Log.Count; // 5
        using (g.BeginAction())
        {
            g.Execute(Roll());
            g.Execute(Delta("a", "fuel", 1, 2));
            g.Execute(Delta("b", "fuel", 1, 2));
            g.Execute(Delta("a", "fuel", 2, 3)); // same key again: one unit
            g.Execute(Delta("c", "fuel", 1, 2));
            g.Execute(Delta("x", "ammo", 1, 2));
        }
        g.Execute(Roll());
        g.Execute(new MovePieces(["p1", "p2"], "m1", new PointD(9, 9), null));
        g.Execute(new MovePieces(["p2", "p3", "p4"], "m1", new PointD(8, 8), null)); // p2 counted once
        g.Execute(Add("p5"));
        g.Execute(new SetStackExpanded("none", true)); // folding: nothing
        var p = g.Preview(at);
        Assert.Equal(5, p.Actions);
        Assert.Equal(2, p.DiceRolls);
        Assert.Equal(new[] { ("fuel", 3), ("ammo", 1) }, p.Ledger);
        Assert.Equal(4, p.PiecesMoved);
        Assert.Equal(1, p.PiecesAdded);
        Assert.Equal(0, p.PiecesRemoved);
    }

    [Fact]
    public void Preview_AtHead_IsEmpty()
    {
        var g = NewSession();
        g.Execute(Add("a"));
        var p = g.Preview(g.Log.Count);
        Assert.Equal(0, p.Actions);
        Assert.Empty(p.Ledger);
    }

    [Fact]
    public void SteppedBack_LogLine_Text()
    {
        var g = NewSession();
        g.Execute(new SteppedBack(5, 14, 2, "Turn 3 · Supply A"));
        g.Execute(new SteppedBack(0, 1, 0, "game start"));
        Assert.Equal("Stepped back to Turn 3 · Supply A. Undid 14 actions, 2 dice rolls.", g.State.Log[0].Text);
        Assert.Equal("Stepped back to game start. Undid 1 action.", g.State.Log[1].Text);
        Assert.Equal(LogKind.System, g.State.Log[0].Kind);
    }

    [Fact]
    public void SteppedBack_IsFolding_JsonRoundTrip()
    {
        Assert.True(GameSession.IsFolding(new SteppedBack(1, 2, 3, "t")));
        var g = NewSession();
        g.Execute(Add("a"));
        g.Execute(new SteppedBack(0, 1, 0, "game start"));
        Assert.Single(g.Actions);
        GameCommand c = new SteppedBack(3, 4, 5, "Turn 2", null, "me");
        var json = JsonSerializer.Serialize(c, QuartermasterJson.Options);
        Assert.Contains("\"steppedBack\"", json);
        Assert.Equal(c, JsonSerializer.Deserialize<GameCommand>(json, QuartermasterJson.Options));
    }

    [Fact]
    public void Rebase_ResetsCheckpoints()
    {
        var g = NewSession();
        g.Execute(Adv("One"));
        g.Rebase();
        Assert.Single(g.Checkpoints);
        Assert.Equal(0, g.Checkpoints[0].LogIndex);
    }

    // ---- Task 1 review follow-ups ----

    [Fact]
    public void RewindTo_MidAction_TailRedoneAsOwnEntry()
    {
        var g = NewSession();
        using (g.BeginAction("batch"))
        {
            g.Execute(Add("a")); g.Execute(Add("b")); g.Execute(Add("c"));
        }
        g.Execute(Add("d"));
        var removed = g.RewindTo(1);
        Assert.Equal(2, removed.Count);
        Assert.Equal("batch", removed[0].Label);
        Assert.Equal(new[] { "b", "c" }, removed[0].Commands.Cast<AddPiece>().Select(x => x.PieceId));
        Assert.Equal(new[] { "d" }, removed[1].Commands.Cast<AddPiece>().Select(x => x.PieceId));
        Assert.Single(g.Actions); // the cut action keeps its mark
        g.Redo();
        Assert.Equal(new[] { "a", "b", "c" }, g.Log.Cast<AddPiece>().Select(x => x.PieceId));
        g.Redo();
        Assert.Equal(4, g.Log.Count);
    }

    [Fact]
    public void RewindTo_MidActionLast_TailNotLost()
    {
        var g = NewSession();
        using (g.BeginAction("batch")) { g.Execute(Add("a")); g.Execute(Add("b")); }
        var removed = g.RewindTo(1);
        var t = Assert.Single(removed);
        Assert.Equal("batch", t.Label);
        Assert.True(g.CanRedo);
    }

    [Fact]
    public void ActionLabels_SingleMove_Singular()
    {
        Assert.Equal("move of 1 piece", ActionLabels.For(new MovePieces(["a"], "m1", new PointD(0, 0), null), 1));
    }

    [Fact]
    public void Rewind_WhileScopeOpen_Throws()
    {
        var g = NewSession();
        g.Execute(Add("a"));
        using (g.BeginAction())
        {
            g.Execute(Add("b"));
            Assert.Throws<InvalidOperationException>(() => g.RewindTo(0));
            Assert.Throws<InvalidOperationException>(() => g.Undo());
        }
        Assert.Equal(2, g.Log.Count);
    }
}
