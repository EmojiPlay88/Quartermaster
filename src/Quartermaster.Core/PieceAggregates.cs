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
    /// <summary>How many filter texts the caches made by <see cref="Cached"/> compiled on this thread (tests read it).</summary>
    [ThreadStatic] internal static int FilterCompiles;

    /// <summary>
    /// The value of an aggregate call. <paramref name="self"/> is the piece being evaluated (null where there is none: the hex ones then throw
    /// "{fn}() is not available here", as they do without a module model). <paramref name="compile"/> turns a filter text into a match;
    /// "" matches every piece. A piece's value is its ledger value of the property, else the property's Default (as a Unit binding reads it).
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

    /// <summary>A compiler that compiles each filter text once and then reuses it (one per evaluation context, or per compiled filter).</summary>
    internal static Func<string, Func<PieceInstance, bool>> Cached(Func<string, Func<PieceInstance, bool>> compile)
    {
        var cache = new Dictionary<string, Func<PieceInstance, bool>>(StringComparer.Ordinal);
        return text =>
        {
            if (cache.TryGetValue(text, out var hit)) return hit;
            FilterCompiles++;
            return cache[text] = compile(text);
        };
    }

    /// <summary>A piece as the log names it: its name, else "piece".</summary>
    private static string Label(PieceInstance p)
    {
        var n = PieceInfo.Name(p);
        return string.IsNullOrWhiteSpace(n) ? "piece" : n;
    }
}
