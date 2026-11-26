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

    public const string RepeatCaption = "Next Phase";
    public const string ContinueText = "Continue";
    public const string CancelText = "Cancel";

    public static string RepeatMessage(RepeatAsk ask) => $"End of {ask.Label} {ask.Number}: play another {ask.Label} or continue?";
    public static string RepeatButton(RepeatAsk ask) => $"Another {ask.Label}";

    /// <summary>The Next Phase question (B5 #6): Another {label} = true, Continue (the default) = false, Cancel or closing = null.</summary>
    public async Task<bool?> AskRepeat(RepeatAsk ask)
    {
        var answer = await host.Dialogs.ChooseAsync(host, RepeatMessage(ask), RepeatCaption, new[] { RepeatButton(ask), ContinueText, CancelText }, 1);
        return answer switch { 0 => true, 1 => false, _ => null };
    }
}
