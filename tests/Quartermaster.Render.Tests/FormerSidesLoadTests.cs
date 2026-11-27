using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>B8: former side ids read as the pack's sides when a game is loaded or the pack changes. Invented sides (red, blue; former id "league").</summary>
public class FormerSidesLoadTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-fsl-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public FormerSidesLoadTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private ModuleSession Open()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        return s;
    }

    private static RulesPack WithFormer(RulesPack p, params string[] former) =>
        p with { Sides = p.Sides.Select(x => x.Id == "red" ? x with { FormerIds = former.ToList() } : x).ToList() };

    private static RulesPack Plain(ModuleSession s) => PhaseFixture.Pack(s.Archive.Sha256);

    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "league" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });

    /// <summary>A save written by a game that still used the side id "league".</summary>
    private string LegacySave(string name = "legacy.qmsave", params PlayerDef[] players)
    {
        var s = Open();
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        var g = s.Game!;
        g.Execute(new SetPlayers(players.Length > 0 ? players : new[] { Alice, Bob }));
        g.Execute(new AddLogLine("Note", "league note", null, null, null, "league"));
        g.Execute(new QueueRuns(new[] { new PendingRun("run1", "f-weather", "Weather roll", "start", new List<PendingUnit>(), true) { AskSideId = "league" } }));
        var path = Path.Combine(_tmp, name);
        s.SaveNative(path);
        return path;
    }

    private ModuleSession OpenWithPack(Func<RulesPack, RulesPack> tweak)
    {
        var s = Open();
        s.NewRules("t");
        s.UpdateRules(_ => tweak(Plain(s)));
        return s;
    }

    private static IEnumerable<Diagnostic> SideDiags(ModuleSession s) => s.Log.Items.Where(d => d.Category == "sides");

    [Fact]
    public void Load_translates_players_log_and_ask_runs()
    {
        var path = LegacySave();
        var s = OpenWithPack(p => WithFormer(p, "league"));
        s.LoadNative(path);
        var st = s.Game!.State;
        Assert.Equal(new[] { "red" }, st.Players!.First(p => p.Id == "p1").Sides);
        Assert.Contains(st.Log, l => l.SideId == "red");
        Assert.DoesNotContain(st.Log, l => l.SideId == "league");
        Assert.Equal("red", Assert.Single(st.Pending).AskSideId);
        Assert.Contains(st.Log, l => l.Text == "league note");   // log text is never rewritten
    }

    [Fact]
    public void Load_without_former_ids_keeps_save_bytes()
    {
        var maker = OpenWithPack(p => WithFormer(p, "league"));   // the pack has a former id the save does not use
        maker.LoadSetup(maker.Model.Setups[0], Difficulty.FullyAuto, new[] { new PlayerDef("p1", "Alice", new[] { "red" }), Bob });
        var path = Path.Combine(_tmp, "plain.qmsave");
        maker.SaveNative(path);
        var s = OpenWithPack(p => WithFormer(p, "league"));
        s.LoadNative(path);
        Assert.Empty(SideDiags(s));
        var again = Path.Combine(_tmp, "again.qmsave");
        s.SaveNative(again);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(again));
    }

    [Fact]
    public void Load_with_pack_without_former_ids_never_scans()
    {
        var path = LegacySave();
        var s = OpenWithPack(p => p);
        s.LoadNative(path);
        var game = s.Game;
        Assert.Empty(SideDiags(s));
        Assert.Equal(new[] { "league" }, s.Game!.State.Players!.First(p => p.Id == "p1").Sides);
        Assert.Same(game, s.Game);
    }

    [Fact]
    public void Load_logs_info_with_names()
    {
        var path = LegacySave();
        var s = OpenWithPack(p => WithFormer(p, "league"));
        s.LoadNative(path);
        var d = Assert.Single(SideDiags(s), x => x.Severity == DiagnosticSeverity.Info);
        Assert.Equal("Former side ids in the game now read as the rules pack's sides: \"league\" → Red", d.Message);
    }

    [Fact]
    public void Load_warns_side_held_by_two_players()
    {
        var path = LegacySave("two.qmsave", Alice, new PlayerDef("p3", "Cy", new[] { "red" }));
        var s = OpenWithPack(p => WithFormer(p, "league"));
        s.LoadNative(path);
        var w = Assert.Single(SideDiags(s), x => x.Severity == DiagnosticSeverity.Warning);
        Assert.Equal("Red is now held by more than one player; change it in Game → Players…", w.Message);
    }

    [Fact]
    public void Opening_pack_translates_open_game()
    {
        var path = LegacySave();
        var s = Open();
        s.LoadNative(path);
        Assert.Equal(new[] { "league" }, s.Game!.State.Players!.First(p => p.Id == "p1").Sides);
        var packPath = Path.Combine(_tmp, "r.qrules");
        var maker = Open();
        maker.NewRules("t");
        maker.UpdateRules(_ => WithFormer(Plain(maker), "league"));
        maker.SaveRules(packPath);
        s.OpenRules(packPath);
        Assert.Equal(new[] { "red" }, s.Game!.State.Players!.First(p => p.Id == "p1").Sides);
        Assert.Single(SideDiags(s), x => x.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void Workbench_edit_adding_former_id_translates_open_game()
    {
        var path = LegacySave();
        var s = OpenWithPack(p => p);
        s.LoadNative(path);
        s.UpdateRules(p => WithFormer(p, "league"));
        Assert.Equal(new[] { "red" }, s.Game!.State.Players!.First(p => p.Id == "p1").Sides);
    }

    [Fact]
    public void Unrelated_workbench_edit_does_not_rescan()
    {
        var path = LegacySave();
        var s = OpenWithPack(p => WithFormer(p, "league"));
        s.LoadNative(path);
        Assert.Single(SideDiags(s), x => x.Severity == DiagnosticSeverity.Info);
        // A command that reintroduces a former id would be translated again by a rescan; the unchanged map must not trigger one.
        s.Game!.Execute(new AddLogLine("Note", "again", null, null, null, "league"));
        s.UpdateRules(p => p with { Name = "renamed" });
        Assert.Contains(s.Game.State.Log, l => l.Text == "again" && l.SideId == "league");
        Assert.Single(SideDiags(s), x => x.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void Save_after_translation_writes_listing_ids_and_reloads_clean()
    {
        var path = LegacySave();
        var s = OpenWithPack(p => WithFormer(p, "league"));
        s.LoadNative(path);
        var out1 = Path.Combine(_tmp, "out.qmsave");
        s.SaveNative(out1);
        var s2 = OpenWithPack(p => WithFormer(p, "league"));
        s2.LoadNative(out1);
        Assert.Empty(SideDiags(s2));
        Assert.Equal(new[] { "red" }, s2.Game!.State.Players!.First(p => p.Id == "p1").Sides);
        Assert.DoesNotContain(s2.Game.State.Log, l => l.SideId == "league");
    }

    [Fact]
    public void Guest_never_translates()
    {
        var s = OpenWithPack(p => WithFormer(p, "league"));
        s.EnterGuestMode();
        s.ApplySnapshot(new GameState { Players = new List<PlayerDef> { Alice } });
        s.LoadGuestRules(WithFormer(Plain(s), "league"));
        Assert.Equal(new[] { "league" }, s.Game!.State.Players!.Single().Sides);
        Assert.Empty(SideDiags(s));
    }

    [Fact]
    public void Turn_replay_not_translated()
    {
        var s = OpenWithPack(p => WithFormer(p, "league"));
        var start = new GameSession(new GameState { Players = new List<PlayerDef> { Alice } }, s.Archive.Sha256);
        s.BeginTurnReplay(start);
        Assert.Equal(new[] { "league" }, s.Game!.State.Players!.Single().Sides);
        Assert.Empty(SideDiags(s));
    }

    [Fact]
    public void Viewer_copy_translated_in_memory()
    {
        var src = Open();
        src.EnterGuestMode();
        src.ApplySnapshot(new GameState { Players = new List<PlayerDef> { Alice } });
        var path = Path.Combine(_tmp, "view.qmsave");
        src.ExportViewerCopy(path);
        var before = File.ReadAllBytes(path);
        var s = OpenWithPack(p => WithFormer(p, "league"));
        s.LoadNative(path);
        Assert.True(s.IsViewerCopy);
        Assert.Equal(new[] { "red" }, s.Game!.State.Players!.Single().Sides);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Hotseat_handover_after_translation_goes_to_listing_side_player()
    {
        var path = LegacySave();
        var s = OpenWithPack(p => WithFormer(p, "league"));
        s.LoadNative(path);
        Func<GameCommand, GameState, GameCommand> prepare = (c, _) => c;
        var phases = new PhaseController(s, prepare);
        var hs = new HotSeat(s, phases);
        phases.Start(); phases.Advance();                  // Supply (red)
        hs.TakeOver(new PlayerDef("p2", "Bob", new[] { "blue" }));
        Assert.Equal("p1", hs.HandoverDue()?.Id);
    }

    [Fact]
    public void Normal_flows_do_not_throw_and_Changed_still_fires()
    {
        var path = LegacySave();
        var s = OpenWithPack(p => p);
        s.LoadNative(path);
        s.UpdateRules(p => WithFormer(p, "league"));   // translates the open game outside any scope
        var packPath = Path.Combine(_tmp, "n.qrules");
        s.SaveRules(packPath);
        s.OpenRules(packPath);
        var fired = 0;
        s.Game!.Changed += _ => fired++;
        s.Game.Execute(new AddLogLine("Note", "after", null, null, null, "red"));
        Assert.Equal(1, fired);
        using (s.Game.DeferChanged()) s.Game.Execute(new AddLogLine("Note", "deferred", null, null, null, "red"));
        Assert.Equal(2, fired);
    }
}
