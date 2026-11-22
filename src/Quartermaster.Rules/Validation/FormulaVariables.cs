using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules;

/// <summary>The one definition of "used variable", shared by the validator and the Workbench so they cannot drift apart.</summary>
public static class FormulaVariables
{
    /// <summary>
    /// The variables of the formula text in text order, then (transitively, only from bindings that are themselves used) those named in
    /// a used binding's Table row/column expression or auto value, never counting a binding's own variable in its own expressions.
    /// Empty when the text does not parse; expressions that do not parse are skipped.
    /// </summary>
    public static List<string> Used(FormulaDef f)
    {
        IReadOnlyList<string> textVars;
        try { textVars = Parser.Variables(Parser.Parse(f.Text ?? "")); }
        catch (ParseError) { return new List<string>(); }
        var names = new List<string>(textVars);
        for (var i = 0; i < names.Count; i++)
        {
            var b = f.Bindings.FirstOrDefault(x => x.Variable == names[i]);
            if (b is null) continue;
            var sources = b.Kind == BindingKind.Table ? new[] { b.RowExpr, b.ColExpr, b.AutoValue } : new[] { b.AutoValue };
            foreach (var src in sources)
            {
                if (string.IsNullOrWhiteSpace(src)) continue;
                IReadOnlyList<string> vars;
                try { vars = Parser.Variables(Parser.Parse(src)); }
                catch (ParseError) { continue; }
                foreach (var v in vars)
                    if (v != b.Variable && !names.Contains(v)) names.Add(v);
            }
        }
        return names;
    }

    /// <summary>
    /// Every holder(H, P) call (see <see cref="ExprFacts.HolderReads"/>) in the formula text, every binding's auto value and every Table
    /// binding's row/column expression, used or not. Expressions that do not parse are skipped (they never run).
    /// </summary>
    public static List<(string? Holder, string? Property)> HolderReads(FormulaDef f)
    {
        var found = new List<(string?, string?)>();
        void Add(string? src)
        {
            if (string.IsNullOrWhiteSpace(src)) return;
            try { found.AddRange(ExprFacts.HolderReads(Parser.Parse(src))); }
            catch (ParseError) { }
        }
        Add(f?.Text);
        foreach (var b in f?.Bindings ?? new List<VariableBinding>())
        {
            if (b == null) continue;
            Add(b.AutoValue);
            if (b.Kind == BindingKind.Table) { Add(b.RowExpr); Add(b.ColExpr); }
        }
        return found;
    }
}
