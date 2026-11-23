using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Rules.Tests;

public class DiceChartsTests
{
    private static TableDef Chart() => new()
    {
        Id = "t1", Name = "Crash", Kind = TableKind.TwoD,
        RowKeys = new() { "1", "2", "3-4" },
        ColumnKeys = new() { "A", "B", "10" },
        Cells = new()
        {
            new() { "1-2", "3-4", "5+" },
            new() { "1-3", "4-6", "7+" },
            new() { "1-3", "", "4+" },
        },
    };

    private sealed class Ctx : IEvalContext
    {
        public List<int> Dice = new() { 1, 2, 5, 6 };
        public Value GetVariable(string name) => Value.Of(1);
        public Value Lookup(string tableName, Value row, Value? col) => Value.Of(1);
        public IReadOnlyList<int> RollDice(int n, int sides) => new[] { 1 };
        public bool Has(string property) => true;
        public Value PieceName() => Value.Of("x");
        public Value Marker(string key) => Value.Of("x");
        public Value LookupRange(string table, Value row, Value value) => TableLookup.RangeColumn(Chart(), row, value);
        public IReadOnlyList<int> DiceOf(string variable) => Dice;
    }

    private sealed class PlainCtx : IEvalContext
    {
        public Value GetVariable(string name) => Value.Of(1);
        public Value Lookup(string tableName, Value row, Value? col) => Value.Of(1);
        public IReadOnlyList<int> RollDice(int n, int sides) => new[] { 1 };
        public bool Has(string property) => true;
        public Value PieceName() => Value.Of("x");
        public Value Marker(string key) => Value.Of("x");
    }

    [Fact]
    public void LookupRange_finds_column_key()
    {
        var v = TableLookup.RangeColumn(Chart(), Value.Of(2), Value.Of(5));
        Assert.Equal(ValueKind.Text, v.Kind);
        Assert.Equal("B", v.Text);
    }

    [Fact]
    public void LookupRange_numeric_column_key_is_a_number()
    {
        var v = TableLookup.RangeColumn(Chart(), Value.Of(2), Value.Of(9));
        Assert.Equal(ValueKind.Number, v.Kind);
        Assert.Equal(10, v.Number);
    }

    [Fact]
    public void LookupRange_first_match_open_ends()
    {
        Assert.Equal("A", TableLookup.RangeColumn(Chart(), Value.Of(3), Value.Of(2)).Text);   // row "3-4"; 1-3 first
        Assert.Equal(10, TableLookup.RangeColumn(Chart(), Value.Of(1), Value.Of(99)).Number); // 5+
        Assert.Equal("A", TableLookup.RangeColumn(Chart(), Value.Of(2), Value.Of(1)).Text);
    }

    [Fact]
    public void LookupRange_empty_cell_never_matches()
    {
        var e = Assert.Throws<EvalError>(() => TableLookup.RangeColumn(Chart(), Value.Of(4), Value.Of(0)));
        Assert.Equal("No column of Crash row 4 holds 0", e.Message);
        Assert.Equal(10, TableLookup.RangeColumn(Chart(), Value.Of(4), Value.Of(5)).Number);
    }

    [Fact]
    public void LookupRange_no_match_error()
    {
        var e = Assert.Throws<EvalError>(() => TableLookup.RangeColumn(Chart(), Value.Of(1), Value.Of(0)));
        Assert.Equal("No column of Crash row 1 holds 0", e.Message);
    }

    [Fact]
    public void LookupRange_needs_two_way_table()
    {
        var t = new TableDef { Id = "t", Name = "One", Kind = TableKind.OneD, RowKeys = new() { "1" }, ColumnKeys = new() { "Value" }, Cells = new() { new() { "1" } } };
        var e = Assert.Throws<EvalError>(() => TableLookup.RangeColumn(t, Value.Of(1), Value.Of(1)));
        Assert.Equal("lookuprange needs a two-way table", e.Message);
    }

    [Fact]
    public void LookupRange_evaluates_through_a_context()
        => Assert.Equal("B", Evaluator.Evaluate(Parser.Parse("lookuprange(Crash, 2, 5)"), new Ctx()).Text);

    [Fact]
    public void LookupRange_default_context_not_available()
    {
        var e = Assert.Throws<EvalError>(() => Evaluator.Evaluate(Parser.Parse("lookuprange(Crash, 2, 5)"), new PlainCtx()));
        Assert.Equal("lookuprange() is not available here", e.Message);
    }

    [Fact]
    public void Count_counts_in_range()
        => Assert.Equal(2, Evaluator.Evaluate(Parser.Parse("count(R, 1, 2)"), new Ctx()).Number);

    [Fact]
    public void Count_default_context_not_available()
    {
        var e = Assert.Throws<EvalError>(() => Evaluator.Evaluate(Parser.Parse("count(R, 1, 2)"), new PlainCtx()));
        Assert.Equal("count() is not available here", e.Message);
    }

    [Fact]
    public void Count_and_lookuprange_arity_and_names()
    {
        Assert.Throws<ParseError>(() => Parser.Parse("count(R, 1)"));
        Assert.Throws<ParseError>(() => Parser.Parse("count(1, 1, 2)"));
        Assert.Throws<ParseError>(() => Parser.Parse("lookuprange(Crash, 1)"));
        Assert.Throws<ParseError>(() => Parser.Parse("lookuprange(1, 1, 2)"));
        Assert.Equal(new[] { "R" }, Parser.Variables(Parser.Parse("count(R, 1, 2)")));
        Assert.Equal(new[] { "X", "Y" }, Parser.Variables(Parser.Parse("lookuprange(Crash, X, Y)")));
    }

    // ---- validator ----

    private static RulesPack Pack(FormulaDef f) => new()
    {
        Properties = new() { new PropertyDef { Id = "p1", Name = "Fuel", Scope = PropertyScope.Both } },
        Tables = new() { Chart() },
        Formulas = new() { f },
    };
    private static VariableBinding In(string v) => new() { Variable = v, Kind = BindingKind.Input };
    private static VariableBinding Roll(string v) => new() { Variable = v, Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6 };
    private static FormulaDef Fx(string text, params VariableBinding[] b) => new() { Id = "f1", Name = "Landing", Text = text, Bindings = b.ToList() };
    private static List<string> Msgs(FormulaDef f) => RulesValidator.Validate(Pack(f)).Where(i => i.EntityId == "f1").Select(i => i.Message).ToList();

    [Fact]
    public void Validator_count_needs_roll_binding()
    {
        Assert.Contains("Formula \"Landing\": count() needs a roll binding, not N", Msgs(Fx("count(N, 1, 2)", In("N"))));
        Assert.DoesNotContain(Msgs(Fx("count(R, 1, 2)", Roll("R"))), m => m.Contains("count()"));
    }

    [Fact]
    public void Validator_dice_count_expr_unknown_variable()
    {
        var f = Fx("R", Roll("R") with { DiceCountExpr = "Zork + 1" });
        Assert.Contains("Formula \"Landing\": variable \"Zork\" has no binding", Msgs(f));
    }

    [Fact]
    public void Validator_dice_count_expr_may_not_read_itself_and_must_parse()
    {
        Assert.Contains("Formula \"Landing\": variable \"R\" dice count cannot use R itself", Msgs(Fx("R", Roll("R") with { DiceCountExpr = "R + 1" })));
        Assert.Contains(Msgs(Fx("R", Roll("R") with { DiceCountExpr = "1 +" })), m => m.StartsWith("Formula \"Landing\": variable \"R\" dice count "));
    }

    [Fact]
    public void Validator_dice_count_expr_ok_and_marks_variables_used()
    {
        var msgs = Msgs(Fx("R", In("Planes"), Roll("R") with { DiceCountExpr = "Planes" }));
        Assert.Empty(msgs);
    }

    [Fact]
    public void Validator_lookuprange_unknown_table()
    {
        Assert.Contains("Formula \"Landing\": no table named \"Nope\"", Msgs(Fx("lookuprange(Nope, 1, 2)")));
        Assert.DoesNotContain(Msgs(Fx("lookuprange(Crash, 1, 2)")), m => m.Contains("no table"));
    }

    [Fact]
    public void Used_includes_dice_count_expr_variables()
    {
        var f = Fx("R", In("Planes"), Roll("R") with { DiceCountExpr = "Planes" });
        Assert.Equal(new[] { "R", "Planes" }, FormulaVariables.Used(f));
        Assert.Contains("Planes", FormulaVariables.Sources(f.Bindings[1]));
    }

    [Fact]
    public void Used_skips_dice_count_expr_of_unused_binding_and_its_own_variable()
    {
        var f = Fx("A", In("A"), In("Planes"), Roll("R") with { DiceCountExpr = "Planes + R" });
        Assert.Equal(new[] { "A" }, FormulaVariables.Used(f));
    }
}

public class DiceChartsFixRound1Tests
{
    [Fact]
    public void ExprFacts_lookuprange_table_name_is_skipped_in_holder_check()
    {
        var pack = new RulesPack { Properties = new() { new PropertyDef { Id = "p", Name = "Crash", Scope = PropertyScope.Pieces } } };
        Assert.True(ExprFacts.IsGameLevel(Parser.Parse("lookuprange(Crash, 1, 2)"), pack));
    }

    [Fact]
    public void Validator_dice_count_expr_on_non_roll_is_flagged()
    {
        var f = new FormulaDef { Id = "f1", Name = "Landing", Text = "A", Bindings = new() { new VariableBinding { Variable = "A", Kind = BindingKind.Input, DiceCountExpr = "1" } } };
        var msgs = RulesValidator.Validate(new RulesPack { Formulas = new() { f } }).Where(i => i.EntityId == "f1").Select(i => i.Message).ToList();
        Assert.Contains("Formula \"Landing\": variable \"A\" has a dice count but is not a roll", msgs);
    }

    [Fact]
    public void KeyChoices_lookuprange_row_variable_gets_row_keys_value_does_not()
    {
        var t = new TableDef { Id = "t", Name = "Crash", Kind = TableKind.TwoD, RowKeys = new() { "1", "2" }, ColumnKeys = new() { "A" }, Cells = new() { new() { "1-3" }, new() { "4+" } } };
        var f = new FormulaDef { Id = "f", Name = "F", Text = "lookuprange(Crash, X, Y)", Bindings = new()
            { new VariableBinding { Variable = "X", Kind = BindingKind.Input }, new VariableBinding { Variable = "Y", Kind = BindingKind.Input } } };
        var pack = new RulesPack { Tables = new() { t } };
        Assert.Equal(new[] { "1", "2" }, KeyChoices.For(pack, f, "X"));
        Assert.Null(KeyChoices.For(pack, f, "Y"));
    }
}
