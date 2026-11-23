using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules.Expr;

/// <summary>Static facts about a parsed expression.</summary>
public static class ExprFacts
{
    /// <summary>
    /// True when the expression reads nothing about a particular piece: no Pieces/Both property, and no
    /// name(), marker(), has(), side() or map() call. Bare names of a lookup table or holder() arguments are
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

    /// <summary>True when the expression calls the named function anywhere.</summary>
    public static bool CallsFunction(AstExpr e, string function) => e switch
    {
        Unary u => CallsFunction(u.Operand, function),
        Binary b => CallsFunction(b.Left, function) || CallsFunction(b.Right, function),
        Call c => c.Function == function || c.Args.Any(a => CallsFunction(a, function)),
        _ => false,
    };

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
                if (c.Function is "name" or "marker" or "has" or "side" or "map") return false;
                int skip = c.Function == "lookup" ? 1 : c.Function == "holder" ? 2 : 0;
                for (int i = skip; i < c.Args.Count; i++)
                    if (!Check(c.Args[i], pack)) return false;
                return true;
            default: return true;
        }
    }
}
