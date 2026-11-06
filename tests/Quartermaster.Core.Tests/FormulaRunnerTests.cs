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
}
