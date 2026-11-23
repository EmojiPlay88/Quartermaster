using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Core.Tests;

/// <summary>lookuprange in every context, Roll bindings whose dice count is an expression, and count() over a roll's dice.</summary>
public class DiceChartsTests
{
    /// <summary>Invented crash chart: each row prints its own range per column.</summary>
    private static TableDef Crash() => new()
    {
        Id = "t-crash", Name = "Crash", Kind = TableKind.TwoD,
        RowKeys = new() { "1", "2", "3-4" },
        ColumnKeys = new() { "A", "B", "10" },
        Cells = new()
        {
            new() { "1-2", "3-4", "5+" },
            new() { "1-3", "4-6", "7+" },
            new() { "1-3", "", "4+" },
        },
    };

    private static VariableBinding In(string v, string? when = null) => new() { Variable = v, Kind = BindingKind.Input, When = when };

    private static VariableBinding Roll(string v, int count = 1, string? countExpr = null, string? when = null) =>
        new() { Variable = v, Kind = BindingKind.Roll, DiceCount = count, DiceSides = 6, DiceCountExpr = countExpr, When = when };

    /// <summary>The fixture pack with the crash chart and one formula of the given text and bindings (no outputs).</summary>
    private static RulesPack Pack(string text, params VariableBinding[] bindings)
    {
        var pack = RunnerFx.Pack(f => f with { Text = text, Bindings = bindings.ToList(), Outputs = new() });
        pack.Tables.Add(Crash());
        return pack;
    }

    private static FormulaTrace Run(RulesPack pack, Dictionary<string, string>? typed = null,
        Dictionary<string, IReadOnlyList<int>>? pins = null, Dictionary<string, string>? overrides = null,
        Func<int, int, IReadOnlyList<int>>? roller = null) =>
        RunnerFx.Runner(pack, roller).Run(RunnerFx.State(), RunnerFx.Req(typed: typed, rolls: pins, overrides: overrides));

    private static VariableState V(FormulaTrace t, string name) => Assert.Single(t.Variables, x => x.Variable == name);

    private static Dictionary<string, IReadOnlyList<int>> Pin(string key, params int[] dice) => new() { [key] = dice };

    [Fact]
    public void Formula_lookuprange_in_text()
    {
        var t = Run(Pack("lookuprange(Crash, Row, Hits)", In("Row"), In("Hits")), RunnerFx.D(("Row", "2"), ("Hits", "5")));
        Assert.Null(t.Error);
        Assert.Equal("B", t.Result!.Value.Text);

        var num = Run(Pack("lookuprange(Crash, Row, Hits) * 2", In("Row"), In("Hits")), RunnerFx.D(("Row", "4"), ("Hits", "9")));
        Assert.Null(num.Error);
        Assert.Equal(20, num.Result!.Value.Number);

        var none = Run(Pack("lookuprange(Crash, Row, Hits)", In("Row"), In("Hits")), RunnerFx.D(("Row", "3"), ("Hits", "0")));
        Assert.Equal("No column of Crash row 3 holds 0", none.Error);

        var oneWay = Run(Pack("lookuprange(Rates, Row, Hits)", In("Row"), In("Hits")), RunnerFx.D(("Row", "3"), ("Hits", "1")));
        Assert.Equal("lookuprange needs a two-way table", oneWay.Error);
    }

    [Fact]
    public void DiceCountExpr_rolls_that_many()
    {
        var t = Run(Pack("R + count(R, 1, 1)", In("Planes"), Roll("R", 2, "Planes")), RunnerFx.D(("Planes", "12")));
        Assert.Null(t.Error);
        Assert.Equal(24, t.Result!.Value.Number);
        var roll = Assert.Single(t.Rolls);
        Assert.Equal((12, 6), (roll.Count, roll.Sides));
        Assert.Equal(12, t.RollsByKey["R"].Count);
        Assert.Equal(12, V(t, "R").Value!.Value.Number);
        Assert.Equal(12, V(t, "Planes").Value!.Value.Number);   // read only by the dice count, still listed
        Assert.Equal(new[] { "R", "Planes" }, t.Variables.Select(x => x.Variable));
    }

    [Fact]
    public void DiceCountExpr_reads_another_roll()
    {
        var t = Run(Pack("Hits", Roll("Base"), Roll("Hits", 1, "Base + 1")), pins: Pin("Base", 3));
        Assert.Null(t.Error);
        Assert.Equal(4, t.RollsByKey["Hits"].Count);
        Assert.Equal(4, t.Result!.Value.Number);
    }

    [Fact]
    public void DiceCountExpr_cycle_errors()
    {
        var t = Run(Pack("A", Roll("A", 1, "B"), Roll("B", 1, "A")));
        Assert.Equal("Variable A depends on itself", t.Error);
        Assert.Null(t.Result);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("2.5")]
    public void DiceCountExpr_out_of_range_error(string planes)
    {
        var calls = 0;
        var t = Run(Pack("R", In("Planes"), Roll("R", 2, "Planes")), RunnerFx.D(("Planes", planes)),
            roller: (n, s) => { calls++; return Enumerable.Repeat(1, n).ToList(); });
        Assert.Equal("'R' dice count must be a whole number from 1 to 100", t.Error);
        Assert.Null(t.Result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DiceCountExpr_text_value_error()
    {
        var t = Run(Pack("R", In("Planes"), Roll("R", 2, "Planes")), RunnerFx.D(("Planes", "many")));
        Assert.Equal("'R' dice count must be a whole number from 1 to 100", t.Error);
    }

    [Fact]
    public void DiceCountExpr_pin_with_other_count_ignored()
    {
        var pack = Pack("R", In("Planes"), Roll("R", 2, "Planes"));
        var ignored = Run(pack, RunnerFx.D(("Planes", "3")), Pin("R", 6, 6));
        Assert.Null(ignored.Error);
        Assert.Equal(3, ignored.Result!.Value.Number);       // fixture roller: all ones
        Assert.Equal(new[] { 1, 1, 1 }, ignored.RollsByKey["R"]);

        var used = Run(pack, RunnerFx.D(("Planes", "3")), Pin("R", 6, 6, 6));
        Assert.Equal(18, used.Result!.Value.Number);
    }

    [Fact]
    public void DiceCountExpr_waits_for_input()
    {
        var calls = 0;
        var t = Run(Pack("R", In("Planes"), Roll("R", 2, "Planes")),
            roller: (n, s) => { calls++; return Enumerable.Repeat(1, n).ToList(); });
        Assert.Null(t.Error);
        Assert.Null(t.Result);
        Assert.Empty(t.Rolls);
        Assert.False(t.RollsByKey.ContainsKey("R"));
        Assert.Equal(0, calls);
        Assert.Null(V(t, "R").Value);
        Assert.Equal("Waiting for input", V(t, "Planes").MissingReason);
    }

    [Fact]
    public void DiceCountExpr_waits_for_undecided_only_when()
    {
        var t = Run(Pack("R", In("Gate"), In("Planes", when: "Gate > 0"), Roll("R", 2, "Planes")));
        Assert.Null(t.Error);
        Assert.Null(t.Result);
        Assert.Empty(t.Rolls);
        Assert.True(V(t, "R").Waiting);
    }

    [Fact]
    public void Count_two_counts_from_one_roll()
    {
        var t = Run(Pack("count(R, 1, 2) * 10 + count(R, 3, 3)", Roll("R", 4)), pins: Pin("R", 1, 2, 3, 6));
        Assert.Null(t.Error);
        Assert.Equal(21, t.Result!.Value.Number);
        Assert.Equal(12, V(t, "R").Value!.Value.Number);
        Assert.Single(t.Rolls);

        var sum = Run(Pack("R + count(R, 6, 6)", Roll("R", 4)), pins: Pin("R", 1, 2, 3, 6));
        Assert.Equal(13, sum.Result!.Value.Number);
    }

    [Fact]
    public void Count_of_skipped_roll_errors()
    {
        var t = Run(Pack("count(R, 1, 6)", In("Gate"), Roll("R", 2, when: "Gate > 0")), RunnerFx.D(("Gate", "0")));
        Assert.Equal("R is not used here: its \"Only when\" condition is false", t.Error);
        Assert.Null(t.Result);
    }

    [Fact]
    public void Count_of_non_roll_errors()
    {
        // A hand-edited pack the validator would reject.
        var t = Run(Pack("count(MP, 1, 6)", In("MP")), RunnerFx.D(("MP", "4")));
        Assert.Equal("count() needs a roll binding, not MP", t.Error);
        Assert.Null(t.Result);
    }

    [Fact]
    public void Count_of_overridden_roll_errors()
    {
        var t = Run(Pack("count(R, 1, 6)", Roll("R", 2)), overrides: RunnerFx.D(("R", "7")));
        Assert.Equal("count() cannot see the dice of R: its value was typed in", t.Error);
        Assert.Null(t.Result);
    }

    [Fact]
    public void LookupRange_in_filter_and_phase_condition()
    {
        var pack = RunnerFx.Pack();
        pack.Tables.Add(Crash());
        pack.Phases.Add(new PhaseDef { Id = "P1", Name = "Phase One", Condition = "lookuprange(Crash, 2, 5) == \"B\"" });
        pack.Phases.Add(new PhaseDef { Id = "P2", Name = "Phase Two", Condition = "lookuprange(Crash, 2, 5) == \"A\"" });
        var s = RunnerFx.State();
        var p = s.Pieces["u1"];

        Assert.True(PieceFilter.Matches("lookuprange(Crash, 2, 5) == \"B\"", p, s, RunnerFx.Model, pack));
        Assert.False(PieceFilter.Matches("lookuprange(Crash, 2, 5) == \"A\"", p, s, RunnerFx.Model, pack));
        Assert.True(PieceFilter.EvaluateGameLevel("lookuprange(Crash, 4, 9) == 10", s, pack));

        Assert.True(PhaseConditions.Runs(pack, s, pack.Phases[0], new PhasePosition(1, 1, "P1", 0), out var err));
        Assert.Null(err);
        Assert.False(PhaseConditions.Runs(pack, s, pack.Phases[1], new PhasePosition(1, 1, "P2", 1), out err));
        Assert.Null(err);

        var bad = new PhaseDef { Id = "P3", Name = "Phase Three", Condition = "lookuprange(Crash, 3, 0) == 1" };
        Assert.True(PhaseConditions.Runs(pack, s, bad, new PhasePosition(1, 1, "P3", 2), out err));   // fail-open
        Assert.Equal("No column of Crash row 3 holds 0", err);
    }

    [Fact]
    public void Formula_without_new_features_unchanged()
    {
        var pack = RunnerFx.Pack(f => f with
        {
            Text = "MP * lookup(Rates, Kind) / Div + Dmg",
            Bindings = f.Bindings.Append(new VariableBinding { Variable = "Dmg", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6 }).ToList(),
        });
        var t = RunnerFx.Runner(pack).Run(RunnerFx.State(), RunnerFx.Req(typed: RunnerFx.D(("MP", "4"), ("Kind", "3")), rolls: Pin("Dmg", 2, 5)));
        Assert.Null(t.Error);
        Assert.Equal(new[] { "MP", "Kind", "Div", "Dmg" }, t.Variables.Select(x => x.Variable));
        Assert.Equal(11, t.Result!.Value.Number);
        Assert.Equal("4 × Rates[3] ÷ 2 + 7 = 11 → Fuel 140 − 11 = 129", t.Working);
        var roll = Assert.Single(t.Rolls);
        Assert.Equal((2, 6), (roll.Count, roll.Sides));
        Assert.Equal(new[] { 2, 5 }, t.RollsByKey["Dmg"]);
    }
}
