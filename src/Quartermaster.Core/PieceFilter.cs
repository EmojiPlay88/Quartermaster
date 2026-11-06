using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>Decides which pieces a formula (or property) applies to, using the piece-facing expression functions.</summary>
public static class PieceFilter
{
    /// <summary>Null or empty matches every piece. Parse and evaluation errors give false.</summary>
    public static bool Matches(string? appliesTo, PieceInstance p, GameState s, ModuleModel model, RulesPack pack)
    {
        if (string.IsNullOrWhiteSpace(appliesTo)) return true;
        try
        {
            var e = Parser.Parse(appliesTo);
            var v = Evaluator.Evaluate(e, new FilterContext(p, s, pack));
            return v.IsTruthy;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Formulas that need a unit (a Unit binding or output) and apply to this piece, ordered by name.</summary>
    public static IReadOnlyList<FormulaDef> FormulasFor(RulesPack pack, PieceInstance p, GameState s, ModuleModel model) =>
        pack.Formulas
            .Where(f => f.Bindings.Any(b => b.Kind == BindingKind.Unit) || f.Outputs.Any(o => o.Target == OutputTarget.Unit))
            .Where(f => Matches(f.AppliesTo, p, s, model, pack))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>True iff the piece's ledger holds a value for the property with this name (no default fallback).</summary>
    internal static bool HasProperty(GameState s, RulesPack pack, PieceInstance? p, string name)
    {
        if (p == null) return false;
        var def = pack.PropertyByName(name);
        return def != null && Ledger.Get(s, LedgerKey.Piece(p.Id), def.Id) != null;
    }

    internal static Value MarkerValue(PieceInstance? p, string key)
    {
        if (p == null) throw new EvalError("This formula needs a unit");
        return PieceInfo.Markers(p).TryGetValue(key, out var v) ? Value.Of(v) : throw new EvalError($"No marker '{key}'");
    }

    internal static Value NameValue(PieceInstance? p) =>
        p == null ? throw new EvalError("This formula needs a unit") : Value.Of(PieceInfo.Name(p));

    internal static Value LookupTable(RulesPack pack, string tableName, Value row, Value? col)
    {
        var t = pack.TableByName(tableName) ?? throw new EvalError($"No table named '{tableName}'");
        return TableLookup.Lookup(t, row, col);
    }

    private sealed class FilterContext(PieceInstance p, GameState s, RulesPack pack) : IEvalContext
    {
        public Value GetVariable(string name)
        {
            var def = pack.PropertyByName(name) ?? throw new EvalError($"Unknown property '{name}'");
            var v = Ledger.Get(s, LedgerKey.Piece(p.Id), def.Id) ?? throw new EvalError($"{name} has no value on this piece");
            return Value.Of(v);
        }

        public Value Lookup(string tableName, Value row, Value? col) => LookupTable(pack, tableName, row, col);
        public IReadOnlyList<int> RollDice(int n, int sides) => throw new EvalError("Dice are not allowed here");
        public bool Has(string property) => HasProperty(s, pack, p, property);
        public Value PieceName() => NameValue(p);
        public Value Marker(string key) => MarkerValue(p, key);
    }
}
