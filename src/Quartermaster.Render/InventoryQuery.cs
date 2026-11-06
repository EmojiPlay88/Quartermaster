using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

public enum InventoryKey { Name, Map, Marker, LedgerProperty }

/// <summary>One inventory group. <see cref="Total"/> is only set for ledger-property queries.</summary>
public sealed record InventoryRow(string Group, int Count, double? Total, IReadOnlyList<string> PieceIds);

/// <summary>Counts placed pieces grouped by name, map, marker value or ledger property. Pure; never throws on odd pieces.</summary>
public static class InventoryQuery
{
    public const string NoneGroup = "(none)";

    public static IReadOnlyList<InventoryRow> Run(GameState s, ModuleModel m, RulesPack? pack, InventoryKey key, string? keyName, string filter)
    {
        var f = (filter ?? "").Trim();
        PropertyDef? prop = null;
        if (key == InventoryKey.LedgerProperty)
        {
            if (pack == null || string.IsNullOrWhiteSpace(keyName)) return Array.Empty<InventoryRow>();
            prop = pack.PropertyByName(keyName.Trim());
            if (prop == null) return Array.Empty<InventoryRow>();
        }
        if (key == InventoryKey.Marker && string.IsNullOrWhiteSpace(keyName)) return Array.Empty<InventoryRow>();

        var groups = new Dictionary<string, (string Label, List<string> Ids, double Total)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in s.Pieces.Values)
        {
            string name;
            try { name = PieceInfo.Name(p) ?? ""; } catch (Exception ex) when (ex is not OutOfMemoryException) { name = ""; }
            if (f.Length > 0 && !name.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            double add = 0;
            string group;
            switch (key)
            {
                case InventoryKey.Map:
                    var map = m.Maps.FirstOrDefault(x => x.Id == p.MapId) ?? m.Maps.FirstOrDefault(x => x.Name == p.MapId);
                    group = Or(map?.Name ?? p.MapId);
                    break;
                case InventoryKey.Marker:
                    group = Markers(p).TryGetValue(keyName!.Trim(), out var v) ? Or(v) : NoneGroup;
                    break;
                case InventoryKey.LedgerProperty:
                    if (Ledger.Get(s, LedgerKey.Piece(p.Id), prop!.Id) is not { } val) continue;
                    add = val;
                    group = Or(name);
                    break;
                default:
                    group = Or(name);
                    break;
            }
            if (!groups.TryGetValue(group, out var g)) g = (group, new List<string>(), 0);
            g.Ids.Add(p.Id);
            groups[group] = (g.Label, g.Ids, g.Total + add);
        }
        return groups.Values.OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Label, StringComparer.Ordinal)
            .Select(g => new InventoryRow(g.Label, g.Ids.Count, key == InventoryKey.LedgerProperty ? g.Total : null, g.Ids)).ToList();
    }

    /// <summary>Distinct marker keys found on the placed pieces, sorted.</summary>
    public static IReadOnlyList<string> MarkerKeys(GameState s) =>
        s.Pieces.Values.SelectMany(p => Markers(p).Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    static IReadOnlyDictionary<string, string> Markers(PieceInstance p)
    {
        try { return PieceInfo.Markers(p); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new Dictionary<string, string>(); }
    }

    static string Or(string? v) => string.IsNullOrWhiteSpace(v) ? NoneGroup : v;
}
