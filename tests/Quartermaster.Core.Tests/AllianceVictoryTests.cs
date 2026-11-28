using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Core.Tests;

/// <summary>
/// B10 Task 3: a met victory condition of a side in an alliance makes the whole alliance win (condition side first, then its allies in pack
/// order); a blank message reads <c>{alliance} wins</c>. Without alliances: one winner, <c>{side name} wins</c>. Invented Riverland pack of
/// <see cref="AllianceScopeTests"/> (Northern Foot, Southern Compact, Northern Wagons, Northern Balloons; the Northern Pact).
/// </summary>
public class AllianceVictoryTests
{
    private static VictoryCondition V(string side, string? message = null) => new() { Id = "v-" + side, Condition = "1 == 1", SideId = side, Message = message };

    private static string Json(GameCommand c) => JsonSerializer.Serialize(c, typeof(GameCommand), QuartermasterJson.Options);

    [Fact]
    public void Victory_winners_include_allies_in_pack_order()
    {
        var pk = AllianceScopeTests.Pack();
        Assert.Equal(new[] { "north-supply", "north-land", "north-air" }, GameEnd.Winners(pk, V("north-supply")));
        Assert.Equal(new[] { "north-air", "north-land", "north-supply" }, GameEnd.Winners(pk, V("north-air")));
        Assert.Equal(new[] { "south" }, GameEnd.Winners(pk, V("south")));   // no alliance of its own
        // An alliance of one side only: no allies, one winner.
        Assert.Equal(new[] { "north-air" }, GameEnd.Winners(AllianceScopeTests.Pack(alliance: false, loneAir: true), V("north-air")));
    }

    [Fact]
    public void Victory_without_alliance_single_winner()
    {
        var pk = AllianceScopeTests.Pack(alliance: false);
        Assert.Equal(new[] { "south" }, GameEnd.Winners(pk, V("south")));
        Assert.Equal(new[] { "north-supply" }, GameEnd.Winners(pk, V("north-supply")));
        Assert.Null(GameEnd.Winners(pk, new VictoryCondition { Id = "v", Condition = "1 == 1" }));
        Assert.Null(GameEnd.Winners(pk, new VictoryCondition { Id = "v", Condition = "1 == 1", SideId = "  " }));
        Assert.Null(GameEnd.Winners(pk, null));
        // The command is byte-equal to the one issued before alliance winners.
        var v = V("south");
        var before = new EndGame(GameEnd.ResultText(pk, v), new[] { v.SideId! }, v.Id);
        var now = new EndGame(GameEnd.ResultText(pk, v), GameEnd.Winners(pk, v), v.Id);
        Assert.Equal(Json(before), Json(now));
        Assert.Contains("\"winners\":[\"south\"]", Json(now));
    }

    [Fact]
    public void Result_text_default_names_alliance()
    {
        var pk = AllianceScopeTests.Pack();
        Assert.Equal("Northern Pact wins", GameEnd.ResultText(pk, V("north-land")));
        Assert.Equal("northern pact wins", GameEnd.ResultText(pk, V("north-supply", "  ")));   // as typed on the condition side, trimmed
        Assert.Equal("The Pact holds the ford", GameEnd.ResultText(pk, V("north-air", " The Pact holds the ford ")));
        Assert.Equal("Southern Compact wins", GameEnd.ResultText(pk, V("south")));
        Assert.Equal("Northern Balloons wins", GameEnd.ResultText(AllianceScopeTests.Pack(alliance: false, loneAir: true), V("north-air")));
    }

    [Fact]
    public void Result_text_default_without_alliance_unchanged()
    {
        var pk = AllianceScopeTests.Pack(alliance: false);
        Assert.Equal("Northern Wagons wins", GameEnd.ResultText(pk, V("north-supply")));
        Assert.Equal("Southern Compact wins", GameEnd.ResultText(pk, V("south")));
        Assert.Equal("the game has ended", GameEnd.ResultText(pk, new VictoryCondition { Id = "v", Condition = "1 == 1" }));
        Assert.Equal(GameEnd.LastTurnText, GameEnd.ResultText(pk, null));
    }

    [Fact]
    public void Former_ids_still_map_winners()
    {
        var pk = AllianceScopeTests.Pack();
        pk = pk with { Sides = pk.Sides.Select(s => s.Id == "north-supply" ? s with { FormerIds = new() { "wagons" } } : s).ToList() };
        var map = SideFacts.FormerMap(pk)!;
        var e = (EndGame)FormerSides.Apply(new EndGame("Northern Pact wins", new[] { "wagons", "north-land", "north-air" }, "v1"), map);
        Assert.Equal(new[] { "north-supply", "north-land", "north-air" }, e.Winners);
        var s = new GameState { GameOver = new GameOverState("Northern Pact wins", new[] { "north-land", "wagons", "north-air" }, "v1", null) };
        Assert.True(FormerSides.Apply(s, map));
        Assert.Equal(new[] { "north-land", "north-supply", "north-air" }, s.GameOver!.Winners);
        // Winners already current are left as they are.
        var current = new EndGame("Northern Pact wins", GameEnd.Winners(pk, V("north-supply")), "v1");
        Assert.Same(current, FormerSides.Apply(current, map));
    }
}
