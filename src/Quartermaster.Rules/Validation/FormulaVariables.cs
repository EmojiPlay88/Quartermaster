using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules;

/// <summary>The one definition of "used variable", shared by the validator and the Workbench so they cannot drift apart.</summary>
public static class FormulaVariables
{
    /// <summary>
    /// The variables of the formula text in text order, then those of the precondition, then those of the outputs' value and pick-by expressions, then the Piece variables the outputs act on (<see cref="PieceTargets"/>), then (transitively, only from bindings that are
    /// themselves used) those named in a used binding's Table row/column expression, auto value, "Only when" condition or dice count expression, never counting a
    /// binding's own variable in its own expressions. Empty when the text does not parse; expressions that do not parse are skipped.
    /// </summary>
    public static List<string> Used(FormulaDef f)
    {
        IReadOnlyList<string> textVars;
        try { textVars = Parser.Variables(Parser.Parse(f.Text ?? "")); }
        catch (ParseError) { return new List<string>(); }
        return Reach(f, textVars.Concat(VarsOf(f.Precondition)).Concat(OutputSources(f).SelectMany(VarsOf)).Concat(PieceTargets(f)));
    }

    /// <summary>The Piece variables the outputs act on (<see cref="FormulaOutput.PieceVariable"/> of each Target = Piece output naming a Piece binding), in output order: the run asks them even when nothing reads their value.</summary>
    public static IEnumerable<string> PieceTargets(FormulaDef f)
    {
        foreach (var o in f?.Outputs ?? new List<FormulaOutput>())
            if (o is { Target: OutputTarget.Piece } && !string.IsNullOrWhiteSpace(o.PieceVariable)
                && f!.Bindings.Any(b => b != null && b.Variable == o.PieceVariable && b.Kind == BindingKind.Piece))
                yield return o.PieceVariable!;
    }

    /// <summary>The expressions the outputs evaluate, in output order: each non-blank value expression, the selector of an output with a property list, each non-blank key, and each non-blank margin.</summary>
    public static IEnumerable<string> OutputSources(FormulaDef f)
    {
        foreach (var o in f?.Outputs ?? new List<FormulaOutput>())
        {
            if (o == null) continue;
            if (!string.IsNullOrWhiteSpace(o.Expr)) yield return o.Expr!;
            if (o.PropertyChoice is { Count: > 0 } && !string.IsNullOrWhiteSpace(o.PropertySelect)) yield return o.PropertySelect!;   // only a list uses its selector
            if (!string.IsNullOrWhiteSpace(o.Key)) yield return o.Key!;
            if (!string.IsNullOrWhiteSpace(o.Margin)) yield return o.Margin!;
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
    /// The formula text, the precondition, every output's value and pick-by expression, every binding's auto value and "Only when" condition
    /// and dice count expression and every Table binding's row/column expression, used or not: every expression a run may evaluate.
    /// </summary>
    private static IEnumerable<string?> ExpressionSources(FormulaDef? f)
    {
        yield return f?.Text;
        yield return f?.Precondition;
        foreach (var o in f?.Outputs ?? new List<FormulaOutput>())
        {
            if (o == null) continue;
            yield return o.Expr;
            if (o.PropertyChoice is { Count: > 0 }) yield return o.PropertySelect;   // only a list uses its selector, as OutputSources
            yield return o.Key;
            yield return o.Margin;
        }
        foreach (var b in f?.Bindings ?? new List<VariableBinding>())
        {
            if (b == null) continue;
            yield return b.AutoValue;
            yield return b.When;
            yield return b.DiceCountExpr;
            if (b.Kind == BindingKind.Table) { yield return b.RowExpr; yield return b.ColExpr; }
        }
    }

    /// <summary>Collects <paramref name="facts"/> over every parsing expression of the formula (<see cref="ExpressionSources"/>); those that do not parse are skipped (they never run).</summary>
    private static List<T> Collect<T>(FormulaDef? f, Func<Quartermaster.Rules.Expr.Expr, List<T>> facts)
    {
        var found = new List<T>();
        foreach (var src in ExpressionSources(f))
        {
            if (string.IsNullOrWhiteSpace(src)) continue;
            try { found.AddRange(facts(Parser.Parse(src))); }
            catch (ParseError) { }
        }
        return found;
    }

    /// <summary>
    /// Every holder(H, P) call (see <see cref="ExprFacts.HolderReads"/>) in the formula text, the precondition, every output's value and pick-by expression, every binding's auto value
    /// and "Only when" condition and dice count expression and every Table binding's row/column expression, used or not. Expressions that do not parse are skipped (they never run).
    /// </summary>
    public static List<(string? Holder, string? Property)> HolderReads(FormulaDef f) => Collect(f, ExprFacts.HolderReads);

    /// <summary>
    /// Every keyed() call (see <see cref="ExprFacts.KeyedReads"/>) as (property name, holder name or null, key expression), from the same expressions as
    /// <see cref="HolderReads(FormulaDef)"/> (outputs' keys included). Expressions that do not parse are skipped (they never run).
    /// </summary>
    public static List<(string Property, string? Holder, Quartermaster.Rules.Expr.Expr Key)> KeyedReads(FormulaDef f) => Collect(f, ExprFacts.KeyedReads);

    /// <summary>
    /// The tables read by lookup(Table, ...) in any expression of the formula (see <see cref="ExprFacts.LookupTables"/>), each once, in the
    /// order of <see cref="HolderReads(FormulaDef)"/>; null for a call whose table is not a bare name. A Table binding's own table is not listed.
    /// </summary>
    public static List<string?> TablesRead(FormulaDef f) => Collect(f, ExprFacts.LookupTables).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Every piece aggregate call (see <see cref="ExprFacts.AggregateReads"/>) as (function, property, filter), from the same expressions as
    /// <see cref="HolderReads(FormulaDef)"/>. Expressions that do not parse are skipped (they never run).
    /// </summary>
    public static List<(string Function, string? Property, string? Filter)> AggregateReads(FormulaDef f) => Collect(f, ExprFacts.AggregateReads);

    /// <summary>
    /// True for a computed property whose expression reads data a viewer who does not hold every side may not see
    /// (<see cref="PropertyFacts.ReadsHidden(RulesPack, PropertyDef)"/>): its value reveals that data wherever the property is read, on the
    /// viewer's own piece and on the global holder too. A Private computed property over public inputs is not one (it is hidden only where a
    /// stored Private value would be).
    /// </summary>
    public static bool ComputedFromHidden(RulesPack pack, PropertyDef? p) => p != null && PropertyFacts.IsComputed(p) && PropertyFacts.ReadsHidden(pack, p);

    /// <summary>
    /// The variables of the formula whose value is a computed property read from hidden data (<see cref="ComputedFromHidden"/>): every Unit,
    /// Global or Holder binding of one, used or not, and every variable named like one in any expression of the formula (as
    /// <see cref="HolderReads(FormulaDef)"/> scans them). Depends on the pack alone, so the host and a guest agree.
    /// </summary>
    public static List<string> ComputedHiddenVariables(RulesPack pack, FormulaDef f)
    {
        var names = new List<string>();
        void Add(string? v) { if (!string.IsNullOrEmpty(v) && !names.Contains(v)) names.Add(v); }
        PropertyDef? ById(string? id) => id == null ? null : (pack.Properties ?? new List<PropertyDef>()).FirstOrDefault(p => p != null && p.Id == id);
        foreach (var b in f?.Bindings ?? new List<VariableBinding>())
            if (b is { Kind: BindingKind.Unit or BindingKind.Global or BindingKind.Holder } && ComputedFromHidden(pack, ById(b.Property)))
                Add(b.Variable);
        foreach (var v in Collect(f, e => Parser.Variables(e).ToList()))
            if (ComputedFromHidden(pack, pack.PropertyByName(v))) Add(v);
        return names;
    }

    /// <summary>
    /// The variables of the formula's Piece bindings whose property is not Public (<see cref="PropertyFacts.IsPublic"/>; an unknown property counts as not public):
    /// a hidden input for a viewer who does not hold every side, like a hidden computed read. Depends on the pack alone, so the host and a guest agree.
    /// </summary>
    public static IEnumerable<string> PieceBindingsHidden(RulesPack pack, FormulaDef f) => PieceBindingsHidden(pack, f, false);

    /// <summary>
    /// As <see cref="PieceBindingsHidden(RulesPack, FormulaDef)"/>, and with <paramref name="piecesMayHide"/> (the viewer's view may lack pieces
    /// or values: <c>PieceAccess.PiecesMayHide</c>, from the pack and module, so host and guest agree) every Piece binding: a masked pick of a
    /// Public property is read on the full state, where the viewer's own view has no value. Same rule as the piece aggregates.
    /// </summary>
    public static IEnumerable<string> PieceBindingsHidden(RulesPack pack, FormulaDef f, bool piecesMayHide)
    {
        if (piecesMayHide)
        {
            foreach (var b in f?.Bindings ?? new List<VariableBinding>())
                if (b is { Kind: BindingKind.Piece }) yield return b.Variable;
            yield break;
        }
        var props = pack.Properties ?? new List<PropertyDef>();
        foreach (var b in f?.Bindings ?? new List<VariableBinding>())
            if (b is { Kind: BindingKind.Piece } && !PropertyFacts.IsPublic(pack, b.Property == null ? null : props.FirstOrDefault(p => p != null && p.Id == b.Property)))
                yield return b.Variable;
    }

    /// <summary>
    /// True when the formula reads a computed property built from hidden data: through a binding or variable (<see cref="ComputedHiddenVariables"/>)
    /// or through holder(H, P) of one anywhere in its expressions. Such a formula's working may show that data to any viewer who does not
    /// hold every side.
    /// </summary>
    public static bool ComputedReadsHidden(RulesPack pack, FormulaDef f) =>
        ComputedHiddenVariables(pack, f).Count > 0 || ComputedHolderCallHidden(pack, f);

    /// <summary>True when a holder(H, P) call anywhere in the formula reads a computed property built from hidden data.</summary>
    public static bool ComputedHolderCallHidden(RulesPack pack, FormulaDef f) =>
        HolderReads(f).Any(r => r.Property != null && ComputedFromHidden(pack, pack.PropertyByName(r.Property)));

    /// <summary>
    /// True when an aggregate in the formula may read data a viewer who does not hold every side cannot see (see <see cref="AggregateCallHidden"/>).
    /// Depends on the pack and <paramref name="piecesMayHide"/> alone, so the host and a guest agree.
    /// </summary>
    public static bool AggregateReadsHidden(RulesPack pack, FormulaDef f, bool piecesMayHide) =>
        AggregateReads(f).Any(r => AggregateCallHidden(pack, r.Function, r.Property, r.Filter, piecesMayHide));

    /// <summary>
    /// True when one aggregate call may read data a viewer who does not hold every side cannot see (aggregates read every piece, hidden or not):
    /// any aggregate when <paramref name="piecesMayHide"/> (the viewer's view may lack pieces or their values: a map owned by a side it does not
    /// hold, a Hideable or Obscurable trait), any count (countof, hexcount), an aggregate over a property that is not Public (or unknown), or
    /// one whose filter reads a property that is not Public, calls holder() or another aggregate, looks up a table a non-Public property
    /// overrides (<see cref="LookupHidden"/>), reads a keyed() value that is not Public or names a holder (B6), or does not parse. Otherwise (a Public
    /// property, with no filter, "", or a filter over Public properties and piece facts) it is not hidden.
    /// </summary>
    public static bool AggregateCallHidden(RulesPack pack, string function, string? property, string? filter, bool piecesMayHide)
    {
        if (piecesMayHide || ExprFacts.IsCountAggregate(function)) return true;
        if (property == null || !PropertyFacts.IsPublic(pack, pack.PropertyByName(property))) return true;
        if (string.IsNullOrWhiteSpace(filter)) return false;   // "" matches every piece
        Quartermaster.Rules.Expr.Expr e;
        try { e = Parser.Parse(filter); }
        catch (ParseError) { return true; }   // fail closed
        return ExprFacts.HolderReads(e).Count > 0 || ExprFacts.AggregateReads(e).Count > 0
            || Parser.Variables(e).Any(v => pack.PropertyByName(v) is { } p && !PropertyFacts.IsPublic(pack, p))
            || ExprFacts.LookupTables(e).Any(t => LookupHidden(pack, t))
            // B6: keyed(Property, key[, Holder]) reads its property by name, not as a variable
            || ExprFacts.KeyedReads(e).Any(k => k.Holder != null || !PropertyFacts.IsPublic(pack, pack.PropertyByName(k.Property)));
    }

    /// <summary>
    /// True when lookup() of the named table may read a non-Public property's value instead of a printed cell
    /// (<see cref="Tables.CellOverrideFacts.HiddenOverride"/>). A call whose table is not a bare name (null) fails closed when any table has one.
    /// </summary>
    public static bool LookupHidden(RulesPack pack, string? tableName)
    {
        if (tableName == null) return (pack.Tables ?? new List<TableDef>()).Any(t => t != null && Tables.CellOverrideFacts.HiddenOverride(pack, t.Id));
        return pack.TableByName(tableName) is { } t2 && Tables.CellOverrideFacts.HiddenOverride(pack, t2.Id);
    }
}
