using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>Decides which pieces a formula (or property) applies to, using the piece-facing expression functions.</summary>
public static class PieceFilter
{
    /// <summary>Null or empty matches every piece. Parse and evaluation errors give false.</summary>
    public static bool Matches(string? appliesTo, PieceInstance p, GameState s, ModuleModel model, RulesPack pack) =>
        Compile(appliesTo, s, pack)(p);

    /// <summary>Parses once. Null or empty matches every piece. A parse error, or an evaluation error on a piece, gives false.</summary>
    public static Func<PieceInstance, bool> Compile(string? expr, GameState s, RulesPack pack)
    {
        if (string.IsNullOrWhiteSpace(expr)) return _ => true;
        var e = TryParse(expr);
        if (e == null) return _ => false;
        return p =>
        {
            try { return Evaluator.Evaluate(e, new FilterContext(p, s, pack)).IsTruthy; }
            catch (Exception) { return false; }
        };
    }

    /// <summary>
    /// Like <see cref="Matches"/>, with values about to be seeded on this piece (property id -> value) read as if already in its ledger
    /// (a ledger value still wins). Used by seeding, so an applies-to sees a sibling's sheet value rather than its Default.
    /// </summary>
    internal static bool MatchesWithPending(string? appliesTo, PieceInstance p, GameState s, RulesPack pack, IReadOnlyDictionary<string, double> pending)
    {
        if (string.IsNullOrWhiteSpace(appliesTo)) return true;
        var e = TryParse(appliesTo);
        if (e == null) return false;
        try { return Evaluator.Evaluate(e, new FilterContext(p, s, pack, pending)).IsTruthy; }
        catch (Exception) { return false; }
    }

    private static Quartermaster.Rules.Expr.Expr? TryParse(string text)
    {
        try { return Parser.Parse(text); }
        catch (Exception) { return null; }
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

    /// <summary>Parsed applies-to per property record (records are replaced on edit, so this never goes stale); null = does not parse.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PropertyDef, ParsedAppliesTo> AppliesToCache = new();

    private sealed class ParsedAppliesTo(Quartermaster.Rules.Expr.Expr? expr) { public Quartermaster.Rules.Expr.Expr? Expr { get; } = expr; }

    private sealed class FilterContext(PieceInstance p, GameState s, RulesPack pack, IReadOnlyDictionary<string, double>? pending = null) : IEvalContext
    {
        private readonly HashSet<string> _checking = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _declared = new(StringComparer.Ordinal);

        /// <summary>The piece's ledger value, else a value about to be seeded, else null.</summary>
        private double? Stored(PropertyDef def) =>
            Ledger.Get(s, LedgerKey.Piece(p.Id), def.Id) ?? (pending != null && pending.TryGetValue(def.Id, out var v) ? v : null);

        public Value GetVariable(string name)
        {
            var def = pack.PropertyByName(name) ?? throw new EvalError($"Unknown property '{name}'");
            var v = Stored(def);
            if (v != null) return Value.Of(v.Value);
            if (!HiddenInView(def) && Declared(def)) return Value.Of(def.Default);
            throw new EvalError($"{name} has no value on this piece");
        }

        /// <summary>
        /// In a player's view a missing value may be hidden rather than unset: a Private value, or any value of the placeholder left for
        /// another side's masked piece (its whole ledger was removed). Those do not fall back to the Default.
        /// </summary>
        private bool HiddenInView(PropertyDef def) =>
            s.IsPlayerView && (def.Visibility == Visibility.Private || PlayerView.IsPlaceholder(p));

        /// <summary>True when the property is declared on this piece: not Holders-scoped, and its applies-to is empty or matches. Re-entry counts as not declared.</summary>
        private bool Declared(PropertyDef def)
        {
            if (def.Scope == PropertyScope.Holders) return false;
            if (string.IsNullOrWhiteSpace(def.AppliesTo)) return true;
            if (_declared.TryGetValue(def.Id, out var known)) return known;
            if (!_checking.Add(def.Id)) return false;
            try
            {
                var e = AppliesToCache.GetValue(def, d => new ParsedAppliesTo(TryParse(d.AppliesTo!))).Expr;
                var result = false;
                if (e != null)
                {
                    try { result = Evaluator.Evaluate(e, this).IsTruthy; }
                    catch (Exception ex) when (ex is EvalError or ParseError) { result = false; }
                }
                _declared[def.Id] = result;
                return result;
            }
            finally { _checking.Remove(def.Id); }
        }

        public Value Lookup(string tableName, Value row, Value? col) => LookupTable(pack, tableName, row, col);
        public IReadOnlyList<int> RollDice(int n, int sides) => throw new EvalError("Dice are not allowed here");
        public bool Has(string property) => pack.PropertyByName(property) is { } def && Stored(def) != null;
        public Value PieceName() => NameValue(p);
        public Value Marker(string key) => MarkerValue(p, key);
        public double Turn() => s.Position?.Turn ?? s.Turn.Turn;
        public double Stage() => s.Position?.Repeat ?? 1;
    }
}
