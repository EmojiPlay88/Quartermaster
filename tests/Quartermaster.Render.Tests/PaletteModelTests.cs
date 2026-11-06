using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class PaletteModelTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-pal-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureModule _fx = new();
    private readonly ModuleSession _s;
    private readonly GameState _state = new();
    private MapDef Map => _s.Model.Maps[0];

    public PaletteModelTests()
    {
        Directory.CreateDirectory(_tmp);
        var armor = """<VASSAL.build.widget.PieceSlot entryName="Armor Division" gpid="2" height="32" width="32">+/null/piece;;;unit.svg;Armor Division/null;0;0;2</VASSAL.build.widget.PieceSlot>""";
        var loose = """<VASSAL.build.widget.PieceSlot entryName="Loose Marker" gpid="3" height="32" width="32">+/null/piece;;;unit.svg;Loose Marker/null;0;0;3</VASSAL.build.widget.PieceSlot>""";
        var xml = FixtureModule.MinimalBuildFile()
            .Replace("</VASSAL.build.widget.ListWidget>", armor + "</VASSAL.build.widget.ListWidget>")
            .Replace("</VASSAL.build.module.PieceWindow>", "</VASSAL.build.module.PieceWindow>" + loose);
        _s = ModuleSession.Open(_fx.WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg").Build(), Path.Combine(_tmp, "cache"));
    }

    public void Dispose()
    {
        _s.Dispose();
        _fx.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    [Fact]
    public void PaletteModel_Build_FollowsNavPath_OtherFolder()
    {
        var root = PaletteModel.Build(_s.Model);
        var units = root.Children.First(c => c.Title == "Units");
        Assert.Equal(new[] { "Test Unit", "Armor Division" }, Flatten(units).Select(x => x.EntryName));
        var other = root.Children.Single(c => c.Title == "Other");
        Assert.Equal("Loose Marker", Assert.Single(other.Slots).EntryName);
        Assert.Equal(3, Flatten(root).Count());
    }

    [Fact]
    public void PaletteModel_Search_CaseInsensitive_Capped()
    {
        Assert.Equal("Armor Division", Assert.Single(PaletteModel.Search(_s.Model, "ARMOR")).EntryName);
        Assert.Equal("Loose Marker", Assert.Single(PaletteModel.Search(_s.Model, "marker")).EntryName);
        Assert.Empty(PaletteModel.Search(_s.Model, "zzz"));
        Assert.Empty(PaletteModel.Search(_s.Model, "  "));

        var many = Enumerable.Range(0, 800).Select(i => new PieceSlotDef("Unit " + i, "g" + i, "piece;;;unit.svg;Unit " + i + "/null", "", 1, 1, Array.Empty<string>())).ToList();
        Assert.Equal(500, PaletteModel.Search(many, "unit").Count);
    }

    [Fact]
    public void PaletteDrop_SnapsToGrid()
    {
        var raw = new PointD(110, 60);
        var r = PaletteModel.ResolveDrop(_state, Map, raw, _s.Images, snap: true);
        Assert.NotNull(r);
        Assert.Equal(Map.Id, r.Value.MapId);
        Assert.Equal(Map.Boards[0].Grid!.Snap(raw), r.Value.Pos);
        Assert.Equal(raw, PaletteModel.ResolveDrop(_state, Map, raw, _s.Images, snap: false)!.Value.Pos);
    }

    [Fact]
    public void PaletteDrop_OntoStack_StacksOnto()
    {
        _state.Apply(new AddPiece("a", "piece;;;unit.svg;A", "", Map.Id, new PointD(75, 75), null));
        var r = PaletteModel.ResolveDrop(_state, Map, new PointD(80, 70), _s.Images, snap: true);
        Assert.Equal(_state.Pieces["a"].StackId, r!.Value.StackOnto);
    }

    [Fact]
    public void PaletteDrop_OutsideBoards_ReturnsNull() =>
        Assert.Null(PaletteModel.ResolveDrop(_state, Map, new PointD(-500, -500), _s.Images, snap: true));

    private static IEnumerable<PieceSlotDef> Flatten(PaletteNode n) => n.Slots.Concat(n.Children.SelectMany(Flatten));
}

public class ThumbQueueTests
{
    [Fact]
    public void Order_VisibleRangeFirst_ThenNearest()
    {
        var o = ThumbQueue.Order(1000, 800, 803);
        Assert.Equal(new[] { 800, 801, 802, 803 }, o.Take(4));
        Assert.Equal(new[] { 804, 799, 805 }, o.Skip(4).Take(3));
        Assert.Equal(1000, o.Distinct().Count());
    }

    [Fact]
    public void Order_ScrolledPast600_StillRendersFarItemsEarly()
    {
        var o = ThumbQueue.Order(2400, 1500, 1510);
        Assert.True(o.ToList().IndexOf(1505) < 11);
        Assert.True(o.ToList().IndexOf(0) > 1000);
        Assert.Empty(ThumbQueue.Order(0, 0, 0));
    }
}
