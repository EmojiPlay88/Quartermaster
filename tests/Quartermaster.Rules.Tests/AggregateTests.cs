using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests;

public class AggregateTests
{
    private sealed class Ctx : IEvalContext
    {
        public readonly List<(string Fn, string? Property, string? Filter)> Calls = new();
        public Value GetVariable(string name) => Value.Of(7);
        public Value Lookup(string tableName, Value row, Value? col) => Value.Of(1);
        public IReadOnlyList<int> RollDice(int n, int sides) => new[] { 1 };
        public bool Has(string property) => true;
        public Value PieceName() => Value.Of("x");
        public Value Marker(string key) => Value.Of("x");
        public Value Aggregate(string fn, string? property, string? filter) { Calls.Add((fn, property, filter)); return Value.Of(42); }
    }

    private sealed class PlainCtx : IEvalContext
    {
        public Value GetVariable(string name) => Value.Of(7);
        public Value Lookup(string tableName, Value row, Value? col) => Value.Of(1);
        public IReadOnlyList<int> RollDice(int n, int sides) => new[] { 1 };
        public bool Has(string property) => true;
        public Value PieceName() => Value.Of("x");
        public Value Marker(string key) => Value.Of("x");
    }

    private static string ParseError_(string text) => Assert.Throws<ParseError>(() => Parser.Parse(text)).Detail;

    [Theory]
    [InlineData("sum(Trucks, \"side() == 1\")")]
    [InlineData("sum(Trucks, \"\")")]
    [InlineData("countof(\"\")")]
    [InlineData("maxof(Trucks, \"a\")")]
    [InlineData("minof(Trucks, \"a\")")]
    [InlineData("hexsum(Trucks)")]
    [InlineData("hexsum(Trucks, \"a\")")]
    [InlineData("hexcount()")]
    [InlineData("hexcount(\"a\")")]
    [InlineData("hexmax(Trucks)")]
    [InlineData("hexmax(Trucks, \"a\")")]
    [InlineData("hexmin(Trucks)")]
    [InlineData("hexmin(Trucks, \"a\")")]
    [InlineData("SUM(Trucks, \"a\")")]
    public void Parses(string text) => Parser.Parse(text);

    [Theory]
    [InlineData("sum(Trucks)", "'sum' takes 2 arguments, not 1")]
    [InlineData("countof()", "'countof' takes 1 argument, not 0")]
    [InlineData("countof(\"a\", \"b\")", "'countof' takes 1 argument, not 2")]
    [InlineData("maxof(Trucks)", "'maxof' takes 2 arguments, not 1")]
    [InlineData("minof(Trucks, \"a\", \"b\")", "'minof' takes 2 arguments, not 3")]
    [InlineData("hexsum()", "'hexsum' takes 1 to 2 arguments, not 0")]
    [InlineData("hexcount(\"a\", \"b\")", "'hexcount' takes 0 to 1 arguments, not 2")]
    [InlineData("hexmax()", "'hexmax' takes 1 to 2 arguments, not 0")]
    [InlineData("hexmin(Trucks, \"a\", \"b\")", "'hexmin' takes 1 to 2 arguments, not 3")]
    public void Arity_errors(string text, string message) => Assert.Equal(message, ParseError_(text));

    [Theory]
    [InlineData("sum(\"Trucks\", \"a\")", "sum needs a property name")]
    [InlineData("sum(1, \"a\")", "sum needs a property name")]
    [InlineData("maxof(Trucks + 1, \"a\")", "maxof needs a property name")]
    [InlineData("minof(1, \"a\")", "minof needs a property name")]
    [InlineData("hexsum(\"a\")", "hexsum needs a property name")]
    [InlineData("hexmax(1)", "hexmax needs a property name")]
    [InlineData("hexmin(\"a\", \"b\")", "hexmin needs a property name")]
    [InlineData("sum(Trucks, Fuel)", "sum needs a text filter")]
    [InlineData("sum(Trucks, 1 + 1)", "sum needs a text filter")]
    [InlineData("countof(Fuel)", "countof needs a text filter")]
    [InlineData("maxof(Trucks, Fuel)", "maxof needs a text filter")]
    [InlineData("minof(Trucks, 3)", "minof needs a text filter")]
    [InlineData("hexsum(Trucks, Fuel)", "hexsum needs a text filter")]
    [InlineData("hexcount(Fuel)", "hexcount needs a text filter")]
    [InlineData("hexmax(Trucks, 2)", "hexmax needs a text filter")]
    [InlineData("hexmin(Trucks, Fuel)", "hexmin needs a text filter")]
    public void Argument_kind_errors(string text, string message) => Assert.Equal(message, ParseError_(text));

    [Fact]
    public void Variables_exclude_property_names_and_filters()
    {
        Assert.Equal(new[] { "Fuel" }, Parser.Variables(Parser.Parse("sum(Trucks, \"Steps > 1\") + Fuel")));
        Assert.Equal(new[] { "A" }, Parser.Variables(Parser.Parse("hexsum(Trucks) + countof(\"x\") + hexcount() + maxof(Steps, \"\") + hexmin(Steps, \"y\") + A")));
        Assert.Empty(Parser.Variables(Parser.Parse("minof(Trucks, \"\") + hexmax(Trucks, \"\")")));
    }

    [Fact]
    public void Evaluator_dispatches_to_the_context()
    {
        var c = new Ctx();
        Assert.Equal(42, Evaluator.Evaluate(Parser.Parse("sum(Trucks, \"side() == 1\")"), c).Number);
        Evaluator.Evaluate(Parser.Parse("countof(\"\")"), c);
        Evaluator.Evaluate(Parser.Parse("maxof(Trucks, \"a\")"), c);
        Evaluator.Evaluate(Parser.Parse("minof(Trucks, \"b\")"), c);
        Evaluator.Evaluate(Parser.Parse("hexsum(Trucks)"), c);
        Evaluator.Evaluate(Parser.Parse("hexsum(Trucks, \"c\")"), c);
        Evaluator.Evaluate(Parser.Parse("hexcount()"), c);
        Evaluator.Evaluate(Parser.Parse("hexcount(\"d\")"), c);
        Evaluator.Evaluate(Parser.Parse("hexmax(Trucks)"), c);
        Evaluator.Evaluate(Parser.Parse("hexmin(Trucks, \"e\")"), c);
        Assert.Equal(new (string, string?, string?)[]
        {
            ("sum", "Trucks", "side() == 1"), ("countof", null, ""), ("maxof", "Trucks", "a"), ("minof", "Trucks", "b"),
            ("hexsum", "Trucks", null), ("hexsum", "Trucks", "c"), ("hexcount", null, null), ("hexcount", null, "d"),
            ("hexmax", "Trucks", null), ("hexmin", "Trucks", "e"),
        }, c.Calls);
    }

    [Theory]
    [InlineData("sum(Trucks, \"a\")", "sum")]
    [InlineData("countof(\"a\")", "countof")]
    [InlineData("maxof(Trucks, \"a\")", "maxof")]
    [InlineData("minof(Trucks, \"a\")", "minof")]
    [InlineData("hexsum(Trucks)", "hexsum")]
    [InlineData("hexcount()", "hexcount")]
    [InlineData("hexmax(Trucks)", "hexmax")]
    [InlineData("hexmin(Trucks)", "hexmin")]
    public void Default_context_is_not_available(string text, string fn)
    {
        var e = Assert.Throws<EvalError>(() => Evaluator.Evaluate(Parser.Parse(text), new PlainCtx()));
        Assert.Equal($"{fn}() is not available here", e.Message);
    }

    [Fact]
    public void Result_is_the_contexts_value_in_arithmetic()
        => Assert.Equal(44, Evaluator.Evaluate(Parser.Parse("sum(Trucks, \"\") + 2"), new Ctx()).Number);

    [Fact]
    public void Functions_are_listed()
    {
        foreach (var n in new[] { "sum", "countof", "maxof", "minof", "hexsum", "hexcount", "hexmax", "hexmin" })
            Assert.Contains(n, Evaluator.FunctionNames);
    }

    // ---- working line and layout ----

    private static string Work(string f) => WorkingLine.Render(Parser.Parse(f), f, n => n == "Fuel" ? Value.Of(3) : null);

    [Fact]
    public void WorkingLine_prints_property_name_and_filter()
    {
        Assert.Equal("sum(Trucks, \"side() == 1\") + 3", Work("sum(Trucks, \"side() == 1\") + Fuel"));
        Assert.Equal("countof(\"\")", Work("countof(\"\")"));
        Assert.Equal("hexmax(Trucks)", Work("hexmax(Trucks)"));
        Assert.Equal("hexcount()", Work("hexcount()"));
        Assert.Equal("hexmin(Trucks, \"x\")", Work("hexmin(Trucks, \"x\")"));
    }

    private static RulesPack Pack() => new()
    {
        Properties = new() { new PropertyDef { Id = "p1", Name = "Trucks", Scope = PropertyScope.Pieces } },
    };

    [Fact]
    public void Layout_property_name_is_not_a_box()
    {
        var f = new FormulaDef
        {
            Id = "f1", Name = "F", Text = "sum(Trucks, \"a\") + hexcount() + Fuel",
            Bindings = new() { new VariableBinding { Variable = "Fuel", Kind = BindingKind.Input } },
        };
        var m = FormulaLayout.Build(f, Pack());
        Assert.Equal(new TextItem("sum(Trucks, \"a\") + hexcount() + "), m.Main[0]);
        Assert.Equal("Fuel", Assert.IsType<BoxItem>(m.Main[1]).Variable);
        Assert.Equal(2, m.Main.Count);
        Assert.Empty(m.SubRows);
    }

    // ---- validator ----

    private static RulesPack ValPack() => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "p1", Name = "Trucks", Scope = PropertyScope.Pieces },
            new PropertyDef { Id = "p2", Name = "Supply", Scope = PropertyScope.Holders },
        },
    };

    private static List<RulesIssue> Check(FormulaDef f) => RulesValidator.Validate(ValPack() with { Formulas = new() { f } }).ToList();
    private static FormulaDef F(string text, string? applies = null) => new() { Id = "f1", Name = "Reserve", Text = text, AppliesTo = applies };

    [Fact]
    public void Validator_accepts_good_aggregates()
    {
        Assert.Empty(Check(F("sum(Trucks, \"\") + countof(\"Trucks > 1\") + maxof(Trucks, \"\") + minof(Trucks, \"\") + hexsum(Trucks) + hexcount() + hexmax(Trucks, \"\") + hexmin(Trucks)")));
    }

    [Theory]
    [InlineData("sum")]
    [InlineData("maxof")]
    [InlineData("minof")]
    public void Validator_unknown_property_whole_game(string fn)
    {
        var i = Assert.Single(Check(F($"{fn}(Nope, \"\")")));
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        Assert.Equal($"Formula \"Reserve\": {fn}() names unknown property \"Nope\"", i.Message);
    }

    [Theory]
    [InlineData("hexsum(Nope)", "hexsum")]
    [InlineData("hexmax(Nope, \"\")", "hexmax")]
    [InlineData("hexmin(Nope)", "hexmin")]
    public void Validator_unknown_property_hex(string text, string fn)
        => Assert.Equal($"Formula \"Reserve\": {fn}() names unknown property \"Nope\"", Assert.Single(Check(F(text))).Message);

    [Theory]
    [InlineData("sum(Trucks, \"1 +\")", "sum")]
    [InlineData("countof(\"(\")", "countof")]
    [InlineData("hexcount(\"1 +\")", "hexcount")]
    public void Validator_filter_does_not_parse(string text, string fn)
    {
        var i = Assert.Single(Check(F(text)));
        Assert.StartsWith($"Formula \"Reserve\": the filter of {fn}() does not parse: ", i.Message);
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
    }

    [Fact]
    public void Validator_filter_with_unknown_property_is_reported()
        => Assert.Equal("Formula \"Reserve\": the filter of countof() refers to unknown property \"Nope\"",
            Assert.Single(Check(F("countof(\"Nope > 1\")"))).Message);

    [Fact]
    public void Validator_property_and_filter_are_not_unbound_variables()
        => Assert.Empty(Check(F("sum(Trucks, \"Trucks > 1\")")));

    [Fact]
    public void Validator_each_problem_reported_once()
        => Assert.Single(Check(F("sum(Nope, \"\") + sum(Nope, \"\")")));

    [Fact]
    public void Validator_applies_to_filter_checks_aggregates()
    {
        var i = Assert.Single(Check(F("1", "sum(Nope, \"\") > 1")));
        Assert.Equal("Formula \"Reserve\": applies-to sum() names unknown property \"Nope\"", i.Message);
    }

    [Fact]
    public void Validator_alert_condition_checks_aggregates()
    {
        var a = new AlertDef { Id = "a1", Name = "Low", Condition = "countof(\"1 +\") < 3", Message = "m" };
        var i = Assert.Single(RulesValidator.Validate(ValPack() with { Alerts = new() { a } }));
        Assert.StartsWith("Alert \"Low\": condition the filter of countof() does not parse: ", i.Message);
    }

    [Fact]
    public void Validator_phase_condition_allows_whole_game_forbids_hex()
    {
        RulesPack P(string cond) => ValPack() with { Phases = new() { new PhaseDef { Id = "ph1", Name = "Supply", Condition = cond } } };
        Assert.Empty(RulesValidator.Validate(P("sum(Trucks, \"\") > 3 and countof(\"Trucks > 1\") > 0")));
        Assert.Equal("Phase \"Supply\": condition may not use hexsum()", Assert.Single(RulesValidator.Validate(P("hexsum(Trucks) > 3"))).Message);
        Assert.Equal("Phase \"Supply\": condition sum() names unknown property \"Nope\"", Assert.Single(RulesValidator.Validate(P("sum(Nope, \"\") > 3"))).Message);
        Assert.Contains(RulesValidator.Validate(P("maxof(Trucks, \"1 +\") > 3")), i => i.Message.StartsWith("Phase \"Supply\": condition the filter of maxof() does not parse: "));
    }

    [Theory]
    [InlineData("sum(Supply, \"\")", "sum")]
    [InlineData("hexmin(Supply)", "hexmin")]
    public void Validator_holder_property_is_not_a_piece_property(string text, string fn)
        => Assert.Equal($"Formula \"Reserve\": {fn}() needs a piece property, not \"Supply\"", Assert.Single(Check(F(text))).Message);

    [Fact]
    public void Validator_side_unit_filter_refuses_hex_aggregates()
    {
        var side = new SideDef { Id = "red", Name = "Red", UnitFilter = "hexcount() > 1 and countof(\"hexsum(Trucks) > 0\") >= 0" };
        var msgs = RulesValidator.Validate(ValPack() with { Sides = new() { side } }).Select(i => i.Message).ToList();
        Assert.Equal(new[] { "Side \"Red\": hexcount() is not available in a side's unit filter", "Side \"Red\": hexsum() is not available in a side's unit filter" }, msgs);
        Assert.Empty(RulesValidator.Validate(ValPack() with { Sides = new() { side with { UnitFilter = "countof(\"Trucks > 1\") > 0" } } }));
    }

    [Fact]
    public void Validator_property_applies_to_refuses_hex_aggregates()
    {
        var pack = ValPack();
        pack.Properties.Add(new PropertyDef { Id = "p3", Name = "Ammo", Scope = PropertyScope.Pieces, AppliesTo = "hexsum(Trucks) > 1" });
        Assert.Equal("Property \"Ammo\": hexsum() is not available in a property's applies-to", Assert.Single(RulesValidator.Validate(pack)).Message);
    }
}
