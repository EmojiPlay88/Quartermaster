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
}
