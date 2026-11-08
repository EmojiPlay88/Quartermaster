using System.Text.RegularExpressions;
using Quartermaster.Rules.Expr;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules.Tests.Expr;

public class EvaluatorRegistryTests
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

    private static AstExpr Num(double d) => new NumberLit(d, 0, 1);

    private static Call CallOf(string fn, params AstExpr[] args) => new(fn, args, 0, 1);

    // Valid dummy arguments per function (the call is built directly so only the evaluator dispatch is tested).
    private static AstExpr[] DummyArgs(string fn) => fn switch
    {
        "if" => new AstExpr[] { new BoolLit(true, 0, 1), Num(1), Num(2) },
        "min" or "max" => new[] { Num(1), Num(2) },
        "round" or "floor" or "ceil" or "abs" => new[] { Num(1) },
        "clamp" => new[] { Num(1), Num(0), Num(2) },
        "lookup" => new AstExpr[] { new VarRef("T", 0, 1), Num(1) },
        "dice" => new[] { Num(1), Num(6) },
        "has" => new AstExpr[] { new VarRef("p", 0, 1) },
        "startswith" or "contains" => new AstExpr[] { new TextLit("a", 0, 1), new TextLit("a", 0, 1) },
        "name" => Array.Empty<AstExpr>(),
        "marker" => new AstExpr[] { new TextLit("k", 0, 1) },
        _ => Array.Empty<AstExpr>(),
    };

    [Fact]
    public void Every_listed_function_is_dispatched()
    {
        Assert.NotEmpty(Evaluator.FunctionNames);
        foreach (var fn in Evaluator.FunctionNames)
        {
            try { Evaluator.Evaluate(CallOf(fn, DummyArgs(fn)), new Ctx()); }
            catch (EvalError e) { Assert.False(e.Message.StartsWith("Unknown function"), $"{fn}: {e.Message}"); }
        }
    }

    [Fact]
    public void Unlisted_function_is_unknown()
    {
        var e = Assert.Throws<EvalError>(() => Evaluator.Evaluate(CallOf("nosuchfn", Num(1)), new Ctx()));
        Assert.Equal("Unknown function 'nosuchfn'", e.Message);
    }

    [Fact]
    public void Every_listed_operator_parses()
    {
        Assert.NotEmpty(Evaluator.OperatorNames);
        foreach (var op in Evaluator.OperatorNames)
        {
            string f = op switch
            {
                "not" => "not 1",
                "and" or "or" => $"1 {op} 1",
                _ => $"1 {op} 2",
            };
            Parser.Parse(f);
        }
        Parser.Parse("-1"); // unary minus
    }

    [Fact]
    public void Registry_matches_source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Quartermaster.sln"))
                           && !File.Exists(Path.Combine(dir.FullName, "Quartermaster.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Quartermaster.Rules", "Expr", "Evaluator.cs"));
        int start = src.IndexOf("private static Value EvalCall", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = src.Substring(start);
        var found = Regex.Matches(body, @"case\s+""([a-z]+)""\s*:").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(found.OrderBy(x => x), Evaluator.FunctionNames.OrderBy(x => x));
    }
}
