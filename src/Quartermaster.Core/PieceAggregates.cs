using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// Piece sums: sum, countof, maxof and minof over every piece, and hexsum, hexcount, hexmax and hexmin over the pieces in the hex of the piece
/// being evaluated. Shared by every evaluation context (<see cref="IEvalContext.Aggregate"/>).
/// </summary>
public static class PieceAggregates
{
    /// <summary>How many filter texts the <see cref="AggregateFilters"/> caches compiled on this thread (tests read it).</summary>
    [ThreadStatic] internal static int FilterCompiles;

    /// <summary>
    /// The value of an aggregate call. <paramref name="self"/> is the piece being evaluated (null where there is none: the hex ones then throw
    /// "{fn}() is not available here", as they do without a module model). <paramref name="compile"/> turns a filter text into a match;
    /// "" matches every piece; an error the filter raises on a piece fails the whole aggregate (no piece is skipped silently). A piece's value is
    /// its ledger value of the property, else the property's Default (as a Unit binding reads it); a holder-only property is an error.
    /// </summary>
    public static Value Evaluate(string fn, string? property, string? filter, GameState s, RulesPack pack, ModuleModel? model,
        PieceInstance? self, Func<string, Func<PieceInstance, bool>> compile)
    {
        IEnumerable<PieceInstance> pieces = s.Pieces.Values;
        if (ExprFacts.IsHexAggregate(fn))
        {
            if (self == null || model == null) throw new EvalError($"{fn}() is not available here");
            var here = PieceInfo.Location(self, model) ?? throw new EvalError($"{Label(self)} is not in a hex");
            pieces = pieces.Where(p => p.MapId == self.MapId && PieceInfo.Location(p, model) == here);
        }
        var text = filter ?? "";
        var match = compile(text);
        pieces = pieces.Where(match);
        if (fn is "countof" or "hexcount") return Value.Of(pieces.Count());

        var def = (property == null ? null : pack.PropertyByName(property)) ?? throw new EvalError($"Unknown property '{property}'");
        if (def.Scope == PropertyScope.Holders) throw new EvalError($"{def.Name} is not a piece property");
        var values = pieces.Select(p => Ledger.GetOrDefault(s, LedgerKey.Piece(p.Id), def));
        switch (fn)
        {
            case "sum" or "hexsum":
                return Value.Of(values.Sum());
            case "maxof" or "hexmax":
            case "minof" or "hexmin":
                var max = fn is "maxof" or "hexmax";
                double? best = null;
                foreach (var v in values)
                    if (best == null || (max ? v > best : v < best)) best = v;
                return best is { } b ? Value.Of(b) : throw new EvalError($"No piece matches \"{text}\"");
            default:
                throw new EvalError($"{fn}() is not available here");
        }
    }

    /// <summary>A piece as the log names it: its name, else "piece".</summary>
    private static string Label(PieceInstance p)
    {
        var n = PieceInfo.Name(p);
        return string.IsNullOrWhiteSpace(n) ? "piece" : n;
    }
}

/// <summary>
/// The compiled filters of aggregate calls, each filter text compiled once (one cache per evaluation context, or per compiled unit filter, so an
/// aggregate in a unit filter does not recompile per piece). Its dictionary and delegate are made only when an aggregate is evaluated.
/// </summary>
internal sealed class AggregateFilters(GameState s, RulesPack pack, ModuleModel? model)
{
    private Dictionary<string, Func<PieceInstance, bool>>? _cache;
    private Func<string, Func<PieceInstance, bool>>? _compile;

    public Func<string, Func<PieceInstance, bool>> Compile => _compile ??= CompileOnce;

    private Func<PieceInstance, bool> CompileOnce(string text)
    {
        _cache ??= new(StringComparer.Ordinal);
        if (_cache.TryGetValue(text, out var hit)) return hit;
        PieceAggregates.FilterCompiles++;
        return _cache[text] = PieceFilter.CompileStrict(text, s, pack, model);
    }
}
