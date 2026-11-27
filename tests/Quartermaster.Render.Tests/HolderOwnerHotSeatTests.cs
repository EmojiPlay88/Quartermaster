using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B9 #13a: the alliance fixture (no Upriver Pact) with the Southern Compact and the Hill Clans in the Delta Pact, a Global Public DumpOwner
/// keyed by table Dumps (Default 1 = Northern League), and Ford Dump (fixed side north) owned by <c>keyed(DumpOwner, "Ford Dump")</c>, keeping
/// a Private Stores. Formulas: Ford count (a fixed Holder binding on Ford Dump) and Haul (adds 2 to Ford Dump's Stores). Invented data.
/// </summary>
public abstract class HolderOwnerTestBase : AllianceTestBase
{
    protected const string Ford = "forddump";
    protected const double FordStores = 31313;
    protected const string DeltaPact = "Delta Pact";

    protected static RulesPack B9Pack(string sha)
    {
        var p = Pack(sha, alliance: false);
        return p with
        {
            Sides = p.Sides.Select(sd => sd.Id is "south" or "hills" ? sd with { AllianceId = DeltaPact } : sd).ToList(),
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "p-dumpowner", Name = "DumpOwner", Scope = PropertyScope.Holders, Holders = new() { HolderDef.GlobalId },
                    KeyedBy = "table:t-dumps", Visibility = Visibility.Public, Default = 1 },
                new PropertyDef { Id = "p-stores", Name = "Stores", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
            }).ToList(),
            Tables = p.Tables.Append(new TableDef
            {
                Id = "t-dumps", Name = "Dumps", Kind = TableKind.OneD, RowKeys = new() { "Ford Dump", "Mill Dump" },
                ColumnKeys = new() { "Value" }, Cells = new() { new() { "1" }, new() { "2" } },
            }).ToList(),
            Holders = p.Holders.Append(new HolderDef { Id = Ford, Name = "Ford Dump", SideId = "north", OwnerExpr = "keyed(DumpOwner, \"Ford Dump\")" }).ToList(),
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-ford", Name = "Ford count", Text = "S",
                    Bindings = new() { new VariableBinding { Variable = "S", Kind = BindingKind.Holder, HolderId = Ford, Property = "p-stores" } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "p-gate", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-haul", Name = "Haul", Text = "2",
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = Ford, Property = "p-stores", Op = OutputOp.Add } },
                },
            }).ToList(),
        };
    }

    /// <summary>The alliance session with the B9 pack and Ford Dump's Stores set.</summary>
    protected ModuleSession B9Session(PlayerDef[]? players = null)
    {
        var s = Session(alliance: false, players: players);
        s.UpdateRules(_ => B9Pack(s.Archive.Sha256));
        s.Game!.Execute(new LedgerDelta(LedgerKey.Holder(Ford), "p-stores", 0, FordStores, "manual", null, null, null, null));
        return s;
    }

    /// <summary>The Southern Compact takes Ford Dump: DumpOwner of "Ford Dump" becomes 2.</summary>
    protected static LedgerDelta Capture(RulesPack pack, double before = 1, double after = 2) =>
        new(LedgerKey.Holder(HolderDef.GlobalId), "p-dumpowner", before, after, "manual", null, null, null, null)
        { ValueKey = KeyedValues.NormaliseKey(pack, null, pack.ById<PropertyDef>("p-dumpowner")!, Value.Of("Ford Dump")) };

    protected static double? Stores(GameState st) => Ledger.Get(st, LedgerKey.Holder(Ford), "p-stores");

    protected static string? Owner(ModuleSession s) => Ownership.SideOfHolder(Ford, s.Rules!, s.Game!.State);
}

/// <summary>B9 #13a in hot-seat: acting rights, the handed-over view and the formula masking follow Ford Dump's current owner.</summary>
public class HolderOwnerHotSeatTests : HolderOwnerTestBase
{
    [Fact]
    public void Hotseat_ledger_edit_follows_current_owner()
    {
        var s = B9Session();
        var key = LedgerKey.Holder(Ford);
        s.SetViewSides(North);
        Assert.True(s.CanEditLedger(key));
        s.SetViewSides(South);
        Assert.False(s.CanEditLedger(key));
        s.Game!.Execute(Capture(s.Rules!));
        Assert.Equal("south", Owner(s));
        Assert.True(s.CanEditLedger(key));
        s.SetViewSides(North);
        Assert.False(s.CanEditLedger(key));
        Assert.True(s.CanEditLedgerFor(key, null));   // solo
    }

    [Fact]
    public void Hotseat_handover_after_capture_shows_new_owner_values()
    {
        var s = B9Session();
        s.SetViewSides(South);
        Assert.Null(Stores(s.View!));
        s.SetViewSides(North);
        Assert.Equal(FordStores, Stores(s.View!));
        s.Game!.Execute(Capture(s.Rules!));
        Assert.Null(Stores(s.View!));   // the north's open view loses the dump's Private values at once
        s.SetViewSides(South);   // the handover
        Assert.Equal(FordStores, Stores(s.View!));
        Assert.DoesNotContain(s.Game.State.Log, l => l.Text.Contains("belongs to", StringComparison.OrdinalIgnoreCase));
        using (s.Game.BeginAction()) s.Game.Execute(Capture(s.Rules!, 2, 1));
        Assert.Null(Stores(s.View!));
    }

    [Fact]
    public void Formula_masking_follows_current_owner()
    {
        var s = B9Session();
        var f = s.Rules!.ById<FormulaDef>("f-ford")!;
        s.SetViewSides(North);
        Assert.Empty(FormulaMasking.HiddenFor(s, f, null));
        s.SetViewSides(South);
        Assert.Contains("S", FormulaMasking.HiddenFor(s, f, null));
        s.Game!.Execute(Capture(s.Rules!));
        Assert.Empty(FormulaMasking.HiddenFor(s, f, null));
        s.SetViewSides(North);
        Assert.Contains("S", FormulaMasking.HiddenFor(s, f, null));   // masked for the north after the capture, not before
        Assert.Contains("S", FormulaMasking.HiddenVariables(s.Rules!, s.Model, s.Game.State, f, null, North));
    }

    [Fact]
    public void Ally_of_current_owner_sees_values()
    {
        var s = B9Session();
        s.SetViewSides(Hills);
        Assert.Equal(new[] { "hills", "south" }, s.ViewSides!.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Null(Stores(s.View!));
        Assert.Contains("S", FormulaMasking.HiddenFor(s, s.Rules!.ById<FormulaDef>("f-ford")!, null));
        s.Game!.Execute(Capture(s.Rules!));
        Assert.Equal(FordStores, Stores(s.View!));   // the Delta Pact holds the south
        Assert.Empty(FormulaMasking.HiddenFor(s, s.Rules!.ById<FormulaDef>("f-ford")!, null));
        Assert.True(s.CanEditLedger(LedgerKey.Holder(Ford)));   // through the held set, as B8
    }
}
