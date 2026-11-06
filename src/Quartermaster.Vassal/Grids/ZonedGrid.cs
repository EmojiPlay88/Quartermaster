// Ported from VASSAL ZonedGrid.java and mapgrid/Zone.java (LGPL-2.1).
using System.Text;
using System.Text.RegularExpressions;

namespace Quartermaster.Vassal;

internal sealed class Zone
{
    private readonly Polygon _polygon;
    private readonly string _locationFormat;
    private readonly IMapGrid? _ownGrid;

    public string Name { get; }
    public bool UseParentGrid { get; }

    public Zone(ModuleNode node, DiagnosticLog log)
    {
        Name = node.Attr("name");
        _polygon = Polygon.Parse(node.Attr("path"));
        _locationFormat = node.Attr("locationFormat", "$name$");
        UseParentGrid = string.Equals(node.Attr("useParentGrid"), "true", StringComparison.Ordinal);
        if (_polygon.Count == 0)
            log.AddOnce("zone-path:" + Name, DiagnosticSeverity.Warning, "Grid", $"Zone '{Name}' has no usable polygon path.", node.ClassName);
        foreach (var c in node.Children)
        {
            if (c.ShortName is "SquareGrid" or "HexGrid" or "RegionGrid")
            {
                if (UseParentGrid) continue; // VASSAL: a zone using the parent grid takes no grid of its own
                _ownGrid ??= GridFactory.FromGridNode(c, log);
            }
            else if (c.ShortName != "ZoneProperty")
                log.AddOnce("zone-child:" + c.ShortName, DiagnosticSeverity.Info, "Grid",
                    $"Unsupported Zone child '{c.ShortName}' ignored.", c.ClassName);
        }
    }

    public IMapGrid? GridOf(IMapGrid? background) => UseParentGrid ? background : _ownGrid;

    public bool Contains(double x, double y) => _polygon.Contains(x, y);

    /// <summary>Zone.snapTo: the zone's grid snaps; a zone without a grid leaves the point alone.</summary>
    public PointD Snap(PointD p, IMapGrid? background) => GridOf(background)?.Snap(p) ?? p;

    public string LocationName(PointD p, IMapGrid? background)
    {
        var grid = GridOf(background);
        var gridLocation = grid?.LocationName(p) ?? "";
        return Format.Expand(_locationFormat, v => v switch
        {
            "name" => Name,
            "gridLocation" => gridLocation,
            _ => "",
        });
    }

    public PointD? GetLocation(string location, IMapGrid? background)
    {
        var grid = GridOf(background);
        var parts = Format.Split(_locationFormat);
        var regex = new StringBuilder();
        int groups = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var token = parts[i];
            if (token.Length == 0) continue;
            if (!Format.IsVariable(parts, i)) regex.Append(Regex.Escape(i % 2 == 1 ? "$" + token : token));
            else if (token == "name") regex.Append(Regex.Escape(Name));
            else if (token == "gridLocation" && grid != null) { regex.Append("(.*)"); groups++; }
        }
        if (regex.Length == 0) return null;

        Match m;
        try { m = Regex.Match(location ?? "", "^(?:" + regex + ")$", RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1)); }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { return null; }
        if (!m.Success) return null;

        if (groups > 0)
        {
            var p = grid!.LocationToPoint(m.Groups[groups].Value);
            return p != null && Contains(p.Value.X, p.Value.Y) ? p : null;
        }

        // No grid location in the name: the polygon's vertex centroid, or its first vertex if that lies outside.
        if (_polygon.Count == 0) return new PointD(0, 0);
        long sx = 0, sy = 0;
        for (int i = 0; i < _polygon.Count; i++) { sx += _polygon.Xs[i]; sy += _polygon.Ys[i]; }
        int cx = (int)(sx / _polygon.Count), cy = (int)(sy / _polygon.Count);
        return Contains(cx, cy) ? new PointD(cx, cy) : new PointD(_polygon.Xs[0], _polygon.Ys[0]);
    }
}

public sealed class ZonedGrid : IMapGrid
{
    private readonly List<Zone> _zones = new();
    private readonly IMapGrid? _background;

    internal ZonedGrid(ModuleNode node, SizeD container, DiagnosticLog log)
    {
        foreach (var c in node.Children)
        {
            if (c.ShortName == "Zone") _zones.Add(new Zone(c, log));
            else if (c.ShortName is "SquareGrid" or "HexGrid" or "RegionGrid")
                _background ??= GridFactory.FromGridNode(c, log);
            else if (c.ShortName != "ZonedGridHighlighter")
                log.AddOnce("grid-child:" + c.ShortName, DiagnosticSeverity.Info, "Grid",
                    $"Unsupported ZonedGrid child '{c.ShortName}' ignored.", c.ClassName);
        }
    }

    private Zone? FindZone(PointD p)
    {
        int x = JavaMath.Round(p.X), y = JavaMath.Round(p.Y);
        foreach (var z in _zones)
            if (z.Contains(x, y)) return z;
        return null;
    }

    public bool Visible => true;

    public RgbColor Color => _background?.Color ?? new RgbColor(0, 0, 0);

    public PointD Snap(PointD p)
    {
        var z = FindZone(p);
        if (z != null) return z.Snap(p, _background);
        return _background?.Snap(p) ?? p;
    }

    public string LocationName(PointD p)
    {
        var z = FindZone(p);
        if (z != null) return z.LocationName(p, _background);
        return _background?.LocationName(p) ?? "";
    }

    public PointD? LocationToPoint(string location)
    {
        foreach (var z in _zones)
        {
            var p = z.GetLocation(location, _background);
            if (p != null && z.Contains(p.Value.X, p.Value.Y)) return p;
        }
        return _background?.LocationToPoint(location);
    }

    public IEnumerable<(PointD A, PointD B)> OverlayLines(RectD area)
    {
        // Background grid is clipped out of zones that do not use it.
        if (_background != null)
        {
            foreach (var l in _background.OverlayLines(area))
            {
                var mx = JavaMath.Round((l.A.X + l.B.X) / 2);
                var my = JavaMath.Round((l.A.Y + l.B.Y) / 2);
                if (!_zones.Any(z => !z.UseParentGrid && z.Contains(mx, my))) yield return l;
            }
        }
        foreach (var z in _zones)
        {
            var g = z.GridOf(_background);
            if (g == null || z.UseParentGrid) continue; // parent grid lines are already drawn above
            foreach (var l in g.OverlayLines(area))
            {
                var mx = JavaMath.Round((l.A.X + l.B.X) / 2);
                var my = JavaMath.Round((l.A.Y + l.B.Y) / 2);
                if (z.Contains(mx, my)) yield return l;
            }
        }
    }
}
