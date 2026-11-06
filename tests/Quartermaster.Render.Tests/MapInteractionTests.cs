using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render.Tests;

public class MapInteractionTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-mi-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureModule _fx = new();
    private readonly ModuleSession _s;
    private readonly GameState _state = new();
    private MapDef Map => _s.Model.Maps[0];

    public MapInteractionTests()
    {
        Directory.CreateDirectory(_tmp);
        _s = ModuleSession.Open(_fx.WithBuildFile(FixtureModule.MinimalBuildFile()).Build(), Path.Combine(_tmp, "cache"));
    }

    public void Dispose()
    {
        _s.Dispose();
        _fx.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private void Add(string id, double x, double y, string? onto = null) =>
        _state.Apply(new AddPiece(id, "piece;;;unit.svg;A", "", Map.Id, new PointD(x, y), onto));

    [Fact]
    public void Drop_SnapsToBoardGrid()
    {
        var r = DropResolver.Resolve(_state, Map, new PointD(110, 60), new[] { "x" }, _s.Images);
        var expected = Map.Boards[0].Grid!.Snap(new PointD(110, 60));
        Assert.Equal(expected, r.Pos);
        Assert.NotEqual(new PointD(110, 60), r.Pos);
        Assert.Null(r.StackOnto);
    }

    [Fact]
    public void Drop_SnapIsBoardLocalWithOffset()
    {
        var b = Map.Boards[0] with { Offset = new PointD(1000, 500) };
        var map = Map with { Boards = new[] { b } };
        var r = DropResolver.Resolve(_state, map, new PointD(1110, 560), new[] { "x" }, _s.Images);
        var local = b.Grid!.Snap(new PointD(110, 60));
        Assert.Equal(new PointD(local.X + 1000, local.Y + 500), r.Pos);
    }

    [Fact]
    public void Drop_OutsideAnyBoard_KeepsRawPoint()
    {
        var r = DropResolver.Resolve(_state, Map, new PointD(-500, -500), new[] { "x" }, _s.Images);
        Assert.Equal(new PointD(-500, -500), r.Pos);
    }

    [Fact]
    public void Drop_OnExistingStack_StacksOntoIt()
    {
        Add("a", 75, 75);
        var stackId = _state.Pieces["a"].StackId;
        var r = DropResolver.Resolve(_state, Map, new PointD(80, 70), new[] { "x" }, _s.Images);
        Assert.Equal(stackId, r.StackOnto);
        Assert.Equal(new PointD(75, 75), r.Pos);
    }

    [Fact]
    public void Drop_OnOwnStack_IsNotAStackTarget()
    {
        Add("a", 75, 75);
        var r = DropResolver.Resolve(_state, Map, new PointD(80, 70), new[] { "a" }, _s.Images);
        Assert.Null(r.StackOnto);
    }

    [Fact]
    public void Drop_StackingDisabled_NeverStacks()
    {
        Add("a", 75, 75);
        var map = Map with { Stacking = Map.Stacking with { Disabled = true } };
        var r = DropResolver.Resolve(_state, map, new PointD(80, 70), new[] { "x" }, _s.Images);
        Assert.Null(r.StackOnto);
    }

    [Fact]
    public void Drop_AwayFromStack_DoesNotStack()
    {
        Add("a", 75, 75);
        var r = DropResolver.Resolve(_state, Map, new PointD(160, 20), new[] { "x" }, _s.Images);
        Assert.Null(r.StackOnto);
    }

    [Fact]
    public void DragTarget_OffCentreGrab_LandsInCellUnderGhostCentre()
    {
        // piece centre is (+30,+20) from the grab point; cursor at (95,45) -> ghost centre (125,65)
        var r = DropResolver.ResolveDrag(_state, Map, new PointD(95, 45), new PointD(30, 20), new[] { "x" }, _s.Images);
        var expected = Map.Boards[0].Grid!.Snap(new PointD(125, 65));
        Assert.Equal(expected, r.Pos);
        var raw = DropResolver.Resolve(_state, Map, new PointD(95, 45), new[] { "x" }, _s.Images);
        Assert.NotEqual(raw.Pos, r.Pos);
    }

    [Fact]
    public void DragTarget_StackTargetUsesGhostCentre()
    {
        Add("a", 75, 75);
        var r = DropResolver.ResolveDrag(_state, Map, new PointD(40, 40), new PointD(35, 35), new[] { "x", "y" }, _s.Images);
        Assert.Equal(_state.Pieces["a"].StackId, r.StackOnto);
        Assert.Equal(new PointD(75, 75), r.Pos);
    }

    private static PointD Shift(PointD p, PointD d) => new(p.X + d.X, p.Y + d.Y);

    private IReadOnlyList<FormationGroup> Formation(PointD cursor, PointD grab, string primary, params string[] ids) =>
        DropResolver.ResolveFormation(_state, Map, cursor, grab, primary, ids, _s.Images);

    [Fact]
    public void Formation_SinglePiece_MatchesResolveDrag()
    {
        Add("a", 50, 50);
        var g = Formation(new PointD(95, 45), new PointD(-45, 5), "a", "a");
        var t = DropResolver.ResolveDrag(_state, Map, new PointD(95, 45), new PointD(-45, 5), new[] { "a" }, _s.Images);
        var only = Assert.Single(g);
        Assert.Equal(new[] { "a" }, only.Ids);
        Assert.Equal(t.Pos, only.Pos);
        Assert.Equal(t.StackOnto, only.StackOnto);
    }

    [Fact]
    public void Formation_TwoPiecesKeepSeparation()
    {
        Add("a", 50, 50);
        Add("b", 150, 50);
        var g = Formation(new PointD(110, 60), new PointD(-60, -10), "a", "a", "b");
        Assert.Equal(2, g.Count);
        var a = g.Single(x => x.Ids.Contains("a"));
        var b = g.Single(x => x.Ids.Contains("b"));
        Assert.Equal(100, b.Pos.X - a.Pos.X, 6);
        Assert.Equal(0, b.Pos.Y - a.Pos.Y, 6);
    }

    [Fact]
    public void Formation_PrimarySnaps_OthersShiftByTheSameDeltaUnsnapped()
    {
        Add("a", 50, 50);
        Add("b", 153, 57);
        var cursor = new PointD(112, 61);
        var grab = new PointD(-62, -11);
        var g = Formation(cursor, grab, "a", "a", "b");
        var a = g.Single(x => x.Ids.Contains("a"));
        var b = g.Single(x => x.Ids.Contains("b"));
        Assert.Equal(DropResolver.ResolveDrag(_state, Map, cursor, grab, new[] { "a", "b" }, _s.Images).Pos, a.Pos);
        var delta = new PointD(a.Pos.X - 50, a.Pos.Y - 50);
        Assert.Equal(Shift(new PointD(153, 57), delta), b.Pos);
    }

    [Fact]
    public void Formation_PiecesFromOneStackStayOneStack()
    {
        Add("a", 50, 50);
        Add("b", 50, 50, _state.Pieces["a"].StackId);
        Add("c", 300, 300);
        var g = Formation(new PointD(110, 60), new PointD(-60, -10), "c", "a", "b", "c");
        Assert.Equal(2, g.Count);
        Assert.Contains(g, x => x.Ids.SequenceEqual(new[] { "a", "b" }));
    }

    [Fact]
    public void Formation_OthersNeverStackOntoForeignStacks()
    {
        Add("a", 50, 50);
        Add("b", 150, 50);
        Add("f", 400, 400);
        var g = Formation(new PointD(300, 400), new PointD(0, 0), "a", "a", "b");
        var b = g.Single(x => x.Ids.Contains("b"));
        Assert.Null(b.StackOnto);
        var a = g.Single(x => x.Ids.Contains("a"));
        Assert.Equal(new PointD(150 + a.Pos.X - 50, 50 + a.Pos.Y - 50), b.Pos);
    }

    [Fact]
    public void Formation_PrimaryMayStackOntoForeignStack_OthersStillFree()
    {
        Add("f", 75, 75);
        Add("a", 400, 400);
        Add("b", 500, 400);
        var g = Formation(new PointD(80, 70), new PointD(0, 0), "a", "a", "b");
        var a = g.Single(x => x.Ids.Contains("a"));
        var b = g.Single(x => x.Ids.Contains("b"));
        Assert.Equal(_state.Pieces["f"].StackId, a.StackOnto);
        Assert.Equal(new PointD(75, 75), a.Pos);
        Assert.Null(b.StackOnto);
        Assert.Equal(new PointD(175, 75), b.Pos);
    }

    [Fact]
    public void Formation_GroupLandingOnPrimaryPosJoinsPrimaryGroup()
    {
        Add("a", 50, 50);
        Add("c", 50, 50); // separate stack, same spot
        var g = Formation(new PointD(110, 60), new PointD(-60, -10), "a", "a", "c");
        var only = Assert.Single(g);
        Assert.Equal(new[] { "a", "c" }, only.Ids.OrderBy(x => x));
    }

    [Fact]
    public void Formation_StackingDisabled_NeverStacks()
    {
        Add("f", 75, 75);
        Add("a", 400, 400);
        var map = Map with { Stacking = Map.Stacking with { Disabled = true } };
        var g = DropResolver.ResolveFormation(_state, map, new PointD(80, 70), new PointD(0, 0), "a", new[] { "a" }, _s.Images);
        Assert.Null(Assert.Single(g).StackOnto);
    }

    [Fact]
    public void Formation_MissingPrimaryOrIds_DoesNotThrow()
    {
        Add("a", 50, 50);
        var g = Formation(new PointD(110, 60), new PointD(-60, -10), "gone", "a", "also-gone");
        Assert.Equal(new[] { "a" }, Assert.Single(g).Ids);
        Assert.Empty(Formation(new PointD(0, 0), new PointD(0, 0), "gone", "none"));
    }

    [Fact]
    public void GhostDelta_IsCursorPlusGrabMinusPrimaryOrigin_Unsnapped()
    {
        Add("a", 50, 50);
        var d = DropResolver.GhostDelta(_state, "a", new PointD(113, 61), new PointD(-60, -10));
        Assert.Equal(new PointD(3, 1), d);
    }

    [Fact]
    public void Sprite_RendersCentredNonEmptyImage()
    {
        Add("a", 50, 50);
        var pr = new PieceRenderer(_s.Images);
        using var sprite = pr.RenderSprite(_state.Pieces["a"], 1f, PieceDrawFlags.Ghost);
        Assert.Equal(sprite.Half * 2, sprite.Image.Width);
        Assert.Equal(sprite.Image.Width, sprite.Image.Height);
        Assert.True(sprite.Bounds.Width >= 1);
    }

    [Fact]
    public void HiddenPieces_AreNotDrawn()
    {
        Add("a", 100, 100);
        var pr = new PieceRenderer(_s.Images);
        var r = new MapRenderer(_s.Images, pr);
        var vp = new Viewport();
        SKBitmap Shot(IReadOnlySet<string>? hidden)
        {
            var bmp = new SKBitmap(300, 300);
            using var c = new SKCanvas(bmp);
            r.Draw(c, Map, new Dictionary<string, TileSet>(), _state, _s.Model, vp, new SKSize(300, 300),
                new MapDrawOptions(false, new HashSet<string>(), null, hidden));
            return bmp;
        }
        using var shown = Shot(null);
        using var gone = Shot(new HashSet<string> { "a" });
        Assert.NotEqual(shown.Bytes, gone.Bytes);
    }

    [Fact]
    public void RubberBand_SelectsPiecesWithCentreInside()
    {
        Add("a", 50, 50);
        Add("b", 150, 50);
        var ids = SelectionRect.PieceIdsIn(_state, Map, new RectD(20, 20, 60, 60), _s.Images);
        Assert.Equal(new[] { "a" }, ids);
        Assert.Equal(2, SelectionRect.PieceIdsIn(_state, Map, new RectD(0, 0, 400, 400), _s.Images).Count);
    }

    [Fact]
    public void RubberBand_NormalisesNegativeSize()
    {
        Add("a", 50, 50);
        var r = SelectionRect.FromCorners(new PointD(80, 80), new PointD(20, 20));
        Assert.Equal(new RectD(20, 20, 60, 60), r);
        Assert.Single(SelectionRect.PieceIdsIn(_state, Map, r, _s.Images));
    }

    [Fact]
    public void MenuGrouping_KeepsFirstSeenGroupOrder()
    {
        PieceAction A(string l, string g) => new(l, g, _ => Array.Empty<GameCommand>());
        var groups = ActionMenu.Group(new[] { A("1", "state"), A("2", "piece"), A("3", "state") });
        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "1", "3" }, groups[0].Select(a => a.Label));
        Assert.Equal(new[] { "2" }, groups[1].Select(a => a.Label));
    }

    [Fact]
    public void FitViewport_CentresAndClamps()
    {
        var vp = new Viewport();
        FitView.ZoomToFit(vp, Map, new SKSize(400, 400));
        // map is 200x100 -> zoom 2 fits width
        Assert.Equal(2f, vp.Zoom, 3);
        var screen = vp.ToScreen(new PointD(100, 50));
        Assert.Equal(200f, screen.X, 1);
        Assert.Equal(200f, screen.Y, 1);
        FitView.ZoomToFit(vp, Map, new SKSize(4000, 4000));
        Assert.Equal(4f, vp.Zoom);
    }
}

public class PieceDetailTests
{
    [Fact]
    public void Details_ListMarkAndPropAndName()
    {
        // VASSAL shape: type "mark;" + comma-encoded keys; the values are the trait state, comma-encoded (Marker.myGetState).
        var markState = SequenceEncoder.Encode(new[] { "Red", "3,5" }, ',');
        var typeStr = SequenceEncoder.Encode(new[] { "mark;" + SequenceEncoder.Encode(new[] { "Side", "Rank" }, ','),
            SequenceEncoder.Encode(new[] { "PROP;Strength;;;;;;;;;;;", "piece;;;unit.svg;Rifle" }, '\t') }, '\t');
        var t = PieceTypeParser.Parse(typeStr, new DiagnosticLog(), null);
        var p = new PieceInstance { Id = "p", TypeString = typeStr, TraitStates = new() { markState, "7", "" } };
        var d = PieceDetails.For(p, t);
        Assert.Equal("Rifle", d.Name);
        Assert.Contains(("Side", "Red"), d.Properties);
        Assert.Contains(("Rank", "3,5"), d.Properties);
        Assert.Contains(d.Properties, x => x.Value == "7");
    }
}
