using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Status-bar texts of a map view (cursor location and zoom), shared by the WinForms and Desktop map views.</summary>
public static class MapStatusText
{
    /// <summary>"x, y" (rounded map pixels), plus two spaces and the grid location name of the board under the point when it has one.</summary>
    public static string Location(MapDef map, PointD m) => Location(map, m, null);

    /// <summary>
    /// As <see cref="Location(MapDef, PointD)"/>; when the point names a location and <paramref name="placeText"/> gives text for it (B6: the
    /// hex's place values, see <see cref="PlaceText"/>), two spaces, "·", two spaces and that text follow: <c>{x}, {y}  {hex}  ·  {place text}</c>.
    /// </summary>
    public static string Location(MapDef map, PointD m, Func<string, string?>? placeText)
    {
        var loc = $"{(int)Math.Round(m.X)}, {(int)Math.Round(m.Y)}";
        foreach (var b in map.Boards)
        {
            var lx = m.X - b.Offset.X;
            var ly = m.Y - b.Offset.Y;
            if (lx < 0 || ly < 0 || lx >= b.Width || ly >= b.Height) continue;
            var name = b.Grid?.LocationName(new PointD(lx, ly)) ?? "";
            if (name.Length > 0)
            {
                loc += "  " + name;
                if (placeText?.Invoke(name) is { Length: > 0 } text) loc += "  ·  " + text;
            }
            break;
        }
        return loc;
    }

    /// <summary>
    /// The place text of a hex in <paramref name="view"/>, the viewer's state (a player view has left out what it may not see):
    /// <see cref="KeyedValues.PlaceText"/> of <see cref="KeyedValues.PlacesAt"/>, null when the hex has none. Null as a whole when there is no
    /// view or the pack has no place property (a pack without places pays nothing).
    /// </summary>
    public static Func<string, string?>? PlaceText(GameState? view, RulesPack? pack)
    {
        if (view == null || pack?.Properties?.Any(KeyedFacts.IsPlace) != true) return null;
        return hex => KeyedValues.PlacesAt(view, pack, hex) is { Count: > 0 } places ? KeyedValues.PlaceText(pack, places) : null;
    }

    /// <summary>The map tooltip of a hex with place values: <c>{hex}: {place text}</c>; null when it has none.</summary>
    public static string? Tooltip(string hex, Func<string, string?>? placeText) =>
        string.IsNullOrEmpty(hex) || placeText?.Invoke(hex) is not { Length: > 0 } text ? null : $"{hex}: {text}";

    /// <summary>The zoom as "75%" (<see cref="Viewport.Zoom"/>; multiplied in float, as the WinForms map view always did).</summary>
    public static string Zoom(float zoom) => $"{Math.Round(zoom * 100)}%";
}
