using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class MoveMeasureTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-measure-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _own = new();

    public MoveMeasureTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var o in _own) o.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private ModuleModel Model(bool grid)
    {
        var fx = PhaseFixture.Module(grid);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), null);
        _own.Add(s);
        _own.Add(fx);
        return s.Model;
    }

    [Fact]
    public void MoveMeasure_SquareGrid_Cells()
    {
        var m = Model(true);
        var map = m.Maps[0].Id;
        Assert.Equal((2, (string?)null), MoveMeasure.Cells(m, map, new PointD(30, 50), map, new PointD(130, 50)));
        Assert.Equal((3, (string?)null), MoveMeasure.Cells(m, map, new PointD(30, 50), map, new PointD(180, 50)));
        Assert.Equal((0, (string?)null), MoveMeasure.Cells(m, map, new PointD(30, 50), map, new PointD(30, 50)));
    }

    [Fact]
    public void MoveMeasure_DifferentMaps_Problem()
    {
        var m = Model(true);
        Assert.Equal((0, (string?)"different maps"), MoveMeasure.Cells(m, m.Maps[0].Id, new PointD(30, 50), "Elsewhere", new PointD(130, 50)));
    }

    [Fact]
    public void MoveMeasure_NoGridOrOffBoard_Problem()
    {
        var m = Model(false);
        var map = m.Maps[0].Id;
        Assert.Equal((0, (string?)"no grid"), MoveMeasure.Cells(m, map, new PointD(30, 50), map, new PointD(130, 50)));
        var g = Model(true);
        Assert.Equal((0, (string?)"no grid"), MoveMeasure.Cells(g, g.Maps[0].Id, new PointD(30, 50), g.Maps[0].Id, new PointD(9000, 9000)));
        Assert.Equal((0, (string?)"no grid"), MoveMeasure.Cells(g, "nope", new PointD(1, 1), "nope", new PointD(2, 2)));
    }
}
