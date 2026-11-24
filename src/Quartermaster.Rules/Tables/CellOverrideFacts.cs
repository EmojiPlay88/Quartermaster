using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Quartermaster.Rules.Tables;

/// <summary>Which properties replace a table cell for the piece that holds them (<see cref="PropertyDef.OverridesTable"/>).</summary>
public static class CellOverrideFacts
{
    private static readonly IReadOnlyList<PropertyDef> None = Array.Empty<PropertyDef>();

    /// <summary>
    /// The overriding properties of one pack, by table id, built once per pack. Packs are replaced on edit (the Workbench builds new lists), so
    /// the index is rebuilt only when the pack, its Properties or Tables list, or their counts change (a list grown in place is noticed).
    /// A table nothing overrides has no entry, so a read of it costs one dictionary miss and no allocation.
    /// </summary>
    private sealed class Index
    {
        public readonly List<PropertyDef>? Props;
        public readonly int PropCount;
        public readonly List<TableDef>? Tables;
        public readonly int TableCount;
        public readonly Dictionary<string, Entry> ByTable = new(StringComparer.Ordinal);

        public Index(RulesPack pack)
        {
            Props = pack.Properties; PropCount = Props?.Count ?? 0;
            Tables = pack.Tables; TableCount = Tables?.Count ?? 0;
            foreach (var p in Props ?? new List<PropertyDef>())
            {
                if (p == null || p.Scope == PropertyScope.Holders || string.IsNullOrWhiteSpace(p.OverridesTable)) continue;
                if (!ByTable.TryGetValue(p.OverridesTable!, out var e))
                    ByTable[p.OverridesTable!] = e = new Entry(pack.ById<TableDef>(p.OverridesTable!)?.Kind != TableKind.TwoD);
                e.All.Add(p);
            }
        }

        public bool Fresh(RulesPack pack) =>
            ReferenceEquals(Props, pack.Properties) && PropCount == (pack.Properties?.Count ?? 0)
            && ReferenceEquals(Tables, pack.Tables) && TableCount == (pack.Tables?.Count ?? 0);
    }

    /// <summary>One overridden table: every overriding property in pack order, its per-column lists (by the raw key asked for) and whether one is hidden.</summary>
    private sealed class Entry(bool oneD)
    {
        public readonly bool OneD = oneD;
        public readonly List<PropertyDef> All = new();
        public readonly ConcurrentDictionary<string, IReadOnlyList<PropertyDef>> ByColumn = new(StringComparer.Ordinal);
        public int Hidden = -1;   // -1 not yet worked out, 0 false, 1 true
    }

    private static readonly ConditionalWeakTable<RulesPack, Index> Indexes = new();

    private static Index IndexOf(RulesPack pack)
    {
        if (Indexes.TryGetValue(pack, out var ix) && ix.Fresh(pack)) return ix;
        ix = new Index(pack);
        Indexes.AddOrUpdate(pack, ix);
        return ix;
    }

    /// <summary>The properties overriding the table's column, in pack order (the first wins); Holders-scope properties never override. A 1-D table has no column, so the column is ignored for it.</summary>
    public static IReadOnlyList<PropertyDef> For(RulesPack pack, string tableId, string? columnKey)
    {
        if (tableId == null || !IndexOf(pack).ByTable.TryGetValue(tableId, out var e)) return None;
        if (e.OneD) return e.All;
        return e.ByColumn.GetOrAdd(columnKey ?? "", static (key, all) =>
        {
            var col = TableLookup.Normalise(key);
            var hits = all.Where(p => TableLookup.Normalise(p.OverridesColumn) == col).ToList();
            return hits.Count == 0 ? None : hits;
        }, e.All);
    }

    /// <summary>Every piece property overriding the table, whatever its column, in pack order (empty when none).</summary>
    public static IReadOnlyList<PropertyDef> Overriders(RulesPack pack, string tableId) =>
        tableId != null && IndexOf(pack).ByTable.TryGetValue(tableId, out var e) ? e.All : None;

    /// <summary>True when any property overriding the table is not <see cref="PropertyFacts.IsPublic"/>.</summary>
    public static bool HiddenOverride(RulesPack pack, string tableId)
    {
        if (tableId == null || !IndexOf(pack).ByTable.TryGetValue(tableId, out var e)) return false;
        if (e.Hidden < 0) e.Hidden = e.All.Any(p => !PropertyFacts.IsPublic(pack, p)) ? 1 : 0;
        return e.Hidden == 1;
    }
}
