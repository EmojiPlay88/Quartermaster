using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Phases;

/// <summary>
/// The Avalonia side of <see cref="PhaseRunDispatch"/> (WinForms MainForm.PhaseUi): the open-decisions dialog through <c>IDialogs</c>, the
/// formula windows and the status line. The batch grid and the missing-input dialog come in a later task (status text / Cancel until then).
/// </summary>
sealed class DesktopPhaseUi(MainWindow host, ModuleSession s, Func<PhaseRunDispatch?> dispatch) : IPhaseUi
{
    public Task<bool> ConfirmOpenDecisions(IReadOnlyList<Decision> open) =>
        host.Dialogs.ConfirmOpenDecisionsAsync(host, open, d => host.GuardUi(() => dispatch()?.JumpToDecision(d)));

    /// <summary>Step Back comes with its dialog in a later task: nothing is picked.</summary>
    public Task<CheckpointRow?> PickCheckpoint(IReadOnlyList<CheckpointRow> rows, bool confirmOnly) => Task.FromResult<CheckpointRow?>(null);

    public void OpenUnitDialog(string formulaId, string? pieceId, string runId, double? moved) =>
        FormulaWindows.Show(host, s, formulaId, pieceId, new RunLink(runId, moved));

    /// <summary>Stub until the batch grid exists: the run stays pending in the phase panel.</summary>
    public void OpenBatch(string runId)
    {
        var name = s.Game?.State.Pending.FirstOrDefault(r => r.Id == runId)?.FormulaName ?? "Calculation";
        host.SetStatus($"{name}: open it from the phase panel");
    }

    /// <summary>Stub until the missing-input dialog exists: cancelled, so the run stays pending.</summary>
    public Task<MissingInputResult> AskMissing(PendingRun run) => Task.FromResult(MissingInputResult.Cancel);

    public void CloseFormulaWindows() => FormulaWindows.CloseAllFor(s);
    public void Centre(string pieceId) => host.CentreOnPiece(pieceId);
    public void Status(string text) => host.SetStatus(text);
    public void RevealPhasePanel() => host.RevealPhasePanel();
    public void SelectChecklist(int index) => host.Phases.SelectChecklist(index);
}
