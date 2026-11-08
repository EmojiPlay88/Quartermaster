using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>The palette drop and map status texts shared by the WinForms and Desktop apps (<see cref="PaletteModel.Place"/>, <see cref="MapStatusText"/>).</summary>
public class PalettePlaceAndStatusTests : IDisposable
{
    private const string P = "VASSAL.build.module.map.boardPicker.board.";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-place-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureModule _fx = new();
    private readonly ModuleSession _s;
    private MapDef Map => _s.Model.Maps[0];
    private PieceSlotDef Slot => _s.Model.Slots[0];

    public PalettePlaceAndStatusTests()
    {
        Directory.CreateDirectory(_tmp);
        // The minimal fixture with a numbered square grid (50px cells from 25,25), so locations have names.
        var xml = FixtureModule.MinimalBuildFile().Replace("y0=\"25\"/>",
            $"y0=\"25\"><{P}mapgrid.SquareGridNumbering first=\"H\" hType=\"N\" vType=\"N\" sep=\"\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"/></{P}SquareGrid>");
        _s = ModuleSession.Open(_fx.WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg").Build(), Path.Combine(_tmp, "cache"), null);
    }

    public void Dispose()
    {
        _s.Dispose();
        _fx.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    [Fact]
    public void Find_by_key()
    {
        Assert.Same(Slot, PaletteModel.Find(_s.Model, PaletteModel.Key(Slot)));
        Assert.Null(PaletteModel.Find(_s.Model, null));
        Assert.Null(PaletteModel.Find(_s.Model, "nope|0"));
    }

    [Fact]
    public void Place_without_game_says_so()
    {
        Assert.Equal((false, "No game is running"), PaletteModel.Place(_s, Map, Slot, new PointD(60, 60), snap: true));
    }

    [Fact]
    public void Place_off_the_boards_changes_nothing()
    {
        _s.StartNewGame();
        var log = _s.Game!.Log.Count;
        Assert.Equal((false, "Drop onto a board"), PaletteModel.Place(_s, Map, Slot, new PointD(-500, -500), snap: true));
        Assert.Empty(_s.Game.State.Pieces);
        Assert.Equal(log, _s.Game.Log.Count);
    }

    [Fact]
    public void Place_adds_a_logged_piece_at_the_snapped_point()
    {
        _s.StartNewGame();
        var at = new PointD(62, 68);
        var expected = PaletteModel.ResolveDrop(_s.Game!.State, Map, at, _s.Images, snap: true)!.Value;

        var (placed, msg) = PaletteModel.Place(_s, Map, Slot, at, snap: true);

        Assert.True(placed);
        Assert.Equal("Placed Test Unit", msg);
        var piece = Assert.Single(_s.Game.State.Pieces.Values);
        Assert.Equal(expected.Pos, piece.Pos);
        Assert.NotEqual(at, piece.Pos);   // snapped to the grid
        Assert.StartsWith("p:", piece.Id);
        var add = Assert.IsType<AddPiece>(_s.Game.Log[^1]);
        Assert.Equal(piece.Id, add.PieceId);
        Assert.False(string.IsNullOrEmpty(add.LogText));   // prepared by CommandLog
    }

    [Fact]
    public void Place_through_the_action_port_is_one_undoable_action()
    {
        _s.StartNewGame();
        var phases = new PhaseController(_s, (c, st) => CommandLog.Prepare(c, st, _s));
        var history = new HistoryController(_s, phases, (c, st) => CommandLog.Prepare(c, st, _s));
        var port = new LocalActions(_s, phases, history);
        Assert.False(history.CanUndo);

        Assert.True(PaletteModel.Place(_s, Map, Slot, new PointD(60, 60), snap: false, port).Placed);
        Assert.Single(_s.Game!.State.Pieces);
        Assert.True(history.CanUndo);
        history.Undo();
        Assert.Empty(_s.Game.State.Pieces);
    }

    [Fact]
    public void Location_is_rounded_map_point_plus_grid_name()
    {
        var b = Map.Boards[0];
        var name = b.Grid!.LocationName(new PointD(74.6, 30.2));
        Assert.NotEqual("", name);
        Assert.Equal($"75, 30  {name}", MapStatusText.Location(Map, new PointD(74.6, 30.2)));
    }

    [Fact]
    public void Location_off_the_boards_is_just_the_point()
    {
        Assert.Equal("-301, 12", MapStatusText.Location(Map, new PointD(-300.6, 12.4)));
        Assert.Equal("200, 50", MapStatusText.Location(Map, new PointD(200, 50)));   // right edge is outside (x >= width)
    }

    [Theory]
    [InlineData(1f, "100%")]
    [InlineData(0.5f, "50%")]
    [InlineData(1.234f, "123%")]
    [InlineData(0.0625f, "6%")]
    public void Zoom_text(float zoom, string expected) => Assert.Equal(expected, MapStatusText.Zoom(zoom));
}
