namespace Quartermaster.Rules.Tables;

/// <summary>Which properties replace a table cell for the piece that holds them (<see cref="PropertyDef.OverridesTable"/>).</summary>
public static class CellOverrideFacts
{
    /// <summary>The properties overriding the table's column, in pack order (the first wins). A 1-D table has no column, so the column is ignored for it.</summary>
    public static IReadOnlyList<PropertyDef> For(RulesPack pack, string tableId, string? columnKey)
    {
        var oneD = pack.ById<TableDef>(tableId)?.Kind != TableKind.TwoD;
        var col = TableLookup.Normalise(columnKey);
        return pack.Properties
            .Where(p => !string.IsNullOrWhiteSpace(p.OverridesTable) && p.OverridesTable == tableId
                && (oneD || TableLookup.Normalise(p.OverridesColumn) == col))
            .ToList();
    }

    /// <summary>True when any property overriding the table is not <see cref="PropertyFacts.IsPublic"/>.</summary>
    public static bool HiddenOverride(RulesPack pack, string tableId) =>
        pack.Properties.Any(p => !string.IsNullOrWhiteSpace(p.OverridesTable) && p.OverridesTable == tableId && !PropertyFacts.IsPublic(pack, p));
}
