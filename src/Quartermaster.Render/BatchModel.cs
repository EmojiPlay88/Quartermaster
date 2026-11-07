using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>One row of the batch grid. Wrong[i] is true when Cells[i] (in <see cref="BatchModel.Variables"/> order) fails the check.</summary>
public sealed record BatchRowView(string? PieceId, string Label, IReadOnlyList<string> Cells, IReadOnlyList<bool> Wrong, string Result,
    string LedgerText, string Exceptions, bool Applied, bool CanApply);

/// <summary>
/// UI-free model of the batch grid: one row per unit of a pending run. Each row owns a <see cref="FormulaDialogModel"/> built on demand,
/// so a 1,200-unit run only pays for the rows that are shown or acted on. Applying goes through
/// <see cref="PhaseController.ApplyFormula"/> with the row's expected engine run, so a wrong typed value never reaches the ledger.
/// </summary>
public sealed class BatchModel(ModuleSession s, PhaseController phases, string runId)
{
    private readonly Dictionary<int, FormulaDialogModel> _models = new();
    private readonly Dictionary<int, BatchRowView> _views = new();
    private readonly Dictionary<int, BatchRowView> _appliedSnapshots = new();
    private Dictionary<string, List<string>>? _warningsByPiece;
    private FormulaRunner? _runner;

    private PendingRun? Run => s.Game?.State.Pending.FirstOrDefault(r => r != null && r.Id == runId && r.Units != null);

    private RulesPack? _formulaPack;
    private string? _formulaId;
    private FormulaDef? _formula;
    private IReadOnlyList<string>? _variables;

    /// <summary>Cached per (rules pack, formula id): rules edits and different runs recompute it, row access does not.</summary>
    private FormulaDef? Formula
    {
        get
        {
            if (Run is not { } r || s.Rules is not { } pack) return null;
            if (!ReferenceEquals(_formulaPack, pack) || _formulaId != r.FormulaId)
            {
                _formulaPack = pack;
                _formulaId = r.FormulaId;
                _formula = pack.ById<FormulaDef>(r.FormulaId);
                _variables = null;
            }
            return _formula;
        }
    }

    /// <summary>Number of per-row models built so far (tests: laziness).</summary>
    internal int BuiltCount => _models.Count;

    public IReadOnlyList<string> Variables =>
        Formula is { } f ? _variables ??= f.Bindings.Select(b => b.Variable).ToList() : Array.Empty<string>();

    /// <summary>The dropdown keys of an Input column (its own Choices, or the table keys it indexes). Null: free text (also for range keys).</summary>
    public IReadOnlyList<string>? ChoicesFor(string variable) =>
        Formula is { } f && s.Rules is { } pack ? KeyChoices.For(pack, f, variable) : null;

    public int RowCount => Run != null && Formula != null ? Run.Units.Count : 0;

    public string Title
    {
        get
        {
            if (Run is not { } r) return "(finished)";
            if (Formula == null) return "(formula not found)";
            return $"{r.FormulaName} — {r.Units.Count(u => u is { Applied: true })} of {r.Units.Count} applied";
        }
    }

    private PendingUnit? Unit(int row) => Run is { } r && row >= 0 && row < r.Units.Count ? r.Units[row] : null;

    /// <summary>The dialog model behind a row (created on first use).</summary>
    public FormulaDialogModel ModelFor(int row)
    {
        if (s.Runner == null || Formula is not { } f || Unit(row) is not { } u) throw new ArgumentOutOfRangeException(nameof(row));
        if (!ReferenceEquals(_runner, s.Runner))
        {
            // The rules pack changed: keep what was typed, run the new runner and formula.
            _runner = s.Runner;
            foreach (var m in _models.Values) m.Rebind(s.Runner, s.Runner.Pack.ById<FormulaDef>(f.Id) ?? f);
            _views.Clear();
            _warningsByPiece = null;
        }
        if (!_models.TryGetValue(row, out var model))
        {
            model = new FormulaDialogModel(s.Runner, f, u.PieceId)
            {
                Checked = s.Game!.State.Difficulty != Difficulty.FullyAuto,
                Moved = u.Moved,
            };
            _models[row] = model;
        }
        // Defence in depth: the mode is read again on every access, so a difficulty switch while a grid is open can never leave a row unchecked.
        model.Checked = s.Game!.State.Difficulty != Difficulty.FullyAuto;
        return model;
    }

    /// <summary>Drops cached row views and warning hits (the game state or rules changed). Typed values are kept.</summary>
    public void Invalidate()
    {
        _views.Clear();
        _warningsByPiece = null;
    }

    public BatchRowView Row(int i)
    {
        if (Unit(i) is not { } u) return new BatchRowView(null, "", Array.Empty<string>(), Array.Empty<bool>(), "", "", "", false, false);
        if (u.Applied) return _appliedSnapshots.TryGetValue(i, out var snap) ? snap : Done(u);
        if (_views.TryGetValue(i, out var cached)) return cached;
        var view = Build(i, u);
        _views[i] = view;
        return view;
    }

    private BatchRowView Done(PendingUnit u)
    {
        var n = Variables.Count;
        return new BatchRowView(u.PieceId, u.Label, Enumerable.Repeat("", n).ToList(), new bool[n], "", "", "", true, false);
    }

    private BatchRowView Build(int i, PendingUnit u)
    {
        var model = ModelFor(i);
        var dv = model.Evaluate(s.Game!.State);
        var vars = Variables;
        var cells = vars.Select(v => dv.Boxes.FirstOrDefault(b => b.Variable == v)?.Text ?? "").ToList();
        var wrong = vars.Select(v => dv.Boxes.FirstOrDefault(b => b.Variable == v)?.Wrong == true).ToList();
        return new BatchRowView(u.PieceId, u.Label, cells, wrong, dv.ResultText, LedgerOf(dv), ExceptionsFor(u, dv), false, dv.CanApply);
    }

    private static string LedgerOf(DialogView dv) => string.Join("; ", dv.Trace.Error != null || dv.Trace.Result == null
        ? Array.Empty<string>()
        : dv.Trace.Outputs.Select(o => $"{o.PropertyName} {Num(o.Before)} → {Num(o.After)}"));

    private static string Num(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    private string ExceptionsFor(PendingUnit u, DialogView dv)
    {
        var parts = new List<string>();
        if (dv.Trace.Error == null && dv.Trace.Outputs.Any(o => o.After < 0)) parts.Add("goes negative");
        if (u.PieceId != null && WarningsByPiece().TryGetValue(u.PieceId, out var names)) parts.AddRange(names);
        return string.Join("; ", parts);
    }

    private Dictionary<string, List<string>> WarningsByPiece()
    {
        if (_warningsByPiece != null) return _warningsByPiece;
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            if (s.Game != null && s.Rules is { } pack && Sequencer.Current(pack, s.Game.State) is { } phase)
                foreach (var w in Decisions.Warnings(pack, s.Game.State, phase))
                {
                    var name = (w.Message ?? "").Replace("{unit}", "").Trim();
                    if (name.Length == 0) name = "warning";
                    foreach (var id in w.PieceIds)
                    {
                        if (!map.TryGetValue(id, out var list)) map[id] = list = new();
                        if (!list.Contains(name)) list.Add(name);
                    }
                }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return _warningsByPiece = map;
    }

    public void SetCell(int row, string variable, string text)
    {
        if (Unit(row) is not { Applied: false }) return;
        ModelFor(row).SetBoxText(variable, text);
        _views.Remove(row);
    }

    public void CalculateRow(int row)
    {
        if (Unit(row) is not { Applied: false }) return;
        ModelFor(row).Calculate(s.Game!.State);
        _views.Remove(row);
    }

    public void CalculateAll()
    {
        for (var i = 0; i < RowCount; i++) CalculateRow(i);
    }

    /// <summary>Applies one row if it can apply (every checked cell matches). Returns true when the unit is now applied.</summary>
    public bool ApplyRow(int row)
    {
        var done = ApplyOne(row);
        Invalidate();
        return done;
    }

    private bool ApplyOne(int row)
    {
        if (s.Game == null || Unit(row) is not { Applied: false } u) return false;
        var model = ModelFor(row);
        var trace = model.Run(s.Game.State);
        var dv = model.View(trace);
        if (!dv.CanApply) return false;
        // Cheap snapshot from the view already computed (no warnings scan, no second evaluation).
        var vars = Variables;
        var snapshot = _views.TryGetValue(row, out var cached) ? cached with { Applied = true, CanApply = false }
            : new BatchRowView(u.PieceId, u.Label, vars.Select(v => dv.Boxes.FirstOrDefault(b => b.Variable == v)?.Text ?? "").ToList(),
                new bool[vars.Count], dv.ResultText, LedgerOf(dv), "", true, false);
        // Refused (run cleared, unit already applied): nothing was written, so no snapshot and not counted.
        if (!phases.ApplyFormula(runId, u.PieceId, trace, model.AutoCalculated)) return false;
        _appliedSnapshots[row] = snapshot;
        _views.Remove(row);
        return true;
    }

    /// <summary>Undo label of an "apply to every unit" action: "{formula} on {n} units".</summary>
    internal static string ActionLabel(string formulaName, int units) => $"{formulaName} on {units} {(units == 1 ? "unit" : "units")}";

    /// <summary>Applies every row that can apply (rows with a wrong or missing checked cell are left alone). Returns how many were applied.
    /// Caches are dropped once at the end, not per row.</summary>
    public int ApplyAll()
    {
        var n = RowCount;
        var applied = 0;
        if (s.Game is { } g && Run is { } run)
        {
            // One undoable action for the whole grid. The label is fixed when the action opens, so it names the units still open.
            var open = run.Units.Count(u => u is { Applied: false });
            using var action = g.BeginAction(ActionLabel(run.FormulaName, open));
            for (var i = 0; i < n; i++)
                if (ApplyOne(i)) applied++;
        }
        Invalidate();
        return applied;
    }
}
