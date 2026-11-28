using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B5 #73 in the phase controller: the last turn and victory conditions end the game (one Next Phase action, undoable), victory on phase
/// entry, host only. Invented PhaseFixture pack (Red, Blue; Weather, Supply, Movement, Combat, End; Round block of 2); temp roots only.
/// </summary>
public class GameEndControllerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-gameend-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public GameEndControllerTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private (ModuleSession S, PhaseController C) Make(Difficulty d, Func<RulesPack, RulesPack>? tweak = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d);
        s.NewRules("t");
        s.UpdateRules(_ => { var p = WithRiver(PhaseFixture.Pack(s.Archive.Sha256)); return tweak == null ? p : tweak(p); });
        return (s, new PhaseController(s, (c, _) => c));
    }

    private static RulesPack WithRiver(RulesPack p) =>
        p with { Properties = p.Properties.Append(new PropertyDef { Id = "river", Name = "River", Scope = PropertyScope.Holders, Default = 0 }).ToList() };

    private static PhasePosition Pos(ModuleSession s) => s.Game!.State.Position!;
    private static double Water(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "water") ?? 10;
    private static double? River(ModuleSession s) => Ledger.Get(s.Game!.State, LedgerKey.Holder(HolderDef.GlobalId), "river");

    private static void SetRiver(ModuleSession s, double v) =>
        s.Game!.Execute(new LedgerDelta(LedgerKey.Holder(HolderDef.GlobalId), "river", River(s) ?? 0, v, "manual", null, null, "test", null));

    private static VictoryCondition V(string id, string condition, string? side = null, string? message = null, params string[] phaseIds) =>
        new() { Id = id, Condition = condition, SideId = side, Message = message, PhaseIds = phaseIds.Length == 0 ? null : phaseIds.ToList() };

    private static Func<RulesPack, RulesPack> Ends(int? lastTurn, params VictoryCondition[] victory) =>
        p => p with { LastTurn = lastTurn, Victory = victory.Length == 0 ? null : victory.ToList() };

    /// <summary>Start, then Next Phase to End of turn 1 (Weather, Supply, Movement, Combat, Movement, Combat, End).</summary>
    private static void ToEnd(ModuleSession s, PhaseController c)
    {
        c.Start();
        for (var i = 0; i < 20 && Pos(s).PhaseId != "ph-end"; i++) c.Advance();
        Assert.Equal(new PhasePosition(1, 1, "ph-end", 4), Pos(s));
    }

    private static IEnumerable<string> Texts(ModuleSession s) => s.Game!.State.Log.Select(l => l.Text);

    // ---- the last turn ----

    [Fact]
    public void Last_turn_ends_game_in_next_phase_action()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ends(1));
        ToEnd(s, c);
        var boundaries = 0;
        c.PhaseBoundary += () => boundaries++;
        var actions = s.Game!.Actions.Count;

        Assert.Null(c.Advance());

        var over = s.Game.State.GameOver;
        Assert.NotNull(over);
        Assert.Equal("the last turn has been played", over!.Text);
        Assert.Null(over.Winners);
        Assert.Null(over.VictoryId);
        Assert.Equal(new PhasePosition(1, 1, "ph-end", 4), Pos(s));   // the position stays
        Assert.Equal(new PhasePosition(1, 1, "ph-end", 4), over.At);
        Assert.Equal("Game over: the last turn has been played", s.Game.State.Log[^1].Text);
        Assert.Equal(actions + 1, s.Game.Actions.Count);
        Assert.Equal("Next Phase", s.Game.UndoLabel);
        Assert.Equal(1, boundaries);   // the final autosave
        Assert.DoesNotContain("Turn 2 · End → Weather", Texts(s));
    }

    [Fact]
    public void Last_turn_victory_checked_at_end()
    {
        // Limited to Weather, so it is not checked on entry later in the turn; the last-turn check reads every condition.
        var (s, c) = Make(Difficulty.FullyAuto, Ends(1, V("v-low", "River >= 5", "blue"), V("v-river", "River >= 1", "red", null, "ph-weather")));
        c.Start();
        SetRiver(s, 1);
        for (var i = 0; i < 20 && Pos(s).PhaseId != "ph-end"; i++) c.Advance();
        Assert.Null(s.Game!.State.GameOver);
        c.Advance();
        var over = s.Game.State.GameOver!;
        Assert.Equal("Red wins", over.Text);
        Assert.Equal(new[] { "red" }, over.Winners);
        Assert.Equal("v-river", over.VictoryId);
        Assert.Equal("Game over: Red wins", s.Game.State.Log[^1].Text);
    }

    [Fact]
    public void Last_turn_no_victory_text()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ends(1, V("v-river", "River >= 1", "red", "The Red fleet holds the river")));
        ToEnd(s, c);
        c.Advance();
        var over = s.Game!.State.GameOver!;
        Assert.Equal(GameEnd.LastTurnText, over.Text);
        Assert.Null(over.Winners);
        Assert.Null(over.VictoryId);
    }

    [Fact]
    public void Last_turn_open_runs_logged_skipped()
    {
        var (s, c) = Make(Difficulty.SemiAuto, Ends(1));
        c.Start();
        for (var i = 0; i < 20 && Pos(s).PhaseId != "ph-end"; i++) c.Advance();
        s.Game!.Execute(new QueueRuns(new[] { new PendingRun("run-x", "f-weather", "Weather roll", "start",
            new[] { new PendingUnit(null, "Weather roll", null, false) }, false) }));
        c.Advance();
        Assert.NotNull(s.Game.State.GameOver);
        Assert.Empty(s.Game.State.Pending);
        Assert.Contains("skipped: Weather roll, Weather roll", Texts(s));
    }

    [Fact]
    public void Last_stage_ends_game()
    {
        // Last turn 1, last stage 1: leaving Combat in Round 1 ends the game; Round 2 is never played.
        var (s, c) = Make(Difficulty.FullyAuto, p => p with { LastTurn = 1, LastStage = 1 });
        c.Start();
        for (var i = 0; i < 20 && Pos(s).PhaseId != "ph-combat"; i++) c.Advance();
        Assert.Equal(new PhasePosition(1, 1, "ph-combat", 3), Pos(s));
        Assert.Null(s.Game!.State.GameOver);
        c.Advance();
        var over = s.Game.State.GameOver;
        Assert.NotNull(over);
        Assert.Equal(GameEnd.LastTurnText, over!.Text);
        Assert.Equal(new PhasePosition(1, 1, "ph-combat", 3), Pos(s));
        Assert.Equal("Game over: the last turn has been played", s.Game.State.Log[^1].Text);
    }

    // ---- victory on entry ----

    [Fact]
    public void Victory_on_entry_ends_game_and_skips_start_entries()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ends(null, V("v-river", "River >= 1", "red", "The Red fleet holds the river")));
        c.Start();
        SetRiver(s, 1);
        var boundaries = 0;
        c.PhaseBoundary += () => boundaries++;

        Assert.Null(c.Advance());   // no "Pass to Red player": the game is over

        var over = s.Game!.State.GameOver!;
        Assert.Equal("The Red fleet holds the river", over.Text);
        Assert.Equal(new[] { "red" }, over.Winners);
        Assert.Equal("v-river", over.VictoryId);
        Assert.Equal("ph-supply", Pos(s).PhaseId);
        Assert.Equal(Pos(s), over.At);
        Assert.Equal(10, Water(s, "r1"));   // Supply's start entries did not run
        Assert.Contains("Turn 1 · Weather → Supply", Texts(s));
        Assert.Equal("Game over: The Red fleet holds the river", s.Game.State.Log[^1].Text);
        Assert.Equal("Next Phase", s.Game.UndoLabel);
        Assert.Equal(1, boundaries);
        Assert.DoesNotContain("Pass to", c.View().Notice ?? "");
    }

    [Fact]
    public void Victory_of_an_alliance_records_every_ally()
    {
        // B10 Task 3: Red and Blue share the invented "Tide Pact"; Blue's condition makes both win, Blue first; a blank message names the pact.
        var (s, c) = Make(Difficulty.FullyAuto, p => Ends(null, V("v-river", "River >= 1", "blue"))(p with
        {
            Sides = p.Sides.Select(x => x with { AllianceId = x.Id == "red" ? "Tide Pact" : " tide pact " }).ToList(),
        }));
        c.Start();
        SetRiver(s, 1);
        Assert.Null(c.Advance());
        var over = s.Game!.State.GameOver!;
        Assert.Equal(new[] { "blue", "red" }, over.Winners);
        Assert.Equal("tide pact wins", over.Text);
        Assert.Equal("Game over: tide pact wins", s.Game.State.Log[^1].Text);
    }

    [Fact]
    public void Victory_on_start_ends_game()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ends(null, V("v-any", "turn() >= 1")));
        c.Start();
        Assert.Equal("ph-weather", Pos(s).PhaseId);
        Assert.Equal("the game has ended", s.Game!.State.GameOver!.Text);
        Assert.Null(s.Game.State.GameOver.Winners);
        Assert.DoesNotContain(s.Game.State.Log, l => l.Text.StartsWith("Weather roll"));   // Weather's start entry did not run
    }

    [Fact]
    public void Victory_phase_ids_limit()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ends(null, V("v-river", "River >= 1", "blue", null, "ph-combat")));
        c.Start();
        SetRiver(s, 1);
        c.Advance();   // Supply
        Assert.Null(s.Game!.State.GameOver);
        c.Advance();   // Movement
        Assert.Null(s.Game.State.GameOver);
        c.Advance();   // Combat
        Assert.Equal("Blue wins", s.Game.State.GameOver!.Text);
        Assert.Equal("ph-combat", Pos(s).PhaseId);
    }

    [Fact]
    public void Victory_not_checked_on_guest_or_replay()
    {
        var always = Ends(1, V("v-any", "turn() >= 1", "red"));

        // A received turn replaying: entering phases checks nothing and the last turn does not end the game.
        var (r, rc) = Make(Difficulty.FullyAuto, always);
        r.BeginTurnReplay(r.Game!);
        Assert.True(r.IsReplaying);
        rc.Start();
        for (var i = 0; i < 20 && Pos(r).Turn < 2; i++) rc.Advance();
        Assert.Equal(2, Pos(r).Turn);
        Assert.Null(r.Game!.State.GameOver);

        // An online guest never decides the end either.
        var (g, gc) = Make(Difficulty.FullyAuto, always);
        g.EnterGuestMode();
        g.ApplySnapshot(new GameState { GameId = "g1" });
        gc.Start();
        for (var i = 0; i < 20 && Pos(g).Turn < 2; i++) gc.Advance();
        Assert.Equal(2, Pos(g).Turn);
        Assert.Null(g.Game!.State.GameOver);

        // The host does.
        var (h, hc) = Make(Difficulty.FullyAuto, always);
        hc.Start();
        Assert.NotNull(h.Game!.State.GameOver);
    }

    [Fact]
    public void Game_end_not_decided_on_viewer_copy()
    {
        // A viewer copy (a player's view of an online game, opened read-only) never decides the end, at the last turn or on entry.
        var (s, c) = Make(Difficulty.FullyAuto, Ends(1, V("v-any", "turn() >= 2", "red")));
        ToEnd(s, c);
        var path = Path.Combine(_tmp, "viewer.qmsave");
        s.SaveNative(path);
        var json = File.ReadAllText(path);
        var open = json.IndexOf('{');
        File.WriteAllText(path, json[..(open + 1)] + "\"viewerCopy\":true," + json[(open + 1)..]);
        var (v, vc) = Make(Difficulty.FullyAuto, Ends(1, V("v-any", "turn() >= 2", "red")));
        v.LoadNative(path);
        Assert.True(v.IsViewerCopy);
        Assert.Equal(new PhasePosition(1, 1, "ph-end", 4), Pos(v));
        vc.Advance();
        Assert.Null(v.Game!.State.GameOver);
        Assert.Equal(2, Pos(v).Turn);   // stepped on into turn 2, where the condition holds, without ending

        c.Advance();   // the host's own game does end
        Assert.NotNull(s.Game!.State.GameOver);
    }

    // ---- after the end ----

    [Fact]
    public void Undo_out_of_game_over()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ends(1));
        ToEnd(s, c);
        c.Advance();
        Assert.NotNull(s.Game!.State.GameOver);
        c.Advance();   // nothing to undo: only the notice
        Assert.Equal(PhaseController.GameOverText, c.View().Notice);
        s.Game.Undo();
        Assert.Null(s.Game.State.GameOver);
        Assert.Null(c.View().Notice);   // the notice belonged to the ended game
        Assert.Equal(new PhasePosition(1, 1, "ph-end", 4), Pos(s));
        Assert.Null(c.View().GameOverText);
        c.Advance();   // ends again
        Assert.NotNull(s.Game.State.GameOver);
    }

    [Fact]
    public void Advance_after_game_over_does_nothing_notice()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ends(1));
        ToEnd(s, c);
        c.Advance();
        var log = s.Game!.State.Log.Count;
        var commands = s.Game.Log.Count;
        var boundaries = 0;
        c.PhaseBoundary += () => boundaries++;

        Assert.Null(c.Advance());
        Assert.Null(c.Advance(true));

        Assert.Equal(log, s.Game.State.Log.Count);
        Assert.Equal(commands, s.Game.Log.Count);
        Assert.Equal(new PhasePosition(1, 1, "ph-end", 4), Pos(s));
        Assert.Equal(0, boundaries);
        Assert.Equal(PhaseController.GameOverText, c.View().Notice);
        Assert.Equal("The game is over.", PhaseController.GameOverText);
    }

    [Fact]
    public void View_game_over_text()
    {
        // An open Round block: the question is not asked once the game is over.
        var (s, c) = Make(Difficulty.FullyAuto, p => Ends(null, V("v-river", "River >= 1", "red", null, "ph-combat"))(p) with
        {
            Blocks = new() { new PhaseBlock { FirstPhaseId = "ph-move", LastPhaseId = "ph-combat", Count = 0, Label = "Round" } },
        });
        c.Start();
        Assert.Null(c.View().GameOverText);
        SetRiver(s, 1);
        c.Advance();
        c.Advance();
        c.Advance();   // Combat: Red wins on entry
        var v = c.View();
        Assert.Equal("Game over: Red wins", v.GameOverText);
        Assert.Null(v.RepeatAsk);
        var panel = PhaseTexts.Panel(v, null);
        Assert.Equal("Game over: Red wins", panel.NextText);
        Assert.False(panel.NextEnabled);
        Assert.Contains(PhaseTexts.Strip(v), x => x.Text == "Game over: Red wins" && x.State == null);
    }

    [Fact]
    public void Victory_error_warns_once()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ends(null, V("v-bad", "Nowhere >= 1"), V("v-worse", "1 / ")));
        c.Start();
        c.Advance();
        c.Advance();
        c.Advance();
        Assert.Null(s.Game!.State.GameOver);   // fail-closed
        var warnings = s.Log.Items.Where(d => d.Message.StartsWith("Victory condition ")).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Equal(DiagnosticSeverity.Warning, w.Severity));
        Assert.All(warnings, w => Assert.Equal("rules", w.Category));
        Assert.StartsWith("Victory condition 1 failed: ", warnings[0].Message);
        Assert.EndsWith("; it counts as not met", warnings[0].Message);
        Assert.StartsWith("Victory condition 2 failed: ", warnings[1].Message);
    }

    [Fact]
    public void Banner_game_over()
    {
        var (s, c) = Make(Difficulty.FullyAuto, Ends(1));
        ToEnd(s, c);
        c.Advance();
        Assert.Equal(("Game over: the last turn has been played", (string?)null), Banner.For(c.View()));
    }

    [Fact]
    public void Harness_game_over()
    {
        using var fx = PhaseFixture.Module(true);
        var vmod = fx.Build();
        string sha;
        using (var a = VmodArchive.Open(vmod)) sha = a.Sha256;
        var rules = Path.Combine(_tmp, "p.qrules");
        RulesPackIo.Save(PhaseFixture.Pack(sha) with { LastTurn = 1 }, rules);
        using var h = PackHarness.Open(vmod, rules, Path.Combine(_tmp, "harness-cache"));
        h.NewGame(null, Difficulty.FullyAuto);
        for (var i = 0; i < 20 && h.GameOver == null; i++) h.Advance();
        Assert.Equal(GameEnd.LastTurnText, h.GameOver!.Text);
        Assert.Equal("End", h.Phase);
    }
}
