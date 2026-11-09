using System.Text.RegularExpressions;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

[Collection(AppDataRootEnv.Name)]
public class CommandAvailabilityTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-ca-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var k in _keep) k.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static FixtureModule Fx() => new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile());

    private ModuleSession Open(FixtureModule fx) => ModuleSession.Open(fx.Build(), _tmp, null);

    private static readonly string[] GuestIds = { "file.openModule", "file.newGame", "file.loadSetup", "file.openGame", "file.openAutosave",
        "file.openTurn", "file.newRules", "file.openRules", "file.saveRules", "file.saveRulesAs",
        "tool.openModule", "tool.newGame", "tool.loadSetup", "tool.openGame" };
    private static readonly string[] SaveIds = { "file.saveGame", "file.saveGameAs", "tool.saveGame" };
    private static readonly string[] GameIds = { "file.saveGame", "file.saveGameAs", "view.log", "view.outliner", "view.overview", "view.palette",
        "tools.inventory", "tools.notes", "tools.dice", "tools.turn", "help.tour", "tool.saveGame", "tool.notes", "tool.overview", "tool.inventory",
        "tool.palette", "tool.los", "dice.1d6", "dice.2d6", "dice.menu", "turn.next" };
    private static readonly string[] ModuleIds = { "file.newGame", "file.loadSetup", "file.openGame", "file.openAutosave", "file.openTurn",
        "file.newRules", "file.openRules", "view.diagnostics", "tools.workbench", "tool.newGame", "tool.loadSetup", "tool.openGame", "tool.diagnostics" };
    private static readonly string[] RulesIds = { "file.saveRules", "file.saveRulesAs", "view.ledger" };
    private static readonly string[] ReplayIds = { "file.newRules", "file.openRules", "tools.workbench" };

    private static void AssertOnly(IReadOnlyDictionary<string, (bool Enabled, string? Reason)> a, IEnumerable<string> enabled)
    {
        var on = enabled.ToHashSet();
        foreach (var id in CommandAvailability.Ids) Assert.True(a[id].Enabled == on.Contains(id), $"{id} enabled={a[id].Enabled}");
    }


    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });

    private sealed class FakeProtector : IPasswordProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).Prepend((byte)0xEE).ToArray();
        public byte[] Unprotect(byte[] p) => p[0] == 0xEE ? p.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray() : throw new InvalidDataException();
    }

    private readonly List<IDisposable> _keep = new();

    private ModuleSession Game(string name, out FixtureModule fx)
    {
        fx = PhaseFixture.Module();
        _keep.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache-" + name), null);
        _keep.Add(s);
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        return s;
    }

    private (FixtureModule fx, ModuleSession s) WithGame()
    {
        var s = Game("g", out var fx);
        return (fx, s);
    }

    [Fact]
    public void No_module_only_guest_blocked_open_module_enabled()
    {
        var a = CommandAvailability.For(null);
        AssertOnly(a, new[] { "file.openModule", "tool.openModule" });
        foreach (var id in CommandAvailability.Ids) Assert.Null(a[id].Reason);
    }

    [Fact]
    public void Module_without_rules_or_game()
    {
        using var fx = Fx();
        using var s = Open(fx);
        var a = CommandAvailability.For(s);
        AssertOnly(a, new[] { "file.openModule", "tool.openModule" }.Concat(ModuleIds));
        foreach (var id in CommandAvailability.Ids) Assert.Null(a[id].Reason);
    }

    [Fact]
    public void Rules_commands_disabled_without_pack()
    {
        using var fx = Fx();
        using var s = Open(fx);
        var a = CommandAvailability.For(s);
        foreach (var id in RulesIds) Assert.False(a[id].Enabled, id);
        s.NewRules("X");
        a = CommandAvailability.For(s);
        foreach (var id in RulesIds) Assert.True(a[id].Enabled, id);
    }

    [Fact]
    public void Game_commands_follow_the_game_and_save_follows_can_save()
    {
        var (fx, s) = WithGame();
        using (fx) using (s)
        {
            Assert.NotNull(s.Game);
            var a = CommandAvailability.For(s);
            foreach (var id in GameIds) Assert.True(a[id].Enabled, id);
            foreach (var id in SaveIds) Assert.Null(a[id].Reason);
        }
    }

    [Fact]
    public void Viewer_copy_cannot_save_and_says_so()
    {
        var s = Game("src", out _);
        s.NewRules("v");
        var path = Path.Combine(_tmp, "v.qmsave");
        s.SaveNative(path);
        var json = File.ReadAllText(path);
        var open = json.IndexOf('{');
        File.WriteAllText(path, json[..(open + 1)] + "\"viewerCopy\":true," + json[(open + 1)..]);
        var v = Game("viewer", out _);
        v.NewRules("v");
        v.LoadNative(path);
        Assert.True(v.IsViewerCopy);
        var a = CommandAvailability.For(v);
        foreach (var id in SaveIds)
        {
            Assert.False(a[id].Enabled, id);
            Assert.Equal(ModuleSession.ViewerCopyText, a[id].Reason);
        }
        Assert.True(a["view.log"].Enabled);
        Assert.True(a["file.newGame"].Enabled);
    }

    [Fact]
    public void Replay_blocks_rules_edits_and_saving_with_replaying_text()
    {
        var alice = Game("alice", out _);
        var g = alice.Game!;
        var id = g.State.Pieces.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
        var p = g.State.Pieces[id];
        using (g.BeginAction()) g.Execute(new MovePieces(new[] { id }, p.MapId, new PointD(p.Pos.X + 10, p.Pos.Y), null));
        var identity = TurnIdentity.Load(g.State.GameId!, Path.Combine(_tmp, "t-alice"), new FakeProtector());
        identity.LocalPlayerId = Alice.Id;
        var turn = TurnSender.Build(alice, Bob.Id, identity);
        TurnSender.AfterSend(alice);
        var bob = Game("bob", out _);
        bob.NewRules("r");
        var bobId = TurnIdentity.Load(turn.GameId, Path.Combine(_tmp, "t-bob"), new FakeProtector());
        var model = bob.OpenTurn(turn, Bob.Id, bobId);
        Assert.True(bob.IsReplaying);
        var a = CommandAvailability.For(bob);
        foreach (var rid in ReplayIds)
        {
            Assert.False(a[rid].Enabled, rid);
            Assert.Equal(ModuleSession.ReplayingText, a[rid].Reason);
        }
        foreach (var sid in SaveIds) Assert.False(a[sid].Enabled, sid);   // CanSave is false while replaying; no text, as in WinForms
        Assert.False(a["file.openTurn"].Enabled);
        Assert.True(a["file.newGame"].Enabled);
        model.PlayAll();
        Assert.False(bob.IsReplaying);
        Assert.True(CommandAvailability.For(bob)["file.newRules"].Enabled);
        Assert.Null(CommandAvailability.For(bob)["file.newRules"].Reason);
    }

    [Fact]
    public void Guest_blocked_commands_disabled_as_guest()
    {
        var (fx, s) = WithGame();
        using (fx) using (s)
        {
            s.NewRules("X");
            s.EnterGuestMode();
            var a = CommandAvailability.For(s);
            foreach (var id in GuestIds)
            {
                Assert.False(a[id].Enabled, id);
                Assert.Equal(ModuleSession.GuestBusyText, a[id].Reason);
            }
            foreach (var id in SaveIds)
            {
                Assert.False(a[id].Enabled, id);
                Assert.Equal(ModuleSession.GuestSaveText, a[id].Reason);
            }
            Assert.True(a["view.ledger"].Enabled);
            Assert.True(a["tools.workbench"].Enabled);
            Assert.Null(a["view.ledger"].Reason);
        }
    }

    [Fact]
    public void Online_switches_open_turn_off_only()
    {
        using var fx = Fx();
        using var s = Open(fx);
        Assert.True(CommandAvailability.For(s)["file.openTurn"].Enabled);
        var a = CommandAvailability.For(s, online: true);
        Assert.False(a["file.openTurn"].Enabled);
        Assert.Null(a["file.openTurn"].Reason);
        Assert.True(a["file.newGame"].Enabled);
    }

    [Fact]
    public void Replay_blocked_ids_are_listed()
    {
        Assert.Equal(ReplayIds.OrderBy(x => x), CommandAvailability.ReplayBlockedIds.OrderBy(x => x));
    }

    [Fact]
    public void Own_tooltip_texts_are_recognised()
    {
        Assert.True(CommandAvailability.IsOwnText(ModuleSession.ReplayingText));
        Assert.True(CommandAvailability.IsOwnText(ModuleSession.GuestSaveText));
        Assert.False(CommandAvailability.IsOwnText("Ctrl+S"));
        Assert.True(CommandAvailability.OwnsTooltip("file.newRules"));
        Assert.False(CommandAvailability.OwnsTooltip("dice.1d6"));
    }

    /// <summary>Drift guard: every id the WinForms MainForm registers as gated exists here, and every id here is registered there.</summary>
    [Fact]
    public void Every_gated_winforms_id_is_in_the_table_and_vice_versa()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Quartermaster.slnx"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var app = Path.Combine(dir!, "src", "Quartermaster.App");
        var main = File.ReadAllText(Path.Combine(app, "MainForm.cs"));
        var dice = File.ReadAllText(Path.Combine(app, "DiceTools.cs"));
        var found = new HashSet<string>();
        // Item(into, "text", act, cmd, "gateId") ; Gate("id", item) ; Tool("id", ...) ; DiceTools: AddButton(toolbar, "id", ...) / gate("id", x)
        foreach (Match m in Regex.Matches(main, "Item\\([^;]*?,\\s*(?:null|\"[^\"]+\"),\\s*\"([A-Za-z0-9.]+)\"\\)")) found.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(main, "Gate\\(\"([A-Za-z0-9.]+)\"")) found.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(main, "Tool\\(\"([A-Za-z0-9.]+)\"")) found.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(dice, "(?:AddButton\\(toolbar,|gate\\()\\s*\"([A-Za-z0-9.]+)\"")) found.Add(m.Groups[1].Value);
        Assert.True(found.Count >= 35, "scan found " + found.Count);
        Assert.Empty(found.Except(CommandAvailability.Ids));
        Assert.Empty(CommandAvailability.Ids.Except(found));
    }
}
