using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

public enum BoxBackground { Auto, Typed }

/// <summary>What one input box in the formula dialog should look like.</summary>
public sealed record BoxView(string Variable, string Text, BoxBackground Background, string CaptionSuffix, bool Enabled,
    string? Tooltip, string? RerollKey);

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

    public FormulaDef Formula => formula;

    /// <summary>The rules pack changed: keep what the user typed but run the new runner and formula.</summary>
    public void Rebind(FormulaRunner newRunner, FormulaDef newFormula)
    {
        runner = newRunner;
        formula = newFormula;
    }
    public string? PieceId => pieceId;

    /// <summary>When on, Roll variables become typed boxes (the player rolled real dice).</summary>
    public bool PhysicalDice
    {
        get => _physical;
        set
        {
            if (_physical == value) return;
            _physical = value;
            foreach (var b in formula.Bindings.Where(b => b.Kind == BindingKind.Roll)) _overrides.Remove(b.Variable);
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
        if (IsPhysicalRoll(variable)) { _overrides[variable] = text; return; }
        if (string.IsNullOrWhiteSpace(text)) _overrides.Remove(variable);
        else _overrides[variable] = text;
    }

    /// <summary>Drops the pinned roll for a key so the next run rolls it again.</summary>
    public void Reroll(string key) => _fixedRolls.Remove(key);

    public FormulaTrace Run(GameState state)
    {
        // Physical roll boxes always carry an override (blank = still waiting), so the runner never rolls for them.
        var ov = new Dictionary<string, string>(_overrides);
        foreach (var b in formula.Bindings.Where(b => b.Kind == BindingKind.Roll && PhysicalDice))
            ov.TryAdd(b.Variable, "");
        var trace = runner.Run(state, new RunRequest(formula.Id, pieceId, new Dictionary<string, string>(_typed), ov, _fixedRolls));
        _fixedRolls = trace.RollsByKey.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return trace;
    }

    public DialogView Evaluate(GameState state) => View(Run(state));

    public DialogView View(FormulaTrace t)
    {
        var boxes = t.Variables.Select(v => Box(v, t)).ToList();
        var inline = t.RollsByKey.Where(kv => kv.Key.StartsWith("dice#", StringComparison.Ordinal))
            .Select(kv => new InlineRollView(kv.Key, DiceText(kv.Value))).ToList();
        var missing = t.Variables.Where(v => v.MissingReason != null).Select(v => v.Variable).ToList();
        var negative = t.Error == null && t.Outputs.Any(o => o.After < 0);
        var status = t.Error ?? (missing.Count > 0 ? "Waiting for: " + string.Join(", ", missing) : "All inputs filled");
        return new DialogView(boxes, inline, t.Result?.Display() ?? "", t.Working + (negative ? " (negative)" : ""), negative, status,
            t.Result != null && t.Error == null, t);
    }

    private BoxView Box(VariableState v, FormulaTrace t)
    {
        var binding = Binding(v.Variable);
        var typedBox = binding?.Kind == BindingKind.Input || IsPhysicalRoll(v.Variable);
        var reason = v.MissingReason == "Waiting for input" || (v.MissingReason != null && typedBox && Raw(v.Variable) == "")
            ? "Waiting for input" : v.MissingReason;
        if (v.Overridden || typedBox)
            return new BoxView(v.Variable, Raw(v.Variable), BoxBackground.Typed, v.Overridden && !typedBox ? " (override)" : "", true, reason, null);
        var text = v.Value?.Display() ?? "";
        string? reroll = null;
        if (binding?.Kind == BindingKind.Roll && t.RollsByKey.TryGetValue(v.Variable, out var dice))
        {
            text = DiceText(dice);
            reroll = v.Variable;
        }
        return new BoxView(v.Variable, text, BoxBackground.Auto, "", true, reason, reroll);
    }

    private string Raw(string variable) =>
        (Binding(variable)?.Kind == BindingKind.Input ? _typed : _overrides).GetValueOrDefault(variable) ?? "";

    private static string DiceText(IReadOnlyList<int> dice) =>
        dice.Count <= 1 ? dice.Sum().ToString() : $"{dice.Sum()} ({string.Join("+", dice)})";
}
