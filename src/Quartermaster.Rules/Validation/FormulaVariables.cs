using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules;

/// <summary>The one definition of "used variable", shared by the validator and the Workbench so they cannot drift apart.</summary>
public static class FormulaVariables
{
    /// <summary>
    /// The variables of the formula text in text order, then those of the precondition, then those of the outputs' value and pick-by expressions, then (transitively, only from bindings that are
    /// themselves used) those named in a used binding's Table row/column expression, auto value, "Only when" condition or dice count expression, never counting a
    /// binding's own variable in its own expressions. Empty when the text does not parse; expressions that do not parse are skipped.
    /// </summary>
    public static List<string> Used(FormulaDef f)
    {
        IReadOnlyList<string> textVars;
        try { textVars = Parser.Variables(Parser.Parse(f.Text ?? "")); }
        catch (ParseError) { return new List<string>(); }
        return Reach(f, textVars.Concat(VarsOf(f.Precondition)).Concat(OutputSources(f).SelectMany(VarsOf)));
    }

    /// <summary>The expressions the outputs evaluate, in output order: each non-blank value expression, and the selector of an output with a property list.</summary>
    public static IEnumerable<string> OutputSources(FormulaDef f)
    {
        foreach (var o in f?.Outputs ?? new List<FormulaOutput>())
        {
            if (o == null) continue;
            if (!string.IsNullOrWhiteSpace(o.Expr)) yield return o.Expr!;
            if (o.PropertyChoice is { Count: > 0 } && !string.IsNullOrWhiteSpace(o.PropertySelect)) yield return o.PropertySelect!;   // only a list uses its selector
        }
    }

    /// <summary>The seeds, then every variable reachable from their bindings' <see cref="Sources"/>, in discovery order.</summary>
    public static List<string> Reach(FormulaDef f, IEnumerable<string> seeds)
    {
        var names = new List<string>();
        foreach (var s in seeds) if (!names.Contains(s)) names.Add(s);
        for (var i = 0; i < names.Count; i++)
        {
            var b = f.Bindings.FirstOrDefault(x => x.Variable == names[i]);
            if (b is null) continue;
            foreach (var src in Sources(b))
                foreach (var v in VarsOf(src))
                    if (v != b.Variable && !names.Contains(v)) names.Add(v);
        }
        return names;
    }

    /// <summary>The expressions a resolved binding evaluates: a Table's row and column, the auto value, the "Only when" condition, the dice count expression (non-blank only).</summary>
    public static IEnumerable<string> Sources(VariableBinding b)
    {
        var count = b.Kind == BindingKind.Roll ? b.DiceCountExpr : null;   // only a Roll expands its dice count expression
        var all = b.Kind == BindingKind.Table ? new[] { b.RowExpr, b.ColExpr, b.AutoValue, b.When, count } : new[] { b.AutoValue, b.When, count };
        return all.Where(x => !string.IsNullOrWhiteSpace(x))!;
    }

    private static IReadOnlyList<string> VarsOf(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return Array.Empty<string>();
        try { return Parser.Variables(Parser.Parse(src)); }
        catch (ParseError) { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Every holder(H, P) call (see <see cref="ExprFacts.HolderReads"/>) in the formula text, the precondition, every output's value and pick-by expression, every binding's auto value
    /// and "Only when" condition and dice count expression and every Table binding's row/column expression, used or not. Expressions that do not parse are skipped (they never run).
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
        Add(f?.Precondition);
        foreach (var o in f?.Outputs ?? new List<FormulaOutput>())
        {
            if (o == null) continue;
            Add(o.Expr);
            Add(o.PropertySelect);
        }
        foreach (var b in f?.Bindings ?? new List<VariableBinding>())
        {
            if (b == null) continue;
            Add(b.AutoValue);
            Add(b.When);
            Add(b.DiceCountExpr);
            if (b.Kind == BindingKind.Table) { Add(b.RowExpr); Add(b.ColExpr); }
        }
        return found;
    }
}
