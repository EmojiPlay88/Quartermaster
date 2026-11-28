using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>
/// B9 #13a: a holder's owner worked out from the game state (<see cref="HolderOwners"/>, <see cref="Ownership"/>). Invented Riverland data:
/// the Northern League (<c>north</c>) and the Southern Compact (<c>south</c>, former id <c>old-south</c>); a Global Public DumpOwner keyed by
/// table Dumps (Default 1); Ford Dump owned by <c>keyed(DumpOwner, "Ford Dump")</c> (fixed side north), Mill Dump owned by its own Public
/// Owner (Default 2; fixed side south); a Private Stores.
/// </summary>
public class HolderOwnersTests
{
    internal const string Ford = "h-ford";
    internal const string Mill = "h-mill";
    private static ModuleModel Model => RunnerFx.Model;
    private static readonly IReadOnlySet<string> NorthOnly = new HashSet<string> { "north" };
    private static readonly IReadOnlySet<string> SouthOnly = new HashSet<string> { "south" };

    internal static RulesPack Pack() => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"North\")" },
            new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"South\")", FormerIds = new() { "old-south" } },
        },
        Properties = new()
        {
            new PropertyDef { Id = "p-dumpowner", Name = "DumpOwner", Scope = PropertyScope.Holders, Holders = new() { HolderDef.GlobalId },
                KeyedBy = "table:t-dumps", Visibility = Visibility.Public, Default = 1 },
            new PropertyDef { Id = "p-owner", Name = "Owner", Scope = PropertyScope.Holders, Holders = new() { Mill, HolderDef.GlobalId },
                Visibility = Visibility.Public, Default = 2 },
            new PropertyDef { Id = "p-stores", Name = "Stores", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
        },
        Tables = new()
        {
            new TableDef { Id = "t-dumps", Name = "Dumps", Kind = TableKind.OneD, RowKeys = new() { "Ford Dump", "Mill Dump" },
                ColumnKeys = new() { "Value" }, Cells = new() { new() { "1" }, new() { "2" } } },
        },
        Holders = new()
        {
            new HolderDef { Id = Ford, Name = "Ford Dump", SideId = "north", OwnerExpr = "keyed(DumpOwner, \"Ford Dump\")" },
            new HolderDef { Id = Mill, Name = "Mill Dump", SideId = "south", OwnerExpr = "Owner" },
            new HolderDef { Id = "h-north", Name = "North Depot", SideId = "north" },
            new HolderDef { Id = "h-south", Name = "South Depot", SideId = "south" },
        },
    };

    private static RulesPack WithFord(RulesPack pk, string? expr, string? side = "north") =>
        pk with { Holders = pk.Holders.Select(h => h.Id == Ford ? h with { OwnerExpr = expr, SideId = side } : h).ToList() };

    private static RulesPack Fixed(RulesPack pk) => pk with { Holders = pk.Holders.Select(h => h with { OwnerExpr = null }).ToList() };

    internal static GameState NewState()
    {
        var s = new GameState { GameId = "g1" };
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { "north" }), new PlayerDef("p2", "Bob", new[] { "south" }) }));
        return s;
    }

    internal static LedgerDelta DumpOwner(RulesPack pk, double before, double after) =>
        new(LedgerKey.Holder(HolderDef.GlobalId), "p-dumpowner", before, after, "manual", null, null, null, null)
        { ValueKey = KeyedValues.NormaliseKey(pk, null, pk.ById<PropertyDef>("p-dumpowner")!, Value.Of("Ford Dump")) };

    private static LedgerDelta Set(string holder, string prop, double after) =>
        new(LedgerKey.Holder(holder), prop, 0, after, "manual", null, null, null, null);

    private static string? Owner(GameState s, RulesPack pk, string holder = Ford) => Ownership.SideOfHolder(holder, pk, s);

    [Fact]
    public void Owner_by_number_and_zero()
    {
        var pk = Pack();
        var s = NewState();
        Assert.Equal("north", Owner(s, pk));   // the Default 1
        s.Apply(DumpOwner(pk, 1, 2));
        Assert.Equal("south", Owner(s, pk));
        s.Apply(DumpOwner(pk, 2, 0));
        Assert.Null(Owner(s, pk));   // 0: no owner
        Assert.Equal("north", Ownership.SideOfHolder(Ford, pk, null));   // no state: the fixed owner
        Assert.Equal("north", Ownership.SideOfHolder(Ford, pk));   // the 2-argument overload: the fixed owner
    }

    [Fact]
    public void Owner_by_name_and_id_case_insensitive()
    {
        var s = NewState();
        Assert.Equal("south", Owner(s, WithFord(Pack(), "\"  southern COMPACT \"")));
        Assert.Equal("south", Owner(s, WithFord(Pack(), "\"SOUTH\"")));
        Assert.Equal("north", Owner(s, WithFord(Pack(), "\"northern league\"", "south")));
        Assert.Null(Owner(s, WithFord(Pack(), "\"\"")));   // "": no owner
        Assert.Null(Owner(s, WithFord(Pack(), "\"  \"")));
    }

    [Fact]
    public void Owner_former_id_reads_listing_side()
    {
        var s = NewState();
        Assert.Equal("south", Owner(s, WithFord(Pack(), "\"Old-South\"")));
    }

    [Fact]
    public void Owner_error_or_unknown_falls_back_to_fixed_side()
    {
        var s = NewState();
        foreach (var expr in new[] { "\"Hill Clans\"", "3", "1.5", "-1", "Unknownprop", "((", "sum(Stores)", "1 > 0", "keyed(DumpOwner, \"Weir\")" })
            Assert.Equal("south", Owner(s, WithFord(Pack(), expr, "south")));
        Assert.Null(Owner(s, WithFord(Pack(), "\"Hill Clans\"", "hills")));   // a dangling fixed side: no owner, as before
        Assert.Null(Owner(s, WithFord(Pack(), "\"Hill Clans\"", null)));
        var oneSide = Pack() with { Sides = Pack().Sides.Take(1).ToList() };
        Assert.Equal("north", Owner(s, WithFord(oneSide, "keyed(DumpOwner, \"Ford Dump\")", null)));   // DumpOwner 1 of one side
    }

    [Fact]
    public void Owner_reads_own_holder_value_then_global()
    {
        var pk = Pack();
        pk = pk with { Holders = pk.Holders.Append(new HolderDef { Id = "h-weir", Name = "Weir Dump", SideId = "south", OwnerExpr = "Owner" }).ToList() };
        var s = NewState();
        Assert.Equal("south", Owner(s, pk, Mill));   // the Default 2 of its own value
        Assert.Equal("south", Owner(s, pk, "h-weir"));   // the global holder's Default 2
        s.Apply(Set(HolderDef.GlobalId, "p-owner", 1));
        Assert.Equal("south", Owner(s, pk, Mill));   // its own value, not the global one
        Assert.Equal("north", Owner(s, pk, "h-weir"));   // Weir Dump does not keep Owner: the global value
        s.Apply(Set(Mill, "p-owner", 1));
        Assert.Equal("north", Owner(s, pk, Mill));
    }

    /// <summary>Final review I2: Controller is kept by Mill Dump only; North Depot's expression reading it falls back to the fixed owner
    /// (north), not to the Default 0 (no owner), which would make its Private Stores public.</summary>
    [Fact]
    public void Owner_reading_property_kept_by_neither_holder_falls_back_to_fixed_side()
    {
        var pk = Pack();
        pk = pk with
        {
            Properties = pk.Properties.Append(new PropertyDef
                { Id = "p-controller", Name = "Controller", Scope = PropertyScope.Holders, Holders = new() { Mill }, Visibility = Visibility.Public, Default = 0 }).ToList(),
            Holders = pk.Holders.Select(h => h.Id == "h-north" ? h with { OwnerExpr = "Controller" } : h).ToList(),
        };
        var s = NewState();
        Assert.Equal("north", Owner(s, pk, "h-north"));
        s.Apply(Set(HolderDef.GlobalId, "p-controller", 2));   // a global ledger value of a property Global does not keep is not read either
        Assert.Equal("north", Owner(s, pk, "h-north"));
        Assert.True(HiddenValues.HolderValueHidden(s, pk, "h-north", pk.ById<PropertyDef>("p-stores"), id => id == "south"));
    }

    [Fact]
    public void Owner_same_on_full_state_and_view()
    {
        var pk = Pack();
        var s = NewState();
        s.Apply(DumpOwner(pk, 1, 2));
        s.Apply(Set(Mill, "p-owner", 1));
        s.Apply(Set(Ford, "p-stores", 9));
        foreach (var sides in new[] { NorthOnly, SouthOnly, new HashSet<string>() })
        {
            var view = PlayerView.For(s, pk, Model, sides);
            Assert.True(view.IsPlayerView);
            Assert.Equal(Owner(s, pk), Owner(view, pk));
            Assert.Equal(Owner(s, pk, Mill), Owner(view, pk, Mill));
        }
        Assert.Equal("south", Owner(s, pk));
        Assert.Equal("north", Owner(s, pk, Mill));
    }

    [Fact]
    public void No_owner_expr_never_evaluates()
    {
        var pk = Fixed(Pack());
        var s = NewState();
        s.Apply(Set(Ford, "p-stores", -2));
        HolderOwners.Evaluations = 0;
        Assert.False(HolderOwners.HasDynamic(pk));
        PlayerView.For(s, pk, Model, SouthOnly);
        Assert.True(HiddenValues.HolderValueHidden(s, pk, Ford, pk.ById<PropertyDef>("p-stores"), x => x == "south"));
        AlertEngine.Evaluate(s, pk, null, null, null, NorthOnly);
        Ownership.HolderSides(s, pk)(Ford);
        Assert.Equal(0, HolderOwners.Evaluations);
        Assert.True(HolderOwners.HasDynamic(Pack()));
    }

    [Fact]
    public void HolderSides_memoises_per_state()
    {
        var pk = Pack();
        var s = NewState();
        HolderOwners.Evaluations = 0;
        var sides = Ownership.HolderSides(s, pk);
        Assert.Equal("north", sides(Ford));
        Assert.Equal("north", sides(Ford));
        Assert.Equal("south", sides(Mill));
        Assert.Equal("north", sides("h-north"));
        Assert.Null(sides(HolderDef.GlobalId));
        Assert.Null(sides("h-gone"));
        Assert.Equal(2, HolderOwners.Evaluations);   // once per holder with an expression
        s.Apply(DumpOwner(pk, 1, 2));
        Assert.Equal("north", sides(Ford));   // the memo is of the state as it stood
        Assert.Equal("south", Ownership.HolderSides(s, pk)(Ford));   // a new one after the change
        s.Apply(Set(Ford, "p-stores", 4)); s.Apply(Set(Ford, "p-stores", 5)); s.Apply(Set(Mill, "p-stores", 6));
        HolderOwners.Evaluations = 0;
        PlayerView.For(s, pk, Model, NorthOnly);
        Assert.Equal(2, HolderOwners.Evaluations);   // one HolderSides per view: once per holder, whatever the number of values
    }

    [Fact]
    public void View_hides_private_values_by_current_owner()
    {
        var pk = Pack();
        var s = NewState();
        s.Apply(Set(Ford, "p-stores", 9));
        Assert.Equal(9, Ledger.Get(PlayerView.For(s, pk, Model, NorthOnly), LedgerKey.Holder(Ford), "p-stores"));
        Assert.Null(Ledger.Get(PlayerView.For(s, pk, Model, SouthOnly), LedgerKey.Holder(Ford), "p-stores"));
        s.Apply(DumpOwner(pk, 1, 2));
        Assert.Equal(9, Ledger.Get(PlayerView.For(s, pk, Model, SouthOnly), LedgerKey.Holder(Ford), "p-stores"));
        Assert.Null(Ledger.Get(PlayerView.For(s, pk, Model, NorthOnly), LedgerKey.Holder(Ford), "p-stores"));
        var view = PlayerView.For(s, pk, Model, NorthOnly);
        Assert.Equal(2, KeyedValues.Read(view, pk, Model, null, "DumpOwner", Value.Of("Ford Dump"), null, null));   // the owner's input is seen
        var ex = Assert.Throws<EvalError>(() => PieceFilter.HolderRead(view, pk, "Ford Dump", "Stores"));
        Assert.True(ex.MissingValue);   // hidden, not unset
        Assert.Equal(9, PieceFilter.HolderRead(PlayerView.For(s, pk, Model, SouthOnly), pk, "Ford Dump", "Stores").Number);
    }

    [Fact]
    public void Capture_write_flips_visibility_and_undo_flips_back()
    {
        var pk = Pack();
        var g = new GameSession(NewState(), "sha");
        using (g.BeginAction()) g.Execute(Set(Ford, "p-stores", 9));
        bool NorthSees() => Ledger.Get(PlayerView.For(g.State, pk, Model, NorthOnly), LedgerKey.Holder(Ford), "p-stores") == 9;
        bool SouthSees() => Ledger.Get(PlayerView.For(g.State, pk, Model, SouthOnly), LedgerKey.Holder(Ford), "p-stores") == 9;
        Assert.True(NorthSees()); Assert.False(SouthSees());
        using (g.BeginAction()) g.Execute(DumpOwner(pk, 1, 2));
        Assert.False(NorthSees()); Assert.True(SouthSees());
        Assert.DoesNotContain(g.State.Log, l => l.Text.Contains("belongs to", StringComparison.OrdinalIgnoreCase));   // no owner line
        g.Undo();
        Assert.True(NorthSees()); Assert.False(SouthSees());
        Assert.Equal("north", Owner(g.State, pk));
        g.Redo();
        Assert.False(NorthSees()); Assert.True(SouthSees());
        Assert.Equal("south", Owner(g.State, pk));
    }

    [Fact]
    public void Replay_gives_same_owner()
    {
        var pk = Pack();
        var g = new GameSession(NewState(), "sha");
        using (g.BeginAction()) g.Execute(Set(Ford, "p-stores", 9));
        using (g.BeginAction()) g.Execute(DumpOwner(pk, 1, 2));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            NativeSave.Write(path, g);
            var back = NativeSave.Read(path);
            Assert.Equal("south", Owner(back.State, pk));
            Assert.Equal(9, Ledger.Get(PlayerView.For(back.State, pk, Model, SouthOnly), LedgerKey.Holder(Ford), "p-stores"));
            back.Undo();
            Assert.Equal("north", Owner(back.State, pk));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Below_zero_alert_follows_current_owner()
    {
        var pk = Pack();
        var s = NewState();
        s.Apply(Set(Ford, "p-stores", -3));
        bool Listed(IReadOnlySet<string> sides) =>
            AlertEngine.Evaluate(s, pk, null, null, null, sides).Any(h => h.Name == "Stores below zero" && h.Matches.Any(m => m.HolderId == Ford));
        Assert.True(Listed(NorthOnly)); Assert.False(Listed(SouthOnly));
        s.Apply(DumpOwner(pk, 1, 2));
        Assert.False(Listed(NorthOnly)); Assert.True(Listed(SouthOnly));
    }

    [Fact]
    public void Holder_value_hidden_old_overload_unchanged()
    {
        var pk = Pack();
        var s = NewState();
        s.Apply(DumpOwner(pk, 1, 2));
        var stores = pk.ById<PropertyDef>("p-stores");
        Func<string, bool> north = x => x == "north", south = x => x == "south";
        Assert.False(HiddenValues.HolderValueHidden(pk, Ford, stores, north));   // the fixed owner north
        Assert.True(HiddenValues.HolderValueHidden(pk, Ford, stores, south));
        Assert.True(HiddenValues.HolderValueHidden(s, pk, Ford, stores, north));   // the current owner south
        Assert.False(HiddenValues.HolderValueHidden(s, pk, Ford, stores, south));
        Assert.True(HiddenValues.HolderValueHidden(s, pk, Ford, null, north));   // the whole key
        Assert.False(HiddenValues.HolderValueHidden(null, pk, Ford, stores, north));   // no state: the fixed owner
        Assert.False(HiddenValues.HolderValueHidden(s, pk, Ford, pk.ById<PropertyDef>("p-owner"), north));   // Public
    }
}
