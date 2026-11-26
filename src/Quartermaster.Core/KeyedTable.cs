using Quartermaster.Rules;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Core;

/// <summary>One keyed value of a counter or holder (B6): its property, key, value (the Default when none is stored) and whether one is stored.</summary>
public sealed record KeyedRow(PropertyDef Property, string Key, double Value, bool Stored);

/// <summary>One hex of the Places tab: its name and one value per <see cref="PlacesResult.Columns"/> entry (null = none stored).</summary>
public sealed record PlaceRow(string Hex, IReadOnlyList<double?> Values);

/// <summary>The Places tab: the place properties (columns, pack order) and the hexes with a stored place value.</summary>
public sealed record PlacesResult(IReadOnlyList<PropertyDef> Columns, IReadOnlyList<PlaceRow> Rows);

/// <summary>Pure models of the Ledger's keyed values (B6): the small table of one row's keyed values and the Places tab.</summary>
public static class KeyedTable
{
    /// <summary>
    /// The keyed values of a ledger row of <paramref name="view"/>, property by property in pack order. A piece row lists the keyed Pieces and Both
    /// properties that apply to it, a holder row the keyed Holders and Both properties that holder keeps; place properties are left out (they have
    /// the Places tab). A table-keyed property lists every row key of its table, in table order; a turn-keyed one its stored keys and the current
    /// turn, ascending.
    /// </summary>
    public static IReadOnlyList<KeyedRow> For(GameState view, RulesPack pack, string ledgerKey)
    {
        var rows = new List<KeyedRow>();
        if (view == null || pack == null || !LedgerKey.TryParse(ledgerKey, out var isPiece, out var id)) return rows;
        PieceInstance? piece = null;
        if (isPiece && !view.Pieces.TryGetValue(id, out piece)) return rows;
        foreach (var p in pack.Properties)
        {
            if (p == null || !KeyedFacts.IsKeyed(p) || KeyedFacts.IsPlace(p)) continue;
            if (isPiece ? p.Scope == PropertyScope.Holders || !PieceFilter.Compile(p.AppliesTo, view, pack)(piece!)
                    : p.Scope == PropertyScope.Pieces || !PropertyFacts.KeptBy(p, id)) continue;
            var (kind, tableId) = KeyedFacts.Parse(p.KeyedBy);
            IEnumerable<string> keys;
            if (kind == KeyKind.Table)
            {
                if (pack.ById<TableDef>(tableId!) is not { } table) continue;
                keys = table.RowKeys.Select(TableLookup.Normalise).Distinct(StringComparer.Ordinal);
            }
            else
            {
                var stored = KeyedValues.Keys(view, ledgerKey, p.Id).ToList();
                var current = ((int)PieceFilter.TurnOf(view)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!stored.Contains(current, StringComparer.Ordinal)) stored.Add(current);
                keys = stored.OrderBy(k => KeyedValues.TurnOf(k) ?? int.MaxValue).ThenBy(k => k, StringComparer.Ordinal);
            }
            foreach (var k in keys)
            {
                var v = KeyedValues.Get(view, ledgerKey, p.Id, k);
                rows.Add(new KeyedRow(p, k, v ?? p.Default, v != null));
            }
        }
        return rows;
    }

    /// <summary>
    /// The Places tab of <paramref name="view"/>: the place properties as columns (pack order) and one row per hex with any stored place value,
    /// ordered by name (ordinal, ignoring case); <paramref name="filter"/> (trimmed) keeps the hexes whose name contains it, ignoring case.
    /// </summary>
    public static PlacesResult Places(GameState view, RulesPack pack, string filter)
    {
        var cols = (pack?.Properties ?? new()).Where(KeyedFacts.IsPlace).ToList();
        var rows = new List<PlaceRow>();
        if (view?.Keyed == null || cols.Count == 0 || !view.Keyed.TryGetValue(LedgerKey.Holder(HolderDef.GlobalId), out var props) || props == null)
            return new PlacesResult(cols, rows);
        var hexes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cols)
            if (props.TryGetValue(c.Id, out var values) && values != null)
                foreach (var k in values.Keys) if (seen.Add(k)) hexes.Add(k);
        var f = (filter ?? "").Trim();
        foreach (var hex in hexes.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ThenBy(h => h, StringComparer.Ordinal))
        {
            if (f.Length > 0 && !hex.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(new PlaceRow(hex, cols.Select(c => KeyedValues.Get(view, LedgerKey.Holder(HolderDef.GlobalId), c.Id, hex)).ToList()));
        }
        return new PlacesResult(cols, rows);
    }
}
