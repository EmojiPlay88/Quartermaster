using Quartermaster.Rules.Expr;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules;

/// <summary>Facts about properties that depend on the pack: whether one is computed and whether every player may see its value.</summary>
public static class PropertyFacts
{
    /// <summary>True when the property is computed from an expression (<see cref="PropertyDef.Expr"/> is set).</summary>
    public static bool IsComputed(PropertyDef p) => !string.IsNullOrWhiteSpace(p.Expr);

    /// <summary>
    /// True when the computed property's expression reads something a player who does not hold every side may not see: a property that is not
    /// <see cref="IsPublic"/> (directly or through another computed one), holder(), any piece aggregate, or lookup() of a table that a property
    /// which is not Public overrides (<see cref="Tables.CellOverrideFacts"/>). An expression that does not parse
    /// reads hidden, and so does a cycle (fail closed).
    /// </summary>
    public static bool ReadsHidden(RulesPack pack, PropertyDef p) => ReadsHidden(pack, p, new HashSet<string>(StringComparer.Ordinal));

    private static bool ReadsHidden(RulesPack pack, PropertyDef p, HashSet<string> path)
    {
        if (!path.Add(p.Id)) return true;   // a cycle
        try
        {
            AstExpr e;
            try { e = Parser.Parse(p.Expr ?? ""); }
            catch (ParseError) { return true; }
            if (ExprFacts.HolderReads(e).Count > 0 || ExprFacts.AggregateReads(e).Count > 0) return true;
            foreach (var v in Parser.Variables(e))
                if (pack.PropertyByName(v) is { } q && !IsPublic(pack, q, path)) return true;
            // lookup() of a table some property overrides reads that property on the piece it is read for. An overrider already being decided
            // (on the path: a computed override reading its own cell reads the printed cell) adds nothing beyond what that decision finds.
            foreach (var t in ExprFacts.LookupTables(e))
                if (t != null && pack.TableByName(t) is { } table
                    && Tables.CellOverrideFacts.Overriders(pack, table.Id).Any(q => !path.Contains(q.Id) && !IsPublic(pack, q, path)))
                    return true;
            return false;
        }
        finally { path.Remove(p.Id); }
    }

    /// <summary>
    /// True when every player sees the property's value: it is Public and, if computed, reads nothing hidden (see <see cref="ReadsHidden(RulesPack, PropertyDef)"/>).
    /// A missing property (null) is not public.
    /// </summary>
    public static bool IsPublic(RulesPack pack, PropertyDef? p) => IsPublic(pack, p, new HashSet<string>(StringComparer.Ordinal));

    private static bool IsPublic(RulesPack pack, PropertyDef? p, HashSet<string> path) =>
        p != null && p.Visibility == Visibility.Public && !(IsComputed(p) && ReadsHidden(pack, p, path));

    /// <summary>
    /// The names of the first cycle through computed properties that starts and ends at <paramref name="p"/> (the closing name is repeated, so A to B to A
    /// gives A, B, A), or null when <paramref name="p"/> is not computed from itself. Expressions that do not parse have no edges.
    /// </summary>
    public static IReadOnlyList<string>? CycleFrom(RulesPack pack, PropertyDef p)
    {
        if (!IsComputed(p)) return null;
        var names = new List<string> { p.Name };
        var onPath = new HashSet<string>(StringComparer.Ordinal) { p.Id };
        return Visit(p) ? names : null;

        bool Visit(PropertyDef q)
        {
            IReadOnlyList<string> vars;
            try { vars = Parser.Variables(Parser.Parse(q.Expr ?? "")); }
            catch (ParseError) { return false; }
            foreach (var v in vars)
            {
                if (pack.PropertyByName(v) is not { } next || !IsComputed(next)) continue;
                if (next.Id == p.Id) { names.Add(next.Name); return true; }
                if (!onPath.Add(next.Id)) continue;
                names.Add(next.Name);
                if (Visit(next)) return true;
                names.RemoveAt(names.Count - 1);
                onPath.Remove(next.Id);
            }
            return false;
        }
    }
}
