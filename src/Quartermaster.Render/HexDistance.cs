using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Distance between two board locations in grid cells (line-of-sight / range tool).</summary>
public static class HexDistance
{
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

    /// <summary>Same, but null when the points are on different boards.</summary>
    public static int? Cells(BoardDef? boardA, BoardDef? boardB, PointD a, PointD b)
        => boardA == null || !ReferenceEquals(boardA, boardB) ? null : Cells(boardA.Grid, a, b);

    // Odd columns sit half a cell lower ("odd-q").
    private static (int X, int Y, int Z) ToCube(int col, int row)
    {
        int z = row - (col - (col & 1)) / 2;
        return (col, -col - z, z);
    }
}
