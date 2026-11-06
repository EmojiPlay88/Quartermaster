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
        var typeStr = SequenceEncoder.Encode(new[] { "mark;Side,Rank;Red,3",
            SequenceEncoder.Encode(new[] { "PROP;Strength;;;;;;;;;;;", "piece;;;unit.svg;Rifle" }, '\t') }, '\t');
        var t = PieceTypeParser.Parse(typeStr, new DiagnosticLog(), null);
        var p = new PieceInstance { Id = "p", TypeString = typeStr, TraitStates = new() { "", "7", "" } };
        var d = PieceDetails.For(p, t);
        Assert.Equal("Rifle", d.Name);
        Assert.Contains(("Side", "Red"), d.Properties);
        Assert.Contains(("Rank", "3"), d.Properties);
        Assert.Contains(d.Properties, x => x.Value == "7");
    }
}
