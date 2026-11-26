using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Quartermaster.Vassal;

/// <summary>Hex names per board and the distance between two cells of a grid.</summary>
public static class BoardCells
{
    // Per board: trimmed lower-case name -> board-local point of the cell (null: the board does not have that hex).
    private static readonly ConditionalWeakTable<BoardDef, ConcurrentDictionary<string, PointD?>> Index = new();

    /// <summary>Uncached lookups made on this thread (tests only).</summary>
    [ThreadStatic] internal static int Probes;

    /// <summary>
    /// The board that has a hex called <paramref name="name"/>: <paramref name="prefer"/> first, then every board in module order
    /// (maps in <see cref="ModuleModel.Maps"/> order, boards in map order). A board has a hex when the grid's numbering gives a point
    /// inside the board whose own name is the same (trimmed, ordinal ignore-case). <c>Local</c> is the board-local point of the cell.
    /// </summary>
    public static (MapDef Map, BoardDef Board, PointD Local)? Find(ModuleModel model, string name, BoardDef? prefer)
    {
        var key = (name ?? "").Trim();
        if (key.Length == 0) return null;
        if (prefer != null)
        {
            foreach (var map in model.Maps)
                if (map.Boards.Contains(prefer) && Lookup(prefer, key) is { } p) return (map, prefer, p);
        }
        foreach (var map in model.Maps)
            foreach (var board in map.Boards)
                if (!ReferenceEquals(board, prefer) && Lookup(board, key) is { } p) return (map, board, p);
        return null;
    }

    private static PointD? Lookup(BoardDef board, string key)
    {
        if (board.Grid == null) return null;
        var cache = Index.GetValue(board, _ => new ConcurrentDictionary<string, PointD?>(StringComparer.OrdinalIgnoreCase));
        if (cache.TryGetValue(key, out var hit)) return hit;
        Probes++;
        PointD? result = null;
        var pt = board.Grid.LocationToPoint(key);
        if (pt is { } p && p.X >= 0 && p.Y >= 0 && p.X < board.Width && p.Y < board.Height
            && string.Equals(board.Grid.LocationName(p).Trim(), key, StringComparison.OrdinalIgnoreCase))
            result = p;
        cache[key] = result;
        return result;
    }

    /// <summary>Cells between <paramref name="a"/> and <paramref name="b"/> (board-local pixels); null when there is no usable grid.</summary>
    public static int? Cells(IMapGrid? grid, PointD a, PointD b)
    {
        if (grid is ZonedGrid z) grid = z.GridFor(a, b);
        switch (grid)
        {
            case SquareGrid sq:
                if (!sq.TryCell(sq.Snap(a), out var ac, out var ar) || !sq.TryCell(sq.Snap(b), out var bc, out var br)) return null;
                return Math.Max(Math.Abs(ac - bc), Math.Abs(ar - br));
            case HexGrid hex:
                if (!hex.TryCell(hex.Snap(a), out var c1, out var r1) || !hex.TryCell(hex.Snap(b), out var c2, out var r2)) return null;
                var (x1, y1, z1) = ToCube(c1, r1);
                var (x2, y2, z2) = ToCube(c2, r2);
                return Math.Max(Math.Abs(x1 - x2), Math.Max(Math.Abs(y1 - y2), Math.Abs(z1 - z2)));
            default:
                return null;
        }
    }

    // Odd columns sit half a cell lower ("odd-q").
    private static (int X, int Y, int Z) ToCube(int col, int row)
    {
        int z = row - (col - (col & 1)) / 2;
        return (col, -col - z, z);
    }
}
