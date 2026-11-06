using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

public sealed record TextAnalysis(IReadOnlyList<string> Variables, ParseError? Error);
public sealed record SyncResult(FormulaDef Formula, IReadOnlyList<VariableBinding> Removed);

/// <summary>A choice for a binding's Target cell: shown by name, stored by id (ruling P2-R4).</summary>
public sealed record TargetOption(string Display, string? Property, string? HolderId, string? TableId);

public sealed record PieceEntry(string PieceId, string Display);

/// <summary>Pure edit operations behind the workbench formula editor; each returns a new FormulaDef and never mutates its input.</summary>
public static class FormulaEdit
{
    public const string Missing = "(missing)";

    // ---------- text ----------

    public static TextAnalysis Analyze(string text)
    {
        try { return new TextAnalysis(Parser.Variables(Parser.Parse(text ?? "")), null); }
        catch (ParseError e) { return new TextAnalysis(Array.Empty<string>(), e); }
    }

    public static string ErrorText(ParseError e) => $"Error at {e.Position}: {e.Detail}";

    /// <summary>Null when blank or parsable, else the message (AppliesTo checks).</summary>
    public static string? SyntaxProblem(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { Parser.Parse(text); return null; }
        catch (ParseError e) { return ErrorText(e); }
    }

    public static FormulaDef Rename(FormulaDef f, string name) => f with { Name = name };
    public static FormulaDef SetText(FormulaDef f, string text) => f with { Text = text };
    public static FormulaDef SetAppliesTo(FormulaDef f, string? text) => f with { AppliesTo = string.IsNullOrWhiteSpace(text) ? null : text };

    // ---------- bindings ----------

    /// <summary>True when the binding holds anything beyond a default, unconfigured Input.</summary>
    public static bool IsConfigured(VariableBinding b) =>
        b.Kind != BindingKind.Input || b.Property != null || b.HolderId != null || b.TableId != null || b.RowExpr != null || b.ColExpr != null
        || b.Min != null || b.Max != null || b.Choices != null || b.DiceCount != null || b.DiceSides != null || b.Caption != null;

    /// <summary>Bindings whose variable no longer appears in the text.</summary>
    public static IReadOnlyList<VariableBinding> Stale(FormulaDef f, IReadOnlyList<string> variables) =>
        f.Bindings.Where(b => !variables.Contains(b.Variable, StringComparer.Ordinal)).ToList();

    /// <summary>Bindings whose variable is not in the formula's current text.</summary>
    public static IReadOnlyList<VariableBinding> Unused(FormulaDef f)
    {
        var a = Analyze(f.Text);
        return a.Error != null ? Array.Empty<VariableBinding>() : Stale(f, a.Variables);
    }

    public static FormulaDef RemoveUnused(FormulaDef f)
    {
        var a = Analyze(f.Text);
        return a.Error != null ? f : Sync(f, a.Variables).Formula;
    }

    /// <summary>Bindings follow the variables in text order; new ones are Input. Removed ones are reported (configured ones are kept on request).</summary>
    public static SyncResult Sync(FormulaDef f, IReadOnlyList<string> variables, bool keepConfigured = false)
    {
        var list = new List<VariableBinding>();
        foreach (var v in variables)
            list.Add(f.Bindings.FirstOrDefault(b => b.Variable == v) ?? new VariableBinding { Variable = v, Kind = BindingKind.Input });
        var removed = new List<VariableBinding>();
        foreach (var b in Stale(f, variables))
        {
            if (keepConfigured && IsConfigured(b)) list.Add(b);
            else removed.Add(b);
        }
        return new SyncResult(f with { Bindings = list }, removed);
    }

    public static FormulaDef SetBinding(FormulaDef f, string variable, Func<VariableBinding, VariableBinding> edit) =>
        f with { Bindings = f.Bindings.Select(b => b.Variable == variable ? edit(b) : b).ToList() };

    /// <summary>Changes the kind and clears everything specific to the old kind (caption stays).</summary>
    public static FormulaDef SetKind(FormulaDef f, string variable, BindingKind kind) =>
        SetBinding(f, variable, b => b.Kind == kind ? b : new VariableBinding { Variable = b.Variable, Kind = kind, Caption = b.Caption });

    public static FormulaDef SetCaption(FormulaDef f, string variable, string? caption) =>
        SetBinding(f, variable, b => b with { Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim() });

    // ---------- details column ----------

    private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    public static string FormatDetails(VariableBinding b)
    {
        switch (b.Kind)
        {
            case BindingKind.Input:
                if (b.Choices is { Count: > 0 }) return string.Join(", ", b.Choices);
                if (b.Min == null && b.Max == null) return "";
                return (b.Min is { } lo ? Num(lo) : "") + ".." + (b.Max is { } hi ? Num(hi) : "");
            case BindingKind.Roll:
                return b.DiceCount is { } n && b.DiceSides is { } s ? $"{n}d{s}" : "";
            case BindingKind.Table:
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(b.RowExpr)) parts.Add("row: " + b.RowExpr);
                if (!string.IsNullOrWhiteSpace(b.ColExpr)) parts.Add("col: " + b.ColExpr);
                return string.Join(" | ", parts);
            default:
                return "";
        }
    }

    /// <summary>Parses the Details cell for the binding's kind. On a problem the binding is returned unchanged with a message.</summary>
    public static (VariableBinding Binding, string? Error) ParseDetails(VariableBinding b, string text)
    {
        text = (text ?? "").Trim();
        switch (b.Kind)
        {
            case BindingKind.Input:
                if (text.Length == 0) return (b with { Min = null, Max = null, Choices = null }, null);
                var dots = text.IndexOf("..", StringComparison.Ordinal);
                if (dots < 0)
                {
                    var choices = text.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                    return (b with { Min = null, Max = null, Choices = choices.Count == 0 ? null : choices }, null);
                }
                if (!TryNum(text[..dots], out var lo) || !TryNum(text[(dots + 2)..], out var hi))
                    return (b, "Use a range like 0..20, or a comma-separated list of choices.");
                return (b with { Min = lo, Max = hi, Choices = null }, null);
            case BindingKind.Roll:
                if (text.Length == 0) return (b with { DiceCount = null, DiceSides = null }, null);
                var d = text.ToLowerInvariant().Split('d');
                if (d.Length != 2 || !int.TryParse(d[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || !int.TryParse(d[1], NumberStyles.None, CultureInfo.InvariantCulture, out var s) || n < 1 || s < 1)
                    return (b, "Use dice notation like 2d6.");
                return (b with { DiceCount = n, DiceSides = s }, null);
            case BindingKind.Table:
                string? row = null, col = null;
                foreach (var part in text.Split('|'))
                {
                    var p = part.Trim();
                    if (p.Length == 0) continue;
                    if (p.StartsWith("row:", StringComparison.OrdinalIgnoreCase)) row = p[4..].Trim();
                    else if (p.StartsWith("col:", StringComparison.OrdinalIgnoreCase)) col = p[4..].Trim();
                    else return (b, "Use \"row: expression | col: expression\".");
                }
                return (b with { RowExpr = string.IsNullOrEmpty(row) ? null : row, ColExpr = string.IsNullOrEmpty(col) ? null : col }, null);
            default:
                return (b, null);
        }
    }

    private static bool TryNum(string s, out double? v)
    {
        s = s.Trim();
        v = null;
        if (s.Length == 0) return true;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return false;
        v = d;
        return true;
    }

    // ---------- target column ----------

    public static IReadOnlyList<TargetOption> TargetOptions(RulesPack pack, BindingKind kind)
    {
        switch (kind)
        {
            case BindingKind.Unit or BindingKind.Global:
                return pack.Properties.Select(p => new TargetOption(p.Name, p.Id, null, null)).ToList();
            case BindingKind.Holder:
                return pack.Holders.SelectMany(h => pack.Properties.Select(p => new TargetOption($"{h.Name}: {p.Name}", p.Id, h.Id, null))).ToList();
            case BindingKind.Table:
                return pack.Tables.Select(t => new TargetOption(t.Name, null, null, t.Id)).ToList();
            default:
                return Array.Empty<TargetOption>();
        }
    }

    public static VariableBinding ApplyTarget(VariableBinding b, TargetOption o) =>
        b with { Property = o.Property, HolderId = o.HolderId, TableId = o.TableId };

    /// <summary>The Target cell text for a binding; "(missing)" when the id no longer resolves, empty for kinds with no target.</summary>
    public static string TargetDisplay(RulesPack pack, VariableBinding b)
    {
        switch (b.Kind)
        {
            case BindingKind.Unit or BindingKind.Global:
                return b.Property == null ? "" : pack.ById<PropertyDef>(b.Property)?.Name ?? Missing;
            case BindingKind.Holder:
                if (b.Property == null && b.HolderId == null) return "";
                var h = b.HolderId == null ? null : pack.ById<HolderDef>(b.HolderId);
                var p = b.Property == null ? null : pack.ById<PropertyDef>(b.Property);
                return h == null || p == null ? Missing : $"{h.Name}: {p.Name}";
            case BindingKind.Table:
                return b.TableId == null ? "" : pack.ById<TableDef>(b.TableId)?.Name ?? Missing;
            default:
                return "";
        }
    }

    // ---------- outputs ----------

    public static FormulaDef AddOutput(FormulaDef f) =>
        f with { Outputs = f.Outputs.Append(new FormulaOutput { Target = OutputTarget.Unit, Op = OutputOp.Subtract }).ToList() };

    public static FormulaDef SetOutput(FormulaDef f, int index, Func<FormulaOutput, FormulaOutput> edit) =>
        f with { Outputs = f.Outputs.Select((o, i) => i == index ? edit(o) : o).ToList() };

    public static FormulaDef RemoveOutput(FormulaDef f, int index) =>
        f with { Outputs = f.Outputs.Where((_, i) => i != index).ToList() };

    /// <summary>Changing the target drops a holder that no longer applies.</summary>
    public static FormulaOutput WithOutputTarget(FormulaOutput o, OutputTarget t) =>
        o with { Target = t, HolderId = t == OutputTarget.Holder ? o.HolderId : null };

    // ---------- usage ----------

    public static IReadOnlyList<FormulaDef> FormulasUsingProperty(RulesPack pack, string propertyId) =>
        pack.Formulas.Where(f => f.Bindings.Any(b => b.Property == propertyId) || f.Outputs.Any(o => o.Property == propertyId)).ToList();

    public static IReadOnlyList<FormulaDef> FormulasUsingHolder(RulesPack pack, string holderId) =>
        pack.Formulas.Where(f => f.Bindings.Any(b => b.HolderId == holderId) || f.Outputs.Any(o => o.HolderId == holderId)).ToList();
}

/// <summary>Property-sheet row names found in the module's pieces: they are field names from the module's own piece definitions.</summary>
public static class SheetFieldCatalog
{
    public static IReadOnlyList<string> Collect(IEnumerable<string> typeStrings)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var log = new DiagnosticLog();
        foreach (var ts in typeStrings)
        {
            try
            {
                foreach (var sheet in PieceTypeParser.Parse(ts ?? "", log, null).Traits.OfType<PropertySheetDef>())
                    foreach (var (name, _) in sheet.Rows)
                        if (name.Length > 0) seen.TryAdd(name, name);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* unreadable piece type: contributes nothing */ }
        }
        return seen.Values.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<string> Collect(ModuleModel model, GameState? state = null)
    {
        var types = model.Slots.Select(s => s.TypeString)
            .Concat(model.Maps.SelectMany(m => m.SetupStacks).SelectMany(st => st.Pieces).Select(p => p.TypeString))
            .Concat(state?.Pieces.Values.Select(p => p.TypeString) ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal);
        var found = Collect(types).ToList();
        foreach (var proto in model.Prototypes.Values)
            foreach (var sheet in proto.Traits.OfType<PropertySheetDef>())
                foreach (var (name, _) in sheet.Rows)
                    if (name.Length > 0 && !found.Contains(name, StringComparer.OrdinalIgnoreCase)) found.Add(name);
        return found.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>The test-piece picker entries of the formula preview.</summary>
public static class PieceChoices
{
    public const int Limit = 2000;

    public static IReadOnlyList<PieceEntry> Entries(GameState state, Func<string, string> mapName)
    {
        var list = new List<PieceEntry>();
        foreach (var p in state.Pieces.Values)
        {
            if (list.Count >= Limit) break;
            var name = PieceInfo.Name(p);
            if (name.Length == 0) name = "(unnamed)";
            list.Add(new PieceEntry(p.Id, $"{name} ({mapName(p.MapId)})"));
        }
        return list;
    }
}

/// <summary>Names must be unique (case-insensitive) and non-empty, because formulas are matched to them by name in places.</summary>
public static class NameRules
{
    /// <summary>Null when acceptable; otherwise a message. currentName is the entity's own present name (allowed to stay).</summary>
    public static string? Problem(IEnumerable<string> existingNames, string? name, string? currentName)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "The name cannot be empty.";
        if (currentName != null && string.Equals(currentName.Trim(), n, StringComparison.OrdinalIgnoreCase)) return null;
        return existingNames.Any(e => string.Equals(e.Trim(), n, StringComparison.OrdinalIgnoreCase)) ? $"There is already an item named \"{n}\"." : null;
    }
}
