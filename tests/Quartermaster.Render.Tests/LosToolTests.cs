using System.Text;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Line of sight measuring, moved unchanged from the WinForms app (cases recorded from its LosTool).</summary>
public class LosToolTests
{
    private const string Pfx = "VASSAL.build.module.map.boardPicker.";

    private static IMapGrid Grid(string kind, string attrs)
    {
        var xml = $"<VASSAL.build.module.Map><{Pfx}Board name=\"B\" width=\"1000\" height=\"800\"><{Pfx}board.{kind} {attrs}/></{Pfx}Board></VASSAL.build.module.Map>";
        var root = BuildFileParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        return GridFactory.From(root.Descendants("Board").Single(), new DiagnosticLog())!;
    }

    private static MapDef Map(IMapGrid? grid, PointD offset) =>
        new("m1", "Main", false, Array.Empty<string>(), new[] { new BoardDef("B", null, 1000, 800, grid, offset) },
            0, 0, new RgbColor(255, 255, 255), new StackMetricsDef(0, 0, 0, 0, false), Array.Empty<SetupStackDef>(), "");

    private static string? Measure(MapDef map, PointD a, PointD b)
    {
        var los = new LosTool(map);
        los.Begin(a);
        Assert.True(los.Dragging);
        los.Update(b);
        los.End();
        Assert.False(los.Dragging);
        Assert.True(los.HasLine);
        return los.Status();
    }

    [Fact]
    public void Los_measures_distance_like_before()
    {
        var hex = Map(Grid("HexGrid", "x0=\"0\" y0=\"0\" dx=\"60\" dy=\"70\""), new PointD(0, 0));
        // Snapped to hex centres: three hexes straight down, four on the diagonal (same counts as HexDistanceTests).
        Assert.Equal("(0, 0) → (0, 210): 3 hexes", Measure(hex, new PointD(3, 4), new PointD(2, 208)));
        Assert.Equal("(0, 0) → (240, 140): 4 hexes", Measure(hex, new PointD(0, 0), new PointD(238, 141)));

        var square = Map(Grid("SquareGrid", "x0=\"25\" y0=\"25\" dx=\"50\" dy=\"50\""), new PointD(100, 0));
        Assert.Equal("(125, 25) → (275, 75): 3 squares", Measure(square, new PointD(130, 20), new PointD(270, 80)));

        var none = Map(null, new PointD(0, 0));
        Assert.Equal("(0, 0) → (30, 40): 50 px", Measure(none, new PointD(0, 0), new PointD(30, 40)));

        // Off every board: no snapping, pixels.
        Assert.Equal("(2000, 0) → (2000, 10): 10 px", Measure(hex, new PointD(2000, 0), new PointD(2000, 10)));
    }

    [Fact]
    public void Los_update_only_while_dragging_and_cancel_clears()
    {
        var los = new LosTool(Map(null, new PointD(0, 0)));
        Assert.False(los.HasLine);
        Assert.Null(los.Status());
        los.Begin(new PointD(0, 0));
        los.End();
        los.Update(new PointD(100, 0));   // ignored after End
        Assert.Equal("(0, 0) → (0, 0): 0 px", los.Status());
        los.Cancel();
        Assert.False(los.HasLine);
        Assert.False(los.Dragging);
        Assert.Null(los.Status());
    }
}
