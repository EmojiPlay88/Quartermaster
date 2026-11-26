using System.Globalization;
using System.Text;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// One row of the ledger grid. Values align with <see cref="LedgerTable.Columns"/>; null means no ledger value (for a computed column: it
/// does not apply, or its evaluation failed). Errors: column index to the error message of a computed cell that failed; null when none did.
/// NotKept: column indexes of a holder row's properties that this holder does not keep (<see cref="PropertyDef.Holders"/>); their value is
/// always null, so a stale stored value never shows. Null when the row keeps every column.
/// </summary>
public sealed record LedgerRow(string Key, string Name, string Kind, IReadOnlyList<double?> Values, IReadOnlyDictionary<int, string>? Errors = null,
    IReadOnlySet<int>? NotKept = null);

/// <summary>Pure model behind the ledger window: columns, rows, filtering and CSV export.</summary>
public static class LedgerTable
{
    /// <summary>All properties except keyed ones (they have no single value; B6), ordered by name (then id).</summary>
    public static IReadOnlyList<PropertyDef> Columns(RulesPack p) =>
        p.Properties.Where(x => !KeyedFacts.IsKeyed(x)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Holders (global first, then pack order), then pieces with at least one ledger value, by map order then name.
    /// With <paramref name="includeEmpty"/>, pieces matching a Pieces/Both property's filter but holding no values are added too.
    /// </summary>
    public static IReadOnlyList<LedgerRow> Rows(GameState s, RulesPack p, ModuleModel m, string filter, bool includeEmpty = false)
    {
        var cols = Columns(p);
        AggregateFilters? filters = null;   // shared by every computed cell of this call
        LedgerRow Make(string key, string name, string kind, string? holderId = null)
        {
            var vals = new double?[cols.Count];
            Dictionary<int, string>? errors = null;
            HashSet<int>? notKept = null;
            for (var i = 0; i < cols.Count; i++)
            {
                if (holderId != null && cols[i].Scope != PropertyScope.Pieces && !PropertyFacts.KeptBy(cols[i], holderId))
                {
                    (notKept ??= new()).Add(i);   // blank: this holder does not keep the property
                    continue;
                }
                if (!PropertyFacts.IsComputed(cols[i])) { vals[i] = Ledger.Get(s, key, cols[i].Id); continue; }
                try { vals[i] = PropertyValues.Get(s, p, m, key, cols[i], filters ??= new(s, p, m)); }
                catch (EvalError ex) { (errors ??= new())[i] = ex.Message; }
            }
            return new LedgerRow(key, name, kind, vals, errors, notKept);
        }

        var rows = new List<LedgerRow> { Make(LedgerKey.Holder(HolderDef.GlobalId), "Global", "Holder", HolderDef.GlobalId) };
        foreach (var h in p.Holders)
            if (h.Id != HolderDef.GlobalId) rows.Add(Make(LedgerKey.Holder(h.Id), h.Name, "Holder", h.Id));

        var mapIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < m.Maps.Count; i++)
        {
            mapIndex.TryAdd(m.Maps[i].Id, i);
            mapIndex.TryAdd(m.Maps[i].Name, i);
        }
        var pieceProps = p.Properties.Where(x => x.Scope != PropertyScope.Holders).ToList();
        var pieces = new List<(int Map, string Name, PieceInstance Piece, string MapName)>();
        foreach (var piece in s.Pieces.Values)
        {
            var key = LedgerKey.Piece(piece.Id);
            var has = s.Ledgers.TryGetValue(key, out var inner) && inner.Count > 0;
            if (!has)
            {
                if (!includeEmpty) continue;
                if (!pieceProps.Any(x => PieceFilter.Matches(x.AppliesTo, piece, s, m, p))) continue;
            }
            var map = m.Maps.FirstOrDefault(x => x.Id == piece.MapId) ?? m.Maps.FirstOrDefault(x => x.Name == piece.MapId);
            var name = PieceInfo.Name(piece);
            pieces.Add((mapIndex.TryGetValue(piece.MapId, out var mi) ? mi : int.MaxValue, name.Length > 0 ? name : piece.Id, piece, map?.Name ?? piece.MapId));
        }
        foreach (var x in pieces.OrderBy(x => x.Map).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Piece.Id, StringComparer.Ordinal))
            rows.Add(Make(LedgerKey.Piece(x.Piece.Id), x.Name, "Unit · " + x.MapName));

        if (string.IsNullOrWhiteSpace(filter)) return rows;
        var f = filter.Trim();
        return rows.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Kind.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>UI/CSV number text: invariant culture, "0.##".</summary>
    public static string FormatNumber(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>RFC 4180 (CRLF line ends); numbers invariant "0.##"; empty for missing values.</summary>
    public static string ToCsv(IReadOnlyList<PropertyDef> cols, IReadOnlyList<LedgerRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(Field("Name")).Append(',').Append(Field("Kind"));
        foreach (var c in cols) sb.Append(',').Append(Field(c.Name));
        sb.Append("\r\n");
        foreach (var r in rows)
        {
            sb.Append(Field(r.Name)).Append(',').Append(Field(r.Kind));
            for (var i = 0; i < cols.Count; i++)
            {
                sb.Append(',');
                if (i < r.Values.Count && r.Values[i] is { } v) sb.Append(v.ToString("0.##", CultureInfo.InvariantCulture));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    static string Field(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
