using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>UI-free rules of the inventory window: the key-name choices and the hover explanation of a Total.</summary>
public static class InventoryRules
{
    /// <summary>What the second combo offers for a key: markers present in <paramref name="view"/>, or the pack's piece-scoped properties sorted by name; nothing otherwise.</summary>
    public static IReadOnlyList<string> KeyNames(RulesPack? pack, InventoryKey key, ModuleModel m, GameState? view = null) => key switch
    {
        InventoryKey.Marker => view is { } g ? InventoryQuery.MarkerKeys(g) : Array.Empty<string>(),
        InventoryKey.LedgerProperty => pack?.Properties.Where(p => p.Scope is PropertyScope.Pieces or PropertyScope.Both).Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
        _ => Array.Empty<string>(),
    };

    /// <summary>The pieces that add up to a row's total; null when the property is unknown or the row has no total.</summary>
    public static Breakdown? TotalBreakdown(InventoryRow row, string prop, GameState view, RulesPack pack, bool visibleOnly)
    {
        var def = pack.PropertyByName((prop ?? "").Trim());
        if (row.Total is not { } total || def == null) return null;
        var parts = new List<(string, double)>();
        foreach (var id in row.PieceIds)
        {
            if (!view.Pieces.TryGetValue(id, out var p) || Ledger.Get(view, LedgerKey.Piece(id), def.Id) is not { } v) continue;
            string name;
            try { name = PieceInfo.Name(p); } catch (Exception ex) when (ex is not OutOfMemoryException) { name = ""; }
            parts.Add((string.IsNullOrWhiteSpace(name) ? id : name, v));
        }
        var title = $"{def.Name} {LedgerTable.FormatNumber(total)}  ({row.Group}, {row.Count} {(row.Count == 1 ? "piece" : "pieces")})";
        if (visibleOnly) title += " — " + ModuleSession.VisibleOnlyText;
        return LedgerBreakdown.ForTotal(title, parts);
    }

    /// <summary>The tooltip text of <see cref="TotalBreakdown"/>; empty when there is none.</summary>
    public static string TotalTip(InventoryRow row, string prop, GameState view, RulesPack pack, bool visibleOnly) =>
        TotalBreakdown(row, prop, view, pack, visibleOnly) is { } b ? LedgerBreakdown.Format(b) : "";
}
