using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class LedgerTabsTests
{
    private const string Main = "Main Map";
    private const string Other = "Other Map";

    private static RulesPack Pack() => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "a", Name = "Alpha Side", UnitFilter = "marker(\"Side\") == \"A\"" },
            new SideDef { Id = "b", Name = "Beta Side", UnitFilter = "marker(\"Side\") == \"B\"" },
        },
        Properties = new()
        {
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Visibility = Visibility.Private, AppliesTo = "marker(\"Side\") != \"\"" },
            new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Public },
            new PropertyDef { Id = "divisor", Name = "Divisor", Scope = PropertyScope.Both, Visibility = Visibility.Public, AppliesTo = "marker(\"Side\") != \"\"" },
        },
        Holders = new() { new HolderDef { Id = "depot", Name = "Depot TEST" } },
    };

    private static void Add(GameState s, string id, string name, string? side, string map = Main)
    {
        var type = side == null
            ? PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;" + name })
            : PieceTypeParser.EncodeState(new[] { "mark;Side", "piece;;;unit.svg;" + name });
        var state = side == null ? PieceTypeParser.EncodeState(new[] { "" }) : PieceTypeParser.EncodeState(new[] { side, "" });
        s.Apply(new AddPiece(id, type, state, map, new PointD(50, 50), null, name + " placed", Who: "t"));
    }

    private static GameState Game()
    {
        var s = new GameState { GameId = "g1" };
        Add(s, "a1", "Anvil TEST", "A");
        Add(s, "a2", "Axle TEST", "A", Other);
        Add(s, "b1", "Bolt TEST", "B");
        Add(s, "n1", "Nobody TEST", "");
        Add(s, "m1", "Marker TEST", null);
        Add(s, "e1", "Empty TEST", "A");
        s.Ledgers[LedgerKey.Piece("a1")] = new() { ["fuel"] = 3 };
        s.Ledgers[LedgerKey.Piece("a2")] = new() { ["fuel"] = 9 };
        s.Ledgers[LedgerKey.Piece("b1")] = new() { ["fuel"] = 4 };
        s.Ledgers[LedgerKey.Piece("n1")] = new() { ["fuel"] = 1 };
        s.Ledgers[LedgerKey.Holder("global")] = new() { ["divisor"] = 2 };
        s.Ledgers[LedgerKey.Holder("depot")] = new() { ["supply"] = 7 };
        return s;
    }

    private static LedgerTabDef Tab(string key, IReadOnlyList<UserLedgerTab>? user = null) =>
        LedgerTabs.Tabs(user ?? Array.Empty<UserLedgerTab>()).First(t => t.Key == key);

    private static LedgerTabDef User(string? filter = null, string[]? cols = null, bool holders = false)
    {
        var u = new UserLedgerTab("Mine TEST", filter, cols ?? Array.Empty<string>(), holders, null);
        return Tab("user:Mine TEST", new[] { u });
    }

    private static LedgerTabResult Run(LedgerTabDef tab, GameState? s = null, string filter = "", string? side = null,
        LedgerGrouping g = LedgerGrouping.None, bool empty = false) =>
        LedgerTabs.Rows(tab, s ?? Game(), Pack(), RunnerFx.Model, filter, side, g, empty);

    private static string[] Names(LedgerTabResult r) => r.Rows.Select(x => x.Row.Name).ToArray();

    [Fact]
    public void Tabs_FixedThreeThenUserInOrder()
    {
        var tabs = LedgerTabs.Tabs(new[] { new UserLedgerTab("Supply TEST", null, Array.Empty<string>(), false, null) });
        Assert.Equal(new[] { "units", "holders", "all", "user:Supply TEST" }, tabs.Select(t => t.Key));
        Assert.Equal(new[] { LedgerTabKind.Units, LedgerTabKind.Holders, LedgerTabKind.Everything, LedgerTabKind.User }, tabs.Select(t => t.Kind));
        Assert.NotNull(tabs[3].User);
    }

    [Fact]
    public void Units_ColumnsArePiecesAndBoth() =>
        Assert.Equal(new[] { "Divisor", "Fuel" }, LedgerTabs.ColumnsFor(Tab("units"), Pack()).Select(c => c.Name));

    [Fact]
    public void Holders_ColumnsAreHoldersAndBoth() =>
        Assert.Equal(new[] { "Divisor", "Supply" }, LedgerTabs.ColumnsFor(Tab("holders"), Pack()).Select(c => c.Name));

    [Fact]
    public void Everything_AllColumns() =>
        Assert.Equal(new[] { "Divisor", "Fuel", "Supply" }, LedgerTabs.ColumnsFor(Tab("all"), Pack()).Select(c => c.Name));

    [Fact]
    public void Units_RowsHaveNoHolders()
    {
        var r = Run(Tab("units"));
        Assert.Equal(new[] { "Anvil TEST", "Bolt TEST", "Nobody TEST", "Axle TEST" }, Names(r));
        Assert.All(r.Rows, x => Assert.NotNull(x.PieceId));
        Assert.Equal(4, r.Count);
        Assert.Equal(new double?[] { null, 3 }, r.Rows[0].Row.Values);
        Assert.Null(r.FilterError);
    }

    [Fact]
    public void Holders_RowsAreGlobalThenHolders()
    {
        var r = Run(Tab("holders"));
        Assert.Equal(new[] { "Global", "Depot TEST" }, Names(r));
        Assert.Equal(new double?[] { 2, null }, r.Rows[0].Row.Values);
        Assert.Equal(new double?[] { null, 7 }, r.Rows[1].Row.Values);
        Assert.All(r.Rows, x => Assert.Null(x.PieceId));
    }

    [Fact]
    public void Everything_MatchesLedgerTableRows()
    {
        var s = Game();
        var expected = LedgerTable.Rows(s, Pack(), RunnerFx.Model, "", true).Select(x => x.Name);
        var r = Run(Tab("all"), s, empty: true);
        Assert.Equal(expected, Names(r));
        Assert.Equal(3, r.Columns.Count);
    }

    [Fact]
    public void Units_IncludeEmpty_AddsMatchingPiecesButNotMarkers()
    {
        var r = Run(Tab("units"), empty: true);
        Assert.Contains("Empty TEST", Names(r));
        Assert.DoesNotContain("Marker TEST", Names(r));
    }

    [Fact]
    public void Units_SideFilter_KeepsOnlyThatSide()
    {
        var r = Run(Tab("units"), side: "a");
        Assert.Equal(new[] { "Anvil TEST", "Axle TEST" }, Names(r));
        Assert.All(r.Rows, x => Assert.Equal("a", x.SideId));
    }

    [Fact]
    public void SideFilter_IgnoredForHoldersAndEverything()
    {
        Assert.Equal(2, Run(Tab("holders"), side: "a").Count);
        Assert.Equal(6, Run(Tab("all"), side: "a").Count);
    }

    [Fact]
    public void Units_TextFilter_AppliedLast()
    {
        Assert.Equal(new[] { "Bolt TEST" }, Names(Run(Tab("units"), filter: "bolt")));
    }

    [Fact]
    public void Units_GroupByMap_InsertsHeaderRows()
    {
        var r = Run(Tab("units"), g: LedgerGrouping.Map);
        Assert.Equal(new[] { Main, "Anvil TEST", "Bolt TEST", "Nobody TEST", Other, "Axle TEST" }, Names(r));
        Assert.Equal(new[] { true, false, false, false, true, false }, r.Rows.Select(x => x.IsGroupHeader));
        Assert.Equal(4, r.Count);
        Assert.Equal("group:" + Main, r.Rows[0].Row.Key);
        Assert.All(r.Rows[0].Row.Values, v => Assert.Null(v));
        Assert.Equal(r.Columns.Count, r.Rows[0].Row.Values.Count);
    }

    [Fact]
    public void GroupBySide_UnownedUnitsUnderNoSide()
    {
        var r = Run(Tab("units"), g: LedgerGrouping.Side);
        Assert.Equal(new[] { "Alpha Side", "Anvil TEST", "Axle TEST", "Beta Side", "Bolt TEST", "No side", "Nobody TEST" }, Names(r));
        Assert.Equal(4, r.Count);
    }

    [Fact]
    public void GroupHeaders_NotInsertedForHoldersOrEverything()
    {
        Assert.DoesNotContain(Run(Tab("holders"), g: LedgerGrouping.Map).Rows, x => x.IsGroupHeader);
        Assert.DoesNotContain(Run(Tab("all"), g: LedgerGrouping.Side).Rows, x => x.IsGroupHeader);
    }

    [Fact]
    public void UserTab_FilterRunsOverUnitsTabSet()
    {
        var r = Run(User(""));
        Assert.Equal(new[] { "Anvil TEST", "Bolt TEST", "Nobody TEST", "Axle TEST" }, Names(r));
        Assert.DoesNotContain("Marker TEST", Names(Run(User(""), empty: true)));
        Assert.Equal(new[] { "Anvil TEST", "Bolt TEST", "Nobody TEST" }, Names(Run(User("Fuel < 5"))));
    }

    [Fact]
    public void UserTab_EmptyColumns_UsesUnitsColumns() =>
        Assert.Equal(new[] { "Divisor", "Fuel" }, LedgerTabs.ColumnsFor(User(), Pack()).Select(c => c.Name));

    [Fact]
    public void UserTab_ColumnIdsKeepOrder()
    {
        var t = User(cols: new[] { "supply", "fuel" });
        Assert.Equal(new[] { "Supply", "Fuel" }, LedgerTabs.ColumnsFor(t, Pack()).Select(c => c.Name));
        var r = Run(t, g: LedgerGrouping.None);
        Assert.Equal(new double?[] { null, 3 }, r.Rows[0].Row.Values);
    }

    [Fact]
    public void UserTab_UnknownColumnIdDropped() =>
        Assert.Equal(new[] { "Fuel" }, LedgerTabs.ColumnsFor(User(cols: new[] { "gone", "fuel" }), Pack()).Select(c => c.Name));

    [Fact]
    public void UserTab_IncludeHolders_HoldersFirst()
    {
        var r = Run(User(holders: true));
        Assert.Equal(new[] { "Global", "Depot TEST", "Anvil TEST", "Bolt TEST", "Nobody TEST", "Axle TEST" }, Names(r));
        Assert.Equal(6, r.Count);
    }

    [Fact]
    public void UserTab_BrokenFilter_NoRowsAndError()
    {
        var r = Run(User("Fuel <"));
        Assert.NotNull(r.FilterError);
        Assert.Empty(r.Rows);
        Assert.Equal(0, r.Count);
        Assert.NotNull(LedgerTabs.FilterError("Fuel <"));
        Assert.Null(LedgerTabs.FilterError(null));
        Assert.Null(LedgerTabs.FilterError("  "));
        Assert.Null(LedgerTabs.FilterError("Fuel < 5"));
    }

    [Fact]
    public void UserTab_FilterNamesDeletedProperty_BannerNoRowsNoCrash()
    {
        var r = Run(User("Gone < 5 and Fuel < 5"));
        Assert.Equal("Unknown property: Gone", r.FilterError); // the Ledger window shows FilterError as its banner
        Assert.Empty(r.Rows);
        Assert.Equal(0, r.Count);
        Assert.Equal("Unknown property: Gone", LedgerTabs.FilterError("has(Gone)", Pack()));
        Assert.Equal("Unknown property: Gone", LedgerTabs.FilterError("has(\"Gone\")", Pack()));
        Assert.Equal("Unknown property: Gone", LedgerTabs.FilterError("not (abs(Gone) > 1)", Pack()));
    }

    [Fact]
    public void FilterError_WithPack_KnownNamesAndPieceIdentifiersNotFlagged()
    {
        var pack = Pack();
        Assert.Null(LedgerTabs.FilterError("Fuel < 5", pack));
        Assert.Null(LedgerTabs.FilterError("fuel < 5", pack)); // property id
        Assert.Null(LedgerTabs.FilterError("has(Fuel) and Divisor > 0", pack));
        Assert.Null(LedgerTabs.FilterError("marker(\"Type\") == \"Infantry TEST\" and name() != \"x\"", pack)); // piece identifiers
        Assert.Null(LedgerTabs.FilterError("lookup(SomeTable, Fuel) > 1", pack)); // a table name is not a property
        Assert.Null(LedgerTabs.FilterError(null, pack));
        Assert.Null(LedgerTabs.FilterError("  ", pack));
        Assert.NotNull(LedgerTabs.FilterError("Fuel <", pack)); // parse errors still come first
        // A bare name is only ever a rules-pack property to PieceFilter: with no property "Type" it matches nothing, so it is reported.
        Assert.Equal("Unknown property: Type", LedgerTabs.FilterError("Type == \"Infantry TEST\"", pack));
        var withType = pack with { Properties = pack.Properties.Append(new PropertyDef { Id = "type", Name = "Type", Scope = PropertyScope.Pieces }).ToList() };
        Assert.Null(LedgerTabs.FilterError("Type == 1", withType));
    }

    [Fact]
    public void UserTab_PrivateProperty_ViewHidesIt()
    {
        var pack = Pack();
        var full = Game();
        var view = PlayerView.For(full, pack, RunnerFx.Model, new HashSet<string> { "b" });
        var r = LedgerTabs.Rows(User("Fuel < 5"), view, pack, RunnerFx.Model, "", null, LedgerGrouping.None, false);
        Assert.DoesNotContain(r.Rows, x => x.SideId == "a");
        Assert.DoesNotContain("Anvil TEST", Names(r));
        Assert.DoesNotContain("Axle TEST", Names(r));
        Assert.Contains("Bolt TEST", Names(r));
    }

    [Fact]
    public void ToCsv_SkipsGroupHeaders_UsesTabColumns()
    {
        var r = Run(User(cols: new[] { "fuel" }), g: LedgerGrouping.Map);
        Assert.Contains(r.Rows, x => x.IsGroupHeader);
        var csv = LedgerTabs.ToCsv(r);
        Assert.Equal("Name,Kind,Fuel\r\nAnvil TEST,Unit · Main Map,3\r\nBolt TEST,Unit · Main Map,4\r\nNobody TEST,Unit · Main Map,1\r\nAxle TEST,Unit · Other Map,9\r\n", csv);
    }
}
