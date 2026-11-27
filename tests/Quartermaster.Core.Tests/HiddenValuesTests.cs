using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Core.Tests;

/// <summary>B7: the one visibility rule (<see cref="HiddenValues"/>). Invented Riverland data.</summary>
public class HiddenValuesTests
{
    internal const string North = "north";
    internal const string South = "south";
    internal const string NorthDepot = "h-north-depot";
    internal const string Ferry = "h-ferry";

    /// <summary>Two sides; holder properties Stores (Public) and Reserve (Private); place properties Ford depth (Private) and Ford name (Public);
    /// piece properties Morale (Public) and Fuel (Private); Tolls (Public) and Bids (Private) keyed by turn on holders.</summary>
    internal static RulesPack Pack() => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = North, Name = "Northern League", UnitFilter = "startsWith(name(), \"North\")" },
            new SideDef { Id = South, Name = "Southern Compact", UnitFilter = "startsWith(name(), \"South\")" },
        },
        Properties = new()
        {
            new PropertyDef { Id = "p-stores", Name = "Stores", Scope = PropertyScope.Holders, Visibility = Visibility.Public, Default = 0 },
            new PropertyDef { Id = "p-reserve", Name = "Reserve", Scope = PropertyScope.Holders, Visibility = Visibility.Private, Default = 7 },
            new PropertyDef { Id = "p-ford", Name = "Ford", Scope = PropertyScope.Holders, Visibility = Visibility.Private, KeyedBy = "hex" },
            new PropertyDef { Id = "p-bank", Name = "Bank", Scope = PropertyScope.Holders, Visibility = Visibility.Public, KeyedBy = "hex" },
            new PropertyDef { Id = "p-morale", Name = "Morale", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Visibility = Visibility.Private },
            new PropertyDef { Id = "p-tolls", Name = "Tolls", Scope = PropertyScope.Holders, Visibility = Visibility.Public, KeyedBy = "turn" },
            new PropertyDef { Id = "p-bids", Name = "Bids", Scope = PropertyScope.Holders, Visibility = Visibility.Private, KeyedBy = "turn" },
        },
        Holders = new()
        {
            new HolderDef { Id = NorthDepot, Name = "NorthDepot", SideId = North },
            new HolderDef { Id = Ferry, Name = "Ferry" },
        },
        Phases = new()
        {
            new PhaseDef { Id = "NM", Name = "North Movement", SideId = North },
            new PhaseDef { Id = "SM", Name = "South Movement", SideId = South },
        },
    };

    private static PropertyDef Prop(RulesPack pk, string id) => pk.ById<PropertyDef>(id)!;

    private static Func<string, bool> Holds(params string[] sides) => s => sides.Contains(s);

    [Fact]
    public void SeesEverything_solo_all_sides_and_game_over()
    {
        var pk = Pack();
        var s = new GameState { GameId = "g1" };
        Assert.True(HiddenValues.SeesEverything(s, null, new HashSet<string> { North }));   // no pack
        Assert.True(HiddenValues.SeesEverything(s, pk, null));                              // solo
        Assert.True(HiddenValues.SeesEverything(s, pk, new HashSet<string> { North, South }));
        Assert.False(HiddenValues.SeesEverything(s, pk, new HashSet<string> { North }));
        Assert.False(HiddenValues.SeesEverything(null, pk, new HashSet<string>()));
        s.Apply(new EndGame("The Northern League holds the river", new[] { North }, null));
        Assert.True(HiddenValues.SeesEverything(s, pk, new HashSet<string> { North }));
        Assert.True(HiddenValues.SeesEverything(s, pk, new HashSet<string>()));
    }

    [Fact]
    public void Holder_public_property_of_other_side_visible()
    {
        var pk = Pack();
        Assert.False(HiddenValues.HolderValueHidden(pk, NorthDepot, Prop(pk, "p-stores"), Holds(South)));
        Assert.False(HiddenValues.HolderValueHidden(pk, NorthDepot, Prop(pk, "p-tolls"), Holds()));
    }

    [Fact]
    public void Holder_private_property_of_other_side_hidden()
    {
        var pk = Pack();
        Assert.True(HiddenValues.HolderValueHidden(pk, NorthDepot, Prop(pk, "p-reserve"), Holds(South)));
        Assert.True(HiddenValues.HolderValueHidden(pk, NorthDepot, null, Holds(South)));   // the whole key
        Assert.False(HiddenValues.HolderValueHidden(pk, NorthDepot, Prop(pk, "p-reserve"), Holds(North)));
        Assert.False(HiddenValues.HolderValueHidden(pk, NorthDepot, null, Holds(North)));
    }

    [Fact]
    public void Holder_unknown_hidden_global_visible()
    {
        var pk = Pack();
        Assert.True(HiddenValues.HolderValueHidden(pk, "h-gone", Prop(pk, "p-stores"), Holds(North, South)));
        Assert.False(HiddenValues.HolderValueHidden(pk, HolderDef.GlobalId, Prop(pk, "p-reserve"), Holds()));
        Assert.False(HiddenValues.HolderValueHidden(pk, HolderDef.GlobalId, null, Holds()));
        Assert.False(HiddenValues.HolderValueHidden(pk, Ferry, Prop(pk, "p-reserve"), Holds()));   // a holder of no side
        var dangling = Pack();
        dangling.Holders[0] = dangling.Holders[0] with { SideId = "west" };   // an owner naming a deleted side is ignored (spec 8a.10)
        Assert.False(HiddenValues.HolderValueHidden(dangling, NorthDepot, Prop(dangling, "p-reserve"), Holds()));
    }

    [Fact]
    public void Private_place_hidden_everywhere()
    {
        var pk = Pack();
        var ford = Prop(pk, "p-ford");
        Assert.True(HiddenValues.HolderValueHidden(pk, HolderDef.GlobalId, ford, Holds(North, South)));
        Assert.True(HiddenValues.HolderValueHidden(pk, NorthDepot, ford, Holds(North)));
        Assert.True(HiddenValues.PieceValueHidden(pk, ford, North, Holds(North)));
        Assert.True(HiddenValues.PieceValueHidden(pk, ford, null, Holds()));
        var bank = Prop(pk, "p-bank");   // a Public place: every player
        Assert.False(HiddenValues.HolderValueHidden(pk, HolderDef.GlobalId, bank, Holds()));
        Assert.False(HiddenValues.PieceValueHidden(pk, bank, North, Holds(South)));
    }

    [Theory]
    [InlineData("p-morale", null, "south", false)]   // a piece of no side
    [InlineData("p-fuel", null, "south", false)]
    [InlineData("p-fuel", "north", "north", false)]  // a held side
    [InlineData(null, "north", "north", false)]
    [InlineData("p-morale", "north", "south", false)] // Public of another side
    [InlineData("p-fuel", "north", "south", true)]   // Private of another side
    [InlineData(null, "north", "south", true)]       // the whole key of another side's piece
    [InlineData("p-fuel", "north", "", true)]        // holding nothing
    [InlineData(null, null, "", false)]
    public void Piece_rules_unchanged(string? property, string? pieceSide, string held, bool hidden)
    {
        var pk = Pack();
        var def = property == null ? null : Prop(pk, property);
        var holds = held.Length == 0 ? Holds() : Holds(held);
        Assert.Equal(hidden, HiddenValues.PieceValueHidden(pk, def, pieceSide, holds));
    }

    [Fact]
    public void Piece_computed_public_reading_private_is_hidden()
    {
        var pk = Pack();
        pk.Properties.Add(new PropertyDef { Id = "p-range", Name = "Range", Scope = PropertyScope.Pieces, Visibility = Visibility.Public, Expr = "Fuel * 2" });
        Assert.True(HiddenValues.PieceValueHidden(pk, Prop(pk, "p-range"), North, Holds(South)));
        pk.Properties.Add(new PropertyDef { Id = "p-cap", Name = "Cap", Scope = PropertyScope.Holders, Visibility = Visibility.Public, Expr = "Reserve + 1" });
        Assert.True(HiddenValues.HolderValueHidden(pk, NorthDepot, Prop(pk, "p-cap"), Holds(South)));
    }
}
