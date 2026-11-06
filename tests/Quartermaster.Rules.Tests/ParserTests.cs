using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests;

public class ParserTests
{
    private static Binary Bin(object e) => Assert.IsType<Binary>(e);
    private static double Num(object e) => Assert.IsType<NumberLit>(e).Value;

    [Fact]
    public void Precedence_MulBeforeAdd()
    {
        var add = Bin(Parser.Parse("1+2*3"));
        Assert.Equal("+", add.Op);
        Assert.Equal(1, Num(add.Left));
        var mul = Bin(add.Right);
        Assert.Equal("*", mul.Op);
        Assert.Equal(2, Num(mul.Left));
        Assert.Equal(3, Num(mul.Right));
    }

    [Fact]
    public void UnaryMinus_BindsTighterThanMul()
    {
        var mul = Bin(Parser.Parse("-2*3"));
        Assert.Equal("*", mul.Op);
        var neg = Assert.IsType<Unary>(mul.Left);
        Assert.Equal("-", neg.Op);
        Assert.Equal(2, Num(neg.Operand));
        Assert.Equal(3, Num(mul.Right));
    }

    [Fact]
    public void Comparison_And_Or_Not()
    {
        var or = Bin(Parser.Parse("a >= 2 and not b == 3 or c"));
        Assert.Equal("or", or.Op);
        var and = Bin(or.Left);
        Assert.Equal("and", and.Op);
        Assert.Equal(">=", Bin(and.Left).Op);
        var not = Assert.IsType<Unary>(and.Right);
        Assert.Equal("not", not.Op);
        Assert.Equal("==", Bin(not.Operand).Op);
        Assert.Equal("c", Assert.IsType<VarRef>(or.Right).Name);
    }

    [Fact]
    public void If_ParsesAsCall()
    {
        var c = Assert.IsType<Call>(Parser.Parse("IF(a < 1, \"x\", true)"));
        Assert.Equal("if", c.Function);
        Assert.Equal(3, c.Args.Count);
        Assert.Equal("<", Bin(c.Args[0]).Op);
        Assert.Equal("x", Assert.IsType<TextLit>(c.Args[1]).Value);
        Assert.True(Assert.IsType<BoolLit>(c.Args[2]).Value);
    }

    [Fact]
    public void Lookup_FirstArgIsVarRef_NotListedInVariables()
    {
        var e = Parser.Parse("lookup(Rates, Kind) * MP");
        Assert.Equal(new[] { "Kind", "MP" }, Parser.Variables(e));
    }

    [Fact]
    public void Lookup_FirstArgMustBeBareName()
    {
        var ex = Assert.Throws<ParseError>(() => Parser.Parse("lookup(\"T\", k)"));
        Assert.Equal("lookup needs a table name", ex.Detail);
    }

    [Fact]
    public void Variables_DistinctInOrder()
    {
        var e = Parser.Parse("b + a * B + b - min(a, c)");
        Assert.Equal(new[] { "b", "a", "B", "c" }, Parser.Variables(e));
    }

    [Fact]
    public void VarRef_KeepsCase_FunctionLowerCased()
    {
        var c = Assert.IsType<Call>(Parser.Parse("MAX(Foo, 1)"));
        Assert.Equal("max", c.Function);
        Assert.Equal("Foo", Assert.IsType<VarRef>(c.Args[0]).Name);
        Assert.IsType<BoolLit>(Parser.Parse("TRUE"));
    }

    [Fact]
    public void UnknownFunction_ParseErrorWithPosition()
    {
        var ex = Assert.Throws<ParseError>(() => Parser.Parse("1 + foo(2)"));
        Assert.Equal(4, ex.Position);
        Assert.Equal("Unknown function 'foo'", ex.Detail);
        Assert.Equal("Unknown function 'foo' at position 4", ex.Message);
    }

    [Theory]
    [InlineData("if(1,2)")]
    [InlineData("min(1)")]
    [InlineData("round()")]
    [InlineData("round(1,2,3)")]
    [InlineData("floor(1,2)")]
    [InlineData("clamp(1,2)")]
    [InlineData("lookup(T)")]
    [InlineData("lookup(T,1,2,3)")]
    [InlineData("dice(1)")]
    [InlineData("has()")]
    [InlineData("name(1)")]
    [InlineData("marker()")]
    public void WrongArgCount_ParseError(string text)
    {
        Assert.Throws<ParseError>(() => Parser.Parse(text));
    }

    [Theory]
    [InlineData("min(1,2,3,4)")]
    [InlineData("round(1)")]
    [InlineData("round(1,2)")]
    [InlineData("lookup(T,1)")]
    [InlineData("lookup(T,1,2)")]
    [InlineData("name()")]
    [InlineData("dice(2,6)")]
    public void RightArgCount_Parses(string text)
    {
        Assert.IsType<Call>(Parser.Parse(text));
    }

    [Fact]
    public void UnterminatedString_ParseError()
    {
        var ex = Assert.Throws<ParseError>(() => Parser.Parse("1 + \"abc"));
        Assert.Equal(4, ex.Position);
    }

    [Fact]
    public void String_Escapes()
    {
        Assert.Equal("a\"b\\c", Assert.IsType<TextLit>(Parser.Parse("\"a\\\"b\\\\c\"")).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_ParseError(string text)
    {
        var ex = Assert.Throws<ParseError>(() => Parser.Parse(text));
        Assert.Equal(0, ex.Position);
        Assert.Equal("Formula is empty", ex.Detail);
    }

    [Fact]
    public void Spans_CoverSourceText()
    {
        const string src = "  (1 + 2) * foo";
        var mul = Bin(Parser.Parse(src));
        Assert.Equal(2, mul.Start);
        Assert.Equal("(1 + 2) * foo", src.Substring(mul.Start, mul.Length));
        Assert.Equal("(1 + 2)", src.Substring(mul.Left.Start, mul.Left.Length));
        Assert.Equal("foo", src.Substring(mul.Right.Start, mul.Right.Length));
        var c = Assert.IsType<Call>(Parser.Parse("min(1, 2)"));
        Assert.Equal(0, c.Start);
        Assert.Equal(9, c.Length);
    }

    [Fact]
    public void Numbers_Invariant()
    {
        Assert.Equal(0.5, Num(Parser.Parse(".5")));
        Assert.Equal(1.5, Num(Parser.Parse("1.5")));
    }

    [Fact]
    public void ChainedComparison_IsError()
    {
        var ex = Assert.Throws<ParseError>(() => Parser.Parse("a < b < c"));
        Assert.Equal("Comparisons can't be chained", ex.Detail);
    }

    [Fact]
    public void UnicodeOperators_AreAliases()
    {
        var e = Bin(Parser.Parse("6 \u00F7 2 \u00D7 3 \u2212 1"));
        Assert.Equal("-", e.Op);
        Assert.Equal("*", Bin(e.Left).Op);
        Assert.Equal("/", Bin(Bin(e.Left).Left).Op);
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("(1")]
    [InlineData("1 2")]
    [InlineData("a = 1")]
    [InlineData("1 $ 2")]
    [InlineData("min(1,)")]
    [InlineData("and")]
    public void Malformed_ParseError(string text)
    {
        Assert.Throws<ParseError>(() => Parser.Parse(text));
    }

    [Fact]
    public void Guards_DepthAndLength()
    {
        var deep = new string('(', 300) + "1" + new string(')', 300);
        Assert.Equal("Formula nests too deeply", Assert.Throws<ParseError>(() => Parser.Parse(deep)).Detail);
        var nots = string.Concat(Enumerable.Repeat("not ", 300)) + "true";
        Assert.Equal("Formula nests too deeply", Assert.Throws<ParseError>(() => Parser.Parse(nots)).Detail);
        var long_ = new string('1', 10001);
        Assert.Equal("Formula is too long", Assert.Throws<ParseError>(() => Parser.Parse(long_)).Detail);
        var ok = new string('(', 100) + "1" + new string(')', 100);
        Assert.IsType<NumberLit>(Parser.Parse(ok));
    }

    private static string Chain(string op, int n, string term = "a") =>
        string.Join(op, Enumerable.Repeat(term, n));

    [Theory]
    [InlineData(" + ")]
    [InlineData(" - ")]
    [InlineData("*")]
    [InlineData("/")]
    [InlineData(" or ")]
    [InlineData(" and ")]
    public void LongLeftChains_AreRejected(string op)
    {
        var ex = Assert.Throws<ParseError>(() => Parser.Parse(Chain(op, 1500, "1")));
        Assert.Contains("nests too deeply", ex.Detail);
    }

    [Fact]
    public void ChainBoundary()
    {
        Assert.IsType<Binary>(Parser.Parse(Chain("+", 150, "1")));
        Assert.IsType<Binary>(Parser.Parse(Chain(" or ", 150)));
        Assert.Throws<ParseError>(() => Parser.Parse(Chain("+", 250, "1")));
    }

    [Fact]
    public void MixedNesting_JustOver200_IsRejected()
    {
        // 120-term chain (height 120) wrapped in 100 calls -> height > 200
        string s = Chain("+", 120, "1");
        for (int i = 0; i < 100; i++) s = "abs(" + s + ")";
        Assert.Contains("nests too deeply", Assert.Throws<ParseError>(() => Parser.Parse(s)).Detail);
        // Unary chain and call args also count
        Assert.Throws<ParseError>(() => Parser.Parse(new string('-', 150) + "(" + Chain("+", 100, "1") + ")"));
        // within limits still fine
        string ok = Chain("+", 50, "1");
        for (int i = 0; i < 50; i++) ok = "abs(" + ok + ")";
        Assert.IsType<Call>(Parser.Parse(ok));
    }
}
