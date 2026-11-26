using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>B5 #73: game over state (<see cref="EndGame"/>), the last turn and victory conditions (<see cref="GameEnd"/>). Invented Riverland data.</summary>
public class GameEndTests
{
    private static ModuleModel Model => RunnerFx.Model;

    /// <summary>Weather, North Supply, North Movement, South Combat, End; block Day over North Movement–South Combat.</summary>
    private static RulesPack Pack(int dayCount = 3, int? lastTurn = null, int? lastStage = null, params VictoryCondition[] victory) => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"North\")" },
            new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"South\")" },
        },
        Properties = new() { new PropertyDef { Id = "ports", Name = "Ports", Scope = PropertyScope.Holders, Default = 0 } },
        Phases = new()
        {
            new PhaseDef { Id = "W", Name = "Weather" },
            new PhaseDef { Id = "NS", Name = "North Supply", SideId = "north" },
            new PhaseDef { Id = "NM", Name = "North Movement", SideId = "north" },
            new PhaseDef { Id = "SC", Name = "South Combat", SideId = "south" },
            new PhaseDef { Id = "E", Name = "End" },
        },
        Blocks = new() { new PhaseBlock { FirstPhaseId = "NM", LastPhaseId = "SC", Count = dayCount, Label = "Day" } },
        LastTurn = lastTurn,
        LastStage = lastStage,
        Victory = victory.Length == 0 ? null : victory.ToList(),
    };

    private static PhasePosition P(RulesPack pk, int turn, int repeat, string id) => new(turn, repeat, id, pk.Phases.FindIndex(p => p.Id == id));

    private static VictoryCondition V(string id, string condition, string? side = null, string? message = null, params string[] phaseIds) =>
        new() { Id = id, Condition = condition, SideId = side, Message = message, PhaseIds = phaseIds.Length == 0 ? null : phaseIds.ToList() };

    private static GameState At(RulesPack pk, PhasePosition pos)
    {
        var s = new GameState { GameId = "g1" };
        s.Apply(new AdvancePhase(null, pos, "", pk.Phases[pos.PhaseIndex].Name));
        return s;
    }

    private static void AddPiece(GameState s, string id, string name, string map = "Main Map") =>
        s.Apply(new AddPiece(id, PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;" + name }), "", map, new PointD(50, 50), null));

    // ---- game over state ----

    [Fact]
    public void EndGame_sets_state_and_logs()
    {
        var pk = Pack();
        var s = At(pk, P(pk, 8, 1, "E"));
        s.Apply(new EndGame("The Northern League holds the river", new[] { "north" }, "v-river", Who: "Ann"));
        Assert.NotNull(s.GameOver);
        Assert.Equal("The Northern League holds the river", s.GameOver!.Text);
        Assert.Equal(new[] { "north" }, s.GameOver.Winners);
        Assert.Equal("v-river", s.GameOver.VictoryId);
        Assert.Equal(P(pk, 8, 1, "E"), s.GameOver.At);
        Assert.Equal(P(pk, 8, 1, "E"), s.Position);   // the position stays
        var last = s.Log[^1];
        Assert.Equal(LogKind.Turn, last.Kind);
        Assert.Equal("Game over: The Northern League holds the river", last.Text);
        Assert.Equal("Ann", last.Who);
    }

    [Fact]
    public void EndGame_logs_skipped_open_runs_and_clears_pending()
    {
        var pk = Pack();
        var s = At(pk, P(pk, 8, 1, "E"));
        s.Apply(new QueueRuns(new[]
        {
            new PendingRun("r1", "f-fuel", "Fuel Use", "start", new[]
            {
                new PendingUnit("a", "North barge 1", null, false), new PendingUnit("b", "North barge 2", null, true),
            }, false),
        }));
        s.Apply(new SetStartCursor(new StartCursor(P(pk, 8, 1, "E"), 1)));
        var viaAdvance = s.DeepClone();
        var before = s.Log.Count;

        s.Apply(new EndGame("the last turn has been played", null, null));
        viaAdvance.Apply(new AdvancePhase(P(pk, 8, 1, "E"), P(pk, 9, 1, "W"), "End", "Weather"));

        Assert.Empty(s.Pending);
        Assert.Null(s.StartCursor);
        var lines = s.Log.Skip(before).Select(l => (l.Kind, l.Text)).ToList();
        Assert.Equal(new[]
        {
            (LogKind.Calc, "skipped: Fuel Use, North barge 1"),
            (LogKind.Calc, "Fuel Use: 2 units, 1 applied"),
            (LogKind.Turn, "Game over: the last turn has been played"),
        }, lines);
        // The same skipped and summary lines as Next Phase writes (one shared helper).
        Assert.Equal(lines.Take(2), viaAdvance.Log.Skip(before).Take(2).Select(l => (l.Kind, l.Text)));
    }

    [Fact]
    public void EndGame_omitted_when_null()
    {
        var pk = Pack();
        var s = At(pk, P(pk, 2, 1, "NS"));
        var json = JsonSerializer.Serialize(s, QuartermasterJson.Options);
        Assert.DoesNotContain("gameOver", json);
        s.Apply(new EndGame("the game has ended", null, null));
        Assert.Contains("\"gameOver\":", JsonSerializer.Serialize(s, QuartermasterJson.Options));
    }

    [Fact]
    public void EndGame_round_trips()
    {
        var pk = Pack();
        GameCommand c = new EndGame("Northern League wins", new[] { "north" }, "v-river");
        var json = JsonSerializer.Serialize(c, QuartermasterJson.Options);
        Assert.Contains("\"$t\":\"endGame\"", json);
        var back = Assert.IsType<EndGame>(JsonSerializer.Deserialize<GameCommand>(json, QuartermasterJson.Options));
        Assert.Equal("Northern League wins", back.Text);
        Assert.Equal(new[] { "north" }, back.Winners);
        Assert.Equal("v-river", back.VictoryId);

        var s = At(pk, P(pk, 3, 2, "SC"));
        s.Apply(back);
        var clone = s.DeepClone();
        Assert.Equal(s.GameOver!.Text, clone.GameOver!.Text);
        Assert.Equal(s.GameOver.Winners, clone.GameOver.Winners);
        Assert.Equal(s.GameOver.VictoryId, clone.GameOver.VictoryId);
        Assert.Equal(s.GameOver.At, clone.GameOver.At);
        Assert.Equal(JsonSerializer.Serialize(s, QuartermasterJson.Options), JsonSerializer.Serialize(clone, QuartermasterJson.Options));
    }

    [Fact]
    public void PlayerView_keeps_game_over()
    {
        var pk = Pack();
        var s = At(pk, P(pk, 8, 1, "E"));
        AddPiece(s, "s1", "South barge 1");
        s.Apply(new MarkScheduled("sched-1", "Schedule: Arrivals, turn 3"));
        s.Apply(new EndGame("Northern League wins", new[] { "north" }, "v-river"));
        var view = PlayerView.For(s, pk, Model, new HashSet<string> { "north" });
        Assert.False(view.IsPlayerView);   // B7 #73: a game that is over hides nothing
        Assert.Equal("Northern League wins", view.GameOver!.Text);
        Assert.Equal(new[] { "north" }, view.GameOver.Winners);
        Assert.Equal(new[] { "sched-1" }, view.ScheduleDone);
    }

    [Fact]
    public void Lockdown_keeps_schedule_done()
    {
        var pk = Pack();
        var s = At(pk, P(pk, 8, 1, "E"));
        s.Apply(new MarkScheduled("sched-1", "Schedule: Arrivals, turn 3"));
        PlayerView.FailForTest = true;
        try
        {
            var view = PlayerView.For(s, pk, Model, new HashSet<string> { "north" });
            Assert.Equal(PlayerView.FilterErrorText, Assert.Single(view.Log).Text);
            Assert.Equal(new[] { "sched-1" }, view.ScheduleDone);
        }
        finally { PlayerView.FailForTest = false; }
    }

    [Fact]
    public void Game_over_view_never_filters()
    {
        var pk = Pack();
        var s = At(pk, P(pk, 8, 1, "E"));
        s.Apply(new EndGame("Northern League wins", new[] { "north" }, "v-river"));
        PlayerView.FailForTest = true;   // B7 #73: the filter (and its lockdown) never runs once the game is over
        try
        {
            var view = PlayerView.For(s, pk, Model, new HashSet<string> { "north" });
            Assert.False(view.IsPlayerView);
            Assert.Equal(s.Log.Count, view.Log.Count);
            Assert.Equal("Northern League wins", view.GameOver!.Text);
        }
        finally { PlayerView.FailForTest = false; }
    }

    // ---- last turn ----

    [Fact]
    public void Reached_after_last_turn()
    {
        var pk = Pack(lastTurn: 8);
        Assert.True(GameEnd.Reached(pk, P(pk, 8, 1, "E"), P(pk, 9, 1, "W")));
        Assert.False(GameEnd.Reached(pk, P(pk, 7, 1, "E"), P(pk, 8, 1, "W")));
        Assert.False(GameEnd.Reached(pk, P(pk, 8, 1, "W"), P(pk, 8, 1, "NS")));
    }

    [Fact]
    public void Not_reached_without_last_turn()
    {
        var pk = Pack();
        Assert.False(GameEnd.Reached(pk, P(pk, 99, 1, "E"), P(pk, 100, 1, "W")));
        var stageOnly = Pack(lastStage: 2);   // a last stage needs a last turn (validator): ignored alone
        Assert.False(GameEnd.Reached(stageOnly, P(stageOnly, 8, 2, "SC"), P(stageOnly, 8, 3, "NM")));
    }

    [Fact]
    public void Reached_at_end_of_last_stage()
    {
        var pk = Pack(dayCount: 3, lastTurn: 8, lastStage: 2);
        Assert.True(GameEnd.Reached(pk, P(pk, 8, 2, "SC"), P(pk, 8, 3, "NM")));
        var two = Pack(dayCount: 2, lastTurn: 8, lastStage: 2);
        Assert.True(GameEnd.Reached(two, P(two, 8, 2, "SC"), P(two, 8, 1, "E")));
        // Past the last turn still ends it.
        Assert.True(GameEnd.Reached(pk, P(pk, 8, 1, "E"), P(pk, 9, 1, "W")));
    }

    [Fact]
    public void Not_reached_inside_last_stage()
    {
        var pk = Pack(dayCount: 3, lastTurn: 8, lastStage: 2);
        Assert.False(GameEnd.Reached(pk, P(pk, 8, 2, "NM"), P(pk, 8, 2, "SC")));   // same repeat of the block
        Assert.False(GameEnd.Reached(pk, P(pk, 8, 1, "SC"), P(pk, 8, 2, "NM")));   // stage 1 ends
        Assert.False(GameEnd.Reached(pk, P(pk, 7, 2, "SC"), P(pk, 7, 3, "NM")));   // an earlier turn
        Assert.False(GameEnd.Reached(pk, P(pk, 8, 1, "NS"), P(pk, 8, 1, "NM")));   // outside the block
    }

    // ---- victory ----

    [Fact]
    public void Check_first_true_in_order()
    {
        var pk = Pack(victory: new[] { V("v1", "1 == 2"), V("v2", "1 == 1", "north"), V("v3", "2 == 2", "south") });
        var s = At(pk, P(pk, 3, 1, "NS"));
        Assert.Equal("v2", GameEnd.Check(pk, s, s.Position!, "NS", Model, null)?.Id);
        var none = Pack(victory: V("v1", "1 == 2"));
        Assert.Null(GameEnd.Check(none, s, s.Position!, "NS", Model, null));
        Assert.Null(GameEnd.Check(Pack(), s, s.Position!, "NS", Model, null));
    }

    [Fact]
    public void Check_respects_phase_ids()
    {
        var pk = Pack(victory: new[] { V("v1", "1 == 1", "north", null, "NS"), V("v2", "1 == 1", "south") });
        var s = At(pk, P(pk, 3, 1, "NS"));
        Assert.Equal("v1", GameEnd.Check(pk, s, s.Position!, "NS", Model, null)?.Id);
        Assert.Equal("v2", GameEnd.Check(pk, s, P(pk, 3, 1, "E"), "E", Model, null)?.Id);
        // No phase (the last-turn check): every condition, ignoring phase ids.
        Assert.Equal("v1", GameEnd.Check(pk, s, P(pk, 3, 1, "E"), null, Model, null)?.Id);
    }

    [Fact]
    public void Check_error_counts_not_met_and_warns()
    {
        var pk = Pack(victory: new[] { V("v1", "Unknown > 1", "north"), V("v2", "1 +", "north"), V("v3", "1 == 1", "south") });
        var s = At(pk, P(pk, 3, 1, "NS"));
        var warnings = new List<string>();
        Assert.Equal("v3", GameEnd.Check(pk, s, s.Position!, "NS", Model, warnings.Add)?.Id);
        Assert.Equal(2, warnings.Count);
        Assert.Equal("Victory condition 1 failed: Unknown property 'Unknown'; it counts as not met", warnings[0]);
        Assert.StartsWith("Victory condition 2 failed: ", warnings[1]);
        Assert.EndsWith("; it counts as not met", warnings[1]);
        // Without a warn callback an error still counts as not met.
        Assert.Null(GameEnd.Check(Pack(victory: V("v1", "Unknown > 1")), s, s.Position!, "NS", Model, null));
    }

    [Fact]
    public void Check_reads_globals_and_whole_game_aggregates()
    {
        var pk = Pack(victory: V("v-river", "Ports >= 4 and countof(\"startsWith(name(), \\\"North\\\")\") >= 2 and turn() == 8 and stage() == 2",
            "north", "The Northern League holds the river"));
        var s = At(pk, P(pk, 8, 2, "NM"));
        AddPiece(s, "n1", "North barge 1");
        AddPiece(s, "n2", "North barge 2");
        Assert.Null(GameEnd.Check(pk, s, s.Position!, "NM", Model, null));   // Ports is 0
        s.Apply(new LedgerDelta(LedgerKey.Holder(HolderDef.GlobalId), "ports", 0, 4, "manual", null, null, null, null));
        Assert.Equal("v-river", GameEnd.Check(pk, s, s.Position!, "NM", Model, null)?.Id);
        Assert.Null(GameEnd.Check(pk, s, P(pk, 8, 1, "NM"), "NM", Model, null));   // stage() reads the position given
    }

    [Fact]
    public void Result_text_message_side_default()
    {
        var pk = Pack();
        Assert.Equal("The Northern League holds the river", GameEnd.ResultText(pk, V("v1", "1 == 1", "north", "The Northern League holds the river")));
        Assert.Equal("Northern League wins", GameEnd.ResultText(pk, V("v1", "1 == 1", "north", "  ")));
        Assert.Equal("Southern Compact wins", GameEnd.ResultText(pk, V("v1", "1 == 1", "south")));
        Assert.Equal("the game has ended", GameEnd.ResultText(pk, V("v1", "1 == 1")));
        Assert.Equal("the last turn has been played", GameEnd.ResultText(pk, null));
        Assert.Equal("the last turn has been played", GameEnd.LastTurnText);
    }
}
