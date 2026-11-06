namespace Quartermaster.Rules.Tests;

public class ValidatorTests
{
    private static RulesPack Clean() => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "p1", Name = "Fuel", Scope = PropertyScope.Both },
            new PropertyDef { Id = "p2", Name = "Steps", Scope = PropertyScope.Pieces },
        },
        Holders = new() { new HolderDef { Id = "h1", Name = "Depot" } },
        Tables = new()
        {
            new TableDef { Id = "t1", Name = "Rates", Kind = TableKind.OneD, RowKeys = new() { "1-3", "4+" }, ColumnKeys = new() { "Value" },
                Cells = new() { new() { "1" }, new() { "2" } } },
        },
        RuleRefs = new() { new RuleRef { Id = "r1", Label = "Rule 1", DocumentEntry = "rules.pdf", Page = 3 } },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f1", Name = "Fuel Use", Text = "MP * Rate", RuleRefId = "r1", AppliesTo = "has(Steps)",
                Bindings = new()
                {
                    new VariableBinding { Variable = "MP", Kind = BindingKind.Input, Min = 0, Max = 9 },
                    new VariableBinding { Variable = "Rate", Kind = BindingKind.Table, TableId = "t1", RowExpr = "MP" },
                },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = "h1", Property = "Fuel", Op = OutputOp.Subtract } },
            },
        },
    };

    private static RulesPack WithFormula(FormulaDef f) => Clean() with { Formulas = new() { f } };
    private static FormulaDef Fx(string text, params VariableBinding[] b) =>
        new() { Id = "f1", Name = "Fuel Use", Text = text, Bindings = b.ToList() };
    private static VariableBinding In(string v) => new() { Variable = v, Kind = BindingKind.Input };

    [Fact] public void Validate_CleanPack_NoIssues() => Assert.Empty(RulesValidator.Validate(Clean()));

    [Fact]
    public void Validate_UnboundVariable_Error()
    {
        var i = Assert.Single(RulesValidator.Validate(WithFormula(Fx("MP * Rate", In("Rate")))));
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        Assert.Equal("f1", i.EntityId);
        Assert.Equal("Formula \"Fuel Use\": variable \"MP\" has no binding", i.Message);
    }

    [Fact]
    public void Validate_UnusedBinding_Warning()
    {
        var i = Assert.Single(RulesValidator.Validate(WithFormula(Fx("A", In("A"), In("Z")))));
        Assert.Equal(RulesIssueSeverity.Warning, i.Severity);
        Assert.Contains("\"Z\"", i.Message);
    }

    [Fact]
    public void Validate_UnknownTableReference_Reported()
    {
        var bound = Fx("Rate", new VariableBinding { Variable = "Rate", Kind = BindingKind.Table, TableId = "nope", RowExpr = "1" });
        var i1 = Assert.Single(RulesValidator.Validate(WithFormula(bound)));
        Assert.Equal(RulesIssueSeverity.Error, i1.Severity);
        Assert.Contains("nope", i1.Message);

        var inline = Fx("lookup(Ratez, 2)");
        var i2 = Assert.Single(RulesValidator.Validate(WithFormula(inline)));
        Assert.Equal("Formula \"Fuel Use\": no table named \"Ratez\"", i2.Message);
    }

    [Fact]
    public void Validate_ParseError_HasPosition()
    {
        var i = Assert.Single(RulesValidator.Validate(WithFormula(Fx("1 + * 2"))));
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        Assert.Contains("position 4", i.Message);
    }

    [Fact]
    public void Validate_OverlappingRanges_Warns()
    {
        var p = Clean();
        p = p with { Tables = new() { p.Tables[0] with { RowKeys = new() { "1-5", "4-8", "20" } , Cells = new() { new() { "1" }, new() { "2" }, new() { "3" } } } } };
        var i = Assert.Single(RulesValidator.Validate(p));
        Assert.Equal(RulesIssueSeverity.Warning, i.Severity);
        Assert.Contains("1-5", i.Message);
        Assert.Contains("4-8", i.Message);
    }

    [Fact]
    public void Validate_EmptyKey_Error()
    {
        var p = Clean();
        p = p with { Tables = new() { p.Tables[0] with { RowKeys = new() { "1", " " } } } };
        Assert.Contains(RulesValidator.Validate(p), i => i.Severity == RulesIssueSeverity.Error && i.EntityId == "t1");
    }

    [Fact]
    public void Validate_DuplicateNames_Error()
    {
        var p = Clean();
        p = p with { Properties = new() { p.Properties[0], p.Properties[1] with { Name = "FUEL" } } };
        var i = Assert.Single(RulesValidator.Validate(p));
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        Assert.Contains("FUEL", i.Message);
    }

    [Fact]
    public void Validate_OutputScopeMismatch_Error()
    {
        var f = Fx("A", In("A")) with
        {
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "Steps", Op = OutputOp.Set } },
        };
        var i = Assert.Single(RulesValidator.Validate(WithFormula(f)));
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        Assert.Contains("Steps", i.Message);
    }

    [Fact]
    public void Validate_BindingProblems_Reported()
    {
        var f = Fx("A + B + C + D",
            new VariableBinding { Variable = "A", Kind = BindingKind.Input, Min = 5, Max = 1 },
            new VariableBinding { Variable = "B", Kind = BindingKind.Roll, DiceCount = 0, DiceSides = 6 },
            new VariableBinding { Variable = "C", Kind = BindingKind.Table, TableId = "t1" },
            new VariableBinding { Variable = "D", Kind = BindingKind.Global, Property = "Nothing" });
        var msgs = RulesValidator.Validate(WithFormula(f)).Select(x => x.Message).ToList();
        Assert.Equal(4, msgs.Count);
        Assert.Contains(msgs, m => m.Contains("\"A\""));
        Assert.Contains(msgs, m => m.Contains("\"B\""));
        Assert.Contains(msgs, m => m.Contains("\"C\""));
        Assert.Contains(msgs, m => m.Contains("Nothing"));
    }

    [Fact]
    public void Validate_BadAppliesTo_And_MissingRuleRef()
    {
        var f = Fx("A", In("A")) with { AppliesTo = "1 +", RuleRefId = "zz" };
        var msgs = RulesValidator.Validate(WithFormula(f)).ToList();
        Assert.Equal(2, msgs.Count);
        Assert.Contains(msgs, m => m.Message.Contains("zz"));
    }
}
