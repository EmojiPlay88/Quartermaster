namespace Quartermaster.Rules.Tests;

/// <summary>B4: validator checks for the choices table and filter, row() placement and the group roll.</summary>
public class GroupRunsValidatorTests
{
    private static RulesPack Base(FormulaDef f, params TableDef[] tables) => new()
    {
        Format = 4,
        Id = "t",
        Name = "TEST",
        Properties = new()
        {
            new PropertyDef { Id = "p1", Name = "Size", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
            new PropertyDef { Id = "p2", Name = "Fuel", Scope = PropertyScope.Both, Visibility = Visibility.Public },
        },
        Tables = new List<TableDef>
        {
            new TableDef { Id = "t1", Name = "Cargo", Kind = TableKind.TwoD, RowKeys = new() { "Grain", "Salt" }, ColumnKeys = new() { "Min size" }, Cells = new() { new() { "1" }, new() { "2" } } },
        }.Concat(tables).ToList(),
        Phases = new() { new PhaseDef { Id = "ph1", Name = "Supply" } },
        Formulas = new() { f },
    };

    private static FormulaDef Formula(VariableBinding b, string text = "A") => new() { Id = "f1", Name = "Barge", Text = text, Bindings = new() { b } };
    private static VariableBinding Input(string? table = "t1", string? filter = null) => new() { Variable = "A", Kind = BindingKind.Input, ChoicesTable = table, ChoicesFilter = filter };
    private static List<RulesIssue> Issues(RulesPack p) => RulesValidator.Validate(p).ToList();
    private static List<string> Msgs(RulesPack p) => Issues(p).Select(i => i.Message).ToList();

    [Fact]
    public void ChoicesTable_unknown()
    {
        var i = Assert.Single(Issues(Base(Formula(Input("zz")))), x => x.Message.Contains("choices"));
        Assert.Equal("Formula \"Barge\": variable \"A\" draws its choices from unknown table \"zz\"", i.Message);
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
    }

    [Fact]
    public void ChoicesTable_not_input()
    {
        var b = new VariableBinding { Variable = "A", Kind = BindingKind.Unit, Property = "p1", ChoicesTable = "t1" };
        var i = Assert.Single(Issues(Base(Formula(b))), x => x.Message.Contains("choices"));
        Assert.Equal("Formula \"Barge\": variable \"A\" has a choices table but is not an Input", i.Message);
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
    }

    [Fact]
    public void ChoicesTable_with_choices()
    {
        var b = Input() with { Choices = new() { "x" } };
        var i = Assert.Single(Issues(Base(Formula(b))), x => x.Message.Contains("choices"));
        Assert.Equal("Formula \"Barge\": variable \"A\" has both choices and a choices table", i.Message);
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
    }

    [Fact]
    public void ChoicesTable_range_keys()
    {
        var ranges = new TableDef { Id = "t2", Name = "Bands", Kind = TableKind.OneD, RowKeys = new() { "1-3", "4+" }, ColumnKeys = new() { "v" }, Cells = new() { new() { "1" }, new() { "2" } } };
        var i = Assert.Single(Issues(Base(Formula(Input("t2")), ranges)), x => x.Message.Contains("choices"));
        Assert.Equal("Formula \"Barge\": variable \"A\" draws its choices from table \"Bands\", whose row keys are ranges", i.Message);
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        var plain = new TableDef { Id = "t3", Name = "Plain", Kind = TableKind.OneD, RowKeys = new() { "1", "2", "Grain" }, ColumnKeys = new() { "v" }, Cells = new() { new() { "1" }, new() { "2" }, new() { "3" } } };
        Assert.DoesNotContain(Msgs(Base(Formula(Input("t3")), plain)), m => m.Contains("choices"));
    }

    [Fact]
    public void ChoicesFilter_without_table_warns()
    {
        var i = Assert.Single(Issues(Base(Formula(Input(null, "Size > 1")))), x => x.Message.Contains("choices"));
        Assert.Equal("Formula \"Barge\": variable \"A\" has a choices filter but no choices table", i.Message);
        Assert.Equal(RulesIssueSeverity.Warning, i.Severity);
    }

    [Fact]
    public void ChoicesFilter_parse_error()
    {
        var i = Assert.Single(Issues(Base(Formula(Input("t1", "Size >")))), x => x.Message.Contains("choices filter"));
        Assert.StartsWith("Formula \"Barge\": variable \"A\" choices filter ", i.Message);
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
    }

    [Fact]
    public void ChoicesFilter_unknown_property()
    {
        var m = Msgs(Base(Formula(Input("t1", "Nope > 1"))));
        Assert.Contains(m, x => x.StartsWith("Formula \"Barge\": variable \"A\" choices filter ", StringComparison.Ordinal) && x.Contains("Nope"));
    }

    [Fact]
    public void Row_outside_choices_filter()
    {
        const string Tail = "row() can only be used in a choices filter";
        var text = Msgs(Base(Formula(new VariableBinding { Variable = "A", Kind = BindingKind.Input }, "A + row()")));
        Assert.Contains(text, x => x.StartsWith("Formula \"Barge\": ", StringComparison.Ordinal) && x.EndsWith(Tail, StringComparison.Ordinal));

        var applies = Msgs(Base(Formula(Input()) with { AppliesTo = "row() > 1" }));
        Assert.Contains(applies, x => x.StartsWith("Formula \"Barge\": applies-to ", StringComparison.Ordinal) && x.EndsWith(Tail, StringComparison.Ordinal));

        var phase = Base(Formula(Input())) with { Phases = new() { new PhaseDef { Id = "ph1", Name = "Supply", Condition = "row() > 1" } } };
        Assert.Contains("Phase \"Supply\": condition may not use row()", Msgs(phase));
    }

    [Fact]
    public void GroupRoll_without_dice_warns()
    {
        var f = Formula(new VariableBinding { Variable = "A", Kind = BindingKind.Input }) with { GroupRoll = true };
        var i = Assert.Single(Issues(Base(f)), x => x.Message.Contains("once for the group"));
        Assert.Equal("Formula \"Barge\": rolls once for the group but has no dice", i.Message);
        Assert.Equal(RulesIssueSeverity.Warning, i.Severity);
        Assert.DoesNotContain(Msgs(Base(f with { GroupRoll = false })), m => m.Contains("once for the group"));
    }

    [Fact]
    public void GroupRoll_with_inline_dice_ok()
    {
        var inText = Formula(new VariableBinding { Variable = "A", Kind = BindingKind.Input }, "A + dice(1, 6)") with { GroupRoll = true };
        Assert.DoesNotContain(Msgs(Base(inText)), m => m.Contains("once for the group"));
        var inSource = Formula(new VariableBinding { Variable = "A", Kind = BindingKind.Input, AutoValue = "dice(1, 6)" }) with { GroupRoll = true };
        Assert.DoesNotContain(Msgs(Base(inSource)), m => m.Contains("once for the group"));
        var roll = new FormulaDef
        {
            Id = "f1", Name = "Barge", Text = "R", GroupRoll = true,
            Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 } },
        };
        Assert.DoesNotContain(Msgs(Base(roll)), m => m.Contains("once for the group"));
    }

    [Fact]
    public void Valid_b4_formula_has_no_issues()
    {
        var f = new FormulaDef
        {
            Id = "f1", Name = "Barge", Text = "A + R", GroupRoll = true,
            Bindings = new()
            {
                Input("t1", "lookup(Cargo, row(), \"Min size\") <= Size"),
                new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 },
            },
        };
        Assert.Empty(Msgs(Base(f)));
    }
}
