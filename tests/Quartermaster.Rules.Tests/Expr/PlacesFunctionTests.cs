using Quartermaster.Rules.Expr;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules.Tests.Expr;

/// <summary>B6: hex(), distance() and keyed() in the language, and the facts that read them.</summary>
public class PlacesFunctionTests
{
    private class BareCtx : IEvalContext
    {
        public Value GetVariable(string name) => Value.Of(1);
        public Value Lookup(string tableName, Value row, Value? col) => Value.Of(1);
        public IReadOnlyList<int> RollDice(int n, int sides) => new[] { 1 };
        public bool Has(string property) => true;
        public Value PieceName() => Value.Of("x");
        public Value Marker(string key) => Value.Of("x");
    }

    private sealed class PlacesCtx : BareCtx, IEvalContext
    {
        public string? LastProperty, LastHolder, LastA, LastB;
        public Value LastKey;
        public Value Hex() => Value.Of("0412");
        public double Distance(string a, string b) { LastA = a; LastB = b; return 7; }
        public double Keyed(string property, Value key, string? holder) { LastProperty = property; LastKey = key; LastHolder = holder; return 5; }
    }

    private static AstExpr P(string s) => Parser.Parse(s);

    [Fact]
    public void Hex_and_distance_and_keyed_parse()
    {
        P("hex()");
        P("distance(\"0101\", \"0202\")");
        P("keyed(Ford, hex())");
        P("keyed(Hulls, \"Barge\", Yard)");
        Assert.Throws<ParseError>(() => P("hex(1)"));
        Assert.Throws<ParseError>(() => P("distance(\"a\")"));
        Assert.Throws<ParseError>(() => P("distance(\"a\", \"b\", \"c\")"));
        Assert.Throws<ParseError>(() => P("keyed(Ford)"));
        Assert.Throws<ParseError>(() => P("keyed(Ford, 1, Yard, 2)"));
        Assert.StartsWith("'hex' takes 0 arguments, not 1", Assert.Throws<ParseError>(() => P("hex(1)")).Message);
    }

    [Fact]
    public void Function_names_registered()
    {
        Assert.Contains("hex", Evaluator.FunctionNames);
        Assert.Contains("distance", Evaluator.FunctionNames);
        Assert.Contains("keyed", Evaluator.FunctionNames);
    }

    [Fact]
    public void Variables_skip_keyed_names()
    {
        Assert.Equal(new[] { "X", "Y" }, Parser.Variables(P("keyed(Hulls, X, Yard) + Y")));
        Assert.Equal(new[] { "X" }, Parser.Variables(P("keyed(Ford, X)")));
        Assert.Empty(Parser.Variables(P("keyed(Ford, hex())")));
    }

    [Theory]
    [InlineData("hex()", "hex() is not available here")]
    [InlineData("distance(\"a\", \"b\")", "distance() is not available here")]
    [InlineData("keyed(Ford, 1)", "keyed() is not available here")]
    public void Default_context_texts(string f, string msg) =>
        Assert.Equal(msg, Assert.Throws<EvalError>(() => Evaluator.Evaluate(P(f), new BareCtx())).Message);

    [Fact]
    public void Dispatch_reaches_the_context()
    {
        var ctx = new PlacesCtx();
        Assert.Equal("0412", Evaluator.Evaluate(P("hex()"), ctx).Text);
        Assert.Equal(7, Evaluator.Evaluate(P("distance(hex(), \"0505\")"), ctx).Number);
        Assert.Equal(("0412", "0505"), (ctx.LastA, ctx.LastB));
        Assert.Equal(5, Evaluator.Evaluate(P("keyed(Ford, hex())"), ctx).Number);
        Assert.Equal(("Ford", "0412", null), (ctx.LastProperty, ctx.LastKey.Text, ctx.LastHolder));
        Evaluator.Evaluate(P("keyed(Hulls, 3, Yard)"), ctx);
        Assert.Equal(("Hulls", 3d, "Yard"), (ctx.LastProperty, ctx.LastKey.Number, ctx.LastHolder));
    }

    [Fact]
    public void Distance_number_argument_error()
    {
        var ctx = new PlacesCtx();
        Assert.Equal("distance() needs hex names as text", Assert.Throws<EvalError>(() => Evaluator.Evaluate(P("distance(1, \"a\")"), ctx)).Message);
        Assert.Equal("distance() needs hex names as text", Assert.Throws<EvalError>(() => Evaluator.Evaluate(P("distance(\"a\", 412)"), ctx)).Message);
    }

    [Fact]
    public void Keyed_needs_property_name()
    {
        var ctx = new PlacesCtx();
        Assert.Equal("keyed() needs a property name", Assert.Throws<EvalError>(() => Evaluator.Evaluate(P("keyed(\"Ford\", 1)"), ctx)).Message);
        Assert.Equal("keyed() needs a property name", Assert.Throws<EvalError>(() => Evaluator.Evaluate(P("keyed(1 + 1, 1)"), ctx)).Message);
    }

    private static RulesPack Pack() => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "p-ford", Name = "Ford", Scope = PropertyScope.Holders, KeyedBy = "hex", Visibility = Visibility.Public },
            new PropertyDef { Id = "p-priv", Name = "Seep", Scope = PropertyScope.Holders, KeyedBy = "hex" },
            new PropertyDef { Id = "p-load", Name = "Load", Scope = PropertyScope.Pieces, KeyedBy = "turn", Visibility = Visibility.Public },
            new PropertyDef { Id = "p-both", Name = "Mix", Scope = PropertyScope.Both, KeyedBy = "turn", Visibility = Visibility.Public },
        },
    };

    [Fact]
    public void IsGameLevel_hex_and_piece_keyed_false()
    {
        var pack = Pack();
        Assert.False(ExprFacts.IsGameLevel(P("hex() == \"a\""), pack));
        Assert.False(ExprFacts.IsGameLevel(P("keyed(Load, 1) > 0"), pack));
        Assert.False(ExprFacts.IsGameLevel(P("keyed(Mix, 1) > 0"), pack));
        Assert.True(ExprFacts.IsGameLevel(P("keyed(Ford, \"0101\") > 0"), pack));
        Assert.True(ExprFacts.IsGameLevel(P("keyed(Ford, turn()) > 0"), pack));
        Assert.True(ExprFacts.IsGameLevel(P("keyed(Load, 1, Yard) > 0"), pack));   // a holder argument reads no piece
        Assert.False(ExprFacts.IsGameLevel(P("keyed(Ford, name())"), pack));       // the key walk still counts
        Assert.True(ExprFacts.IsGameLevel(P("distance(\"a\", \"b\") > 1"), pack));
    }

    [Fact]
    public void KeyedReads_lists_calls()
    {
        var reads = ExprFacts.KeyedReads(P("keyed(Ford, hex()) + keyed(Hulls, \"Barge\", Yard) + keyed(\"x\", 1)"));
        Assert.Equal(2, reads.Count);
        Assert.Equal(("Ford", (string?)null), (reads[0].Property, reads[0].Holder));
        Assert.Equal(("Hulls", "Yard"), (reads[1].Property, reads[1].Holder));
        Assert.IsType<Call>(reads[0].Key);
    }

    [Fact]
    public void ReadsHidden_keyed_private()
    {
        var pack = Pack();
        PropertyDef Computed(string expr) => new() { Id = "c", Name = "C", Scope = PropertyScope.Holders, Expr = expr, Visibility = Visibility.Public };
        Assert.False(PropertyFacts.ReadsHidden(pack, Computed("keyed(Ford, \"a\")")));
        Assert.True(PropertyFacts.ReadsHidden(pack, Computed("keyed(Seep, \"a\")")));
        Assert.True(PropertyFacts.ReadsHidden(pack, Computed("keyed(Ford, \"a\", Yard)")));
        Assert.True(PropertyFacts.ReadsHidden(pack, Computed("keyed(Nope, \"a\")")));   // unknown reads hidden
    }

    [Fact]
    public void FormulaVariables_see_the_key()
    {
        var f = new FormulaDef
        {
            Id = "f", Name = "F", Text = "1",
            Bindings = new() { new VariableBinding { Variable = "D", Kind = BindingKind.Input } },
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "p-ford", Op = OutputOp.Set, Expr = "1", Key = "hex() + D" } },
        };
        Assert.Contains("D", FormulaVariables.Used(f));
        Assert.Contains("hex() + D", FormulaVariables.OutputSources(f));
        f = f with { Outputs = new() { f.Outputs[0] with { Key = "keyed(Ford, \"a\") + keyed(Hulls, \"b\", Yard)" } } };
        var reads = FormulaVariables.KeyedReads(f);
        Assert.Equal(new[] { "Ford", "Hulls" }, reads.Select(r => r.Property));
    }

    [Fact]
    public void Aggregate_filter_keyed_read_hidden()
    {
        var pack = Pack() with
        {
            Properties = Pack().Properties.Append(new PropertyDef { Id = "p-str", Name = "Strength", Scope = PropertyScope.Pieces, Visibility = Visibility.Public })
                .Append(new PropertyDef { Id = "p-cargo", Name = "Cargo", Scope = PropertyScope.Pieces, KeyedBy = "turn" }).ToList(),
        };
        Assert.True(FormulaVariables.AggregateCallHidden(pack, "sum", "Strength", "keyed(Cargo, 1) > 0", piecesMayHide: false));   // Private
        Assert.True(FormulaVariables.AggregateCallHidden(pack, "sum", "Strength", "keyed(Seep, \"a\") > 0", piecesMayHide: false));   // Private place
        Assert.True(FormulaVariables.AggregateCallHidden(pack, "sum", "Strength", "keyed(Ford, \"a\", Yard) > 0", piecesMayHide: false));   // a holder
        Assert.True(FormulaVariables.AggregateCallHidden(pack, "sum", "Strength", "keyed(Nope, 1) > 0", piecesMayHide: false));   // unknown
        Assert.False(FormulaVariables.AggregateCallHidden(pack, "sum", "Strength", "keyed(Load, 1) > 0", piecesMayHide: false));   // Public
        var f = new FormulaDef { Id = "f", Name = "F", Text = "sum(Strength, \"keyed(Cargo, 1) > 0\")" };
        Assert.True(FormulaVariables.AggregateReadsHidden(pack, f, piecesMayHide: false));
    }

    [Fact]
    public void WorkingLine_names_keyed_property_and_holder()
    {
        const string text = "keyed(Ford, X) + keyed(Hulls, \"Barge\", Yard) + hex()";
        var line = WorkingLine.Render(P(text), text, v => v == "X" ? Value.Of("0412") : null);
        Assert.Equal("keyed(Ford, 0412) + keyed(Hulls, \"Barge\", Yard) + hex()", line);
    }
}
