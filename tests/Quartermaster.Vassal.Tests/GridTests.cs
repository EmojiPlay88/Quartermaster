using System.Text;
using Quartermaster.Vassal;

namespace Quartermaster.Vassal.Tests;

public class GridTests
{
    private const string Pfx = "VASSAL.build.module.map.boardPicker.";

    private static IMapGrid? Build(string gridXml, DiagnosticLog? log = null, int w = 1000, int h = 800)
    {
        var xml = $"<VASSAL.build.module.Map><{Pfx}Board name=\"B\" width=\"{w}\" height=\"{h}\">{gridXml}</{Pfx}Board></VASSAL.build.module.Map>";
        var root = BuildFileParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        var board = root.Descendants("Board").Single();
        return GridFactory.From(board, log ?? new DiagnosticLog());
    }

    private static string Square(string attrs, string numbering = "") =>
        $"<{Pfx}board.SquareGrid {attrs}>{numbering}</{Pfx}board.SquareGrid>";

    private static string Hex(string attrs, string numbering = "") =>
        $"<{Pfx}board.HexGrid {attrs}>{numbering}</{Pfx}board.HexGrid>";

    private static string Num(string kind, string attrs) =>
        $"<{Pfx}board.mapgrid.{kind}GridNumbering {attrs}/>";

    [Fact]
    public void Square_SnapsToNearestCenter()
    {
        var g = Build(Square("x0=\"25\" y0=\"25\" dx=\"50\" dy=\"50\""))!;
        Assert.Equal(new PointD(75, 75), g.Snap(new PointD(64, 71)));
    }

    [Fact]
    public void Square_SnapToFalse_ReturnsInput()
    {
        var g = Build(Square("x0=\"25\" y0=\"25\" dx=\"50\" dy=\"50\" snapTo=\"false\""))!;
        Assert.Equal(new PointD(64, 71), g.Snap(new PointD(64, 71)));
    }

    [Fact]
    public void Square_CornersAndEdges_SnapToHalfCells()
    {
        var both = Build(Square("x0=\"0\" y0=\"0\" dx=\"50\" dy=\"50\" cornersLegal=\"true\" edgesLegal=\"true\""))!;
        Assert.Equal(new PointD(25, 0), both.Snap(new PointD(23, 4)));
        var corners = Build(Square("x0=\"0\" y0=\"0\" dx=\"50\" dy=\"50\" cornersLegal=\"true\""))!;
        Assert.Equal(new PointD(25, 25), corners.Snap(new PointD(23, 22)));
    }

    // Brief vector said hLeading=2/vLeading=2; VASSAL's getName(leading=N) pads to N+1 digits ("004"), so leading=1 gives "04".
    [Fact]
    public void Square_Numbering_NumericWithLeadingZeros()
    {
        var g = Build(Square("x0=\"25\" y0=\"25\" dx=\"50\" dy=\"50\"",
            Num("Square", "first=\"H\" hType=\"N\" vType=\"N\" sep=\"\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"")))!;
        // column index 3, row index 4
        Assert.Equal("0405", g.LocationName(new PointD(25 + 3 * 50, 25 + 4 * 50)));
        Assert.Equal("0405", g.LocationName(new PointD(25 + 3 * 50 + 10, 25 + 4 * 50 - 9)));
    }

    [Fact]
    public void Square_Numbering_RoundTrip()
    {
        var g = Build(Square("x0=\"25\" y0=\"25\" dx=\"50\" dy=\"50\"",
            Num("Square", "first=\"H\" hType=\"N\" vType=\"N\" sep=\"\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"")))!;
        var p = g.LocationToPoint("0405");
        Assert.Equal(new PointD(175, 225), p);
        Assert.Equal("0405", g.LocationName(p!.Value));
        Assert.Null(g.LocationToPoint("nonsense"));
    }

    [Fact]
    public void Square_Numbering_AlphaVerticalFirstWithSeparator()
    {
        var g = Build(Square("x0=\"0\" y0=\"0\" dx=\"50\" dy=\"50\"",
            Num("Square", "first=\"V\" hType=\"N\" vType=\"A\" sep=\"-\" hLeading=\"0\" hOff=\"0\" vOff=\"0\"")))!;
        Assert.Equal("C-2", g.LocationName(new PointD(100, 100)));
        Assert.Equal(new PointD(100, 100), g.LocationToPoint("C-2"));
    }

    [Fact]
    public void Square_Numbering_Descending()
    {
        // 1000x800 board, dx=dy=100: 10 columns, 8 rows; descending flips against the max.
        var g = Build(Square("x0=\"0\" y0=\"0\" dx=\"100\" dy=\"100\"",
            Num("Square", "hType=\"N\" vType=\"N\" hLeading=\"0\" vLeading=\"0\" hOff=\"0\" vOff=\"0\" hDescend=\"true\" vDescend=\"true\"")))!;
        Assert.Equal("108", g.LocationName(new PointD(0, 0)));
    }

    [Fact]
    public void Square_NoNumbering_NameIsEmpty()
    {
        var g = Build(Square("dx=\"50\" dy=\"50\""))!;
        Assert.Equal("", g.LocationName(new PointD(10, 10)));
        Assert.Null(g.LocationToPoint("0101"));
    }

    [Fact]
    public void Square_Defaults_AndMalformedAttributesDoNotThrow()
    {
        var log = new DiagnosticLog();
        var g = Build(Square("dx=\"abc\" x0=\"1.5\" color=\"zzz\" visible=\"yes\""), log)!;
        Assert.Equal(new PointD(24, 24), g.Snap(new PointD(30, 30)));
        Assert.Equal(new RgbColor(0, 0, 0), g.Color);
        Assert.NotEmpty(log.Items);
    }

    [Fact]
    public void Square_OverlayLines_AreCellBoundaries()
    {
        var g = Build(Square("x0=\"25\" y0=\"25\" dx=\"50\" dy=\"50\" visible=\"true\" color=\"255,0,0\""))!;
        Assert.True(g.Visible);
        Assert.Equal(new RgbColor(255, 0, 0), g.Color);
        var lines = g.OverlayLines(new RectD(0, 0, 100, 100)).ToList();
        Assert.Contains(lines, l => l.A.X == 50 && l.B.X == 50 && l.A.Y == 0 && l.B.Y == 100);
        Assert.Contains(lines, l => l.A.Y == 50 && l.B.Y == 50 && l.A.X == 0 && l.B.X == 100);
        var hidden = Build(Square("dx=\"50\" dy=\"50\""))!;
        Assert.Empty(hidden.OverlayLines(new RectD(0, 0, 100, 100)));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public void Hex_SnapCenterIsFixedPoint(string sideways)
    {
        var g = Build(Hex($"dx=\"60\" dy=\"70\" x0=\"0\" y0=\"32\" sideways=\"{sideways}\""))!;
        for (int i = 0; i < 20; i++)
            for (int j = 0; j < 20; j++)
            {
                var s = g.Snap(new PointD(7 + i * 29, 11 + j * 31));
                Assert.Equal(s, g.Snap(s));
            }
    }

    [Fact]
    public void Hex_NeighbourCentersSpacing()
    {
        var g = Build(Hex("dx=\"60\" dy=\"70\" x0=\"0\" y0=\"0\""))!;
        // Column 2 (x=120): centers at y = 70*k
        var a = g.Snap(new PointD(120, 140));
        var b = g.Snap(new PointD(120, 210));
        Assert.Equal(a.X, b.X);
        Assert.Equal(70, b.Y - a.Y);
        // Odd column (x=60) is shifted by dy/2.
        var c = g.Snap(new PointD(60, 100));
        Assert.Equal(new PointD(60, 105), c);
    }

    [Fact]
    public void Hex_SidewaysSwapsAxes()
    {
        var up = Build(Hex("dx=\"60\" dy=\"70\" x0=\"0\" y0=\"0\""))!;
        var side = Build(Hex("dx=\"60\" dy=\"70\" x0=\"0\" y0=\"0\" sideways=\"true\""))!;
        var u = up.Snap(new PointD(125, 143));
        var s = side.Snap(new PointD(143, 125));
        Assert.Equal(new PointD(u.Y, u.X), s);
    }

    [Fact]
    public void Hex_SnapToFalse_ReturnsInput()
    {
        var g = Build(Hex("dx=\"60\" dy=\"70\" snapTo=\"false\""))!;
        Assert.Equal(new PointD(13, 17), g.Snap(new PointD(13, 17)));
    }

    [Fact]
    public void Hex_EdgesAndCorners_ProduceNonCenterPoints()
    {
        var centers = Build(Hex("dx=\"60\" dy=\"70\" x0=\"0\" y0=\"0\""))!;
        var edges = Build(Hex("dx=\"60\" dy=\"70\" x0=\"0\" y0=\"0\" edgesLegal=\"true\""))!;
        var corners = Build(Hex("dx=\"60\" dy=\"70\" x0=\"0\" y0=\"0\" cornersLegal=\"true\""))!;
        // A point near an edge midpoint but off-center snaps elsewhere than the center.
        var p = new PointD(30, 17);
        Assert.NotEqual(centers.Snap(p), edges.Snap(p));
        Assert.NotEqual(centers.Snap(new PointD(40, 17)), corners.Snap(new PointD(40, 17)));
    }

    [Fact]
    public void Hex_Numbering_RoundTrip()
    {
        var g = Build(Hex("dx=\"60\" dy=\"70\" x0=\"0\" y0=\"32\"",
            Num("Hex", "first=\"H\" hType=\"N\" vType=\"N\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\" stagger=\"true\"")))!;
        var p = g.LocationToPoint("0405");
        Assert.NotNull(p);
        Assert.Equal("0405", g.LocationName(p!.Value));
        // Every cell center we can name round-trips, including the staggered odd columns.
        for (int c = 0; c < 6; c++)
            for (int r = 0; r < 6; r++)
            {
                var name = $"{c + 1:00}{r + 1:00}";
                var pt = g.LocationToPoint(name)!.Value;
                Assert.Equal(name, g.LocationName(pt));
            }
    }

    [Fact]
    public void Hex_Numbering_SidewaysRoundTrip()
    {
        var g = Build(Hex("dx=\"60\" dy=\"70\" x0=\"0\" y0=\"32\" sideways=\"true\"",
            Num("Hex", "hLeading=\"1\" vLeading=\"1\"")))!;
        for (int c = 0; c < 5; c++)
            for (int r = 0; r < 5; r++)
            {
                var name = $"{c + 1:00}{r + 1:00}";
                var pt = g.LocationToPoint(name)!.Value;
                Assert.Equal(name, g.LocationName(pt));
            }
    }

    [Fact]
    public void Hex_OverlayLines_ProducesSegments()
    {
        var g = Build(Hex("dx=\"60\" dy=\"70\" visible=\"true\""))!;
        Assert.NotEmpty(g.OverlayLines(new RectD(0, 0, 300, 300)));
    }

    private const string ZoneBox = "<" + Pfx + "board.mapgrid.Zone name=\"Box\" path=\"0,0;100,0;100,100;0,100\" locationFormat=\"$name$\"";

    [Fact]
    public void Zoned_PointInZone_UsesZoneName()
    {
        var xml = $"<{Pfx}board.ZonedGrid>{ZoneBox}/>" +
            Square("x0=\"0\" y0=\"0\" dx=\"100\" dy=\"100\"", Num("Square", "hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"")) +
            $"</{Pfx}board.ZonedGrid>";
        var g = Build(xml)!;
        Assert.Equal("Box", g.LocationName(new PointD(50, 50)));
        Assert.Equal("0302", g.LocationName(new PointD(200, 100)));
    }

    [Fact]
    public void Zoned_GridLocationInZoneFormat()
    {
        var xml = $"<{Pfx}board.ZonedGrid><{Pfx}board.mapgrid.Zone name=\"Z\" path=\"0,0;400,0;400,400;0,400\" locationFormat=\"$name$:$gridLocation$\">" +
            Square("x0=\"0\" y0=\"0\" dx=\"100\" dy=\"100\"", Num("Square", "hLeading=\"1\" vLeading=\"1\"")) +
            $"</{Pfx}board.mapgrid.Zone></{Pfx}board.ZonedGrid>";
        var g = Build(xml)!;
        Assert.Equal("Z:0202", g.LocationName(new PointD(100, 100)));
        Assert.Equal(new PointD(100, 100), g.LocationToPoint("Z:0202"));
    }

    [Fact]
    public void Zoned_ZoneWithOwnGrid_SnapsToZoneGrid()
    {
        var xml = $"<{Pfx}board.ZonedGrid><{Pfx}board.mapgrid.Zone name=\"Z\" path=\"0,0;200,0;200,200;0,200\">" +
            Square("x0=\"0\" y0=\"0\" dx=\"100\" dy=\"100\"") +
            $"</{Pfx}board.mapgrid.Zone>" +
            Square("x0=\"0\" y0=\"0\" dx=\"30\" dy=\"30\"") +
            $"</{Pfx}board.ZonedGrid>";
        var g = Build(xml)!;
        Assert.Equal(new PointD(100, 100), g.Snap(new PointD(88, 91)));   // zone grid
        Assert.Equal(new PointD(300, 300), g.Snap(new PointD(298, 301))); // background grid (30px)
    }

    [Fact]
    public void Zoned_UseParentGrid_SnapsToBackground_ZoneWithoutGridIsFree()
    {
        var xml = $"<{Pfx}board.ZonedGrid>" +
            $"<{Pfx}board.mapgrid.Zone name=\"P\" path=\"0,0;200,0;200,200;0,200\" useParentGrid=\"true\"/>" +
            $"<{Pfx}board.mapgrid.Zone name=\"Free\" path=\"300,0;500,0;500,200;300,200\"/>" +
            Square("x0=\"0\" y0=\"0\" dx=\"30\" dy=\"30\"") +
            $"</{Pfx}board.ZonedGrid>";
        var g = Build(xml)!;
        Assert.Equal(new PointD(90, 90), g.Snap(new PointD(88, 91)));
        Assert.Equal(new PointD(333, 77), g.Snap(new PointD(333, 77)));
        Assert.Equal("Free", g.LocationName(new PointD(400, 100)));
    }

    [Fact]
    public void Zoned_OutsideAllZonesWithoutBackground_DoesNotThrow()
    {
        var xml = $"<{Pfx}board.ZonedGrid>{ZoneBox}/></{Pfx}board.ZonedGrid>";
        var g = Build(xml)!;
        Assert.Equal("", g.LocationName(new PointD(500, 500)));
        Assert.Equal(new PointD(500, 500), g.Snap(new PointD(500, 500)));
        Assert.Null(g.LocationToPoint("nope"));
        Assert.Equal(new PointD(50, 50), g.LocationToPoint("Box"));
    }

    [Fact]
    public void Factory_NoGrid_ReturnsNull_AndRegionGridIsDiagnosed()
    {
        Assert.Null(Build(""));
        var log = new DiagnosticLog();
        Assert.Null(Build($"<{Pfx}board.RegionGrid/>", log));
        Assert.NotEmpty(log.Items);
    }

    [Fact]
    public void Zone_Polygon_IsNonRectangularAndMalformedPathSafe()
    {
        var xml = $"<{Pfx}board.ZonedGrid>" +
            $"<{Pfx}board.mapgrid.Zone name=\"Tri\" path=\"0,0;100,0;0,100\"/>" +
            $"<{Pfx}board.mapgrid.Zone name=\"Bad\" path=\"x,y;;1\"/>" +
            $"</{Pfx}board.ZonedGrid>";
        var g = Build(xml)!;
        Assert.Equal("Tri", g.LocationName(new PointD(10, 10)));
        Assert.Equal("", g.LocationName(new PointD(90, 90)));
    }
}
