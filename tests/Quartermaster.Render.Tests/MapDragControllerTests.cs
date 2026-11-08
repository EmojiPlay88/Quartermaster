using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render.Tests;

public class MapDragControllerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-mdc-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureModule _fx = new();
    private readonly ModuleSession _s;
    private readonly PieceRenderer _pieces;
    private MapDef Map => _s.Model.Maps[0];

    public MapDragControllerTests()
    {
        Directory.CreateDirectory(_tmp);
        _s = ModuleSession.Open(_fx.WithBuildFile(FixtureModule.MinimalBuildFile()).Build(), Path.Combine(_tmp, "cache"));
        _s.StartNewGame();
        _s.Game!.Execute(new AddPiece("p", "piece;;;unit.svg;A", "", Map.Id, new PointD(50, 50), null));
        _pieces = new PieceRenderer(_s.Images);
    }

    public void Dispose()
    {
        _s.Dispose();
        _fx.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly Viewport Vp = new();
    private static readonly SKSize Size = new(200, 100);

    private bool Begin(MapDragController c, PointD grab) =>
        c.Begin(_s.Game!.State, "p", new[] { "p" }, grab, Vp, Size, hidden =>
        {
            using var bmp = new SKBitmap(200, 100);
            return SKImage.FromBitmap(bmp);
        });

    [Fact]
    public void Drag_moves_piece_and_snaps()
    {
        var st = _s.Game!.State;
        using var c = new MapDragController(_s, Map, _pieces, a => a());
        var grab = new PointD(52, 48);
        Assert.True(Begin(c, grab));
        Assert.True(c.Active);
        var cursor = new PointD(130, 70);
        c.Update(st, cursor);
        var expected = DropResolver.ResolveDrag(st, Map, cursor, c.Info!.GrabOffset, new[] { "p" }, _s.Images).Pos;
        c.Commit(st, cursor);
        Assert.False(c.Active);
        Assert.Equal(expected, _s.Game!.State.Pieces["p"].Pos);
        Assert.NotEqual(cursor, _s.Game.State.Pieces["p"].Pos);
    }

    [Fact]
    public void Cancel_restores()
    {
        var st = _s.Game!.State;
        var before = st.Pieces["p"].Pos;
        using var c = new MapDragController(_s, Map, _pieces, a => a());
        Assert.True(Begin(c, new PointD(52, 48)));
        c.Update(st, new PointD(130, 70));
        c.End();
        Assert.False(c.Active);
        Assert.Equal(before, _s.Game.State.Pieces["p"].Pos);
    }
}
