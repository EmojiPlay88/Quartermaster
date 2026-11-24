using System.Globalization;
using System.Text;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

public sealed record BreakdownLine(string Amount, string Label, string? Where);

public sealed record Breakdown(string Title, IReadOnlyList<BreakdownLine> Lines);

/// <summary>Builds history-only explanations of a ledger value (never previews a formula result).</summary>
public static class LedgerBreakdown
{
    const int MaxLines = 12;
    const string Minus = "−";
    const string Ellipsis = "…";
    const string Dot = " · ";

    static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    static string Signed(double v)
    {
        var n = Num(Math.Abs(v));
        if (n == "0") return "0";
        return (v < 0 ? Minus : "+") + n;
    }

    public static Breakdown For(GameState s, RulesPack? pack, ModuleModel? model, string key, string propertyId)
    {
        var b = ForCore(s, pack, model, key, propertyId);
        var prop = pack?.ById<PropertyDef>(propertyId);
        return OverrideLine(pack, prop) is { } line ? b with { Lines = b.Lines.Append(new BreakdownLine("", line, null)).ToList() } : b;
    }

    /// <summary>"overrides {table}, column {c}" (1-D: "overrides {table}") for a piece property overriding a table that exists, else null.</summary>
    static string? OverrideLine(RulesPack? pack, PropertyDef? prop)
    {
        if (pack == null || prop == null || prop.Scope == PropertyScope.Holders || string.IsNullOrWhiteSpace(prop.OverridesTable)) return null;
        if (pack.ById<TableDef>(prop.OverridesTable!) is not { } t) return null;
        return t.Kind == TableKind.TwoD
            ? $"overrides {t.Name}, column {Rules.Tables.TableLookup.Normalise(prop.OverridesColumn)}"
            : $"overrides {t.Name}";
    }

    static Breakdown ForCore(GameState s, RulesPack? pack, ModuleModel? model, string key, string propertyId)
    {
        var prop = pack?.ById<PropertyDef>(propertyId);
        if (prop != null && PropertyFacts.IsComputed(prop)) return ForComputed(s, pack!, model, key, prop);
        var value = Ledger.Get(s, key, propertyId) ?? prop?.Default ?? 0;
        var title = $"{prop?.Name ?? propertyId} {Num(value)}  ({OwnerName(s, pack, key)})";

        var history = Ledger.History(s, key, propertyId);
        var lines = new List<BreakdownLine>();
        if (history.Count == 0)
        {
            lines.Add(new BreakdownLine("", "no changes recorded", null));
            return new Breakdown(title, lines);
        }

        int turn = s.Turn?.Turn ?? 1;
        var earlier = history.Where(e => e.Turn != turn).ToList();
        var current = history.Where(e => e.Turn == turn).ToList();
        if (earlier.Count > 0)
            lines.Add(new BreakdownLine("", "Earlier turns: net " + Signed(earlier.Sum(e => e.After - e.Before)), null));
        if (current.Count > 0)
        {
            lines.Add(new BreakdownLine(Num(current[0].Before), $"Start of turn {turn}", null));
            var shown = current;
            if (current.Count > MaxLines)
            {
                var hidden = current.Count - MaxLines;
                var hiddenNet = current.Take(hidden).Sum(e => e.After - e.Before);
                lines.Add(new BreakdownLine(Signed(hiddenNet), $"{Ellipsis} {hidden} more changes", null));
                shown = current.Skip(hidden).ToList();
            }
            foreach (var e in shown)
                lines.Add(new BreakdownLine(Signed(e.After - e.Before), Ledger.SourceLabel(e, pack), Where(e)));
        }
        return new Breakdown(title, lines);
    }

    /// <summary>A computed value has no history: its value (or "error" and the message) and its expression.</summary>
    static Breakdown ForComputed(GameState s, RulesPack pack, ModuleModel? model, string key, PropertyDef prop)
    {
        string shown;
        string? error = null;
        try { shown = PropertyValues.Get(s, pack, model, key, prop) is { } v ? Num(v) : "—"; }
        catch (Rules.Expr.EvalError ex) { shown = "error"; error = ex.Message; }
        var lines = new List<BreakdownLine> { new("", "= " + prop.Expr!.Trim(), null) };
        if (error != null) lines.Add(new BreakdownLine("", error, null));
        return new Breakdown($"{prop.Name} {shown}  ({OwnerName(s, pack, key)})", lines);
    }

    public static Breakdown ForTotal(string title, IEnumerable<(string Label, double Value)> parts)
    {
        var sorted = parts.Select((p, i) => (p, i)).OrderByDescending(x => Math.Abs(x.p.Value)).ThenBy(x => x.i).Select(x => x.p).ToList();
        var lines = sorted.Take(MaxLines).Select(p => new BreakdownLine(Signed(p.Value), p.Label, null)).ToList();
        if (sorted.Count > MaxLines) lines.Add(new BreakdownLine("", $"{Ellipsis} {sorted.Count - MaxLines} more", null));
        return new Breakdown(title, lines);
    }

    public static string Format(Breakdown b)
    {
        var w = b.Lines.Count == 0 ? 0 : b.Lines.Max(l => l.Amount.Length);
        var sb = new StringBuilder(b.Title);
        foreach (var l in b.Lines)
        {
            sb.Append('\n').Append(l.Amount.PadRight(w)).Append("  ").Append(l.Label);
            if (l.Where != null) sb.Append("  ").Append(l.Where);
        }
        return sb.ToString();
    }

    static string Where(LedgerEntry e) =>
        e.Turn == null ? "" : string.IsNullOrEmpty(e.Phase) ? $"Turn {e.Turn}" : $"Turn {e.Turn}{Dot}{e.Phase}";

    static string OwnerName(GameState s, RulesPack? pack, string key)
    {
        if (!LedgerKey.TryParse(key, out var isPiece, out var id)) return key;
        if (isPiece)
        {
            if (s.Pieces.TryGetValue(id, out var piece))
            {
                string n;
                try { n = PieceInfo.Name(piece); } catch (Exception) { n = ""; }
                if (!string.IsNullOrWhiteSpace(n)) return n;
            }
            return id;
        }
        if (id == HolderDef.GlobalId) return "Global";
        var h = pack?.ById<HolderDef>(id)?.Name;
        return string.IsNullOrWhiteSpace(h) ? id : h;
    }
}
