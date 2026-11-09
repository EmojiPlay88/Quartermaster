using System.Globalization;
using Quartermaster.Rules;

namespace Quartermaster.Render;

public sealed record TableRef(string Id);
public sealed record FormulaRef(string Id);

/// <summary>Rules-pack tree: root folders "Tables" and "Formulas", items sorted by name then id.</summary>
public static class TablesTreeModel
{
    public const string TablesKey = "tables", FormulasKey = "formulas";

    public static IReadOnlyList<TreeItem> Build(RulesPack? rules)
    {
        if (rules == null) return [new TreeItem("(no rules pack)", null, null, [])];
        var tables = rules.Tables
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Id, StringComparer.Ordinal)
            .Select(t =>
            {
                var rows = t.Cells.Count;
                var cols = t.Cells.Count == 0 ? 0 : t.Cells.Max(r => r.Count);
                var info = string.Create(CultureInfo.InvariantCulture, $"{t.Kind}, {rows}×{cols}");
                return new TreeItem(t.Name, string.IsNullOrWhiteSpace(t.Notes) ? info : t.Notes + "\n" + info, new TableRef(t.Id), []);
            }).ToList();
        var formulas = rules.Formulas
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.Id, StringComparer.Ordinal)
            .Select(f => new TreeItem(f.Name, string.IsNullOrWhiteSpace(f.Notes) ? null : f.Notes, new FormulaRef(f.Id), []))
            .ToList();
        return [new TreeItem("Tables", null, TablesKey, tables), new TreeItem("Formulas", null, FormulasKey, formulas)];
    }
}
