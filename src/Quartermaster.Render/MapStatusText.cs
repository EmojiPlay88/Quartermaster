using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Status-bar texts of a map view (cursor location and zoom), shared by the WinForms and Desktop map views.</summary>
public static class MapStatusText
{
    /// <summary>"x, y" (rounded map pixels), plus two spaces and the grid location name of the board under the point when it has one.</summary>
    public static string Location(MapDef map, PointD m)
    {
        var loc = $"{(int)Math.Round(m.X)}, {(int)Math.Round(m.Y)}";
        foreach (var b in map.Boards)
        {
            var lx = m.X - b.Offset.X;
            var ly = m.Y - b.Offset.Y;
            if (lx < 0 || ly < 0 || lx >= b.Width || ly >= b.Height) continue;
            var name = b.Grid?.LocationName(new PointD(lx, ly)) ?? "";
            if (name.Length > 0) loc += "  " + name;
            break;
        }
        return loc;
    }

    /// <summary>The zoom as "75%" (<see cref="Viewport.Zoom"/>; multiplied in float, as the WinForms map view always did).</summary>
    public static string Zoom(float zoom) => $"{Math.Round(zoom * 100)}%";
}
