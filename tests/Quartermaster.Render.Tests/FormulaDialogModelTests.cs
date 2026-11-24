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
            new FormulaDef
            {
                Id = "f3", Name = "Inline", Text = "dice(1, 6) + MP",
                Bindings = new() { new VariableBinding { Variable = "MP", Kind = BindingKind.Input } },
            },
            new FormulaDef { Id = "f4", Name = "Miss", Text = "dice(1, 6) + lookup(NoSuchTable, 1)" },
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
    public void Always_ask_input_shows_suffix_and_calculate_leaves_it_empty()
    {
        var pack = Pack();
        var f = pack.Formulas.First(x => x.Id == "f1");
        f = f with { Bindings = f.Bindings.Select(b => b.Variable == "MP" ? b with { AutoValue = "5", AlwaysAsk = true } : b).ToList() };
        var m = new FormulaDialogModel(new FormulaRunner(pack, LazyModel.Value, (n, _) => Enumerable.Repeat(1, n).ToList()), f, "u1");
        var state = State();
        m.Calculate(state);
        var v = m.Evaluate(state);
        var mp = v.Boxes.Single(b => b.Variable == "MP");
        Assert.Equal(FormulaDialogModel.AlwaysAskedSuffix, mp.CaptionSuffix);
        Assert.Equal("", mp.Text);
        Assert.Equal("Waiting for: MP", v.Status);
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
    public void ClearingATypedInputKeepsTheInlineDicePin()
    {
        var calls = 0;
        var m = Make("f3", (n, _) => { calls++; return Enumerable.Repeat(calls, n).ToList(); });
        m.SetBoxText("MP", "1");
        Assert.Equal("2", m.Evaluate(State()).ResultText);
        m.SetBoxText("MP", "");
        Assert.False(m.Evaluate(State()).CanApply);
        m.SetBoxText("MP", "5");
        Assert.Equal("6", m.Evaluate(State()).ResultText);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ErrorRunKeepsPins()
    {
        var calls = 0;
        var m = Make("f4", (n, _) => { calls++; return Enumerable.Repeat(calls, n).ToList(); });
        var a = m.Run(State());
        Assert.NotNull(a.Error);
        Assert.True(a.RollsByKey.ContainsKey("dice#0"));
        m.Run(State());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RerollStillRemovesItsKey()
    {
        var calls = 0;
        var m = Make("f3", (n, _) => { calls++; return Enumerable.Repeat(calls, n).ToList(); });
        m.SetBoxText("MP", "1");
        m.Evaluate(State());
        m.Reroll("dice#0");
        Assert.Equal("3", m.Evaluate(State()).ResultText);
        Assert.Equal(2, calls);
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
    [Fact]
    public void PhysicalRollBoxHintsTypingEachDieWhenMoreThanOneDie()
    {
        FormulaDialogModel Build(VariableBinding b)
        {
            var pack = new RulesPack { Formulas = new() { new FormulaDef { Id = "fh", Name = "H", Text = "R", Bindings = new() { b } } } };
            using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile()).AddImage("board.png", 200, 100).AddSvg("unit.svg");
            using var arch = VmodArchive.Open(fx.Build());
            var model = ModuleModelBuilder.Build(arch, new DiagnosticLog());
            return new FormulaDialogModel(new FormulaRunner(pack, model, (_, _) => throw new InvalidOperationException("rolled")), pack.Formulas[0], null) { PhysicalDice = true };
        }
        Assert.Equal("Type each die, separated by spaces", Build(new() { Variable = "R", Kind = BindingKind.Roll, DiceCount = 4, DiceSides = 6 }).Evaluate(new GameState()).Boxes.Single().Hint);
        Assert.Null(Build(new() { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 }).Evaluate(new GameState()).Boxes.Single().Hint);
        Assert.Equal("Type each die, separated by spaces", Build(new() { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6, DiceCountExpr = "3" }).Evaluate(new GameState()).Boxes.Single().Hint);
    }
    // ---- Piece picks follow the picked piece (invented "Rifle" pieces) ----

    private static readonly RulesPack PickPack = new()
    {
        Properties = new() { new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Default = 0 } },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "fp", Name = "Pick", Text = "P",
                Bindings = new() { new VariableBinding { Variable = "P", Kind = BindingKind.Piece, Property = "fuel" } },
            },
        },
    };

    /// <summary>Two "Rifle" pieces on the same spot (a1 then a2 in state order: a2 is labelled " #2"), fuel 1 and 2.</summary>
    private static GameState TwoRifles()
    {
        var s = new GameState();
        foreach (var (id, fuel) in new[] { ("a1", 1.0), ("a2", 2.0) })
        {
            s.Pieces[id] = new PieceInstance { Id = id, TypeString = "piece;;;unit.svg;Rifle", MapId = "Main Map", Pos = new PointD(60, 40), TraitStates = new() { "" } };
            s.Ledgers[LedgerKey.Piece(id)] = new() { ["fuel"] = fuel };
        }
        return s;
    }

    private static FormulaDialogModel MakePick() =>
        new(new FormulaRunner(PickPack, LazyModel.Value, (n, _) => Enumerable.Repeat(1, n).ToList()), PickPack.Formulas[0], null);

    private static string LabelOfPiece(GameState s, string pieceId) =>
        Quartermaster.Core.PieceChoices.For(s, PickPack, LazyModel.Value, PickPack.Formulas[0].Bindings[0], null).Single(c => c.PieceId == pieceId).Label;

    [Fact]
    public void Pick_follows_piece_when_earlier_duplicate_removed()
    {
        var s = TwoRifles();
        var m = MakePick();
        m.Evaluate(s);
        var second = LabelOfPiece(s, "a2");
        Assert.EndsWith(" #2", second);
        m.SetBoxText("P", second);
        Assert.Equal("2", m.Evaluate(s).ResultText);

        s.Pieces.Remove("a1");   // the earlier duplicate leaves: a2 now carries the plain label
        var v = m.Evaluate(s);
        var plain = LabelOfPiece(s, "a2");
        Assert.DoesNotContain("#", plain);
        Assert.Equal(plain, v.Boxes.Single(b => b.Variable == "P").Text);
        Assert.Equal("2", v.ResultText);
        Assert.Equal(plain, m.Intent(null, false).Typed["P"]);
    }

    [Fact]
    public void Pick_cleared_when_picked_piece_leaves_candidates()
    {
        var s = TwoRifles();
        var m = MakePick();
        m.Evaluate(s);
        m.SetBoxText("P", LabelOfPiece(s, "a1"));
        Assert.Equal("1", m.Evaluate(s).ResultText);

        s.Pieces.Remove("a1");   // the picked piece leaves: a2 now has a1's old plain label, but the pick must not move to it
        var v = m.Evaluate(s);
        var box = v.Boxes.Single(b => b.Variable == "P");
        Assert.Equal("", box.Text);
        Assert.Equal("Waiting for input", box.Tooltip);
        Assert.Equal("Waiting for: P", v.Status);
        Assert.Equal("", v.ResultText);
        Assert.Null(v.Trace.Result);
        Assert.False(m.Intent(null, false).Typed.ContainsKey("P"));
    }

    [Fact]
    public void Pick_typed_before_first_run_is_tracked()
    {
        var s = TwoRifles();
        var m = MakePick();
        m.SetBoxText("P", LabelOfPiece(s, "a1"));   // no run yet: adopted against the first run's list
        Assert.Equal("1", m.Evaluate(s).ResultText);
        s.Pieces.Remove("a1");
        Assert.Equal("", m.Evaluate(s).Boxes.Single(b => b.Variable == "P").Text);
    }

    [Fact]
    public void Pick_cleared_when_pick_sides_change()
    {
        var s = TwoRifles();
        var m = MakePick();
        m.PickSides = new HashSet<string> { "blue" };
        m.Evaluate(s);
        m.SetBoxText("P", LabelOfPiece(s, "a1"));
        m.PickSides = new HashSet<string> { "blue" };   // the same sides (another set object): kept
        Assert.Equal(LabelOfPiece(s, "a1"), (m.Intent(null, false).Typed.GetValueOrDefault("P") ?? ""));
        m.PickSides = new HashSet<string> { "red" };    // another viewer: the previous viewer's pick is dropped
        Assert.Equal("", (m.Intent(null, false).Typed.GetValueOrDefault("P") ?? ""));
        m.SetBoxText("P", LabelOfPiece(s, "a1"));
        m.PickSides = null;
        Assert.Equal("", (m.Intent(null, false).Typed.GetValueOrDefault("P") ?? ""));
    }
}

public class FormulaDialogModelPhysicalToggleTests
{
    [Fact]
    public void PhysicalDiceTypedOneByOneFeedCount()
    {
        var pack = new RulesPack
        {
            Formulas = new()
            {
                new FormulaDef
                {
                    Id = "f3", Name = "Crash", Text = "count(R, 1, 2) * 10 + count(R, 3, 3)",
                    Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 4, DiceSides = 6 } },
                },
            },
        };
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile()).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var arch = VmodArchive.Open(fx.Build());
        var model = ModuleModelBuilder.Build(arch, new DiagnosticLog());
        var m = new FormulaDialogModel(new FormulaRunner(pack, model, (_, _) => throw new InvalidOperationException("rolled")), pack.Formulas[0], null);
        m.PhysicalDice = true;
        m.SetBoxText("R", "1 2 3 6");
        var v = m.Evaluate(new GameState());
        Assert.Equal("21", v.ResultText);
        Assert.True(v.CanApply);
    }

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

    [Fact]
    public void OverridingASingleDieRollInNormalModeKeepsTheRealRollAndLogsNothing()
    {
        var pack = new RulesPack
        {
            Formulas = new()
            {
                new FormulaDef
                {
                    Id = "f2", Name = "Dice", Text = "R + 1",
                    Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 } },
                },
            },
        };
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile()).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var arch = VmodArchive.Open(fx.Build());
        var model = ModuleModelBuilder.Build(arch, new DiagnosticLog());
        var m = new FormulaDialogModel(new FormulaRunner(pack, model, (n, _) => Enumerable.Repeat(3, n).ToList()), pack.Formulas[0], null);
        var s = new GameState();
        Assert.Equal("4", m.Evaluate(s).ResultText);
        Assert.Equal(new[] { 3 }, m.PinnedRolls["R"]);

        m.SetBoxText("R", "4");
        var trace = m.Run(s);
        Assert.Equal(5, trace.Result!.Value.Number);
        Assert.Empty(trace.Rolls);
        Assert.Equal(new[] { 3 }, m.PinnedRolls["R"]);

        m.SetBoxText("R", "");
        var v = m.Evaluate(s);
        Assert.Equal("4", v.ResultText);
        Assert.Equal("3", v.Boxes.Single().Text.Split(' ')[0]);
        Assert.Equal(new[] { 3 }, m.PinnedRolls["R"]);
    }

}
