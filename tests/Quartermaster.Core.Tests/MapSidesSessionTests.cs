using System.Text.Json;
using Quartermaster.Core;

namespace Quartermaster.Core.Tests;

/// <summary>B8 #8: <see cref="GameSession.MapSides"/> translates a game's genesis, command log and redo stack from former side ids.</summary>
public class MapSidesSessionTests
{
    static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string> { ["league"] = "north", ["union"] = "north" };
    static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    static string Json(object o) => JsonSerializer.Serialize(o, QuartermasterJson.Options);

    static GameState Start(params PlayerDef[] players)
    {
        var s = new GameState { GameId = "g1" };
        s.MapOrder.Add("m1");
        s.Players = players.ToList();
        return s;
    }

    static GameState Replay(GameSession g)
    {
        var r = g.Genesis.DeepClone();
        foreach (var c in g.Log) r.Apply(c);
        return r;
    }

    /// <summary>A game started with Ann holding <c>league</c>, then a turn change (a checkpoint), a ticked item and a log line for league.</summary>
    static GameSession LeagueGame()
    {
        var g = new GameSession(Start(new PlayerDef("p1", "Ann", ["league"]), new PlayerDef("p2", "Bo", ["south"])), "mod");
        g.Execute(new AddLogLine(LogKind.System, "Toll paid", At: At, SideId: "league"));
        g.Execute(new SetTurn(2, null, At));
        g.Execute(new TickChecklist(0, true, "Pay toll", At) { SideId = "league" });
        g.Execute(new AddLogLine(LogKind.System, "Bridge held", At: At, SideId: "league"));
        return g;
    }

    [Fact]
    public void MapSides_false_and_untouched_without_use()
    {
        var g = new GameSession(Start(new PlayerDef("p1", "Ann", ["north"])), "mod");
        g.Execute(new AddLogLine(LogKind.System, "league", At: At, SideId: "north"));
        g.Execute(new TickChecklist(0, true, "x", At) { SideId = "south" });
        g.Undo();
        var state = g.State; var genesis = g.Genesis; var log = g.Log.ToList(); var redo = g.RedoActions.SelectMany(a => a.Commands).ToList();
        var before = Json(state) + Json(genesis) + Json(log);
        Assert.False(g.MapSides(Map));
        Assert.Same(state, g.State);
        Assert.Same(genesis, g.Genesis);
        Assert.Equal(log.Count, g.Log.Count);
        for (int i = 0; i < log.Count; i++) Assert.Same(log[i], g.Log[i]);
        Assert.Equal(redo, g.RedoActions.SelectMany(a => a.Commands));
        Assert.Equal(before, Json(g.State) + Json(g.Genesis) + Json(g.Log.ToList()));
    }

    [Fact]
    public void MapSides_translates_genesis_log_and_redo()
    {
        var g = LeagueGame();
        g.Undo();   // the Bridge held line goes to the redo stack
        int raised = 0; g.Changed += _ => raised++; g.Rewound += () => raised++;
        int actions = g.Actions.Count, checkpoints = g.Checkpoints.Count;
        Assert.True(g.MapSides(Map));
        Assert.Equal(0, raised);
        Assert.Equal(actions, g.Actions.Count);
        Assert.Equal(checkpoints, g.Checkpoints.Count);
        Assert.Equal(["north"], g.Genesis.Players[0].Sides);
        Assert.Equal("north", ((AddLogLine)g.Log[0]).SideId);
        Assert.Equal("north", ((TickChecklist)g.Log[2]).SideId);
        Assert.Equal("north", ((AddLogLine)Assert.Single(Assert.Single(g.RedoActions).Commands)).SideId);
        Assert.Equal(["north"], g.State.Players[0].Sides);
        Assert.Equal(["south"], g.State.Players[1].Sides);
        Assert.All(g.State.Log.Where(l => l.SideId != null), l => Assert.Equal("north", l.SideId));
        Assert.Equal("Toll paid", g.State.Log[0].Text);
        Assert.False(g.MapSides(Map));
    }

    [Fact]
    public void Mapped_state_equals_replay()
    {
        var g = LeagueGame();
        g.Execute(new SetPlayers([new PlayerDef("p1", "Ann", ["league", "south"])], At));
        g.Execute(new EndGame("Northern League wins", ["league"], "v1", At));
        Assert.True(g.MapSides(Map));
        Assert.Equal(Json(Replay(g)), Json(g.State));
        Assert.Equal(["north"], g.State.GameOver!.Winners!);
    }

    [Fact]
    public void Mapped_players_line_names_listing_ids()
    {
        var g = new GameSession(Start(), "mod");
        g.Execute(new SetPlayers([new PlayerDef("p1", "Ann", ["league"]), new PlayerDef("p2", "Bo", ["south"])], At));
        Assert.Contains(g.State.Log, l => l.Text == "Players: Ann (league); Bo (south)");
        Assert.True(g.MapSides(Map));
        Assert.Contains(g.State.Log, l => l.Text == "Players: Ann (north); Bo (south)");
        Assert.DoesNotContain(g.State.Log, l => l.Text.Contains("league"));
    }

    [Fact]
    public void Undo_after_mapping_keeps_listing_ids()
    {
        var g = LeagueGame();
        Assert.True(g.MapSides(Map));
        g.Undo();
        Assert.Equal(["north"], g.State.Players[0].Sides);
        Assert.Equal("north", g.State.Log[0].SideId);
        Assert.Equal(3, g.Log.Count);   // one undo still takes back one action
        Assert.Equal("north", ((AddLogLine)Assert.Single(Assert.Single(g.RedoActions).Commands)).SideId);
        Assert.Equal(Json(Replay(g)), Json(g.State));
    }

    [Fact]
    public void Redo_after_mapping_keeps_listing_ids()
    {
        var g = LeagueGame();
        g.Undo();
        g.Undo();
        Assert.True(g.MapSides(Map));
        g.Redo();
        g.Redo();
        Assert.Equal(4, g.Log.Count);
        Assert.Equal("north", ((TickChecklist)g.Log[2]).SideId);
        Assert.Equal("north", ((AddLogLine)g.Log[3]).SideId);
        Assert.Equal("north", g.State.Log[^1].SideId);
        Assert.Equal(Json(Replay(g)), Json(g.State));
    }

    [Fact]
    public void Step_back_after_mapping_keeps_listing_ids()
    {
        var g = LeagueGame();
        var cp = g.Checkpoints[^1];   // after SetTurn: a cached snapshot made before the mapping
        Assert.Equal(2, cp.LogIndex);
        Assert.True(g.MapSides(Map));
        g.RewindTo(cp.LogIndex);
        Assert.Equal(["north"], g.State.Players[0].Sides);
        Assert.Equal("north", g.State.Log[0].SideId);
        Assert.Equal(Json(Replay(g)), Json(g.State));
        Assert.Equal(Json(g.StateAt(cp.LogIndex)), Json(g.State));
    }

    [Fact]
    public void Mapped_session_save_replays_without_rebase()
    {
        var g = LeagueGame();
        g.Execute(new SetPlayers([new PlayerDef("p1", "Ann", ["league"]), new PlayerDef("p2", "Bo", ["south"])], At));
        Assert.True(g.MapSides(Map));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            NativeSave.Write(path, g);
            var back = NativeSave.Read(path);
            Assert.False(back.RebasedOnLoad);
            Assert.Null(back.LoadNotice);
            Assert.Equal(["north"], back.State.Players[0].Sides);
            Assert.Equal(["north"], back.Genesis.Players[0].Sides);
            Assert.Equal("north", ((AddLogLine)back.Log[0]).SideId);
            Assert.Equal(Json(g.State), Json(back.State));
            Assert.False(back.MapSides(Map));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Two_former_ids_one_player_gets_side_once()
    {
        var g = new GameSession(Start(new PlayerDef("p1", "Ann", ["league", "union", "south"])), "mod");
        g.Execute(new SetPlayers([new PlayerDef("p1", "Ann", ["union", "league"])], At));
        g.Undo();
        Assert.True(g.MapSides(Map));
        Assert.Equal(["north", "south"], g.State.Players[0].Sides);
        Assert.Equal(["north", "south"], g.Genesis.Players[0].Sides);
        g.Redo();
        Assert.Equal(["north"], g.State.Players[0].Sides);
        Assert.Contains(g.State.Log, l => l.Text == "Players: Ann (north)");
    }

    [Fact]
    public void Two_players_former_ids_both_hold_side()
    {
        var g = new GameSession(Start(new PlayerDef("p1", "Ann", ["league"]), new PlayerDef("p2", "Bo", ["union", "hills"])), "mod");
        g.Execute(new AddLogLine(LogKind.System, "x", At: At, SideId: "union"));
        Assert.True(g.MapSides(Map));
        Assert.Equal(["north"], g.State.Players[0].Sides);
        Assert.Equal(["north", "hills"], g.State.Players[1].Sides);
        Assert.Equal("north", g.State.Log[^1].SideId);
    }
}
