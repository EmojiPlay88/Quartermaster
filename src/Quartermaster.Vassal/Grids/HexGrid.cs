// Ported from VASSAL HexGrid.java (LGPL-2.1).
namespace Quartermaster.Vassal;

public sealed class HexGrid : IMapGrid
{
    private const double Sqrt32 = 0.8660254037844386; // sqrt(3) / 2
    private const double DefaultHeight = 64.0;

    private readonly bool _snapTo;
    private readonly bool _cornersLegal;
    private readonly bool _edgesLegal;
    private readonly bool _visible;
    private readonly bool _dotsVisible;
    private readonly int _snapScale;
    private readonly HexGridNumbering? _numbering;

    internal double Dx { get; }
    internal double Dy { get; }
    internal int OriginX { get; }
    internal int OriginY { get; }
    internal bool Sideways { get; }
    internal double HexWidth => Dx;
    internal double HexSize => Dy;

    public RgbColor Color { get; }

    internal HexGrid(ModuleNode node, SizeD container, DiagnosticLog log)
    {
        Dy = GridAttr.Double(node, "dy", DefaultHeight, log);
        // VASSAL derives the hex width from the height unless a width was set explicitly.
        Dx = GridAttr.Double(node, "dx", Sqrt32 * Dy, log);
        OriginX = GridAttr.Int(node, "x0", 0, log);
        OriginY = GridAttr.Int(node, "y0", 32, log);
        Sideways = GridAttr.Bool(node, "sideways", false);
        _snapTo = GridAttr.Bool(node, "snapTo", true);
        _cornersLegal = GridAttr.Bool(node, "cornersLegal", false);
        _edgesLegal = GridAttr.Bool(node, "edgesLegal", false);
        _visible = GridAttr.Bool(node, "visible", false);
        _dotsVisible = GridAttr.Bool(node, "dotsVisible", false);
        _snapScale = GridAttr.Int(node, "snapscale", 0, log);
        Color = GridAttr.Color(node, log);
        if (Degenerate)
        {
            log.AddOnce("grid-size:" + node.ClassName + Dx + Dy, DiagnosticSeverity.Warning, "Grid",
                "HexGrid has a non-positive cell size; snapping disabled.", node.ClassName);
        }
        foreach (var c in node.Children)
        {
            if (c.ShortName == "HexGridNumbering") _numbering = new HexGridNumbering(c, this, container, log);
            else
                log.AddOnce("grid-child:" + c.ShortName, DiagnosticSeverity.Info, "Grid",
                    $"Unsupported HexGrid child '{c.ShortName}' ignored.", c.ClassName);
        }
    }

    private bool Degenerate => !(Dx > 0 && Dy > 0);

    public bool Visible => _visible || _dotsVisible || (_numbering?.Visible ?? false);

    // --- snapping -------------------------------------------------------------------------------

    public PointD Snap(PointD p)
    {
        if (!_snapTo || Degenerate) return p;
        var (x, y) = SnapTo(JavaMath.Round(p.X), JavaMath.Round(p.Y));
        return new PointD(x, y);
    }

    private (int X, int Y) SnapTo(int px, int py)
    {
        var center = SnapToHex(px, py);
        if (_edgesLegal && _cornersLegal)
        {
            var edge = SnapToHexSide(px, py);
            var vertex = SnapToHexVertex(px, py);
            long de = (long)(px - edge.X) * (px - edge.X) + (long)(py - edge.Y) * (py - edge.Y);
            long dv = (long)(px - vertex.X) * (px - vertex.X) + (long)(py - vertex.Y) * (py - vertex.Y);
            return de < dv ? CheckCenter(center, edge) : CheckCenter(center, vertex);
        }
        if (_edgesLegal) return CheckCenter(center, SnapToHexSide(px, py));
        if (_cornersLegal) return CheckCenter(center, SnapToHexVertex(px, py));
        return center;
    }

    // VASSAL works around vertexX being off by a pixel near centers.
    private static (int X, int Y) CheckCenter((int X, int Y) center, (int X, int Y) target)
    {
        long dx = center.X - target.X, dy = center.Y - target.Y;
        return dx * dx + dy * dy <= 2 ? center : target;
    }

    private (int X, int Y) Rot(int x, int y) => Sideways ? (y, x) : (x, y);

    private (int X, int Y) SnapToHex(int px, int py)
    {
        var (x, y) = Rot(px, py);
        var c = HexPoint(x, y);
        return Rot(c.X, c.Y);
    }

    private (int X, int Y) SnapToHexSide(int px, int py)
    {
        var (px2, py2) = Rot(px, py);
        int x = SideX(px2, py2), y = SideY(px2, py2);
        if (_snapScale > 0)
        {
            var hp = HexPoint(px2, py2);
            if (Math.Abs(px2 - hp.X) + Math.Abs(py2 - hp.Y) <= Math.Abs(px2 - x) + Math.Abs(py2 - y)) { x = hp.X; y = hp.Y; }
        }
        return Rot(x, y);
    }

    private (int X, int Y) SnapToHexVertex(int px, int py)
    {
        var (px2, py2) = Rot(px, py);
        int x = VertexX(px2, py2), y = VertexY(px2, py2);
        if (_snapScale > 0)
        {
            var hp = HexPoint(px2, py2);
            if (Math.Abs(px2 - hp.X) + Math.Abs(py2 - hp.Y) <= Math.Abs(px2 - x) + Math.Abs(py2 - y)) { x = hp.X; y = hp.Y; }
        }
        return Rot(x, y);
    }

    private (int X, int Y) HexPointCenterFromRawIndex(int nx, int ny)
    {
        int xLoc = JavaMath.ToInt(Dx * nx + OriginX);
        int yLoc = nx % 2 == 0
            ? JavaMath.ToInt(Dy * ny + OriginY)
            : JavaMath.ToInt(Dy * ny + JavaMath.ToInt(Dy / 2) + OriginY);
        return (xLoc, yLoc);
    }

    private static readonly (int X, int Y)[] Offsets1 = { (-1, -1), (-1, 0), (1, -1), (1, 0) };
    private static readonly (int X, int Y)[] Offsets2 = { (-1, 0), (-1, 1), (1, 0), (1, 1) };

    // Assumes the point has already been rotated if sideways.
    private (int X, int Y) HexPoint(int x, int y)
    {
        int nx = JavaMath.Floor((x - OriginX + Dx / 2) / Dx);
        int ny = nx % 2 == 0
            ? JavaMath.Floor((y - OriginY + Dy / 2) / Dy)
            : JavaMath.Floor((y - OriginY) / Dy);

        var first = HexPointCenterFromRawIndex(nx, ny);
        int xLoc = first.X, yLoc = first.Y;

        if (!HexContains(xLoc, yLoc, x, y))
        {
            foreach (var o in nx % 2 == 0 ? Offsets1 : Offsets2)
            {
                var c = HexPointCenterFromRawIndex(nx + o.X, ny + o.Y);
                if (HexContains(c.X, c.Y, x, y)) { xLoc = c.X; yLoc = c.Y; break; }
            }
        }

        if (_snapScale > 0)
        {
            xLoc += SnapDelta(x - xLoc, Dx);
            yLoc += SnapDelta(y - yLoc, Dy);
        }
        return (xLoc, yLoc);
    }

    private int SnapDelta(int delta, double size)
    {
        delta = JavaMath.Round(delta / (0.5 * size / _snapScale));
        delta = Math.Max(delta, 1 - _snapScale);
        delta = Math.Min(delta, _snapScale - 1);
        return JavaMath.Round(delta * 0.5 * size / _snapScale);
    }

    private int SideX(int x, int y) =>
        JavaMath.ToInt(Dx / 2 * JavaMath.Floor((x - OriginX + Dx / 4) * 2 / Dx) + OriginX);

    private int SideY(int x, int y)
    {
        int nx = JavaMath.Floor((x - OriginX + Dx / 4) * 2 / Dx);
        return nx % 2 == 0
            ? JavaMath.ToInt(Dy / 2 * JavaMath.Floor((y - OriginY + Dy / 4) * 2 / Dy) + OriginY)
            : JavaMath.ToInt(Dy / 2 * JavaMath.Floor((y - OriginY) * 2 / Dy) + JavaMath.ToInt(Dy / 4) + OriginY);
    }

    private int VertexX(int x, int y)
    {
        int ny = JavaMath.Floor((y - OriginY + Dy / 4) * 2 / Dy);
        if (ny % 2 == 0)
            return JavaMath.ToInt(2 * Dx / 3 * JavaMath.ToInt(Math.Floor(x - OriginX + Dx / 3) * 3 / (2 * Dx)) + OriginX);
        return JavaMath.ToInt(2 * Dx / 3 * JavaMath.ToInt(Math.Floor(x - OriginX + Dx / 3 + Dx / 3) * 3 / (2 * Dx))
            - JavaMath.ToInt(Dx / 3) + OriginX);
    }

    private int VertexY(int x, int y) =>
        JavaMath.ToInt(Dy / 2 * JavaMath.Floor((y - OriginY + Dy / 4) * 2 / Dy) + OriginY);

    private static int RoundF(float f) => JavaMath.ToInt(Math.Floor(f + 0.5f));

    /// <summary>VASSAL getSingleHexShape(sideways=false) containment (java.awt.Polygon rules).</summary>
    private bool HexContains(int cx, int cy, int px, int py)
    {
        float x = cx, y = cy;
        float deltaX = (float)Dx, deltaY = (float)Dy;
        float r = 2.0f * deltaX / 3.0f;
        float x1 = x - r, y1 = y;
        float x2 = x - 0.5f * r, y2 = y - 0.5f * deltaY;
        float x3 = x + 0.5f * r, y3 = y2;
        float x4 = x + r, y4 = y;
        float x5 = x3, y5 = y + 0.5f * deltaY;
        float x6 = x2, y6 = y5;
        int[] xs = { RoundF(x1), RoundF(x2), RoundF(x3) + 1, RoundF(x4) + 1, RoundF(x5) + 1, RoundF(x6), RoundF(x1) };
        int[] ys = { RoundF(y1), RoundF(y2), RoundF(y3), RoundF(y4), RoundF(y5) + 1, RoundF(y6) + 1, RoundF(y1) };
        return Polygon.Contains(xs, ys, px, py);
    }

    // --- numbering ------------------------------------------------------------------------------

    internal int RawColumn(int px, int py)
    {
        var (x, _) = Rot(px, py);
        return JavaMath.Floor((x - OriginX) / HexWidth + 0.5);
    }

    internal int RawRow(int px, int py)
    {
        var (x, y) = Rot(px, py);
        int nx = JavaMath.Round((x - OriginX) / Dx);
        return nx % 2 == 0
            ? JavaMath.Round((y - OriginY) / Dy)
            : JavaMath.Round((y - OriginY - Dy / 2) / Dy);
    }

    /// <summary>Raw (unnumbered) offset column/row of the hex containing <paramref name="p"/>: odd columns sit half a hex lower, rotated when sideways. False when the grid is degenerate.</summary>
    public bool TryCell(PointD p, out int col, out int row)
    {
        col = row = 0;
        if (Degenerate) return false;
        int x = JavaMath.Round(p.X), y = JavaMath.Round(p.Y);
        col = RawColumn(x, y);
        row = RawRow(x, y);
        return true;
    }

    public string LocationName(PointD p) =>
        _numbering == null ? "" : _numbering.LocationName(JavaMath.Round(p.X), JavaMath.Round(p.Y));

    public PointD? LocationToPoint(string location)
    {
        var r = _numbering?.GetLocation(location);
        return r == null ? null : new PointD(r.Value.X, r.Value.Y);
    }

    // --- overlay --------------------------------------------------------------------------------

    private PointD Out(double x, double y) => Sideways ? new PointD(y, x) : new PointD(x, y);

    public IEnumerable<(PointD A, PointD B)> OverlayLines(RectD area)
    {
        if (!_visible || Degenerate) yield break;
        // Work in the unrotated frame; Out() rotates results back.
        var a = Sideways ? new RectD(area.Y, area.X, area.H, area.W) : area;
        double r = 2.0 * Dx / 3.0;
        double xmin = OriginX + 2 * Dx * Math.Floor((a.X - OriginX) / (2 * Dx));
        double xmax = a.X + a.W + 2 * Dx;
        double ymin = OriginY + Dy * Math.Floor((a.Y - OriginY) / Dy);
        double ymax = a.Y + a.H + Dy;
        for (double x = xmin; x < xmax; x += 2 * Dx)
        {
            for (double y = ymin; y < ymax; y += Dy)
            {
                foreach (var seg in UpperEdges(x, y, r)) yield return seg;
                foreach (var seg in UpperEdges(x + Dx, y + Dy / 2, r)) yield return seg;
                if (x == xmin) yield return (Out(x - r, y), Out(x - r / 2, y + Dy / 2));
            }
        }
    }

    private IEnumerable<(PointD A, PointD B)> UpperEdges(double x, double y, double r)
    {
        double top = y - Dy / 2;
        yield return (Out(x - r, y), Out(x - r / 2, top));
        yield return (Out(x - r / 2, top), Out(x + r / 2, top));
        yield return (Out(x + r / 2, top), Out(x + r, y));
    }
}
