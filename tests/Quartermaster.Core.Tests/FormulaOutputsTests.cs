using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Core.Tests;

/// <summary>Outputs with their own value expression and a property picked at run time (invented fixtures).</summary>
public class FormulaOutputsTests
{
    private static FormulaOutput Out(string property, string? expr = null) =>
        new() { Target = OutputTarget.Unit, Property = property, Op = OutputOp.Subtract, Expr = expr };

    private static FormulaOutput Pick(string select, string? expr = null) =>
        new() { Target = OutputTarget.Unit, PropertyChoice = new() { "fuel", "ammo" }, PropertySelect = select, Op = OutputOp.Subtract, Expr = expr };

    /// <summary>Text "MP"; MP and Extra are free inputs, Slot an input, R a one-die roll.</summary>
    private static RulesPack Pack(params FormulaOutput[] outputs) => RunnerFx.Pack(f => f with
    {
        Text = "MP",
        Bindings = new()
        {
            new VariableBinding { Variable = "MP", Kind = BindingKind.Input },
            new VariableBinding { Variable = "Extra", Kind = BindingKind.Input },
            new VariableBinding { Variable = "Slot", Kind = BindingKind.Input },
            new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 },
        },
        Outputs = outputs.ToList(),
    }) with
    {
        Properties = new()
        {
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Default = 0 },
            new PropertyDef { Id = "ammo", Name = "Ammo", Scope = PropertyScope.Pieces, Default = 0 },
            new PropertyDef { Id = "divisor", Name = "Divisor", Scope = PropertyScope.Holders, Default = 1 },
        },
    };

    private static GameState State()
    {
        var s = RunnerFx.State();
        s.Ledgers[LedgerKey.Piece("u1")]["ammo"] = 30;
        return s;
    }

    private static FormulaTrace Run(RulesPack pack, params (string, string)[] typed) =>
        RunnerFx.Runner(pack).Run(State(), RunnerFx.Req(typed: RunnerFx.D(typed)));

    [Fact]
    public void Output_expr_gives_its_own_value()
    {
        var t = Run(Pack(Out("fuel", "MP * 2"), Out("ammo")), ("MP", "6"));
        Assert.Null(t.Error);
        Assert.Equal(6, t.Result!.Value.Number);
        Assert.Equal(new[] { ("fuel", 140.0, 128.0), ("ammo", 30.0, 24.0) }, t.Outputs.Select(o => (o.PropertyId, o.Before, o.After)));
    }

    [Fact]
    public void Blank_expr_takes_the_result()
    {
        var t = Run(Pack(Out("fuel", "   "), Out("ammo", null)), ("MP", "6"));
        Assert.Null(t.Error);
        Assert.Equal(new[] { 134.0, 24.0 }, t.Outputs.Select(o => o.After));
    }

    [Fact]
    public void Output_exprs_share_the_run_dice()
    {
        var calls = 0;
        var pack = Pack(Out("fuel", "R"), Out("ammo", "R * 2")) with { };
        pack = pack with { Formulas = new() { pack.Formulas[0] with { Text = "R" } } };
        var runner = RunnerFx.Runner(pack, (n, sides) => { calls++; return Enumerable.Repeat(calls + 2, n).ToList(); });
        var t = runner.Run(State(), RunnerFx.Req());
        Assert.Null(t.Error);
        Assert.Equal(1, calls);
        Assert.Single(t.Rolls);
        Assert.Equal(3, t.Result!.Value.Number);
        Assert.Equal(new[] { 137.0, 24.0 }, t.Outputs.Select(o => o.After));
    }

    [Fact]
    public void Output_expr_not_number_error()
    {
        var t = Run(Pack(Out("fuel", "\"lots\"")), ("MP", "6"));
        Assert.Equal("The value for Fuel is not a number", t.Error);
        Assert.Empty(t.Outputs);
    }

    [Fact]
    public void Pick_by_number()
    {
        var t = Run(Pack(Pick("Slot")), ("MP", "6"), ("Slot", "2"));
        Assert.Null(t.Error);
        var o = Assert.Single(t.Outputs);
        Assert.Equal(("ammo", "Ammo", 30.0, 24.0), (o.PropertyId, o.PropertyName, o.Before, o.After));
        var first = Run(Pack(Pick("Slot")), ("MP", "6"), ("Slot", "1"));
        Assert.Equal("fuel", Assert.Single(first.Outputs).PropertyId);
    }

    [Fact]
    public void Pick_by_choice_name()
    {
        var t = Run(Pack(Pick("\"  aMMo \"")), ("MP", "6"));
        Assert.Null(t.Error);
        Assert.Equal("ammo", Assert.Single(t.Outputs).PropertyId);
        var typed = Run(Pack(Pick("Slot")), ("MP", "6"), ("Slot", "fuel"));
        Assert.Equal("fuel", Assert.Single(typed.Outputs).PropertyId);
    }

    [Fact]
    public void Pick_out_of_list_error()
    {
        foreach (var (slot, shown) in new[] { ("3", "3"), ("0", "0"), ("1.5", "1.5"), ("Water", "Water") })
        {
            var t = Run(Pack(Pick("Slot")), ("MP", "6"), ("Slot", slot));
            Assert.Equal($"{shown} is not one of the output's properties", t.Error);
            Assert.Empty(t.Outputs);
        }
        var byId = Run(Pack(Pick("\"divisor\"")), ("MP", "6"));   // a property not in the list, by its name or id
        Assert.Equal("divisor is not one of the output's properties", byId.Error);
        var truth = Run(Pack(Pick("MP > 1")), ("MP", "6"));
        Assert.Equal("true is not one of the output's properties", truth.Error);
    }

    [Fact]
    public void Failing_second_output_writes_nothing()
    {
        var pack = Pack(Out("fuel"), Pick("Slot"));
        var runner = RunnerFx.Runner(pack);
        var t = runner.Run(State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "6"), ("Slot", "9"))));
        Assert.Equal("9 is not one of the output's properties", t.Error);
        Assert.Empty(t.Outputs);
        Assert.Empty(runner.Commands(t, null));
        var bad = Run(Pack(Out("fuel"), Out("ammo", "\"x\"")), ("MP", "6"));
        Assert.Equal("The value for Ammo is not a number", bad.Error);
        Assert.Empty(bad.Outputs);
    }

    [Fact]
    public void Working_line_shows_each_value()
    {
        var t = Run(Pack(Out("fuel", "MP * 2"), Out("ammo")), ("MP", "6"));
        Assert.Equal("6 = 6 → Fuel 140 − 12 = 128; Ammo 30 − 6 = 24", t.Working);
    }

    [Fact]
    public void Output_expr_reads_waiting_input_waits()
    {
        var t = Run(Pack(Out("fuel", "MP + Extra")), ("MP", "6"));
        Assert.Null(t.Error);
        Assert.Null(t.Result);
        Assert.Empty(t.Outputs);
        Assert.Equal("Waiting for input", t.Variables.Single(v => v.Variable == "Extra").MissingReason);

        var sel = Run(Pack(Pick("Slot")), ("MP", "6"));
        Assert.Null(sel.Error);
        Assert.Null(sel.Result);
        Assert.Equal("Waiting for input", sel.Variables.Single(v => v.Variable == "Slot").MissingReason);

        // A roll read only by an output waits while the precondition is not decided.
        var held = Pack(Out("fuel", "R")) with { };
        held = held with { Formulas = new() { held.Formulas[0] with { Precondition = "Extra > 0" } } };
        var h = RunnerFx.Runner(held).Run(State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "6"))));
        Assert.Null(h.Error);
        Assert.Null(h.Result);
        Assert.True(h.Variables.Single(v => v.Variable == "R").Waiting);
        Assert.Empty(h.Rolls);
    }

    [Fact]
    public void Formula_without_new_fields_unchanged()
    {
        var t = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "12"), ("Kind", "4"))));
        Assert.Null(t.Error);
        Assert.Equal("12 × Rates[4] ÷ 2 = 12 → Fuel 140 − 12 = 128", t.Working);
        Assert.Equal(new[] { "MP", "Kind", "Div" }, t.Variables.Select(v => v.Variable));
        // A list-less output ignores a stray selector, and its variables are not asked for.
        var stray = RunnerFx.Runner(RunnerFx.Pack(f => f with { Outputs = new() { f.Outputs[0] with { PropertySelect = "Nope" } } }))
            .Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "12"), ("Kind", "4"))));
        Assert.Null(stray.Error);
        Assert.Equal(t.Working, stray.Working);
        Assert.Equal(new[] { "MP", "Kind", "Div" }, stray.Variables.Select(v => v.Variable));
    }
}
