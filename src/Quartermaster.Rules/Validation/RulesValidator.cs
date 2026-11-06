using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules;

public enum RulesIssueSeverity { Error, Warning }

public sealed record RulesIssue(RulesIssueSeverity Severity, string EntityId, string EntityName, string Message);

/// <summary>Checks a rules pack for problems. Never throws on bad user data.</summary>
public static class RulesValidator
{
    public static IReadOnlyList<RulesIssue> Validate(RulesPack pack)
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
                        foreach (var u in UnknownPropertyRefs(p.AppliesTo!, pack))
                            Add(issues, RulesIssueSeverity.Error, p.Id, p.Name, $"Property \"{p.Name}\": applies-to refers to unknown property \"{u}\"");
                }
            foreach (var t in pack.Tables) CheckTable(pack, t, issues);
            foreach (var f in pack.Formulas) Guarded(issues, f.Id, f.Name, () => CheckFormula(pack, f, issues));
            foreach (var s in pack.Sides) Guarded(issues, s.Id, s.Name, () => CheckSide(s, pack, issues));
            foreach (var ph in pack.Phases) Guarded(issues, ph.Id, ph.Name, () => CheckPhase(pack, ph, issues));
            CheckBlocks(pack, issues);
        }
        catch (Exception ex)
        {
            Add(issues, RulesIssueSeverity.Error, pack.Id, pack.Name, $"Validation failed unexpectedly: {ex.Message}");
        }
        return issues;
    }

    /// <summary>Property names an applies-to expression refers to (variables and has() arguments) that no property has.</summary>
    private static IEnumerable<string> UnknownPropertyRefs(string text, RulesPack pack)
    {
        var found = new List<string>();
        try { Walk(Parser.Parse(text)); }
        catch (ParseError) { }
        return found.Distinct(StringComparer.Ordinal);

        void Check(string n) { if (pack.PropertyByName(n) is null) found.Add(n); }
        void Walk(AstExpr e)
        {
            switch (e)
            {
                case VarRef v: Check(v.Name); break;
                case Unary u: Walk(u.Operand); break;
                case Binary b: Walk(b.Left); Walk(b.Right); break;
                case Call c:
                    if (c.Function == "has" && c.Args.Count == 1 && c.Args[0] is TextLit t) Check(t.Value);
                    else if (c.Function == "has" && c.Args.Count == 1 && c.Args[0] is VarRef hv) Check(hv.Name);
                    else foreach (var a in c.Args) Walk(a);
                    break;
            }
        }
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
        else foreach (var u in UnknownPropertyRefs(text!, pack)) err($"{who}: {what} refers to unknown property \"{u}\"");
    }

    private static void CheckSide(SideDef s, RulesPack pack, List<RulesIssue> issues) =>
        CheckPhaseExpr(pack, s.UnitFilter, $"Side \"{s.Name}\"", "unit filter",
            m => Add(issues, RulesIssueSeverity.Error, s.Id, s.Name, m));

    private static void CheckPhase(RulesPack pack, PhaseDef ph, List<RulesIssue> issues)
    {
        string who = $"Phase \"{ph.Name}\"";
        void Err(string m) => Add(issues, RulesIssueSeverity.Error, ph.Id, ph.Name, m);

        if (!string.IsNullOrEmpty(ph.SideId) && pack.ById<SideDef>(ph.SideId!) is null)
            Err($"{who}: side \"{ph.SideId}\" does not exist");
        if (!string.IsNullOrEmpty(ph.RuleRefId) && pack.ById<RuleRef>(ph.RuleRefId!) is null)
            Err($"{who}: rule reference \"{ph.RuleRefId}\" does not exist");

        foreach (var e in ph.OnStart)
        {
            if (pack.ById<FormulaDef>(e.FormulaId) is null) Err($"{who}: formula \"{e.FormulaId}\" does not exist");
            CheckPhaseExpr(pack, e.UnitFilter, who, "on-start filter", Err);
        }
        foreach (var e in ph.OnMove)
        {
            var f = pack.ById<FormulaDef>(e.FormulaId);
            if (f is null) Err($"{who}: formula \"{e.FormulaId}\" does not exist");
            else if (!f.Bindings.Any(b => b.Kind == BindingKind.Moved))
                Add(issues, RulesIssueSeverity.Warning, ph.Id, ph.Name, $"{who}: on-move formula \"{f.Name}\" has no Moved binding");
            CheckPhaseExpr(pack, e.UnitFilter, who, "on-move filter", Err);
        }
        foreach (var w in ph.Warnings)
        {
            CheckPhaseExpr(pack, w.Condition, who, "warning condition", Err);
            if (string.IsNullOrWhiteSpace(w.Message)) Err($"{who}: warning message is empty");
        }
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

        var used = new HashSet<string>(StringComparer.Ordinal);
        AstExpr? ast = null;
        try { ast = Parser.Parse(f.Text ?? ""); }
        catch (ParseError e) { Err($"{e.Message}"); }

        if (ast is not null)
        {
            foreach (var v in Parser.Variables(ast)) used.Add(v);
            CheckLookups(ast, pack, Err);
        }

        if (!string.IsNullOrWhiteSpace(f.AppliesTo))
        {
            if (ParseProblem(f.AppliesTo!) is { } ape) Err($"applies-to {ape}");
            else foreach (var u in UnknownPropertyRefs(f.AppliesTo!, pack)) Err($"applies-to refers to unknown property \"{u}\"");
        }
        if (!string.IsNullOrEmpty(f.RuleRefId) && pack.ById<RuleRef>(f.RuleRefId) is null)
            Err($"rule reference \"{f.RuleRefId}\" does not exist");

        var bindingNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in f.Bindings)
        {
            if (!bindingNames.Add(b.Variable)) Err($"variable \"{b.Variable}\" is bound more than once");
            CheckBinding(pack, f, b, used, Err);
            if (!string.IsNullOrWhiteSpace(b.AutoValue) && ParseProblem(b.AutoValue!) is { } ae)
                Err($"variable \"{b.Variable}\" auto value {ae}");
        }

        if (ast is not null)
            foreach (var v in Parser.Variables(ast))
                if (!bindingNames.Contains(v)) Err($"variable \"{v}\" has no binding");
        // Variables used only inside table row/column expressions also need bindings.
        foreach (var extra in used.Where(u => ast is null || !Parser.Variables(ast).Contains(u)))
            if (!bindingNames.Contains(extra)) Err($"variable \"{extra}\" has no binding");
        foreach (var b in f.Bindings)
            if (!used.Contains(b.Variable)) Warn($"variable \"{b.Variable}\" is bound but not used in the formula");

        foreach (var o in f.Outputs) CheckOutput(pack, o, Err);
    }

    private static void CheckLookups(AstExpr e, RulesPack pack, Action<string> err)
    {
        switch (e)
        {
            case Unary u: CheckLookups(u.Operand, pack, err); break;
            case Binary b: CheckLookups(b.Left, pack, err); CheckLookups(b.Right, pack, err); break;
            case Call c:
                if (c.Function == "lookup" && c.Args.Count > 0 && c.Args[0] is VarRef tv && pack.TableByName(tv.Name) is null)
                    err($"no table named \"{tv.Name}\"");
                foreach (var a in c.Args) CheckLookups(a, pack, err);
                break;
        }
    }

    private static void CheckBinding(RulesPack pack, FormulaDef f, VariableBinding b, HashSet<string> used, Action<string> err)
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
                else CheckExpr(b.RowExpr!, "row expression", v, used, err);
                if (t?.Kind == TableKind.TwoD && string.IsNullOrWhiteSpace(b.ColExpr))
                    err($"{v} needs a column expression because table \"{t.Name}\" has two dimensions");
                else if (!string.IsNullOrWhiteSpace(b.ColExpr))
                    CheckExpr(b.ColExpr!, "column expression", v, used, err);
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

    private static void CheckExpr(string text, string what, string v, HashSet<string> used, Action<string> err)
    {
        try
        {
            foreach (var name in Parser.Variables(Parser.Parse(text))) used.Add(name);
        }
        catch (ParseError e) { err($"{v} {what}: {e.Message}"); }
    }

    private static void CheckProperty(RulesPack pack, string? name, string v, Action<string> err)
    {
        if (string.IsNullOrWhiteSpace(name)) err($"{v} has no property chosen");
        else if (pack.ById<PropertyDef>(name!) is null) err($"{v} refers to a property that no longer exists");
    }

    private static void CheckOutput(RulesPack pack, FormulaOutput o, Action<string> err)
    {
        var p = pack.ById<PropertyDef>(o.Property ?? "");
        if (p is null) { err("an output refers to a property that no longer exists"); }
        if (o.Target == OutputTarget.Holder && o.HolderId != HolderDef.GlobalId && pack.ById<HolderDef>(o.HolderId ?? "") is null)
            err($"output refers to unknown holder \"{o.HolderId}\"");
        if (p is null) return;
        bool ok = o.Target == OutputTarget.Unit
            ? p.Scope is PropertyScope.Pieces or PropertyScope.Both
            : p.Scope is PropertyScope.Holders or PropertyScope.Both;
        if (!ok)
            err($"output property \"{p.Name}\" cannot be written to {(o.Target == OutputTarget.Unit ? "a unit" : "a holder")} (its scope is {p.Scope})");
    }
}
