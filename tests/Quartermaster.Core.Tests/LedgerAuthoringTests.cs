using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Core.Tests;

/// <summary>B1 displays: holder cells of properties a holder does not keep, dropped columns and labelled breakdown titles. Invented data.</summary>
public class LedgerAuthoringTests
{
    static TableDef WeatherNames => new()
    {
        Id = "t-wx", Name = "Weather names", Kind = TableKind.OneD,
        RowKeys = new() { "1", "2", "3" }, ColumnKeys = new() { "Name" },
        Cells = new() { new() { "Clear" }, new() { "Rain" }, new() { "Fog" } },
    };

    /// <summary>Ferry tolls kept by the North depot only; Stores kept by every holder; Weather (global) with labels.</summary>
    static RulesPack Pack() => new()
    {
        Format = 4,
        Properties = new()
        {
            new PropertyDef { Id = "tolls", Name = "Ferry tolls", Scope = PropertyScope.Holders, Holders = new() { "north" } },
            new PropertyDef { Id = "stores", Name = "Stores", Scope = PropertyScope.Holders },
            new PropertyDef { Id = "wx", Name = "Weather", Scope = PropertyScope.Holders, Holders = new() { HolderDef.GlobalId }, Labels = "t-wx" },
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces },
        },
        Holders = new() { new HolderDef { Id = "north", Name = "North depot" }, new HolderDef { Id = "south", Name = "South depot" } },
        Tables = new() { WeatherNames },
    };

    static GameState Game()
    {
        var s = new GameState { GameId = "g1" };
        s.Ledgers[LedgerKey.Holder("north")] = new() { ["tolls"] = 4, ["stores"] = 10 };
        s.Ledgers[LedgerKey.Holder("south")] = new() { ["tolls"] = 9, ["stores"] = 6 };   // a stale value of a property South does not keep
        s.Ledgers[LedgerKey.Holder(HolderDef.GlobalId)] = new() { ["wx"] = 3 };
        return s;
    }

    static int Col(RulesPack p, string id) => LedgerTable.Columns(p).ToList().FindIndex(c => c.Id == id);

    [Fact]
    public void LedgerTable_not_kept_cell_blank()
    {
        var p = Pack();
        var rows = LedgerTable.Rows(Game(), p, RunnerFx.Model, "");
        var south = rows.Single(r => r.Name == "South depot");
        var north = rows.Single(r => r.Name == "North depot");
        var global = rows.Single(r => r.Name == "Global");
        var tolls = Col(p, "tolls");
        var stores = Col(p, "stores");
        var wx = Col(p, "wx");
        Assert.Null(south.Values[tolls]);                // the stale 9 is not shown
        Assert.Contains(tolls, south.NotKept!);
        Assert.Contains(wx, south.NotKept!);
        Assert.DoesNotContain(stores, south.NotKept!);
        Assert.Equal(6, south.Values[stores]);
        Assert.Equal(4, north.Values[tolls]);
        Assert.DoesNotContain(tolls, north.NotKept ?? new HashSet<int>());
        Assert.Equal(3, global.Values[wx]);
        Assert.Contains(tolls, global.NotKept!);
        Assert.DoesNotContain(Col(p, "fuel"), south.NotKept!);   // a piece-only property is not a holder's to keep
        // CSV leaves the not-kept cell empty
        Assert.DoesNotContain(",9", LedgerTable.ToCsv(LedgerTable.Columns(p), rows));
    }

    [Fact]
    public void LedgerTabs_drop_unkept_holder_column()
    {
        var p = Pack();
        var holders = LedgerTabs.Tabs(Array.Empty<UserLedgerTab>()).First(t => t.Key == "holders");
        var all = LedgerTabs.Rows(holders, Game(), p, RunnerFx.Model, "", null, LedgerGrouping.None, false);
        Assert.Equal(new[] { "Ferry tolls", "Stores", "Weather" }, all.Columns.Select(c => c.Name));
        var southCols = all.Rows.Single(r => r.Row.Name == "South depot").Row;
        Assert.Contains(0, southCols.NotKept!);   // not-kept indexes follow the tab's own column order
        Assert.Null(southCols.Values[0]);

        // Filtered to South: no shown row keeps Ferry tolls or Weather, so those columns go; Stores stays.
        var south = LedgerTabs.Rows(holders, Game(), p, RunnerFx.Model, "South", null, LedgerGrouping.None, false);
        Assert.Equal(new[] { "Stores" }, south.Columns.Select(c => c.Name));
        Assert.Equal(new double?[] { 6 }, south.Rows.Single().Row.Values);
        Assert.True(south.Rows.Single().Row.NotKept is null or { Count: 0 });

        // A user tab with holders, filtered to the North depot: Weather (kept by Global only) goes.
        var user = new UserLedgerTab("Depots TEST", null, new[] { "tolls", "wx", "stores" }, true, null);
        var tab = LedgerTabs.Tabs(new[] { user }).First(t => t.Kind == LedgerTabKind.User);
        var north = LedgerTabs.Rows(tab, Game(), p, RunnerFx.Model, "North", null, LedgerGrouping.None, false);
        Assert.Equal(new[] { "Ferry tolls", "Stores" }, north.Columns.Select(c => c.Name));
        Assert.Equal(new double?[] { 4, 10 }, north.Rows.Single().Row.Values);

        // Everything keeps every column.
        var everything = LedgerTabs.Tabs(Array.Empty<UserLedgerTab>()).First(t => t.Key == "all");
        Assert.Equal(4, LedgerTabs.Rows(everything, Game(), p, RunnerFx.Model, "South", null, LedgerGrouping.None, false).Columns.Count);
    }

    [Fact]
    public void Breakdown_title_label()
    {
        var p = Pack();
        var b = LedgerBreakdown.For(Game(), p, RunnerFx.Model, LedgerKey.Holder(HolderDef.GlobalId), "wx");
        Assert.StartsWith("Weather 3 (Fog)  (Global)", b.Title);
        var plain = LedgerBreakdown.For(Game(), p, RunnerFx.Model, LedgerKey.Holder("north"), "stores");
        Assert.StartsWith("Stores 10  (North depot)", plain.Title);
        var s = Game();
        s.Ledgers[LedgerKey.Holder(HolderDef.GlobalId)]["wx"] = 7;   // no row matches: only the number
        Assert.StartsWith("Weather 7  (Global)", LedgerBreakdown.For(s, p, RunnerFx.Model, LedgerKey.Holder(HolderDef.GlobalId), "wx").Title);
    }
}
