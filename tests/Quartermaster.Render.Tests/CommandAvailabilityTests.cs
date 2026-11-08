using Quartermaster.Render;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

public class CommandAvailabilityTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-ca-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private ModuleSession Open(FixtureModule fx) => ModuleSession.Open(fx.Build(), _tmp, null);

    [Fact]
    public void No_module_only_open_module_enabled()
    {
        var a = CommandAvailability.For(null);
        foreach (var id in CommandAvailability.Ids)
            Assert.Equal(id == "file.openModule", a[id].Enabled);
    }

    [Fact]
    public void Rules_commands_disabled_without_pack()
    {
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile());
        using var s = Open(fx);
        var a = CommandAvailability.For(s);
        foreach (var id in new[] { "file.saveRules", "file.saveRulesAs", "view.ledger" }) Assert.False(a[id].Enabled, id);
        foreach (var id in new[] { "file.newRules", "file.openRules", "file.newGame", "file.openModule" }) Assert.True(a[id].Enabled, id);
        s.NewRules("X");
        a = CommandAvailability.For(s);
        foreach (var id in new[] { "file.saveRules", "file.saveRulesAs", "view.ledger" }) Assert.True(a[id].Enabled, id);
    }

    [Fact]
    public void Guest_blocked_commands_disabled_as_guest()
    {
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile());
        using var s = Open(fx);
        s.NewRules("X");
        s.EnterGuestMode();
        var guest = CommandAvailability.For(s);
        var blocked = new[] { "file.openModule", "file.newGame", "file.loadSetup", "file.openGame", "file.openAutosave", "file.openTurn",
            "file.newRules", "file.openRules", "file.saveRules", "file.saveRulesAs" };
        foreach (var id in blocked)
        {
            Assert.False(guest[id].Enabled, id);
            Assert.Equal(ModuleSession.GuestBusyText, guest[id].Reason);
        }
        Assert.True(guest["view.ledger"].Enabled);
        Assert.Null(guest["view.ledger"].Reason);
    }
}
