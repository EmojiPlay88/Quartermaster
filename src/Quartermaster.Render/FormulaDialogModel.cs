using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Render;

public enum BoxBackground { Auto, Typed }

/// <summary>What one input box in the formula dialog should look like. Wrong = checked mode and the typed value differs from the engine's.</summary>
public sealed record BoxView(string Variable, string Text, BoxBackground Background, string CaptionSuffix, bool Enabled,
    string? Tooltip, string? RerollKey, bool Wrong = false, IReadOnlyList<string>? Choices = null);

/// <summary>A pinned inline dice() roll, with the key that re-rolls it.</summary>
public sealed record InlineRollView(string Key, string Text);

public sealed record DialogView(IReadOnlyList<BoxView> Boxes, IReadOnlyList<InlineRollView> InlineRolls, string ResultText,
    string WorkingText, bool WorkingNegative, string Status, bool CanApply, FormulaTrace Trace);

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
            foreach (var v in _hidden) { _typed.Remove(v); _overrides.Remove(v); }
        }
    }

    /// <summary>True on an online guest's screen: hidden boxes say the host calculates them, and the host's verdict is shown.</summary>
    public bool HostCalculates { get; set; }

    /// <summary>The text of a hidden box.</summary>
    public string HiddenBoxText => HostCalculates ? FormulaMasking.HostCalculatesText : FormulaMasking.HiddenText;

    private bool IsHidden(string v) => _hidden.Contains(v);

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

    /// <summary>Distance moved, for Moved variables (null = unknown).</summary>
    public double? Moved { get; set; }

    /// <summary>True once Calculate has filled the boxes. The boxes stay editable.</summary>
    public bool AutoCalculated { get; private set; }

    /// <summary>The expected (engine-only) run behind the last checked evaluation.</summary>
    public FormulaTrace? LastExpected => _lastExpected;

    private static bool IsCheckedKind(BindingKind k) =>
        k is BindingKind.Unit or BindingKind.Global or BindingKind.Holder or BindingKind.Table or BindingKind.Location or BindingKind.Moved;

    private bool IsCheckedBox(string v) => Checked && Binding(v) is { } b && IsCheckedKind(b.Kind);

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

    private bool IsPhysicalRoll(string v) => PhysicalDice && Binding(v)?.Kind == BindingKind.Roll;

    /// <summary>The user changed a box. Typed boxes keep their text; auto boxes add or (when blank) drop an override.</summary>
    public void SetBoxText(string variable, string text)
    {
        var b = Binding(variable);
        if (b == null || IsHidden(variable)) return;
        _verdict.Remove(variable);
        _verdictMessage = null;
        if (b.Kind == BindingKind.Input) { _typed[variable] = text; return; }
        if (IsPhysicalRoll(variable) || IsCheckedBox(variable)) { _overrides[variable] = text; return; }
        if (string.IsNullOrWhiteSpace(text)) _overrides.Remove(variable);
        else _overrides[variable] = text;
    }

    /// <summary>Drops the pinned roll for a key so the next run rolls it again.</summary>
    public void Reroll(string key)
    {
        if (HostCalculates) return;   // a guest's dice are the host's: no re-rolling
        _fixedRolls.Remove(key);
    }

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

    /// <summary>
    /// Which ledger outputs the viewer may see (set by <see cref="FormulaMasking.Configure"/>); null = all (solo). Only those decide the
    /// "(negative)" mark: another side's value going negative is not shown.
    /// </summary>
    public Func<string, bool>? OutputVisible { get; set; }

    private Dictionary<string, string> PhysicalRollOverrides()
    {
        var ov = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!PhysicalDice) return ov;
        foreach (var b in formula.Bindings.Where(b => b.Kind == BindingKind.Roll))
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
        _fixedRolls = merged;
    }

    public FormulaTrace Run(GameState state)
    {
        var typed = new Dictionary<string, string>(_typed);
        // Physical roll boxes always carry an override (blank = still waiting), so the runner never rolls for them.
        var ov = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _overrides)
        {
            if (IsHidden(kv.Key)) continue;   // a hidden input is never overridden: the engine reads it
            if (!Checked || Binding(kv.Key)?.Kind == BindingKind.Roll) ov[kv.Key] = kv.Value; // checked: typed text only gates, never feeds the run
        }
        foreach (var b in formula.Bindings.Where(b => b.Kind == BindingKind.Roll && PhysicalDice))
            ov.TryAdd(b.Variable, "");
        var trace = runner.Run(state, new RunRequest(formula.Id, pieceId, typed, ov, _fixedRolls, Moved, false, DiceSource));
        MergePins(trace);
        if (!Checked) return trace;
        // Checked: this is the EXPECTED engine run, the only source of ledger commands. Until every box matches it carries no result.
        _lastExpected = trace;
        return View(trace).CanApply ? trace : trace with { Result = null, Working = "", Outputs = Array.Empty<OutputPreview>() };
    }

    /// <summary>Runs with the engine's auto values (rolling and pinning dice) and copies every value into the boxes.</summary>
    public void Calculate(GameState state)
    {
        var trace = runner.Run(state, new RunRequest(formula.Id, pieceId, new Dictionary<string, string>(_typed),
            PhysicalRollOverrides(), _fixedRolls, Moved, true, DiceSource));
        MergePins(trace);
        foreach (var v in trace.Variables)
        {
            if (v.Value is not { } val || IsHidden(v.Variable)) continue;   // a hidden value is never copied into a box
            var b = Binding(v.Variable);
            if (b == null) continue;
            if (b.Kind == BindingKind.Input) _typed[v.Variable] = Exact(val);
            else if (b.Kind != BindingKind.Roll && Checked) _overrides[v.Variable] = Exact(val);
        }
        AutoCalculated = true;
    }

    public DialogView Evaluate(GameState state) => View(Run(state));

    public DialogView View(FormulaTrace t)
    {
        var vars = t.Variables;
        var boxes = vars.Select(v => Box(v, t)).ToList();
        var inline = t.RollsByKey.Where(kv => kv.Key.StartsWith("dice#", StringComparison.Ordinal) && (!HostCalculates || HostRolled(kv.Key, t)))
            .Select(kv => new InlineRollView(kv.Key, DiceText(kv.Value))).ToList();
        // A guest's local run rolls its own dice; until the host's are in, no result built on them is shown.
        var localDice = HostCalculates && t.RollsByKey.Keys.Any(k => !HostRolled(k, t));
        var masked = _hidden.Count > 0;
        var missing = vars.Where(v => v.MissingReason != null && !IsHidden(v.Variable)).Select(v => v.Variable).ToList();
        var wrong = boxes.Where(b => b.Wrong).Select(b => b.Variable).ToList();
        var canApply = t.Result != null && t.Error == null;
        if (Checked)
        {
            var empty = boxes.Where(b => IsCheckedBox(b.Variable) && !IsHidden(b.Variable) && b.Text.Trim().Length == 0).Select(b => b.Variable).ToList();
            missing = vars.Select(v => v.Variable).Where(n => missing.Contains(n) || empty.Contains(n)).ToList();
            // A wrong formula must never reach the ledger: every manual box needs an engine value to be checked against.
            var verifiable = _lastExpected is { Error: null } exp
                && vars.All(v => !IsCheckedBox(v.Variable) || exp.Variables.FirstOrDefault(e => e.Variable == v.Variable)?.Value != null);
            canApply = canApply && wrong.Count == 0 && missing.Count == 0 && verifiable;
        }
        var negative = t.Error == null && t.Outputs.Any(o => o.After < 0 && (OutputVisible == null || OutputVisible(o.Key)));
        var status = wrong.Count > 0 ? "Doesn't match: " + string.Join(", ", wrong)
            : t.Error != null ? (masked ? FormulaMasking.ErrorHiddenText : t.Error)
            : missing.Count > 0 ? "Waiting for: " + string.Join(", ", missing)
            : masked && t.Result == null ? (HostCalculates ? HostWaitText : HiddenWaitText)
            : "All inputs filled";
        if (_verdictMessage != null) status = _verdictMessage;
        // The working line shows every input's value: with a hidden input it is replaced (the logged working is redacted by PlayerView).
        var working = masked ? (t.Result != null ? FormulaMasking.WorkingHiddenText : "") : t.Working;
        var result = t.Result?.Display() ?? "";
        if (localDice) { working = ""; result = ""; negative = false; }
        return new DialogView(boxes, inline, result, working + (negative ? " (negative)" : ""), negative, status,
            canApply, t);
    }

    private BoxView Box(VariableState v, FormulaTrace t)
    {
        var binding = Binding(v.Variable);
        if (IsHidden(v.Variable)) return new BoxView(v.Variable, HiddenBoxText, BoxBackground.Auto, "", false, null, null);
        if (IsCheckedBox(v.Variable)) return CheckedBox(v);
        var typedBox = binding?.Kind == BindingKind.Input || IsPhysicalRoll(v.Variable);
        var choices = binding?.Kind == BindingKind.Input ? KeyChoices.For(runner.Pack, formula, v.Variable) : null;
        var reason = v.MissingReason == "Waiting for input" || (v.MissingReason != null && typedBox && Raw(v.Variable) == "")
            ? "Waiting for input" : v.MissingReason;
        if (v.Overridden || typedBox)
            return new BoxView(v.Variable, Raw(v.Variable), BoxBackground.Typed, v.Overridden && !typedBox ? " (override)" : "", true, reason, null,
                false, choices);
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

    /// <summary>Status of a hot-seat run whose hidden inputs gave no result (which input is not said: that would tell whether it is set).</summary>
    public const string HiddenWaitText = "Some inputs are hidden and could not be calculated.";

    /// <summary>Status of a guest's dialog before the host answered: the hidden inputs are filled in on the host.</summary>
    public const string HostWaitText = "Some inputs are hidden: Calculate or Apply asks the host.";

    /// <summary>Tooltip of a box the host found wrong.</summary>
    public const string HostWrongText = "Doesn't match (checked by the host)";

    /// <summary>
    /// What an online guest sends for this dialog: the typed Input boxes, and in the manual modes the typed checked boxes (never a hidden
    /// box, an override of the fully automatic mode or a dice value). The host runs it with the full game.
    /// </summary>
    public FormulaIntent Intent(string? runId, bool calculate, bool preview = false)
    {
        var typed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in formula.Bindings)
        {
            if (b?.Variable == null || IsHidden(b.Variable)) continue;
            if (b.Kind == BindingKind.Input && _typed.TryGetValue(b.Variable, out var t)) typed[b.Variable] = t;
            else if (Checked && IsCheckedKind(b.Kind) && _overrides.TryGetValue(b.Variable, out var o)) typed[b.Variable] = o;
        }
        return new FormulaIntent(formula.Id, pieceId, runId, typed, calculate, preview);
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
            else if (Checked && IsCheckedKind(b.Kind)) _overrides[variable] = text;
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
        (Binding(variable)?.Kind == BindingKind.Input ? _typed : _overrides).GetValueOrDefault(variable) ?? "";

    private static string DiceText(IReadOnlyList<int> dice) =>
        dice.Count <= 1 ? dice.Sum().ToString() : $"{dice.Sum()} ({string.Join("+", dice)})";
}
