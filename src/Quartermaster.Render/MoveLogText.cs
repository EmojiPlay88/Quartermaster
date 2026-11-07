using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>
/// Readable log text of a move: "{who} moved {from} → {to}". The log line carries only the destination map, so the source location is
/// left out when the source map has an owner (otherwise a player could learn where another side's unit stood on that side's map).
/// </summary>
public static class MoveLogText
{
    /// <summary>The text, or null when none of the moved pieces exists.</summary>
    public static string? For(MovePieces m, GameState st, ModuleModel model, RulesPack? rules)
    {
        var first = m.PieceIds.Select(id => st.Pieces.GetValueOrDefault(id)).FirstOrDefault(p => p != null);
        if (first == null) return null;
        var from = IsOwned(first.MapId, model, rules) ? null : PieceInfo.Location(first, model);
        var to = PieceInfo.Location(new PieceInstance { MapId = m.MapId, Pos = m.Pos }, model);
        var who = m.PieceIds.Count == 1 ? Name(first) : $"{m.PieceIds.Count} pieces";
        return from != null && to != null ? $"{who} moved {from} → {to}" : to != null ? $"{who} moved → {to}" : $"{who} moved";
    }

    /// <summary>True when a side owns the map (by id, or by the name of a module map, as <see cref="PieceAccess.MapHidden"/> reads it).</summary>
    public static bool IsOwned(string? mapId, ModuleModel model, RulesPack? rules)
    {
        if (mapId == null || rules == null) return false;
        if (Ownership.SideOfMap(mapId, rules) != null) return true;
        return model.Maps.Any(x => x.Name == mapId && x.Id != mapId && Ownership.SideOfMap(x.Id, rules) != null);
    }

    private static string Name(PieceInstance p)
    {
        var n = PieceInfo.Name(p);
        return string.IsNullOrWhiteSpace(n) ? "piece" : n;
    }
}
