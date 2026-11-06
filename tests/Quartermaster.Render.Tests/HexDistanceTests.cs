using System.Text;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class HexDistanceTests
{
    private const string Pfx = "VASSAL.build.module.map.boardPicker.";

    private static IMapGrid Build(string kind, string attrs)
    {
        var xml = $"<VASSAL.build.module.Map><{Pfx}Board name=\"B\" width=\"1000\" height=\"800\"><{Pfx}board.{kind} {attrs}/></{Pfx}Board></VASSAL.build.module.Map>";
        var root = BuildFileParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        return GridFactory.From(root.Descendants("Board").Single(), new DiagnosticLog())!;
    }

    private static PointD P(double x, double y) => new(x, y);

    [Fact]
    public void Square_Distance()
    {
        var g = Build("SquareGrid", "x0=\"25\" y0=\"25\" dx=\"50\" dy=\"50\"");
        Assert.Equal(3, HexDistance.Cells(g, P(25, 25), P(175, 75)));
        Assert.Equal(2, HexDistance.Cells(g, P(30, 20), P(120, 130)));
        Assert.Equal(0, HexDistance.Cells(g, P(10, 10), P(30, 30)));
    }

    [Fact]
    public void Hex_Distance_Vertical_Flat()
    {
        var g = Build("HexGrid", "x0=\"0\" y0=\"0\" dx=\"60\" dy=\"70\"");
        Assert.Equal(3, HexDistance.Cells(g, P(0, 0), P(0, 210)));
        Assert.Equal(3, HexDistance.Cells(g, P(0, 0), P(0, -210)));
        Assert.Equal(1, HexDistance.Cells(g, P(0, 0), P(60, 35)));
        Assert.Equal(1, HexDistance.Cells(g, P(0, 0), P(60, -35)));
        Assert.Equal(4, HexDistance.Cells(g, P(0, 0), P(240, 140)));
        Assert.Equal(6, HexDistance.Cells(g, P(0, 0), P(120, 350)));
        Assert.Equal(1, HexDistance.Cells(g, P(60, 35), P(120, 70)));
    }

    [Fact]
    public void Hex_Distance_Sideways()
    {
        var g = Build("HexGrid", "x0=\"0\" y0=\"0\" dx=\"60\" dy=\"70\" sideways=\"true\"");
        Assert.Equal(3, HexDistance.Cells(g, P(0, 0), P(210, 0)));
        Assert.Equal(1, HexDistance.Cells(g, P(0, 0), P(35, 60)));
        Assert.Equal(1, HexDistance.Cells(g, P(0, 0), P(-35, 60)));
        Assert.Equal(4, HexDistance.Cells(g, P(0, 0), P(140, 240)));
        Assert.Equal(6, HexDistance.Cells(g, P(0, 0), P(350, 120)));
    }

    [Fact]
    public void DifferentBoards_Null()
    {
        var g = Build("SquareGrid", "dx=\"50\" dy=\"50\"");
        var b1 = new BoardDef("A", null, 100, 100, g, new PointD(0, 0));
        var b2 = new BoardDef("B", null, 100, 100, g, new PointD(100, 0));
        Assert.Null(HexDistance.Cells(b1, b2, P(0, 0), P(50, 0)));
        Assert.Equal(1, HexDistance.Cells(b1, b1, P(24, 24), P(74, 24)));
        Assert.Null(HexDistance.Cells((IMapGrid?)null, P(0, 0), P(50, 0)));
    }
}
