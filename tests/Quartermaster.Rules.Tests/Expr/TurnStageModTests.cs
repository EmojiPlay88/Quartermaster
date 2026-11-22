using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests.Expr;

public class TurnStageModTests
{
    private sealed class Ctx : IEvalContext
    {
        public Value GetVariable(string name) => Value.Of(1);
        public Value Lookup(string tableName, Value row, Value? col) => Value.Of(1);
        public IReadOnlyList<int> RollDice(int n, int sides) => new[] { 1 };
        public bool Has(string property) => true;
        public Value PieceName() => Value.Of("x");
        public Value Marker(string key) => Value.Of("x");
    }

    private static double Eval(string f) => Evaluator.Evaluate(Parser.Parse(f), new Ctx()).Number;

    [Fact]
    public void Mod_is_floored()
    {
        Assert.Equal(3, Eval("mod(7,4)"));
        Assert.Equal(3, Eval("mod(-1,4)"));
        Assert.Equal(0, Eval("mod(8,4)"));
        Assert.Equal(1.5, Eval("mod(5.5,2)"));
        var e = Assert.Throws<EvalError>(() => Eval("mod(1,0)"));
        Assert.Equal("mod: division by zero", e.Message);
    }

    [Fact]
    public void Turn_and_stage_default_to_one()
    {
        Assert.Equal(1, Eval("turn()"));
        Assert.Equal(1, Eval("stage()"));
    }

    [Fact]
    public void Wrong_arity_is_parse_error()
    {
        Assert.Throws<ParseError>(() => Parser.Parse("turn(1)"));
        Assert.Throws<ParseError>(() => Parser.Parse("stage(1)"));
        Assert.Throws<ParseError>(() => Parser.Parse("mod(1)"));
    }
}
