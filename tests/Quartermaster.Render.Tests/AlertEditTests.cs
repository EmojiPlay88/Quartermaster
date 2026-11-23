using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class AlertEditTests
{
    private static RulesPack Pack(params string[] ids) => new()
    {
        Properties = new() { new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces } },
        Sides = new() { new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" } },
        Alerts = ids.Select(i => new AlertDef { Id = i, Name = "Alert " + i }).ToList(),
    };

    private static GameState State()
    {
        var s = new GameState();
        foreach (var (id, name, fuel) in new[] { ("a", "Red One", 5.0), ("b", "Blue One", 3.0), ("c", "Red Two", 50.0) })
        {
            s.Pieces[id] = new PieceInstance
            {
                Id = id, MapId = "Main Map", TraitStates = new() { "" },
                TypeString = SequenceEncoder.Encode(new[] { "piece;;;unit.svg;" + name }, '\t'),
            };
            s.Ledgers[LedgerKey.Piece(id)] = new() { ["fuel"] = fuel };
        }
        return s;
    }

    [Fact]
    public void Add_Remove_Move_Update()
    {
        var pack = Pack("a", "b", "c");
        var p1 = AlertEdit.Add(pack, 1);
        Assert.Equal(4, p1.Alerts.Count);
        Assert.Equal("New alert", p1.Alerts[1].Name);
        Assert.Equal(32, p1.Alerts[1].Id.Length);
        Assert.Equal(new[] { "a", "c" }, AlertEdit.Remove(pack, 1).Alerts.Select(x => x.Id).ToArray());
        Assert.Equal(new[] { "b", "a", "c" }, AlertEdit.Move(pack, 0, 1).Alerts.Select(x => x.Id).ToArray());
        Assert.Same(pack, AlertEdit.Move(pack, 0, -1));
        Assert.Same(pack, AlertEdit.Remove(pack, 9));
        var up = AlertEdit.Update(pack, "b", a => a with { Name = "Z" });
        Assert.Equal("Z", up.Alerts[1].Name);
        Assert.Equal("Alert b", pack.Alerts[1].Name);
    }

    [Fact]
    public void MatchStatus_Counts_Errors_AndNoGame()
    {
        var s = State();
        var pack = Pack();
        Assert.Equal("no game open", AlertEdit.MatchStatus(new AlertDef { Condition = "Fuel < 20" }, null, pack));
        Assert.Equal("3 matches now", AlertEdit.MatchStatus(new AlertDef { Condition = "Fuel < 60" }, s, pack));
        Assert.Equal("1 match now", AlertEdit.MatchStatus(new AlertDef { Condition = "Fuel > 20" }, s, pack));
        Assert.Equal("0 matches now", AlertEdit.MatchStatus(new AlertDef { Condition = "Fuel > 500" }, s, pack));
        Assert.Equal("1 match now", AlertEdit.MatchStatus(new AlertDef { Condition = "Fuel < 20", SideId = "red" }, s, pack));
        Assert.Equal("no condition", AlertEdit.MatchStatus(new AlertDef { Condition = " " }, s, pack));
        var bad = AlertEdit.MatchStatus(new AlertDef { Condition = "Fuel <" }, s, pack);
        Assert.StartsWith("Does not parse: ", bad);
    }

    [Fact]
    public void MatchStatus_GameLevelCondition_CountsOnce()
    {
        // Weather is a global value: the condition never reads a unit, so the alert shows once, not once per unit.
        var pack = Pack() with
        {
            Properties = Pack().Properties.Append(new PropertyDef { Id = "weather", Name = "Weather", Scope = PropertyScope.Holders }).ToList(),
        };
        var s = State();
        s.Ledgers[LedgerKey.Holder(HolderDef.GlobalId)] = new() { ["weather"] = 2 };
        Assert.Equal("1 match now", AlertEdit.MatchStatus(new AlertDef { Condition = "Weather == 2" }, s, pack));
        Assert.Equal("0 matches now", AlertEdit.MatchStatus(new AlertDef { Condition = "Weather == 3" }, s, pack));
        Assert.Equal("1 match now", AlertEdit.MatchStatus(new AlertDef { Condition = "Weather == 2", SideId = "red" }, s, pack));   // no side filter
        Assert.Equal("0 matches now", AlertEdit.MatchStatus(new AlertDef { Condition = "1 / 0 > 0" }, s, pack));   // an error matches nothing
        Assert.Equal("3 matches now", AlertEdit.MatchStatus(new AlertDef { Condition = "Weather == 2 and Fuel > 0" }, s, pack));   // reads the unit
    }

    private static readonly Lazy<ModuleModel> GridModel = new(() =>
    {
        const string P = "VASSAL.build.module.map.boardPicker.board.";
        var xml = Quartermaster.TestFixtures.FixtureModule.MinimalBuildFile().Replace("y0=\"25\"/>",
            $"y0=\"25\"><{P}mapgrid.SquareGridNumbering first=\"H\" hType=\"N\" vType=\"N\" sep=\"\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"/></{P}SquareGrid>");
        using var fx = new Quartermaster.TestFixtures.FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var arch = VmodArchive.Open(fx.Build());
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    });

    [Fact]
    public void MatchStatus_HexAggregate_UsesTheModel()
    {
        var s = State();   // the three units share one square
        s.Pieces["c"].Pos = new PointD(160, 40);
        Assert.Equal("2 matches now", AlertEdit.MatchStatus(new AlertDef { Condition = "hexcount() > 1" }, s, Pack(), GridModel.Value));
    }
}
