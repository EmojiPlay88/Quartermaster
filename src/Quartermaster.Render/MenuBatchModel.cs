using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>
/// UI-free model of a menu batch grid (B4): one formula run once per row, each row on a counter (a selection, a stack, or repeated runs of
/// one counter), applied in order as ONE undo step. It lives in memory only (closing the grid discards it; it is never saved).
/// <para>Rows are only counters the viewer may act on, listed from the viewer's own state. Each row owns a <see cref="FormulaDialogModel"/>
/// (masked as the formula dialog is). An <see cref="AllRows"/> line fills every row's blank Input and Piece cells.</para>
/// <para>Rows see the rows before them through a preview chain: the rows are evaluated in order on one clone of the game state
/// (<see cref="GameState.DeepClone"/>), each row's commands applied to it before the next row runs. The chain is built once per change
/// (a cell, a row, the game state, the viewer), not per row read; no clone is made until a row's commands must be seen by a later row.
/// A row whose counter an earlier row removes is skipped (<see cref="FormulaWindowRules.RemovedByEarlierRowText"/>). With
/// <see cref="FormulaDef.GroupRoll"/> the first row that rolls a key fixes it for the whole batch; otherwise every row rolls its own.</para>
/// <para><see cref="ApplyAll"/> is all or nothing: when any open row cannot apply in the chain nothing is applied; otherwise one action
/// (<see cref="ActionLabel"/>) applies every row on the live state through the port, each its own run (working line, run stamp).</para>
/// </summary>
public sealed class MenuBatchModel : IBatchRows
{
    /// <summary>Most rows one batch holds (also the most a guest's batch intent may carry).</summary>
    public const int MaxBatchRows = 100;

    private readonly ModuleSession s;
    private readonly Func<string?, string?, FormulaTrace, bool, bool> _apply;
    private readonly IActionPort? _port;
    private readonly string _formulaId;

    private sealed class Slot(string pieceId)
    {
        public string PieceId { get; } = pieceId;
        public FormulaDialogModel? Model;
        /// <summary>What the player typed into this row's Input and Piece cells (blank = take the All rows line's).</summary>
        public readonly Dictionary<string, string> Own = new(StringComparer.Ordinal);
        /// <summary>What was last written into the model for each Input and Piece cell.</summary>
        public readonly Dictionary<string, string> Pushed = new(StringComparer.Ordinal);
    }

    /// <summary>One row of the evaluated chain: skipped (removed, or the viewer may not act on it) or its view on the chain's state.</summary>
    private sealed record Link(bool Skipped, string? Exceptions, DialogView? View);

    private readonly List<Slot> _rows = new();
    private readonly Dictionary<string, string> _all = new(StringComparer.Ordinal);
    private Link[]? _chain;
    private object? _chainKey;
    private object? _maskedFor;
    private FormulaRunner? _runner;

    /// <param name="apply">Applies one row's run (<see cref="PhaseController.ApplyFormula"/> or the port's <see cref="IActionPort.ApplyTrace"/>).</param>
    /// <param name="pieceIds">The rows' counters in order (a counter may repeat). Counters the viewer may not act on, that are not in its
    /// state or that the formula does not apply to are left out; at most <see cref="MaxBatchRows"/> rows are taken.</param>
    public MenuBatchModel(ModuleSession s, Func<string?, string?, FormulaTrace, bool, bool> apply, string formulaId, IReadOnlyList<string> pieceIds)
    {
        this.s = s ?? throw new ArgumentNullException(nameof(s));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _formulaId = formulaId ?? throw new ArgumentNullException(nameof(formulaId));
        if (s.Game?.State is not { } st || s.Rules is not { } pack || pack.ById<FormulaDef>(formulaId) is not { } f) return;
        var applies = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var id in pieceIds ?? Array.Empty<string>())
        {
            if (id == null || !st.Pieces.TryGetValue(id, out var p) || !s.CanAct(id)) continue;
            if (!applies.TryGetValue(id, out var ok))
            {
                try { ok = PieceFilter.FormulasFor(pack, p, st, s.Model).Any(x => x.Id == f.Id); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { ok = false; }
                applies[id] = ok;
            }
            if (!ok) continue;
            if (_rows.Count >= MaxBatchRows)
            {
                Status = FormulaWindowRules.CutRowsText;   // only counters this viewer may act on are counted
                break;
            }
            _rows.Add(new Slot(id));
        }
    }

    public MenuBatchModel(ModuleSession s, PhaseController phases, string formulaId, IReadOnlyList<string> pieceIds)
        : this(s, phases.ApplyFormula, formulaId, pieceIds) { }

    public MenuBatchModel(ModuleSession s, IActionPort actions, string formulaId, IReadOnlyList<string> pieceIds)
        : this(s, actions.ApplyTrace, formulaId, pieceIds) { _port = actions; }

    /// <summary>True for an online guest's grid: the batch is sent to the host as one intent (<see cref="SendAsync"/>).</summary>
    public bool HostCalculates => _port?.IsRemote == true;

    /// <summary>The last status line: applied, nothing applied (and the row that blocks), the row limit, or the host's answer.</summary>
    public string? Status { get; private set; }

    /// <summary>True after the last Apply all (local, or the host's verdict for a guest) applied every row; false after a refusal, a stop, or a preview.</summary>
    public bool FullyApplied { get; private set; }

    private FormulaDef? Formula => s.Rules?.ById<FormulaDef>(_formulaId);

    public IReadOnlyList<string> Variables => Formula is { } f ? f.Bindings.Select(b => b.Variable).ToList() : Array.Empty<string>();

    public int RowCount => Formula == null ? 0 : _rows.Count;

    public string Title => Formula is { } f ? FormulaWindowRules.MenuBatchTitle(f.Name, _rows.Count) : "(formula not found)";

    /// <summary>The undo label of <see cref="ApplyAll"/> (see <see cref="ActionLabelFor"/>).</summary>
    public string ActionLabel => ActionLabelFor(Formula?.Name ?? _formulaId, _rows.Select(r => (string?)r.PieceId).ToList());

    /// <summary>"{formula} on {n} units" when every row is another counter, else "{formula}: {n} runs".</summary>
    public static string ActionLabelFor(string formulaName, IReadOnlyList<string?> pieceIds)
    {
        var n = pieceIds?.Count ?? 0;
        return pieceIds != null && pieceIds.Distinct(StringComparer.Ordinal).Count() == n
            ? BatchModel.ActionLabel(formulaName, n)
            : $"{formulaName}: {n} runs";
    }

    // ---- rows ----

    /// <summary>
    /// Adds a blank row for the counter of <paramref name="afterRow"/>, right after it (no row: after the last row, for its counter). False
    /// (and <see cref="FormulaWindowRules.MaxRowsText"/>) at <see cref="MaxBatchRows"/> rows.
    /// </summary>
    public bool AddRow(int? afterRow)
    {
        if (_rows.Count == 0) return false;
        if (_rows.Count >= MaxBatchRows)
        {
            Status = FormulaWindowRules.MaxRowsText;
            return false;
        }
        var at = afterRow is { } r && r >= 0 && r < _rows.Count ? r : _rows.Count - 1;
        _rows.Insert(at + 1, new Slot(_rows[at].PieceId));
        Invalidate();
        return true;
    }

    /// <summary>Removes a row; the last row left cannot be removed.</summary>
    public bool RemoveRow(int row)
    {
        if (_rows.Count <= 1 || row < 0 || row >= _rows.Count) return false;
        _rows.RemoveAt(row);
        Invalidate();
        return true;
    }

    private Slot? SlotAt(int row) => row >= 0 && row < _rows.Count ? _rows[row] : null;

    private VariableBinding? Binding(string variable) => Formula?.Bindings.FirstOrDefault(b => b?.Variable == variable);

    /// <summary>True for a column the <see cref="AllRows"/> line may fill: an Input or a Piece binding.</summary>
    public bool IsAllRowsColumn(string variable) => Binding(variable) is { Kind: BindingKind.Input or BindingKind.Piece };

    /// <summary>The All rows line: its Input and Piece cells (the others blank).</summary>
    public BatchRowView AllRows
    {
        get
        {
            var vars = Variables;
            return new BatchRowView(null, FormulaWindowRules.AllRowsText,
                vars.Select(v => IsAllRowsColumn(v) ? _all.GetValueOrDefault(v) ?? "" : "").ToList(), new bool[vars.Count], "", "", "", false, false);
        }
    }

    /// <summary>Sets an All rows cell (Input and Piece columns only): every row whose own cell is blank takes it.</summary>
    public void SetAllRowsCell(string variable, string text)
    {
        if (!IsAllRowsColumn(variable)) return;
        _all[variable] = text ?? "";
        Invalidate();
    }

    public void SetCell(int row, string variable, string text)
    {
        if (SlotAt(row) is not { } slot || Binding(variable) is not { } b) return;
        if (b.Kind is BindingKind.Input or BindingKind.Piece) slot.Own[variable] = text ?? "";
        else Model(row).SetBoxText(variable, text ?? "");
        Invalidate();
    }

    /// <summary>Writes each Input and Piece cell's effective text (the row's own, else the All rows line's) into the row's model.</summary>
    private void Sync(int row, FormulaDialogModel m)
    {
        var slot = _rows[row];
        foreach (var b in Formula?.Bindings ?? new List<VariableBinding>())
        {
            if (b is not { Kind: BindingKind.Input or BindingKind.Piece, Variable: { } v }) continue;
            var own = slot.Own.GetValueOrDefault(v);
            var text = !string.IsNullOrWhiteSpace(own) ? own : _all.GetValueOrDefault(v) ?? "";
            if (string.Equals(slot.Pushed.GetValueOrDefault(v) ?? "", text, StringComparison.Ordinal)) continue;
            m.SetBoxText(v, text);
            slot.Pushed[v] = text;
        }
    }

    /// <summary>The row's model, created on first use (a menu run: the formula's phase limit applies).</summary>
    private FormulaDialogModel Model(int row)
    {
        if (s.Runner is not { } runner || Formula is not { } f || SlotAt(row) is not { } slot) throw new ArgumentOutOfRangeException(nameof(row));
        if (!ReferenceEquals(_runner, runner))
        {
            // The rules pack changed: keep what was typed, run the new runner and formula.
            _runner = runner;
            foreach (var x in _rows.Where(x => x.Model != null))
            {
                x.Model!.Rebind(runner, runner.Pack.ById<FormulaDef>(f.Id) ?? f);
                FormulaMasking.Configure(x.Model, s);
                x.Pushed.Clear();
            }
            _chain = null;
        }
        if (slot.Model == null)
        {
            slot.Model = new FormulaDialogModel(runner, f, slot.PieceId) { EnforcePhaseLimit = true };
            FormulaMasking.Configure(slot.Model, s);
        }
        slot.Model.Checked = s.Game?.State.Difficulty != Difficulty.FullyAuto;
        return slot.Model;
    }

    /// <summary>The dialog model behind a row, as the chain last evaluated it (its group dice set).</summary>
    public FormulaDialogModel ModelFor(int row)
    {
        Chain();
        return Model(row);
    }

    /// <summary>
    /// Hot-seat: another viewer masks every row again (hidden boxes drop what was typed into them; picks are the previous viewer's) and
    /// drops the chain built for the previous viewer.
    /// </summary>
    private void CheckViewer()
    {
        var viewer = (object?)FormulaMasking.ViewerSides(s);
        if (ReferenceEquals(viewer, _maskedFor)) return;
        _maskedFor = viewer;
        foreach (var x in _rows.Where(x => x.Model != null))
        {
            FormulaMasking.Configure(x.Model!, s);
            x.Pushed.Clear();
        }
        _chain = null;
        _choices.Clear();
    }

    /// <summary>Drops the chain (typed values are kept). The dropdown lists stay: they depend only on the state and the viewer, which their
    /// cache key holds, so a cell edit never rebuilds a player's view for them.</summary>
    public void Invalidate() => _chain = null;

    // ---- the preview chain ----

    private Link[] Chain()
    {
        CheckViewer();
        if (s.Game?.State is not { } live || Formula == null || s.Runner == null) return Array.Empty<Link>();
        var key = (live, FormulaDialogModel.StateFingerprint(live), live.Position, live.Difficulty, s.Runner, s.Rules);
        if (_chain != null && _chain.Length == _rows.Count && Equals(_chainKey, key)) return _chain;
        _chain = Build(live, calculate: false);
        _chainKey = key;
        return _chain;
    }

    /// <summary>Evaluates every row in order on the chain (with <paramref name="calculate"/>, each row is Calculated there first).</summary>
    private Link[] Build(GameState live, bool calculate)
    {
        var f = Formula!;
        var runner = s.Runner!;
        var links = new Link[_rows.Count];
        var at = live;
        var cloned = false;
        var group = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        for (var i = 0; i < _rows.Count; i++)
        {
            var id = _rows[i].PieceId;
            // Another side's row (hot-seat, after a viewer change) is never evaluated or shown; it cannot apply.
            if (!s.CanAct(id)) { links[i] = new Link(true, ModuleSession.OtherSideText, null); continue; }
            if (!live.Pieces.ContainsKey(id)) { links[i] = new Link(true, FormulaIntentRunner.GoneUnitText, null); continue; }
            if (!at.Pieces.ContainsKey(id)) { links[i] = new Link(true, FormulaWindowRules.RemovedByEarlierRowText, null); continue; }
            var m = Model(i);
            Sync(i, m);
            m.SharedRolls = f.GroupRoll && group.Count > 0 ? new Dictionary<string, IReadOnlyList<int>>(group, StringComparer.Ordinal) : null;
            if (calculate && !HostCalculates) m.Calculate(at);
            var dv = m.Evaluate(at);
            links[i] = new Link(false, null, dv);
            if (f.GroupRoll) BatchChain.AddDrawn(group, dv.Trace);
            // The rows after this one see it: its commands (the engine's expected run in the manual modes) applied to the chain's clone.
            var chained = dv.CanApply ? dv.Trace : m.Checked ? m.LastExpected : null;
            if (i < _rows.Count - 1 && chained is { Result: not null, Error: null })
            {
                if (!cloned) { at = live.DeepClone(); cloned = true; }
                BatchChain.Apply(at, runner, chained);
            }
        }
        return links;
    }

    public BatchRowView Row(int i)
    {
        var links = Chain();
        if (SlotAt(i) is not { } slot || i >= links.Length)
            return new BatchRowView(null, "", Array.Empty<string>(), Array.Empty<bool>(), "", "", "", false, false);
        var n = Variables.Count;
        if (!s.CanAct(slot.PieceId))
        {
            var hidden = Enumerable.Repeat(ModuleSession.HiddenText, n).ToList();
            return new BatchRowView(slot.PieceId, s.CanSee(slot.PieceId) ? Label(slot.PieceId) : ModuleSession.HiddenText, hidden, new bool[n],
                ModuleSession.HiddenText, "", ModuleSession.OtherSideText, false, false);
        }
        var link = links[i];
        if (link.View is not { } dv)
            return new BatchRowView(slot.PieceId, Label(slot.PieceId), Enumerable.Repeat("", n).ToList(), new bool[n], "", "", link.Exceptions ?? "",
                false, false);
        var vars = Variables;
        var cells = vars.Select(v => dv.Boxes.FirstOrDefault(b => b.Variable == v)?.Text ?? "").ToList();
        var wrong = vars.Select(v => dv.Boxes.FirstOrDefault(b => b.Variable == v)?.Wrong == true).ToList();
        return new BatchRowView(slot.PieceId, Label(slot.PieceId), cells, wrong, dv.ResultText, LedgerOf(dv), ExceptionsOf(dv), false,
            dv.CanApply || (HostCalculates && Model(i).Hidden.Count > 0));
    }

    private string Label(string pieceId) =>
        s.Game?.State.Pieces.TryGetValue(pieceId, out var p) == true ? PieceInfo.Name(p) : pieceId;

    /// <summary>What the row would write. A value the viewer may not see (another side's holder) shows only its name.</summary>
    private string LedgerOf(DialogView dv)
    {
        if (dv.Trace.Error != null || dv.Trace.Result == null) return "";
        var sides = FormulaMasking.ViewerSides(s);
        return string.Join("; ", dv.Trace.Outputs.Select(o => FormulaMasking.OutputShown(s, sides, o)
            ? $"{o.PropertyName} {Num(o.Before)} → {Num(o.After)}"
            : $"{o.PropertyName} changes ({ModuleSession.HiddenText})"));
    }

    private static string Num(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    private string ExceptionsOf(DialogView dv)
    {
        var sides = FormulaMasking.ViewerSides(s);
        return dv.Trace.Error == null && dv.Trace.Outputs.Any(o => o.After < 0 && (FormulaMasking.OutputShown(s, sides, o)))
            ? "goes negative" : "";   // only for values the viewer may see
    }

    public IReadOnlyList<(string Key, string PropertyId)> OutputsOf(int row)
    {
        var links = Chain();
        if (row < 0 || row >= links.Length || links[row].View is not { } dv || !s.CanAct(_rows[row].PieceId)) return Array.Empty<(string, string)>();
        return dv.Trace.Error != null || dv.Trace.Result == null ? Array.Empty<(string, string)>()
            : dv.Trace.Outputs.Select(o => (o.Key, o.PropertyId)).ToList();
    }

    /// <summary>The rows not skipped (removed by an earlier row, gone, or another side's).</summary>
    public IReadOnlyList<int> OpenRows()
    {
        var links = Chain();
        return Enumerable.Range(0, links.Length).Where(i => !links[i].Skipped).ToList();
    }

    // ---- choices ----

    private readonly Dictionary<(string Variable, string? Piece), IReadOnlyList<string>?> _choices = new();
    private object? _choicesKey;
    private GameState? _shown;

    /// <summary>
    /// The dropdown of a column for a row (<paramref name="row"/> -1: the All rows line, listed without a run piece): a Piece binding's
    /// candidates as the viewer sees them, a choices table's keys for the row's counter (on the viewer's own view), or an Input's keys.
    /// </summary>
    public IReadOnlyList<string>? ChoicesFor(string variable, int row)
    {
        if (Formula is not { } f || s.Rules is not { } pack || s.Game?.State is not { } st || Binding(variable) is not { } b) return null;
        var piece = SlotAt(row)?.PieceId;
        var sides = FormulaMasking.PickSides(s);
        var key = (st, FormulaDialogModel.StateFingerprint(st), pack, f, sides == null ? "" : string.Join("\n", sides.Order(StringComparer.Ordinal)));
        if (!Equals(_choicesKey, key)) { _choices.Clear(); _choicesKey = key; _shown = null; }
        if (_choices.TryGetValue((variable, piece), out var cached)) return cached;
        IReadOnlyList<string>? list;
        if (b.Kind == BindingKind.Piece)
            list = Quartermaster.Core.PieceChoices.For(st, pack, s.Model, b, sides, piece).Select(c => c.Label).ToList();
        else if (b.Kind == BindingKind.Input && !string.IsNullOrWhiteSpace(b.ChoicesTable))
        {
            // The seat's own view, built once per (state, viewer) for every row and column.
            var shown = sides == null || st.IsPlayerView ? st : _shown ??= PlayerView.For(st, pack, s.Model, sides);
            list = InputChoices.For(shown, pack, s.Model, f, variable, piece);
        }
        else list = KeyChoices.For(pack, f, variable);
        _choices[(variable, piece)] = list;
        return list;
    }

    public bool IsChoiceColumn(string variable) =>
        Binding(variable) is { } b && (b.Kind == BindingKind.Piece || ChoicesFor(variable, -1) is { Count: > 0 });

    // ---- actions ----

    /// <summary>Calculates every row in order on the chain (each row sees the rows before it), rolling and pinning its dice.</summary>
    public void CalculateAll()
    {
        CheckViewer();
        if (HostCalculates || s.Game?.State is not { } live || Formula == null || s.Runner == null) return;
        Build(live, calculate: true);
        _chain = null;   // the calculated boxes change the rows: evaluate them again on the next read
    }

    /// <summary>
    /// All or nothing: when an open row cannot apply in the chain nothing is applied (<see cref="Status"/> names it); otherwise one action
    /// applies every row in order on the live state, with the dice the chain showed. A row that still fails live stops the loop; the rows
    /// applied stay (one undo step). Returns how many rows were applied.
    /// </summary>
    public int ApplyAll()
    {
        Status = null;
        FullyApplied = false;
        if (HostCalculates || s.Game is not { } g || Formula is not { } f || s.Runner == null) return 0;
        _chain = null;
        var links = Chain();
        for (var i = 0; i < links.Length; i++)
        {
            if (links[i] is { Skipped: true } skipped && skipped.Exceptions != ModuleSession.OtherSideText) continue;   // removed: skipped
            if (links[i].View is not { CanApply: true })
            {
                Status = FormulaWindowRules.NothingAppliedRowText(i + 1);
                return 0;
            }
        }
        var applied = 0;
        var failed = -1;
        var group = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        using (g.BeginAction(ActionLabel))
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                var id = _rows[i].PieceId;
                if (!g.State.Pieces.ContainsKey(id)) continue;   // removed by an earlier row
                var m = Model(i);
                m.SharedRolls = f.GroupRoll && group.Count > 0 ? new Dictionary<string, IReadOnlyList<int>>(group, StringComparer.Ordinal) : null;
                var trace = m.Run(g.State);
                if (!m.View(trace).CanApply || !_apply(null, id, trace, m.AutoCalculated)) { failed = i; break; }
                applied++;
                if (f.GroupRoll) BatchChain.AddDrawn(group, trace);
            }
        }
        FullyApplied = failed < 0 && applied > 0;
        Status = failed < 0 ? FormulaWindowRules.AppliedRunsText(applied) : FormulaWindowRules.StoppedText(applied, failed + 1);
        Invalidate();
        return applied;
    }

    /// <summary>
    /// Online guest: sends the whole batch to the host as one intent (every row's typed values, the All rows line merged in) and shows the
    /// host's verdict on each row. Returns how many runs the host applied and its message (null when it applied the batch).
    /// </summary>
    /// <param name="preview">Calculate all: the host only runs the chain (fills the cells, pins the dice); nothing is applied.</param>
    public async Task<(int Applied, string? Message)> SendAsync(bool calculate, bool preview = false)
    {
        FullyApplied = false;
        if (_port == null || !HostCalculates || Formula is not { } f || s.Game == null) return (0, null);   // guest only: the local grid uses ApplyAll
        Chain();   // every row's model synced and evaluated: each pick goes with its current label and piece
        var rows = Enumerable.Range(0, _rows.Count).Where(i => s.CanAct(_rows[i].PieceId)).ToList();
        if (rows.Count == 0) return (0, null);
        var intent = new FormulaBatchIntent(f.Id, rows.Select(i =>
        {
            var one = Model(i).Intent(null, calculate, preview);
            return new FormulaBatchRow(one.PieceId, one.Typed, one.Picks);
        }).ToList(), calculate, preview);
        var v = await _port.ApplyFormulaBatchAsync(intent).ConfigureAwait(true) ?? FormulaBatchVerdict.Refused(GuestSession.NoAnswerText);
        for (var k = 0; k < rows.Count && k < (v.Rows?.Count ?? 0); k++)
            if (v.Rows![k] is { } rv) Model(rows[k]).ApplyVerdict(rv);
        var applied = (v.Rows ?? Array.Empty<FormulaVerdict>()).Count(r => r?.Applied == true);
        Status = preview ? v.Message ?? FormulaWindowRules.HostCalculatedText(rows.Count)
            : v.Applied ? FormulaWindowRules.HostAppliedRunsText(applied)
            : v.Message ?? FormulaMasking.ErrorHiddenText;
        FullyApplied = !preview && v.Applied && applied > 0;
        Invalidate();
        return (applied, v.Applied ? null : v.Message);
    }
}

/// <summary>The preview chain of a batch (B4): a row's run applied to a cloned state as the live apply would, and a group's dice.</summary>
internal static class BatchChain
{
    /// <summary>
    /// Applies a run's commands to a preview state, as <see cref="PhaseController.ApplyFormula"/> would without an action: its rolls, ledger
    /// lines, effects and removals, and the working line that stamps a run with no outputs (so <c>runs()</c> counts it). A placed piece
    /// is not seeded (that needs the session; the live apply does).
    /// </summary>
    public static void Apply(GameState state, FormulaRunner runner, FormulaTrace t)
    {
        foreach (var cmd in runner.Commands(t, null)) state.Apply(cmd);
        if (t.Outputs.Count == 0 && (t.Effects?.Count ?? 0) == 0 && !string.IsNullOrWhiteSpace(t.Working))
            state.Apply(new AddLogLine(LogKind.Calc, t.Working, t.PieceId, FormulaId: t.Formula.Id) { AppliedRun = t.Formula.Id });
    }

    /// <summary>Adds the keys a run rolled itself (not those it took from the group) to the group's dice; per key the first wins.</summary>
    public static void AddDrawn(Dictionary<string, IReadOnlyList<int>> group, FormulaTrace t)
    {
        foreach (var (key, dice) in RollGroups.Drawn(t)) group.TryAdd(key, dice);   // under group keys: matched on sides too
    }
}
