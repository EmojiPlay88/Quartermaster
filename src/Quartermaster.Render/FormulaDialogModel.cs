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
        if (b == null) return;
        if (b.Kind == BindingKind.Input) { _typed[variable] = text; return; }
        if (IsPhysicalRoll(variable) || IsCheckedBox(variable)) { _overrides[variable] = text; return; }
        if (string.IsNullOrWhiteSpace(text)) _overrides.Remove(variable);
        else _overrides[variable] = text;
    }

    /// <summary>Drops the pinned roll for a key so the next run rolls it again.</summary>
    public void Reroll(string key) => _fixedRolls.Remove(key);

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
        foreach (var kv in trace.RollsByKey) merged[kv.Key] = kv.Value;
        _fixedRolls = merged;
    }

    public FormulaTrace Run(GameState state)
    {
        var typed = new Dictionary<string, string>(_typed);
        if (Checked)
        {
            // Expected run: engine values only (typed Inputs, no overrides), sharing the dice pins with the actual run.
            var expected = runner.Run(state, new RunRequest(formula.Id, pieceId, typed, PhysicalRollOverrides(), _fixedRolls, Moved, false));
            MergePins(expected);
            _lastExpected = expected;
        }
        // Physical roll boxes always carry an override (blank = still waiting), so the runner never rolls for them.
        var ov = new Dictionary<string, string>(_overrides);
        foreach (var b in formula.Bindings.Where(b => b.Kind == BindingKind.Roll && PhysicalDice))
            ov.TryAdd(b.Variable, "");
        if (Checked)
            foreach (var b in formula.Bindings.Where(b => IsCheckedKind(b.Kind)))
            {
                ov.TryAdd(b.Variable, "");
                // Accept the same number spellings as the comparison (a U+2212 minus, a stray space) before the runner parses them.
                if (TableLookup.TryNum(ov[b.Variable], out var num))
                    ov[b.Variable] = num.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
        var trace = runner.Run(state, new RunRequest(formula.Id, pieceId, typed, ov, _fixedRolls, Moved, false));
        MergePins(trace);
        return trace;
    }

    /// <summary>Runs with the engine's auto values (rolling and pinning dice) and copies every value into the boxes.</summary>
    public void Calculate(GameState state)
    {
        var trace = runner.Run(state, new RunRequest(formula.Id, pieceId, new Dictionary<string, string>(_typed),
            PhysicalRollOverrides(), _fixedRolls, Moved, true));
        MergePins(trace);
        foreach (var v in trace.Variables)
        {
            if (v.Value is not { } val) continue;
            var b = Binding(v.Variable);
            if (b == null) continue;
            if (b.Kind == BindingKind.Input) _typed[v.Variable] = val.Display();
            else if (b.Kind != BindingKind.Roll && Checked) _overrides[v.Variable] = val.Display();
        }
        AutoCalculated = true;
    }

    public DialogView Evaluate(GameState state) => View(Run(state));

    public DialogView View(FormulaTrace t)
    {
        // A mistyped manual value can make the typed run fail before binding anything; keep the boxes (from the expected run) so the user can fix it.
        var vars = t.Variables.Count == 0 && Checked && _lastExpected != null ? _lastExpected.Variables : t.Variables;
        var boxes = vars.Select(v => Box(v, t)).ToList();
        var inline = t.RollsByKey.Where(kv => kv.Key.StartsWith("dice#", StringComparison.Ordinal))
            .Select(kv => new InlineRollView(kv.Key, DiceText(kv.Value))).ToList();
        var missing = vars.Where(v => v.MissingReason != null).Select(v => v.Variable).ToList();
        var wrong = boxes.Where(b => b.Wrong).Select(b => b.Variable).ToList();
        var canApply = t.Result != null && t.Error == null;
        if (Checked)
        {
            var empty = boxes.Where(b => IsCheckedBox(b.Variable) && b.Text.Trim().Length == 0).Select(b => b.Variable).ToList();
            missing = vars.Select(v => v.Variable).Where(n => missing.Contains(n) || empty.Contains(n)).ToList();
            // A wrong formula must never reach the ledger: every manual box needs an engine value to be checked against.
            var verifiable = _lastExpected is { Error: null } exp
                && vars.All(v => !IsCheckedBox(v.Variable) || exp.Variables.FirstOrDefault(e => e.Variable == v.Variable)?.Value != null);
            canApply = canApply && wrong.Count == 0 && missing.Count == 0 && verifiable;
        }
        var negative = t.Error == null && t.Outputs.Any(o => o.After < 0);
        var status = wrong.Count > 0 ? "Doesn't match: " + string.Join(", ", wrong)
            : t.Error ?? (missing.Count > 0 ? "Waiting for: " + string.Join(", ", missing) : "All inputs filled");
        return new DialogView(boxes, inline, t.Result?.Display() ?? "", t.Working + (negative ? " (negative)" : ""), negative, status,
            canApply, t);
    }

    private BoxView Box(VariableState v, FormulaTrace t)
    {
        var binding = Binding(v.Variable);
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
        if (binding?.Kind == BindingKind.Roll && t.RollsByKey.TryGetValue(v.Variable, out var dice))
        {
            text = DiceText(dice);
            reroll = v.Variable;
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
        else if (!SameValue(ev, text, out var notNumber))
        {
            wrong = true;
            tip = notNumber ? "Not a number" : $"Doesn't match ({exp.Source ?? v.Variable})";
        }
        return new BoxView(v.Variable, text, BoxBackground.Typed, "", true, tip, null, wrong);
    }

    /// <summary>Numbers compare invariantly (a comma decimal is not a number); text compares case- and space-insensitively.</summary>
    private static bool SameValue(Value expected, string typed, out bool notNumber)
    {
        notNumber = false;
        typed = typed.Trim();
        switch (expected.Kind)
        {
            case ValueKind.Number:
                if (!TableLookup.TryNum(typed, out var n)) { notNumber = true; return false; }
                // The engine shows 0.## precision, so the displayed figure counts as correct too.
                return n == expected.Number || (TableLookup.TryNum(expected.Display(), out var shown) && n == shown);
            case ValueKind.Text:
                return TableLookup.Matches(new TableKey(TableKeyKind.Text, 0, 0, TableLookup.Normalise(expected.Text)), Value.Of(typed));
            default:
                return string.Equals(typed, expected.Display(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private string Raw(string variable) =>
        (Binding(variable)?.Kind == BindingKind.Input ? _typed : _overrides).GetValueOrDefault(variable) ?? "";

    private static string DiceText(IReadOnlyList<int> dice) =>
        dice.Count <= 1 ? dice.Sum().ToString() : $"{dice.Sum()} ({string.Join("+", dice)})";
}
