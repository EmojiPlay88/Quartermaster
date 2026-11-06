using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests;

public class EvaluatorTests
{
    private sealed class FakeContext : IEvalContext
    {
        public Dictionary<string, Value> Vars = new(StringComparer.OrdinalIgnoreCase);
        public List<int> Rolls = new() { 3, 4 };
        public HashSet<string> Props = new(StringComparer.OrdinalIgnoreCase);
        public string Piece = "Rook";
        public Dictionary<string, string> Markers = new();

        public Value GetVariable(string name) =>
            Vars.TryGetValue(name, out var v) ? v : throw new EvalError($"'{name}' has no value");
        public Value Lookup(string tableName, Value row, Value? col) =>
            Value.Of(row.Number * 10 + (col?.Number ?? 0));
        public IReadOnlyList<int> RollDice(int n, int sides) => Rolls;
        public bool Has(string property) => Props.Contains(property);
        public Value PieceName() => Value.Of(Piece);
        public Value Marker(string key) => Value.Of(Markers[key]);
    }

    private static FakeContext Ctx()
    {
        var c = new FakeContext();
        c.Vars["MP"] = Value.Of(12);
        c.Vars["Rate"] = Value.Of(3);
        c.Vars["Mod"] = Value.Of(1.5);
        c.Vars["Div"] = Value.Of(2);
        c.Vars["Label"] = Value.Of("abc");
        return c;
    }

    private static Value Eval(string f, FakeContext? c = null) => Evaluator.Evaluate(Parser.Parse(f), c ?? Ctx());

    [Fact]
    public void Arithmetic_AndPrecedence()
    {
        Assert.Equal(27, Eval("MP * Rate * Mod / Div").Number);
        Assert.Equal(7, Eval("1 + 2 * 3").Number);
    }

    [Fact]
    public void Round_AwayFromZero()
    {
        Assert.Equal(3, Eval("round(2.5)").Number);
        Assert.Equal(-3, Eval("round(-2.5)").Number);
        Assert.Equal(1.24, Eval("round(1.235, 2)").Number);
        Assert.Throws<EvalError>(() => Eval("round(1.5, 11)"));
        Assert.Throws<EvalError>(() => Eval("round(1.5, 0.5)"));
    }

    [Fact]
    public void DivisionByZero_EvalError()
    {
        var ex = Assert.Throws<EvalError>(() => Eval("1 / 0"));
        Assert.Equal("Division by zero", ex.Message);
    }

    [Fact]
    public void TextInArithmetic_EvalError()
    {
        var ex = Assert.Throws<EvalError>(() => Eval("Label + 1"));
        Assert.Contains("'+' needs numbers", ex.Message);
        Assert.Throws<EvalError>(() => Eval("Label < 1"));
    }

    [Fact]
    public void Overflow_IsEvalError()
    {
        var ex = Assert.Throws<EvalError>(() => Eval(new string('9', 308) + " * 100"));
        Assert.Equal("Result is not a finite number", ex.Message);
    }

    [Fact]
    public void If_OnlyEvaluatesChosenBranch()
    {
        Assert.Equal(1, Eval("if(true, 1, Missing)").Number);
        Assert.Equal(2, Eval("if(false, Missing, 2)").Number);
    }

    [Fact]
    public void AndOr_ShortCircuit()
    {
        Assert.False(Eval("false and Missing").Bool);
        Assert.True(Eval("true or Missing").Bool);
        Assert.Throws<EvalError>(() => Eval("true and Missing"));
    }

    [Fact]
    public void Compare_TextCaseInsensitive()
    {
        Assert.True(Eval("Label == \"ABC\"").Bool);
        Assert.False(Eval("Label != \"abc\"").Bool);
        Assert.False(Eval("Label == 1").Bool);
        Assert.True(Eval("Label != 1").Bool);
        Assert.True(Eval("1 == 1").Bool);
    }

    [Fact]
    public void Dice_SumsRolls()
    {
        Assert.Equal(7, Eval("dice(2, 6)").Number);
    }

    [Fact]
    public void Dice_OutOfRange_EvalError()
    {
        Assert.Throws<EvalError>(() => Eval("dice(0, 6)"));
        Assert.Throws<EvalError>(() => Eval("dice(101, 6)"));
        Assert.Throws<EvalError>(() => Eval("dice(2, 1)"));
        Assert.Throws<EvalError>(() => Eval("dice(2, 1001)"));
    }

    [Fact]
    public void Has_And_Marker_And_Name_DelegateToContext()
    {
        var c = Ctx();
        c.Props.Add("Armoured");
        c.Markers["fuel"] = "full";
        Assert.True(Eval("has(Armoured)", c).Bool);
        Assert.False(Eval("has(Flying)", c).Bool);
        Assert.Equal("Rook", Eval("name()", c).Text);
        Assert.Equal("full", Eval("marker(\"fuel\")", c).Text);
    }

    [Fact]
    public void MinMaxClampAbsFloorCeil()
    {
        Assert.Equal(1, Eval("min(3, 1, 2)").Number);
        Assert.Equal(3, Eval("max(3, 1, 2)").Number);
        Assert.Equal(5, Eval("clamp(9, 0, 5)").Number);
        Assert.Equal(2, Eval("abs(-2)").Number);
        Assert.Equal(1, Eval("floor(1.9)").Number);
        Assert.Equal(2, Eval("ceil(1.1)").Number);
        Assert.Throws<EvalError>(() => Eval("min(1, \"a\")"));
    }

    [Fact]
    public void Display_Formats()
    {
        Assert.Equal("1.5", Value.Of(1.5).Display());
        Assert.Equal("27", Value.Of(27).Display());
        Assert.Equal("-3", Value.Of(-3).Display());
        Assert.Equal("0.33", Value.Of(1.0 / 3).Display());
        Assert.Equal("raw", Value.Of("raw").Display());
        Assert.Equal("true", Value.Of(true).Display());
    }

    private static string Work(string f, FakeContext? c = null)
    {
        var ctx = c ?? Ctx();
        return WorkingLine.Render(Parser.Parse(f), f, n => ctx.Vars.TryGetValue(n, out var v) ? v : null);
    }

    [Fact]
    public void WorkingLine_SubstitutesAndUsesMathSymbols()
    {
        Assert.Equal("12 × 3 × 1.5 ÷ 2", Work("MP * Rate * Mod / Div"));
        Assert.Equal("−3 − 1", Work("-3 - 1"));
        Assert.Equal("(12 + 3) × 2", Work("(MP + Rate) * Div"));
        Assert.Equal("(12 + 3) × (2)", Work("(MP + Rate) * (Div)"));
        Assert.Equal("12 ≥ 3", Work("MP >= Rate"));
        Assert.Equal("min(12, 3)", Work("min(MP, Rate)"));
        Assert.Equal("if(true, 1, \"x\")", Work("if(true, 1, \"x\")"));
    }

    [Fact]
    public void WorkingLine_MissingShowsQuestionMark()
    {
        Assert.Equal("12 + ?", Work("MP + Nope"));
    }

    [Fact]
    public void WorkingLine_LookupRendersBracketed()
    {
        Assert.Equal("Rates[12]", Work("lookup(Rates, MP)"));
        Assert.Equal("Rates[12, 3]", Work("lookup(Rates, MP, Rate)"));
    }

    [Fact]
    public void StartsWith_Contains_CaseInsensitive()
    {
        Assert.True(Eval("startsWith(\"Red 1\", \"red\")").Bool);
        Assert.False(Eval("startsWith(\"Red 1\", \"1\")").Bool);
        Assert.True(Eval("contains(\"Blue Inf\", \"INF\")").Bool);
        Assert.False(Eval("contains(\"Blue Inf\", \"cav\")").Bool);
    }

    [Fact]
    public void StartsWith_NonText_EvalError()
    {
        var e = Assert.Throws<EvalError>(() => Eval("startsWith(5, \"a\")"));
        Assert.Equal("'startsWith' needs text, got a number", e.Message);
        Assert.Throws<EvalError>(() => Eval("contains(\"a\", 5)"));
    }
}
