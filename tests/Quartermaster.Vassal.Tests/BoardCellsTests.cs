using System.Text;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Vassal.Tests;

public class BoardCellsTests
{
    private const string V = "VASSAL.build.";
    private const string BP = "VASSAL.build.module.map.boardPicker.";

    private const string HexNum = "first=\"H\" hType=\"N\" vType=\"N\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\" stagger=\"true\"";

    private static string HexBoard(string name, string image) =>
        $"<{BP}Board name=\"{name}\" image=\"{image}\"><{BP}board.HexGrid x0=\"0\" y0=\"0\" dx=\"60\" dy=\"70\">" +
        $"<{BP}board.mapgrid.HexGridNumbering {HexNum}/></{BP}board.HexGrid></{BP}Board>";

    private static string SquareBoard(string name, string image) =>
        $"<{BP}Board name=\"{name}\" image=\"{image}\"><{BP}board.SquareGrid x0=\"25\" y0=\"25\" dx=\"50\" dy=\"50\">" +
        $"<{BP}board.mapgrid.SquareGridNumbering first=\"H\" hType=\"N\" vType=\"N\" sep=\"\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"/></{BP}board.SquareGrid></{BP}Board>";

    // Map "North": Riverside (hex, 600x500) then Delta (hex, 300x200, repeats hex names). Map "South": Square (square grid).
    private static ModuleModel Build(out IDisposable owner)
    {
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <{{V}}GameModule name="Fix" version="1" VassalVersion="3.7.1">
              <{{V}}module.Map mapName="North"><{{V}}module.map.BoardPicker>
                {{HexBoard("Riverside", "river.png")}}
                {{HexBoard("Delta", "delta.png")}}
              </{{V}}module.map.BoardPicker></{{V}}module.Map>
              <{{V}}module.Map mapName="South"><{{V}}module.map.BoardPicker>
                {{SquareBoard("Square", "sq.png")}}
              </{{V}}module.map.BoardPicker></{{V}}module.Map>
            </{{V}}GameModule>
            """;
        var fx = new FixtureModule().WithBuildFile(xml).AddImage("river.png", 600, 500).AddImage("delta.png", 300, 200).AddImage("sq.png", 400, 400);
        var a = VmodArchive.Open(fx.Build());
        owner = new Both(a, fx);
        return ModuleModelBuilder.Build(a, new DiagnosticLog());
    }

    private sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose() { a.Dispose(); b.Dispose(); }
    }

    private static BoardDef B(ModuleModel m, string name) => m.Maps.SelectMany(x => x.Boards).Single(b => b.Name == name);

    [Fact]
    public void Find_returns_board_spelling()
    {
        var m = Build(out var o); using var _ = o;
        var riverside = B(m, "Riverside");
        var name = riverside.Grid!.LocationName(new PointD(120, 105));
        Assert.NotEqual("", name);
        var hit = BoardCells.Find(m, "  " + name.ToUpperInvariant() + " ", null);
        Assert.NotNull(hit);
        Assert.Same(riverside, hit!.Value.Board);
        Assert.Equal("North", hit.Value.Map.Id);
        Assert.Equal(name, riverside.Grid.LocationName(hit.Value.Local));
    }

    [Fact]
    public void Find_unknown_name_null()
    {
        var m = Build(out var o); using var _ = o;
        Assert.Null(BoardCells.Find(m, "zzz", null));
        Assert.Null(BoardCells.Find(m, "", null));
    }

    [Fact]
    public void Find_name_outside_board_null()
    {
        var m = Build(out var o); using var _ = o;
        // Far beyond every board (largest is 600x500): the numbering parses it, the point is off the board.
        Assert.NotNull(B(m, "Riverside").Grid!.LocationToPoint("9999"));
        Assert.Null(BoardCells.Find(m, "9999", null));
    }

    [Fact]
    public void Find_prefers_given_board()
    {
        var m = Build(out var o); using var _ = o;
        var delta = B(m, "Delta");
        var name = delta.Grid!.LocationName(new PointD(60, 35));
        Assert.Same(B(m, "Riverside"), BoardCells.Find(m, name, null)!.Value.Board);
        Assert.Same(delta, BoardCells.Find(m, name, delta)!.Value.Board);
    }

    [Fact]
    public void Find_module_order_without_prefer()
    {
        var m = Build(out var o); using var _ = o;
        var name = B(m, "Delta").Grid!.LocationName(new PointD(60, 35));
        // Both hex boards know it; the first in module order wins.
        Assert.Equal("Riverside", BoardCells.Find(m, name, null)!.Value.Board.Name);
        // A name only the bigger Riverside board holds is not found on Delta.
        var far = B(m, "Riverside").Grid!.LocationName(new PointD(480, 420));
        Assert.Equal("Riverside", BoardCells.Find(m, far, B(m, "Delta"))!.Value.Board.Name);
    }

    [Fact]
    public void Find_caches_per_board()
    {
        var m = Build(out var o); using var _ = o;
        var name = B(m, "Riverside").Grid!.LocationName(new PointD(120, 105));
        BoardCells.Probes = 0;
        Assert.NotNull(BoardCells.Find(m, name, null));
        var after = BoardCells.Probes;
        Assert.True(after > 0);
        Assert.NotNull(BoardCells.Find(m, name, null));
        Assert.Equal(after, BoardCells.Probes);
    }

    [Fact]
    public void Find_keeps_misses_only_up_to_the_limit()
    {
        var m = Build(out var o); using var _ = o;
        var square = B(m, "Square");
        var known = square.Grid!.LocationName(new PointD(75, 75));
        for (var i = 0; i < BoardCells.MissLimit + 50; i++) Assert.Null(BoardCells.Find(m, "junk" + i, square));
        BoardCells.Probes = 0;
        Assert.Null(BoardCells.Find(m, "junk0", square));                                  // an early miss stays cached
        var cachedMiss = BoardCells.Probes;
        Assert.Null(BoardCells.Find(m, "junk" + (BoardCells.MissLimit + 40), square));     // a late miss was not kept
        Assert.True(BoardCells.Probes > cachedMiss);
        Assert.NotNull(BoardCells.Find(m, known, square));                                 // a name the board has is still kept
        var after = BoardCells.Probes;
        Assert.NotNull(BoardCells.Find(m, known, square));
        Assert.Equal(after, BoardCells.Probes);
    }

    private static IMapGrid Grid(string kind, string attrs)
    {
        var xml = $"<VASSAL.build.module.Map><{BP}Board name=\"B\" width=\"1000\" height=\"800\"><{BP}board.{kind} {attrs}/></{BP}Board></VASSAL.build.module.Map>";
        var root = BuildFileParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        return GridFactory.From(root.Descendants("Board").Single(), new DiagnosticLog())!;
    }

    private static PointD P(double x, double y) => new(x, y);

    [Fact]
    public void Cells_hex_matches_old_HexDistance()
    {
        var g = Grid("HexGrid", "x0=\"0\" y0=\"0\" dx=\"60\" dy=\"70\"");
        Assert.Equal(3, BoardCells.Cells(g, P(0, 0), P(0, 210)));
        Assert.Equal(3, BoardCells.Cells(g, P(0, 0), P(0, -210)));
        Assert.Equal(1, BoardCells.Cells(g, P(0, 0), P(60, 35)));
        Assert.Equal(1, BoardCells.Cells(g, P(0, 0), P(60, -35)));
        Assert.Equal(4, BoardCells.Cells(g, P(0, 0), P(240, 140)));
        Assert.Equal(6, BoardCells.Cells(g, P(0, 0), P(120, 350)));
        Assert.Equal(1, BoardCells.Cells(g, P(60, 35), P(120, 70)));
        var s = Grid("HexGrid", "x0=\"0\" y0=\"0\" dx=\"60\" dy=\"70\" sideways=\"true\"");
        Assert.Equal(3, BoardCells.Cells(s, P(0, 0), P(210, 0)));
        Assert.Equal(1, BoardCells.Cells(s, P(0, 0), P(35, 60)));
        Assert.Equal(1, BoardCells.Cells(s, P(0, 0), P(-35, 60)));
        Assert.Equal(4, BoardCells.Cells(s, P(0, 0), P(140, 240)));
        Assert.Equal(6, BoardCells.Cells(s, P(0, 0), P(350, 120)));
    }

    [Fact]
    public void Cells_square_chebyshev()
    {
        var g = Grid("SquareGrid", "x0=\"25\" y0=\"25\" dx=\"50\" dy=\"50\"");
        Assert.Equal(3, BoardCells.Cells(g, P(25, 25), P(175, 75)));
        Assert.Equal(2, BoardCells.Cells(g, P(30, 20), P(120, 130)));
        Assert.Equal(0, BoardCells.Cells(g, P(10, 10), P(30, 30)));
    }

    [Fact]
    public void Cells_no_grid_null() => Assert.Null(BoardCells.Cells(null, P(0, 0), P(50, 0)));
}
