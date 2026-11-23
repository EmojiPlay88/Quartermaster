using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Core.Tests;

/// <summary>Formula preconditions (refused runs) and bindings used only when a condition holds.</summary>
public class FormulaGuardsTests
{
    private static readonly Dictionary<string, string> Fuel = RunnerFx.D(("MP", "4"), ("Kind", "3"));

    private static FormulaRunner Runner(Func<FormulaDef, FormulaDef> tweak, Func<int, int, IReadOnlyList<int>>? roller = null) =>
        RunnerFx.Runner(RunnerFx.Pack(tweak), roller);

    private static FormulaDef With(FormulaDef f, string text, params VariableBinding[] extra) =>
        f with { Text = text, Bindings = f.Bindings.Concat(extra).ToList() };

    private static VariableBinding In(string v, string? when = null) => new() { Variable = v, Kind = BindingKind.Input, When = when };

    private static VariableBinding Dmg(string? when = null) =>
        new() { Variable = "Dmg", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6, When = when };

    private static VariableState V(FormulaTrace t, string name) => Assert.Single(t.Variables, x => x.Variable == name);

    [Fact]
    public void Formula_without_guards_unchanged()
    {
        var t = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel));
        Assert.Null(t.Error);
        Assert.False(t.Refused);
        Assert.Equal(new[] { "MP", "Kind", "Div" }, t.Variables.Select(x => x.Variable));
        Assert.Equal(new[] { 4.0, 3.0, 2.0 }, t.Variables.Select(x => x.Value!.Value.Number));
        Assert.All(t.Variables, x => { Assert.False(x.Skipped); Assert.Null(x.MissingReason); });
        Assert.Equal(4, t.Result!.Value.Number);
        var o = Assert.Single(t.Outputs);
        Assert.Equal((140.0, 136.0), (o.Before, o.After));
        Assert.Empty(t.Rolls);
        Assert.Empty(t.RollsByKey);
    }

    [Fact]
    public void Precondition_true_runs_as_before()
    {
        var plain = RunnerFx.Runner(RunnerFx.Pack()).Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel));
        var t = Runner(f => f with { Precondition = "Div > 1", RefusalMessage = "No" }).Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel));
        Assert.Null(t.Error);
        Assert.False(t.Refused);
        Assert.Equal(plain.Result!.Value.Number, t.Result!.Value.Number);
        Assert.Equal(plain.Working, t.Working);
        Assert.Equal(plain.Outputs, t.Outputs);
        Assert.Equal(plain.Variables.Select(x => x.Variable), t.Variables.Select(x => x.Variable));
    }

    [Fact]
    public void Precondition_false_refused_with_message()
    {
        var runner = Runner(f => f with { Precondition = "Div > 5", RefusalMessage = "Out of supply" });
        var t = runner.Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel));
        Assert.True(t.Refused);
        Assert.Equal("Out of supply", t.Error);
        Assert.Null(t.Result);
        Assert.Empty(t.Outputs);
        Assert.Empty(runner.Commands(t, null));
    }

    [Fact]
    public void Precondition_false_default_message()
    {
        var t = Runner(f => f with { Precondition = "Div > 5" }).Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel));
        Assert.True(t.Refused);
        Assert.Equal("Fuel Use cannot be run now", t.Error);
    }

    [Fact]
    public void Refused_run_rolls_and_writes_nothing()
    {
        var calls = 0;
        var runner = Runner(f => With(f, "MP + Dmg", Dmg()) with { Precondition = "Div > 5" },
            (n, s) => { calls++; return Enumerable.Repeat(1, n).ToList(); });
        var pins = new Dictionary<string, IReadOnlyList<int>> { ["Dmg"] = new[] { 2, 3 } };
        var t = runner.Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel, rolls: pins));
        Assert.True(t.Refused);
        Assert.Empty(t.Rolls);
        Assert.False(t.RollsByKey.ContainsKey("Dmg"));
        Assert.Equal(0, calls);
        var cmds = runner.Commands(t, null);
        Assert.DoesNotContain(cmds, c => c is RollDice);
        Assert.DoesNotContain(cmds, c => c is LedgerDelta);
    }

    [Fact]
    public void Precondition_not_boolean_is_error()
    {
        var t = Runner(f => f with { Precondition = "\"yes\"" }).Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel));
        Assert.False(t.Refused);
        Assert.Null(t.Result);
        Assert.Equal("'if' needs true/false or a number, got text", t.Error);
    }

    [Fact]
    public void Precondition_waiting_for_input_returns_without_result()
    {
        var t = Runner(f => f with { Precondition = "MP > 2" }).Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("Kind", "3"))));
        Assert.False(t.Refused);
        Assert.Null(t.Error);
        Assert.Null(t.Result);
        Assert.Equal("Waiting for input", V(t, "MP").MissingReason);
    }

    [Fact]
    public void Precondition_variables_are_listed()
    {
        var t = Runner(f => With(f, "MP + Div", In("Gate")) with { Precondition = "Gate > 0" })
            .Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "4"))));
        Assert.Equal(new[] { "MP", "Div", "Gate" }, t.Variables.Select(x => x.Variable));
        Assert.Equal("Waiting for input", V(t, "Gate").MissingReason);
        Assert.Null(t.Result);
    }

    [Fact]
    public void Precondition_that_would_roll_fails_without_rolling()
    {
        var calls = 0;
        Func<int, int, IReadOnlyList<int>> roller = (n, s) => { calls++; return Enumerable.Repeat(1, n).ToList(); };
        foreach (var pre in new[] { "Dmg > 3", "dice(1, 6) > 3" })
        {
            var t = Runner(f => With(f, "MP + Dmg", Dmg()) with { Precondition = pre }, roller).Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel));
            Assert.False(t.Refused);
            Assert.Null(t.Result);
            Assert.Equal("The precondition of Fuel Use cannot roll dice", t.Error);
            Assert.Empty(t.Rolls);
        }
        Assert.Equal(0, calls);
    }

    [Fact]
    public void When_false_roll_not_rolled()
    {
        var t = Runner(f => With(f, "if(Div > 5, MP + Dmg, MP)", Dmg("Div > 5"))).Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel));
        Assert.Null(t.Error);
        Assert.Equal(4, t.Result!.Value.Number);
        Assert.Empty(t.Rolls);
        Assert.False(t.RollsByKey.ContainsKey("Dmg"));
        var d = V(t, "Dmg");
        Assert.True(d.Skipped);
        Assert.Null(d.Value);
        Assert.Null(d.MissingReason);
    }

    [Fact]
    public void When_true_roll_rolled()
    {
        var t = Runner(f => With(f, "if(Div > 5, MP + Dmg, MP)", Dmg("Div > 5"))).Run(RunnerFx.State(divisor: 10), RunnerFx.Req(typed: Fuel));
        Assert.Null(t.Error);
        Assert.Equal(6, t.Result!.Value.Number);
        var r = Assert.Single(t.Rolls);
        Assert.Equal("Fuel Use: Dmg", r.Purpose);
        Assert.False(V(t, "Dmg").Skipped);
    }

    [Fact]
    public void When_false_input_not_missing()
    {
        var t = Runner(f => With(f, "if(Div > 5, MP + Extra, MP)", In("Extra", "Div > 5"))).Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel));
        Assert.Null(t.Error);
        Assert.Equal(4, t.Result!.Value.Number);
        var e = V(t, "Extra");
        Assert.True(e.Skipped);
        Assert.Null(e.MissingReason);
        Assert.Null(e.Value);
    }

    [Fact]
    public void When_depends_on_input()
    {
        var runner = Runner(f => With(f, "MP + if(Mode == 1, Extra, 0)", In("Mode"), In("Extra", "Mode == 1")));
        FormulaTrace Go(params (string, string)[] typed) => runner.Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(typed)));

        // Mode not filled: Extra waits (not missing, not skipped), the run has no result yet.
        var waiting = Go(("MP", "4"));
        Assert.Null(waiting.Error);
        Assert.Null(waiting.Result);
        Assert.Equal("Waiting for input", V(waiting, "Mode").MissingReason);
        var e = V(waiting, "Extra");
        Assert.Null(e.MissingReason);
        Assert.False(e.Skipped);
        Assert.Null(e.Value);

        var skipped = Go(("MP", "4"), ("Mode", "2"));
        Assert.Equal(4, skipped.Result!.Value.Number);
        Assert.True(V(skipped, "Extra").Skipped);

        var asked = Go(("MP", "4"), ("Mode", "1"));
        Assert.Null(asked.Result);
        Assert.Equal("Waiting for input", V(asked, "Extra").MissingReason);

        var filled = Go(("MP", "4"), ("Mode", "1"), ("Extra", "3"));
        Assert.Equal(7, filled.Result!.Value.Number);
    }

    [Fact]
    public void When_variables_are_listed()
    {
        var t = Runner(f => With(f, "MP + Extra", In("Extra", "Ask > 0"), In("Ask")))
            .Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "4"))));
        Assert.Equal(new[] { "MP", "Extra", "Ask" }, t.Variables.Select(x => x.Variable));
        Assert.Equal("Waiting for input", V(t, "Ask").MissingReason);
        Assert.Null(V(t, "Extra").MissingReason);
        Assert.Null(t.Result);
        Assert.Null(t.Error);
    }

    [Fact]
    public void When_not_boolean_is_error()
    {
        var t = Runner(f => With(f, "MP + Extra", In("Extra", "\"x\""))).Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel));
        Assert.Null(t.Result);
        Assert.Equal("'if' needs true/false or a number, got text", t.Error);
    }

    [Fact]
    public void Skipped_binding_read_fails()
    {
        var t = Runner(f => With(f, "MP + Extra", In("Extra", "Div > 5"))).Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel));
        Assert.Null(t.Result);
        Assert.False(t.Refused);
        Assert.Equal("Extra is not used here: its \"Only when\" condition is false", t.Error);
    }

    [Fact]
    public void Skipped_binding_ignores_override()
    {
        var t = Runner(f => With(f, "if(Div > 5, MP + Extra, MP)", In("Extra", "Div > 5")))
            .Run(RunnerFx.State(), RunnerFx.Req(typed: Fuel, overrides: RunnerFx.D(("Extra", "9"))));
        Assert.Equal(4, t.Result!.Value.Number);
        Assert.True(V(t, "Extra").Skipped);
    }
}
