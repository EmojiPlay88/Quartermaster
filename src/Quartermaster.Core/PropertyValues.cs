using System.Runtime.CompilerServices;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Core;

/// <summary>
/// The value of a property as the rules read it: a stored property's ledger value, or a computed property's expression
/// (<see cref="PropertyDef.Expr"/>) evaluated now. The one place that knows about computed properties; every rules read site calls it.
/// <see cref="Ledger.Get"/> and <see cref="Ledger.GetOrDefault"/> stay the raw stored reads (an output's "before", manual edits).
/// </summary>
public static class PropertyValues
{
    /// <summary>Deepest nesting of computed reads (one computed property reading another ...) before the read is an error.</summary>
    private const int MaxDepth = 16;

    /// <summary>The computed reads in progress on this thread, as (ledger key, property id): reading one again is a cycle.</summary>
    [ThreadStatic] private static HashSet<(string Key, string Id)>? _active;
    [ThreadStatic] private static int _depth;

    /// <summary>Marks an error already named after the computed property that raised it, so the properties reading it do not rename it.</summary>
    private const string NamedKey = "Quartermaster.ComputedProperty";

    /// <summary>Parsed expression per property record (records are replaced on edit, so this never goes stale).</summary>
    private static readonly ConditionalWeakTable<PropertyDef, ParsedExpr> ExprCache = new();

    private sealed class ParsedExpr(AstExpr? expr, string? error)
    {
        public AstExpr? Expr { get; } = expr;
        public string? Error { get; } = error;
    }

    /// <summary>
    /// The value at <paramref name="key"/>: stored, the ledger value (null when unset). Computed, the expression evaluated for that piece (its
    /// properties, globals and functions, as a unit filter reads them) or holder (Holders-scope names read this holder); null when the property
    /// does not apply there (a piece its applies-to excludes, a piece that is gone, a holder key of a unit property or the reverse).
    /// An error is thrown as <see cref="EvalError"/> "{name}: {message}", keeping <see cref="EvalError.MissingValue"/> and
    /// <see cref="EvalError.HiddenData"/>; a cycle or nesting deeper than 16 is "{name} is computed from itself".
    /// </summary>
    public static double? Get(GameState s, RulesPack pack, ModuleModel? model, string key, PropertyDef def) => Get(s, pack, model, key, def, null);

    /// <summary>
    /// Like <see cref="Get(GameState, RulesPack, ModuleModel?, string, PropertyDef)"/>, a stored property falling back to its Default. A computed
    /// property has no Default: no value is the error "{name} has no value on this piece" (a missing value).
    /// </summary>
    public static double GetOrDefault(GameState s, RulesPack pack, ModuleModel? model, string key, PropertyDef def) => GetOrDefault(s, pack, model, key, def, null);

    /// <param name="filters">The caller's aggregate state (same game state), so an aggregate in the expression reuses its compiled filters.</param>
    internal static double? Get(GameState s, RulesPack pack, ModuleModel? model, string key, PropertyDef def, AggregateFilters? filters) =>
        PropertyFacts.IsComputed(def) ? Computed(s, pack, model, key, def, filters) : Ledger.Get(s, key, def.Id);

    internal static double GetOrDefault(GameState s, RulesPack pack, ModuleModel? model, string key, PropertyDef def, AggregateFilters? filters) =>
        !PropertyFacts.IsComputed(def) ? Ledger.GetOrDefault(s, key, def)
        : Computed(s, pack, model, key, def, filters) ?? throw new EvalError($"{def.Name} has no value on this piece") { MissingValue = true };

    /// <summary>
    /// True when a computed property applies to the piece (its scope holds pieces and its applies-to is empty or matches): what has() reads,
    /// since a computed value is never stored.
    /// </summary>
    internal static bool AppliesTo(GameState s, RulesPack pack, ModuleModel? model, PieceInstance p, PropertyDef def) =>
        def.Scope != PropertyScope.Holders && PieceFilter.PieceContext(p, s, pack, filters: new AggregateFilters(s, pack, model)).Declared(def);

    private static double? Computed(GameState s, RulesPack pack, ModuleModel? model, string key, PropertyDef def, AggregateFilters? filters)
    {
        if (!LedgerKey.TryParse(key, out var isPiece, out var id)) return null;
        PieceInstance? piece = null;
        if (isPiece ? def.Scope == PropertyScope.Holders || !s.Pieces.TryGetValue(id, out piece) : def.Scope == PropertyScope.Pieces) return null;

        var active = _active ??= new();
        if (_depth >= MaxDepth || !active.Add((key, def.Id))) throw Named(new EvalError($"{def.Name} is computed from itself"));
        _depth++;
        try
        {
            var parsed = ExprCache.GetValue(def, Parse);
            if (parsed.Expr == null) throw new EvalError(parsed.Error!);
            filters ??= new AggregateFilters(s, pack, model);
            Value v;
            if (piece != null)
            {
                var ctx = PieceFilter.PieceContext(piece, s, pack, filters);
                if (!ctx.Declared(def)) return null;
                v = Evaluator.Evaluate(parsed.Expr, ctx);
            }
            else v = Evaluator.Evaluate(parsed.Expr, PieceFilter.HolderContext(s, pack, id, filters));
            if (v.Kind != ValueKind.Number) throw new EvalError("the value is not a number");
            if (!double.IsFinite(v.Number)) throw new EvalError("the value is not a finite number");
            return v.Number;
        }
        catch (EvalError ex) when (!ex.Data.Contains(NamedKey))
        {
            throw Named(new EvalError($"{def.Name}: {ex.Message}", ex.At) { MissingValue = ex.MissingValue, HiddenData = ex.HiddenData });
        }
        finally
        {
            _depth--;
            active.Remove((key, def.Id));
        }
    }

    private static ParsedExpr Parse(PropertyDef def)
    {
        try { return new ParsedExpr(Parser.Parse(def.Expr!), null); }
        catch (ParseError e) { return new ParsedExpr(null, "the expression does not parse: " + e.Message); }
    }

    private static EvalError Named(EvalError e)
    {
        e.Data[NamedKey] = true;
        return e;
    }
}
