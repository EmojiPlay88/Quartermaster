using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Phases;

/// <summary>
/// The Avalonia side of <see cref="PhaseRunDispatch"/> (WinForms MainForm.PhaseUi): the open-decisions dialog through <c>IDialogs</c>, the
/// Step Back dialog, the missing-input dialog, the formula windows, the batch grids and the status line.
/// </summary>
sealed class DesktopPhaseUi(MainWindow host, ModuleSession s, Func<PhaseRunDispatch?> dispatch) : IPhaseUi
{
    public Task<bool> ConfirmOpenDecisions(IReadOnlyList<Decision> open) =>
        host.Dialogs.ConfirmOpenDecisionsAsync(host, open, d => host.GuardUi(() => dispatch()?.JumpToDecision(d)));

    public Task<CheckpointRow?> PickCheckpoint(IReadOnlyList<CheckpointRow> rows, bool confirmOnly) =>
        host.Dialogs.PickCheckpointAsync(host, rows, confirmOnly);

    public void OpenUnitDialog(string formulaId, string? pieceId, string runId, double? moved) =>
        FormulaWindows.Show(host, s, formulaId, pieceId, new RunLink(runId, moved));

    /// <summary>The run's batch grid (WinForms PhaseUi.OpenBatch); double-clicking a row opens that unit's window through the dispatch.</summary>
    public void OpenBatch(string runId)
    {
        if (host.Actions is not { } port || dispatch() is not { } d) return;
        FormulaWindows.ShowBatch(host, s, port, runId, row => host.GuardUi(() => d.OpenBatchRow(runId, row)));
    }

    public Task<MissingInputResult> AskMissing(PendingRun run) =>
        host.Actions is { } port ? host.Dialogs.AskMissingAsync(host, s, port, run) : Task.FromResult(MissingInputResult.Cancel);

    public void CloseFormulaWindows() => FormulaWindows.CloseAllFor(s);
    public void Centre(string pieceId) => host.CentreOnPiece(pieceId);
    public void Status(string text) => host.SetStatus(text);
    public void RevealPhasePanel() => host.RevealPhasePanel();
    public void SelectChecklist(int index) => host.Phases.SelectChecklist(index);
}
