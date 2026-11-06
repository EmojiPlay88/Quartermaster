using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class LedgerTableTests
{
    static GameState Game()
    {
        var s = RunnerFx.State();
        s.Pieces["u2"] = new PieceInstance { Id = "u2", TypeString = "piece;;;unit.svg;Alpha, tank", MapId = "Main Map", TraitStates = new() { "" } };
        s.Pieces["u3"] = new PieceInstance { Id = "u3", TypeString = "piece;;;unit.svg;Bare", MapId = "Main Map", TraitStates = new() { "" } };
        s.Ledgers[LedgerKey.Piece("u2")] = new() { ["fuel"] = -3.5 };
        s.Ledgers[LedgerKey.Holder("depot")] = new() { ["supply"] = 9 };
        return s;
    }

    [Fact]
    public void Rows_HoldersFirst_ThenUnitsWithValues()
    {
        var pack = RunnerFx.Pack();
        var cols = LedgerTable.Columns(pack);
        Assert.Equal(new[] { "Divisor", "Fuel", "Supply" }, cols.Select(c => c.Name));
        var rows = LedgerTable.Rows(Game(), pack, RunnerFx.Model, "");
        Assert.Equal(new[] { "Global", "Depot A", "Alpha, tank", "Rifle" }, rows.Select(r => r.Name));
        Assert.Equal(new[] { "Holder", "Holder", "Unit · Main Map", "Unit · Main Map" }, rows.Select(r => r.Kind));
        Assert.Equal(LedgerKey.Holder("global"), rows[0].Key);
        Assert.Equal(new double?[] { 2, null, null }, rows[0].Values);
        Assert.Equal(new double?[] { null, -3.5, null }, rows[2].Values);
        var all = LedgerTable.Rows(Game(), pack, RunnerFx.Model, "", includeEmpty: true);
        Assert.Contains("Bare", all.Select(r => r.Name));
    }

    [Fact]
    public void Rows_FilterMatchesNameOrKind()
    {
        var pack = RunnerFx.Pack();
        Assert.Equal(new[] { "Alpha, tank" }, LedgerTable.Rows(Game(), pack, RunnerFx.Model, "ALPHA").Select(r => r.Name));
        Assert.Equal(new[] { "Global", "Depot A" }, LedgerTable.Rows(Game(), pack, RunnerFx.Model, "holder").Select(r => r.Name));
        Assert.Equal(2, LedgerTable.Rows(Game(), pack, RunnerFx.Model, "main map").Count);
        Assert.Empty(LedgerTable.Rows(Game(), pack, RunnerFx.Model, "zzz"));
    }

    [Fact]
    public void Csv_QuotesAndInvariantNumbers()
    {
        var cols = new List<PropertyDef> { new() { Id = "a", Name = "Fuel, \"main\"" }, new() { Id = "b", Name = "Ammo" } };
        var rows = new List<LedgerRow> { new("p:1", "Alpha, tank", "Unit · Main", new double?[] { 1234.5678, null }), new("h:x", "Depot", "Holder", new double?[] { -3, 0.5 }) };
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var csv = LedgerTable.ToCsv(cols, rows);
            Assert.Equal("Name,Kind,\"Fuel, \"\"main\"\"\",Ammo\r\n\"Alpha, tank\",Unit · Main,1234.57,\r\nDepot,Holder,-3,0.5\r\n", csv);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void LedgerHistory_DeletedFormula_DescribedAsDeleted()
    {
        var s = new GameState();
        s.Apply(new LedgerDelta("p:u1", "fuel", 100, 73, "formula", "gone", null, null, null));
        var line = Ledger.Describe(Ledger.History(s, "p:u1", "fuel")[0], RunnerFx.Pack());
        Assert.Equal("−27 Fuel ((deleted formula))", line);
        Assert.Contains("(deleted formula)", line);
    }
}
