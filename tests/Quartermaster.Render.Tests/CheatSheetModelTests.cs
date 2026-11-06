using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Render.Tests;

public class CheatSheetModelTests
{
    [Fact]
    public void CheatSheet_TableRowHighlighted_DiceNotShown()
    {
        var table = new TableDef { Id = "t1", Name = "TestTerrainCost", Kind = TableKind.TwoD, RowKeys = new() { "Sand", "Rock" },
            ColumnKeys = new() { "Foot", "Wheel" }, Cells = new() { new() { "1", "2" }, new() { "3", "4" } } };
        var pack = new RulesPack { Tables = new() { table } };
        var f = new FormulaDef
        {
            Id = "f", Name = "F", Text = "R + Cost + T",
            Bindings = new()
            {
                new VariableBinding { Variable = "T", Kind = BindingKind.Input },
                new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6 },
                new VariableBinding { Variable = "Cost", Kind = BindingKind.Table, TableId = "t1" },
            },
        };
        var vars = new List<VariableState>
        {
            new("T", Value.Of(4), false, false, null),
            new("R", Value.Of(9), true, false, null),
            new("Cost", Value.Of(4), true, false, null, "TestTerrainCost, row Rock, column Wheel", new TableCell("t1", 1, 1)),
        };
        var trace = new FormulaTrace(f, vars, Value.Of(17), "", Array.Empty<OutputPreview>(), null, Array.Empty<DiceEntry>(),
            new Dictionary<string, IReadOnlyList<int>>());

        var rows = CheatSheetModel.Build(trace, pack);

        Assert.Equal(3, rows.Count);
        Assert.Equal("typed by you", rows[0].ValueText);
        Assert.Null(rows[0].Table);
        Assert.Equal("2d6 — roll it", rows[1].ValueText);
        Assert.DoesNotContain("9", rows[1].ValueText);
        Assert.Equal("4", rows[2].ValueText);
        Assert.Same(table, rows[2].Table);
        Assert.Equal(1, rows[2].HighlightRow);
        Assert.Equal(1, rows[2].HighlightCol);
        Assert.Equal("TestTerrainCost, row Rock, column Wheel", rows[2].Source);
    }
}
