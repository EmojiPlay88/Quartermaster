using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules.Expr;

/// <summary>Static facts about a parsed expression.</summary>
public static class ExprFacts
{
    /// <summary>
    /// True when the expression reads nothing about a particular piece: no Pieces/Both property, no
    /// name(), marker(), has(), side() or map() call, and no runs() of a formula that needs a unit. Bare names of a lookup table or holder() arguments are
    /// not property reads; unknown names count as piece-level.
    /// </summary>
    public static bool IsGameLevel(AstExpr e, RulesPack pack) => Check(e, pack);

    /// <summary>
    /// Every holder(H, P) call in the expression, in text order, as (holder name, property name). A holder() call whose arguments are not
    /// bare names gives (null, null): it cannot be resolved, so callers that mask treat it as reading an unknown holder.
    /// </summary>
    public static List<(string? Holder, string? Property)> HolderReads(AstExpr e)
    {
        var found = new List<(string?, string?)>();
        Walk(e);
        return found;

        void Walk(AstExpr x)
        {
            switch (x)
            {
                case Unary u: Walk(u.Operand); break;
                case Binary b: Walk(b.Left); Walk(b.Right); break;
                case Call c:
                    if (c.Function == "holder")
                        found.Add(c.Args is [VarRef h, VarRef p] ? (h.Name, p.Name) : (null, null));
                    foreach (var a in c.Args) Walk(a);
                    break;
            }
        }
    }

    /// <summary>
    /// The table named by every lookup(Table, ...) call in the expression, in text order (<c>lookuprange</c> is not one: it searches a row's
    /// values, never a piece's own cell). A call whose first argument is not a bare name gives null.
    /// </summary>
    public static List<string?> LookupTables(AstExpr e)
    {
        var found = new List<string?>();
        Walk(e);
        return found;

        void Walk(AstExpr x)
        {
            switch (x)
            {
                case Unary u: Walk(u.Operand); break;
                case Binary b: Walk(b.Left); Walk(b.Right); break;
                case Call c:
                    if (c.Function == "lookup") found.Add(c.Args.Count > 0 && c.Args[0] is VarRef t ? t.Name : null);
                    foreach (var a in c.Args) Walk(a);
                    break;
            }
        }
    }

    private static readonly HashSet<string> WholeGameAggregates = new() { "sum", "countof", "maxof", "minof" };
    private static readonly HashSet<string> HexAggregates = new() { "hexsum", "hexcount", "hexmax", "hexmin" };
    private static readonly HashSet<string> AttachedAggregates = new() { "attachedsum", "attachedcount", "attachedmax", "attachedmin" };

    /// <summary>True for the piece-sum functions: sum, countof, maxof, minof and the hex* ones (lower-case name).</summary>
    public static bool IsAggregate(string function) => WholeGameAggregates.Contains(function) || HexAggregates.Contains(function) || AttachedAggregates.Contains(function);

    /// <summary>True for the aggregates over this piece's hex (hexsum, hexcount, hexmax, hexmin): they need a piece being evaluated.</summary>
    public static bool IsHexAggregate(string function) => HexAggregates.Contains(function);

    /// <summary>True for the aggregates over the pieces attached to this piece (attachedsum, attachedcount, attachedmax, attachedmin): they need a piece being evaluated.</summary>
    public static bool IsAttachedAggregate(string function) => AttachedAggregates.Contains(function);

    /// <summary>True for the aggregates that read from the piece being evaluated (the hex* and attached* ones), so they have no meaning without one.</summary>
    public static bool NeedsPiece(string function) => IsHexAggregate(function) || IsAttachedAggregate(function);

    /// <summary>
    /// The property name (bare first argument, none for countof/hexcount) and the filter text (the text literal argument, null when omitted)
    /// of an aggregate call. The parser guarantees these shapes; for a hand-built call a misplaced argument reads as absent.
    /// </summary>
    public static (string? Property, string? Filter) AggregateArgs(Call c) =>
        (c.Args.Count > 0 ? (c.Args[0] as VarRef)?.Name : null, c.Args.OfType<TextLit>().FirstOrDefault()?.Value);

    /// <summary>
    /// Every aggregate call (see <see cref="IsAggregate"/>) in the expression, in text order, as (function, property, filter) from
    /// <see cref="AggregateArgs"/>. Aggregates nested in another call's arguments are found too; a filter's text is not parsed.
    /// </summary>
    public static List<(string Function, string? Property, string? Filter)> AggregateReads(AstExpr e)
    {
        var found = new List<(string, string?, string?)>();
        Walk(e);
        return found;

        void Walk(AstExpr x)
        {
            switch (x)
            {
                case Unary u: Walk(u.Operand); break;
                case Binary b: Walk(b.Left); Walk(b.Right); break;
                case Call c:
                    if (IsAggregate(c.Function))
                    {
                        var (property, filter) = AggregateArgs(c);
                        found.Add((c.Function, property, filter));
                    }
                    foreach (var a in c.Args) Walk(a);
                    break;
            }
        }
    }

    /// <summary>True for the aggregates that count pieces (countof, hexcount, attachedcount) rather than read a property.</summary>
    public static bool IsCountAggregate(string function) => function is "countof" or "hexcount" or "attachedcount";

    /// <summary>True when the expression calls the named function anywhere.</summary>
    public static bool CallsFunction(AstExpr e, string function) => e switch
    {
        Unary u => CallsFunction(u.Operand, function),
        Binary b => CallsFunction(b.Left, function) || CallsFunction(b.Right, function),
        Call c => c.Function == function || c.Args.Any(a => CallsFunction(a, function)),
        _ => false,
    };

    /// <summary>A formula that needs a unit (a Unit binding or a Unit output), as <c>PieceFilter.FormulasFor</c> tests it.</summary>
    private static bool NeedsUnit(FormulaDef? f) =>
        f != null && ((f.Bindings ?? new()).Any(b => b?.Kind == BindingKind.Unit) || (f.Outputs ?? new()).Any(o => o?.Target == OutputTarget.Unit));

    private static bool Check(AstExpr e, RulesPack pack)
    {
        switch (e)
        {
            case VarRef v:
                var p = pack.PropertyByName(v.Name);
                return p is not null && p.Scope == PropertyScope.Holders;
            case Unary u: return Check(u.Operand, pack);
            case Binary b: return Check(b.Left, pack) && Check(b.Right, pack);
            case Call c:
                if (c.Function is "name" or "marker" or "layer" or "has" or "side" or "map") return false;
                if (NeedsPiece(c.Function)) return false;
                if (IsAggregate(c.Function)) return true;   // the property and filter are read per piece, whatever they name
                if (c.Function == "runs" && c.Args.Count > 0 && c.Args[0] is TextLit id && NeedsUnit(pack.ById<FormulaDef>(id.Value)))
                    return false;   // the runs of a unit formula are counted per counter
                int skip = c.Function is "lookup" or "lookuprange" ? 1 : c.Function == "holder" ? 2 : 0;
                for (int i = skip; i < c.Args.Count; i++)
                    if (!Check(c.Args[i], pack)) return false;
                return true;
            default: return true;
        }
    }
}
