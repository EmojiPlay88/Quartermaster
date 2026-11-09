using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>How the missing-input dialog closed: answers applied, "Per unit..." (open the checked UI), or cancelled.</summary>
public enum MissingInputResult { Ok, PerUnit, Cancel }

/// <summary>The windows and status line <see cref="PhaseRunDispatch"/> drives. Each app implements it with its own dialogs.</summary>
public interface IPhaseUi
{
    /// <summary>The Next Phase warning list. True = Continue anyway; false = Go back (the dialog may have jumped to a decision itself).</summary>
    Task<bool> ConfirmOpenDecisions(IReadOnlyList<Decision> open);

    /// <summary>The Step Back dialog over checkpoints, newest first (never empty). Null = Cancel.</summary>
    Task<CheckpointRow?> PickCheckpoint(IReadOnlyList<CheckpointRow> rows, bool confirmOnly);

    /// <summary>The checked formula dialog for one unit of a pending run. <paramref name="pieceId"/> is null for a once-per-phase run;
    /// <paramref name="moved"/> is the measured distance of an OnMove run.</summary>
    void OpenUnitDialog(string formulaId, string? pieceId, string runId, double? moved);

    /// <summary>The checked batch grid for a run of several units (rows open through <see cref="PhaseRunDispatch.OpenBatchRow"/>).</summary>
    void OpenBatch(string runId);

    /// <summary>The missing-input dialog of a fully automatic run (it applies the answers itself through the action port).</summary>
    Task<MissingInputResult> AskMissing(PendingRun run);

    /// <summary>Closes every formula dialog, batch grid and cheat sheet of the session (they belong to the old state).</summary>
    void CloseFormulaWindows();

    void Centre(string pieceId);
    void Status(string text);
    void RevealPhasePanel();

    /// <summary>Highlights a checklist item of the phase panel (after <see cref="RevealPhasePanel"/>).</summary>
    void SelectChecklist(int index) { }
}

/// <summary>
/// The Next Phase flow, pending-run dispatch per difficulty, and the Step Back flow, shared by both apps. Game changes go through
/// <c>port()</c>; windows and the status line through <see cref="IPhaseUi"/>. The caller wires the controller's RunsQueued,
/// PhaseBoundary and DifficultyChanged events to <see cref="OnRunsQueued"/>, <see cref="OnPhaseBoundary"/> and
/// <see cref="OnDifficultyChanged"/> on its UI thread.
/// </summary>
public sealed class PhaseRunDispatch(ModuleSession s, PhaseController c, HistoryController h, Func<IActionPort> port, IPhaseUi ui,
    Autosaver? autosaver, Func<DateTimeOffset> now)
{
    public const string SteppedBackText = "Stepped back — reopen from the phase panel";
    public const string DifficultyChangedText = "Difficulty changed — reopen from the phase panel";
    public const string NoLongerWaitingText = "That calculation is no longer waiting.";

    private bool _nextRunning;

    /// <summary>Raised after Next Phase started the game or advanced (the caller refreshes its phase views at once).</summary>
    public event Action? Advanced;

    /// <summary>Raised after an autosave was written (phase boundary or before a step back).</summary>
    public event Action? Autosaved;

    /// <summary>The autosave interval setting. When set, the phase-boundary autosave honours it (0 = off); when null it always writes.</summary>
    public Func<int>? AutosaveMinutes { get; set; }

    /// <summary>Hot-seat: return true to hold queued runs (the caller opens them after the handover); the status line is set first.</summary>
    public Func<IReadOnlyList<PendingRun>, bool>? HoldRuns { get; set; }

    /// <summary>Receives an exception from opening one queued run; the other runs still open. Unset: the exception is rethrown.</summary>
    public Action<Exception>? Failed { get; set; }

    /// <summary>Diagnostic lines (an autosave that threw).</summary>
    public Action<string>? Log { get; set; }

    private bool HasPhases => s.Game != null && s.Rules is { Phases.Count: > 0 };

    // ---------- Next Phase ----------

    /// <summary>Ctrl+Right: Next Phase when the pack has phases, otherwise <paramref name="stepTurnFallback"/> (the plain turn counter).</summary>
    public async Task<bool> NextTurnCommand(Func<bool> stepTurnFallback)
    {
        if (s.Game == null) return false;
        if (!HasPhases) return stepTurnFallback();
        await NextPhase();
        return true;
    }

    /// <summary>Start game, or advance one phase. Open decisions only warn: the dialog lists them and the user may continue anyway.
    /// A call while one is still running returns at once.</summary>
    public async Task NextPhase()
    {
        if (_nextRunning) return;
        _nextRunning = true;
        try
        {
            if (s.Game == null) return;
            if (s.IsReplaying) { ui.Status(ModuleSession.ReplayingText); return; }   // the sender's turn replays as sent
            var v = c.View();
            if (v.NoPhases) return;
            if (v.PlayingText is { } playing) { ui.Status(playing); return; }   // hot-seat: another side's phase
            if (v.BeforeStart)
            {
                port().NextPhase();
                Advanced?.Invoke();
                return;
            }
            if (v.Decisions.Count > 0 && !await ui.ConfirmOpenDecisions(v.Decisions)) return;
            if (port().NextPhase() is { } pass) ui.Status(pass);
            Advanced?.Invoke();
        }
        finally { _nextRunning = false; }
    }

    /// <summary>Takes the user to an open decision: a pending run opens, a checklist item is shown in the phase panel, else the unit is centred.</summary>
    public void JumpToDecision(Decision d)
    {
        if (d.Kind == DecisionKind.Pending && d.RunId != null) Observe(OpenPendingRun(d.RunId));
        else if (d.Kind == DecisionKind.Checklist)
        {
            ui.RevealPhasePanel();
            ui.SelectChecklist(d.Index);
        }
        else if (d.PieceIds.FirstOrDefault() is { } id) ui.Centre(id);
    }

    // ---------- pending runs ----------

    /// <summary>Triggers queued runs: each opens its calculation UI at once (call on the UI thread, outside Advance / ExecuteMove).</summary>
    public void OnRunsQueued(IReadOnlyList<PendingRun> runs)
    {
        ui.Status($"{runs.Count} calculation(s) waiting in the phase panel");
        // Hot-seat: the phase belongs to the next player; their calculations open once they are at the screen.
        if (HoldRuns?.Invoke(runs) == true) return;
        foreach (var r in runs) Observe(OpenPendingRun(r.Id));
    }

    /// <summary>
    /// Opens the calculation UI of a pending run in the CURRENT difficulty, whatever mode it was queued in (spec 7.3).
    /// Fully automatic: the run is finished from the auto values, asking once (missing-input dialog) only for what has no auto value.
    /// Hardcore / semi-auto: the checked dialog for one unit or the checked batch grid for several (semi-auto adds the cheat sheet).
    /// </summary>
    public async Task OpenPendingRun(string runId)
    {
        if (s.Game is not { } game) return;
        var run = game.State.Pending.FirstOrDefault(r => r.Id == runId);
        if (run == null) { ui.Status(NoLongerWaitingText); return; }
        var open = run.Units.Where(u => !u.Applied).ToList();
        if (open.Count == 0) return;
        // Hot-seat: a run with no unit of a held side (or of no side) is not this player's to calculate.
        if (!open.Any(u => s.CanAct(u.PieceId))) { ui.Status("That calculation " + ModuleSession.OtherSideText + "."); return; }
        if (game.State.Difficulty == Difficulty.FullyAuto)
        {
            await OpenPendingRunAutomatic(run);
            return;
        }
        OpenPendingRunAsUnits(run, open);
    }

    private async Task OpenPendingRunAutomatic(PendingRun run)
    {
        if (MissingInputs.For(s, run).Count == 0)
        {
            // Nothing to ask (for example a run left over from a manual mode): finish it from the auto values, no window.
            var (applied, failed) = port().ApplyMissingInputs(run, new Dictionary<string, string>());
            ui.Status($"{run.FormulaName}: {applied} unit(s) calculated automatically" + (failed.Count > 0 ? $", {failed.Count} could not be" : ""));
            return;
        }
        if (await ui.AskMissing(run) == MissingInputResult.PerUnit
            && s.Game?.State.Pending.FirstOrDefault(r => r.Id == run.Id) is { } fresh   // re-read: the dialog may have applied some units
            && fresh.Units.Where(u => !u.Applied).ToList() is { Count: > 0 } left)
            OpenPendingRunAsUnits(fresh, left);   // "Per unit..."
    }

    /// <summary>The checked UI for a run: the batch grid for several units, the unit dialog for one. Also "Per unit..." from the missing-input dialog.</summary>
    private void OpenPendingRunAsUnits(PendingRun run, List<PendingUnit> open)
    {
        if (open.Count > 1) ui.OpenBatch(run.Id);
        else OpenUnitDialog(run, open[0]);
    }

    /// <summary>Double-click in the batch grid: that unit's formula dialog, linked to the run so applying it counts for the batch.</summary>
    public void OpenBatchRow(string runId, int row)
    {
        if (s.Game is not { } game) return;
        var run = game.State.Pending.FirstOrDefault(r => r.Id == runId);
        if (run == null || row < 0 || row >= run.Units.Count || run.Units[row].Applied) return;
        OpenUnitDialog(run, run.Units[row]);
    }

    private void OpenUnitDialog(PendingRun run, PendingUnit unit)
    {
        if (s.Game is not { } game) return;
        if (unit.PieceId != null && !game.State.Pieces.ContainsKey(unit.PieceId)) { ui.Status($"{unit.Label} is no longer on the map."); return; }
        ui.OpenUnitDialog(run.FormulaId, unit.PieceId, run.Id, unit.Moved);
    }

    /// <summary>The difficulty changed: every open formula dialog, batch grid and cheat sheet belongs to the old mode, so they close. Pending runs stay pending.</summary>
    public void OnDifficultyChanged()
    {
        ui.CloseFormulaWindows();
        ui.Status(DifficultyChangedText);
    }

    // ---------- autosave and step back ----------

    /// <summary>The phase just changed: write an autosave now (with <see cref="AutosaveMinutes"/> set, 0 is off).</summary>
    public void OnPhaseBoundary()
    {
        if (autosaver == null) return;
        try
        {
            var path = AutosaveMinutes is { } minutes ? autosaver.SaveNow(now(), minutes()) : autosaver.SaveForced(now());
            if (path != null) Autosaved?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke("phase autosave: " + ex.Message);
        }
    }

    /// <summary>True (and says so) while a received turn replays or for an online guest: only the host can step back.</summary>
    private bool CannotStepBack()
    {
        if (s.IsReplaying) { ui.Status(ModuleSession.ReplayingText); return true; }
        if (!s.IsGuest) return false;
        ui.Status(ModuleSession.GuestUndoText);
        return true;
    }

    /// <summary>Edit > Step Back to Start of Phase: confirm, then step back.</summary>
    public async Task StepBackToStart()
    {
        if (CannotStepBack() || h.StartOfPhase() is not { } row) return;
        if (await ui.PickCheckpoint(new[] { row }, confirmOnly: true) is not { } picked) return;
        StepBack(picked);
    }

    /// <summary>Edit > Step Back To...: pick a checkpoint, then step back.</summary>
    public async Task StepBackTo()
    {
        if (CannotStepBack()) return;
        var rows = h.Checkpoints();
        if (rows.Count == 0) return;
        if (await ui.PickCheckpoint(rows, confirmOnly: false) is not { } row) return;
        StepBack(row);
    }

    /// <summary>Autosaves first (a failure is logged and does not block), then steps back; when the history did rewind, closes the
    /// formula windows that belonged to the old state and says so.</summary>
    public void StepBack(CheckpointRow row)
    {
        if (CannotStepBack()) return;
        try
        {
            if (autosaver?.SaveForced(now()) != null) Autosaved?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke("step back autosave: " + ex.Message);
        }
        var rewound = false;
        void OnRewound() => rewound = true;
        h.Rewound += OnRewound;
        try { port().StepBack(row); }
        finally { h.Rewound -= OnRewound; }
        if (!rewound) return;
        ui.CloseFormulaWindows();
        ui.Status(SteppedBackText);
    }

    /// <summary>Runs a fire-and-forget task; its exception goes to <see cref="Failed"/> (rethrown when unset).</summary>
    private async void Observe(Task t)
    {
        try { await t; }
        catch (Exception ex) when (Failed != null) { Failed(ex); }
    }
}
