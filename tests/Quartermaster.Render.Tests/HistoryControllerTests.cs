using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Invented pack and numbers from PhaseFixture; temp roots only.</summary>
public class HistoryControllerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-history-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public HistoryControllerTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private (ModuleSession S, PhaseController C, HistoryController H) Make(Difficulty d)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        Func<GameCommand, GameState, GameCommand> prepare = (c, _) => c;
        var phases = new PhaseController(s, prepare);
        return (s, phases, new HistoryController(s, phases, prepare));
    }

    private static string Json(GameState st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);
    private static MovePieces Move(string id, int x) => new(new[] { id }, "Main Map", new PointD(x, 50), null);
    private static double? Get(ModuleSession s, string id, string prop) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), prop);

    private static void ToMovementAndMove(PhaseController c)
    {
        c.Start();      // Weather
        c.Advance();    // Supply
        c.Advance();    // Movement
        c.ExecuteMove(new[] { Move("r1", 130), Move("r2", 180) });
    }

    [Fact]
    public void StepBack_LogsSteppedBackLine_AndSummary()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        ToMovementAndMove(c);
        var rewound = 0;
        h.Rewound += () => rewound++;
        var row = h.Checkpoints().Single(r => r.Phase == "Supply");
        Assert.Equal("Red", row.Side);
        Assert.Equal(1, row.Turn);
        Assert.Equal(1, row.ActionsSince);

        var summary = HistoryController.Describe(h.Preview(row));
        h.StepBack(row);

        Assert.Equal(string.Join(Environment.NewLine, "2 actions", "Ledger: Fuel on 2 units", "Pieces: 2 moved"), summary);
        Assert.Equal("ph-supply", s.Game!.State.Position!.PhaseId);
        Assert.IsType<SteppedBack>(s.Game.Log[^1]);
        Assert.Equal("Stepped back to Turn 1 · Supply. Undid 2 actions.", s.Game.State.Log[^1].Text);
        Assert.Equal(LogKind.System, s.Game.State.Log[^1].Kind);
        Assert.Equal(1, rewound);
        Assert.True(h.CanRedo);
    }

    [Fact]
    public void StepBack_ThenRedoAll_RemovesLine_StateIdentical()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        ToMovementAndMove(c);
        var played = Json(s.Game!.State);
        var log = s.Game.Log.Count;

        foreach (var phase in new[] { "Supply", "game start" })
        {
            h.StepBack(h.Checkpoints().Single(r => r.Phase == phase));
            Assert.Contains(s.Game.State.Log, l => l.Text.StartsWith("Stepped back to", StringComparison.Ordinal));
            for (var i = 0; i < 20 && h.CanRedo; i++) h.Redo();
            Assert.False(h.CanRedo);
            Assert.DoesNotContain(s.Game.Log, x => x is SteppedBack);
            Assert.Equal(log, s.Game.Log.Count);
            Assert.Equal(played, Json(s.Game.State));
        }
    }

    [Fact]
    public void StartOfPhase_AtCheckpoint_ReturnsPrevious()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        c.Start();
        c.Advance();   // at the Supply checkpoint, nothing done yet
        Assert.Equal("Weather", h.StartOfPhase()!.Phase);
        Assert.DoesNotContain(h.Checkpoints(), r => r.Phase == "Supply");

        c.ExecuteMove(new[] { Move("b1", 120) });
        Assert.Equal("Supply", h.StartOfPhase()!.Phase);
        Assert.Equal(new[] { "Supply", "Weather", "game start" }, h.Checkpoints().Select(r => r.Phase));
        Assert.Equal(s.Game!.Log.Count, h.Checkpoints().Max(r => r.LogIndex) + 1);
    }

    [Fact]
    public void UndoRedo_TextsAndFlags()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        c.Start();
        Assert.Equal("Undo Next Phase", h.UndoText);
        Assert.Equal("Redo", h.RedoText);
        Assert.False(h.AtFloorFromOldSave);
        var rewound = 0;
        h.Rewound += () => rewound++;
        h.Undo();
        Assert.Equal("Redo Next Phase", h.RedoText);
        Assert.Null(s.Game!.State.Position);
        h.Redo();
        Assert.Equal("ph-weather", s.Game.State.Position!.PhaseId);
        Assert.Equal(2, rewound);
    }

    [Fact]
    public void RulesChanged_ClearsRedo()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        c.Start();
        c.Advance();
        h.Undo();
        Assert.True(h.CanRedo);
        s.UpdateRules(p => p);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void SaveRules_KeepsRedo()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        c.Start();
        c.Advance();
        h.Undo();
        s.SaveRules(Path.Combine(_tmp, "r.qrules"));
        Assert.True(h.CanRedo);
    }

    // ---- fix round 1: no silent reroll, no undo loop ----

    /// <summary>Fully automatic play into turn 2 (the turn-2 Weather rolls a die), then a move in turn 2 Supply.</summary>
    private static void PlayIntoTurnTwo(ModuleSession s, PhaseController c)
    {
        c.Start();
        for (var i = 0; i < 30 && s.Game!.State.Position!.Turn < 2; i++) c.Advance();
        c.Advance();   // Supply, turn 2
        c.ExecuteMove(new[] { Move("r1", 130) });
    }

    private static CheckpointRow Row(HistoryController h, string phase, int turn) =>
        h.Checkpoints().First(r => r.Phase == phase && r.Turn == turn);

    private static (int Actions, int Dice) After(IReadOnlyList<GameCommand> log, IReadOnlyList<ActionMark> marks, int index) =>
        (marks.Count(a => a.Start >= index), log.Skip(index).Count(x => x is RollDice));

    private static void AssertRedoAllRestores(ModuleSession s, HistoryController h, string played)
    {
        // Each Redo re-logs one line covering only what is still undone; the last one leaves none.
        for (var i = 0; i < 40 && h.CanRedo; i++)
        {
            h.Redo();
            var redo = s.Game!.RedoActions;
            if (redo.Count == 0) break;
            var line = Assert.Single(s.Game.Log.OfType<SteppedBack>());
            Assert.Equal(redo.Count, line.ActionsUndone);
            Assert.Equal(redo.Sum(a => a.Commands.Count(x => x is RollDice)), line.DiceUndone);
        }
        Assert.False(h.CanRedo);
        Assert.DoesNotContain(s.Game!.Log, x => x is SteppedBack);
        Assert.Equal(played, Json(s.Game.State));
    }

    [Fact]
    public void StepBackTwice_OneLineCoversAllDice()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        PlayIntoTurnTwo(s, c);
        var played = Json(s.Game!.State);
        var log = s.Game.Log.ToList();
        var marks = s.Game.Actions.ToList();
        var end1 = Row(h, "End", 1);
        var supply1 = Row(h, "Supply", 1);
        Assert.True(After(log, marks, end1.LogIndex).Dice > 0);   // the first step back undoes the turn-2 Weather roll

        h.StepBack(end1);
        h.StepBack(supply1);

        var line = Assert.Single(s.Game.Log.OfType<SteppedBack>());
        var (actions, dice) = After(log, marks, supply1.LogIndex);
        Assert.Equal(dice, line.DiceUndone);
        Assert.Equal(actions, line.ActionsUndone);
        Assert.Equal(1, s.Game.State.Log.Count(l => l.Text.StartsWith("Stepped back to", StringComparison.Ordinal)));
        AssertRedoAllRestores(s, h, played);
    }

    [Fact]
    public void StepBackThenUndo_MergedLineCoversAllDice()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        PlayIntoTurnTwo(s, c);
        var played = Json(s.Game!.State);
        var log = s.Game.Log.ToList();
        var marks = s.Game.Actions.ToList();

        h.StepBack(Row(h, "End", 1));
        h.Undo();

        var line = Assert.Single(s.Game.Log.OfType<SteppedBack>());
        var (actions, dice) = After(log, marks, line.ToIndex);
        Assert.True(dice > 0);
        Assert.Equal(dice, line.DiceUndone);
        Assert.Equal(actions, line.ActionsUndone);
        Assert.Equal("ph-combat", s.Game.State.Position!.PhaseId);
        Assert.Contains(s.Game.State.Log, l => l.Text.StartsWith("Stepped back to Turn 1 · Combat", StringComparison.Ordinal));
        Assert.Equal(1, s.Game.State.Log.Count(l => l.Text.StartsWith("Stepped back to", StringComparison.Ordinal)));
        AssertRedoAllRestores(s, h, played);
    }

    [Fact]
    public void PlainUndo_AddsNoLine()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        PlayIntoTurnTwo(s, c);
        h.Undo();
        Assert.DoesNotContain(s.Game!.Log, x => x is SteppedBack);
    }

    [Fact]
    public void Redo_AfterUndoOfNewAction_KeepsOlderRelocation()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        c.Start();      // Weather
        c.Advance();    // Supply
        c.Advance();    // Movement
        s.UpdateRules(p => p with { Phases = p.Phases.Where(x => x.Id != "ph-supply").ToList() });
        h.Undo();       // relocation off the deleted Supply: the tail of this rewind
        c.Advance();    // action B: real play on top of the relocation (now history)
        var before = Json(s.Game!.State);
        var relocations = s.Game.Log.Count(x => x is AdvancePhase { Reason: not null });
        Assert.Equal(1, relocations);

        h.Undo();       // B
        Assert.Equal(relocations, s.Game.Log.Count(x => x is AdvancePhase { Reason: not null }));
        h.Redo();

        Assert.False(h.CanRedo);
        Assert.Equal(before, Json(s.Game.State));
    }

    [Fact]
    public void UndoThenStepBack_LineCountsPlainlyUndoneDice()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        c.Start();
        for (var i = 0; i < 30 && s.Game!.State.Position!.Turn < 2; i++) c.Advance();   // action A rolls the turn-2 Weather die
        var log = s.Game!.Log.ToList();
        var marks = s.Game.Actions.ToList();
        var supply1 = Row(h, "Supply", 1);
        var aDice = log.Skip(marks[^1].Start).Count(x => x is RollDice);
        Assert.True(aDice > 0);

        h.Undo();       // plain undo of A: no line
        Assert.DoesNotContain(s.Game.Log, x => x is SteppedBack);
        h.StepBack(supply1);

        var line = Assert.Single(s.Game.Log.OfType<SteppedBack>());
        var (actions, dice) = After(log, marks, supply1.LogIndex);
        Assert.Equal(dice, line.DiceUndone);
        Assert.Equal(actions, line.ActionsUndone);
    }

    [Fact]
    public void Undo_ThroughDeletedPhase_DoesNotLoop()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        c.Start();      // Weather
        c.Advance();    // Supply
        c.Advance();    // Movement
        var played = Json(s.Game!.State);
        s.UpdateRules(p => p with { Phases = p.Phases.Where(x => x.Id != "ph-supply").ToList() });

        h.Undo();       // back into Supply, which is gone: relocated (logged), redo kept
        Assert.Contains(s.Game.Log, x => x is AdvancePhase { Reason: not null });
        Assert.True(h.CanRedo);
        Assert.Equal("Undo Next Phase", h.UndoText);

        h.Undo();       // through the relocation and the advance into Supply, in one step
        Assert.Equal("ph-weather", s.Game.State.Position!.PhaseId);
        Assert.DoesNotContain(s.Game.Log, x => x is AdvancePhase { Reason: not null });

        h.Redo();
        Assert.True(h.CanRedo);
        h.Redo();
        Assert.False(h.CanRedo);
        Assert.Equal("ph-move", s.Game.State.Position!.PhaseId);
        Assert.Equal(played, Json(s.Game.State));
    }

    [Fact]
    public void ApplyFormula_AfterRunUndone_ReturnsFalse()
    {
        var (s, c, h) = Make(Difficulty.SemiAuto);
        c.Start();
        c.Advance();   // Supply: water run queued
        var run = s.Game!.State.Pending.Single(r => r.FormulaId == "f-water");
        var none = new Dictionary<string, string>();
        var trace = s.Runner!.Run(s.Game.State, new RunRequest("f-water", "r1", none, none, new Dictionary<string, IReadOnlyList<int>>(), null, true));

        h.Undo();
        var log = s.Game.Log.Count;

        Assert.False(c.ApplyFormula(run.Id, "r1", trace, false));
        Assert.Equal(log, s.Game.Log.Count);
        Assert.Null(Get(s, "r1", "water"));
    }

    [Fact]
    public void BatchApplyAll_IsOneAction()
    {
        var (s, c, h) = Make(Difficulty.Hardcore);
        ToMovementAndMove(c);
        var run = s.Game!.State.Pending.Single(r => r.FormulaId == "f-fuel");
        var b = new BatchModel(s, c, run.Id);
        b.CalculateAll();
        var before = Json(s.Game.State);
        var actions = s.Game.Actions.Count;

        Assert.Equal(2, b.ApplyAll());

        Assert.Equal(actions + 1, s.Game.Actions.Count);
        Assert.Equal("Undo Fuel for movement on 2 units", h.UndoText);
        h.Undo();
        Assert.Equal(before, Json(s.Game.State));
    }

    [Fact]
    public void MissingInputsApplyAll_IsOneAction()
    {
        var (s, c, h) = Make(Difficulty.Hardcore);
        ToMovementAndMove(c);
        var run = s.Game!.State.Pending.Single(r => r.FormulaId == "f-fuel");
        var actions = s.Game.Actions.Count;

        var (applied, _) = MissingInputs.ApplyAll(s, c, run, new Dictionary<string, string> { ["Terrain"] = "Sand" });

        Assert.Equal(2, applied);
        Assert.Equal(actions + 1, s.Game.Actions.Count);
        Assert.Equal("Undo Fuel for movement on 2 units", h.UndoText);
    }

    [Fact]
    public void StepBack_AfterPhaseDeleted_Reconciles()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        ToMovementAndMove(c);
        s.UpdateRules(p => p with { Phases = p.Phases.Where(x => x.Id != "ph-supply").ToList() });
        var row = h.Checkpoints().Single(r => r.Phase == "Supply");
        Assert.Null(row.Side);
        Assert.Null(row.Stage);

        var ex = Record.Exception(() => h.StepBack(row));

        Assert.Null(ex);
        Assert.Contains(s.Game!.Log, x => x is AdvancePhase { Reason: not null });
        Assert.NotEqual("ph-supply", s.Game.State.Position!.PhaseId);
        Assert.Contains(s.Game.State.Log, l => l.Text.StartsWith("Stepped back to Turn 1 · Supply", StringComparison.Ordinal));
    }

    [Fact]
    public void Undo_ReseedsKeysAddedSince()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        c.Start();
        // A new sheet-bound property: its seed joins the last action (Start) and goes down with it on Undo.
        s.UpdateRules(p => p with
        {
            Properties = p.Properties.Append(new PropertyDef { Id = "fuel2", Name = "Fuel two", Scope = PropertyScope.Pieces, SheetField = "Fuel" }).ToList(),
        });
        Assert.Equal(40, Get(s, "r1", "fuel2"));

        h.Undo();

        Assert.Null(s.Game!.State.Position);
        Assert.Equal(40, Get(s, "r1", "fuel2"));
    }

    [Fact]
    public void Describe_Text()
    {
        var full = new RewindPreview(3, 2, new List<(string, int)> { ("Fuel", 3), ("Ammo", 1) }, 4, 1, 0);
        Assert.Equal(string.Join(Environment.NewLine,
            "3 actions",
            "2 dice rolls. Rolling again will give new results.",
            "Ledger: Fuel on 3 units, Ammo on 1 unit",
            "Pieces: 4 moved, 1 added"), HistoryController.Describe(full));

        var one = new RewindPreview(1, 1, new List<(string, int)>(), 0, 0, 2);
        Assert.Equal(string.Join(Environment.NewLine,
            "1 action",
            "1 dice roll. Rolling again will give new results.",
            "Pieces: 2 removed"), HistoryController.Describe(one));

        Assert.Equal("Nothing to undo.", HistoryController.Describe(new RewindPreview(0, 0, new List<(string, int)>(), 0, 0, 0)));
    }

    [Fact]
    public void NewGame_SeedIsInGenesis_CannotUndo()
    {
        var (s, _, h) = Make(Difficulty.FullyAuto);
        s.LoadSetup(s.Model.Setups[0]);   // a fresh game with the rules pack already open

        Assert.Empty(s.Game!.Log);
        Assert.False(h.CanUndo);
        Assert.Equal("Undo", h.UndoText);
        Assert.Equal(40, Ledger.Get(s.Game.Genesis, LedgerKey.Piece("r1"), "fuel"));
    }

    [Fact]
    public void BadIndexOrOpenScope_DoesNothing_LogsDiagnostic()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        c.Start();
        var log = s.Game!.Log.Count;
        var diags = s.Log.Items.Count;

        Assert.Null(Record.Exception(() => h.StepBack(new CheckpointRow(999, 1, null, null, "Nowhere", null, 0))));
        using (s.Game.BeginAction("open"))
        {
            Assert.Null(Record.Exception(() => h.Undo()));
            Assert.Null(Record.Exception(() => h.StepBack(h.Checkpoints()[^1])));
        }

        Assert.Equal(log, s.Game.Log.Count);
        Assert.True(s.Log.Items.Count >= diags + 3);
    }

    [Fact]
    public void LoadNative_LoadNotice_IsLogged()
    {
        var (s, c, _) = Make(Difficulty.FullyAuto);
        c.Start();
        var path = Path.Combine(_tmp, "g.qsave");
        s.SaveNative(path);
        // Tamper with the snapshot so the replay check fails and the load rebases with a notice.
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        doc["snapshot"]!["notes"] = "tampered";
        File.WriteAllText(path, doc.ToJsonString());

        s.LoadNative(path);

        Assert.NotNull(s.Game!.LoadNotice);
        Assert.Contains(s.Log.Items, d => d.Category == "save" && d.Message == s.Game.LoadNotice);
    }

    // ---- final review fixes ----

    private static void AddSeededProperty(ModuleSession s) => s.UpdateRules(p => p with
    {
        Properties = p.Properties.Append(new PropertyDef { Id = "fuel2", Name = "Fuel two", Scope = PropertyScope.Pieces, SheetField = "Fuel" }).ToList(),
    });

    [Fact]
    public void RulesChangeAfterStepBack_ThenUndo_KeepsLineWithItsDice()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        PlayIntoTurnTwo(s, c);
        // Supply is deleted, so the step back to it relocates: the relocation is an action after the "Stepped back" line.
        s.UpdateRules(p => p with { Phases = p.Phases.Where(x => x.Id != "ph-supply").ToList() });
        var log = s.Game!.Log.ToList();
        var marks = s.Game.Actions.ToList();
        var row = Row(h, "Supply", 1);
        var (_, dice) = After(log, marks, row.LogIndex);
        Assert.True(dice > 0);
        h.StepBack(row);
        var line = Assert.Single(s.Game.Log.OfType<SteppedBack>());
        Assert.Equal(dice, line.DiceUndone);

        AddSeededProperty(s);   // clears redo: the line is now the only record of the discarded dice
        h.Undo();

        Assert.Equal(line, Assert.Single(s.Game.Log.OfType<SteppedBack>()));
    }

    [Fact]
    public void RulesChangeAfterStepBack_ThenUndoOfEarlierAction_LineGoesToRedoIntact()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        PlayIntoTurnTwo(s, c);
        h.StepBack(Row(h, "End", 1));
        var line = Assert.Single(s.Game!.Log.OfType<SteppedBack>());
        Assert.True(line.DiceUndone > 0);

        AddSeededProperty(s);
        var before = Json(s.Game.State);
        var undoneDice = After(s.Game.Log.ToList(), s.Game.Actions.ToList(), s.Game.Actions[^1].Start).Dice;
        h.Undo();   // the line folds into the action before it and goes down with it, unchanged...

        Assert.Equal(line, Assert.Single(s.Game.RedoActions.SelectMany(a => a.Commands).OfType<SteppedBack>()));
        // ...and a new line keeps the trace of its dice (plus the undone action's) in the log
        var relog = Assert.Single(s.Game.Log.OfType<SteppedBack>());
        Assert.Equal(line.DiceUndone + undoneDice, relog.DiceUndone);
        Assert.Equal(line.ActionsUndone + 1, relog.ActionsUndone);

        h.Redo();   // the re-logged line goes; the action comes back with its original line
        Assert.Equal(line, Assert.Single(s.Game.Log.OfType<SteppedBack>()));
        Assert.Equal(before, Json(s.Game.State));
    }

    [Fact]
    public void RulesChangeAfterStepBack_ThenUndo_ThenNewAction_KeepsDiceTrace()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        PlayIntoTurnTwo(s, c);
        h.StepBack(Row(h, "End", 1));
        var line = Assert.Single(s.Game!.Log.OfType<SteppedBack>());
        Assert.True(line.DiceUndone > 0);

        AddSeededProperty(s);
        h.Undo();
        c.Advance();   // a new action clears redo: the discarded dice must still be mentioned in the log

        Assert.Contains(s.Game.Log.OfType<SteppedBack>(), x => x.DiceUndone >= line.DiceUndone);
    }

    [Fact]
    public void RulesChangeAfterStepBack_ThenStepBackFurther_CountsEarlierDice()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        PlayIntoTurnTwo(s, c);
        h.StepBack(Row(h, "End", 1));
        var line = Assert.Single(s.Game!.Log.OfType<SteppedBack>());
        Assert.True(line.DiceUndone > 0);

        AddSeededProperty(s);
        var row = Row(h, "Supply", 1);
        var (actions, dice) = After(s.Game.Log.ToList(), s.Game.Actions.ToList(), row.LogIndex);
        h.StepBack(row);

        var relog = Assert.Single(s.Game.Log.OfType<SteppedBack>());
        Assert.Equal(line.DiceUndone + dice, relog.DiceUndone);
        Assert.Equal(line.ActionsUndone + actions, relog.ActionsUndone);
    }

    [Fact]
    public void PartialRedoAfterStepBack_RelogsLineForWhatRemains()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        c.Start();
        for (var i = 0; i < 30 && s.Game!.State.Position!.Turn < 2; i++) c.Advance();   // rolls the turn-2 Weather die
        for (var i = 0; i < 30 && s.Game!.State.Position!.Turn < 3; i++) c.Advance();   // rolls the turn-3 Weather die
        var played = Json(s.Game!.State);
        var end1 = Row(h, "End", 1);
        var log = s.Game.Log.ToList();
        var marks = s.Game.Actions.ToList();
        Assert.Equal(2, After(log, marks, end1.LogIndex).Dice);

        h.StepBack(end1);
        Assert.Equal(2, Assert.Single(s.Game.Log.OfType<SteppedBack>()).DiceUndone);

        h.Redo();   // the first undone action: the turn-2 roll
        var line = Assert.Single(s.Game.Log.OfType<SteppedBack>());
        Assert.Equal(1, line.DiceUndone);
        Assert.Equal(s.Game.RedoActions.Count, line.ActionsUndone);
        Assert.Equal(1, s.Game.State.Log.Count(l => l.Text.StartsWith("Stepped back to", StringComparison.Ordinal)));

        while (s.Game.RedoActions.Count > 1)
        {
            h.Redo();
            line = Assert.Single(s.Game.Log.OfType<SteppedBack>());
            Assert.Equal(s.Game.RedoActions.Count, line.ActionsUndone);
            Assert.Equal(s.Game.RedoActions.Sum(a => a.Commands.Count(x => x is RollDice)), line.DiceUndone);
        }
        h.Redo();   // the last one: no trace of the step back remains

        Assert.False(h.CanRedo);
        Assert.DoesNotContain(s.Game.Log, x => x is SteppedBack);
        Assert.DoesNotContain(s.Game.State.Log, l => l.Text.StartsWith("Stepped back to", StringComparison.Ordinal));
        Assert.Equal(played, Json(s.Game.State));
    }

    [Fact]
    public void PartialRedoAfterStepBack_ThenNewAction_KeepsDiceTrace()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        PlayIntoTurnTwo(s, c);
        h.StepBack(Row(h, "Supply", 1));
        var dice = Assert.Single(s.Game!.Log.OfType<SteppedBack>()).DiceUndone;
        Assert.True(dice > 0);

        h.Redo();
        c.ExecuteMove(new[] { Move("b1", 120) });   // a new action clears what is left to redo

        Assert.False(h.CanRedo);
        Assert.Equal(dice, Assert.Single(s.Game.Log.OfType<SteppedBack>()).DiceUndone);
    }

    [Fact]
    public void OldSave_StepBackToLoadPoint_SaysLoadPoint()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        c.Start();
        var path = Path.Combine(_tmp, "old.qsave");
        s.SaveNative(path);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        doc["snapshot"]!["notes"] = "tampered";   // the history check fails: rebased at the load point
        File.WriteAllText(path, doc.ToJsonString());
        s.LoadNative(path);
        Assert.True(s.Game!.RebasedOnLoad);

        c.Advance();
        var row = h.Checkpoints().Last();
        Assert.Equal(0, row.LogIndex);
        Assert.Equal("load point", row.Phase);
        h.StepBack(row);

        Assert.Contains(s.Game.State.Log, l => l.Text.StartsWith("Stepped back to load point.", StringComparison.Ordinal));
        Assert.DoesNotContain(s.Game.State.Log, l => l.Text.Contains("game start", StringComparison.Ordinal));
    }
}
