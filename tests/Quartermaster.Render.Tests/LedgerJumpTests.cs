using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>"Show in Ledger": which tab, which rows and which filter changes for counters picked on the map (invented TEST data).</summary>
public class LedgerJumpTests
{
    private static readonly Lazy<ModuleModel> LazyModel = new(() =>
    {
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile()).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        var path = fx.Build();
        using var arch = VmodArchive.Open(path);
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    });

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
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Visibility = Visibility.Public, AppliesTo = "marker(\"Side\") != \"\"" },
            new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Public },
        },
        Holders = new() { new HolderDef { Id = "depot", Name = "Depot TEST" } },
    };

    private static void Add(GameState s, string id, string name, string? side)
    {
        var type = side == null
            ? PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;" + name })
            : PieceTypeParser.EncodeState(new[] { "mark;Side", "piece;;;unit.svg;" + name });
        var state = side == null ? PieceTypeParser.EncodeState(new[] { "" }) : PieceTypeParser.EncodeState(new[] { side, "" });
        s.Apply(new AddPiece(id, type, state, "Main Map", new PointD(50, 50), null, name + " placed", Who: "t"));
    }

    private static GameState Game()
    {
        var s = new GameState { GameId = "g1" };
        Add(s, "a1", "Anvil TEST", "A");
        Add(s, "a2", "Axle TEST", "A");
        Add(s, "b1", "Bolt TEST", "B");
        Add(s, "m1", "Marker TEST", null);
        Add(s, "e1", "Empty TEST", "A");
        s.Ledgers[LedgerKey.Piece("a1")] = new() { ["fuel"] = 3 };
        s.Ledgers[LedgerKey.Piece("a2")] = new() { ["fuel"] = 9 };
        s.Ledgers[LedgerKey.Piece("b1")] = new() { ["fuel"] = 1 };
        s.Ledgers[LedgerKey.Holder("depot")] = new() { ["supply"] = 7 };
        return s;
    }

    private static readonly UserLedgerTab LowFuel = new("Low fuel TEST", "Fuel < 5", new[] { "fuel" }, false, null);
    private static readonly UserLedgerTab Alphas = new("Alphas TEST", "marker(\"Side\") == \"A\"", new[] { "fuel" }, false, null);

    private static LedgerJumpPlan Plan(string[] ids, string current = "units", string filter = "", string? side = null, bool empty = false,
        Func<string, bool>? canSee = null, GameState? view = null, UserLedgerTab[]? user = null) =>
        LedgerJump.Plan(ids, LedgerTabs.Tabs(user ?? new[] { LowFuel }), current, view ?? Game(), Pack(), LazyModel.Value, filter, side, empty, canSee);

    private static string P(string id) => LedgerKey.Piece(id);

    [Fact]
    public void RowOnCurrentTab_StaysThere()
    {
        var plan = Plan(new[] { "a1" });
        Assert.Equal("units", plan.TabKey);
        Assert.False(plan.ClearFilter);
        Assert.Equal(new[] { P("a1") }, plan.RowKeys);
        Assert.Equal(0, plan.Missing);
        Assert.Null(plan.Message);
    }

    [Fact]
    public void RowOnCurrentUserTab_StaysThere()
    {
        var plan = Plan(new[] { "a1" }, current: "user:Low fuel TEST");
        Assert.Equal("user:Low fuel TEST", plan.TabKey);
        Assert.Equal(new[] { P("a1") }, plan.RowKeys);
    }

    [Fact]
    public void RowOnlyOnAnotherTab_SwitchesToTheFirstTabShowingIt()
    {
        var plan = Plan(new[] { "a1" }, current: "holders");
        Assert.Equal("units", plan.TabKey);
        Assert.False(plan.ClearFilter);
        Assert.Equal(new[] { P("a1") }, plan.RowKeys);
    }

    [Fact]
    public void NotOnCurrentUserTab_SwitchesToUnits()
    {
        var plan = Plan(new[] { "a2" }, current: "user:Low fuel TEST");   // fuel 9: not in "Fuel < 5"
        Assert.Equal("units", plan.TabKey);
        Assert.Equal(new[] { P("a2") }, plan.RowKeys);
    }

    [Fact]
    public void HiddenByTextFilter_EverythingWithTheFilterCleared()
    {
        var plan = Plan(new[] { "a1" }, filter: "Bolt");
        Assert.Equal("all", plan.TabKey);
        Assert.True(plan.ClearFilter);
        Assert.Equal(new[] { P("a1") }, plan.RowKeys);
        Assert.Null(plan.Message);
    }

    [Fact]
    public void TextFilterThatStillShowsTheRow_IsKept()
    {
        var plan = Plan(new[] { "a1" }, filter: "Anvil");
        Assert.Equal("units", plan.TabKey);
        Assert.False(plan.ClearFilter);
    }

    [Fact]
    public void HiddenBySideFilter_MovesToEverything_WhichIgnoresTheSide()
    {
        var plan = Plan(new[] { "a1" }, side: "b");
        Assert.Equal("all", plan.TabKey);
        Assert.False(plan.ClearFilter);
        Assert.Equal(new[] { P("a1") }, plan.RowKeys);
    }

    [Fact]
    public void HiddenBySideFilterOnUserTab_PrefersUnitsOnlyWhenItShowsIt()
    {
        // Side "b" hides a1 on Units and on the user tab alike, so Everything is the first tab that shows it.
        var plan = Plan(new[] { "a1" }, current: "user:Low fuel TEST", side: "b");
        Assert.Equal("all", plan.TabKey);
    }

    [Fact]
    public void MultiplePieces_OneTabShowingAll_WinsOverTheCurrentTabShowingSome()
    {
        var plan = Plan(new[] { "b1", "a2" }, current: "user:Low fuel TEST");   // b1 is on Low fuel, a2 is not
        Assert.Equal("units", plan.TabKey);
        Assert.Equal(new[] { P("a2"), P("b1") }.OrderBy(x => x), plan.RowKeys.OrderBy(x => x));
        Assert.Equal(0, plan.Missing);
    }

    [Fact]
    public void MultiplePieces_RowKeysFollowTheTabOrder()
    {
        var s = Game();
        var order = LedgerTabs.Rows(LedgerTabs.Tabs(Array.Empty<UserLedgerTab>())[0], s, Pack(), LazyModel.Value, "", null, LedgerGrouping.None, false)
            .Rows.Select(r => r.Row.Key).Where(k => k == P("a1") || k == P("b1")).ToList();
        var plan = Plan(new[] { "b1", "a1" }, view: s);
        Assert.Equal(order, plan.RowKeys);
    }

    [Fact]
    public void MultiplePieces_SomeWithoutRows_SelectsTheRestAndSaysSo()
    {
        var plan = Plan(new[] { "a1", "m1" });
        Assert.Equal("units", plan.TabKey);
        Assert.Equal(new[] { P("a1") }, plan.RowKeys);
        Assert.Equal(1, plan.Missing);
        Assert.Contains("1 of the 2", plan.Message);
    }

    [Fact]
    public void MultiplePieces_SideSplit_EverythingShowsBoth()
    {
        var plan = Plan(new[] { "a1", "b1" }, side: "a");
        Assert.Equal("all", plan.TabKey);
        Assert.Equal(2, plan.RowKeys.Count);
    }

    [Fact]
    public void PieceNotVisibleToViewer_NeverPlanned()
    {
        var plan = Plan(new[] { "a1" }, canSee: id => id != "a1");
        Assert.Null(plan.TabKey);
        Assert.Empty(plan.RowKeys);
        Assert.Equal(1, plan.Missing);
        Assert.NotNull(plan.Message);
        Assert.DoesNotContain("Anvil", plan.Message);
    }

    [Fact]
    public void PieceMissingFromTheView_NeverPlanned()
    {
        var view = Game();
        view.Pieces.Remove("a1");
        view.Ledgers.Remove(P("a1"));
        var plan = Plan(new[] { "a1", "b1" }, view: view);
        Assert.Equal(new[] { P("b1") }, plan.RowKeys);
        Assert.Equal(1, plan.Missing);
    }

    [Fact]
    public void HiddenPieceIsNotSelected_EvenWhenTheViewHasItsRow()
    {
        // Fail closed: the predicate wins over a row the view would list.
        var plan = Plan(new[] { "a1", "b1" }, canSee: id => id == "b1");
        Assert.Equal(new[] { P("b1") }, plan.RowKeys);
    }

    [Fact]
    public void CanSeeThatThrows_FailsClosed()
    {
        var plan = Plan(new[] { "a1", "b1" }, canSee: id => id == "a1" ? throw new InvalidOperationException("test fault") : true);
        Assert.Equal(new[] { P("b1") }, plan.RowKeys);
        Assert.Equal(1, plan.Missing);
    }

    [Fact]
    public void TextFilterKept_WhenAnotherTabShowsTheRowUnderIt()
    {
        // The side hides a1 on Units; Everything still shows it with the text filter, so the filter is not cleared.
        var plan = Plan(new[] { "a1" }, filter: "Anvil", side: "b");
        Assert.Equal("all", plan.TabKey);
        Assert.False(plan.ClearFilter);
        Assert.Equal(new[] { P("a1") }, plan.RowKeys);
    }

    [Fact]
    public void CurrentUserTabWithAFilterError_MovesToUnits()
    {
        var broken = new UserLedgerTab("Broken TEST", "Fuel <", Array.Empty<string>(), false, null);
        var plan = Plan(new[] { "a1" }, current: "user:Broken TEST", user: new[] { broken });
        Assert.Equal("units", plan.TabKey);
        Assert.Equal(new[] { P("a1") }, plan.RowKeys);
    }

    [Fact]
    public void PieceWithNoRow_NoTabAndAHint()
    {
        var plan = Plan(new[] { "e1" });   // no ledger values, units without values hidden
        Assert.Null(plan.TabKey);
        Assert.Empty(plan.RowKeys);
        Assert.Equal(1, plan.Missing);
        Assert.Contains("no row in the Ledger", plan.Message);
        Assert.Contains("Show units without values", plan.Message);
    }

    [Fact]
    public void PieceWithNoValues_ShownWhenUnitsWithoutValuesAreOn()
    {
        var plan = Plan(new[] { "e1" }, empty: true);
        Assert.Equal("units", plan.TabKey);
        Assert.Equal(new[] { P("e1") }, plan.RowKeys);
        Assert.Null(plan.Message);
    }

    [Fact]
    public void MarkerWithNoRow_NoHintAboutValuesWhenTheyAreAlreadyShown()
    {
        var plan = Plan(new[] { "m1" }, empty: true);
        Assert.Null(plan.TabKey);
        Assert.DoesNotContain("Show units without values", plan.Message);
    }

    [Fact]
    public void NoPieces_NothingToDo()
    {
        var plan = Plan(Array.Empty<string>());
        Assert.Null(plan.TabKey);
        Assert.Empty(plan.RowKeys);
        Assert.Equal(0, plan.Missing);
        Assert.Null(plan.Message);
    }

    [Fact]
    public void UnknownCurrentTab_TreatedAsNoCurrentTab()
    {
        var plan = Plan(new[] { "a1" }, current: "user:Gone TEST");
        Assert.Equal("units", plan.TabKey);
    }

    [Fact]
    public void UserTabsAfterTheFixedOnes_AreUsedWhenCurrent()
    {
        var plan = Plan(new[] { "a1" }, current: "user:Alphas TEST", user: new[] { LowFuel, Alphas });
        Assert.Equal("user:Alphas TEST", plan.TabKey);
    }
}
