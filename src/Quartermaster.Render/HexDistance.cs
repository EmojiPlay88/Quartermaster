using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Distance between two board locations in grid cells (line-of-sight / range tool).</summary>
public static class HexDistance
{
    /// <summary>Cells between <paramref name="a"/> and <paramref name="b"/> (board-local pixels); null when there is no usable grid.</summary>
    public static int? Cells(IMapGrid? grid, PointD a, PointD b)
        => BoardCells.Cells(grid, a, b);

    /// <summary>Same, but null when the points are on different boards.</summary>
    public static int? Cells(BoardDef? boardA, BoardDef? boardB, PointD a, PointD b)
        => boardA == null || !ReferenceEquals(boardA, boardB) ? null : Cells(boardA.Grid, a, b);
}
