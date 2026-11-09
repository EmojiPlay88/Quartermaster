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
