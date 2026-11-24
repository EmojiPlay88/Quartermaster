using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules;

public enum RulesIssueSeverity { Error, Warning }

public sealed record RulesIssue(RulesIssueSeverity Severity, string EntityId, string EntityName, string Message);

/// <summary>Checks a rules pack for problems. Never throws on bad user data.</summary>
public static class RulesValidator
{
    public static IReadOnlyList<RulesIssue> Validate(RulesPack pack, IReadOnlyCollection<string>? knownMapIds = null)
    {
        var issues = new List<RulesIssue>();
        try
        {
            Duplicates(pack.Properties.Select(p => (p.Id, p.Name)), "Property", issues);
            Duplicates(pack.Holders.Select(p => (p.Id, p.Name)), "Holder", issues);
            Duplicates(pack.Tables.Select(p => (p.Id, p.Name)), "Table", issues);
            Duplicates(pack.Formulas.Select(p => (p.Id, p.Name)), "Formula", issues);

            foreach (var p in pack.Properties)
                if (!string.IsNullOrWhiteSpace(p.AppliesTo))
                {
                    if (ParseProblem(p.AppliesTo!) is { } pe)
                        Add(issues, RulesIssueSeverity.Error, p.Id, p.Name, $"Property \"{p.Name}\": applies-to {pe}");
                    else
                        foreach (var u in FilterRefProblems(p.AppliesTo!, pack))
                            Add(issues, RulesIssueSeverity.Error, p.Id, p.Name, $"Property \"{p.Name}\": applies-to {u}");
                    // Seeding evaluates applies-to without the module's grid, so there is no hex to read.
                    foreach (var fn in PieceBoundAggregatesIn(p.AppliesTo))
                        Add(issues, RulesIssueSeverity.Error, p.Id, p.Name, $"Property \"{p.Name}\": {fn}() is not available in a property's applies-to");
                }
            foreach (var p in pack.Properties) CheckComputed(pack, p, issues);
            CheckComputedCycles(pack, issues);
            CheckOverrides(pack, issues);
            foreach (var t in pack.Tables) CheckTable(pack, t, issues);
            foreach (var f in pack.Formulas) Guarded(issues, f.Id, f.Name, () => CheckFormula(pack, f, issues));
            foreach (var s in pack.Sides) Guarded(issues, s.Id, s.Name, () => CheckSide(s, pack, issues));
            foreach (var ph in pack.Phases) Guarded(issues, ph.Id, ph.Name, () => CheckPhase(pack, ph, issues));
            CheckBlocks(pack, issues);
            CheckAlerts(pack, issues);
            CheckOwners(pack, knownMapIds, issues);
            CheckRuleRefPaths(pack, issues);
        }
        catch (Exception ex)
        {
            Add(issues, RulesIssueSeverity.Error, pack.Id, pack.Name, $"Validation failed unexpectedly: {ex.Message}");
        }
        return issues;
    }

    private static void CheckComputed(RulesPack pack, PropertyDef p, List<RulesIssue> issues)
    {
        if (!PropertyFacts.IsComputed(p)) return;
        if (ParseProblem(p.Expr!) is { } pe)
        {
            Add(issues, RulesIssueSeverity.Error, p.Id, p.Name, $"Property \"{p.Name}\": the expression does not parse: {pe}");
            return;
        }
        foreach (var u in FilterRefProblems(p.Expr!, pack))   // unknown property, table and holder names, as an applies-to is checked
            Add(issues, RulesIssueSeverity.Error, p.Id, p.Name, $"Property \"{p.Name}\": {u}");
        if (p.Default != 0 || !string.IsNullOrWhiteSpace(p.SheetField))
            Add(issues, RulesIssueSeverity.Warning, p.Id, p.Name, $"Property \"{p.Name}\" is computed; its Default and Sheet field are ignored");
        if (p.Visibility == Visibility.Public && PropertyFacts.ReadsHidden(pack, p))
            Add(issues, RulesIssueSeverity.Warning, p.Id, p.Name,
                $"Property \"{p.Name}\" is Public but is computed from hidden data; players who do not hold every side see it as Private");
    }

    private static void CheckOverrides(RulesPack pack, List<RulesIssue> issues)
    {
        foreach (var p in pack.Properties)
        {
            if (string.IsNullOrWhiteSpace(p.OverridesTable)) continue;
            if (p.Scope == PropertyScope.Holders)
                Add(issues, RulesIssueSeverity.Error, p.Id, p.Name, $"Property \"{p.Name}\" overrides a table but is not a piece property");
            if (pack.ById<TableDef>(p.OverridesTable!) is not { } t)
            {
                Add(issues, RulesIssueSeverity.Error, p.Id, p.Name, $"Property \"{p.Name}\" overrides a table that no longer exists");
                continue;
            }
            if (t.Kind == TableKind.TwoD)
            {
                var c = TableLookup.Normalise(p.OverridesColumn);
                if (!t.ColumnKeys.Any(k => TableLookup.Normalise(k) == c))
                    Add(issues, RulesIssueSeverity.Error, p.Id, p.Name, $"Property \"{p.Name}\" overrides column \"{p.OverridesColumn}\", which table \"{t.Name}\" does not have");
            }
        }
        var seen = new HashSet<(string, string)>();
        foreach (var p in pack.Properties)
        {
            if (string.IsNullOrWhiteSpace(p.OverridesTable) || pack.ById<TableDef>(p.OverridesTable!) is not { } t) continue;
            var oneD = t.Kind != TableKind.TwoD;
            var col = oneD ? "" : TableLookup.Normalise(p.OverridesColumn);
            if (!seen.Add((t.Id, col))) continue;
            var all = CellOverrideFacts.For(pack, t.Id, col);
            if (all.Count < 2) continue;
            var (a, b) = (all[0], all[1]);
            Add(issues, RulesIssueSeverity.Warning, b.Id, b.Name, oneD
                ? $"Properties \"{a.Name}\" and \"{b.Name}\" both override {t.Name}; \"{a.Name}\" wins"
                : $"Properties \"{a.Name}\" and \"{b.Name}\" both override {t.Name}, column {col}; \"{a.Name}\" wins");
        }
    }

    /// <summary>Each distinct cycle once, on the first property of it in pack order.</summary>
    private static void CheckComputedCycles(RulesPack pack, List<RulesIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pack.Properties)
        {
            if (PropertyFacts.CycleFrom(pack, p) is not { } cycle) continue;
            if (!seen.Add(string.Join("|", cycle.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)))) continue;
            Add(issues, RulesIssueSeverity.Error, p.Id, p.Name, $"Property \"{p.Name}\" is computed from itself: {string.Join(" → ", cycle)}");
        }
    }

    /// <summary>Problems in an applies-to/filter expression, as message tails: property names (variables and has() arguments) that no property has,
    /// and lookup() table names that no table has. The table name of a lookup is not a property; its other arguments are walked as usual.</summary>
    private static IEnumerable<string> FilterRefProblems(string text, RulesPack pack)
    {
        var found = new List<string>();
        try { Walk(Parser.Parse(text)); }
        catch (ParseError) { }
        return found.Distinct(StringComparer.Ordinal);

        void Prop(string n) { if (pack.PropertyByName(n) is null) found.Add(UnknownPropertyTail(n)); }
        void Walk(AstExpr e)
        {
            switch (e)
            {
                case VarRef v: Prop(v.Name); break;
                case Unary u: Walk(u.Operand); break;
                case Binary b: Walk(b.Left); Walk(b.Right); break;
                case Call c:
                    if (c.Function == "has" && c.Args.Count == 1 && c.Args[0] is TextLit t) Prop(t.Value);
                    else if (c.Function == "has" && c.Args.Count == 1 && c.Args[0] is VarRef hv) Prop(hv.Name);
                    else if (c.Function is "lookup" or "lookuprange" && c.Args.Count > 0 && c.Args[0] is VarRef tv)
                    {
                        if (pack.TableByName(tv.Name) is null) found.Add($"refers to unknown table \"{tv.Name}\"");
                        foreach (var a in c.Args.Skip(1)) Walk(a);
                    }
                    else if (c.Function == "holder" && c.Args is [VarRef hn, VarRef hp]) found.AddRange(HolderCallProblems(hn.Name, hp.Name, pack));
                    else if (ExprFacts.IsAggregate(c.Function)) found.AddRange(AggregateProblems(c, pack));
                    else if (c.Function != "holder") foreach (var a in c.Args) Walk(a);
                    break;
            }
        }
    }

    /// <summary>Problems in a holder(H, P) call, as message tails: an unknown holder, an unknown property, a property that is not a holder property.</summary>
    private static IEnumerable<string> HolderCallProblems(string holder, string property, RulesPack pack)
    {
        if (pack.HolderByName(holder) is null) yield return $"refers to unknown holder \"{holder}\"";
        var prop = pack.PropertyByName(property);
        if (prop is null) yield return UnknownPropertyTail(property);
        else if (prop.Scope == PropertyScope.Pieces) yield return $"refers to \"{prop.Name}\", which is not a holder property";
    }

    /// <summary>
    /// Problems in a piece-sum call, as message tails: a property that does not exist, a filter that does not parse (the same parse the unit filters get),
    /// a filter that names an unknown property or table. The filter is a separate expression run per piece, so its names are not the formula's variables.
    /// </summary>
    private static IEnumerable<string> AggregateProblems(Call c, RulesPack pack)
    {
        var (property, filter) = ExprFacts.AggregateArgs(c);
        var prop = property is null ? null : pack.PropertyByName(property);
        if (property is not null && prop is null) yield return $"{c.Function}() names unknown property \"{property}\"";
        else if (prop is { Scope: PropertyScope.Holders }) yield return $"{c.Function}() needs a piece property, not \"{property}\"";
        if (string.IsNullOrWhiteSpace(filter)) yield break;
        if (ParseProblem(filter) is { } pe) yield return $"the filter of {c.Function}() does not parse: {pe}";
        else foreach (var u in FilterRefProblems(filter, pack)) yield return $"the filter of {c.Function}() {u}";
    }

    private static string UnknownPropertyTail(string name) => $"refers to unknown property \"{name}\"";

    /// <summary>
    /// The aggregates that need a piece (hexsum(), attachedsum() ...) an expression text calls, also inside the filters of its aggregate calls, distinct in text order. Empty when
    /// the text does not parse (that is reported elsewhere).
    /// </summary>
    private static List<string> PieceBoundAggregatesIn(string? text)
    {
        var found = new List<string>();
        Scan(text);
        return found.Distinct(StringComparer.Ordinal).ToList();

        void Scan(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return;
            try { Walk(Parser.Parse(t)); }
            catch (ParseError) { }
        }
        void Walk(AstExpr e)
        {
            switch (e)
            {
                case Unary u: Walk(u.Operand); break;
                case Binary b: Walk(b.Left); Walk(b.Right); break;
                case Call c:
                    if (ExprFacts.NeedsPiece(c.Function)) found.Add(c.Function);
                    if (ExprFacts.IsAggregate(c.Function)) Scan(ExprFacts.AggregateArgs(c).Filter);
                    foreach (var a in c.Args) Walk(a);
                    break;
            }
        }
    }

    /// <summary>True when the expression text parses and contains a side() call.</summary>
    private static bool UsesSide(string text)
    {
        try { return Has(Parser.Parse(text)); }
        catch (ParseError) { return false; }

        static bool Has(AstExpr e) => e switch
        {
            Unary u => Has(u.Operand),
            Binary b => Has(b.Left) || Has(b.Right),
            Call c => c.Function == "side" || c.Args.Any(Has),
            _ => false,
        };
    }

    private static void Guarded(List<RulesIssue> issues, string id, string name, Action check)
    {
        try { check(); }
        catch (Exception ex) { Add(issues, RulesIssueSeverity.Error, id, name, $"Validation of \"{name}\" failed unexpectedly: {ex.Message}"); }
    }

    // ---- phases ----

    /// <summary>Checks an expression: parse error, else unknown property names. Message is "{who}: {what} {problem}".</summary>
    private static void CheckPhaseExpr(RulesPack pack, string? text, string who, string what, Action<string> err)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (ParseProblem(text!) is { } pe) err($"{who}: {what} {pe}");
        else foreach (var u in FilterRefProblems(text!, pack)) err($"{who}: {what} {u}");
    }

    private static void CheckSide(SideDef s, RulesPack pack, List<RulesIssue> issues)
    {
        // An empty filter matches every unit; with several sides that side owns every piece it comes before (ownership is first match).
        if (string.IsNullOrWhiteSpace(s.UnitFilter) && pack.Sides.Count > 1)
            Add(issues, RulesIssueSeverity.Warning, s.Id, s.Name, $"Side \"{s.Name}\": unit filter is empty, so it matches every unit");
        CheckPhaseExpr(pack, s.UnitFilter, $"Side \"{s.Name}\"", "unit filter",
            m => Add(issues, RulesIssueSeverity.Error, s.Id, s.Name, m));
        // A side's filter decides who owns a piece, so it cannot depend on the owning side.
        if (!string.IsNullOrWhiteSpace(s.UnitFilter) && UsesSide(s.UnitFilter!))
            Add(issues, RulesIssueSeverity.Error, s.Id, s.Name, $"Side \"{s.Name}\": unit filter may not use side()");
        // A player's view strips Private values, so there a filter reading one cannot place another side's pieces; a computed property that is
        // Private in effect is hidden only on pieces of a side the viewer does not hold, so it would show on those pieces.
        if (!string.IsNullOrWhiteSpace(s.UnitFilter) && ParseProblem(s.UnitFilter!) == null
            && pack.Properties.Any(p => p != null && PropertyFacts.IsComputed(p) && !PropertyFacts.IsPublic(pack, p)))
            foreach (var v in Parser.Variables(Parser.Parse(s.UnitFilter!)).Distinct(StringComparer.Ordinal))
                if (pack.PropertyByName(v) is { } read && !PropertyFacts.IsPublic(pack, read))
                    Add(issues, RulesIssueSeverity.Error, s.Id, s.Name,
                        $"Side \"{s.Name}\": unit filter may not read Private property \"{read.Name}\" while the pack has Private computed properties (players' views could not tell whose pieces they hide)");
        // Ownership evaluates side filters without the module's grid, so there is no hex to read.
        foreach (var fn in PieceBoundAggregatesIn(s.UnitFilter))
            Add(issues, RulesIssueSeverity.Error, s.Id, s.Name, $"Side \"{s.Name}\": {fn}() is not available in a side's unit filter");
    }

    private static void CheckPhase(RulesPack pack, PhaseDef ph, List<RulesIssue> issues)
    {
        string who = $"Phase \"{ph.Name}\"";
        void Err(string m) => Add(issues, RulesIssueSeverity.Error, ph.Id, ph.Name, m);

        if (!string.IsNullOrEmpty(ph.SideId) && pack.ById<SideDef>(ph.SideId!) is null)
            Err($"{who}: side \"{ph.SideId}\" does not exist");
        if (!string.IsNullOrEmpty(ph.RuleRefId) && pack.ById<RuleRef>(ph.RuleRefId!) is null)
            Err($"{who}: rule reference \"{ph.RuleRefId}\" does not exist");

        CheckRunEntries(pack, ph, ph.OnStart, "start", issues);
        CheckRunEntries(pack, ph, ph.OnEnd ?? new(), "end", issues);
        foreach (var e in ph.OnMove)
        {
            var f = pack.ById<FormulaDef>(e.FormulaId);
            if (f is null) Err($"{who}: formula \"{e.FormulaId}\" does not exist");
            else if (!f.Bindings.Any(b => b.Kind is BindingKind.Moved or BindingKind.Input or BindingKind.Unit
                                              or BindingKind.Global or BindingKind.Holder))
                Add(issues, RulesIssueSeverity.Warning, ph.Id, ph.Name, $"{who}: on-move formula \"{f.Name}\" has no Moved binding");
            CheckPhaseExpr(pack, e.UnitFilter, who, "on-move filter", Err);
            WarnOutsidePhases(ph, f, "move", issues);
        }
        CheckPhaseCondition(pack, ph, issues);
        foreach (var w in ph.Warnings)
        {
            CheckPhaseExpr(pack, w.Condition, who, "warning condition", Err);
            if (string.IsNullOrWhiteSpace(w.Message)) Err($"{who}: warning message is empty");
            else if (ShowsOnceWithUnit(pack, w.Condition, w.Message))
                Add(issues, RulesIssueSeverity.Warning, ph.Id, ph.Name, $"{who}: warning {ShowsOnceText}");
        }
    }

    /// <summary>OnStart / OnEnd entries: the formula exists, the unit filter is sound, the formula is allowed in this phase. <paramref name="kind"/> is "start" or "end".</summary>
    private static void CheckRunEntries(RulesPack pack, PhaseDef ph, List<StartEntry> entries, string kind, List<RulesIssue> issues)
    {
        string who = $"Phase \"{ph.Name}\"";
        void Err(string m) => Add(issues, RulesIssueSeverity.Error, ph.Id, ph.Name, m);
        foreach (var e in entries)
        {
            var f = pack.ById<FormulaDef>(e.FormulaId);
            if (f is null) Err($"{who}: formula \"{e.FormulaId}\" does not exist");
            CheckPhaseExpr(pack, e.UnitFilter, who, $"on-{kind} filter", Err);
            WarnOutsidePhases(ph, f, kind, issues);
        }
    }

    private static void WarnOutsidePhases(PhaseDef ph, FormulaDef? f, string kind, List<RulesIssue> issues)
    {
        if (f?.Phases is { Count: > 0 } allowed && !allowed.Contains(ph.Id))
            Add(issues, RulesIssueSeverity.Warning, ph.Id, ph.Name, $"Phase \"{ph.Name}\": {kind} entry runs \"{f.Name}\", which is limited to other phases");
    }

    /// <summary>The tail of the notice for a game-level warning or alert whose message names {unit}.</summary>
    internal const string ShowsOnceText = "shows once (its condition never reads the unit), so {unit} is left out";

    /// <summary>
    /// True when the condition parses and is game-level (<see cref="ExprFacts.IsGameLevel"/>: it is shown once, not once per unit) while
    /// the message uses {unit}, which is then left out.
    /// </summary>
    private static bool ShowsOnceWithUnit(RulesPack pack, string? condition, string? message)
    {
        if (string.IsNullOrWhiteSpace(condition) || message?.Contains("{unit}", StringComparison.Ordinal) != true) return false;
        try { return ExprFacts.IsGameLevel(Parser.Parse(condition), pack); }
        catch (ParseError) { return false; }
    }

    private static readonly string[] ForbiddenConditionFunctions = { "name", "marker", "has", "dice", "side", "map", "holder", "hexsum", "hexcount", "hexmax", "hexmin", "attachedsum", "attachedcount", "attachedmax", "attachedmin" };

    /// <summary>A phase's run condition reads only global properties, tables, turn()/stage(), constants and pure functions.</summary>
    private static void CheckPhaseCondition(RulesPack pack, PhaseDef ph, List<RulesIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(ph.Condition)) return;
        string who = $"Phase \"{ph.Name}\"";
        void Err(string m) => Add(issues, RulesIssueSeverity.Error, ph.Id, ph.Name, m);

        AstExpr ast;
        try { ast = Parser.Parse(ph.Condition!); }
        catch (ParseError e) { Err($"{who}: condition {e.Message}"); return; }

        var vars = new List<string>();
        var forbidden = new List<string>();
        bool dynamic = false;   // reads something that can change: turn(), stage() or a table
        var unknownTables = new List<string>();
        var aggregateProblems = new List<string>();
        Walk(ast);

        foreach (var fn in forbidden.Distinct()) Err($"{who}: condition may not use {fn}()");
        foreach (var t in unknownTables.Distinct(StringComparer.Ordinal)) Err($"{who}: condition refers to unknown table \"{t}\"");
        foreach (var m in aggregateProblems.Distinct(StringComparer.Ordinal)) Err($"{who}: condition {m}");
        foreach (var v in vars.Distinct(StringComparer.Ordinal))
        {
            var p = pack.PropertyByName(v);
            if (p is null || p.Scope is not (PropertyScope.Holders or PropertyScope.Both))
                Err($"{who}: condition uses \"{v}\", which is not a global property");
        }

        if (vars.Count == 0 && !dynamic && forbidden.Count == 0)
        {
            try
            {
                var r = Evaluator.Evaluate(ast, new ConstantContext());
                if (r.Kind != ValueKind.Text && !r.IsTruthy)
                    Add(issues, RulesIssueSeverity.Warning, ph.Id, ph.Name, $"{who} never runs (its condition is always false)");
            }
            catch (EvalError) { }
        }

        void Walk(AstExpr e)
        {
            switch (e)
            {
                case VarRef v: vars.Add(v.Name); break;
                case Unary u: Walk(u.Operand); break;
                case Binary b: Walk(b.Left); Walk(b.Right); break;
                case Call c:
                    if (ForbiddenConditionFunctions.Contains(c.Function)) forbidden.Add(c.Function);
                    if (c.Function is "turn" or "stage" or "lookup" or "lookuprange" || ExprFacts.IsAggregate(c.Function)) dynamic = true;
                    if (ExprFacts.IsAggregate(c.Function)) { aggregateProblems.AddRange(AggregateProblems(c, pack)); break; }
                    if (c.Function is "lookup" or "lookuprange" && c.Args.Count > 0 && c.Args[0] is VarRef tv)
                    {
                        if (pack.TableByName(tv.Name) is null) unknownTables.Add(tv.Name);
                        foreach (var a in c.Args.Skip(1)) Walk(a);
                    }
                    else if (c.Function != "holder") foreach (var a in c.Args) Walk(a);
                    break;
            }
        }
    }

    /// <summary>Context for evaluating a condition that reads nothing: any variable or table read is an <see cref="EvalError"/>.</summary>
    private sealed class ConstantContext : IEvalContext
    {
        public Value GetVariable(string name) => throw new EvalError($"No value for {name}");
        public Value Lookup(string tableName, Value row, Value? col) => throw new EvalError("No tables here");
        public Value LookupRange(string table, Value row, Value value) => throw new EvalError("No tables here");
        public IReadOnlyList<int> RollDice(int n, int sides) => throw new EvalError("No dice here");
        public bool Has(string property) => throw new EvalError("No pieces here");
        public Value PieceName() => throw new EvalError("No pieces here");
        public Value Marker(string key) => throw new EvalError("No pieces here");
    }

    private static void CheckOwners(RulesPack pack, IReadOnlyCollection<string>? knownMapIds, List<RulesIssue> issues)
    {
        foreach (var h in pack.Holders)
            if (!string.IsNullOrEmpty(h.SideId) && pack.ById<SideDef>(h.SideId!) is null)
                Add(issues, RulesIssueSeverity.Error, h.Id, h.Name, $"Holder \"{h.Name}\": side \"{h.SideId}\" does not exist");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in pack.MapOwners)
            Guarded(issues, m.MapId, m.MapId, () =>
            {
                void Err(string msg) => Add(issues, RulesIssueSeverity.Error, m.MapId, m.MapId, msg);
                string who = $"Map owner \"{m.MapId}\"";
                if (pack.ById<SideDef>(m.SideId) is null) Err($"{who}: side \"{m.SideId}\" does not exist");
                if (knownMapIds != null && !knownMapIds.Contains(m.MapId)) Err($"{who}: map does not exist in the module");
                if (!seen.Add(m.MapId)) Err($"{who}: map has more than one owner");
            });
    }

    private static void CheckAlerts(RulesPack pack, List<RulesIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in pack.Alerts)
            Guarded(issues, a.Id, a.Name, () =>
            {
                string who = $"Alert \"{a.Name}\"";
                void Err(string m) => Add(issues, RulesIssueSeverity.Error, a.Id, a.Name, m);
                if (!ids.Add(a.Id)) Err($"{who}: duplicate id \"{a.Id}\"");
                if (string.IsNullOrWhiteSpace(a.Name)) Err("Alert has no name");
                else if (!names.Add(a.Name)) Err($"Alert \"{a.Name}\": another alert already has this name");
                if (string.IsNullOrWhiteSpace(a.Condition)) Err($"{who}: condition is empty");
                else CheckPhaseExpr(pack, a.Condition, who, "condition", Err);
                if (ShowsOnceWithUnit(pack, a.Condition, a.Message))
                    Add(issues, RulesIssueSeverity.Warning, a.Id, a.Name, $"{who}: {ShowsOnceText}");
                if (!string.IsNullOrEmpty(a.SideId) && pack.ById<SideDef>(a.SideId!) is null)
                    Err($"{who}: side \"{a.SideId}\" does not exist");
            });
    }

    private static void CheckBlocks(RulesPack pack, List<RulesIssue> issues)
    {
        var valid = new List<(PhaseBlock B, int Lo, int Hi, string Label)>();
        foreach (var b in pack.Blocks)
            Guarded(issues, b.FirstPhaseId, b.Label, () =>
            {
                string label = string.IsNullOrEmpty(b.Label) ? "(unnamed)" : b.Label;
                string who = $"Block \"{label}\"";
                void Err(string m) => Add(issues, RulesIssueSeverity.Error, b.FirstPhaseId, b.Label, m);
                int lo = pack.Phases.FindIndex(p => p.Id == b.FirstPhaseId);
                int hi = pack.Phases.FindIndex(p => p.Id == b.LastPhaseId);
                if (lo < 0) Err($"{who}: phase \"{b.FirstPhaseId}\" does not exist");
                if (hi < 0) Err($"{who}: phase \"{b.LastPhaseId}\" does not exist");
                if (lo >= 0 && hi >= 0 && lo > hi) Err($"{who}: its first phase comes after its last phase");
                if (b.Count < 1) Err($"{who}: count must be at least 1");
                if (lo >= 0 && hi >= lo) valid.Add((b, lo, hi, label));
            });
        for (int i = 0; i < valid.Count; i++)
            for (int j = i + 1; j < valid.Count; j++)
                if (valid[i].Lo <= valid[j].Hi && valid[j].Lo <= valid[i].Hi)
                    Add(issues, RulesIssueSeverity.Error, valid[i].B.FirstPhaseId, valid[i].B.Label,
                        $"Block \"{valid[i].Label}\" overlaps block \"{valid[j].Label}\"");
    }

    private static void Add(List<RulesIssue> l, RulesIssueSeverity s, string id, string name, string msg) =>
        l.Add(new RulesIssue(s, id, name, msg));

    /// <summary>
    /// True when a rule ref's document looks like a path on someone's computer (a drive like <c>C:</c>, a UNC or rooted path):
    /// rule refs name a module document or <c>scan:{file name}</c>, never a local path (the pack is shared with other players).
    /// </summary>
    public static bool IsLocalPath(string? entry)
    {
        if (string.IsNullOrEmpty(entry)) return false;
        var e = entry.Trim();
        if (e.Length >= 2 && char.IsAsciiLetter(e[0]) && e[1] == ':') return true;   // C:\…, C:/…, C:file
        if (e.StartsWith('\\') || e.StartsWith('/')) return true;                     // \\server\share, \x, /x
        try { return Path.IsPathRooted(e); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Rule refs whose document is a local path: an error on every item that uses them (or on the ref itself when none does).</summary>
    private static void CheckRuleRefPaths(RulesPack pack, List<RulesIssue> issues)
    {
        foreach (var r in pack.RuleRefs)
        {
            if (!IsLocalPath(r.DocumentEntry)) continue;
            string file;
            try { file = Path.GetFileName(r.DocumentEntry.Replace('\\', '/').TrimEnd('/')); }
            catch (ArgumentException) { file = ""; }
            var msg = $"rule reference points to a file on this computer (\"{file}\"); link it again to a module document or an opened scan";
            var users = pack.Formulas.Where(f => f.RuleRefId == r.Id).Select(f => (f.Id, f.Name, Who: $"Formula \"{f.Name}\""))
                .Concat(pack.Tables.Where(t => t.RuleRefId == r.Id).Select(t => (t.Id, t.Name, Who: $"Table \"{t.Name}\"")))
                .Concat(pack.Phases.Where(p => p.RuleRefId == r.Id).Select(p => (p.Id, p.Name, Who: $"Phase \"{p.Name}\""))).ToList();
            if (users.Count == 0) Add(issues, RulesIssueSeverity.Error, r.Id, r.Label, $"Rule reference \"{r.Label}\": {msg}");
            foreach (var u in users) Add(issues, RulesIssueSeverity.Error, u.Id, u.Name, $"{u.Who}: {msg}");
        }
    }

    private static string? ParseProblem(string text)
    {
        try { Parser.Parse(text); return null; }
        catch (ParseError e) { return e.Message; }
    }

    private static void Duplicates(IEnumerable<(string Id, string Name)> items, string kind, List<RulesIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in items)
            if (!seen.Add(name ?? ""))
                Add(issues, RulesIssueSeverity.Error, id, name ?? "", $"{kind} \"{name}\": another {kind.ToLowerInvariant()} already has this name");
    }

    // ---- tables ----

    private static void CheckTable(RulesPack pack, TableDef t, List<RulesIssue> issues)
    {
        string who = $"Table \"{t.Name}\"";
        CheckKeys(t.RowKeys, "row", who, t, issues);
        if (t.Kind == TableKind.TwoD) CheckKeys(t.ColumnKeys, "column", who, t, issues);
        if (!string.IsNullOrEmpty(t.RuleRefId) && pack.ById<RuleRef>(t.RuleRefId) is null)
            Add(issues, RulesIssueSeverity.Error, t.Id, t.Name, $"{who}: rule reference \"{t.RuleRefId}\" does not exist");
    }

    private static void CheckKeys(List<string> keys, string axis, string who, TableDef t, List<RulesIssue> issues)
    {
        var parsed = new List<(string Raw, TableKey Key)>();
        foreach (var raw in keys)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                Add(issues, RulesIssueSeverity.Error, t.Id, t.Name, $"{who}: a {axis} key is empty");
                continue;
            }
            parsed.Add((raw, TableLookup.ParseKey(raw)));
        }
        for (int i = 0; i < parsed.Count; i++)
            for (int j = i + 1; j < parsed.Count; j++)
            {
                var a = parsed[i].Key; var b = parsed[j].Key;
                if (a.Kind == TableKeyKind.Text || b.Kind == TableKeyKind.Text) continue;
                if (a.Lo <= b.Hi && b.Lo <= a.Hi)
                    Add(issues, RulesIssueSeverity.Warning, t.Id, t.Name,
                        $"{who}: {axis} keys \"{parsed[i].Raw}\" and \"{parsed[j].Raw}\" overlap");
            }
    }

    // ---- formulas ----

    private static void CheckFormula(RulesPack pack, FormulaDef f, List<RulesIssue> issues)
    {
        string who = $"Formula \"{f.Name}\"";
        void Err(string m) => Add(issues, RulesIssueSeverity.Error, f.Id, f.Name, $"{who}: {m}");
        void Warn(string m) => Add(issues, RulesIssueSeverity.Warning, f.Id, f.Name, $"{who}: {m}");

        foreach (var id in (f.Phases ?? new()).Distinct(StringComparer.Ordinal))
            if (pack.ById<PhaseDef>(id) is null) Err($"phase list names unknown phase \"{id}\"");

        AstExpr? ast = null;
        try { ast = Parser.Parse(f.Text ?? ""); }
        catch (ParseError e) { Err($"{e.Message}"); }

        if (ast is not null)
        {
            CheckLookups(ast, pack, Once(Err), f);
        }

        if (!string.IsNullOrWhiteSpace(f.AppliesTo))
        {
            if (ParseProblem(f.AppliesTo!) is { } ape) Err($"applies-to {ape}");
            else foreach (var u in FilterRefProblems(f.AppliesTo!, pack)) Err($"applies-to {u}");
        }
        if (!string.IsNullOrEmpty(f.RuleRefId) && pack.ById<RuleRef>(f.RuleRefId) is null)
            Err($"rule reference \"{f.RuleRefId}\" does not exist");

        var bindingNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in f.Bindings)
        {
            if (!bindingNames.Add(b.Variable)) Err($"variable \"{b.Variable}\" is bound more than once");
            CheckBinding(pack, f, b, Err);
            if (!string.IsNullOrWhiteSpace(b.AutoValue))
            {
                if (ParseProblem(b.AutoValue!) is { } ae) Err($"variable \"{b.Variable}\" auto value {ae}");
                else
                {
                    CheckLookups(Parser.Parse(b.AutoValue!), pack, Once(Err), f);
                }
            }
        }

        var noBinding = Once(Err);   // one message per name, however many expressions use it
        void CheckBound(AstExpr e)
        {
            foreach (var v in Parser.Variables(e))
                if (!bindingNames.Contains(v)) noBinding($"variable \"{v}\" has no binding");
        }
        // Guards: parsed and checked like the text (lookups, unknown variables); blank means none.
        AstExpr? ParseGuard(string? src, string what)
        {
            if (string.IsNullOrWhiteSpace(src)) return null;
            if (ParseProblem(src!) is { } ge) { Err($"{what} {ge}"); return null; }
            var g = Parser.Parse(src!);
            CheckLookups(g, pack, Once(Err), f);
            CheckBound(g);
            return g;
        }
        var pre = ParseGuard(f.Precondition, "precondition");
        foreach (var b in f.Bindings) ParseGuard(b.When, $"variable \"{b.Variable}\" only-when");
        foreach (var b in f.Bindings.Where(b => b.Kind != BindingKind.Roll && !string.IsNullOrWhiteSpace(b.DiceCountExpr)))
            Warn($"variable \"{b.Variable}\" has a dice count but is not a roll");
        foreach (var b in f.Bindings)
            if (ParseGuard(b.DiceCountExpr, $"variable \"{b.Variable}\" dice count") is { } dc && Parser.Variables(dc).Contains(b.Variable))
                Err($"variable \"{b.Variable}\" dice count cannot use {b.Variable} itself");
        if (pre is not null)
        {
            // Everything the precondition reaches is resolved before it runs, so none of it may roll.
            var reached = FormulaVariables.Reach(f, Parser.Variables(pre));
            foreach (var v in reached)
                if (f.Bindings.Any(b => b.Variable == v && b.Kind == BindingKind.Roll))
                    Err($"the precondition cannot use the roll {v}");
            var reachedSources = new[] { f.Precondition! }.Concat(f.Bindings.Where(b => reached.Contains(b.Variable)).SelectMany(FormulaVariables.Sources));
            if (reachedSources.Any(src => ParseProblem(src) is null && ExprFacts.CallsFunction(Parser.Parse(src), "dice")))
                Err("the precondition cannot use dice()");
        }

        if (ast is not null) CheckBound(ast);
        // Variables used only inside table row/column expressions or auto values also need bindings.
        var used = new HashSet<string>(FormulaVariables.Used(f), StringComparer.Ordinal);
        foreach (var extra in used.Where(u => ast is null || !Parser.Variables(ast).Contains(u)))
            if (!bindingNames.Contains(extra)) noBinding($"variable \"{extra}\" has no binding");
        foreach (var b in f.Bindings)
            if (!used.Contains(b.Variable)) Warn($"variable \"{b.Variable}\" is bound but not used in the formula");

        var outputNo = 0;
        foreach (var o in f.Outputs)
        {
            outputNo++;
            CheckOutput(pack, o, outputNo, Err);
            ParseGuard(o.Expr, $"output {outputNo} value");
            if (o.PropertyChoice is { Count: > 0 })
            {
                if (string.IsNullOrWhiteSpace(o.PropertySelect)) Err($"output {outputNo} has a property list but no \"Pick by\"");
                else ParseGuard(o.PropertySelect, $"output {outputNo} pick-by");
            }
        }
    }

    /// <summary>Reports each distinct message once (as <see cref="FilterRefProblems"/> does), so a repeated call is one issue.</summary>
    private static Action<string> Once(Action<string> err)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return m => { if (seen.Add(m)) err(m); };
    }

    private static void CheckLookups(AstExpr e, RulesPack pack, Action<string> err, FormulaDef? f = null)
    {
        switch (e)
        {
            case Unary u: CheckLookups(u.Operand, pack, err, f); break;
            case Binary b: CheckLookups(b.Left, pack, err, f); CheckLookups(b.Right, pack, err, f); break;
            case Call c:
                if (c.Function is "lookup" or "lookuprange" && c.Args.Count > 0 && c.Args[0] is VarRef tv && pack.TableByName(tv.Name) is null)
                    err($"no table named \"{tv.Name}\"");
                if (c.Function == "count" && f is not null && c.Args.Count > 0 && c.Args[0] is VarRef rv
                    && !f.Bindings.Any(b => b.Variable == rv.Name && b.Kind == BindingKind.Roll))
                    err($"count() needs a roll binding, not {rv.Name}");
                if (c.Function == "holder" && c.Args is [VarRef hn, VarRef hp])
                    foreach (var u in HolderCallProblems(hn.Name, hp.Name, pack)) err(u);
                if (ExprFacts.IsAggregate(c.Function))
                {
                    foreach (var u in AggregateProblems(c, pack)) err(u);
                    break;
                }
                foreach (var a in c.Args) CheckLookups(a, pack, err, f);
                break;
        }
    }

    private static void CheckBinding(RulesPack pack, FormulaDef f, VariableBinding b, Action<string> err)
    {
        string v = $"variable \"{b.Variable}\"";
        switch (b.Kind)
        {
            case BindingKind.Unit:
            case BindingKind.Global:
                CheckProperty(pack, b.Property, v, err);
                break;
            case BindingKind.Holder:
                CheckProperty(pack, b.Property, v, err);
                if (b.HolderId != HolderDef.GlobalId && pack.ById<HolderDef>(b.HolderId ?? "") is null)
                    err($"{v} refers to unknown holder \"{b.HolderId}\"");
                break;
            case BindingKind.Table:
            {
                var t = pack.ById<TableDef>(b.TableId ?? "");
                if (t is null) err($"{v} refers to unknown table \"{b.TableId}\"");
                if (string.IsNullOrWhiteSpace(b.RowExpr)) err($"{v} needs a row expression");
                else CheckExpr(b.RowExpr!, "row expression", v, err);
                if (t?.Kind == TableKind.TwoD && string.IsNullOrWhiteSpace(b.ColExpr))
                    err($"{v} needs a column expression because table \"{t.Name}\" has two dimensions");
                else if (!string.IsNullOrWhiteSpace(b.ColExpr))
                    CheckExpr(b.ColExpr!, "column expression", v, err);
                break;
            }
            case BindingKind.Piece:
            {
                if (string.IsNullOrWhiteSpace(b.Property)) err($"{v} has no property chosen");
                else if (pack.ById<PropertyDef>(b.Property!) is not { } pp) err($"{v} reads unknown property \"{b.Property}\"");
                else if (pp.Scope == PropertyScope.Holders) err($"{v} reads holder property \"{pp.Name}\"");
                if (!string.IsNullOrWhiteSpace(b.PieceFilter))
                {
                    try { Parser.Parse(b.PieceFilter!); }
                    catch (ParseError e) { err($"{v} \"which pieces\" filter does not parse: {e.Message}"); }
                }
                break;
            }
            case BindingKind.Input:
                if (b.Min is { } lo && b.Max is { } hi && lo > hi)
                    err($"{v} has a minimum ({lo}) above its maximum ({hi})");
                break;
            case BindingKind.Roll:
                if (b.DiceCount is not { } n || n < 1) err($"{v} needs a dice count of at least 1");
                if (b.DiceSides is not { } s || s < 2) err($"{v} needs dice with at least 2 sides");
                break;
        }
    }

    private static void CheckExpr(string text, string what, string v, Action<string> err)
    {
        try { Parser.Parse(text); }
        catch (ParseError e) { err($"{v} {what}: {e.Message}"); }
    }

    private static void CheckProperty(RulesPack pack, string? name, string v, Action<string> err)
    {
        if (string.IsNullOrWhiteSpace(name)) err($"{v} has no property chosen");
        else if (pack.ById<PropertyDef>(name!) is null) err($"{v} refers to a property that no longer exists");
    }

    private static void CheckOutput(RulesPack pack, FormulaOutput o, int outputNo, Action<string> err)
    {
        var gone = Once(err);   // one "no longer exists" per output, however many of its ids are gone
        if (o.Target == OutputTarget.Holder && o.HolderId != HolderDef.GlobalId && pack.ById<HolderDef>(o.HolderId ?? "") is null)
            err($"output refers to unknown holder \"{o.HolderId}\"");
        // With a property list the fixed Property is not used; every listed id must suit the target as Property must.
        if (o.PropertyChoice is { Count: > 0 } ids) foreach (var id in ids) CheckOutputProperty(pack, o, id, outputNo, err, gone);
        else CheckOutputProperty(pack, o, o.Property, outputNo, err, gone);
    }

    private static void CheckOutputProperty(RulesPack pack, FormulaOutput o, string? id, int outputNo, Action<string> err, Action<string> gone)
    {
        var p = pack.ById<PropertyDef>(id ?? "");
        if (p is null) { gone($"output {outputNo} refers to a property that no longer exists"); return; }
        bool ok = o.Target == OutputTarget.Unit
            ? p.Scope is PropertyScope.Pieces or PropertyScope.Both
            : p.Scope is PropertyScope.Holders or PropertyScope.Both;
        if (PropertyFacts.IsComputed(p)) { err($"output {outputNo} writes computed property \"{p.Name}\""); return; }
        if (!ok)
            err($"output property \"{p.Name}\" cannot be written to {(o.Target == OutputTarget.Unit ? "a unit" : "a holder")} (its scope is {p.Scope})");
    }
}
