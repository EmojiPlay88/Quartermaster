using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Pure helpers behind the map view tools (last-moved highlight, centre on selection).</summary>
public static class MapViewTools
{
    /// <summary>Piece ids of the most recent <see cref="MovePieces"/> on <paramref name="mapId"/> in the command list; empty when there is none.</summary>
    public static IReadOnlySet<string> LastMovedIds(IReadOnlyList<GameCommand> log, string mapId)
    {
        for (var i = log.Count - 1; i >= 0; i--)
            if (log[i] is MovePieces m && m.MapId == mapId)
                return new HashSet<string>(m.PieceIds);
        return new HashSet<string>();
    }

    /// <summary>Centre of the selected pieces that sit on the map (mean of their positions); null when none do.</summary>
    public static PointD? SelectionCentre(GameState s, string mapId, IReadOnlySet<string> selected)
    {
        double x = 0, y = 0;
        var n = 0;
        foreach (var id in selected)
        {
            if (!s.Pieces.TryGetValue(id, out var p) || p.MapId != mapId) continue;
            x += p.Pos.X; y += p.Pos.Y; n++;
        }
        return n == 0 ? null : new PointD(x / n, y / n);
    }
}
