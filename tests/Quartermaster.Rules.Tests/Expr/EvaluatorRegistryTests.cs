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
        public Value Aggregate(string fn, string? property, string? filter) => Value.Of(1);
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
        "lookuprange" => new AstExpr[] { new VarRef("T", 0, 1), Num(1), Num(2) },
        "count" => new AstExpr[] { new VarRef("R", 0, 1), Num(1), Num(2) },
        "sum" or "maxof" or "minof" => new AstExpr[] { new VarRef("P", 0, 1), new TextLit("f", 0, 1) },
        "countof" => new AstExpr[] { new TextLit("f", 0, 1) },
        "hexsum" or "hexmax" or "hexmin" => new AstExpr[] { new VarRef("P", 0, 1) },
        "hexcount" or "attachedcount" => Array.Empty<AstExpr>(),
        "attachedsum" or "attachedmax" or "attachedmin" => new AstExpr[] { new VarRef("P", 0, 1) },
        "dice" => new[] { Num(1), Num(6) },
        "has" => new AstExpr[] { new VarRef("p", 0, 1) },
        "startswith" or "contains" => new AstExpr[] { new TextLit("a", 0, 1), new TextLit("a", 0, 1) },
        "mod" => new[] { Num(7), Num(4) },
        "holder" => new AstExpr[] { new VarRef("H", 0, 1), new VarRef("P", 0, 1) },
        "name" or "turn" or "stage" or "side" or "phaseside" or "map" => Array.Empty<AstExpr>(),
        "marker" or "layer" => new AstExpr[] { new TextLit("k", 0, 1) },
        "runs" => new AstExpr[] { new TextLit("f", 0, 1) },
        "distance" => new AstExpr[] { new TextLit("a", 0, 1), new TextLit("b", 0, 1) },
        "keyed" => new AstExpr[] { new VarRef("P", 0, 1), Num(1) },
        _ => Array.Empty<AstExpr>(),
    };

    [Fact]
    public void Read_functions_are_listed()
    {
        foreach (var n in new[] { "side", "phaseside", "map", "holder", "lookuprange", "count", "sum", "countof", "maxof", "minof", "hexsum", "hexcount", "hexmax", "hexmin", "attachedsum", "attachedcount", "attachedmax", "attachedmin" })
            Assert.Contains(n, Evaluator.FunctionNames);
    }

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
