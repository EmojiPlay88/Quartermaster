using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Next Phase, pending-run and Step Back dispatch shared by both apps. Invented pack from PhaseFixture; temp roots only.</summary>
public class PhaseRunDispatchTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-dispatch-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public PhaseRunDispatchTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset Stamp = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public required ModuleSession S;
        public required PhaseController C;
        public required HistoryController H;
        public required RecordingPort Port;
        public required FakeUi Ui;
        public required PhaseRunDispatch D;
        public required List<string> Order;
        public string? Position => S.Game!.State.Position?.PhaseId;
    }

    private Rig Make(Difficulty d, Func<RulesPack, RulesPack>? tweak = null, bool autosaver = true)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d);
        s.NewRules("t");
        s.UpdateRules(_ => { var p = PhaseFixture.Pack(s.Archive.Sha256); return tweak == null ? p : tweak(p); });
        Func<GameCommand, GameState, GameCommand> prepare = (cmd, _) => cmd;
        var c = new PhaseController(s, prepare);
        var h = new HistoryController(s, c, prepare);
        var order = new List<string>();
        var port = new RecordingPort(new LocalActions(s, c, h, prepare), order);
        var ui = new FakeUi(order);
        var saver = autosaver
            ? new Autosaver(() => s.Archive.Sha256, p => { order.Add("save"); File.WriteAllText(p, "x"); }, Path.Combine(_tmp, "saves"))
            : null;
        var dispatch = new PhaseRunDispatch(s, c, h, () => port, ui, saver, () => Stamp);
        return new Rig { S = s, C = c, H = h, Port = port, Ui = ui, D = dispatch, Order = order };
    }

    private static RulesPack NoAutoTerrain(RulesPack p) => p with
    {
        Formulas = p.Formulas.Select(f => f.Id != "f-fuel" ? f : f with
        {
            Bindings = f.Bindings.Select(b => b.Variable == "Terrain" ? b with { AutoValue = null } : b).ToList(),
        }).ToList(),
    };

    private static MovePieces Move(string id, int x) => new(new[] { id }, "Main Map", new PointD(x, 50), null);

    private static void ToMovement(PhaseController c) { c.Start(); c.Advance(); c.Advance(); }

    // ---- Next Phase ----

    [Fact]
    public async Task Next_turn_without_phases_uses_fallback()
    {
        var r = Make(Difficulty.FullyAuto, p => p with { Phases = new(), Blocks = new() });
        var fell = 0;
        Assert.True(await r.D.NextTurnCommand(() => { fell++; return true; }));
        Assert.Equal(1, fell);
        Assert.DoesNotContain("next", r.Order);

        var phased = Make(Difficulty.FullyAuto);
        Assert.True(await phased.D.NextTurnCommand(() => { fell++; return true; }));
        Assert.Equal(1, fell);
        Assert.Equal("ph-weather", phased.Position);   // Start game
        Assert.Contains("next", phased.Order);
    }

    [Fact]
    public async Task Next_phase_with_open_decisions_asks_and_go_back_keeps_phase()
    {
        var r = Make(Difficulty.SemiAuto);
        var advanced = 0;
        r.D.Advanced += () => advanced++;
        await r.D.NextPhase();             // Start game: no question
        Assert.Equal("ph-weather", r.Position);
        Assert.Equal(1, advanced);
        Assert.NotEmpty(r.C.View().Decisions);   // the weather roll waits

        r.Ui.Continue = false;
        await r.D.NextPhase();
        Assert.Equal(1, r.Ui.ConfirmCalls);
        Assert.Equal("ph-weather", r.Position);
        Assert.Equal(1, advanced);

        r.Ui.Continue = true;
        await r.D.NextPhase();
        Assert.Equal(2, r.Ui.ConfirmCalls);
        Assert.Equal("ph-supply", r.Position);
        Assert.Equal("Pass to Red player", r.Ui.Statuses[^1]);
        Assert.Equal(2, advanced);
    }

    [Fact]
    public async Task Next_phase_reentry_returns_immediately()
    {
        var r = Make(Difficulty.SemiAuto);
        r.C.Start();
        var gate = new TaskCompletionSource<bool>();
        r.Ui.ConfirmTask = gate.Task;
        var first = r.D.NextPhase();
        Assert.False(first.IsCompleted);
        var second = r.D.NextPhase();
        Assert.True(second.IsCompleted);
        Assert.Equal(1, r.Ui.ConfirmCalls);
        gate.SetResult(true);
        await first;
        Assert.Equal("ph-supply", r.Position);
        Assert.Equal(1, r.Order.Count(x => x == "next"));
    }

    [Fact]
    public async Task Next_phase_replaying_or_other_side_says_why()
    {
        var r = Make(Difficulty.SemiAuto);
        r.C.Start();
        r.S.SetViewSides(new HashSet<string> { "blue" });
        r.C.Advance();   // Supply: Red's phase
        await r.D.NextPhase();
        Assert.Equal("Red is playing Supply", r.Ui.Statuses[^1]);
        Assert.Equal("ph-supply", r.Position);
        Assert.Equal(0, r.Ui.ConfirmCalls);
    }

    [Fact]
    public void Jump_to_decision_by_kind()
    {
        var r = Make(Difficulty.SemiAuto);
        r.D.JumpToDecision(new Decision(DecisionKind.Checklist, "Resolve attacks", 2, null, Array.Empty<string>()));
        Assert.Equal(new[] { "reveal", "checklist 2" }, r.Ui.Calls);
        r.Ui.Calls.Clear();
        r.D.JumpToDecision(new Decision(DecisionKind.Warning, "Out of fuel", 0, null, new[] { "r1" }));
        Assert.Equal(new[] { "centre r1" }, r.Ui.Calls);
    }

    // ---- pending runs ----

    [Fact]
    public async Task Fully_auto_applies_without_dialog()
    {
        var r = Make(Difficulty.SemiAuto);
        r.C.Start();   // the weather roll waits
        var run = Assert.Single(r.S.Game!.State.Pending);
        r.C.SetDifficulty(Difficulty.FullyAuto);
        await r.D.OpenPendingRun(run.Id);
        Assert.Empty(r.S.Game.State.Pending);
        Assert.Equal("Weather roll: 1 unit(s) calculated automatically", r.Ui.Statuses[^1]);
        Assert.Empty(r.Ui.Calls);
        Assert.Contains("apply-missing", r.Order);

        await r.D.OpenPendingRun("gone");
        Assert.Equal("That calculation is no longer waiting.", r.Ui.Statuses[^1]);
    }

    [Fact]
    public async Task Fully_auto_missing_input_asks_then_per_unit_opens_units()
    {
        var r = Make(Difficulty.FullyAuto, NoAutoTerrain);
        ToMovement(r.C);
        r.C.ExecuteMove(new[] { new MovePieces(new[] { "r1", "r2" }, "Main Map", new PointD(130, 50), null) });
        var run = Assert.Single(r.S.Game!.State.Pending, x => x.FormulaId == "f-fuel");

        r.Ui.Missing = MissingInputResult.Cancel;
        await r.D.OpenPendingRun(run.Id);
        Assert.Equal(new[] { "ask " + run.Id }, r.Ui.Calls);

        r.Ui.Calls.Clear();
        r.Ui.Missing = MissingInputResult.PerUnit;
        await r.D.OpenPendingRun(run.Id);
        Assert.Equal(new[] { "ask " + run.Id, "batch " + run.Id }, r.Ui.Calls);

        r.Ui.Calls.Clear();
        r.Ui.Missing = MissingInputResult.Ok;
        await r.D.OpenPendingRun(run.Id);
        Assert.Equal(new[] { "ask " + run.Id }, r.Ui.Calls);
    }

    [Fact]
    public async Task Semi_auto_one_unit_opens_unit_dialog_many_opens_batch()
    {
        var r = Make(Difficulty.SemiAuto);
        r.C.Start();
        var weather = Assert.Single(r.S.Game!.State.Pending);
        await r.D.OpenPendingRun(weather.Id);
        Assert.Equal(new[] { $"unit f-weather - {weather.Id} -" }, r.Ui.Calls);

        r.C.Advance(); r.C.Advance();   // Movement (the weather run is dropped)
        r.C.ExecuteMove(new[] { new MovePieces(new[] { "r1", "r2" }, "Main Map", new PointD(130, 50), null) });
        var fuel = Assert.Single(r.S.Game.State.Pending, x => x.FormulaId == "f-fuel");
        r.Ui.Calls.Clear();
        await r.D.OpenPendingRun(fuel.Id);
        Assert.Equal(new[] { "batch " + fuel.Id }, r.Ui.Calls);

        r.Ui.Calls.Clear();
        r.D.OpenBatchRow(fuel.Id, 1);
        Assert.Equal(new[] { $"unit f-fuel r2 {fuel.Id} {fuel.Units[1].Moved}" }, r.Ui.Calls);
    }

    [Fact]
    public void Runs_queued_says_how_many_and_opens_each_unless_held()
    {
        var r = Make(Difficulty.SemiAuto);
        r.C.Start();
        var runs = r.S.Game!.State.Pending.ToList();
        r.D.HoldRuns = _ => true;
        r.D.OnRunsQueued(runs);
        Assert.Equal("1 calculation(s) waiting in the phase panel", r.Ui.Statuses[^1]);
        Assert.Empty(r.Ui.Calls);
        r.D.HoldRuns = null;
        r.D.OnRunsQueued(runs);
        Assert.Single(r.Ui.Calls, x => x.StartsWith("unit f-weather", StringComparison.Ordinal));
    }

    [Fact]
    public void Difficulty_change_closes_formula_windows()
    {
        var r = Make(Difficulty.SemiAuto);
        r.D.OnDifficultyChanged();
        Assert.Equal(new[] { "close" }, r.Ui.Calls);
        Assert.Equal("Difficulty changed — reopen from the phase panel", r.Ui.Statuses[^1]);
    }

    // ---- step back and autosave ----

    [Fact]
    public async Task Step_back_to_start_saves_forced_then_rewinds_and_closes_windows()
    {
        var r = Make(Difficulty.FullyAuto);
        ToMovement(r.C);
        r.C.ExecuteMove(new[] { Move("r1", 130) });
        var saved = 0;
        r.D.Autosaved += () => saved++;
        r.Order.Clear();
        await r.D.StepBackToStart();
        Assert.Equal(new[] { "pick 1 confirm", "save", "stepback Movement", "close" }, r.Order);
        Assert.Equal(1, saved);
        Assert.Equal("Stepped back — reopen from the phase panel", r.Ui.Statuses[^1]);
        Assert.Equal(PhaseRunDispatch.SteppedBackText, r.Ui.Statuses[^1]);
        Assert.Equal("ph-move", r.Position);
        Assert.True(r.H.CanRedo);
    }

    [Fact]
    public async Task Step_back_to_lists_checkpoints_and_rewinds_to_the_pick()
    {
        var r = Make(Difficulty.FullyAuto);
        ToMovement(r.C);
        r.Ui.Pick = rows => rows.Single(x => x.Phase == "Supply");
        var n = r.H.Checkpoints().Count;
        Assert.True(n > 1);
        r.Order.Clear();
        await r.D.StepBackTo();
        Assert.Equal($"pick {n} list", r.Order[0]);
        Assert.Equal("ph-supply", r.Position);
        Assert.Contains("stepback Supply", r.Order);
    }

    [Fact]
    public async Task Step_back_to_cancelled_does_nothing()
    {
        var r = Make(Difficulty.FullyAuto);
        ToMovement(r.C);
        r.Ui.Pick = _ => null;
        r.Order.Clear();
        await r.D.StepBackTo();
        await r.D.StepBackToStart();
        Assert.Equal(new[] { "pick", "pick" }, r.Order.Select(x => x.Split(' ')[0]));
        Assert.Empty(r.Ui.Statuses);
        Assert.Equal("ph-move", r.Position);
        Assert.False(r.H.CanRedo);
    }

    [Fact]
    public async Task Step_back_declined_by_history_closes_nothing()
    {
        var r = Make(Difficulty.FullyAuto);
        ToMovement(r.C);
        r.Ui.Pick = rows => rows[0] with { LogIndex = 9999 };   // no longer in this game's history
        r.Order.Clear();
        await r.D.StepBackTo();
        Assert.DoesNotContain("close", r.Order);
        Assert.Empty(r.Ui.Statuses);
    }

    [Fact]
    public void Phase_boundary_autosaves()
    {
        var r = Make(Difficulty.FullyAuto);
        var saved = 0;
        r.D.Autosaved += () => saved++;
        r.D.OnPhaseBoundary();
        Assert.Equal(new[] { "save" }, r.Order);
        Assert.Equal(1, saved);

        r.D.AutosaveMinutes = () => 0;   // autosave off: the boundary writes nothing
        r.D.OnPhaseBoundary();
        Assert.Single(r.Order);
        r.D.AutosaveMinutes = () => 5;
        r.D.OnPhaseBoundary();
        Assert.Equal(2, r.Order.Count);

        Make(Difficulty.FullyAuto, autosaver: false).D.OnPhaseBoundary();   // no autosaver: nothing to do
    }

    // ---- fakes ----

    private sealed class FakeUi(List<string> order) : IPhaseUi
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Statuses = new();
        public bool Continue = true;
        public Task<bool>? ConfirmTask;
        public int ConfirmCalls;
        public MissingInputResult Missing = MissingInputResult.Cancel;
        public Func<IReadOnlyList<CheckpointRow>, CheckpointRow?> Pick = rows => rows[0];

        private void Call(string c) { Calls.Add(c); order.Add(c); }

        public Task<bool> ConfirmOpenDecisions(IReadOnlyList<Decision> open) { ConfirmCalls++; return ConfirmTask ?? Task.FromResult(Continue); }

        public Task<CheckpointRow?> PickCheckpoint(IReadOnlyList<CheckpointRow> rows, bool confirmOnly)
        {
            order.Add($"pick {rows.Count} {(confirmOnly ? "confirm" : "list")}");
            return Task.FromResult(Pick(rows));
        }

        public void OpenUnitDialog(string formulaId, string? pieceId, string runId, double? moved) => Call($"unit {formulaId} {pieceId ?? "-"} {runId} {moved?.ToString() ?? "-"}");
        public void OpenBatch(string runId) => Call("batch " + runId);
        public Task<MissingInputResult> AskMissing(PendingRun run) { Call("ask " + run.Id); return Task.FromResult(Missing); }
        public void CloseFormulaWindows() => Call("close");
        public void Centre(string pieceId) => Call("centre " + pieceId);
        public void Status(string text) => Statuses.Add(text);
        public void RevealPhasePanel() => Call("reveal");
        public void SelectChecklist(int index) => Call("checklist " + index);
    }

    private sealed class RecordingPort(IActionPort inner, List<string> order) : IActionPort
    {
        public bool IsRemote => inner.IsRemote;
        public void Move(IReadOnlyList<MovePieces> moves) => inner.Move(moves);
        public void PieceCommand(GameCommand command) => inner.PieceCommand(command);
        public void PieceCommands(IReadOnlyList<GameCommand> commands) => inner.PieceCommands(commands);
        public FormulaVerdict ApplyFormula(FormulaIntent intent) => inner.ApplyFormula(intent);
        public Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent) => inner.ApplyFormulaAsync(intent);
        public bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated) => inner.ApplyTrace(runId, pieceId, trace, calculated);
        public (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values)
        {
            order.Add("apply-missing");
            return inner.ApplyMissingInputs(run, values);
        }
        public void EditLedger(string key, string propertyId, double after, string note) => inner.EditLedger(key, propertyId, after, note);
        public void Roll(DiceSpec spec, string purpose) => inner.Roll(spec, purpose);
        public void RecordRoll(RollDice roll) => inner.RecordRoll(roll);
        public void Tick(int index, bool ticked) => inner.Tick(index, ticked);
        public void Ack(int warningIndex) => inner.Ack(warningIndex);
        public string? NextPhase() { order.Add("next"); return inner.NextPhase(); }
        public void HideAlert(string alertId, string? name) => inner.HideAlert(alertId, name);
        public void Chat(string text) => inner.Chat(text);
        public void SetTurn(int turn, string? phase) => inner.SetTurn(turn, phase);
        public void SetNotes(string text) => inner.SetNotes(text);
        public void Undo() => inner.Undo();
        public void Redo() => inner.Redo();
        public void StepBack(CheckpointRow row) { order.Add("stepback " + row.Phase); inner.StepBack(row); }
    }
}
