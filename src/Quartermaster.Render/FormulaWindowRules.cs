using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>How one batch-grid cell is drawn: normal, a wrong typed value (red background), an applied row (grey text) or an exception (red text).</summary>
public enum CellTone { Normal, Wrong, Applied, Exception }

/// <summary>Text and tone of one batch-grid cell.</summary>
public sealed record BatchCell(string Text, CellTone Tone);

/// <summary>
/// UI-free rules of the formula window, the batch grid and the missing-input dialog, shared by the WinForms and Avalonia apps.
/// Texts are copied exactly from the WinForms windows.
/// </summary>
public static class FormulaWindowRules
{
    // ---- formula window ----

    /// <summary>De-dupe key of an open formula window: the same formula, unit and run are one window.</summary>
    public static string Key(string formulaId, string? pieceId, string? runId) =>
        string.Join("\u001f", formulaId, pieceId ?? "\0", runId ?? "\0");

    /// <summary>The window title: the formula name, then ": unit" when it runs on a unit.</summary>
    public static string Title(FormulaDef f, string? pieceName) => pieceName == null ? f.Name : $"{f.Name}: {pieceName}";

    /// <summary>Null when the viewer may act on the unit, else the refusal sentence.</summary>
    public static string? Refusal(ModuleSession s, string? pieceId) =>
        s.CanAct(pieceId) ? null : "That unit " + ModuleSession.OtherSideText + ".";

    /// <summary>What the difficulty means for the window: typed-and-checked boxes, a cheat sheet beside it (semi-automatic), a Calculate button.</summary>
    public static (bool Checked, bool ShowCheatSheet, bool ShowCalculate) Mode(Difficulty d)
    {
        var manual = d != Difficulty.FullyAuto;
        return (manual, d == Difficulty.SemiAuto, manual);
    }

    /// <summary>
    /// The group dice a dialog linked to a pending run uses (<see cref="FormulaDialogModel.SharedRolls"/>, B4): those the unit's roll group
    /// already drew (<see cref="RollGroups.SharedFor"/>). Null for a menu run (a group of one), a unit without a group or before its group rolled.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<int>>? SharedRolls(ModuleSession s, string? runId, string? pieceId) =>
        runId == null || s?.Game?.State is not { } st ? null : RollGroups.SharedFor(st, runId, pieceId);

    /// <summary>The side a dialog linked to a pending run answers for (<see cref="FormulaDialogModel.AskSideId"/>, B7): the run's
    /// <see cref="PendingRun.AskSideId"/>; null for a menu run or a run no side is asked.</summary>
    public static string? AskSideOf(ModuleSession s, string? runId) =>
        runId == null ? null : s?.Game?.State.Pending.FirstOrDefault(r => r != null && r.Id == runId)?.AskSideId;

    /// <summary>True when a dialog is a player-started run, so the formula's phase limit applies (not a waiting run, not a Workbench preview).</summary>
    public static bool EnforcesPhaseLimit(string? runId, bool previewOnly) => runId == null && !previewOnly;

    /// <summary>True when the linked run is no longer pending for the unit (phase change or replaced game).</summary>
    public static bool RunGone(PhaseController? c, string? pieceId, string? runId) =>
        runId != null && c != null && !c.IsPending(runId, pieceId);

    /// <summary>True when the unit the window is for no longer exists.</summary>
    public static bool PieceGone(ModuleSession s, string? pieceId) =>
        pieceId != null && s.Game?.State.Pieces.ContainsKey(pieceId) != true;

    /// <summary>The window should close: its run was cleared, or its unit is gone.</summary>
    public static bool ShouldClose(ModuleSession s, PhaseController? c, string? pieceId, string? runId) =>
        RunGone(c, pieceId, runId) || PieceGone(s, pieceId);

    // ---- batch grid ----

    public const string BatchUnit = "Unit", BatchResult = "Result", BatchLedger = "Ledger", BatchExceptions = "Exceptions";

    public const string AppliedText = "Applied";
    public const string SelectRowText = "Select a row first.";
    public const string RowAppliedText = "Row applied.";
    public const string RowNotMatchingText = "That row does not match yet: fix the red cells or press Calculate all.";
    public const string NothingAppliedText = "Nothing applied: check the red cells, or press Calculate all.";
    public const string AskingHostText = "Asking the host…";
    public const string DoesntMatchTip = "Doesn't match";

    public static string CalculatedText(int rows) => "Calculated " + rows + " row(s).";
    public static string AppliedRowsText(int n) => $"Applied {n} row(s); rows with a red cell were skipped.";
    public static string HostCalculatedText(int rows) => $"The host calculated {rows} row(s).";
    public static string HostAppliedText(int applied, int rows) => $"The host applied {applied} of {rows} row(s).";
    public static string WentWrongText(string message) => "Something went wrong: " + message;

    /// <summary>A fixed column of the grid: Unit, Result, Ledger or Exceptions (anything else is empty). Variable columns use <see cref="VariableCell"/>.</summary>
    public static BatchCell CellOf(BatchRowView row, string column)
    {
        var applied = row.Applied ? CellTone.Applied : CellTone.Normal;
        return column switch
        {
            BatchUnit => new(row.Label, applied),
            BatchResult => new(row.Applied && row.Result.Length == 0 ? AppliedText : row.Result, applied),
            BatchLedger => new(row.LedgerText, applied),
            BatchExceptions => new(row.Exceptions, row.Applied ? CellTone.Applied : row.Exceptions.Length > 0 ? CellTone.Exception : CellTone.Normal),
            _ => new("", CellTone.Normal),
        };
    }

    /// <summary>The cell of variable column <paramref name="index"/> (0-based): wrong values are red, applied rows grey.</summary>
    public static BatchCell VariableCell(BatchRowView row, int index) =>
        new(index >= 0 && index < row.Cells.Count ? row.Cells[index] : "",
            index >= 0 && index < row.Wrong.Count && row.Wrong[index] ? CellTone.Wrong : row.Applied ? CellTone.Applied : CellTone.Normal);

    // ---- menu batch (B4) ----

    /// <summary>Counter menu submenus: the clicked counter in a selection of two or more, else its stack.</summary>
    public const string CalculateOnSelectionText = "Calculate on selection", CalculateOnStackText = "Calculate on stack";

    /// <summary>The formula window's button of a menu run (no run link) and its tooltip.</summary>
    public const string SeveralRunsText = "Several runs…";
    public const string SeveralRunsTip = "Open a batch grid for this counter: one row per run, applied in order as one step.";

    /// <summary>The menu batch grid's first line: its Input and Piece cells fill every row whose own cell is blank.</summary>
    public const string AllRowsText = "All rows";

    public const string AddRowText = "Add row", AddRowTip = "Add a run of the selected row's counter.";
    public const string RemoveRowText = "Remove row", RemoveRowTip = "Remove the selected row (not the All rows line).";
    public const string CalculateAllText = "Calculate all", ApplyAllText = "Apply all";

    /// <summary>Exceptions cell of a menu batch row whose counter an earlier row removes (the row is skipped; it does not block).</summary>
    public const string RemovedByEarlierRowText = "removed by an earlier row";

    /// <summary>Status when a row would make the batch longer than <see cref="MenuBatchModel.MaxBatchRows"/>.</summary>
    public const string MaxRowsText = "A batch holds at most 100 rows.";

    /// <summary>Status when a menu batch is opened on more counters than <see cref="MenuBatchModel.MaxBatchRows"/> (only the first are taken).</summary>
    public const string CutRowsText = "A batch holds at most 100 rows: only the first 100 were taken.";

    /// <summary>
    /// Status of a menu batch whose live apply stopped at <paramref name="row"/> (1-based): nothing applied, or the earlier rows applied as
    /// one step (they stay). Counts only, so a guest learns nothing hidden.
    /// </summary>
    public static string StoppedText(int applied, int row) =>
        applied == 0 ? NothingAppliedRowText(row) : AppliedRunsText(applied) + " " + RowMessage(row, "cannot be applied yet.");

    /// <summary>The menu batch grid's title.</summary>
    public static string MenuBatchTitle(string formula, int runs) => $"{formula} — {runs} runs";

    public static string AppliedRunsText(int runs) => $"Applied {runs} run(s) as one step.";
    public static string NothingAppliedRowText(int row) => $"Nothing applied: row {row} cannot be applied yet.";
    public static string HostAppliedRunsText(int runs) => $"The host applied {runs} run(s) as one step.";

    /// <summary>The host's message for a refused menu batch row (1-based), with the single run's neutral text.</summary>
    public static string RowMessage(int row, string message) => $"Row {row}: {message}";

    // ---- missing-input dialog ----

    /// <summary>Head line: the values asked for (singular/plural), or the per-unit fallback when nothing can be asked.</summary>
    public static string MissingHead(string formula, int units, int variables) =>
        variables == 0
            ? MissingFailed(formula, units)
            : $"{formula} needs {(variables == 1 ? "a value" : "values")} for {units} unit(s). The answer is used for every unit of this run.";

    /// <summary>Head line when the run cannot be calculated automatically and there is nothing to ask.</summary>
    public static string MissingFailed(string formula, int units) =>
        $"{formula} could not be calculated automatically. Use Per unit… to work through the {units} unit(s).";

    /// <summary>"N applied; M failed. " prefix of the failure message.</summary>
    public static string MissingResult(int applied, int failed) => $"{applied} applied; {failed} failed. ";

    /// <summary>The full failure line: the counts, the first three reasons and an ellipsis when there are more.</summary>
    public static string MissingFailureLine(int applied, IReadOnlyList<string> failed) =>
        MissingResult(applied, failed.Count) + string.Join("; ", failed.Take(3)) + (failed.Count > 3 ? "…" : "");

    /// <summary>OK is enabled when every asked variable has a non-blank answer. With <paramref name="checkRange"/>, a numeric answer outside the variable's Min/Max also disables it.</summary>
    public static bool MissingOkEnabled(IReadOnlyDictionary<string, string?> answers, IReadOnlyList<MissingInput> inputs, bool checkRange = true)
    {
        if (inputs.Count == 0) return false;
        foreach (var i in inputs)
        {
            var text = answers.TryGetValue(i.Variable, out var a) ? a?.Trim() : null;
            if (string.IsNullOrEmpty(text)) return false;
            if (checkRange && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                && ((i.Min is { } lo && v < lo) || (i.Max is { } hi && v > hi))) return false;
        }
        return true;
    }
}
