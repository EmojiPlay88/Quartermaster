using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class InventoryQueryTests
{
    static readonly Lazy<ModuleModel> LazyModel = new(() =>
    {
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile()).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var arch = VmodArchive.Open(fx.Build());
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    });

    static ModuleModel Model => LazyModel.Value;

    static PieceInstance P(string id, string name, string? side = null, string map = "Main Map")
    {
        var types = new List<string>();
        var states = new List<string>();
        if (side != null) { types.Add("mark;" + SequenceEncoder.Encode(new[] { "Side" }, ',')); states.Add(side); }
        types.Add("piece;;;unit.svg;" + name);
        states.Add("");
        return new PieceInstance { Id = id, TypeString = SequenceEncoder.Encode(types, '\t'), MapId = map, TraitStates = states };
    }

    static GameState State(params PieceInstance[] ps)
    {
        var s = new GameState();
        foreach (var p in ps) s.Pieces[p.Id] = p;
        return s;
    }

    [Fact]
    public void Inventory_ByName_Counts()
    {
        var s = State(P("a", "Rifle"), P("b", "rifle"), P("c", "Armor"));
        var rows = InventoryQuery.Run(s, Model, null, InventoryKey.Name, null, "");
        Assert.Equal(new[] { "Armor", "Rifle" }, rows.Select(r => r.Group));
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Count));
        Assert.Equal(new[] { "a", "b" }, rows[1].PieceIds);
        Assert.All(rows, r => Assert.Null(r.Total));
        var byMap = InventoryQuery.Run(s, Model, null, InventoryKey.Map, null, "");
        Assert.Equal(3, byMap.Single().Count);
    }

    [Fact]
    public void Inventory_ByMarker_NoneGroup()
    {
        var s = State(P("a", "Rifle", "Red"), P("b", "Rifle", "red"), P("c", "Armor"), P("d", "Armor", "Blue"));
        var rows = InventoryQuery.Run(s, Model, null, InventoryKey.Marker, "side", "");
        Assert.Equal(new[] { "(none)", "Blue", "Red" }, rows.Select(r => r.Group));
        Assert.Equal(new[] { 1, 1, 2 }, rows.Select(r => r.Count));
        Assert.Equal(new[] { "c" }, rows[0].PieceIds);
    }

    [Fact]
    public void Inventory_ByLedger_Totals()
    {
        var pack = new RulesPack();
        pack.Properties.Add(new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces });
        var s = State(P("a", "Rifle"), P("b", "Rifle"), P("c", "Armor"), P("d", "Armor"));
        s.Ledgers[LedgerKey.Piece("a")] = new() { ["fuel"] = 3 };
        s.Ledgers[LedgerKey.Piece("b")] = new() { ["fuel"] = 4.5 };
        s.Ledgers[LedgerKey.Piece("c")] = new() { ["fuel"] = -1 };
        var rows = InventoryQuery.Run(s, Model, pack, InventoryKey.LedgerProperty, "Fuel", "");
        Assert.Equal(new[] { "Armor", "Rifle" }, rows.Select(r => r.Group));
        Assert.Equal(new double?[] { -1, 7.5 }, rows.Select(r => r.Total));
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Count)); // d has no value: excluded
        Assert.Empty(InventoryQuery.Run(s, Model, pack, InventoryKey.LedgerProperty, "Nope", ""));
        Assert.Empty(InventoryQuery.Run(s, Model, null, InventoryKey.LedgerProperty, "Fuel", ""));
    }

    [Fact]
    public void Inventory_Filter()
    {
        var s = State(P("a", "Rifle"), P("b", "Armor"), P("c", "Rifle Sniper"));
        var rows = InventoryQuery.Run(s, Model, null, InventoryKey.Name, null, "RIFLE");
        Assert.Equal(new[] { "Rifle", "Rifle Sniper" }, rows.Select(r => r.Group));
        Assert.Empty(InventoryQuery.Run(s, Model, null, InventoryKey.Name, null, "zzz"));
        Assert.Equal(3, InventoryQuery.Run(s, Model, null, InventoryKey.Name, null, "  ").Sum(r => r.Count));
    }
}
