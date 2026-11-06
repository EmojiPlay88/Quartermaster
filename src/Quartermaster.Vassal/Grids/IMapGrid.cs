namespace Quartermaster.Vassal;

public readonly record struct PointD(double X, double Y);

public readonly record struct RectD(double X, double Y, double W, double H);

/// <summary>A board grid (square, hex or zoned). Coordinates are board pixels.</summary>
public interface IMapGrid
{
    /// <summary>Nearest legal grid location; the input when the grid has snapTo=false.</summary>
    PointD Snap(PointD p);

    /// <summary>Numbering name of the location containing <paramref name="p"/>; "" when the grid has no numbering. Never throws.</summary>
    string LocationName(PointD p);

    /// <summary>Inverse of <see cref="LocationName"/> (VASSAL getLocation). Null when the name cannot be parsed.</summary>
    PointD? LocationToPoint(string location);

    /// <summary>VASSAL isVisible: lines, dots or numbering are shown.</summary>
    bool Visible { get; }

    RgbColor Color { get; }

    /// <summary>Grid line segments near <paramref name="area"/> (not clipped to it). Empty unless the grid's own lines are visible.</summary>
    IEnumerable<(PointD A, PointD B)> OverlayLines(RectD area);
}

public readonly record struct SizeD(double W, double H);

/// <summary>Builds grids from buildFile nodes.</summary>
public static class GridFactory
{
    /// <summary>Builds the grid child of a Board node; null when the board has none.</summary>
    /// <param name="containerSize">Real board size (e.g. read from the board image); overrides the Board width/height attributes when positive.</param>
    public static IMapGrid? From(ModuleNode boardNode, DiagnosticLog log, SizeD? containerSize = null)
    {
        foreach (var c in boardNode.Children)
        {
            if (c.ShortName is "SquareGrid" or "HexGrid" or "ZonedGrid" or "RegionGrid")
                return FromGridNode(c, log, containerSize);
        }
        return null;
    }

    /// <summary>Builds a grid from a SquareGrid/HexGrid/ZonedGrid node (also used for zone-owned grids). Unsupported grids give null plus a diagnostic.</summary>
    public static IMapGrid? FromGridNode(ModuleNode gridNode, DiagnosticLog log, SizeD? containerSize = null)
    {
        var size = containerSize is { W: > 0, H: > 0 } given && gridNode.Parent?.ShortName == "Board" ? given : ContainerSize(gridNode.Parent);
        switch (gridNode.ShortName)
        {
            case "SquareGrid": return new SquareGrid(gridNode, size, log);
            case "HexGrid": return new HexGrid(gridNode, size, log);
            case "ZonedGrid": return new ZonedGrid(gridNode, size, log);
            default:
                log.AddOnce("grid-unsupported:" + gridNode.ShortName, DiagnosticSeverity.Warning, "Grid",
                    $"Unsupported grid type '{gridNode.ShortName}' ignored.", gridNode.ClassName);
                return null;
        }
    }

    /// <summary>Size of the VASSAL GridContainer that holds a grid: the board, a zone's polygon bounds, or the zoned grid's container.</summary>
    internal static SizeD ContainerSize(ModuleNode? n)
    {
        while (n != null)
        {
            if (n.ShortName == "Zone")
            {
                var (_, _, w, h) = Polygon.Parse(n.Attr("path")).Bounds;
                return new SizeD(w, h);
            }
            if (n.ShortName != "ZonedGrid")
                return new SizeD(GridAttr.Double(n, "width", 0, null), GridAttr.Double(n, "height", 0, null));
            n = n.Parent;
        }
        return new SizeD(0, 0);
    }
}
