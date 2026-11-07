using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class ViewStateTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-view-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public ViewStateTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private ModuleSession Make(bool blueOwnsMap = false)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.LoadSetup(s.Model.Setups[0]);
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            return blueOwnsMap ? p with { MapOwners = new() { new MapOwner { MapId = "Main Map", SideId = "blue" } } } : p;
        });
        return s;
    }

    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };

    [Fact]
    public void Solo_ViewIsGameState()
    {
        var s = Make();
        Assert.Null(s.ViewSides);
        Assert.Same(s.Game!.State, s.View);
        Assert.True(s.CanSee("b1"));
        Assert.True(s.CanSeeMap("Main Map"));
    }

    [Fact]
    public void SetViewSides_FiltersAndRaisesViewChanged()
    {
        var s = Make(blueOwnsMap: true);
        var n = 0;
        s.ViewChanged += () => n++;
        s.SetViewSides(Red);
        Assert.Equal(1, n);
        Assert.NotSame(s.Game!.State, s.View);
        Assert.DoesNotContain("b1", s.View!.Pieces.Keys);
        Assert.False(s.CanSee("b1"));
        Assert.False(s.CanSeeMap("Main Map"));
        Assert.Same(s.View, s.View);   // cached
        s.SetViewSides(null);
        Assert.Equal(2, n);
        Assert.Same(s.Game.State, s.View);
    }

    [Fact]
    public void GameChange_InvalidatesView()
    {
        var s = Make();
        s.SetViewSides(Red);
        var before = s.View;
        var n = 0;
        s.ViewChanged += () => n++;
        s.Game!.Execute(new MovePieces(new[] { "r1" }, "Main Map", new PointD(321, 50), null));
        Assert.Equal(1, n);
        Assert.NotSame(before, s.View);
        Assert.Equal(321, s.View!.Pieces["r1"].Pos.X);
    }

    [Fact]
    public void Rewind_InvalidatesView()
    {
        var s = Make();
        s.Game!.Execute(new MovePieces(new[] { "r1" }, "Main Map", new PointD(321, 50), null));
        s.SetViewSides(Red);
        var before = s.View;
        var n = 0;
        s.ViewChanged += () => n++;
        s.Game.Undo();
        Assert.True(n >= 1);
        Assert.NotSame(before, s.View);
        Assert.NotEqual(321, s.View!.Pieces["r1"].Pos.X);
    }

    [Fact]
    public void GameReplaced_RehooksAndInvalidates()
    {
        var s = Make();
        s.SetViewSides(Red);
        var old = s.Game;
        s.LoadSetup(s.Model.Setups[0]);
        Assert.NotSame(old, s.Game);
        var n = 0;
        s.ViewChanged += () => n++;
        s.Game!.Execute(new MovePieces(new[] { "r1" }, "Main Map", new PointD(77, 50), null));
        Assert.Equal(1, n);
        Assert.Equal(77, s.View!.Pieces["r1"].Pos.X);
    }

    [Fact]
    public void CanSeeMap_RespectsOwners()
    {
        var s = Make(blueOwnsMap: true);
        Assert.True(s.CanSeeMap("Main Map"));
        s.SetViewSides(Red);
        Assert.False(s.CanSeeMap("Main Map"));
        s.SetViewSides(new HashSet<string> { "blue" });
        Assert.True(s.CanSeeMap("Main Map"));
        Assert.True(s.CanSeeMap("Other Map"));   // public
    }

    [Fact]
    public void GameReplaced_HandlerReadsViewNonNull_AndSoloResetsSides()
    {
        var s = Make();
        s.SetViewSides(Red);
        GameState? seen = null;
        s.GameReplaced += () => seen = s.View;
        s.LoadSetup(s.Model.Setups[0]);
        Assert.NotNull(seen);
        Assert.Same(s.Game!.State, seen);
        Assert.Null(s.ViewSides);   // solo game: view sides reset
    }

    [Fact]
    public void CanAct_HeldOrUnownedUnitsOnly()
    {
        var s = Make();
        Assert.True(s.CanAct("b1"));
        Assert.True(s.CanAct(null));
        s.SetViewSides(Red);
        Assert.True(s.CanAct("r1"));
        Assert.False(s.CanAct("b1"));
        Assert.False(s.CanAct("gone"));
        Assert.True(s.CanAct(null));             // unit-less runs are stamped with the actor side
        s.UpdateRules(p => p with { Sides = p.Sides.Take(1).ToList() });   // Blue 1 no longer belongs to any side
        Assert.True(s.CanAct("b1"));
    }

    [Fact]
    public void CanEditLedger_HeldOrPublicOwnersOnly()
    {
        var s = Make();
        s.UpdateRules(p => p with { Holders = new() { new HolderDef { Id = "bluedepot", Name = "Blue Depot", SideId = "blue" }, new HolderDef { Id = "pool", Name = "Pool" } } });
        Assert.True(s.CanEditLedger(LedgerKey.Piece("b1")));
        Assert.True(s.CanEditLedger(LedgerKey.Holder("bluedepot")));
        s.SetViewSides(Red);
        Assert.True(s.CanEditLedger(LedgerKey.Piece("r1")));
        Assert.False(s.CanEditLedger(LedgerKey.Piece("b1")));
        Assert.False(s.CanEditLedger(LedgerKey.Holder("bluedepot")));
        Assert.True(s.CanEditLedger(LedgerKey.Holder("pool")));
        Assert.True(s.CanEditLedger(LedgerKey.Holder(HolderDef.GlobalId)));
        Assert.False(s.CanEditLedger("garbage"));
    }

    [Fact]
    public void ViewFiltered_OnlyWhenSomethingIsHidden()
    {
        var s = Make();
        s.UpdateRules(p => p with { Properties = p.Properties.Select(x => x with { Visibility = Visibility.Public }).ToList() });
        Assert.False(s.ViewFiltered);
        Assert.Equal("Fuel 80", s.VisibleOnly("Fuel 80"));
        s.SetViewSides(Red);
        Assert.False(s.ViewFiltered);            // nothing private, no owned map: the view hides nothing
        Assert.Equal("Fuel 80", s.VisibleOnly("Fuel 80"));
        s.UpdateRules(p => p with { MapOwners = new() { new MapOwner { MapId = "Main Map", SideId = "blue" } } });
        Assert.True(s.ViewFiltered);
        Assert.Equal("Fuel 80 — visible units only", s.VisibleOnly("Fuel 80"));
    }

    [Fact]
    public void ViewFiltered_PrivateLedgerValueHidden()
    {
        var s = Make();
        s.UpdateRules(p => p with
        {
            Format = 4,
            Properties = p.Properties.Select(x => x.Id == "water" ? x with { Visibility = Visibility.Private } : x).ToList(),
        });
        s.Game!.Execute(new LedgerDelta(LedgerKey.Piece("b1"), "water", 10, 7, "manual", null, null, "test", null));
        s.SetViewSides(Red);
        Assert.True(s.ViewFiltered);
    }

    [Fact]
    public void HistoryLabels_HiddenPieces_ShowPlainUndoRedo()
    {
        var s = Make(blueOwnsMap: true);
        var h = new HistoryController(s, new PhaseController(s, (c, _) => c), (c, _) => c);
        using (s.Game!.BeginAction("move of Blue 1")) s.Game.Execute(new MovePieces(new[] { "b1" }, "Main Map", new PointD(80, 50), null));
        Assert.Equal("Undo move of Blue 1", h.UndoText);
        s.SetViewSides(Red);
        Assert.Equal("Undo", h.UndoText);
        h.Undo();
        Assert.Equal("Redo", h.RedoText);
        s.SetViewSides(null);
        Assert.Equal("Redo move of Blue 1", h.RedoText);
        h.Redo();
        using (s.Game.BeginAction("Turn 3")) s.Game.Execute(new SetTurn(3, null));
        s.SetViewSides(Red);
        Assert.Equal("Undo Turn 3", h.UndoText);   // touches no piece
    }

    [Fact]
    public void SetViewSides_CopiesTheSet()
    {
        var s = Make();
        var set = new HashSet<string> { "red" };
        s.SetViewSides(set);
        set.Add("blue");
        Assert.Single(s.ViewSides!);
    }

    [Fact]
    public void UpdateRules_InvalidatesView()
    {
        var s = Make();
        s.SetViewSides(Red);
        Assert.True(s.CanSee("b1"));
        Assert.True(s.CanSeeMap("Main Map"));
        var before = s.View;
        bool? seenInHandler = null;
        s.RulesChanged += () => seenInHandler = s.CanSeeMap("Main Map");
        s.UpdateRules(p => p with { MapOwners = new() { new MapOwner { MapId = "Main Map", SideId = "blue" } } });
        Assert.False(s.CanSeeMap("Main Map"));
        Assert.False(seenInHandler);
        Assert.NotSame(before, s.View);
        Assert.False(s.CanSee("b1"));
    }
}
