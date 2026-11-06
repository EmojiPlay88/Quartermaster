using Quartermaster.Core;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class NewGameBuilderTests
{
    private static GameState Build(string setupStacks, out DiagnosticLog log, string? baseXml = null)
    {
        var xml = (baseXml ?? FixtureModule.MinimalBuildFile()).Replace("</VASSAL.build.module.map.BoardPicker>",
            "</VASSAL.build.module.map.BoardPicker>" + setupStacks);
        using var fx = new FixtureModule().WithBuildFile(xml);
        var path = fx.Build();
        log = new DiagnosticLog();
        using var arch = VmodArchive.Open(path);
        var model = ModuleModelBuilder.Build(arch, log);
        return NewGameBuilder.FromSetupStacks(model, log);
    }

    private const string Slot =
        "<VASSAL.build.widget.PieceSlot entryName=\"U\" gpid=\"9\" height=\"32\" width=\"32\">" +
        "+/null/prototype;Proto\tpiece;;;unit.svg;U/null;0;0;9</VASSAL.build.widget.PieceSlot>";

    private static string Stack(bool useGrid, string location, string slots) =>
        $"<VASSAL.build.module.map.SetupStack name=\"S\" owningBoard=\"Main Board\" useGridLocation=\"{(useGrid ? "true" : "false")}\" location=\"{location}\" x=\"60\" y=\"70\">{slots}</VASSAL.build.module.map.SetupStack>";

    [Fact]
    public void NewGame_SetupStackWithGridLocation_ResolvesToSnappedPoint()
    {
        const string P = "VASSAL.build.module.map.boardPicker.board.";
        var xml = FixtureModule.MinimalBuildFile().Replace("y0=\"25\"/>",
            $"y0=\"25\"><{P}mapgrid.SquareGridNumbering first=\"H\" hType=\"N\" vType=\"N\" sep=\"\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"/></{P}SquareGrid>");
        // "0405" is column index 3, row index 4: centre (25 + 3*50, 25 + 4*50)
        var s = Build(Stack(true, "0405", Slot), out _, xml);
        Assert.Single(s.Pieces);
        Assert.Equal(new PointD(175, 225), s.Pieces["n:1"].Pos);
        Assert.Equal("Main Map;175;225;9;0", s.Pieces["n:1"].TraitStates[^1]);
    }

    [Fact]
    public void NewGame_ExpandsPrototypesAndCarriesPosition()
    {
        var s = Build(Stack(false, "", Slot + Slot), out _);
        Assert.Equal(new[] { "n:1", "n:2" }, s.Pieces.Keys);
        var p = s.Pieces["n:1"];
        Assert.Equal("Main Map", p.MapId);
        Assert.Equal(new PointD(60, 70), p.Pos);
        Assert.Single(s.Stacks);
        Assert.Equal(p.StackId, s.Pieces["n:2"].StackId);
        var traits = PieceTypeParser.Parse(p.TypeString, new DiagnosticLog(), null).Traits;
        Assert.Equal(new[] { "mark", "piece" }, traits.Select(t => t.Id).ToArray());
        Assert.Equal(2, p.TraitStates.Count);
        Assert.Equal("Main Map;60;70;9;0", p.TraitStates[1]);
    }
}
