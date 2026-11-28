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
        b.Kind != BindingKind.Input || b.PieceFilter != null || b.Property != null || b.HolderId != null || b.TableId != null || b.RowExpr != null || b.ColExpr != null
        || b.Min != null || b.Max != null || b.Choices != null || b.DiceCount != null || b.DiceSides != null || b.Caption != null || b.AutoValue != null || b.AlwaysAsk || b.When != null || b.DiceCountExpr != null || b.ChoicesTable != null || b.ChoicesFilter != null
        || b.HolderChoices != null || b.DrawCount != null || b.SameHex;

    /// <summary>Bindings whose variable no longer appears in the text.</summary>
    public static IReadOnlyList<VariableBinding> Stale(FormulaDef f, IReadOnlyList<string> variables) =>
        f.Bindings.Where(b => !variables.Contains(b.Variable, StringComparer.Ordinal)).ToList();

    /// <summary>
    /// The variables of the text and the precondition, then any used only inside a used binding's Table row/column expression, auto value
    /// or "Only when" condition (nested included), the same set the formula runner resolves and the validator counts as used. Empty when the text does not parse.
    /// </summary>
    public static IReadOnlyList<string> UsedVariables(FormulaDef f) => FormulaVariables.Used(f);

    /// <summary>Bindings whose variable is neither in the formula's current text nor in a table binding's row/column expression.</summary>
    public static IReadOnlyList<VariableBinding> Unused(FormulaDef f) =>
        Analyze(f.Text).Error != null ? Array.Empty<VariableBinding>() : Stale(f, UsedVariables(f));

    /// <summary>Drops unused bindings and adds an Input binding for any used variable that has none (e.g. a new row-expression variable).</summary>
    public static FormulaDef RemoveUnused(FormulaDef f) =>
        Analyze(f.Text).Error != null ? f : Sync(f, UsedVariables(f)).Formula;

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

    /// <summary>
    /// Changes the kind and clears everything specific to the old kind; the caption and the "Only when" condition are kept. B9: Piece and Draw
    /// share the pool (which counters, the property and "same hex"), so a change between them keeps it (<see cref="WithKind"/>).
    /// </summary>
    public static FormulaDef SetKind(FormulaDef f, string variable, BindingKind kind) =>
        SetBinding(f, variable, b => b.Kind == kind ? b
            : IsPool(b.Kind) && IsPool(kind) ? WithKind(b, kind)
            : new VariableBinding { Variable = b.Variable, Kind = kind, Caption = b.Caption, When = b.When });

    private static bool IsPool(BindingKind k) => k is BindingKind.Piece or BindingKind.Draw;

    /// <summary>B9: sets the kind and clears the fields that kind does not use: holder choices off Holder, the draw count off Draw, "same hex" off Piece and Draw.</summary>
    public static VariableBinding WithKind(VariableBinding b, BindingKind kind) =>
        b with
        {
            Kind = kind,
            HolderChoices = kind == BindingKind.Holder ? b.HolderChoices : null,
            DrawCount = kind == BindingKind.Draw ? b.DrawCount : null,
            SameHex = IsPool(kind) && b.SameHex,
        };

    /// <summary>B9: a Draw binding's draw count expression; blank is null, never "".</summary>
    public static VariableBinding WithDrawCount(VariableBinding b, string? count) => b with { DrawCount = Blank(count) };

    /// <summary>
    /// B9: a Holder binding's holder choices from the cell text: holder names (any case) separated by commas, a name holding a comma in
    /// "double quotes" (the Properties tab's Holders format). Blank = null (the fixed holder, never an empty list); a list clears the fixed
    /// holder, which a picker does not use. An unknown name is left out and reported; a quoting problem returns the binding unchanged.
    /// </summary>
    public static (VariableBinding Binding, string? Error) WithHolderChoices(RulesPack pack, VariableBinding b, string? names)
    {
        var (list, err) = ParseNameList(names ?? "");
        if (err != null) return (b, err);
        var ids = new List<string>();
        string? unknown = null;
        foreach (var raw in list!)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            if (pack.HolderByName(name) is not { } h) { unknown ??= $"No holder is named \"{name}\""; continue; }
            if (!ids.Contains(h.Id)) ids.Add(h.Id);
        }
        var nb = ids.Count == 0 ? b with { HolderChoices = null } : b with { HolderChoices = ids, HolderId = null };
        return (nb, unknown);
    }

    /// <summary>B9: the Holder choices cell text (an id that no longer resolves shows as it is).</summary>
    public static string FormatHolderChoices(RulesPack pack, VariableBinding b) =>
        b.HolderChoices is not { Count: > 0 } ids ? "" : FormatNameList(ids.Select(id => pack.ById<HolderDef>(id)?.Name ?? id));

    public static FormulaDef SetCaption(FormulaDef f, string variable, string? caption) =>
        SetBinding(f, variable, b => b with { Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim() });

    // ---------- details column ----------

    /// <summary>The Details hint of a Piece binding (both Workbenches).</summary>
    public const string PieceDetailsHint =
        "Which pieces: a unit filter such as startsWith(name(), \"Blue\"); blank = every piece. The player picks one of the pieces they can see when the formula runs.";

    /// <summary>B9: the Details hint of a Draw binding (both Workbenches).</summary>
    public const string DrawDetailsHint =
        "Which counters to draw from (a unit filter; blank = every counter). Target: the property giving each counter's number of members (one each when none).";

    /// <summary>B9: the Target choice of a Draw binding with no weight property (each counter is one member).</summary>
    public const string OneEach = "(one each)";

    /// <summary>B9: the Target text of a holder picker's property.</summary>
    public static string ChosenHolderTarget(string propertyName) => $"{propertyName} of (chosen holder)";

    private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    public static string FormatDetails(VariableBinding b)
    {
        switch (b.Kind)
        {
            case BindingKind.Input:
                string basic;
                if (b.Choices is { Count: > 0 }) basic = string.Join(", ", b.Choices.Select(QuoteChoice));
                else if (b.Min == null && b.Max == null) basic = "";
                else basic = (b.Min is { } lo ? Num(lo) : "") + ".." + (b.Max is { } hi ? Num(hi) : "");
                var inputParts = new List<string>();
                if (basic.Length > 0) inputParts.Add(basic);
                if (b.AlwaysAsk) inputParts.Add("always ask");
                if (!string.IsNullOrWhiteSpace(b.AutoValue)) inputParts.Add("auto: " + b.AutoValue);
                // An auto part alone keeps its leading bar: the bar is what marks it (without it the text would read as a choice).
                return (inputParts.Count == 1 && inputParts[0].StartsWith("auto: ", StringComparison.Ordinal) ? "| " : "") + string.Join(" | ", inputParts);
            case BindingKind.Roll:
                return b.DiceCount is { } n && b.DiceSides is { } s ? $"{n}d{s}" : "";
            case BindingKind.Table:
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(b.RowExpr)) parts.Add("row: " + b.RowExpr);
                if (!string.IsNullOrWhiteSpace(b.ColExpr)) parts.Add("col: " + b.ColExpr);
                return string.Join(" | ", parts);
            case BindingKind.Piece or BindingKind.Draw:
                return b.PieceFilter ?? "";
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
                string? auto = null;
                var alwaysAsk = false;
                var bar = AutoBar(text);
                if (bar >= 0)
                {
                    auto = text[(bar + 1)..].Trim()[5..].Trim();
                    text = text[..bar].Trim();
                    (auto, alwaysAsk) = SplitAlwaysAskAfterAuto(auto);
                    if (auto.Length == 0) auto = null;
                    else if (SyntaxProblem(auto) is { } autoErr) return (b, "Auto value: " + autoErr);
                }
                const string inputError = "Use a range like 0..20, or choices separated by commas; add | always ask and/or | auto: expression.";
                string? basicPart = null;
                foreach (var part in SplitAtBars(text))
                {
                    if (part.Equals("always ask", StringComparison.OrdinalIgnoreCase)) alwaysAsk = true;
                    else if (part.Length == 0) continue;
                    else if (basicPart == null) basicPart = part;
                    else return (b, inputError);
                }
                text = basicPart ?? "";
                if (text.Length == 0) return (b with { Min = null, Max = null, Choices = null, AutoValue = auto, AlwaysAsk = alwaysAsk }, null);
                var dots = DotsOutsideQuotes(text);
                if (dots < 0)
                {
                    var (choices, choiceErr) = ParseChoices(text);
                    if (choiceErr != null) return (b, choiceErr);
                    return (b with { Min = null, Max = null, Choices = choices, AutoValue = auto, AlwaysAsk = alwaysAsk }, null);
                }
                if (!TryNum(text[..dots], out var lo) || !TryNum(text[(dots + 2)..], out var hi))
                    return (b, inputError);
                return (b with { Min = lo, Max = hi, Choices = null, AutoValue = auto, AlwaysAsk = alwaysAsk }, null);
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
            case BindingKind.Piece or BindingKind.Draw:
                // The "which pieces" filter: blank = every piece (stored as null, never ""); it must parse.
                if (text.Length == 0) return (b with { PieceFilter = null }, null);
                if (SyntaxProblem(text) is { } filterErr) return (b, "Which pieces: " + filterErr);
                return (b with { PieceFilter = text }, null);
            default:
                return (b, null);
        }
    }

    /// <summary>
    /// Index of the '|' (outside quotes) that starts an "auto:" part, else -1. Scans the comma-level tokens, so a quote only
    /// protects a bar when it opens its own choice; in a quoted token the search starts after the closing quote.
    /// </summary>
    private static int AutoBar(string text)
    {
        foreach (var t in Tokenize(text, ','))
        {
            int from;
            if (!t.Quoted) from = t.Start;
            else if (t.Closed) from = t.QuoteEnd + 1;
            else continue;
            for (var i = text.IndexOf('|', from, Math.Max(0, t.End - from)); i >= 0 && i < t.End; i = text.IndexOf('|', i + 1, Math.Max(0, t.End - i - 1)))
                if (text[(i + 1)..].TrimStart().StartsWith("auto:", StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>
    /// An expression has no '|' outside its text literals, so a bar there ends the auto value: when every part after it reads
    /// "always ask" (any case), the parts are the flag. Otherwise the text is returned whole, so the auto value reports its own error.
    /// </summary>
    private static (string Auto, bool AlwaysAsk) SplitAlwaysAskAfterAuto(string auto)
    {
        var cuts = new List<int>();
        var inText = false;
        for (var i = 0; i < auto.Length; i++)
        {
            var c = auto[i];
            if (inText && c == '\\') { i++; continue; }   // the expression lexer's \" and \\ escapes
            if (c == '"') inText = !inText;
            else if (c == '|' && !inText) cuts.Add(i);
        }
        if (cuts.Count == 0) return (auto, false);
        for (var k = 0; k < cuts.Count; k++)
        {
            var end = k + 1 < cuts.Count ? cuts[k + 1] : auto.Length;
            if (!auto[(cuts[k] + 1)..end].Trim().Equals("always ask", StringComparison.OrdinalIgnoreCase)) return (auto, false);
        }
        return (auto[..cuts[0]].Trim(), true);
    }

    /// <summary>Splits at every '|' outside quotes (same scan as <see cref="AutoBar"/>); parts are trimmed.</summary>
    private static List<string> SplitAtBars(string text)
    {
        var cuts = new List<int>();
        foreach (var t in Tokenize(text, ','))
        {
            int from;
            if (!t.Quoted) from = t.Start;
            else if (t.Closed) from = t.QuoteEnd + 1;
            else continue;
            for (var i = text.IndexOf('|', from, Math.Max(0, t.End - from)); i >= 0 && i < t.End; i = text.IndexOf('|', i + 1, Math.Max(0, t.End - i - 1)))
                cuts.Add(i);
        }
        var parts = new List<string>();
        var start = 0;
        foreach (var c in cuts) { parts.Add(text[start..c].Trim()); start = c + 1; }
        parts.Add(text[start..].Trim());
        return parts;
    }

    /// <summary>One piece of a text split at a separator: its range, and whether it is a quoted token.</summary>
    private readonly record struct Token(int Start, int End, bool Quoted, bool Closed, int QuoteEnd);

    /// <summary>
    /// Splits at the separator, but not inside a double-quoted token. Only a token whose trimmed text starts with a
    /// double quote is quoted; a doubled quote inside it is one quote. A quote in the middle of a token is plain text.
    /// An unclosed quoted token runs to the end of the text.
    /// </summary>
    private static List<Token> Tokenize(string text, char separator)
    {
        var tokens = new List<Token>();
        var start = 0;
        while (true)
        {
            var i = start;
            while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] != separator) i++;
            var quoted = i < text.Length && text[i] == '"';
            var closed = false;
            var quoteEnd = -1;
            if (quoted)
            {
                var j = i + 1;
                while (j < text.Length)
                {
                    if (text[j] == '"')
                    {
                        if (j + 1 < text.Length && text[j + 1] == '"') { j += 2; continue; }
                        closed = true; quoteEnd = j;
                        break;
                    }
                    j++;
                }
                i = closed ? j + 1 : text.Length;
            }
            var end = text.IndexOf(separator, i);
            if (end < 0) end = text.Length;
            tokens.Add(new Token(start, end, quoted, closed, quoteEnd));
            if (end >= text.Length) return tokens;
            start = end + 1;
        }
    }

    /// <summary>Splits at the separator outside double quotes. Each part is trimmed; quotes are kept as written.</summary>
    internal static IReadOnlyList<string> SplitOutsideQuotes(string text, char separator) =>
        Tokenize(text, separator).Select(t => text[t.Start..t.End].Trim()).ToList();

    /// <summary>Writes a choice so ParseDetails reads it back: in double quotes (quotes doubled) when it would otherwise be misread.</summary>
    internal static string QuoteChoice(string choice)
    {
        var plain = choice.Length > 0 && choice == choice.Trim() && choice[0] != '"'
            && choice.IndexOfAny([',', '|']) < 0 && !choice.Contains("..", StringComparison.Ordinal)
            && !choice.Equals("always ask", StringComparison.OrdinalIgnoreCase);
        return plain ? choice : "\"" + choice.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Names separated by commas, in the Input-choices format: a name holding a comma is written in "double quotes".</summary>
    public static string FormatNameList(IEnumerable<string> names) => string.Join(", ", names.Select(QuoteChoice));

    /// <summary>The reverse of <see cref="FormatNameList"/>; a blank text gives an empty list.</summary>
    public static (List<string>? Names, string? Error) ParseNameList(string text)
    {
        var (list, err) = ParseChoices(text);
        return err != null ? (null, err) : (list ?? new List<string>(), null);
    }

    private static (List<string>? Choices, string? Error) ParseChoices(string text)
    {
        var choices = new List<string>();
        foreach (var t in Tokenize(text, ','))
        {
            if (!t.Quoted)
            {
                var c = text[t.Start..t.End].Trim();
                if (c.Length > 0) choices.Add(c);
                continue;
            }
            if (!t.Closed) return (null, "A choice has an opening quote but no closing quote.");
            if (text[(t.QuoteEnd + 1)..t.End].Trim().Length > 0) return (null, "Put the whole choice inside the quotes.");
            var open = text.IndexOf('"', t.Start);
            var c2 = text[(open + 1)..t.QuoteEnd].Replace("\"\"", "\"");
            if (c2.Length > 0) choices.Add(c2);
        }
        return (choices.Count == 0 ? null : choices, null);
    }

    /// <summary>Index of the first ".." that is not inside a quoted choice, else -1.</summary>
    private static int DotsOutsideQuotes(string text)
    {
        foreach (var t in Tokenize(text, ','))
        {
            if (t.Quoted) continue;
            var d = text.IndexOf("..", t.Start, t.End - t.Start, StringComparison.Ordinal);
            if (d >= 0) return d;
        }
        return -1;
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
            case BindingKind.Piece:   // a property a piece holds (a holder-only property is never a piece's)
                return pack.Properties.Where(p => p.Scope != PropertyScope.Holders).Select(p => new TargetOption(p.Name, p.Id, null, null)).ToList();
            case BindingKind.Holder:
                return pack.Holders.SelectMany(h => pack.Properties.Select(p => new TargetOption($"{h.Name}: {p.Name}", p.Id, h.Id, null))).ToList();
            case BindingKind.Table:
                return pack.Tables.Select(t => new TargetOption(t.Name, null, null, t.Id)).ToList();
            default:
                return Array.Empty<TargetOption>();
        }
    }

    /// <summary>
    /// The Target choices for this binding: as <see cref="TargetOptions(RulesPack, BindingKind)"/>, except (B9) a Draw binding offers
    /// <see cref="OneEach"/> first and then the properties a counter holds, and a holder picker offers "{Prop} of (chosen holder)" for each
    /// property every listed holder keeps.
    /// </summary>
    public static IReadOnlyList<TargetOption> TargetOptions(RulesPack pack, VariableBinding b)
    {
        if (b.Kind == BindingKind.Draw)
            return new[] { new TargetOption(OneEach, null, null, null) }.Concat(TargetOptions(pack, BindingKind.Piece)).ToList();
        if (b.Kind == BindingKind.Holder && b.HolderChoices is { Count: > 0 } ids)
            return pack.Properties.Where(p => p.Scope != PropertyScope.Pieces && ids.All(id => PropertyFacts.KeptBy(p, id)))
                .Select(p => new TargetOption(ChosenHolderTarget(p.Name), p.Id, null, null)).ToList();
        return TargetOptions(pack, b.Kind);
    }

    public static VariableBinding ApplyTarget(VariableBinding b, TargetOption o) =>
        b with { Property = o.Property, HolderId = o.HolderId, TableId = o.TableId };

    /// <summary>The Target cell text for a binding; "(missing)" when the id no longer resolves, empty for kinds with no target.</summary>
    public static string TargetDisplay(RulesPack pack, VariableBinding b)
    {
        switch (b.Kind)
        {
            case BindingKind.Unit or BindingKind.Global or BindingKind.Piece:
                return b.Property == null ? "" : pack.ById<PropertyDef>(b.Property)?.Name ?? Missing;
            case BindingKind.Draw:
                return b.Property == null ? OneEach : pack.ById<PropertyDef>(b.Property)?.Name ?? Missing;
            case BindingKind.Holder when b.HolderChoices is { Count: > 0 }:
                return b.Property == null ? "" : pack.ById<PropertyDef>(b.Property) is { } chosen ? ChosenHolderTarget(chosen.Name) : Missing;
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

    /// <summary>Changing the target drops a holder and a Holder variable that no longer apply, and a Piece variable unless the target is Piece.</summary>
    public static FormulaOutput WithOutputTarget(FormulaOutput o, OutputTarget t) =>
        o with
        {
            Target = t,
            HolderId = t == OutputTarget.Holder ? o.HolderId : null,
            PieceVariable = t == OutputTarget.Piece ? o.PieceVariable : null,
            HolderVariable = t == OutputTarget.Holder ? o.HolderVariable : null,
        };

    /// <summary>B9: the Holder variable (a holder picker) whose chosen holder a Holder output writes; blank is null (the fixed holder).</summary>
    public static FormulaOutput WithHolderVariable(FormulaOutput o, string? variable) => o with { HolderVariable = Blank(variable) };

    /// <summary>Changing the action drops the fields only another action uses: palette entry unless Place, layer unless SetLayer, map unless
    /// Remove, the property unless Write or Reveal, the property list and key unless Write, the margin unless Reveal (B7).</summary>
    public static FormulaOutput WithOutputAction(FormulaOutput o, OutputAction a) =>
        o with
        {
            Action = a,
            Slot = a == OutputAction.Place ? o.Slot : null,
            Layer = a == OutputAction.SetLayer ? o.Layer : null,
            ToMapId = a == OutputAction.Remove ? o.ToMapId : null,
            PropertyChoice = a == OutputAction.Write ? o.PropertyChoice : null,
            PropertySelect = a == OutputAction.Write ? o.PropertySelect : null,
            Property = a is OutputAction.Write or OutputAction.Reveal ? o.Property : "",
            Key = a == OutputAction.Write ? o.Key : null,
            Margin = a == OutputAction.Reveal ? o.Margin : null,
        };

    /// <summary>B7: how wide a Reveal output's revealed range is (an expression); blank is null (the exact value).</summary>
    public static FormulaOutput WithMargin(FormulaOutput o, string? margin) => o with { Margin = Blank(margin) };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>B6: the key a Write output of a keyed property writes (an expression, e.g. <c>hex()</c>); blank is null.</summary>
    public static FormulaOutput WithKey(FormulaOutput o, string? key) => o with { Key = Blank(key) };

    /// <summary>The Piece variable the output acts on; blank is null.</summary>
    public static FormulaOutput WithPieceVariable(FormulaOutput o, string? variable) => o with { PieceVariable = Blank(variable) };

    /// <summary>The palette entry a Place output places; blank is null.</summary>
    public static FormulaOutput WithSlot(FormulaOutput o, string? slot) => o with { Slot = Blank(slot) };

    /// <summary>The layer a SetLayer output sets; blank is null.</summary>
    public static FormulaOutput WithLayer(FormulaOutput o, string? layer) => o with { Layer = Blank(layer) };

    /// <summary>The map a Remove output moves the counter to; blank is null (delete).</summary>
    public static FormulaOutput WithToMap(FormulaOutput o, string? mapId) => o with { ToMapId = Blank(mapId) };

    /// <summary>A Piece binding's "same hex" switch.</summary>
    public static VariableBinding WithSameHex(VariableBinding b, bool sameHex) => b with { SameHex = sameHex };

    /// <summary>An Input's choices table (a table id); blank is null, and clearing it also clears the filter, which only applies to a table.</summary>
    public static VariableBinding WithChoicesTable(VariableBinding b, string? tableId) =>
        string.IsNullOrWhiteSpace(tableId) ? b with { ChoicesTable = null, ChoicesFilter = null } : b with { ChoicesTable = tableId.Trim() };

    /// <summary>An Input's choices filter expression; blank is null, never "".</summary>
    public static VariableBinding WithChoicesFilter(VariableBinding b, string? filter) =>
        b with { ChoicesFilter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim() };

    /// <summary>The formula's "roll once for the group" switch.</summary>
    public static FormulaDef WithGroupRoll(FormulaDef f, bool on) => f with { GroupRoll = on };

    /// <summary>Sets the output's value expression; blank is null (the formula's result), never "".</summary>
    public static FormulaDef SetOutputValue(FormulaDef f, int index, string? expr) =>
        SetOutput(f, index, o => o with { Expr = string.IsNullOrWhiteSpace(expr) ? null : expr.Trim() });

    /// <summary>Sets the output's property list; an empty list is null (the fixed Property is used), never [].</summary>
    public static FormulaDef SetOutputChoice(FormulaDef f, int index, IReadOnlyList<string>? propertyIds) =>
        SetOutput(f, index, o => o with { PropertyChoice = propertyIds is { Count: > 0 } ? propertyIds.ToList() : null });

    /// <summary>Sets the output's "Pick by" expression; blank is null.</summary>
    public static FormulaDef SetOutputPick(FormulaDef f, int index, string? expr) =>
        SetOutput(f, index, o => o with { PropertySelect = string.IsNullOrWhiteSpace(expr) ? null : expr.Trim() });

    // ---------- usage ----------

    public static IReadOnlyList<FormulaDef> FormulasUsingProperty(RulesPack pack, string propertyId) =>
        pack.Formulas.Where(f => f.Bindings.Any(b => b.Property == propertyId) || f.Outputs.Any(o => o.Property == propertyId || o.PropertyChoice?.Contains(propertyId) == true)).ToList();

    public static IReadOnlyList<FormulaDef> FormulasUsingHolder(RulesPack pack, string holderId) =>
        pack.Formulas.Where(f => f.Bindings.Any(b => b.HolderId == holderId || b.HolderChoices?.Contains(holderId) == true) || f.Outputs.Any(o => o.HolderId == holderId)).ToList();
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

/// <summary>De-duplicates deferred confirmations: one queued or showing ask per key.</summary>
public sealed class AskGate
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    public bool TryEnter(string key) => _keys.Add(key);
    public void Exit(string key) => _keys.Remove(key);
}
