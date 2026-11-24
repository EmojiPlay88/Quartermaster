using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tables;

/// <summary>The text a property's labels table gives for a value, e.g. "3 (Fog)". Values stay numbers; the label is display only.</summary>
public static class PropertyLabels
{
    /// <summary>The trimmed cell of the labels table for the value, or null when the property has no labels table, the table is not one-column, nothing matches or the cell is blank.</summary>
    public static string? Label(RulesPack pack, PropertyDef p, double value)
    {
        if (string.IsNullOrWhiteSpace(p.Labels) || pack.ById<TableDef>(p.Labels!) is not { } t) return null;
        if (t.Kind == TableKind.TwoD) return null;
        try
        {
            var (r, c) = TableLookup.Locate(t, Value.Of(value), null);
            if (r < 0 || r >= t.Cells.Count || c < 0 || c >= t.Cells[r].Count) return null;
            var cell = (t.Cells[r][c] ?? "").Trim();
            return cell.Length == 0 ? null : cell;
        }
        catch (EvalError) { return null; }
    }

    /// <summary>The value as a number, followed by its label in brackets when it has one.</summary>
    public static string Text(RulesPack pack, PropertyDef p, double value)
    {
        var n = Value.FormatNumber(value);
        return Label(pack, p, value) is { } l ? $"{n} ({l})" : n;
    }
}
