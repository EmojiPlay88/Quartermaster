using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

public class VisibilityEditTests
{
    private static RulesPack Pack() => new()
    {
        Properties = new() { new PropertyDef { Id = "p1", Name = "Fuel", Visibility = Visibility.Private } },
        Holders = new() { new HolderDef { Id = "h1", Name = "Depot" } },
        Formulas = new() { new FormulaDef { Id = "f1", Name = "Attack" } },
        Sides = new() { new SideDef { Id = "red", Name = "Red" }, new SideDef { Id = "blue", Name = "Blue" } },
    };

    [Fact]
    public void SetPropertyVisibility_Changes_AndNoOpKeepsReference()
    {
        var p = Pack();
        var q = VisibilityEdit.SetPropertyVisibility(p, "p1", Visibility.Public);
        Assert.Equal(Visibility.Public, q.Properties[0].Visibility);
        Assert.Same(q, VisibilityEdit.SetPropertyVisibility(q, "p1", Visibility.Public));
        Assert.Same(p, VisibilityEdit.SetPropertyVisibility(p, "missing", Visibility.Public));
    }

    [Fact]
    public void SetHolderOwner_SetsAndClears()
    {
        var q = VisibilityEdit.SetHolderOwner(Pack(), "h1", "red");
        Assert.Equal("red", q.Holders[0].SideId);
        var r = VisibilityEdit.SetHolderOwner(q, "h1", null);
        Assert.Null(r.Holders[0].SideId);
        Assert.Same(r, VisibilityEdit.SetHolderOwner(r, "h1", ""));
    }

    [Fact]
    public void SetMapOwner_AddsReplacesRemoves()
    {
        var q = VisibilityEdit.SetMapOwner(Pack(), "m1", "red");
        Assert.Single(q.MapOwners);
        var r = VisibilityEdit.SetMapOwner(q, "m1", "blue");
        Assert.Equal("blue", Assert.Single(r.MapOwners).SideId);
        var s = VisibilityEdit.SetMapOwner(r, "m1", null);
        Assert.Empty(s.MapOwners);
        Assert.Same(s, VisibilityEdit.SetMapOwner(s, "m1", null));
    }

    [Fact]
    public void SetPrivateRolls_Toggles()
    {
        var q = VisibilityEdit.SetPrivateRolls(Pack(), "f1", true);
        Assert.True(q.Formulas[0].PrivateRolls);
        Assert.False(VisibilityEdit.SetPrivateRolls(q, "f1", false).Formulas[0].PrivateRolls);
    }

    [Fact]
    public void MapOwnerOf_ReturnsSideOrNull()
    {
        var q = VisibilityEdit.SetMapOwner(Pack(), "m1", "red");
        Assert.Equal("red", VisibilityEdit.MapOwnerOf(q, "m1"));
        Assert.Null(VisibilityEdit.MapOwnerOf(q, "m2"));
    }
}
