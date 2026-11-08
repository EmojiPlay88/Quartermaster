using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>The "Last moved" highlight set, as the WinForms map canvas computed it (the latest move on the map in the command list).</summary>
[Collection(AppDataRootEnv.Name)]
public class LastMovedTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-last-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureModule _fx = PhaseFixture.Module();
    private ModuleSession? _s;

    public LastMovedTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        _s?.Dispose();
        _fx.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    [Fact]
    public void Last_moved_matches_previous_highlight()
    {
        Assert.Empty(LastMoved.For(null, "m"));
        _s = ModuleSession.Open(_fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _s.LoadSetup(_s.Model.Setups[0], Difficulty.SemiAuto, null);
        var g = _s.Game!;
        var mapId = g.State.Pieces[PhaseFixture.RedOne].MapId;
        Assert.Empty(LastMoved.For(g, mapId));

        using (g.BeginAction()) g.Execute(new MovePieces(new[] { PhaseFixture.RedOne }, mapId, new PointD(80, 50), null));
        Assert.Equal(new[] { PhaseFixture.RedOne }, LastMoved.For(g, mapId));

        using (g.BeginAction()) g.Execute(new MovePieces(new[] { PhaseFixture.RedTwo, PhaseFixture.BlueOne }, mapId, new PointD(130, 50), null));
        Assert.Equal(new[] { PhaseFixture.BlueOne, PhaseFixture.RedTwo }, LastMoved.For(g, mapId).OrderBy(x => x));
        Assert.Empty(LastMoved.For(g, "another map"));

        // A later non-move command keeps the highlight; undoing the move brings back the one before.
        using (g.BeginAction()) g.Execute(new ToggleTrail(PhaseFixture.RedOne));
        Assert.Equal(2, LastMoved.For(g, mapId).Count);
        g.Undo();
        g.Undo();
        Assert.Equal(new[] { PhaseFixture.RedOne }, LastMoved.For(g, mapId));
        Assert.Equal(MapViewTools.LastMovedIds(g.Log, mapId), LastMoved.For(g, mapId));
    }
}
