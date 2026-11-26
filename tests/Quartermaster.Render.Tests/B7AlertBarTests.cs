using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>B7 #74b: the alert bar evaluates for the viewer's sides. Invented pack from PhaseFixture; temp roots only.</summary>
public class B7AlertBarTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b7ab-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };

    public B7AlertBarTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static AlertDef Side(string side, string prefix) =>
        new() { Id = "a-" + side, Name = side + " alert", Condition = $"startsWith(name(), \"{prefix}\")", Message = "here", Severity = AlertSeverity.Warning, SideId = side };

    private ModuleSession Make()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256) with { Alerts = new() { Side("red", "Red"), Side("blue", "Blue") } });
        return s;
    }

    private static IEnumerable<string> Ids(ModuleSession s) =>
        new AlertBarModel(s).Recompute(null, "red", new HashSet<string>()).Select(c => c.Id).OrderBy(x => x);

    [Fact]
    public void Hotseat_alert_bar_follows_view_sides()
    {
        var s = Make();
        Assert.Equal(new[] { "a-blue", "a-red" }, Ids(s));   // solo: every side alert, whatever the phase
        s.SetViewSides(Red);
        Assert.Equal(new[] { "a-red" }, Ids(s));
        s.SetViewSides(Blue);
        Assert.Equal(new[] { "a-blue" }, Ids(s));
        s.SetViewSides(new HashSet<string> { "red", "blue" });
        Assert.Equal(new[] { "a-blue", "a-red" }, Ids(s));
    }

    [Fact]
    public void Guest_alert_bar_uses_guest_sides()
    {
        var host = Make();
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var guest = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "gcache"), Path.Combine(_tmp, "groot"));
        _sessions.Add(guest);
        guest.EnterGuestMode();
        guest.LoadGuestRules(host.Rules!);
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, Blue), Blue);
        Assert.Equal(new[] { "a-blue" }, Ids(guest));
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, Red), Red);
        Assert.Equal(new[] { "a-red" }, Ids(guest));
    }

    [Fact]
    public void Game_over_alert_bar_all()
    {
        var s = Make();
        s.SetViewSides(Red);
        Assert.Equal(new[] { "a-red" }, Ids(s));
        s.Game!.State.GameOver = new GameOverState("done", null, null, s.Game.State.Position);
        s.SetViewSides(Red);
        Assert.Equal(new[] { "a-blue", "a-red" }, Ids(s));
    }
}
