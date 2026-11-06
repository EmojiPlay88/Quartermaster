using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules;

/// <summary>Dropdown choices for an Input box: its own Choices, or the row/column keys of the table it indexes.</summary>
public static class KeyChoices
{
    public static IReadOnlyList<string>? For(RulesPack pack, FormulaDef f, string variable)
    {
        try
        {
            var binding = f.Bindings.FirstOrDefault(b => b.Variable == variable);
            if (binding is not { Kind: BindingKind.Input }) return null;
            if (binding.Choices is { Count: > 0 } own) return own;

            foreach (var b in f.Bindings.Where(b => b.Kind == BindingKind.Table))
            {
                var t = b.TableId == null ? null : pack.ById<TableDef>(b.TableId);
                if (t == null) continue;
                if (IsBare(b.RowExpr, variable)) return Keys(t.RowKeys);
                if (IsBare(b.ColExpr, variable) && t.Kind == TableKind.TwoD) return Keys(t.ColumnKeys);
            }

            AstExpr ast;
            try { ast = Parser.Parse(f.Text ?? ""); }
            catch (ParseError) { return null; }
            return FromLookup(ast, pack, variable, out var found) ? found : null;
        }
        catch (Exception) { return null; }
    }

    private static bool IsBare(string? expr, string variable)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;
        try { return Parser.Parse(expr) is VarRef v && v.Name == variable; }
        catch (ParseError) { return false; }
    }

    /// <summary>True once a lookup using the variable as a bare row/column argument was found (found may still be null for range keys).</summary>
    private static bool FromLookup(AstExpr e, RulesPack pack, string variable, out IReadOnlyList<string>? found)
    {
        found = null;
        switch (e)
        {
            case Unary u: return FromLookup(u.Operand, pack, variable, out found);
            case Binary b: return FromLookup(b.Left, pack, variable, out found) || FromLookup(b.Right, pack, variable, out found);
            case Call c:
                if (c.Function == "lookup" && c.Args.Count >= 2 && c.Args[0] is VarRef tn && pack.TableByName(tn.Name) is { } t)
                {
                    if (c.Args[1] is VarRef r && r.Name == variable) { found = Keys(t.RowKeys); return true; }
                    if (c.Args.Count > 2 && c.Args[2] is VarRef col && col.Name == variable && t.Kind == TableKind.TwoD)
                    { found = Keys(t.ColumnKeys); return true; }
                }
                foreach (var a in c.Args)
                    if (FromLookup(a, pack, variable, out found)) return true;
                return false;
            default: return false;
        }
    }

    private static IReadOnlyList<string>? Keys(List<string> raw)
    {
        var res = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in raw)
        {
            var n = TableLookup.Normalise(k);
            if (n.Length == 0) continue;
            if (TableLookup.ParseKey(n).Kind is TableKeyKind.Range or TableKeyKind.AtLeast or TableKeyKind.AtMost) return null;
            if (seen.Add(n)) res.Add(n);
        }
        return res.Count == 0 ? null : res;
    }
}
