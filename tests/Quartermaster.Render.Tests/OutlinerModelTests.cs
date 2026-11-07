using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class OutlinerModelTests
{
    private const string V = "VASSAL.build.";

    private static ModuleModel Model()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            $"""
            <{V}module.ChartWindow name="Charts"><{V}widget.TabWidget entryName="Tab">
              <{V}widget.WidgetMap mapName="Org A"/><{V}widget.WidgetMap mapName="Org B"/><{V}widget.WidgetMap mapName="Org Empty"/>
            </{V}widget.TabWidget></{V}module.ChartWindow></VASSAL.build.GameModule>
            """);
        using var fx = new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var a = VmodArchive.Open(fx.Build());
        return ModuleModelBuilder.Build(a, new DiagnosticLog());
    }

    private static void Add(GameState s, string id, string name, string map, double? fuel = null, double? ammo = null)
    {
        s.Pieces[id] = new PieceInstance
        {
            Id = id, MapId = map, TraitStates = new() { "" },
            TypeString = SequenceEncoder.Encode(new[] { "piece;;;unit.svg;" + name }, '\t'),
        };
        var l = new OrderedDictionary<string, double>();
        if (fuel != null) l["fuel"] = fuel.Value;
        if (ammo != null) l["ammo"] = ammo.Value;
        if (l.Count > 0) s.Ledgers[LedgerKey.Piece(id)] = l;
    }

    private static RulesPack Pack(bool withColumns = true) => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Outliner = withColumns ? OutlinerAggregate.Sum : OutlinerAggregate.None },
            new PropertyDef { Id = "ammo", Name = "Ammo", Scope = PropertyScope.Pieces, Outliner = withColumns ? OutlinerAggregate.Min : OutlinerAggregate.None },
            new PropertyDef { Id = "mor", Name = "Morale", Scope = PropertyScope.Pieces, Outliner = withColumns ? OutlinerAggregate.Average : OutlinerAggregate.None },
        },
        Sides = new() { new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" } },
    };

    private static GameState State()
    {
        var s = new GameState();
        Add(s, "a", "Red One", "Main Map", 10, 5);
        Add(s, "b", "Red Two", "Org A", 20, 3);
        Add(s, "c", "Blue One", "Org A", 30);
        Add(s, "d", "Blue Two", "Org B");
        s.Ledgers[LedgerKey.Piece("a")]["mor"] = 2;
        s.Ledgers[LedgerKey.Piece("b")]["mor"] = 4;
        return s;
    }

    [Fact]
    public void Outliner_OrgDisplays_FoldersGroupsPieces()
    {
        var m = Model();
        Assert.Contains(m.Maps, x => x.IsOrgDisplay && x.Name == "Org A");
        var t = OutlinerModel.Build(State(), Pack(), m, OutlinerGrouping.OrgDisplays, new List<AlertHit>());
        Assert.Equal(new[] { "Ammo", "Fuel", "Morale" }, t.Columns.Select(c => c.Name));
        Assert.Equal(2, t.Roots.Count);
        var folder = t.Roots[0];
        Assert.Equal(OutlinerNodeKind.Folder, folder.Kind);
        Assert.Equal("Charts", folder.Name);
        Assert.Equal(3, folder.Units);
        var tab = folder.Children.Single();
        Assert.Equal("Tab", tab.Name);
        var groups = tab.Children;
        Assert.Equal(new[] { "Org A", "Org B" }, groups.Select(g => g.Name));
        Assert.All(groups, g => Assert.Equal(OutlinerNodeKind.Group, g.Kind));
        Assert.Equal(new[] { "Blue One", "Red Two" }, groups[0].Children.Select(c => c.Name));
        Assert.Equal(OutlinerNodeKind.Piece, groups[0].Children[0].Kind);
        Assert.Equal("c", groups[0].Children[0].PieceId);
        Assert.Equal("Org A", groups[0].Children[0].MapId);
        Assert.Equal(2, groups[0].Units);
        var main = t.Roots[1];
        Assert.Equal("Main Map", main.Name);
        Assert.Equal(OutlinerNodeKind.Group, main.Kind);
        Assert.Equal("Red One", main.Children.Single().Name);
    }

    [Fact]
    public void Outliner_BySide_WithOther()
    {
        var t = OutlinerModel.Build(State(), Pack(), Model(), OutlinerGrouping.BySide, new List<AlertHit>());
        Assert.Equal(new[] { "Red", "Other" }, t.Roots.Select(r => r.Name));
        Assert.Equal(new[] { "Red One", "Red Two" }, t.Roots[0].Children.Select(c => c.Name));
        Assert.Equal(new[] { "Blue One", "Blue Two" }, t.Roots[1].Children.Select(c => c.Name));
        var none = OutlinerModel.Build(State(), null, Model(), OutlinerGrouping.BySide, new List<AlertHit>());
        Assert.Equal(new[] { "Other" }, none.Roots.Select(r => r.Name));
        Assert.Empty(none.Columns);
    }

    [Fact]
    public void Outliner_Aggregates_SumMinAverage()
    {
        var t = OutlinerModel.Build(State(), Pack(), Model(), OutlinerGrouping.BySide, new List<AlertHit>());
        var red = t.Roots[0];
        Assert.Equal(new double?[] { 3, 30, 3 }, red.Values);
        Assert.Equal(new double?[] { null, 30, null }, t.Roots[1].Values);
        Assert.Equal(new double?[] { 5, 10, 2 }, red.Children[0].Values);
    }

    [Fact]
    public void Outliner_AlertSeverities_Bubble()
    {
        var hits = new List<AlertHit>
        {
            new("x", "X", AlertSeverity.Warning, "", new List<AlertMatch> { new("a", null, "Red One"), new("zz", null, "Gone") }, null),
            new("y", "Y", AlertSeverity.Critical, "", new List<AlertMatch> { new("b", null, "Red Two") }, null),
            new("w", "W", AlertSeverity.Warning, "", new List<AlertMatch> { new("b", null, "Red Two") }, null),
        };
        var t = OutlinerModel.Build(State(), Pack(), Model(), OutlinerGrouping.BySide, hits);
        var red = t.Roots[0];
        Assert.Equal(new[] { AlertSeverity.Critical, AlertSeverity.Warning }, red.Alerts);
        Assert.Equal(new[] { AlertSeverity.Warning }, red.Children[0].Alerts);
        Assert.Equal(new[] { AlertSeverity.Critical, AlertSeverity.Warning }, red.Children[1].Alerts);
        Assert.Empty(t.Roots[1].Alerts);
    }

    [Fact]
    public void Outliner_NoOutlinerProperties_NoColumns()
    {
        var t = OutlinerModel.Build(State(), Pack(false), Model(), OutlinerGrouping.BySide, new List<AlertHit>());
        Assert.Empty(t.Columns);
        Assert.Empty(t.Roots[0].Values);
        Assert.Equal(2, t.Roots[0].Units);
    }
}
