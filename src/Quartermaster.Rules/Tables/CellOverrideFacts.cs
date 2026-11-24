namespace Quartermaster.Rules.Tables;

/// <summary>Which properties replace a table cell for the piece that holds them (<see cref="PropertyDef.OverridesTable"/>).</summary>
public static class CellOverrideFacts
{
    /// <summary>The properties overriding the table's column, in pack order (the first wins); Holders-scope properties never override. A 1-D table has no column, so the column is ignored for it.</summary>
    public static IReadOnlyList<PropertyDef> For(RulesPack pack, string tableId, string? columnKey)
    {
        var oneD = pack.ById<TableDef>(tableId)?.Kind != TableKind.TwoD;
        var col = TableLookup.Normalise(columnKey);
        return pack.Properties
            .Where(p => Overrides(p, tableId)
                && (oneD || TableLookup.Normalise(p.OverridesColumn) == col))
            .ToList();
    }

    /// <summary>True when any property overriding the table is not <see cref="PropertyFacts.IsPublic"/>.</summary>
    public static bool HiddenOverride(RulesPack pack, string tableId) =>
        pack.Properties.Any(p => Overrides(p, tableId) && !PropertyFacts.IsPublic(pack, p));

    /// <summary>A piece property (not Holders scope) naming the table as the one it overrides.</summary>
    private static bool Overrides(PropertyDef p, string tableId) =>
        p.Scope != PropertyScope.Holders && !string.IsNullOrWhiteSpace(p.OverridesTable) && p.OverridesTable == tableId;
}
