using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class GameHistoryTests
{
    static GameState Make()
    {
        var s = new GameState();
        s.MapOrder.Add("m1"); s.MapOrder.Add("m2");
        return s;
    }

    static GameSession NewSession() => new(Make(), "sha");

    static string Json(GameState s) => JsonSerializer.Serialize(s, QuartermasterJson.Options);

    static AddPiece Add(string id) => new(id, "piece;;;a.png;" + id, "", "m1", new PointD(1, 2), null);
    static MovePieces Move(string id, double x = 5) => new([id], "m1", new PointD(x, 5), null);
    static RollDice Roll() => new("test roll", 2, 6, [3, 4]);
    static LedgerDelta Delta(double a, double b) => new("k", "fuel", a, b, "manual", null, null, null, null);
    static PhasePosition P(int turn, string id, int idx) => new(turn, 1, id, idx);

    static List<GameCommand> Mixed()
    {
        var l = new List<GameCommand>();
        l.Add(new SeedLedger([new LedgerSeed("k", "fuel", 10)]));
        for (int i = 0; i < 6; i++) l.Add(Add("p" + i));
        for (int i = 0; i < 6; i++) l.Add(Move("p" + i, i + 3));
        for (int i = 0; i < 6; i++) l.Add(new SetTraitState("p" + i, 0, "s" + i));
        for (int i = 0; i < 10; i++) l.Add(Delta(10 - i, 9 - i));
        for (int i = 0; i < 6; i++) l.Add(Roll());
        l.Add(new SetNotes("hello"));
        l.Add(new SetTurn(2, "Alpha"));
        l.Add(new AdvancePhase(P(2, "a", 0), P(2, "b", 1), "Alpha", "Beta"));
        for (int i = 0; i < 6; i++) l.Add(new SetStackExpanded("nostack" + i, true));
        for (int i = 0; i < 3; i++) l.Add(new RemovePiece("p" + i));
        while (l.Count < 60) l.Add(new SetNotes("n" + l.Count));
        return l;
    }

    [Fact]
    public void RewindTo_EveryIndex_MatchesRecordedState()
    {
        var cmds = Mixed();
        Assert.Equal(60, cmds.Count);
        var g = NewSession();
        var rec = new List<string> { Json(g.State) };
        foreach (var c in cmds) { g.Execute(c); rec.Add(Json(g.State)); }
        Assert.Equal(Json(g.Genesis), rec[0]);

        for (int i = 0; i <= cmds.Count; i++)
        {
            var f = NewSession();
            foreach (var c in cmds) f.Execute(c);
            f.RewindTo(i);
            Assert.Equal(i, f.Log.Count);
            Assert.Equal(rec[i], Json(f.State));
        }
    }

    [Fact]
    public void BeginAction_GroupsCommands_UndoRemovesAll()
    {
        var g = NewSession();
        using (g.BeginAction("group"))
        {
            g.Execute(Add("a")); g.Execute(Move("a")); g.Execute(new SetNotes("x"));
        }
        Assert.Single(g.Actions);
        Assert.Equal(3, g.Log.Count);
        g.Undo();
        Assert.Empty(g.Log);
        Assert.Equal(Json(g.Genesis), Json(g.State));
    }

    [Fact]
    public void NestedScopes_JoinOuter()
    {
        var g = NewSession();
        using (g.BeginAction("outer"))
        {
            g.Execute(Add("a"));
            using (g.BeginAction("inner")) { g.Execute(Move("a")); }
            g.Execute(new SetNotes("x"));
        }
        Assert.Single(g.Actions);
        Assert.Equal("outer", g.UndoLabel);
        Assert.Equal(3, g.Log.Count);
    }

    [Fact]
    public void EmptyScope_CreatesNoAction()
    {
        var g = NewSession();
        using (g.BeginAction("nothing")) { }
        Assert.Empty(g.Actions);
        Assert.False(g.CanUndo);
    }

    [Fact]
    public void FoldingCommand_JoinsPreviousAction()
    {
        var g = NewSession();
        g.Execute(Add("a"));
        g.Execute(Move("a"));
        g.Execute(new SetStackExpanded("s", true));
        Assert.Equal(2, g.Actions.Count);
        Assert.Equal(1, g.Actions[1].Start);
        g.Undo();
        Assert.Single(g.Log);
    }

    [Fact]
    public void FoldingCommand_JoinsSingleMove_OneAction()
    {
        var g = NewSession();
        g.Execute(Move("a"));
        g.Execute(new SetStackExpanded("s", true));
        Assert.Single(g.Actions);
        Assert.Equal(2, g.Log.Count);
    }

    [Fact]
    public void FoldingCommand_BeforeAnyAction_IsOrphan_NotUndone()
    {
        var g = NewSession();
        g.Execute(new SeedLedger([new LedgerSeed("k", "fuel", 10)]));
        g.Execute(Add("a"));
        g.Undo();
        Assert.Single(g.Log);
        Assert.IsType<SeedLedger>(g.Log[0]);
        Assert.False(g.CanUndo);
    }

    [Fact]
    public void Redo_ReappliesSameCommands_StateIdentical()
    {
        var g = NewSession();
        g.Execute(Add("a"));
        g.Execute(Roll());
        using (g.BeginAction("pair")) { g.Execute(Move("a")); g.Execute(new SetNotes("n")); }
        var before = Json(g.State);
        var log = g.Log.ToList();
        var labels = g.Actions.Select(a => a.Label).ToList();

        g.Undo(); g.Undo();
        Assert.True(g.CanRedo);
        g.Redo(); g.Redo();
        Assert.False(g.CanRedo);
        Assert.Equal(before, Json(g.State));
        Assert.Equal(log.Count, g.Log.Count);
        for (int i = 0; i < log.Count; i++) Assert.Equal(log[i], g.Log[i]);
        Assert.Equal(labels, g.Actions.Select(a => a.Label).ToList());
    }

    [Fact]
    public void Redo_DoesNotClearRemainingRedoStack()
    {
        var g = NewSession();
        g.Execute(Add("a")); g.Execute(Add("b")); g.Execute(Add("c"));
        g.Undo(); g.Undo(); g.Undo();
        g.Redo();
        Assert.True(g.CanRedo);
        g.Redo();
        Assert.True(g.CanRedo);
        g.Redo();
        Assert.False(g.CanRedo);
        Assert.Equal(3, g.Log.Count);
    }

    [Fact]
    public void RewindTo_ReturnsRemovedActions_EarliestRedoneFirst()
    {
        var g = NewSession();
        g.Execute(Add("a")); g.Execute(Add("b")); g.Execute(Add("c"));
        var removed = g.RewindTo(1);
        Assert.Equal(2, removed.Count);
        Assert.Equal("b", ((AddPiece)removed[0].Commands[0]).PieceId);
        Assert.Equal("c", ((AddPiece)removed[1].Commands[0]).PieceId);
        g.Redo();
        Assert.Equal("b", ((AddPiece)g.Log[^1]).PieceId);
    }

    [Fact]
    public void NewAction_ClearsRedo()
    {
        var g = NewSession();
        g.Execute(Add("a"));
        g.Undo();
        Assert.True(g.CanRedo);
        g.Execute(Add("b"));
        Assert.False(g.CanRedo);
    }

    [Fact]
    public void FoldingCommand_DoesNotClearRedo()
    {
        var g = NewSession();
        g.Execute(Add("a")); g.Execute(Add("b"));
        g.Undo();
        g.Execute(new SetStackExpanded("s", true));
        Assert.True(g.CanRedo);
    }

    [Fact]
    public void Undo_RaisesRewound_StateObjectReplaced()
    {
        var g = NewSession();
        g.Execute(Add("a"));
        var old = g.State;
        int n = 0;
        g.Rewound += () => n++;
        g.Undo();
        Assert.Equal(1, n);
        Assert.NotSame(old, g.State);
    }

    [Fact]
    public void Rebase_MakesCurrentStateGenesis_CannotUndo()
    {
        var g = NewSession();
        g.Execute(Add("a")); g.Execute(Add("b"));
        g.Undo();
        var json = Json(g.State);
        g.Rebase();
        Assert.False(g.CanUndo);
        Assert.False(g.CanRedo);
        Assert.Empty(g.Log);
        Assert.Equal(json, Json(g.Genesis));
        g.Undo();
        Assert.Equal(json, Json(g.State));
    }

    [Fact]
    public void UndoLabel_NamesAction()
    {
        var g = NewSession();
        using (g.BeginAction("Fuel formula on 2/7 Test")) g.Execute(Delta(1, 2));
        Assert.Equal("Fuel formula on 2/7 Test", g.UndoLabel);
        g.Undo();
        Assert.Equal("Fuel formula on 2/7 Test", g.RedoLabel);
    }

    [Fact]
    public void ActionLabels_Defaults()
    {
        Assert.Equal("move of Alpha", ActionLabels.For(new MovePieces(["a"], "m1", new PointD(0, 0), null, "Alpha moved to X"), 1));
        Assert.Equal("move of 2 pieces", ActionLabels.For(new MovePieces(["a", "b"], "m1", new PointD(0, 0), null), 1));
        Assert.Equal("roll test roll", ActionLabels.For(Roll(), 1));
        Assert.Equal("fuel change", ActionLabels.For(Delta(1, 2), 1));
        Assert.Equal("Next Phase", ActionLabels.For(new AdvancePhase(null, P(1, "a", 0), "", "A"), 1));
        Assert.Equal("clear moved", ActionLabels.For(new ClearMoved("m1"), 1));
    }

    [Fact]
    public void UnlabelledScope_LabelFromFirstCommand()
    {
        var g = NewSession();
        using (g.BeginAction()) { g.Execute(Add("a")); g.Execute(Add("b")); }
        Assert.Equal("add piece", g.UndoLabel);
    }

    [Fact]
    public void SnapshotCache_RewindUsesCachedSnapshot_SameResult()
    {
        var g = NewSession();
        var cmds = Mixed();
        string at30 = "";
        for (int i = 0; i < cmds.Count; i++)
        {
            g.Execute(cmds[i]);
            if (i == 29) { at30 = Json(g.State); g.CacheSnapshot(30); }
        }
        g.RewindTo(35);
        g.RewindTo(30);
        Assert.Equal(at30, Json(g.State));
    }

    [Fact]
    public void SnapshotCache_EvictsBeyondForty_RewindStillCorrect()
    {
        var g = NewSession();
        var rec = new List<string> { Json(g.State) };
        for (int i = 1; i <= 50; i++)
        {
            g.Execute(new SetNotes("n" + i)); g.CacheSnapshot(i);
            rec.Add(Json(g.State));
        }
        g.RewindTo(3);
        Assert.Equal(rec[3], Json(g.State));
        g.RewindTo(0);
        Assert.Equal(rec[0], Json(g.State));
    }
}
