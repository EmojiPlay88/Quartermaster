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
/// <see cref="PhaseController.ApplyFormula"/> (or the action port's <see cref="IActionPort.ApplyTrace"/>) with the row's expected engine
/// run, so a wrong typed value never reaches the ledger.
/// </summary>
public sealed class BatchModel(ModuleSession s, Func<string?, string?, FormulaTrace, bool, bool> apply, string runId)
{
    public BatchModel(ModuleSession s, PhaseController phases, string runId) : this(s, phases.ApplyFormula, runId) { }

    public BatchModel(ModuleSession s, IActionPort actions, string runId) : this(s, actions.ApplyTrace, runId) { _port = actions; }

    private readonly IActionPort? _port;

    /// <summary>
    /// True for an online guest's grid: rows are sent to the host as formula intents (<see cref="SendRowsAsync"/>) and hidden cells read
    /// <see cref="FormulaMasking.HostCalculatesText"/>. The local Calculate / Apply members then do nothing useful (a guest has no
    /// hidden data and cannot write the ledger).
    /// </summary>
    public bool HostCalculates => _port?.IsRemote == true;

    /// <summary>The viewer the row models were masked for (reference compare: a new view set means a new viewer).</summary>
    private object? _maskedFor;

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
            foreach (var m in _models.Values)
            {
                m.Rebind(s.Runner, s.Runner.Pack.ById<FormulaDef>(f.Id) ?? f);
                FormulaMasking.Configure(m, s);
            }
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
            FormulaMasking.Configure(model, s);
            _models[row] = model;
        }
        CheckViewer();
        // Defence in depth: the mode is read again on every access, so a difficulty switch while a grid is open can never leave a row unchecked.
        model.Checked = s.Game!.State.Difficulty != Difficulty.FullyAuto;
        return model;
    }

    /// <summary>
    /// Hot-seat: another viewer masks every row again (hidden boxes drop what was typed into them) and drops every row view built for the
    /// previous viewer, including the snapshots of applied rows (they showed what that viewer could see).
    /// </summary>
    private void CheckViewer()
    {
        var viewer = (object?)FormulaMasking.ViewerSides(s);
        if (ReferenceEquals(viewer, _maskedFor)) return;
        _maskedFor = viewer;
        foreach (var m in _models.Values) FormulaMasking.Configure(m, s);
        _views.Clear();
        _appliedSnapshots.Clear();
    }

    /// <summary>The row of a unit the current viewer may not act on: nothing of it is shown.</summary>
    private BatchRowView HiddenRow(PendingUnit u)
    {
        var n = Variables.Count;
        var label = u.PieceId != null && s.CanSee(u.PieceId) ? u.Label : ModuleSession.HiddenText;
        return new BatchRowView(u.PieceId, label, Enumerable.Repeat(ModuleSession.HiddenText, n).ToList(), new bool[n],
            ModuleSession.HiddenText, "", ModuleSession.OtherSideText, u.Applied, false);
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
        CheckViewer();
        if (!s.CanAct(u.PieceId)) return HiddenRow(u);   // applied or not: another side's unit shows nothing
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
        if (!s.CanAct(u.PieceId)) return HiddenRow(u);   // hot-seat: another side's unit is never evaluated or shown
        var model = ModelFor(i);
        var dv = model.Evaluate(s.Game!.State);
        var vars = Variables;
        var cells = vars.Select(v => dv.Boxes.FirstOrDefault(b => b.Variable == v)?.Text ?? "").ToList();
        var wrong = vars.Select(v => dv.Boxes.FirstOrDefault(b => b.Variable == v)?.Wrong == true).ToList();
        return new BatchRowView(u.PieceId, u.Label, cells, wrong, dv.ResultText, LedgerOf(dv), ExceptionsFor(u, dv), false,
            dv.CanApply || (HostCalculates && model.Hidden.Count > 0));
    }

    /// <summary>The (ledger key, property id) pairs a pending row would write; empty for an applied row or a failed run.</summary>
    public IReadOnlyList<(string Key, string PropertyId)> OutputsOf(int row)
    {
        if (Unit(row) is not { Applied: false } u || s.Game == null || !s.CanAct(u.PieceId)) return Array.Empty<(string, string)>();
        try
        {
            var dv = ModelFor(row).Evaluate(s.Game.State);
            return dv.Trace.Error != null || dv.Trace.Result == null ? Array.Empty<(string, string)>()
                : dv.Trace.Outputs.Select(o => (o.Key, o.PropertyId)).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Array.Empty<(string, string)>(); }
    }

    /// <summary>What the row would write. A value the viewer may not see (another side's holder) shows only its name.</summary>
    private string LedgerOf(DialogView dv)
    {
        if (dv.Trace.Error != null || dv.Trace.Result == null) return "";
        var sides = FormulaMasking.ViewerSides(s);
        return string.Join("; ", dv.Trace.Outputs.Select(o => sides == null || s.CanEditLedgerFor(o.Key, sides)
            ? $"{o.PropertyName} {Num(o.Before)} → {Num(o.After)}"
            : $"{o.PropertyName} changes ({ModuleSession.HiddenText})"));
    }

    private static string Num(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    private string ExceptionsFor(PendingUnit u, DialogView dv)
    {
        var parts = new List<string>();
        var sides = FormulaMasking.ViewerSides(s);
        if (dv.Trace.Error == null && dv.Trace.Outputs.Any(o => o.After < 0 && (sides == null || s.CanEditLedgerFor(o.Key, sides))))
            parts.Add("goes negative");   // only for values the viewer may see
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
        if (s.Game == null || Unit(row) is not { Applied: false } u || !s.CanAct(u.PieceId)) return false;
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
        if (!apply(runId, u.PieceId, trace, model.AutoCalculated)) return false;
        _appliedSnapshots[row] = snapshot;
        _views.Remove(row);
        return true;
    }

    /// <summary>
    /// Online guest: sends each open row the guest may act on to the host as a formula intent (typed cells, or Calculate) and shows the
    /// host's verdict on the row. Returns how many the host applied and the last refusal message (null when none).
    /// </summary>
    /// <param name="preview">Calculate all: the host only previews each row (fills the cells, pins its dice); nothing is applied.</param>
    public async Task<(int Applied, string? Message)> SendRowsAsync(IReadOnlyList<int> rows, bool calculate, bool preview = false)
    {
        if (_port == null || rows == null) return (0, null);
        var applied = 0;
        string? message = null;
        foreach (var row in rows)
        {
            if (s.Game == null || Unit(row) is not { Applied: false } u || !s.CanAct(u.PieceId)) continue;
            var v = await FormulaMasking.SendAsync(_port, ModelFor(row), runId, calculate, preview).ConfigureAwait(true);
            _views.Remove(row);
            if (v.Applied) applied++;
            else if (v.Message != null) message = v.Message;
            if (v.Message == GuestSession.NoAnswerText) break;   // the host is gone: don't wait again for every row
        }
        Invalidate();
        return (applied, message);
    }

    /// <summary>The rows <see cref="SendRowsAsync"/> would send for "all": every open row.</summary>
    public IReadOnlyList<int> OpenRows() =>
        Enumerable.Range(0, RowCount).Where(i => Unit(i) is { Applied: false }).ToList();

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
            var open = run.Units.Count(u => u is { Applied: false } && s.CanAct(u.PieceId));   // hot-seat: only units this player may apply
            using var action = g.BeginAction(ActionLabel(run.FormulaName, open));
            for (var i = 0; i < n; i++)
                if (ApplyOne(i)) applied++;
        }
        Invalidate();
        return applied;
    }
}
