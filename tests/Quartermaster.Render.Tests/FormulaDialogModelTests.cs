using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class FormulaDialogModelTests
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
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Default = 0 },
            new PropertyDef { Id = "cost", Name = "Cost", Scope = PropertyScope.Holders, Default = 3 },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f1", Name = "Burn", Text = "MP * Cost",
                Bindings = new()
                {
                    new VariableBinding { Variable = "MP", Kind = BindingKind.Input, Min = 0, Max = 20 },
                    new VariableBinding { Variable = "Cost", Kind = BindingKind.Global, Property = "cost" },
                },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Subtract } },
            },
            new FormulaDef
            {
                Id = "f2", Name = "Dice", Text = "R + MP",
                Bindings = new()
                {
                    new VariableBinding { Variable = "MP", Kind = BindingKind.Input },
                    new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6 },
                },
            },
        },
    };

    private static GameState State(double fuel = 10)
    {
        var s = new GameState();
        s.Pieces["u1"] = new PieceInstance { Id = "u1", TypeString = "piece;;;unit.svg;Rifle", MapId = "Main Map", Pos = new PointD(60, 40), TraitStates = new() { "" } };
        s.Ledgers[LedgerKey.Piece("u1")] = new() { ["fuel"] = fuel };
        return s;
    }

    private static FormulaDialogModel Make(string id, Func<int, int, IReadOnlyList<int>>? roller = null, bool nopiece = false)
    {
        var runner = new FormulaRunner(Pack(), LazyModel.Value, roller ?? ((n, _) => Enumerable.Repeat(1, n).ToList()));
        return new FormulaDialogModel(runner, Pack().Formulas.First(f => f.Id == id), nopiece ? null : "u1");
    }

    [Fact]
    public void AutoBoxShowsValueAndTypedBoxIsEmptyWithReasonTooltip()
    {
        var m = Make("f1");
        var v = m.Evaluate(State());
        var cost = v.Boxes.Single(b => b.Variable == "Cost");
        var mp = v.Boxes.Single(b => b.Variable == "MP");
        Assert.Equal("3", cost.Text);
        Assert.Equal(BoxBackground.Auto, cost.Background);
        Assert.Equal("", cost.CaptionSuffix);
        Assert.Equal("", mp.Text);
        Assert.Equal(BoxBackground.Typed, mp.Background);
        Assert.Equal("Waiting for input", mp.Tooltip);
        Assert.Equal("Waiting for: MP", v.Status);
        Assert.False(v.CanApply);
    }

    [Fact]
    public void FilledInputsGiveResultStatusAndApply()
    {
        var m = Make("f1");
        m.SetBoxText("MP", "4");
        var v = m.Evaluate(State());
        Assert.Equal("12", v.ResultText);
        Assert.Equal("All inputs filled", v.Status);
        Assert.True(v.CanApply);
        Assert.Contains("Fuel 10 − 12 = -2", v.WorkingText);
    }

    [Fact]
    public void NegativeAfterIsFlagged()
    {
        var m = Make("f1");
        m.SetBoxText("MP", "4");
        var v = m.Evaluate(State());
        Assert.True(v.WorkingNegative);
        Assert.EndsWith("(negative)", v.WorkingText);
    }

    [Fact]
    public void NonNegativeAfterIsNotFlagged()
    {
        var m = Make("f1");
        m.SetBoxText("MP", "2");
        var v = m.Evaluate(State(10));
        Assert.False(v.WorkingNegative);
        Assert.DoesNotContain("(negative)", v.WorkingText);
    }

    [Fact]
    public void OverrideMarksBoxAndClearingRestoresAuto()
    {
        var m = Make("f1");
        m.SetBoxText("MP", "2");
        m.SetBoxText("Cost", "5");
        var v = m.Evaluate(State());
        var cost = v.Boxes.Single(b => b.Variable == "Cost");
        Assert.Equal("5", cost.Text);
        Assert.Equal(BoxBackground.Typed, cost.Background);
        Assert.Equal(" (override)", cost.CaptionSuffix);
        Assert.Equal("10", v.ResultText);

        m.SetBoxText("Cost", "");
        v = m.Evaluate(State());
        cost = v.Boxes.Single(b => b.Variable == "Cost");
        Assert.Equal("3", cost.Text);
        Assert.Equal(BoxBackground.Auto, cost.Background);
        Assert.Equal("", cost.CaptionSuffix);
        Assert.Equal("6", v.ResultText);
    }

    [Fact]
    public void ErrorShowsErrorTextAndDisablesApply()
    {
        var m = Make("f1");
        m.SetBoxText("MP", "4");
        m.SetBoxText("Cost", "abc");
        var v = m.Evaluate(State());
        Assert.False(v.CanApply);
        Assert.Equal("Waiting for: Cost", v.Status);
        Assert.Equal("Not a number", v.Boxes.Single(b => b.Variable == "Cost").Tooltip);

        var noPiece = Make("f1", nopiece: true);
        noPiece.SetBoxText("MP", "1");
        var e = noPiece.Evaluate(State());
        Assert.False(e.CanApply);
        Assert.Equal("This formula needs a unit", e.Status);
    }

    [Fact]
    public void RollsAreFedBackSoDiceStayStableUntilReroll()
    {
        var calls = 0;
        var m = Make("f2", (n, _) => { calls++; return Enumerable.Repeat(calls, n).ToList(); });
        m.SetBoxText("MP", "1");
        var a = m.Evaluate(State());
        var b = m.Evaluate(State());
        var box = a.Boxes.Single(x => x.Variable == "R");
        Assert.Equal("2 (1+1)", box.Text);
        Assert.Equal("R", box.RerollKey);
        Assert.Equal(box.Text, b.Boxes.Single(x => x.Variable == "R").Text);
        m.Reroll("R");
        var c = m.Evaluate(State());
        Assert.Equal("4 (2+2)", c.Boxes.Single(x => x.Variable == "R").Text);
    }

    [Fact]
    public void PhysicalDiceTurnsRollBoxIntoTypedBox()
    {
        var m = Make("f2");
        m.PhysicalDice = true;
        m.SetBoxText("MP", "1");
        var v = m.Evaluate(State());
        var r = v.Boxes.Single(x => x.Variable == "R");
        Assert.Equal("", r.Text);
        Assert.Equal(BoxBackground.Typed, r.Background);
        Assert.Null(r.RerollKey);
        Assert.False(v.CanApply);
        m.SetBoxText("R", "9");
        v = m.Evaluate(State());
        Assert.Equal("10", v.ResultText);
        Assert.True(v.CanApply);
    }
}

public class FormulaDialogModelPhysicalToggleTests
{
    [Fact]
    public void TogglingPhysicalDiceClearsRollOverridesBothWays()
    {
        var pack = new RulesPack
        {
            Formulas = new()
            {
                new FormulaDef
                {
                    Id = "f2", Name = "Dice", Text = "R + 1",
                    Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6 } },
                },
            },
        };
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile()).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var arch = VmodArchive.Open(fx.Build());
        var model = ModuleModelBuilder.Build(arch, new DiagnosticLog());
        var m = new FormulaDialogModel(new FormulaRunner(pack, model, (n, _) => Enumerable.Repeat(2, n).ToList()), pack.Formulas[0], null);
        var s = new GameState();
        m.PhysicalDice = true;
        m.SetBoxText("R", "9");
        Assert.Equal("10", m.Evaluate(s).ResultText);
        m.PhysicalDice = false;
        var v = m.Evaluate(s);
        Assert.Equal("4 (2+2)", v.Boxes.Single().Text);
        m.PhysicalDice = true;
        Assert.Equal("", m.Evaluate(s).Boxes.Single().Text);
    }
}
