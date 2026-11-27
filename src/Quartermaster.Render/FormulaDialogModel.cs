using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using PieceCandidates = Quartermaster.Core.PieceChoices;

namespace Quartermaster.Render;

public enum BoxBackground { Auto, Typed }

/// <summary>What one input box in the formula dialog should look like. Wrong = checked mode and the typed value differs from the engine's.</summary>
public sealed record BoxView(string Variable, string Text, BoxBackground Background, string CaptionSuffix, bool Enabled,
    string? Tooltip, string? RerollKey, bool Wrong = false, IReadOnlyList<string>? Choices = null, string? Hint = null);

/// <summary>A pinned inline dice() roll, with the key that re-rolls it.</summary>
public sealed record InlineRollView(string Key, string Text);

public sealed record DialogView(IReadOnlyList<BoxView> Boxes, IReadOnlyList<InlineRollView> InlineRolls, string ResultText,
    string WorkingText, bool WorkingNegative, string Status, bool CanApply, FormulaTrace Trace)
{
    /// <summary>
    /// B7: the run the view was built from when it is not <see cref="Trace"/> (null otherwise). For a masked viewer of a formula whose
    /// precondition or "Only when" reads hidden data, the view shows a run that ignores the precondition and uses every host guard
    /// (<see cref="FormulaDialogModel"/>), so nothing shown depends on the hidden data; <see cref="Trace"/> stays the real run, the only one
    /// ever applied.
    /// </summary>
    public FormulaTrace? Shown { get; init; }

    /// <summary>The run whose state the view shows (<see cref="Shown"/>, else <see cref="Trace"/>): read it for anything displayed, never apply it.</summary>
    public FormulaTrace ShownTrace => Shown ?? Trace;

    /// <summary>B7: the viewer lacks data the formula reads (its hidden inputs are not empty): before Apply nothing of the run's values, result,
    /// "(negative)" mark or dice is shown or sent (<see cref="FormulaMasking.LedgerPreview"/> and the related rules read this).</summary>
    public bool Masked { get; init; }

    /// <summary>
    /// The run's writes as the viewer is shown them before Apply (<see cref="FormulaMasking.OutputLines"/> of <see cref="ShownTrace"/>):
    /// <c>{Prop} {before} → {after}</c> for a visible write of an unmasked run, <c>{Prop} changes (hidden)</c> otherwise, and (B9 #55) one
    /// line <c>{Prop} of the drawn counters changes</c> for the outputs on a draw's counters. Empty while no result is shown.
    /// </summary>
    public IReadOnlyList<string> OutputLines { get; init; } = Array.Empty<string>();
}

/// <summary>UI-free state of the formula dialog: typed values, overrides, pinned dice, and the mapping from a run to what the controls show.</summary>
public sealed class FormulaDialogModel(FormulaRunner initialRunner, FormulaDef initialFormula, string? pieceId)
{
    private FormulaRunner runner = initialRunner;
    private FormulaDef formula = initialFormula;

    private readonly Dictionary<string, string> _typed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyList<int>> _fixedRolls = new(StringComparer.Ordinal);
    private bool _checked;
    private FormulaTrace? _lastExpected;

    public FormulaDef Formula => formula;

    /// <summary>
    /// True for a run the player starts (menu, not a waiting run): the formula's phase limit then applies and Run / Calculate refuse
    /// outside its phases. Hook runs and waiting runs leave it off.
    /// </summary>
    public bool EnforcePhaseLimit { get; set; }

    private string? _phaseReason;

    private string? PhaseReasonFor(GameState state)
    {
        _phaseReason = null;
        if (EnforcePhaseLimit && !FormulaPhases.Allowed(runner.Pack, state, formula, out var why)) _phaseReason = why;
        return _phaseReason;
    }

    private static readonly IReadOnlySet<string> NoneHidden = new HashSet<string>();
    private IReadOnlySet<string> _hidden = NoneHidden;
    private readonly Dictionary<string, string> _verdict = new(StringComparer.Ordinal);
    private string? _verdictMessage;

    /// <summary>
    /// Inputs whose value comes from data the viewer may not see (<see cref="FormulaMasking"/>; empty in solo). Their boxes show
    /// <see cref="HiddenBoxText"/> and cannot be typed into; no text of the view (boxes, tooltips, working, status) carries their value.
    /// The run still reads them (the host is this PC); in the manual modes the engine's value stands in for the box.
    /// </summary>
    public IReadOnlySet<string> Hidden
    {
        get => _hidden;
        set
        {
            _hidden = value == null || value.Count == 0 ? NoneHidden : new HashSet<string>(value, StringComparer.Ordinal);
            foreach (var v in _hidden) { if (!IsPick(v)) _typed.Remove(v); _overrides.Remove(v); }   // a pick is the viewer's own: kept
        }
    }

    /// <summary>
    /// The sides a Piece binding's candidates are listed and resolved for (<see cref="RunRequest.PickSides"/>), set by
    /// <see cref="FormulaMasking.Configure"/>: the viewer's sides when the dialog runs on the full state for a viewer (hot-seat, the online
    /// host's screen, the host running a guest's intent); null in solo and on a guest, whose state is already its own view.
    /// </summary>
    public IReadOnlySet<string>? PickSides
    {
        get => _pickSides;
        set
        {
            var same = value == null ? _pickSides == null : _pickSides != null && _pickSides.SetEquals(value);
            _pickSides = value;
            if (same) return;
            // Another viewer: a pick is a label from the previous viewer's own list, never carried into this one's box.
            foreach (var b in formula.Bindings)
                if (b is { Variable: not null } && IsPickBinding(b)) { _typed.Remove(b.Variable); _pickedId.Remove(b.Variable); }
        }
    }

    private IReadOnlySet<string>? _pickSides;

    /// <summary>
    /// The piece each Piece binding's typed label was picked as (from the viewer's candidate list at the time). A label is only a position
    /// among same-named candidates (" #2"), so when the list changes the box is rewritten to that piece's current label, or cleared when the
    /// piece is no longer a candidate: a pick never silently moves to another piece.
    /// </summary>
    private readonly Dictionary<string, string> _pickedId = new(StringComparer.Ordinal);

    /// <summary>The state of the last run: a Piece binding's candidates are listed from it.</summary>
    private GameState? _state;

    /// <summary>True on an online guest's screen: hidden boxes say the host calculates them, and the host's verdict is shown.</summary>
    public bool HostCalculates { get; set; }

    /// <summary>B7: the side answering the pending run this dialog is for (its <see cref="PendingRun.AskSideId"/>), passed to every run as
    /// <see cref="RunRequest.AskSideId"/> so <c>askside()</c> names it. Null for a menu run or a run no side is asked.</summary>
    public string? AskSideId { get; set; }

    /// <summary>The text of a hidden box.</summary>
    public string HiddenBoxText => HostCalculates ? FormulaMasking.HostCalculatesText : FormulaMasking.HiddenText;

    private bool IsHidden(string v) => _hidden.Contains(v);

    private FormulaDef? _guardsFor;
    private IReadOnlySet<string>? _guardsHidden;
    private IReadOnlySet<string> _guards = NoHostGuards;

    /// <summary>
    /// B7: the bindings whose "Only when" (and the precondition, <see cref="FormulaMasking.PreconditionGuard"/>) depends on data hidden from
    /// the viewer (<see cref="FormulaMasking.GuardsOnHidden"/>), for every masked viewer (hot-seat, the host's screen for a side, the host
    /// running a guest's intent, a guest). Such a typed box is a host guard: always open and always to be filled, so nothing the viewer sees
    /// (the box, its tooltip, the status, Apply, a preview's values) depends on whether the guard holds; the engine decides whether the
    /// typed value is used. Cached per formula and hidden set.
    /// </summary>
    private IReadOnlySet<string> Guards()
    {
        if (_hidden.Count == 0) return NoHostGuards;
        if (!ReferenceEquals(_guardsFor, formula) || !ReferenceEquals(_guardsHidden, _hidden))
        {
            _guardsFor = formula;
            _guardsHidden = _hidden;
            _guards = FormulaMasking.GuardsOnHidden(formula, _hidden);
        }
        return _guards;
    }

    /// <summary>A typed box (Input or Piece) of a host guard (<see cref="Guards"/>) that is not itself hidden.</summary>
    private bool IsGuardBox(string variable) =>
        !IsHidden(variable) && Guards().Contains(variable) && Binding(variable) is { } b && IsTyped(b);

    /// <summary>The hidden Input variables with an auto value: the viewer cannot type them, so every run reads their auto value.</summary>
    private IReadOnlySet<string>? HiddenAutoInputs()
    {
        if (_hidden.Count == 0) return null;
        HashSet<string>? set = null;
        foreach (var b in formula.Bindings)
            if (b is { Kind: BindingKind.Input, Variable: { } v } && IsHidden(v) && !string.IsNullOrWhiteSpace(b.AutoValue))
                (set ??= new HashSet<string>(StringComparer.Ordinal)).Add(v);
        return set;
    }

    /// <summary>Manual modes: the engine's values are hidden; the user types them and each box is compared with the engine.</summary>
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            if (!value)
                foreach (var b in formula.Bindings.Where(b => IsCheckedKind(b.Kind) && string.IsNullOrWhiteSpace(_overrides.GetValueOrDefault(b.Variable))))
                    _overrides.Remove(b.Variable);
        }
    }

    /// <summary>
    /// A roll group's dice already drawn (key → dice; B4), passed into every run as <see cref="RunRequest.SharedRolls"/>: a shared key's box
    /// shows the dice, is not editable and cannot be re-rolled. Null = none (the run rolls its own).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>>? SharedRolls { get; set; }

    /// <summary>Distance moved, for Moved variables (null = unknown).</summary>
    public double? Moved { get; set; }

    /// <summary>True once Calculate has filled the boxes. The boxes stay editable.</summary>
    public bool AutoCalculated { get; private set; }

    /// <summary>The expected (engine-only) run behind the last checked evaluation.</summary>
    public FormulaTrace? LastExpected => _lastExpected;

    private static bool IsCheckedKind(BindingKind k) =>
        k is BindingKind.Unit or BindingKind.Global or BindingKind.Holder or BindingKind.Table or BindingKind.Location or BindingKind.Moved;

    private bool IsCheckedBox(string v) => Checked && Binding(v) is { } b && IsChecked(b);

    /// <summary>A box the manual modes check against the engine: never a holder picker, whose box is the player's pick (B9 #13b).</summary>
    private static bool IsChecked(VariableBinding b) => IsCheckedKind(b.Kind) && !HolderChoices.IsPicker(b);

    /// <summary>The rules pack changed: keep what the user typed but run the new runner and formula.</summary>
    public void Rebind(FormulaRunner newRunner, FormulaDef newFormula)
    {
        runner = newRunner;
        formula = newFormula;
    }
    public string? PieceId
    {
        get => pieceId;
        set => pieceId = value;
    }

    /// <summary>When on, Roll variables become typed boxes (the player rolled real dice).</summary>
    public bool PhysicalDice
    {
        get => _physical;
        set
        {
            if (_physical == value) return;
            _physical = value;
            foreach (var b in formula.Bindings.Where(b => b.Kind == BindingKind.Roll)) { _overrides.Remove(b.Variable); _fixedRolls.Remove(b.Variable); }
        }
    }

    private bool _physical;

    private VariableBinding? Binding(string v) => formula.Bindings.FirstOrDefault(b => b.Variable == v);

    /// <summary>A Roll the player types with physical dice. Never a hidden Roll (B7): the engine rolls that one.</summary>
    private bool IsPhysicalRoll(string v) => PhysicalDice && Binding(v)?.Kind == BindingKind.Roll && !IsHidden(v);

    /// <summary>A Piece binding or a holder picker (B9 #13b): its box holds the picked candidate's label (typed like an Input), never the
    /// value read from it.</summary>
    private bool IsPick(string v) => Binding(v) is { } b && IsPickBinding(b);

    private static bool IsPickBinding(VariableBinding b) => b.Kind == BindingKind.Piece || HolderChoices.IsPicker(b);

    /// <summary>Bindings whose box text is what the player typed or picked (sent to the host, never an override): Input, Piece, holder picker.</summary>
    private static bool IsTyped(VariableBinding b) => b.Kind == BindingKind.Input || IsPickBinding(b);

    /// <summary>
    /// The reasons a Piece box may show even when its value is hidden: they come from the viewer's own candidate list, never from the
    /// picked piece's value.
    /// </summary>
    private static bool IsPickReason(string? reason) =>
        reason is "Waiting for input" or PieceCandidates.Unknown or PieceCandidates.NoPiece or HolderChoices.Unknown or HolderChoices.None
        || (reason?.StartsWith("No piece matches \"", StringComparison.Ordinal) ?? false);

    /// <summary>The user changed a box. Typed boxes keep their text; auto boxes add or (when blank) drop an override.</summary>
    public void SetBoxText(string variable, string text)
    {
        var b = Binding(variable);
        if (b == null || (IsHidden(variable) && !IsPickBinding(b))) return;   // a hidden pick is still the viewer's to make
        if (b.Kind == BindingKind.Draw) return;   // B9 #55: the engine draws (also with physical dice); its box is never typed or overridden
        _verdict.Remove(variable);
        _verdictMessage = null;
        if (IsPickBinding(b)) { _typed[variable] = text; RecordPick(variable, text); return; }
        if (IsTyped(b)) { _typed[variable] = text; return; }
        if (IsPhysicalRoll(variable) || IsCheckedBox(variable)) { _overrides[variable] = text; return; }
        if (string.IsNullOrWhiteSpace(text)) _overrides.Remove(variable);
        else _overrides[variable] = text;
    }

    /// <summary>Drops the pinned roll for a key so the next run rolls it again.</summary>
    public void Reroll(string key)
    {
        if (HostCalculates) return;   // a guest's dice are the host's: no re-rolling
        if (IsShared(key)) return;    // a group's dice are rolled once for the group
        if (FormulaMasking.IsDrawKey(key)) return;   // B9 #55: a draw is drawn once; asking again never draws other counters
        _fixedRolls.Remove(key);
    }

    /// <summary>True when the group's dice already hold this key (<see cref="SharedRolls"/>).</summary>
    private bool IsShared(string key) => SharedRolls is { } shared
        && (shared.ContainsKey(key) || shared.Keys.Any(k => k.StartsWith(key + "@d", StringComparison.Ordinal)));   // inline keys carry sides

    /// <summary>Caption suffix of a Roll box whose dice are the group's (B4).</summary>
    public const string GroupRollSuffix = " (group roll)";

    /// <summary>Tooltip of a Roll box whose dice are the group's (B4).</summary>
    public const string GroupRollTooltip = "Rolled once for the group";

    /// <summary>The pinned dice by roll key (what the next run reuses).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> PinnedRolls => _fixedRolls;

    /// <summary>Pins these dice (the host reusing a preview's rolls for the same sender).</summary>
    public void PinRolls(IReadOnlyDictionary<string, IReadOnlyList<int>> rolls)
    {
        if (rolls == null) return;
        var merged = new Dictionary<string, IReadOnlyList<int>>(_fixedRolls, StringComparer.Ordinal);
        foreach (var kv in rolls) if (kv.Key != null && kv.Value != null) merged[kv.Key] = kv.Value.ToList();
        _fixedRolls = merged;
    }

    /// <summary>Rolls dice the pins don't cover (key, count, sides); null = the runner's roller. Set by the online host for a guest's run.</summary>
    public Func<string, int, int, IReadOnlyList<int>>? DiceSource { get; set; }

    /// <summary>True when the run's dice for this key are the host's (same key and same dice; another count means the guest changed an input).</summary>
    private bool HostRolled(string key, FormulaTrace t) =>
        _hostDice.TryGetValue(key, out var host) && t.RollsByKey.TryGetValue(key, out var used) && host.SequenceEqual(used);

    /// <summary>Roll keys whose dice came from the host (guest). Until then a guest's dice boxes say the host rolls.</summary>
    private readonly Dictionary<string, IReadOnlyList<int>> _hostDice = new(StringComparer.Ordinal);

    /// <summary>A guest's dice box before the host rolled it.</summary>
    public const string HostRollsText = "rolled by the host";

    /// <summary>B9 #55: the tooltip of a Draw box.</summary>
    public const string DrawTooltip = "Drawn at random when you apply; the log names the counters.";

    /// <summary>Shown after the caption of an input that always asks (<see cref="VariableBinding.AlwaysAsk"/>).</summary>
    public const string AlwaysAskedSuffix = " (always asked)";

    /// <summary>The box of a binding whose "Only when" condition is false.</summary>
    public const string NotUsedText = "not used";

    /// <summary>A binding that is neither filled, missing nor skipped: its "Only when" condition (or the precondition) is not decided yet.</summary>
    private static bool IsUndecided(VariableState v) => v.Waiting;

    /// <summary>
    /// Which ledger outputs the viewer may see (set by <see cref="FormulaMasking.Configure"/>); null = all (solo). Only those decide the
    /// "(negative)" mark: another side's value going negative is not shown.
    /// </summary>
    public Func<OutputPreview, bool>? OutputVisible { get; set; }

    private Dictionary<string, string> PhysicalRollOverrides()
    {
        var ov = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!PhysicalDice) return ov;
        // B7: a hidden Roll (its count or "Only when" reads hidden data) cannot be typed: the engine rolls and pins it.
        foreach (var b in formula.Bindings.Where(b => b.Kind == BindingKind.Roll && !IsShared(b.Variable) && !IsHidden(b.Variable)))
            ov[b.Variable] = _overrides.GetValueOrDefault(b.Variable) ?? "";
        return ov;
    }

    /// <summary>Pins survive runs that did not produce them (missing input, errors). Only Reroll / the physical-dice toggle drop pins.</summary>
    private void MergePins(FormulaTrace trace)
    {
        var merged = new Dictionary<string, IReadOnlyList<int>>(_fixedRolls, StringComparer.Ordinal);
        foreach (var kv in trace.RollsByKey)
        {
            // A guest's local roll (e.g. after an input changed the dice count) never replaces the host's dice for that key.
            if (HostCalculates && _hostDice.ContainsKey(kv.Key)) continue;
            merged[kv.Key] = kv.Value;
        }
        // B9 #55: the draw dice (draw:{Var}#{i}) are pinned like any roll, so a preview and its Apply draw the same counters.
        foreach (var kv in trace.DrawRolls) merged[kv.Key] = kv.Value;
        _fixedRolls = merged;
    }

    public FormulaTrace Run(GameState state)
    {
        _state = state;
        SyncPicks();
        if (PhaseReasonFor(state) != null)
        {
            // (The runner call only gives the view its variable list; its dice are not pinned and nothing is applied.)
            // Outside the formula's phases nothing is run: the trace carries no result, so nothing can be applied.
            var idle = runner.Run(state, new RunRequest(formula.Id, pieceId, new Dictionary<string, string>(_typed), new Dictionary<string, string>(StringComparer.Ordinal), _fixedRolls, Moved, false, DiceSource, PickSides) { SharedRolls = SharedRolls, AutoInputs = HiddenAutoInputs(), AskSideId = AskSideId });
            return idle with { Result = null, Working = "", Outputs = Array.Empty<OutputPreview>() };
        }
        var typed = new Dictionary<string, string>(_typed);
        // Physical roll boxes always carry an override (blank = still waiting), so the runner never rolls for them.
        var ov = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _overrides)
        {
            if (IsHidden(kv.Key)) continue;   // a hidden input is never overridden: the engine reads it
            if (IsShared(kv.Key) && Binding(kv.Key)?.Kind == BindingKind.Roll) continue;   // the group's dice stand
            if (!Checked || Binding(kv.Key)?.Kind == BindingKind.Roll) ov[kv.Key] = kv.Value; // checked: typed text only gates, never feeds the run
        }
        foreach (var b in formula.Bindings.Where(b => b.Kind == BindingKind.Roll && PhysicalDice && !IsShared(b.Variable) && !IsHidden(b.Variable)))
            ov.TryAdd(b.Variable, "");
        // A hidden Input cannot be typed: the engine reads its auto value (B7), as it reads every other hidden value.
        var request = new RunRequest(formula.Id, pieceId, typed, ov, _fixedRolls, Moved, false, DiceSource, PickSides)
            { SharedRolls = SharedRolls, AutoInputs = HiddenAutoInputs(), AskSideId = AskSideId };
        var trace = runner.Run(state, request);
        MergePins(trace);
        // B7: a precondition or guard over hidden data is the host's to decide at Apply; the viewer is shown a run that does not depend on it.
        _shownFor = null;
        _shownTrace = null;
        if (ShownRequest(request) is { } shownRequest)
        {
            _shownTrace = runner.Run(state, shownRequest with { FixedRolls = _fixedRolls });
            MergePins(_shownTrace);
            _shownFor = trace;
        }
        if (!Checked) return trace;
        // Checked: this is the EXPECTED engine run, the only source of ledger commands. Until every box matches it carries no result.
        // The boxes are checked against what the viewer is shown.
        _lastExpected = _shownTrace ?? trace;
        if (View(trace).CanApply) return trace;
        var stripped = trace with { Result = null, Working = "", Outputs = Array.Empty<OutputPreview>() };
        if (_shownFor != null) _shownFor = stripped;
        return stripped;
    }

    private FormulaTrace? _shownFor;
    private FormulaTrace? _shownTrace;

    /// <summary>
    /// B7: the run a masked viewer (not an online guest, whose own run is guesses the host replaces) is shown instead of the real one when
    /// the formula's precondition or a typed box's "Only when" reads data hidden from it (<see cref="Guards"/>): it ignores the precondition
    /// (<see cref="RunRequest.IgnorePrecondition"/>) and resolves every guard box as if its guard held (<see cref="RunRequest.ForceWhen"/>),
    /// so the boxes, their validation, the status and Apply never depend on the hidden data. Null when no such run is needed.
    /// </summary>
    private RunRequest? ShownRequest(RunRequest real)
    {
        if (HostCalculates || _hidden.Count == 0) return null;
        var guards = Guards();
        if (guards.Count == 0) return null;
        HashSet<string>? force = null;
        foreach (var b in formula.Bindings)
            if (b?.Variable is { } v && IsGuardBox(v)) (force ??= new HashSet<string>(StringComparer.Ordinal)).Add(v);
        var pre = guards.Contains(FormulaMasking.PreconditionGuard);
        return force == null && !pre ? null : real with { IgnorePrecondition = pre, ForceWhen = force };
    }

    /// <summary>
    /// B7: the status to show instead of applying when the view allowed Apply but the real run cannot be applied: a precondition or
    /// guard over hidden data decided against it. The generic <see cref="FormulaMasking.ErrorHiddenText"/>, which never says which
    /// condition failed. Null when the run can be applied (or the view did not allow it).
    /// </summary>
    public static string? ApplyRefusal(DialogView v) =>
        v is { CanApply: true } && (v.Trace.Refused || v.Trace.Error != null || v.Trace.Result == null) ? FormulaMasking.ErrorHiddenText : null;

    /// <summary>Runs with the engine's auto values (rolling and pinning dice) and copies every value into the boxes.</summary>
    public void Calculate(GameState state)
    {
        _state = state;
        SyncPicks();
        if (PhaseReasonFor(state) != null) return;
        var calc = new RunRequest(formula.Id, pieceId, new Dictionary<string, string>(_typed),
            PhysicalRollOverrides(), _fixedRolls, Moved, true, DiceSource, PickSides) { SharedRolls = SharedRolls, AskSideId = AskSideId };
        // B7: a precondition or guard over hidden data must not decide which boxes are filled: the boxes come from the shown run
        // (ShownRequest). Nothing is applied from this run.
        var trace = runner.Run(state, ShownRequest(calc) ?? calc);
        MergePins(trace);
        foreach (var v in trace.Variables)
        {
            if (v.Value is not { } val || IsHidden(v.Variable)) continue;   // a hidden value is never copied into a box
            if (IsGuardBox(v.Variable)) continue;   // filled or not as the hidden guard holds: the viewer types it either way
            var b = Binding(v.Variable);
            if (b == null) continue;
            if (b.Kind == BindingKind.Input) _typed[v.Variable] = Exact(val);
            else if (Checked && IsChecked(b)) _overrides[v.Variable] = Exact(val);   // a pick keeps its label: never its value
        }
        AutoCalculated = true;
    }

    public DialogView Evaluate(GameState state) => View(Run(state));

    public DialogView View(FormulaTrace t)
    {
        var real = t;
        if (_shownTrace != null && ReferenceEquals(t, _shownFor)) t = _shownTrace;   // B7: what the viewer is shown (ShownRequest)
        var vars = t.Variables;
        // A guard over data the viewer may not see is a host guard (B7, every masked viewer): its box stays open and must be filled
        // whether the guard holds or not. An online guest cannot decide it at all: the host decides (spec 8a.2).
        var guards = Guards();
        var hostGuards = HostCalculates ? guards : NoHostGuards;
        var picks = PieceLabels();
        var boxes = vars.Select(v => Box(v, t, guards, picks)).ToList();
        // B7: a masked formula's dice are never shown before Apply (their count or whether they are rolled may follow the hidden data).
        var inline = t.RollsByKey.Where(kv => _hidden.Count == 0 && kv.Key.StartsWith("dice#", StringComparison.Ordinal) && (!HostCalculates || HostRolled(kv.Key, t)))
            .Select(kv => new InlineRollView(kv.Key, DiceText(kv.Value))).ToList();
        // A guest's local run rolls its own dice; until the host's are in, no result built on them is shown.
        var localDice = HostCalculates && t.RollsByKey.Keys.Any(k => !HostRolled(k, t));
        var masked = _hidden.Count > 0;
        // A hidden pick still waits for the viewer's own choice (the reason comes from the viewer's list, never from the value). On a guest, a
        // pick whose target its view cannot read ("Hidden from this player": a masked piece, another side's private value a Piece output
        // writes) is made: the host decides it.
        // A guard box is missing exactly when it is blank, whatever the guard says (B7).
        var missing = vars.Where(v => IsGuardBox(v.Variable)
                ? Raw(v.Variable).Trim().Length == 0
                : v.MissingReason != null && (!IsHidden(v.Variable) || (IsPick(v.Variable) && IsPickReason(v.MissingReason)))
                    && !(HostCalculates && IsPick(v.Variable) && v.MissingReason == TargetHiddenText))
            .Select(v => v.Variable).ToList();
        var wrong = boxes.Where(b => b.Wrong).Select(b => b.Variable).ToList();
        var canApply = t.Result != null && t.Error == null && _phaseReason == null
            && !vars.Any(v => IsGuardBox(v.Variable) && Raw(v.Variable).Trim().Length == 0);   // a blank guard box waits even when unused (B7)
        if (Checked)
        {
            var empty = boxes.Where(b => IsCheckedBox(b.Variable) && !IsHidden(b.Variable) && b.Text.Trim().Length == 0).Select(b => b.Variable).ToList();
            missing = vars.Select(v => v.Variable).Where(n => missing.Contains(n) || empty.Contains(n)).ToList();
            // A wrong formula must never reach the ledger: every manual box needs an engine value to be checked against.
            var verifiable = _lastExpected is { Error: null } exp
                && vars.All(v => !IsCheckedBox(v.Variable) || v.Skipped || exp.Variables.FirstOrDefault(e => e.Variable == v.Variable)?.Value != null);
            canApply = canApply && wrong.Count == 0 && missing.Count == 0 && verifiable;
        }
        // The local run decided (or could not decide) a guard over hidden data on this guest's defaults: its refusal, error, waiting
        // state and result are guesses, so none is shown; the host decides.
        var hostDecides = (hostGuards.Contains(FormulaMasking.PreconditionGuard) && (t.Refused || vars.Any(IsUndecided)))
            || vars.Any(v => (v.Skipped || IsUndecided(v)) && hostGuards.Contains(v.Variable));
        if (hostDecides) canApply = false;
        // B9 #55: a drawn counter going negative is never marked (it would tell which counters were drawn).
        var negative = !hostDecides && t.Error == null && t.Outputs.Any(o => o.Draw == null && o.After < 0 && (OutputVisible == null || OutputVisible(o)));
        var status = wrong.Count > 0 ? "Doesn't match: " + string.Join(", ", wrong)
            : hostDecides ? (missing.Count > 0 ? "Waiting for: " + string.Join(", ", missing) : HostWaitText)
            : t.Error != null ? (masked ? FormulaMasking.ErrorHiddenText : t.Error)
            : missing.Count > 0 ? "Waiting for: " + string.Join(", ", missing)
            : masked && t.Result == null ? (HostCalculates ? HostWaitText : HiddenWaitText)
            : t.Result == null && vars.Any(IsUndecided) ? "Waiting for the other inputs"
            : "All inputs filled";
        if (_phaseReason != null) status = _phaseReason;
        else if (_verdictMessage != null) status = _verdictMessage;
        // The working line shows every input's value: with a hidden input it is replaced (the logged working is redacted by PlayerView).
        var working = masked ? (t.Result != null ? FormulaMasking.WorkingHiddenText : "") : t.Working;
        var result = t.Result?.Display() ?? "";
        if (localDice || hostDecides) { working = ""; result = ""; negative = false; }
        // B7: a blank guard box waits whether or not its hidden guard holds, so nothing of a run that went ahead without it is shown.
        if (vars.Any(v => IsGuardBox(v.Variable) && Raw(v.Variable).Trim().Length == 0)) { working = ""; result = ""; negative = false; }
        // B7: a masked formula never previews its result or its "(negative)" mark before Apply: either could be the hidden value, or change
        // with it as the viewer retypes a value compared with it. After Apply the ledger shows what the viewer may see.
        var outputs = localDice || hostDecides || (result.Length == 0 && !masked) ? Array.Empty<string>()
            : FormulaMasking.OutputLines(t, masked, o => OutputVisible == null || OutputVisible(o));
        if (masked) { result = HiddenBoxText; negative = false; }
        return new DialogView(boxes, inline, result, working + (negative ? " (negative)" : ""), negative, status,
            canApply, real) { Shown = ReferenceEquals(t, real) ? null : t, Masked = masked, OutputLines = outputs };
    }

    private static readonly IReadOnlySet<string> NoHostGuards = new HashSet<string>();

    /// <summary>Shown on a typed dice box that may hold several dice (what count() reads).</summary>
    public const string TypeEachDieHint = "Type each die, separated by spaces";

    /// <summary>
    /// The candidate labels of each Piece binding on the last run's state, as the viewer sees them (<see cref="PickSides"/>). Cached while the
    /// state's pieces, ledger history, log, the formula and the sides are unchanged, so typing in a box does not rebuild a player view.
    /// </summary>
    private Dictionary<string, IReadOnlyList<string>> PieceLabels()
    {
        PieceLists();
        return _labels ?? new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The candidates (label and piece) of each Piece binding on the last run's state for <see cref="PickSides"/>, and (B9 #13b) of each
    /// holder picker (label and holder id, <see cref="HolderChoices"/>: the listed holders the viewer may act on now; on a player view its
    /// own sides); cached as the labels. A holder's owner follows the ledger, which the cache key covers.
    /// </summary>
    private Dictionary<string, IReadOnlyList<PieceChoice>> PieceLists()
    {
        if (_state == null || !formula.Bindings.Any(b => b != null && IsPickBinding(b)))
        {
            _labels = null;
            return new(StringComparer.Ordinal);
        }
        var key = LabelsKey(_state);
        if (_choices != null && _labelsKey == key) return _choices;
        var choices = new Dictionary<string, IReadOnlyList<PieceChoice>>(StringComparer.Ordinal);
        foreach (var b in formula.Bindings)
            if (b is { Kind: BindingKind.Piece, Variable: not null })
                choices[b.Variable] = PieceCandidates.For(_state, runner.Pack, runner.Model, b, PickSides, pieceId);   // the run piece's hex first
            else if (b is { Variable: not null } && HolderChoices.IsPicker(b))
                choices[b.Variable] = HolderChoices.For(_state, runner.Pack, b, PickSides).Select(c => new PieceChoice(c.Label, c.HolderId)).ToList();
        _labelsKey = key;
        _labels = choices.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Select(c => c.Label).ToList(), StringComparer.Ordinal);
        return _choices = choices;
    }

    /// <summary>
    /// The state a choices table's filter is listed on (B4): the viewer's own view (<see cref="PlayerView.For"/> for <see cref="PickSides"/>)
    /// when the dialog runs on the full state for a seat, as Piece candidates are, so the list never reflects data that seat cannot see (a
    /// filter reading hidden data keeps the row; the run decides). Cached while the state and the sides are unchanged.
    /// </summary>
    private GameState ChoicesState()
    {
        var st = _state ?? new GameState();
        if (PickSides is not { } sides || st.IsPlayerView) return st;
        var key = (st, StateFingerprint(st), runner.Pack, sides);
        if (_choicesView != null && Equals(_choicesViewKey, key)) return _choicesView;
        _choicesViewKey = key;
        return _choicesView = PlayerView.For(st, runner.Pack, runner.Model, sides);
    }

    private GameState? _choicesView;
    private object? _choicesViewKey;

    private Dictionary<string, IReadOnlyList<string>>? _labels;
    private Dictionary<string, IReadOnlyList<PieceChoice>>? _choices;
    private (GameState, FormulaDef, FormulaRunner, IReadOnlySet<string>?, string?, int, int, int, int)? _labelsKey;

    /// <summary>What the candidate lists depend on: the state object, its pieces (place, type, trait states), the ledger and log lengths.</summary>
    private (GameState, FormulaDef, FormulaRunner, IReadOnlySet<string>?, string?, int, int, int, int) LabelsKey(GameState st)
    {
        var (h, count, ledger, log) = StateFingerprint(st);
        return (st, formula, runner, PickSides, pieceId, h, count, ledger, log);   // the run piece orders (and "same hex" limits) the lists
    }

    /// <summary>
    /// What a Piece binding's candidate list depends on in a state: its pieces (place, type, trait states) and the ledger and log lengths
    /// (a filter may read either). Compared together with the state object and the viewer's sides.
    /// </summary>
    internal static (int Hash, int Pieces, int Ledger, int Log) StateFingerprint(GameState st)
    {
        var h = new HashCode();
        foreach (var p in st.Pieces.Values)
        {
            if (p == null) continue;
            h.Add(p.Id); h.Add(p.MapId); h.Add(p.Pos); h.Add(p.TypeString);
            foreach (var t in p.TraitStates ?? new List<string>()) h.Add(t);
        }
        return (h.ToHashCode(), st.Pieces.Count, st.LedgerHistory.Count, st.Log.Count);
    }

    /// <summary>The user picked (typed) a Piece label: remember which candidate of the current list it is (none: not a listed label).</summary>
    private void RecordPick(string variable, string? text)
    {
        _pickedId.Remove(variable);
        if (_state == null || !PieceLists().TryGetValue(variable, out var list)) return;   // adopted by the next run (SyncPicks)
        var typed = (text ?? "").Trim();
        if (typed.Length == 0) return;
        var hit = list.FirstOrDefault(c => string.Equals(c.Label, typed, StringComparison.OrdinalIgnoreCase));
        if (hit != null) _pickedId[variable] = hit.PieceId;
    }

    /// <summary>
    /// Before a run: each picked piece's box follows that piece in the (possibly changed) candidate list, its label rewritten when it
    /// changed and the pick cleared (back to "Waiting for input") when the piece is no longer a candidate. A label typed before any list
    /// existed is matched against this list first. Text that is no listed label is left as typed (the run says to choose a listed piece).
    /// </summary>
    private void SyncPicks()
    {
        foreach (var (variable, list) in PieceLists())
        {
            if (_pickedId.TryGetValue(variable, out var id))
            {
                var now = list.FirstOrDefault(c => c.PieceId == id);
                if (now == null) { _pickedId.Remove(variable); _typed.Remove(variable); continue; }
                if (!string.Equals(_typed.GetValueOrDefault(variable)?.Trim(), now.Label, StringComparison.OrdinalIgnoreCase))
                    _typed[variable] = now.Label;
            }
            else if (_typed.TryGetValue(variable, out var text)) RecordPick(variable, text);
        }
    }

    private BoxView Box(VariableState v, FormulaTrace t, IReadOnlySet<string> hostGuards, IReadOnlyDictionary<string, IReadOnlyList<string>> picks)
    {
        var binding = Binding(v.Variable);
        // A hidden pick: the box shows the viewer's own pick and list; the value read from the piece stays hidden (only list reasons shown).
        if (IsHidden(v.Variable) && binding != null && IsPickBinding(binding))
            return new BoxView(v.Variable, Raw(v.Variable), BoxBackground.Typed, "", true,
                Raw(v.Variable).Trim().Length == 0 ? "Waiting for input" : IsPickReason(v.MissingReason) ? v.MissingReason : HiddenBoxText, null,
                false, picks.GetValueOrDefault(v.Variable) ?? Array.Empty<string>());
        // B9 #55: a Draw box is read-only: k (or hidden when its pool is masked), never a re-roll, the counters drawn never named.
        if (binding?.Kind == BindingKind.Draw && IsHidden(v.Variable))
            return new BoxView(v.Variable, HiddenBoxText, BoxBackground.Auto, "", false, DrawTooltip, null);
        if (IsHidden(v.Variable)) return new BoxView(v.Variable, HiddenBoxText, BoxBackground.Auto, "", false, null, null);
        // B7: the dice of a masked formula are not shown before Apply (dice the viewer types itself are its own).
        if (binding?.Kind == BindingKind.Roll && _hidden.Count > 0 && !IsPhysicalRoll(v.Variable))
            return new BoxView(v.Variable, HiddenBoxText, BoxBackground.Auto, "", false, null, null);
        // A guarded variable is not asked: "not used" once its condition is false, blank and closed while the condition is undecided.
        // On an online guest a guard over hidden data is the host's to decide: the box stays as it would be without the guard.
        var hostGuard = hostGuards.Contains(v.Variable);
        if (v.Skipped && !hostGuard) return new BoxView(v.Variable, NotUsedText, BoxBackground.Auto, "", false, null, null);
        if (IsUndecided(v) && !hostGuard && !IsCheckedBox(v.Variable)) return new BoxView(v.Variable, "", BoxBackground.Auto, "", false, null, null);
        if (binding?.Kind == BindingKind.Draw)
            return new BoxView(v.Variable, v.Value?.Display() ?? "", BoxBackground.Auto, "", false, v.MissingReason ?? DrawTooltip, null);
        if (IsCheckedBox(v.Variable)) return CheckedBox(v);
        // A group's dice (B4): shown, never typed, overridden or re-rolled. A guest shows them once they are the host's.
        if (binding?.Kind == BindingKind.Roll && t.SharedRollKeys.Contains(v.Variable) && t.RollsByKey.TryGetValue(v.Variable, out var groupDice)
            && !(HostCalculates && !HostRolled(v.Variable, t)))
            return new BoxView(v.Variable, DiceText(groupDice), BoxBackground.Auto, GroupRollSuffix, false, GroupRollTooltip, null);
        var typedBox = (binding != null && IsTyped(binding)) || IsPhysicalRoll(v.Variable);
        var choices = binding?.Kind == BindingKind.Input
            ? InputChoices.For(string.IsNullOrWhiteSpace(binding.ChoicesTable) ? _state ?? new GameState() : ChoicesState(), runner.Pack, runner.Model, formula, v.Variable, pieceId)
            : binding != null && IsPickBinding(binding) ? picks.GetValueOrDefault(v.Variable) ?? Array.Empty<string>() : null;
        var reason = v.MissingReason == "Waiting for input" || (v.MissingReason != null && typedBox && Raw(v.Variable) == "")
            ? "Waiting for input" : v.MissingReason;
        // A guard box's tooltip never says whether the hidden guard holds: blank waits, typed is the viewer's own (B7).
        if (IsGuardBox(v.Variable)) reason = Raw(v.Variable).Trim().Length == 0 ? "Waiting for input" : v.Skipped ? null : v.MissingReason;
        if (v.Overridden || typedBox)
            return new BoxView(v.Variable, Raw(v.Variable), BoxBackground.Typed, v.Overridden && !typedBox ? " (override)" : binding is { Kind: BindingKind.Input, AlwaysAsk: true } ? AlwaysAskedSuffix : "", true, reason, null,
                false, choices, binding is { Kind: BindingKind.Roll } && (binding.DiceCount > 1 || binding.DiceCountExpr != null) ? TypeEachDieHint : null);
        var text = v.Value?.Display() ?? "";
        string? reroll = null;
        if (binding?.Kind == BindingKind.Roll && HostCalculates && !HostRolled(v.Variable, t))
            return new BoxView(v.Variable, HostRollsText, BoxBackground.Auto, "", false, null, null);
        if (binding?.Kind == BindingKind.Roll && t.RollsByKey.TryGetValue(v.Variable, out var dice))
        {
            text = DiceText(dice);
            reroll = HostCalculates ? null : v.Variable;
        }
        return new BoxView(v.Variable, text, BoxBackground.Auto, "", true, reason, reroll);
    }

    /// <summary>A manual-mode box: shows only what the user typed and compares it with the expected engine value.</summary>
    private BoxView CheckedBox(VariableState v)
    {
        var text = Raw(v.Variable);
        var exp = _lastExpected?.Variables.FirstOrDefault(e => e.Variable == v.Variable);
        string? tip = null;
        var wrong = false;
        if (text.Trim().Length == 0)
            tip = exp?.MissingReason is { } r && r != "Waiting for input" ? r : "Waiting for input";
        else if (exp?.Value is not { } ev)
            tip = exp?.MissingReason ?? "Cannot be checked yet";
        else if (!SameValue(ev, text, Binding(v.Variable)?.Kind == BindingKind.Location, out var notNumber))
        {
            wrong = true;
            tip = notNumber ? "Not a number" : $"Doesn't match ({exp.Source ?? v.Variable})";
        }
        if (!wrong && _verdict.GetValueOrDefault(v.Variable) == FormulaBox.Wrong)
        {
            wrong = true;
            tip = HostWrongText;
        }
        return new BoxView(v.Variable, text, BoxBackground.Typed, "", true, tip, null, wrong);
    }

    /// <summary>A pick's missing reason on a player view whose target (or the value a Piece output writes on it) the view cannot read.</summary>
    private const string TargetHiddenText = "Hidden from this player";

    /// <summary>Status of a hot-seat run whose hidden inputs gave no result (which input is not said: that would tell whether it is set).</summary>
    public const string HiddenWaitText = "Some inputs are hidden and could not be calculated.";

    /// <summary>Status of a guest's dialog before the host answered: the hidden inputs are filled in on the host.</summary>
    public const string HostWaitText = "Some inputs are hidden: Calculate or Apply asks the host.";

    /// <summary>Tooltip of a box the host found wrong.</summary>
    public const string HostWrongText = "Doesn't match (checked by the host)";

    /// <summary>
    /// What an online guest sends for this dialog: the typed Input boxes and picked pieces (a hidden pick too: its label is from the
    /// guest's own list), and in the manual modes the typed checked boxes (never a hidden
    /// box, an override of the fully automatic mode or a dice value). The host runs it with the full game.
    /// </summary>
    public FormulaIntent Intent(string? runId, bool calculate, bool preview = false)
    {
        var typed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in formula.Bindings)
        {
            if (b?.Variable == null || (IsHidden(b.Variable) && !IsPickBinding(b))) continue;   // a pick is a label from the guest's own list
            if (IsTyped(b) && _typed.TryGetValue(b.Variable, out var t)) typed[b.Variable] = t;
            else if (Checked && IsChecked(b) && _overrides.TryGetValue(b.Variable, out var o)) typed[b.Variable] = o;
        }
        // The piece (or holder, B9) each sent pick stands for: the host refuses the run if the label now resolves to another one.
        Dictionary<string, string>? picks = null;
        foreach (var b in formula.Bindings)
            if (b is { Variable: { } v } && IsPickBinding(b) && typed.ContainsKey(v) && _pickedId.TryGetValue(v, out var id))
                (picks ??= new(StringComparer.Ordinal))[v] = id;
        return new FormulaIntent(formula.Id, pieceId, runId, typed, calculate, preview, picks);
    }

    /// <summary>The host's verdict: its wrong boxes are marked (until edited) and its message becomes the status. States only, never values.</summary>
    public void ApplyVerdict(FormulaVerdict verdict)
    {
        _verdict.Clear();
        _verdictMessage = null;
        if (verdict == null) return;
        foreach (var b in verdict.Boxes ?? Array.Empty<FormulaBox>())
            if (b?.Var != null && b.State != null && !IsHidden(b.Var)) _verdict[b.Var] = b.State;
        var preview = verdict.Values != null || verdict.Rolls != null;
        _verdictMessage = verdict.Applied || preview ? verdict.Message
            : verdict.Message ?? (_verdict.ContainsValue(FormulaBox.Wrong) ? null : FormulaMasking.ErrorHiddenText);
        if (!preview) return;
        // A preview (the guest's Calculate): fill the boxes as Calculate does in solo, with the host's values and dice.
        foreach (var (variable, text) in verdict.Values ?? new Dictionary<string, string>())
        {
            if (variable == null || text == null || IsHidden(variable) || Binding(variable) is not { } b) continue;
            if (b.Kind == BindingKind.Input) _typed[variable] = text;
            else if (Checked && IsChecked(b)) _overrides[variable] = text;
        }
        if (verdict.Rolls != null)
        {
            PinRolls(verdict.Rolls);
            foreach (var (k, dice) in verdict.Rolls) if (k != null && dice != null) _hostDice[k] = dice.ToList();
        }
        AutoCalculated = true;
    }

    /// <summary>The host's state of a box from the last verdict (<see cref="FormulaBox.Ok"/> or <see cref="FormulaBox.Wrong"/>), or null.</summary>
    public string? VerdictState(string variable) => _verdict.GetValueOrDefault(variable);

    /// <summary>Full-precision invariant text for a value, so a Calculate-then-Apply compares exactly.</summary>
    public static string ExactText(Value v) => Exact(v);

    private static string Exact(Value v) =>
        v.Kind == ValueKind.Number ? v.Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : v.Display();

    /// <summary>Numbers compare invariantly against the exact engine value (a comma decimal is not a number); text compares after TableLookup.Normalise
    /// on both sides; Location is always text. The typed text only gates Apply, it never feeds the run.</summary>
    private static bool SameValue(Value expected, string typed, bool asText, out bool notNumber)
    {
        notNumber = false;
        if (asText || expected.Kind != ValueKind.Number)
            return string.Equals(TableLookup.Normalise(typed), TableLookup.Normalise(expected.Display()), StringComparison.OrdinalIgnoreCase);
        if (!TableLookup.TryNum(typed, out var n)) { notNumber = true; return false; }
        return n == expected.Number;
    }

    private string Raw(string variable) =>
        (Binding(variable) is { } b && IsTyped(b) ? _typed : _overrides).GetValueOrDefault(variable) ?? "";

    private static string DiceText(IReadOnlyList<int> dice) =>
        dice.Count <= 1 ? dice.Sum().ToString() : $"{dice.Sum()} ({string.Join("+", dice)})";
}
