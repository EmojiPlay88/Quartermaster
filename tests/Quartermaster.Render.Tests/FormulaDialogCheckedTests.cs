using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class FormulaDialogCheckedTests
{
    private static readonly Lazy<ModuleModel> LazyModel = new(() =>
    {
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile()).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var arch = VmodArchive.Open(fx.Build());
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    });

    private static RulesPack Pack() => new()
    {
        Properties = new() { new PropertyDef { Id = "bonus", Name = "Bonus", Scope = PropertyScope.Holders, Default = 3 } },
        Tables = new()
        {
            new TableDef { Id = "t1", Name = "TestTerrainCost", Kind = TableKind.OneD, RowKeys = new() { "Sand", "Swamp" },
                ColumnKeys = new() { "Value" }, Cells = new() { new() { "2" }, new() { "-2" } } },
            new TableDef { Id = "t2", Name = "TestTerrainLabel", Kind = TableKind.OneD, RowKeys = new() { "Sand", "Swamp" },
                ColumnKeys = new() { "Value" }, Cells = new() { new() { "Hill Fort" }, new() { "Bog" } } },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f1", Name = "Cost", Text = "Cost + Bonus",
                Bindings = new()
                {
                    new VariableBinding { Variable = "Terrain", Kind = BindingKind.Input, Choices = new() { "Sand", "Swamp" } },
                    new VariableBinding { Variable = "Cost", Kind = BindingKind.Table, TableId = "t1", RowExpr = "Terrain" },
                    new VariableBinding { Variable = "Bonus", Kind = BindingKind.Global, Property = "bonus" },
                },
            },
            new FormulaDef
            {
                Id = "f2", Name = "Label", Text = "Cost + if(Label == \"zzz\", 1, 0)",
                Bindings = new()
                {
                    new VariableBinding { Variable = "Terrain", Kind = BindingKind.Input, Choices = new() { "Sand", "Swamp" } },
                    new VariableBinding { Variable = "Cost", Kind = BindingKind.Table, TableId = "t1", RowExpr = "Terrain" },
                    new VariableBinding { Variable = "Label", Kind = BindingKind.Table, TableId = "t2", RowExpr = "Terrain" },
                },
            },
            new FormulaDef
            {
                Id = "f3", Name = "Dice", Text = "R + Cost",
                Bindings = new()
                {
                    new VariableBinding { Variable = "Terrain", Kind = BindingKind.Input, Choices = new() { "Sand", "Swamp" }, AutoValue = "\"Sand\"" },
                    new VariableBinding { Variable = "Cost", Kind = BindingKind.Table, TableId = "t1", RowExpr = "Terrain" },
                    new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 },
                },
            },
        },
    };

    private static int _next;

    private static FormulaDialogModel Make(string id, bool isChecked = true)
    {
        _next = 0;
        var runner = new FormulaRunner(Pack(), LazyModel.Value, (n, _) => Enumerable.Range(0, n).Select(_ => (_next++ % 6) + 1).ToList());
        return new FormulaDialogModel(runner, Pack().Formulas.First(f => f.Id == id), null) { Checked = isChecked };
    }

    [Fact]
    public void Checked_BoxesStartEmpty_CannotApply()
    {
        var m = Make("f1");
        m.SetBoxText("Terrain", "Sand");
        var v = m.Evaluate(new GameState());
        Assert.Equal("", v.Boxes.Single(b => b.Variable == "Cost").Text);
        Assert.Equal("", v.Boxes.Single(b => b.Variable == "Bonus").Text);
        Assert.All(v.Boxes, b => Assert.False(b.Wrong));
        Assert.False(v.CanApply);
        Assert.NotNull(m.LastExpected);
    }

    [Fact]
    public void Checked_WrongValue_RedTooltipStatus_ApplyDisabled()
    {
        var m = Make("f1");
        m.SetBoxText("Terrain", "Sand");
        m.SetBoxText("Bonus", "3");
        m.SetBoxText("Cost", "9");
        var v = m.Evaluate(new GameState());
        var cost = v.Boxes.Single(b => b.Variable == "Cost");
        Assert.True(cost.Wrong);
        Assert.Equal("Doesn't match (TestTerrainCost, row Sand)", cost.Tooltip);
        Assert.False(v.Boxes.Single(b => b.Variable == "Bonus").Wrong);
        Assert.Equal("Doesn't match: Cost", v.Status);
        Assert.False(v.CanApply);
    }

    [Fact]
    public void Checked_CorrectValues_CanApply()
    {
        var m = Make("f1");
        m.SetBoxText("Terrain", "Sand");
        m.SetBoxText("Bonus", "3");
        m.SetBoxText("Cost", "2");
        var v = m.Evaluate(new GameState());
        Assert.All(v.Boxes, b => Assert.False(b.Wrong));
        Assert.Equal("5", v.ResultText);
        Assert.True(v.CanApply);
        // Clearing a box makes it missing again.
        m.SetBoxText("Cost", "");
        Assert.False(m.Evaluate(new GameState()).CanApply);
    }

    [Fact]
    public void Checked_TypedFormats_CompareInvariant()
    {
        var m = Make("f2");
        m.SetBoxText("Terrain", " sand ");
        m.SetBoxText("Cost", "2.0");
        m.SetBoxText("Label", " hill   FORT ");
        var v = m.Evaluate(new GameState());
        Assert.All(v.Boxes, b => Assert.False(b.Wrong));
        Assert.True(v.CanApply);

        m.SetBoxText("Cost", "2,0");
        v = m.Evaluate(new GameState());
        Assert.True(v.Boxes.Single(b => b.Variable == "Cost").Wrong);
        Assert.Equal("Not a number", v.Boxes.Single(b => b.Variable == "Cost").Tooltip);
        Assert.False(v.CanApply);

        m.SetBoxText("Terrain", "Swamp");
        m.SetBoxText("Cost", "−2");
        m.SetBoxText("Label", "Bog");
        v = m.Evaluate(new GameState());
        Assert.All(v.Boxes, b => Assert.False(b.Wrong));
        Assert.True(v.CanApply);
    }

    [Fact]
    public void Checked_WrongTextBox_Flagged()
    {
        var m = Make("f2");
        m.SetBoxText("Terrain", "Sand");
        m.SetBoxText("Cost", "2");
        m.SetBoxText("Label", "Bog");
        var v = m.Evaluate(new GameState());
        Assert.True(v.Boxes.Single(b => b.Variable == "Label").Wrong);
        Assert.False(v.CanApply);
    }

    [Fact]
    public void Calculate_FillsBoxes_SetsAutoCalculated_PinsDice()
    {
        var m = Make("f3");
        Assert.False(m.AutoCalculated);
        m.Calculate(new GameState());
        Assert.True(m.AutoCalculated);
        var v = m.Evaluate(new GameState());
        Assert.Equal("Sand", v.Boxes.Single(b => b.Variable == "Terrain").Text);
        Assert.Equal("2", v.Boxes.Single(b => b.Variable == "Cost").Text);
        Assert.All(v.Boxes, b => Assert.False(b.Wrong));
        Assert.True(v.CanApply);
        var first = v.ResultText;
        Assert.Equal("3", first); // roller yields 1 first: 1 + 2
        // Dice are pinned: re-evaluating does not roll again.
        Assert.Equal(first, m.Evaluate(new GameState()).ResultText);
    }

    [Fact]
    public void Unchecked_BehavesAsPiece2()
    {
        var m = Make("f1", isChecked: false);
        m.SetBoxText("Terrain", "Sand");
        var v = m.Evaluate(new GameState());
        Assert.Equal("2", v.Boxes.Single(b => b.Variable == "Cost").Text);
        Assert.Equal(BoxBackground.Auto, v.Boxes.Single(b => b.Variable == "Cost").Background);
        Assert.All(v.Boxes, b => Assert.False(b.Wrong));
        Assert.True(v.CanApply);
        Assert.Equal("5", v.ResultText);
    }

    [Fact]
    public void InputBoxes_CarryKeyChoices()
    {
        var m = Make("f1");
        var v = m.Evaluate(new GameState());
        Assert.Equal(new[] { "Sand", "Swamp" }, v.Boxes.Single(b => b.Variable == "Terrain").Choices);
        Assert.Null(v.Boxes.Single(b => b.Variable == "Cost").Choices);
    }
}
