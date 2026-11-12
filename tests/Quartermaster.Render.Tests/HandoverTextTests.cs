using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

public class HandoverTextTests
{
    private static RulesPack Pack() => new()
    {
        Sides = new() { new SideDef { Id = "red", Name = "Red" }, new SideDef { Id = "blue", Name = "Blue" }, new SideDef { Id = "green", Name = " " } },
    };

    [Fact]
    public void MessageFor_formats() =>
        Assert.Equal("Pass to Anna (Red)", HandoverText.MessageFor(new PlayerDef("p1", "Anna", new[] { "red" }), "Red"));

    [Fact]
    public void SidesText_uses_names_in_player_order_and_ids_for_unknown()
    {
        var p = new PlayerDef("p1", "Anna", new[] { "blue", "red", "green", "zzz" });
        Assert.Equal("Blue, Red, green, zzz", HandoverText.SidesText(p, Pack()));
        Assert.Equal("blue, red", HandoverText.SidesText(new PlayerDef("p1", "Anna", new[] { "blue", "red" }), null));
    }

    [Fact]
    public void StatusText_dash_without_player()
    {
        Assert.Equal("Player: —", HandoverText.StatusText(null));
        Assert.Equal("Player: Ben", HandoverText.StatusText(new PlayerDef("p2", "Ben", new[] { "blue" })));
    }

    [Fact]
    public void MenuText_formats() =>
        Assert.Equal("Ben (Blue)", HandoverText.MenuText(new PlayerDef("p2", "Ben", new[] { "blue" }), Pack()));

    [Fact]
    public void Constants_are_verbatim()
    {
        Assert.Equal("(no players)", HandoverText.NoPlayers);
        Assert.Equal("Who is at the screen. Choose a player to hand over.", HandoverText.PlayerTip);
    }
}
