// Ported from VASSAL SquareGrid.java (LGPL-2.1).
namespace Quartermaster.Vassal;

public sealed class SquareGrid : IMapGrid
{
    private readonly bool _snapTo;
    private readonly bool _cornersLegal;
    private readonly bool _edgesLegal;
    private readonly bool _visible;
    private readonly bool _dotsVisible;
    private readonly SquareGridNumbering? _numbering;

    internal double Dx { get; }
    internal double Dy { get; }
    internal int OriginX { get; }
    internal int OriginY { get; }

    public RgbColor Color { get; }

    internal SquareGrid(ModuleNode node, SizeD container, DiagnosticLog log)
    {
        Dx = GridAttr.Double(node, "dx", 48.0, log);
        Dy = GridAttr.Double(node, "dy", 48.0, log);
        OriginX = GridAttr.Int(node, "x0", 24, log);
        OriginY = GridAttr.Int(node, "y0", 24, log);
        _snapTo = GridAttr.Bool(node, "snapTo", true);
        _cornersLegal = GridAttr.Bool(node, "cornersLegal", false);
        _edgesLegal = GridAttr.Bool(node, "edgesLegal", false);
        _visible = GridAttr.Bool(node, "visible", false);
        _dotsVisible = GridAttr.Bool(node, "dotsVisible", false);
        Color = GridAttr.Color(node, log);
        if (Dx <= 0 || Dy <= 0)
        {
            log.AddOnce("grid-size:" + node.ClassName + Dx + Dy, DiagnosticSeverity.Warning, "Grid",
                "SquareGrid has a non-positive cell size; snapping disabled.", node.ClassName);
        }
        foreach (var c in node.Children)
        {
            if (c.ShortName == "SquareGridNumbering") _numbering = new SquareGridNumbering(c, this, container, log);
            else
                log.AddOnce("grid-child:" + c.ShortName, DiagnosticSeverity.Info, "Grid",
                    $"Unsupported SquareGrid child '{c.ShortName}' ignored.", c.ClassName);
        }
    }

    public bool Visible => _visible || _dotsVisible || (_numbering?.Visible ?? false);

    private bool Degenerate => !(Dx > 0 && Dy > 0);

    public PointD Snap(PointD p)
    {
        if (!_snapTo || Degenerate) return p;
        var (x, y) = SnapTo(JavaMath.Round(p.X), JavaMath.Round(p.Y));
        return new PointD(x, y);
    }

    private (int X, int Y) SnapTo(int px, int py)
    {
        int offsetX = px - OriginX;
        int nx = JavaMath.Round(offsetX / (0.5 * Dx));
        int offsetY = py - OriginY;
        int ny = JavaMath.Round(offsetY / (0.5 * Dy));

        if (!_cornersLegal || !_edgesLegal)
        {
            if (_cornersLegal)
            {
                if (ny % 2 == 0) nx = 2 * JavaMath.Round(offsetX / Dx);           // on a cell center
                else nx = 1 + 2 * JavaMath.Round(offsetX / Dx - 0.5);              // on a corner
            }
            else if (_edgesLegal)
            {
                if (ny % 2 == 0)
                {
                    if (nx % 2 == 0) nx = 2 * JavaMath.Round(offsetX / Dx);        // cell center
                }
                else nx = 2 * JavaMath.Round(offsetX / Dx);                        // horizontal edge
            }
            else
            {
                nx = 2 * JavaMath.Round(offsetX / Dx);
                ny = 2 * JavaMath.Round(offsetY / Dy);
            }
        }
        return (OriginX + JavaMath.Round(nx * Dx / 2), OriginY + JavaMath.Round(ny * Dy / 2));
    }

    /// <summary>Cell column/row containing <paramref name="p"/>. False when the grid is degenerate.</summary>
    public bool TryCell(PointD p, out int col, out int row)
    {
        col = row = 0;
        if (Degenerate) return false;
        col = JavaMath.Floor((JavaMath.Round(p.X) - OriginX) / Dx + 0.5);
        row = JavaMath.Floor((JavaMath.Round(p.Y) - OriginY) / Dy + 0.5);
        return true;
    }

    public string LocationName(PointD p) =>
        _numbering == null ? "" : _numbering.LocationName(JavaMath.Round(p.X), JavaMath.Round(p.Y));

    public PointD? LocationToPoint(string location)
    {
        var r = _numbering?.GetLocation(location);
        return r == null ? null : new PointD(r.Value.X, r.Value.Y);
    }

    public IEnumerable<(PointD A, PointD B)> OverlayLines(RectD area)
    {
        if (!_visible || Degenerate) yield break;
        // Cell boundaries lie half a cell from the centers.
        double x0 = OriginX + Dx / 2 + Dx * Math.Floor((area.X - OriginX - Dx / 2) / Dx);
        for (double x = x0; x <= area.X + area.W; x += Dx)
            if (x >= area.X) yield return (new PointD(x, area.Y), new PointD(x, area.Y + area.H));
        double y0 = OriginY + Dy / 2 + Dy * Math.Floor((area.Y - OriginY - Dy / 2) / Dy);
        for (double y = y0; y <= area.Y + area.H; y += Dy)
            if (y >= area.Y) yield return (new PointD(area.X, y), new PointD(area.X + area.W, y));
    }
}
