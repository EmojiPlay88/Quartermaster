using System.Globalization;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using Quartermaster.Vassal;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Core;

public sealed record VariableState(string Variable, Value? Value, bool Auto, bool Overridden, string? MissingReason);

public sealed record OutputPreview(string Key, string PropertyId, string PropertyName, double Before, double After);

public sealed record FormulaTrace(FormulaDef Formula, IReadOnlyList<VariableState> Variables, Value? Result, string Working,
    IReadOnlyList<OutputPreview> Outputs, string? Error, IReadOnlyList<DiceEntry> Rolls);

public sealed record RunRequest(string FormulaId, string? PieceId, IReadOnlyDictionary<string, string> Typed,
    IReadOnlyDictionary<string, string> Overrides, IReadOnlyDictionary<string, IReadOnlyList<int>> FixedRolls);

/// <summary>Binds a formula's variables to game data, evaluates it and previews/produces the ledger commands. Run never throws.</summary>
public sealed class FormulaRunner(RulesPack pack, ModuleModel model, Func<int, int, IReadOnlyList<int>> roller)
{
    private const string NeedsUnit = "This formula needs a unit";
    private readonly RulesPack _pack = pack;
    private readonly ModuleModel _model = model;
    private readonly Func<int, int, IReadOnlyList<int>> _roller = roller;

    /// <summary>A variable that cannot be filled yet (not an error).</summary>
    private sealed class Missing(string reason) : Exception(reason);

    public FormulaTrace Run(GameState s, RunRequest r)
    {
        var formula = _pack.ById<FormulaDef>(r.FormulaId);
        if (formula == null) return Fail(new FormulaDef { Id = r.FormulaId }, "Formula not found");
        try
        {
            return RunCore(s, r, formula);
        }
        catch (Exception ex)
        {
            return Fail(formula, ex is EvalError or ParseError ? ex.Message : "Could not run formula: " + ex.Message);
        }
    }

    private static FormulaTrace Fail(FormulaDef f, string error) =>
        new(f, Array.Empty<VariableState>(), null, "", Array.Empty<OutputPreview>(), error, Array.Empty<DiceEntry>());

    private FormulaTrace RunCore(GameState s, RunRequest r, FormulaDef formula)
    {
        AstExpr expr;
        try { expr = Parser.Parse(formula.Text ?? ""); }
        catch (ParseError pe) { return Fail(formula, pe.Message); }

        var piece = r.PieceId != null && s.Pieces.TryGetValue(r.PieceId, out var pi) ? pi : null;
        var run = new RunScope(this, s, r, formula, piece);

        // Variables of the text, then any used only inside Table binding row/column expressions.
        var names = new List<string>(Parser.Variables(expr));
        for (var i = 0; i < names.Count; i++)
        {
            var b = formula.Bindings.FirstOrDefault(x => x.Variable == names[i]);
            if (b is not { Kind: BindingKind.Table }) continue;
            foreach (var src in new[] { b.RowExpr, b.ColExpr })
            {
                if (string.IsNullOrWhiteSpace(src)) continue;
                foreach (var v in Parser.Variables(Parser.Parse(src)))
                    if (!names.Contains(v)) names.Add(v);
            }
        }

        var states = names.Select(run.Resolve).ToList();
        var rolls = run.Rolls;

        var needsUnit = formula.Bindings.Any(b => b.Kind == BindingKind.Unit)
            || formula.Outputs.Any(o => o.Target == OutputTarget.Unit);
        if (needsUnit && piece == null)
            return new(formula, states, null, "", Array.Empty<OutputPreview>(), NeedsUnit, rolls);

        if (states.Any(x => x.MissingReason != null))
            return new(formula, states, null, "", Array.Empty<OutputPreview>(), null, rolls);

        var result = Evaluator.Evaluate(expr, new RunContext(run));
        var working = WorkingLine.Render(expr, formula.Text ?? "", n => run.Cached(n)?.Value) + " = " + result.Display();

        var outputs = new List<OutputPreview>();
        var parts = new List<string>();
        foreach (var o in formula.Outputs)
        {
            var prop = _pack.ById<PropertyDef>(o.Property) ?? throw new EvalError("An output refers to a deleted property");
            string key;
            switch (o.Target)
            {
                case OutputTarget.Unit: key = LedgerKey.Piece(piece!.Id); break;
                case OutputTarget.Global: key = LedgerKey.Holder(HolderDef.GlobalId); break;
                default:
                    if (o.HolderId == null || (o.HolderId != HolderDef.GlobalId && _pack.ById<HolderDef>(o.HolderId) == null))
                        throw new EvalError("An output refers to a deleted holder");
                    key = LedgerKey.Holder(o.HolderId);
                    break;
            }
            if (result.Kind != ValueKind.Number) throw new EvalError("The result is not a number, so it cannot change " + prop.Name);
            var x = result.Number;
            var before = Ledger.GetOrDefault(s, key, prop);
            var after = o.Op switch { OutputOp.Subtract => before - x, OutputOp.Add => before + x, _ => x };
            if (double.IsNaN(after) || double.IsInfinity(after)) throw new EvalError("Result is not a finite number");
            outputs.Add(new OutputPreview(key, prop.Id, prop.Name, before, after));
            var b = Value.Of(before).Display();
            var xs = result.Display();
            var a = Value.Of(after).Display();
            parts.Add(o.Op switch
            {
                OutputOp.Subtract => $" → {prop.Name} {b} − {xs} = {a}",
                OutputOp.Add => $" → {prop.Name} {b} + {xs} = {a}",
                _ => $" → {prop.Name} = {a}",
            });
        }
        if (parts.Count > 0) working += string.Join("; ", parts);
        return new(formula, states, result, working, outputs, null, rolls);
    }

    public IReadOnlyList<GameCommand> Commands(FormulaTrace t, string? note)
    {
        var list = new List<GameCommand>();
        if (t.Result == null || t.Error != null) return list;
        foreach (var d in t.Rolls) list.Add(new RollDice(d.Purpose, d.Count, d.Sides, d.Results));
        foreach (var o in t.Outputs)
            list.Add(new LedgerDelta(o.Key, o.PropertyId, o.Before, o.After, "formula", t.Formula.Id, t.Formula.RuleRefId, note, t.Working));
        return list;
    }

    /// <summary>Per-run resolution of bindings to values.</summary>
    private sealed class RunScope(FormulaRunner owner, GameState s, RunRequest r, FormulaDef formula, PieceInstance? piece)
    {
        private readonly Dictionary<string, VariableState> _done = new(StringComparer.Ordinal);
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);
        private readonly List<DiceEntry> _rolls = new();
        public IReadOnlyList<DiceEntry> Rolls => _rolls;
        public PieceInstance? Piece => piece;
        public GameState State => s;
        public RulesPack Pack => owner._pack;
        public IReadOnlyList<int> Roll(int n, int sides)
        {
            var res = owner._roller(n, sides);
            _rolls.Add(new DiceEntry(0, $"{formula.Name}: dice", n, sides, res));
            return res;
        }

        public VariableState? Cached(string name) => _done.TryGetValue(name, out var v) ? v : null;

        /// <summary>The value, or throws <see cref="Missing"/> when it cannot be filled.</summary>
        public Value ValueOf(string name)
        {
            var st = Resolve(name);
            return st.Value ?? throw new Missing(st.MissingReason ?? "Missing value");
        }

        public VariableState Resolve(string name)
        {
            if (_done.TryGetValue(name, out var done)) return done;
            if (!_active.Add(name)) throw new EvalError($"Variable {name} depends on itself");
            VariableState st;
            try
            {
                var b = formula.Bindings.FirstOrDefault(x => x.Variable == name);
                try { st = b == null ? new(name, null, false, false, "No binding for " + name) : Bind(b); }
                catch (Missing m) { st = new(name, null, b is { Kind: not BindingKind.Input }, false, m.Message); }
            }
            finally { _active.Remove(name); }
            _done[name] = st;
            return st;
        }

        private static bool Numeric(VariableBinding b) =>
            b.Kind is BindingKind.Unit or BindingKind.Global or BindingKind.Holder or BindingKind.Roll
            || (b.Kind == BindingKind.Input && !(b.Choices is { Count: > 0 } && !b.Choices.All(c => TryNum(c, out _))));

        private static bool TryNum(string s, out double d) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d);

        private VariableState Bind(VariableBinding b)
        {
            var name = b.Variable;
            var auto = b.Kind != BindingKind.Input;
            if (r.Overrides.TryGetValue(name, out var ov) && ov != null)
            {
                if (Numeric(b))
                    return TryNum(ov, out var n)
                        ? new(name, Value.Of(n), auto, true, null)
                        : new(name, null, auto, true, "Not a number");
                return new(name, TryNum(ov, out var n2) && b.Kind != BindingKind.Location ? Value.Of(n2) : Value.Of(ov), auto, true, null);
            }
            switch (b.Kind)
            {
                case BindingKind.Unit:
                {
                    if (piece == null) throw new Missing("Needs a unit");
                    var prop = Prop(b);
                    return new(name, Value.Of(Ledger.GetOrDefault(s, LedgerKey.Piece(piece.Id), prop)), true, false, null);
                }
                case BindingKind.Global:
                    return new(name, Value.Of(Ledger.GetOrDefault(s, LedgerKey.Holder(HolderDef.GlobalId), Prop(b))), true, false, null);
                case BindingKind.Holder:
                {
                    var hid = b.HolderId;
                    if (hid == null || (hid != HolderDef.GlobalId && owner._pack.ById<HolderDef>(hid) == null)) throw new Missing("Unknown holder");
                    return new(name, Value.Of(Ledger.GetOrDefault(s, LedgerKey.Holder(hid), Prop(b))), true, false, null);
                }
                case BindingKind.Table:
                {
                    var table = (b.TableId == null ? null : owner._pack.ById<TableDef>(b.TableId))
                        ?? throw new EvalError($"The table for {name} was deleted");
                    var ctx = new RunContext(this);
                    var row = Evaluator.Evaluate(Parser.Parse(b.RowExpr ?? ""), ctx);
                    Value? col = string.IsNullOrWhiteSpace(b.ColExpr) ? null : Evaluator.Evaluate(Parser.Parse(b.ColExpr), ctx);
                    return new(name, TableLookup.Lookup(table, row, col), true, false, null);
                }
                case BindingKind.Location:
                {
                    if (piece == null) throw new Missing("Needs a unit");
                    var loc = PieceInfo.Location(piece, owner._model) ?? throw new Missing("No grid location");
                    return new(name, Value.Of(loc), true, false, null);
                }
                case BindingKind.Roll:
                {
                    var n = b.DiceCount ?? 1;
                    var sides = b.DiceSides ?? 6;
                    var purpose = $"{formula.Name}: {name}";
                    var res = r.FixedRolls.TryGetValue(name, out var fixedRoll) && fixedRoll != null ? fixedRoll : owner._roller(n, sides);
                    _rolls.Add(new DiceEntry(0, purpose, n, sides, res));
                    return new(name, Value.Of(res.Sum()), true, false, null);
                }
                default:
                    return Input(b);
            }
        }

        private PropertyDef Prop(VariableBinding b) =>
            (b.Property == null ? null : owner._pack.ById<PropertyDef>(b.Property)) ?? throw new Missing("Unknown property");

        private VariableState Input(VariableBinding b)
        {
            var name = b.Variable;
            if (!r.Typed.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw)) return new(name, null, false, false, "Waiting for input");
            raw = raw.Trim();
            if (b.Choices is { Count: > 0 })
            {
                var hit = b.Choices.FirstOrDefault(c => string.Equals(c.Trim(), raw, StringComparison.OrdinalIgnoreCase));
                if (hit == null) return new(name, null, false, false, "Choose one of " + string.Join(", ", b.Choices));
                return b.Choices.All(c => TryNum(c, out _)) && TryNum(hit, out var cn)
                    ? new(name, Value.Of(cn), false, false, null)
                    : new(name, Value.Of(hit.Trim()), false, false, null);
            }
            if (!TryNum(raw, out var v)) return new(name, null, false, false, "Not a number");
            if ((b.Min != null && v < b.Min) || (b.Max != null && v > b.Max))
            {
                var msg = b.Min != null && b.Max != null ? $"Must be {Value.Of(b.Min.Value).Display()}–{Value.Of(b.Max.Value).Display()}"
                    : b.Min != null ? $"Must be at least {Value.Of(b.Min.Value).Display()}"
                    : $"Must be at most {Value.Of(b.Max!.Value).Display()}";
                return new(name, null, false, false, msg);
            }
            return new(name, Value.Of(v), false, false, null);
        }
    }

    private sealed class RunContext(RunScope run) : IEvalContext
    {
        public Value GetVariable(string name) => run.ValueOf(name);
        public Value Lookup(string tableName, Value row, Value? col) => PieceFilter.LookupTable(run.Pack, tableName, row, col);
        public IReadOnlyList<int> RollDice(int n, int sides) => run.Roll(n, sides);
        public bool Has(string property) => PieceFilter.HasProperty(run.State, run.Pack, run.Piece, property);
        public Value PieceName() => PieceFilter.NameValue(run.Piece);
        public Value Marker(string key) => PieceFilter.MarkerValue(run.Piece, key);
    }
}
