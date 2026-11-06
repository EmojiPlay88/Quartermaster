using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Distance a piece moved, in grid cells of the board under both points. Never throws.</summary>
public static class MoveMeasure
{
    /// <summary>Cells moved (board-local, via <see cref="HexDistance"/>), or (0, problem) when it cannot be measured.</summary>
    public static (int Cells, string? Problem) Cells(ModuleModel m, string fromMap, PointD from, string toMap, PointD to)
    {
        try
        {
            if (!string.Equals(fromMap, toMap, StringComparison.Ordinal)) return (0, "different maps");
            var map = m.Maps.FirstOrDefault(x => x.Id == fromMap) ?? m.Maps.FirstOrDefault(x => x.Name == fromMap);
            if (map == null) return (0, "no grid");
            var a = BoardAt(map, from);
            var b = BoardAt(map, to);
            if (a == null || b == null) return (0, "no grid");
            if (!ReferenceEquals(a, b)) return (0, "different boards");
            if (a.Grid == null) return (0, "no grid");
            var n = HexDistance.Cells(a, b, Local(a, from), Local(b, to));
            return n is { } cells ? (cells, null) : (0, "no grid");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (0, "no grid");
        }
    }

    private static BoardDef? BoardAt(MapDef map, PointD p)
    {
        foreach (var b in map.Boards)
        {
            var lx = p.X - b.Offset.X;
            var ly = p.Y - b.Offset.Y;
            if (lx >= 0 && ly >= 0 && lx < b.Width && ly < b.Height) return b;
        }
        return null;
    }

    private static PointD Local(BoardDef b, PointD p) => new(p.X - b.Offset.X, p.Y - b.Offset.Y);
}
