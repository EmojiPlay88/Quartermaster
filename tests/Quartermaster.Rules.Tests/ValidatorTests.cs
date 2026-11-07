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
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = "h1", Property = "p1", Op = OutputOp.Subtract } },
            },
        },
    };

    private static RulesPack WithFormula(FormulaDef f) => Clean() with { Formulas = new() { f } };
    private static FormulaDef Fx(string text, params VariableBinding[] b) =>
        new() { Id = "f1", Name = "Fuel Use", Text = text, Bindings = b.ToList() };
    private static VariableBinding In(string v) => new() { Variable = v, Kind = BindingKind.Input };

    [Fact]
    public void Validator_FlagsBadOwners()
    {
        var sides = new List<SideDef> { new() { Id = "s1", Name = "Red" } };
        var ok = Clean() with { Sides = sides, MapOwners = new() { new MapOwner { MapId = "m1", SideId = "s1" } } };
        Assert.Empty(RulesValidator.Validate(ok, new[] { "m1" }));
        Assert.Empty(RulesValidator.Validate(ok)); // no module: map ids unchecked

        var badHolder = Clean() with { Sides = sides, Holders = new() { new HolderDef { Id = "h1", Name = "Depot", SideId = "zz" } } };
        var i1 = Assert.Single(RulesValidator.Validate(badHolder));
        Assert.Equal("h1", i1.EntityId);
        Assert.Equal("Holder \"Depot\": side \"zz\" does not exist", i1.Message);

        var badSide = ok with { MapOwners = new() { new MapOwner { MapId = "m1", SideId = "zz" } } };
        Assert.Equal("Map owner \"m1\": side \"zz\" does not exist", Assert.Single(RulesValidator.Validate(badSide, new[] { "m1" })).Message);

        var badMap = RulesValidator.Validate(ok, new[] { "other" });
        Assert.Equal("Map owner \"m1\": map does not exist in the module", Assert.Single(badMap).Message);

        var dupe = ok with { MapOwners = new() { new MapOwner { MapId = "m1", SideId = "s1" }, new MapOwner { MapId = "m1", SideId = "s1" } } };
        Assert.Equal("Map owner \"m1\": map has more than one owner", Assert.Single(RulesValidator.Validate(dupe)).Message);
    }

    [Fact]
    public void Validator_FlagsBadAlert()
    {
        AlertDef A(string id, string name, string cond, string? side = null) =>
            new() { Id = id, Name = name, Condition = cond, Message = "m", SideId = side };
        RulesIssue One(AlertDef a) => Assert.Single(RulesValidator.Validate(Clean() with { Alerts = new() { a } }));

        Assert.Empty(RulesValidator.Validate(Clean() with { Alerts = new() { A("a1", "Low", "Fuel < 3") } }));
        var parse = One(A("a1", "Low", "Fuel < < 3"));
        Assert.Equal(RulesIssueSeverity.Error, parse.Severity);
        Assert.Equal("a1", parse.EntityId);
        Assert.StartsWith("Alert \"Low\": condition ", parse.Message);
        Assert.Equal("Alert \"Low\": condition refers to unknown property \"Ammo\"", One(A("a1", "Low", "Ammo < 3")).Message);
        Assert.Equal("Alert \"Low\": side \"zz\" does not exist", One(A("a1", "Low", "Fuel < 3", "zz")).Message);
        Assert.Equal("Alert has no name", One(A("a1", "", "Fuel < 3")).Message);
        Assert.Equal("Alert \"Low\": condition is empty", One(A("a1", "Low", " ")).Message);
        var dup = RulesValidator.Validate(Clean() with { Alerts = new() { A("a1", "X", "Fuel<1"), A("a1", "x", "Fuel<1") } });
        Assert.Contains(dup, i => i.Message.Contains("another alert already has this name"));
        Assert.Contains(dup, i => i.Message.Contains("duplicate id"));
    }

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
        p = p with { Properties = new() { p.Properties[0], p.Properties[1], new PropertyDef { Id = "p3", Name = "FUEL", Scope = PropertyScope.Both } } };
        var i = Assert.Single(RulesValidator.Validate(p));
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        Assert.Contains("FUEL", i.Message);
    }

    [Fact]
    public void Validate_OutputScopeMismatch_Error()
    {
        var f = Fx("A", In("A")) with
        {
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "p2", Op = OutputOp.Set } },
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
            new VariableBinding { Variable = "D", Kind = BindingKind.Global, Property = "gone" });
        var msgs = RulesValidator.Validate(WithFormula(f)).Select(x => x.Message).ToList();
        Assert.Equal(4, msgs.Count);
        Assert.Contains(msgs, m => m.Contains("\"A\""));
        Assert.Contains(msgs, m => m.Contains("\"B\""));
        Assert.Contains(msgs, m => m.Contains("\"C\""));
        Assert.Contains(msgs, m => m.Contains("no longer exists"));
    }

    [Fact]
    public void Validate_RenamedProperty_StaysValid()
    {
        var p = Clean();
        p = p with { Properties = new() { p.Properties[0] with { Name = "Petrol" }, p.Properties[1] } };
        Assert.Empty(RulesValidator.Validate(p));
    }

    [Fact]
    public void Validate_BadAppliesTo_And_MissingRuleRef()
    {
        var f = Fx("A", In("A")) with { AppliesTo = "1 +", RuleRefId = "zz" };
        var msgs = RulesValidator.Validate(WithFormula(f)).ToList();
        Assert.Equal(2, msgs.Count);
        Assert.Contains(msgs, m => m.Message.Contains("zz"));
    }

    [Fact]
    public void Validate_FormulaAppliesTo_UnknownPropertyRefs_AreErrors()
    {
        var f = Fx("A", In("A")) with { Name = "Walk", AppliesTo = "Ghost > 1 and has(\"Gone\") and has(Steps) and Fuel > 0" };
        var msgs = RulesValidator.Validate(WithFormula(f)).Select(m => m.Message).ToList();
        Assert.Contains("Formula \"Walk\": applies-to refers to unknown property \"Ghost\"", msgs);
        Assert.Contains("Formula \"Walk\": applies-to refers to unknown property \"Gone\"", msgs);
        Assert.Equal(2, msgs.Count);
    }

    [Fact]
    public void Validate_PropertyAppliesTo_UnknownPropertyRef_IsError()
    {
        var p = Clean();
        p = p with { Properties = new() { p.Properties[0] with { AppliesTo = "has(Nope)" }, p.Properties[1] } };
        var msgs = RulesValidator.Validate(p).Select(m => m.Message).ToList();
        Assert.Contains("Property \"Fuel\": applies-to refers to unknown property \"Nope\"", msgs);
    }

    private static PhaseDef Ph(string id, string name = "") => new() { Id = id, Name = name == "" ? id : name };
    private static IReadOnlyList<RulesIssue> V(RulesPack p) => RulesValidator.Validate(p);

    [Fact]
    public void Validator_PhaseDanglingIds_Errors()
    {
        var ph = Ph("a", "Alpha") with
        {
            SideId = "nope", RuleRefId = "gone",
            OnStart = new() { new StartEntry { FormulaId = "zz" } },
            OnMove = new() { new MoveEntry { FormulaId = "yy" } },
        };
        var msgs = V(Clean() with { Phases = new() { ph } }).Where(i => i.EntityId == "a").Select(i => i.Message).ToList();
        Assert.Contains("Phase \"Alpha\": formula \"zz\" does not exist", msgs);
        Assert.Contains("Phase \"Alpha\": formula \"yy\" does not exist", msgs);
        Assert.Contains("Phase \"Alpha\": side \"nope\" does not exist", msgs);
        Assert.Contains("Phase \"Alpha\": rule reference \"gone\" does not exist", msgs);
    }

    [Fact]
    public void Validator_UnparsableConditionAndFilters_Errors()
    {
        var ph = Ph("a", "Alpha") with
        {
            Warnings = new() { new PhaseWarning { Condition = "1 + * 2", Message = "m" }, new PhaseWarning { Condition = "Nope > 1", Message = "" } },
            OnStart = new() { new StartEntry { FormulaId = "f1", UnitFilter = "1 +" } },
            OnMove = new() { new MoveEntry { FormulaId = "f1", UnitFilter = "has(Zork)" } },
        };
        var side = new SideDef { Id = "s", Name = "Red", UnitFilter = "* 2" };
        var msgs = V(Clean() with { Phases = new() { ph }, Sides = new() { side } }).Select(i => i.Message).ToList();
        Assert.Contains(msgs, m => m.StartsWith("Phase \"Alpha\": warning condition ") && !m.Contains("unknown"));
        Assert.Contains("Phase \"Alpha\": warning condition refers to unknown property \"Nope\"", msgs);
        Assert.Contains("Phase \"Alpha\": warning message is empty", msgs);
        Assert.Contains(msgs, m => m.StartsWith("Phase \"Alpha\": on-start filter "));
        Assert.Contains("Phase \"Alpha\": on-move filter refers to unknown property \"Zork\"", msgs);
        Assert.Contains(msgs, m => m.StartsWith("Side \"Red\": unit filter "));
    }

    [Fact]
    public void Validator_EmptySideFilter_WarnsWhenSeveralSides()
    {
        var red = new SideDef { Id = "r", Name = "Red" };
        var blue = new SideDef { Id = "b", Name = "Blue", UnitFilter = "has(Fuel)" };
        Assert.Empty(V(Clean() with { Sides = new() { red } }));            // one side: nothing to share
        var issue = Assert.Single(V(Clean() with { Sides = new() { red, blue } }));
        Assert.Equal(RulesIssueSeverity.Warning, issue.Severity);
        Assert.Equal("Side \"Red\": unit filter is empty, so it matches every unit", issue.Message);
    }

    [Fact]
    public void Validator_Blocks_OverlapReversedCountMissing()
    {
        var phases = new List<PhaseDef> { Ph("a"), Ph("b"), Ph("c") };
        var blocks = new List<PhaseBlock>
        {
            new() { FirstPhaseId = "a", LastPhaseId = "b", Count = 2, Label = "One" },
            new() { FirstPhaseId = "b", LastPhaseId = "c", Count = 2, Label = "Two" },
            new() { FirstPhaseId = "c", LastPhaseId = "a", Count = 2, Label = "Rev" },
            new() { FirstPhaseId = "c", LastPhaseId = "x", Count = 0, Label = "" },
        };
        var errs = V(Clean() with { Phases = phases, Blocks = blocks }).Where(i => i.Severity == RulesIssueSeverity.Error).Select(i => i.Message).ToList();
        Assert.Contains("Block \"One\" overlaps block \"Two\"", errs);
        Assert.Contains("Block \"Rev\": its first phase comes after its last phase", errs);
        Assert.Contains("Block \"(unnamed)\": phase \"x\" does not exist", errs);
        Assert.Contains("Block \"(unnamed)\": count must be at least 1", errs);
    }

    [Fact]
    public void Validator_OnMoveWithoutMovedBinding_Warning()
    {
        var ph = Ph("a", "Alpha") with { OnMove = new() { new MoveEntry { FormulaId = "f1" } } };
        var w = Assert.Single(V(Clean() with { Phases = new() { ph } }), i => i.Severity == RulesIssueSeverity.Warning);
        Assert.Equal("Phase \"Alpha\": on-move formula \"Fuel Use\" has no Moved binding", w.Message);
        Assert.Equal("a", w.EntityId);
    }

    [Fact]
    public void Validator_BadAutoValue_Error()
    {
        var f = Fx("A", new VariableBinding { Variable = "A", Kind = BindingKind.Input, AutoValue = "1 +" });
        var i = Assert.Single(V(WithFormula(f)));
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        Assert.StartsWith("Formula \"Fuel Use\": variable \"A\" auto value ", i.Message);
    }
}
