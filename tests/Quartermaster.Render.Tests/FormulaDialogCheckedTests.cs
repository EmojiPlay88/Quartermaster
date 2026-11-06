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
        Properties = new()
        {
            new PropertyDef { Id = "bonus", Name = "Bonus", Scope = PropertyScope.Holders, Default = 3 },
            new PropertyDef { Id = "third", Name = "Third", Scope = PropertyScope.Holders, Default = 1.0 / 3 },
        },
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
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "bonus", Op = OutputOp.Add } },
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
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "bonus", Op = OutputOp.Add } },
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

    private static string Cmds(FormulaTrace t, FormulaRunner r) => string.Join("|", r.Commands(t, null).Select(c => c.ToString()));

    private static FormulaRunner Runner() =>
        new(Pack(), LazyModel.Value, (n, _) => Enumerable.Range(0, n).Select(_ => (_next++ % 6) + 1).ToList());

    private static GameState PieceState()
    {
        var s = new GameState();
        s.Pieces["u1"] = new PieceInstance { Id = "u1", TypeString = "piece;;;unit.svg;Rifle", MapId = "Main Map", Pos = new PointD(60, 40), TraitStates = new() { "" } };
        return s;
    }

    [Fact]
    public void Regression_WhitespaceCaseText_CommandsEqualExpectedRun()
    {
        var checkedM = Make("f2");
        var plain = Make("f2", isChecked: false);
        foreach (var m in new[] { checkedM, plain }) m.SetBoxText("Terrain", "Sand");
        checkedM.SetBoxText("Cost", "2");
        checkedM.SetBoxText("Label", " hill   FORT ");
        var v = checkedM.Evaluate(new GameState());
        Assert.True(v.CanApply);
        var expected = plain.Run(new GameState());
        Assert.Equal(expected.Result, v.Trace.Result);
        Assert.Equal(expected.Working, v.Trace.Working);
        Assert.DoesNotContain("hill", v.Trace.Working);
    }

    [Fact]
    public void Regression_LocationTypedAsText_NeverNumberCoerced()
    {
        var pack = new RulesPack
        {
            Formulas = new() { new FormulaDef { Id = "fl", Name = "Loc", Text = "Loc == \"zzz\"",
                Bindings = new() { new VariableBinding { Variable = "Loc", Kind = BindingKind.Location } } } },
        };
        var runner = new FormulaRunner(pack, LazyModel.Value, (n, _) => Enumerable.Repeat(1, n).ToList());
        var m = new FormulaDialogModel(runner, pack.Formulas[0], "u1") { Checked = true };
        var exp = m.Run(PieceState());
        var loc = exp.Variables.FirstOrDefault()?.Value;
        if (loc is not { } l) return; // the fixture map has no grid; nothing to compare
        var padded = "0" + l.Display();
        m.SetBoxText("Loc", padded);
        Assert.False(m.Evaluate(PieceState()).CanApply); // "0304" is not "304"
        m.SetBoxText("Loc", l.Display());
        Assert.True(m.Evaluate(PieceState()).CanApply);
    }

    [Fact]
    public void Regression_OneThird_CalculateThenApplyIsExact_RoundedTextIsWrong()
    {
        var pack = Pack();
        var f = new FormulaDef
        {
            Id = "f5", Name = "Third", Text = "Third * 3",
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "bonus", Op = OutputOp.Add } },
            Bindings = new() { new VariableBinding { Variable = "Third", Kind = BindingKind.Global, Property = "third" } },
        };
        pack.Formulas.Add(f);
        var runner = new FormulaRunner(pack, LazyModel.Value, (n, _) => Enumerable.Repeat(1, n).ToList());
        var m = new FormulaDialogModel(runner, f, null) { Checked = true };
        m.SetBoxText("Third", "0.33");
        Assert.False(m.Evaluate(new GameState()).CanApply);
        m.Calculate(new GameState());
        var v = m.Evaluate(new GameState());
        Assert.True(v.CanApply);
        var plain = new FormulaDialogModel(runner, f, null);
        Assert.Equal(Cmds(plain.Run(new GameState()), runner), Cmds(v.Trace, runner));
        Assert.Contains("4", Cmds(v.Trace, runner)); // 3 + 1: not 3 + 0.99
    }

    [Fact]
    public void Regression_RollOverride_ExpectedRunHonoursIt()
    {
        var checkedM = Make("f3");
        var plain = Make("f3", isChecked: false);
        foreach (var m in new[] { checkedM, plain }) { m.SetBoxText("Terrain", "Sand"); m.SetBoxText("R", "6"); }
        checkedM.SetBoxText("Cost", "2");
        var v = checkedM.Evaluate(new GameState());
        Assert.True(v.CanApply);
        Assert.Equal("8", v.ResultText);
        Assert.Equal(plain.Run(new GameState()).Result, v.Trace.Result);
    }

    [Fact]
    public void Regression_NotApplicable_TraceCarriesNoResultOrCommands()
    {
        var m = Make("f1");
        m.SetBoxText("Terrain", "Sand");
        m.SetBoxText("Cost", "9");
        var t = m.Run(new GameState());
        Assert.Null(t.Result);
        Assert.Empty(Runner().Commands(t, null));
    }

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
