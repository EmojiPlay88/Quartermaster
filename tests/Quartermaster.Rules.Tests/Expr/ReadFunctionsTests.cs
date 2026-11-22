using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests.Expr;

public class ReadFunctionsTests
{
    private sealed class BareCtx : IEvalContext
    {
        public Value GetVariable(string name) => Value.Of(1);
        public Value Lookup(string tableName, Value row, Value? col) => Value.Of(1);
        public IReadOnlyList<int> RollDice(int n, int sides) => new[] { 1 };
        public bool Has(string property) => true;
        public Value PieceName() => Value.Of("x");
        public Value Marker(string key) => Value.Of("x");
    }

    [Theory]
    [InlineData("side()", "side() is not available here")]
    [InlineData("phaseside()", "phaseside() is not available here")]
    [InlineData("map()", "map() is not available here")]
    [InlineData("holder(Rail, Tonnage)", "holder() is not available here")]
    public void Defaults_throw_not_available(string f, string msg)
    {
        var e = Assert.Throws<EvalError>(() => Evaluator.Evaluate(Parser.Parse(f), new BareCtx()));
        Assert.Equal(msg, e.Message);
    }

    [Fact]
    public void Holder_needs_bare_names()
    {
        Assert.Throws<ParseError>(() => Parser.Parse("holder(\"A\", B)"));
        Assert.Throws<ParseError>(() => Parser.Parse("holder(A, \"B\")"));
        Assert.Throws<ParseError>(() => Parser.Parse("holder(A)"));
        Parser.Parse("holder(A, B)");
    }

    [Fact]
    public void Zero_arg_functions_reject_arguments()
    {
        foreach (var f in new[] { "side(1)", "phaseside(1)", "map(1)" })
            Assert.Throws<ParseError>(() => Parser.Parse(f));
    }

    [Fact]
    public void Variables_skip_holder_args()
    {
        Assert.Equal(new[] { "X" }, Parser.Variables(Parser.Parse("holder(Rail, Tonnage) > X")));
    }

    private static RulesPack Pack() => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "pf", Name = "Flag", Scope = PropertyScope.Holders },
            new PropertyDef { Id = "pb", Name = "Both1", Scope = PropertyScope.Both },
            new PropertyDef { Id = "pp", Name = "Fuel", Scope = PropertyScope.Pieces },
        },
    };

    [Theory]
    [InlineData("Flag == 1", true)]
    [InlineData("holder(Rail, Tonnage) > 3", true)]
    [InlineData("Fuel > 0", false)]
    [InlineData("Both1 > 0", false)]
    [InlineData("name() == \"a\"", false)]
    [InlineData("side() == \"North\"", false)]
    [InlineData("phaseside() == \"North\"", true)]
    [InlineData("lookup(T, Flag) > 0", true)]
    [InlineData("Unknown > 0", false)]
    [InlineData("map() == \"m\"", false)]
    [InlineData("has(Flag)", false)]
    [InlineData("marker(\"k\") == 1", false)]
    [InlineData("1 + 2", true)]
    public void IsGameLevel(string f, bool expected)
    {
        Assert.Equal(expected, ExprFacts.IsGameLevel(Parser.Parse(f), Pack()));
    }

    [Fact]
    public void Working_line_shows_holder_names()
    {
        const string f = "holder(Rail, Tonnage) + X";
        var line = WorkingLine.Render(Parser.Parse(f), f, n => n == "X" ? Value.Of(2) : null);
        Assert.Equal("holder(Rail, Tonnage) + 2", line);
    }
}
