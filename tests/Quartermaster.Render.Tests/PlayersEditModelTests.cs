using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

public class PlayersEditModelTests
{
    private static RulesPack Pack(params string[] sides) => new()
    {
        Sides = sides.Select(s => new SideDef { Id = s.ToLowerInvariant(), Name = s }).ToList(),
    };

    private static RulesPack Three() => Pack("Red", "Blue", "Green");

    private static PlayersEditModel TwoPlayers(RulesPack pack)
    {
        var m = new PlayersEditModel(pack, Array.Empty<PlayerDef>());
        m.FillTwoPlayersForTests();
        return m;
    }

    [Fact]
    public void Starts_with_one_player_holding_every_side()
    {
        var m = new PlayersEditModel(Three(), Array.Empty<PlayerDef>());
        var row = Assert.Single(m.Rows);
        Assert.Equal("Player 1", row.Name);
        Assert.Equal("p1", row.Id);
        Assert.All(row.Held, h => Assert.True(h));
        Assert.Equal(3, row.Held.Length);
        Assert.Empty(m.Result);
    }

    [Fact]
    public void Ticking_a_side_takes_it_from_others()
    {
        var m = new PlayersEditModel(Three(), Array.Empty<PlayerDef>());
        var b = m.Add();
        m.SetSide(b, 0, true);
        Assert.True(b.Held[0]);
        Assert.False(m.Rows[0].Held[0]);
        Assert.True(m.Rows[0].Held[1]);
    }

    [Fact]
    public void Remove_disabled_with_one_row()
    {
        var m = new PlayersEditModel(Three(), Array.Empty<PlayerDef>());
        Assert.False(m.CanRemove);
        m.Remove(m.Rows[0]);
        Assert.Single(m.Rows);
        m.Add();
        Assert.True(m.CanRemove);
    }

    [Fact]
    public void Add_disabled_at_MaxPlayers()
    {
        var m = new PlayersEditModel(Three(), Array.Empty<PlayerDef>());
        while (m.Rows.Count < Players.MaxPlayers) { Assert.True(m.CanAdd); m.Add(); }
        Assert.False(m.CanAdd);
    }

    [Fact]
    public void Removed_id_not_reused()
    {
        var m = new PlayersEditModel(Three(), Array.Empty<PlayerDef>());
        var p2 = m.Add();
        Assert.Equal("p2", p2.Id);
        m.Remove(p2);
        Assert.Equal("p3", m.Add().Id);
    }

    [Fact]
    public void Result_empty_for_one_player() =>
        Assert.Empty(new PlayersEditModel(Three(), Array.Empty<PlayerDef>()).Result);

    [Fact]
    public void Result_trims_names()
    {
        var m = TwoPlayers(Three());
        m.SetName(m.Rows[0], "  Anna ");
        Assert.Equal("Anna", m.Result[0].Name);
    }

    [Fact]
    public void Problem_null_for_valid_two_players()
    {
        var m = TwoPlayers(Three());
        m.SetName(m.Rows[0], "Anna");
        m.SetName(m.Rows[1], "Ben");
        Assert.Null(m.Problem);
        Assert.Equal(new[] { "red", "green" }, m.Result[0].Sides);
        Assert.Equal(new[] { "blue" }, m.Result[1].Sides);
    }

    [Fact]
    public void Problem_from_Players_Validate()
    {
        var pack = Three();
        var m = TwoPlayers(pack);
        m.SetSide(m.Rows[0], 0, false);
        Assert.Equal(Players.Validate(m.Result, pack), m.Problem);
        Assert.Equal("Red is not held by any player.", m.Problem);
    }

    [Fact]
    public void Problem_without_pack()
    {
        var m = new PlayersEditModel(null, new[]
        {
            new PlayerDef("p1", "Anna", new[] { "red" }), new PlayerDef("p2", "Ben", new[] { "blue" }),
        });
        Assert.Equal("Open a rules pack to set up players.", m.Problem);
    }

    [Fact]
    public void Hint_without_pack()
    {
        var m = new PlayersEditModel(null, Array.Empty<PlayerDef>());
        Assert.Equal("Open a rules pack to set up players.", m.Hint);
        Assert.False(m.CanAdd);
        Assert.False(m.CanHaveSeveral);
    }

    [Fact]
    public void Hint_with_one_side()
    {
        var m = new PlayersEditModel(Pack("Red"), Array.Empty<PlayerDef>());
        Assert.Equal("The rules pack has fewer than two sides: one player plays everything.", m.Hint);
        Assert.False(m.CanAdd);
    }

    [Fact]
    public void Hint_with_sides()
    {
        var m = new PlayersEditModel(Three(), Array.Empty<PlayerDef>());
        Assert.Equal($"Each side is held by exactly one player (at most {Players.MaxPlayers} players).", m.Hint);
        Assert.True(m.CanAdd);
    }

    [Fact]
    public void Changed_fires_on_edits()
    {
        var m = new PlayersEditModel(Three(), Array.Empty<PlayerDef>());
        var n = 0;
        m.Changed += () => n++;
        var r = m.Add();
        m.SetName(r, "Ben");
        m.SetSide(r, 0, true);
        m.Remove(r);
        Assert.Equal(4, n);
    }

    [Fact]
    public void Same_ignores_solo_lists_and_respects_order()
    {
        var a = new PlayerDef("p1", "Anna", new[] { "red" });
        var b = new PlayerDef("p2", "Ben", new[] { "blue" });
        Assert.True(PlayersEditModel.Same(Array.Empty<PlayerDef>(), new[] { a }));
        Assert.True(PlayersEditModel.Same(new[] { a, b }, new[] { a, b }));
        Assert.False(PlayersEditModel.Same(new[] { a, b }, new[] { b, a }));
        Assert.False(PlayersEditModel.Same(new[] { a, b }, Array.Empty<PlayerDef>()));
    }
}
