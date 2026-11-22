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
