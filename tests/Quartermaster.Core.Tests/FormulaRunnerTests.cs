using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>Invented fixtures shared by the runner and filter tests.</summary>
internal static class RunnerFx
{
    private const string P = "VASSAL.build.module.map.boardPicker.board.";

    private static readonly Lazy<ModuleModel> LazyModel = new(() =>
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("y0=\"25\"/>",
            $"y0=\"25\"><{P}mapgrid.SquareGridNumbering first=\"H\" hType=\"N\" vType=\"N\" sep=\"\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"/></{P}SquareGrid>");
        using var fx = new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        var path = fx.Build();
        using var arch = VmodArchive.Open(path);
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    });

    public static ModuleModel Model => LazyModel.Value;

    public static RulesPack Pack(Func<FormulaDef, FormulaDef>? tweak = null)
    {
        var formula = new FormulaDef
        {
            Id = "f-fuel", Name = "Fuel Use", Text = "MP * lookup(Rates, Kind) / Div", RuleRefId = "rr1",
            Bindings = new()
            {
                new VariableBinding { Variable = "MP", Kind = BindingKind.Input, Min = 0, Max = 20 },
                new VariableBinding { Variable = "Kind", Kind = BindingKind.Input },
                new VariableBinding { Variable = "Div", Kind = BindingKind.Global, Property = "divisor" },
            },
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Subtract } },
        };
        return new RulesPack
        {
            Properties = new()
            {
                new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Default = 0 },
                new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders },
                new PropertyDef { Id = "divisor", Name = "Divisor", Scope = PropertyScope.Holders, Default = 1 },
            },
            Holders = new() { new HolderDef { Id = "depot", Name = "Depot A" } },
            Tables = new()
            {
                new TableDef
                {
                    Id = "t-rates", Name = "Rates", Kind = TableKind.OneD,
                    RowKeys = new() { "1-2", "3-5", "6+" }, ColumnKeys = new() { "Value" },
                    Cells = new() { new() { "1" }, new() { "2" }, new() { "3" } },
                },
            },
            Formulas = new() { tweak == null ? formula : tweak(formula) },
        };
    }

    public static GameState State(double? fuel = 140, double? divisor = 2, PointD? pos = null)
    {
        var s = new GameState();
        s.Pieces["u1"] = new PieceInstance { Id = "u1", TypeString = "piece;;;unit.svg;Rifle", MapId = "Main Map", Pos = pos ?? new PointD(60, 40), TraitStates = new() { "" } };
        if (fuel != null) s.Ledgers[LedgerKey.Piece("u1")] = new() { ["fuel"] = fuel.Value };
        if (divisor != null) s.Ledgers[LedgerKey.Holder("global")] = new() { ["divisor"] = divisor.Value };
        return s;
    }

    public static Dictionary<string, string> D(params (string, string)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    public static RunRequest Req(string formula = "f-fuel", string? piece = "u1", Dictionary<string, string>? typed = null,
        Dictionary<string, string>? overrides = null, Dictionary<string, IReadOnlyList<int>>? rolls = null) =>
        new(formula, piece, typed ?? new(), overrides ?? new(), rolls ?? new());

    public static FormulaRunner Runner(RulesPack pack, Func<int, int, IReadOnlyList<int>>? roller = null) =>
        new(pack, Model, roller ?? ((n, sides) => Enumerable.Repeat(1, n).ToList()));
}

public class FormulaRunnerTests
{
    private static readonly Dictionary<string, string> Full = RunnerFx.D(("MP", "12"), ("Kind", "4"));

    [Fact]
    public void Run_AllFilled_ComputesResultWorkingAndOutputs()
    {
        var t = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(), RunnerFx.Req(typed: Full));
        Assert.Null(t.Error);
        Assert.Equal(12, t.Result!.Value.Number);
        Assert.Equal("12 × Rates[4] ÷ 2 = 12 → Fuel 140 − 12 = 128", t.Working);
        var o = Assert.Single(t.Outputs);
        Assert.Equal((LedgerKey.Piece("u1"), "fuel", "Fuel", 140.0, 128.0), (o.Key, o.PropertyId, o.PropertyName, o.Before, o.After));
        var div = t.Variables.Single(v => v.Variable == "Div");
        Assert.True(div.Auto);
        Assert.Equal(2, div.Value!.Value.Number);
    }

    [Fact]
    public void Run_MissingInput_NoResult_ListsWaiting()
    {
        var t = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "12"))));
        Assert.Null(t.Result);
        Assert.Null(t.Error);
        Assert.Empty(t.Outputs);
        Assert.Equal("Waiting for input", t.Variables.Single(v => v.Variable == "Kind").MissingReason);
        Assert.Null(t.Variables.Single(v => v.Variable == "MP").MissingReason);
    }

    [Fact]
    public void Run_TypedInputNotNumber_IsMissingWithReason()
    {
        var t = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "abc"), ("Kind", "4"))));
        Assert.Null(t.Result);
        Assert.Equal("Not a number", t.Variables.Single(v => v.Variable == "MP").MissingReason);
    }

    [Fact]
    public void Run_UnboundedInputWithoutChoices_AcceptsText_BoundedStillRejects()
    {
        var textPack = RunnerFx.Pack(f => f with { Text = "if(startsWith(Kind, \"Cl\"), MP, 0)" });
        var t = RunnerFx.Runner(textPack).Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "12"), ("Kind", "Clear"))));
        Assert.Equal(12, t.Result!.Value.Number);
        var kind = t.Variables.Single(v => v.Variable == "Kind");
        Assert.Null(kind.MissingReason);
        Assert.Equal("Clear", kind.Value!.Value.Display());
        var num = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "12"), ("Kind", "4.0"))));
        Assert.Equal(ValueKind.Number, num.Variables.Single(v => v.Variable == "Kind").Value!.Value.Kind);
        var bounded = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "Clear"), ("Kind", "4"))));
        Assert.Equal("Not a number", bounded.Variables.Single(v => v.Variable == "MP").MissingReason);
    }

    [Fact]
    public void Run_InputOutOfRange_IsMissingWithReason()
    {
        var t = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "21"), ("Kind", "4"))));
        Assert.Null(t.Result);
        Assert.Equal("Must be 0–20", t.Variables.Single(v => v.Variable == "MP").MissingReason);
    }

    [Fact]
    public void Run_Override_UsedAndFlagged()
    {
        var t = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(),
            RunnerFx.Req(typed: Full, overrides: RunnerFx.D(("Div", "4"))));
        var div = t.Variables.Single(v => v.Variable == "Div");
        Assert.True(div.Overridden);
        Assert.Equal(6, t.Result!.Value.Number);
        var bad = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(),
            RunnerFx.Req(typed: Full, overrides: RunnerFx.D(("Div", "x"))));
        Assert.Null(bad.Result);
        Assert.Equal("Not a number", bad.Variables.Single(v => v.Variable == "Div").MissingReason);
    }

    private static RulesPack RollPack() => RunnerFx.Pack(f => f with
    {
        Text = "MP + Dmg",
        Bindings = new()
        {
            new VariableBinding { Variable = "MP", Kind = BindingKind.Input },
            new VariableBinding { Variable = "Dmg", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6 },
        },
    });

    [Fact]
    public void Run_Roll_UsesFixedRollsWhenGiven()
    {
        var calls = 0;
        var runner = RunnerFx.Runner(RollPack(), (n, s) => { calls++; return Enumerable.Repeat(6, n).ToList(); });
        var fixedRolls = new Dictionary<string, IReadOnlyList<int>> { ["Dmg"] = new[] { 2, 3 } };
        var t = runner.Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "1")), rolls: fixedRolls));
        Assert.Equal(0, calls);
        Assert.Equal(6, t.Result!.Value.Number);
        var r = Assert.Single(t.Rolls);
        Assert.Equal(("Fuel Use: Dmg", 2, 6), (r.Purpose, r.Count, r.Sides));
        Assert.Equal(new[] { 2, 3 }, r.Results);
        var t2 = runner.Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "1"))));
        Assert.Equal(1, calls);
        Assert.Equal(13, t2.Result!.Value.Number);
    }

    private static RulesPack InlinePack() => RunnerFx.Pack(f => f with
    {
        Text = "dice(1, 6) * 10 + dice(2, 6)",
        Bindings = new(),
        Outputs = new(),
    });

    [Fact]
    public void Run_InlineDice_PinnedViaRollsByKey_SecondRunIdenticalWithoutRoller()
    {
        var calls = 0;
        var runner = RunnerFx.Runner(InlinePack(), (n, sides) => { calls++; return Enumerable.Repeat(calls + 1, n).ToList(); });
        var t1 = runner.Run(RunnerFx.State(), RunnerFx.Req());
        Assert.Equal(2, calls);
        Assert.Equal(new[] { "dice#0", "dice#1" }, t1.RollsByKey.Keys.OrderBy(k => k));
        Assert.Equal(new[] { "Fuel Use: dice#0", "Fuel Use: dice#1" }, t1.Rolls.Select(r => r.Purpose));
        var pins = t1.RollsByKey.ToDictionary(kv => kv.Key, kv => kv.Value);
        var t2 = runner.Run(RunnerFx.State(), RunnerFx.Req(rolls: pins));
        Assert.Equal(2, calls);
        Assert.Equal(t1.Result, t2.Result);
        Assert.Equal(t1.Working, t2.Working);
    }

    [Fact]
    public void Run_PinWithMismatchedShape_IsIgnored()
    {
        var calls = 0;
        var runner = RunnerFx.Runner(RollPack(), (n, sides) => { calls++; return Enumerable.Repeat(4, n).ToList(); });
        var pins = new Dictionary<string, IReadOnlyList<int>> { ["Dmg"] = new[] { 2, 3, 1 } };
        var t = runner.Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "1")), rolls: pins));
        Assert.Equal(1, calls);
        Assert.Equal(9, t.Result!.Value.Number);
        var pins2 = new Dictionary<string, IReadOnlyList<int>> { ["Dmg"] = new[] { 2, 9 } };
        runner.Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "1")), rolls: pins2));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Run_TwoOutputs_WorkingUsesSingleArrow()
    {
        var pack = RunnerFx.Pack(f => f with
        {
            Outputs = new()
            {
                new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Subtract },
                new FormulaOutput { Target = OutputTarget.Holder, HolderId = "depot", Property = "supply", Op = OutputOp.Set },
            },
        });
        var t = RunnerFx.Runner(pack).Run(RunnerFx.State(), RunnerFx.Req(typed: Full));
        Assert.Equal("12 × Rates[4] ÷ 2 = 12 → Fuel 140 − 12 = 128; Supply = 12", t.Working);
    }

    [Fact]
    public void Run_DeletedTable_ReturnsErrorTraceNotThrow()
    {
        var pack = RunnerFx.Pack();
        pack.Tables.Clear();
        var t = RunnerFx.Runner(pack).Run(RunnerFx.State(), RunnerFx.Req(typed: Full));
        Assert.Null(t.Result);
        Assert.False(string.IsNullOrEmpty(t.Error));
        Assert.Empty(RunnerFx.Runner(pack).Commands(t, null));
    }

    [Fact]
    public void Run_UnitFormulaWithoutPiece_Error()
    {
        var t = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(), RunnerFx.Req(piece: null, typed: Full));
        Assert.Equal("This formula needs a unit", t.Error);
        Assert.Null(t.Result);
    }

    [Fact]
    public void Commands_RollsThenDeltas_WithProvenance()
    {
        var pack = RollPack();
        pack.Formulas[0] = pack.Formulas[0] with { Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Subtract } } };
        var runner = RunnerFx.Runner(pack);
        var t = runner.Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "1"))));
        var cmds = runner.Commands(t, "scouting");
        Assert.Equal(2, cmds.Count);
        var roll = Assert.IsType<RollDice>(cmds[0]);
        Assert.Equal(("Fuel Use: Dmg", 2, 6), (roll.Purpose, roll.Count, roll.Sides));
        var d = Assert.IsType<LedgerDelta>(cmds[1]);
        Assert.Equal((LedgerKey.Piece("u1"), "fuel", 140.0, 137.0, "formula"), (d.Key, d.PropertyId, d.Before, d.After, d.Source));
        Assert.Equal(("f-fuel", "rr1", "scouting"), (d.FormulaId, d.RuleRefId, d.Note));
        Assert.Equal(t.Working, d.Working);
    }

    [Fact]
    public void Commands_EmptyWhenIncomplete()
    {
        var runner = RunnerFx.Runner(RunnerFx.Pack());
        var t = runner.Run(RunnerFx.State(), RunnerFx.Req());
        Assert.Empty(runner.Commands(t, null));
    }

    [Fact]
    public void Location_BindsGridLocationName()
    {
        var pack = RunnerFx.Pack(f => f with
        {
            Text = "Where",
            Bindings = new() { new VariableBinding { Variable = "Where", Kind = BindingKind.Location } },
            Outputs = new(),
        });
        var pos = new PointD(60, 40);
        var board = RunnerFx.Model.Maps[0].Boards[0];
        var expected = board.Grid!.LocationName(new PointD(pos.X - board.Offset.X, pos.Y - board.Offset.Y));
        Assert.NotEmpty(expected);
        var t = RunnerFx.Runner(pack).Run(RunnerFx.State(pos: pos), RunnerFx.Req());
        Assert.Null(t.Error);
        Assert.Equal(expected, t.Result!.Value.Text);
    }

    private static RulesPack PackWith(string text, params VariableBinding[] bindings) =>
        new()
        {
            Properties = new()
            {
                new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Default = 0 },
                new PropertyDef { Id = "divisor", Name = "Divisor", Scope = PropertyScope.Holders, Default = 1 },
                new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders },
            },
            Holders = new() { new HolderDef { Id = "depot", Name = "Depot A" } },
            Tables = new()
            {
                new TableDef { Id = "t1", Name = "TestTerrainCost", Kind = TableKind.TwoD,
                    RowKeys = new() { "Sand", "Mud" }, ColumnKeys = new() { "Dry", "Wet" },
                    Cells = new() { new() { "1", "2" }, new() { "3", "4" } } },
                new TableDef { Id = "t2", Name = "OneT", Kind = TableKind.OneD,
                    RowKeys = new() { "1-2", "3+" }, ColumnKeys = new() { "Value" }, Cells = new() { new() { "5" }, new() { "6" } } },
            },
            Formulas = new() { new FormulaDef { Id = "f", Name = "F", Text = text, Bindings = bindings.ToList() } },
        };

    [Fact]
    public void Moved_FromRequest_Auto()
    {
        var pack = PackWith("M * 2", new VariableBinding { Variable = "M", Kind = BindingKind.Moved });
        var t = RunnerFx.Runner(pack).Run(RunnerFx.State(), RunnerFx.Req("f") with { Moved = 3.5 });
        Assert.Null(t.Error);
        var v = t.Variables.Single();
        Assert.True(v.Auto);
        Assert.Equal(3.5, v.Value!.Value.Number);
        Assert.Equal(7, t.Result!.Value.Number);
        Assert.Equal("distance moved", v.Source);
    }

    [Fact]
    public void Moved_Absent_MissingWithReason()
    {
        var pack = PackWith("M * 2", new VariableBinding { Variable = "M", Kind = BindingKind.Moved });
        var runner = RunnerFx.Runner(pack);
        var t = runner.Run(RunnerFx.State(), RunnerFx.Req("f"));
        Assert.Null(t.Result);
        Assert.Equal("Only known when a unit is moved", t.Variables.Single().MissingReason);
        var o = runner.Run(RunnerFx.State(), RunnerFx.Req("f", overrides: RunnerFx.D(("M", "4"))));
        Assert.Equal(8, o.Result!.Value.Number);
        Assert.True(o.Variables.Single().Overridden);
    }

    private static RulesPack AutoPack(string auto) => PackWith("Pace",
        new VariableBinding { Variable = "Pace", Kind = BindingKind.Input, Choices = new() { "Slow", "Normal", "Fast" }, AutoValue = auto });

    [Fact]
    public void AutoValue_UsedOnlyWhenRequested()
    {
        var runner = RunnerFx.Runner(AutoPack("\"Normal\""));
        var off = runner.Run(RunnerFx.State(), RunnerFx.Req("f"));
        Assert.Equal("Waiting for input", off.Variables.Single().MissingReason);
        var on = runner.Run(RunnerFx.State(), RunnerFx.Req("f") with { UseAutoValues = true });
        Assert.Equal("Normal", on.Variables.Single().Value!.Value.Text);
        Assert.Equal("default", on.Variables.Single().Source);
        var typed = runner.Run(RunnerFx.State(), RunnerFx.Req("f", typed: RunnerFx.D(("Pace", "fast"))) with { UseAutoValues = true });
        Assert.Equal("Fast", typed.Variables.Single().Value!.Value.Text);
        Assert.Null(typed.Variables.Single().Source);
        var o = RunnerFx.Runner(AutoPack("\"Sprint\"")).Run(RunnerFx.State(), RunnerFx.Req("f") with { UseAutoValues = true });
        Assert.StartsWith("Choose one of", o.Variables.Single().MissingReason);
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("nosuch + 1")]
    [InlineData("\"a\" + 1")]
    public void AutoValue_BadExpression_Missing(string auto)
    {
        var t = RunnerFx.Runner(AutoPack(auto)).Run(RunnerFx.State(), RunnerFx.Req("f") with { UseAutoValues = true });
        Assert.Null(t.Error);
        var v = t.Variables.Single();
        Assert.Null(v.Value);
        Assert.False(string.IsNullOrEmpty(v.MissingReason));
    }

    [Fact]
    public void Sources_PerBindingKind()
    {
        var pack = PackWith("U + G + H + T + O + 0",
            new VariableBinding { Variable = "U", Kind = BindingKind.Unit, Property = "fuel" },
            new VariableBinding { Variable = "G", Kind = BindingKind.Global, Property = "divisor" },
            new VariableBinding { Variable = "H", Kind = BindingKind.Holder, Property = "supply", HolderId = "depot" },
            new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t1", RowExpr = "\"Sand\"", ColExpr = "\"Dry\"" },
            new VariableBinding { Variable = "O", Kind = BindingKind.Table, TableId = "t2", RowExpr = "4" });
        var t = RunnerFx.Runner(pack).Run(RunnerFx.State(), RunnerFx.Req("f"));
        Assert.Null(t.Error);
        VariableState V(string n) => t.Variables.Single(x => x.Variable == n);
        Assert.Equal("Fuel of Rifle", V("U").Source);
        Assert.Equal("Divisor (global)", V("G").Source);
        Assert.Equal("Supply of Depot A", V("H").Source);
        Assert.Equal("TestTerrainCost, row Sand, column Dry", V("T").Source);
        Assert.Equal(new TableCell("t1", 0, 0), V("T").Cell);
        Assert.Equal("OneT, row 3+", V("O").Source);
        Assert.Equal(new TableCell("t2", 1, 0), V("O").Cell);

        var loc = PackWith("L", new VariableBinding { Variable = "L", Kind = BindingKind.Location });
        var lt = RunnerFx.Runner(loc).Run(RunnerFx.State(), RunnerFx.Req("f"));
        Assert.Equal("location of Rifle", lt.Variables.Single().Source);

        var roll = PackWith("R + I", new VariableBinding { Variable = "R", Kind = BindingKind.Roll }, new VariableBinding { Variable = "I", Kind = BindingKind.Input });
        var rt = RunnerFx.Runner(roll).Run(RunnerFx.State(), RunnerFx.Req("f", typed: RunnerFx.D(("I", "2"))));
        Assert.All(rt.Variables, x => Assert.Null(x.Source));
    }

    [Fact]
    public void Commands_CarryTagAndSubject()
    {
        var runner = RunnerFx.Runner(RunnerFx.Pack());
        var t = runner.Run(RunnerFx.State(), RunnerFx.Req(typed: Full));
        var d = runner.Commands(t, "n", "move", "Rifle").OfType<LedgerDelta>().Single();
        Assert.Equal("move", d.Tag);
        Assert.Equal("Rifle", d.Subject);
        var d0 = runner.Commands(t, "n").OfType<LedgerDelta>().Single();
        Assert.Null(d0.Tag);
        Assert.Null(d0.Subject);
    }
}
