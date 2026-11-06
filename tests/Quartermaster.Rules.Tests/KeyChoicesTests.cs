namespace Quartermaster.Rules.Tests;

public class KeyChoicesTests
{
    private static RulesPack Pack(params string[] rows) => new()
    {
        Tables = new()
        {
            new TableDef { Id = "t1", Name = "TestTerrainCost", Kind = TableKind.TwoD, RowKeys = rows.ToList(),
                ColumnKeys = new() { "Foot", "Wheel", "Foot" },
                Cells = rows.Select(_ => new List<string> { "1", "2", "3" }).ToList() },
        },
    };

    private static FormulaDef F(string text, params VariableBinding[] b) => new() { Id = "f", Name = "F", Text = text, Bindings = b.ToList() };

    [Fact]
    public void KeyChoices_InputChoices()
    {
        var f = F("X", new VariableBinding { Variable = "X", Kind = BindingKind.Input, Choices = new() { "a", "b" } });
        Assert.Equal(new[] { "a", "b" }, KeyChoices.For(Pack("Sand"), f, "X"));
    }

    [Fact]
    public void KeyChoices_BareRowExpr_TableRowKeys()
    {
        var f = F("Cost",
            new VariableBinding { Variable = "Terrain", Kind = BindingKind.Input },
            new VariableBinding { Variable = "Cost", Kind = BindingKind.Table, TableId = "t1", RowExpr = " Terrain ", ColExpr = "Mode" },
            new VariableBinding { Variable = "Mode", Kind = BindingKind.Input });
        Assert.Equal(new[] { "Sand", "Rock" }, KeyChoices.For(Pack("Sand", "Rock", "sand"), f, "Terrain"));
        Assert.Equal(new[] { "Foot", "Wheel" }, KeyChoices.For(Pack("Sand"), f, "Mode"));
    }

    [Fact]
    public void KeyChoices_LookupArgs_RowAndColumn()
    {
        var f = F("lookup(TestTerrainCost, T, M)",
            new VariableBinding { Variable = "T", Kind = BindingKind.Input },
            new VariableBinding { Variable = "M", Kind = BindingKind.Input });
        Assert.Equal(new[] { "Sand", "Rock" }, KeyChoices.For(Pack("Sand", "Rock"), f, "T"));
        Assert.Equal(new[] { "Foot", "Wheel" }, KeyChoices.For(Pack("Sand", "Rock"), f, "M"));
    }

    [Fact]
    public void KeyChoices_RangeKeys_Null()
    {
        var f = F("lookup(TestTerrainCost, T, M)", new VariableBinding { Variable = "T", Kind = BindingKind.Input });
        Assert.Null(KeyChoices.For(Pack("1", "2-3"), f, "T"));
        Assert.Null(KeyChoices.For(Pack("1", "4+"), f, "T"));
        Assert.Null(KeyChoices.For(Pack("1", "<=0"), f, "T"));
    }

    [Fact]
    public void KeyChoices_ComputedExpr_Null()
    {
        var f = F("Cost",
            new VariableBinding { Variable = "Terrain", Kind = BindingKind.Input },
            new VariableBinding { Variable = "Cost", Kind = BindingKind.Table, TableId = "t1", RowExpr = "Terrain + 1", ColExpr = "\"Foot\"" });
        Assert.Null(KeyChoices.For(Pack("Sand"), f, "Terrain"));
    }
}
