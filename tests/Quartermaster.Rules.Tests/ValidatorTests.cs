namespace Quartermaster.Rules.Tests;

public class ValidatorTests
{
    [Fact]
    public void RuleRef_ToLocalPath_IsAnErrorOnItsUsers()
    {
        var pack = new RulesPack
        {
            RuleRefs = new()
            {
                new RuleRef { Id = "r1", Label = "Supply", DocumentEntry = @"C:\Users\someone\Rules.pdf", Page = 2 },
                new RuleRef { Id = "r2", Label = "Orphan", DocumentEntry = @"\\server\share\x.pdf", Page = 1 },
                new RuleRef { Id = "r3", Label = "Fine", DocumentEntry = "scan:Rules.pdf", Page = 2 },
                new RuleRef { Id = "r4", Label = "Module", DocumentEntry = "docs/rules.pdf", Page = 1 },
            },
            Formulas = new() { new FormulaDef { Id = "f1", Name = "Supply roll", Text = "1", RuleRefId = "r1" } },
            Tables = new() { new TableDef { Id = "t1", Name = "Chart", Kind = TableKind.OneD, RowKeys = new() { "1" }, ColumnKeys = new() { "V" }, Cells = new() { new() { "1" } }, RuleRefId = "r3" } },
        };
        var issues = RulesValidator.Validate(pack).Where(i => i.Message.Contains("file on this computer")).ToList();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(RulesIssueSeverity.Error, i.Severity));
        Assert.Contains(issues, i => i.EntityId == "f1" && i.Message.StartsWith("Formula \"Supply roll\":") && i.Message.Contains("\"Rules.pdf\""));
        Assert.Contains(issues, i => i.EntityId == "r2");
        Assert.DoesNotContain(issues, i => i.Message.Contains(@"C:\Users"));   // the message names the file, not the path
        Assert.True(RulesValidator.IsLocalPath("/home/x/r.pdf"));
        Assert.True(RulesValidator.IsLocalPath("D:r.pdf"));
        Assert.False(RulesValidator.IsLocalPath("scan:r.pdf"));
        Assert.False(RulesValidator.IsLocalPath("docs/r.pdf"));
        Assert.False(RulesValidator.IsLocalPath(""));
    }

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

    private static VariableBinding Roll(string v) => new() { Variable = v, Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 };
    private static List<string> FormulaMsgs(RulesPack p) => V(p).Where(i => i.EntityId == "f1").Select(i => i.Message).ToList();

    private static FormulaDef WithOut(FormulaOutput o, params VariableBinding[] b) =>
        Fx("A", b.Prepend(In("A")).ToArray()) with { Outputs = new() { o } };
    private static FormulaOutput GOut() => new() { Target = OutputTarget.Global, Property = "p1", Op = OutputOp.Set };

    [Fact]
    public void Validator_output_choice_unknown_property()
    {
        var msgs = FormulaMsgs(WithFormula(WithOut(GOut() with { PropertyChoice = new() { "p1", "zz" }, PropertySelect = "A" })));
        Assert.Contains("Formula \"Fuel Use\": an output refers to a property that no longer exists", msgs);
        // scope is checked per id too: p2 is Pieces-only, the target is a holder
        var scope = FormulaMsgs(WithFormula(WithOut(GOut() with { PropertyChoice = new() { "p2" }, PropertySelect = "A" })));
        Assert.Contains(scope, m => m.Contains("output property \"Steps\" cannot be written to a holder"));
        // the fixed Property is not used (and not checked) when a list is given
        Assert.Empty(FormulaMsgs(WithFormula(WithOut(GOut() with { Property = "", PropertyChoice = new() { "p1" }, PropertySelect = "A" }))));
    }

    [Fact]
    public void Validator_output_choice_without_pick_by()
    {
        var msgs = FormulaMsgs(WithFormula(WithOut(GOut() with { PropertyChoice = new() { "p1" } })));
        Assert.Contains("Formula \"Fuel Use\": output 1 has a property list but no \"Pick by\"", msgs);
        var second = Fx("A", In("A")) with { Outputs = new() { GOut(), GOut() with { PropertyChoice = new() { "p1" }, PropertySelect = " " } } };
        Assert.Contains("Formula \"Fuel Use\": output 2 has a property list but no \"Pick by\"", FormulaMsgs(WithFormula(second)));

        // an empty list means the fixed property: no error
        Assert.Empty(FormulaMsgs(WithFormula(WithOut(GOut() with { PropertyChoice = new() }))));
    }

    [Fact]
    public void Validator_output_expr_unknown_variable()
    {
        var msgs = FormulaMsgs(WithFormula(WithOut(GOut() with { Expr = "A + Zork", PropertyChoice = new() { "p1" }, PropertySelect = "Quux" })));
        Assert.Contains("Formula \"Fuel Use\": variable \"Zork\" has no binding", msgs);
        Assert.Contains("Formula \"Fuel Use\": variable \"Quux\" has no binding", msgs);
        var bad = FormulaMsgs(WithFormula(WithOut(GOut() with { Expr = "A >", PropertyChoice = new() { "p1" }, PropertySelect = "A +" })));
        Assert.Contains(bad, m => m.StartsWith("Formula \"Fuel Use\": output 1 value "));
        Assert.Contains(bad, m => m.StartsWith("Formula \"Fuel Use\": output 1 pick-by "));
        // a variable read only by an output expression is used (no unused warning)
        var used = WithOut(GOut() with { Expr = "B" }, In("B"));
        Assert.DoesNotContain(FormulaMsgs(WithFormula(used)), m => m.Contains("not used"));
    }

    [Fact]
    public void Validator_precondition_parse_error()
    {
        var msgs = FormulaMsgs(WithFormula(Fx("A", In("A")) with { Precondition = "A >" }));
        Assert.Contains(msgs, m => m.StartsWith("Formula \"Fuel Use\": precondition "));
    }

    [Fact]
    public void Validator_precondition_unknown_variable()
    {
        var msgs = FormulaMsgs(WithFormula(Fx("A", In("A")) with { Precondition = "A > Zork" }));
        Assert.Contains("Formula \"Fuel Use\": variable \"Zork\" has no binding", msgs);
        Assert.DoesNotContain(msgs, m => m.Contains("\"A\""));   // bound, and used by the precondition
    }

    [Fact]
    public void Validator_when_unknown_variable()
    {
        var b = In("B") with { When = "Zork > 1" };
        var msgs = FormulaMsgs(WithFormula(Fx("A + B", In("A"), b)));
        Assert.Contains("Formula \"Fuel Use\": variable \"Zork\" has no binding", msgs);
        var bad = FormulaMsgs(WithFormula(Fx("A + B", In("A"), In("B") with { When = "A >" })));
        Assert.Contains(bad, m => m.StartsWith("Formula \"Fuel Use\": variable \"B\" only-when "));
    }

    [Fact]
    public void Validator_precondition_uses_roll()
    {
        var f = Fx("A + R", In("A"), Roll("R")) with { Precondition = "R > 3" };
        var issues = V(WithFormula(f)).Where(i => i.EntityId == "f1").ToList();
        var hit = Assert.Single(issues, i => i.Message == "Formula \"Fuel Use\": the precondition cannot use the roll R");
        Assert.Equal(RulesIssueSeverity.Error, hit.Severity);
    }

    [Fact]
    public void Validator_precondition_uses_dice()
    {
        var f = Fx("A", In("A")) with { Precondition = "dice(1, 6) > 3" };
        var issues = V(WithFormula(f)).Where(i => i.EntityId == "f1").ToList();
        var hit = Assert.Single(issues, i => i.Message == "Formula \"Fuel Use\": the precondition cannot use dice()");
        Assert.Equal(RulesIssueSeverity.Error, hit.Severity);
    }

    [Fact]
    public void Validator_precondition_uses_roll_through_table_row()
    {
        var t = new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t1", RowExpr = "R" };
        var f = Fx("A + T", In("A"), t, Roll("R")) with { Precondition = "T > 1" };
        Assert.Contains("Formula \"Fuel Use\": the precondition cannot use the roll R", FormulaMsgs(WithFormula(f)));
    }

    [Fact]
    public void Validator_precondition_uses_dice_through_auto_value()
    {
        var f = Fx("A", In("A") with { AutoValue = "dice(1, 6)" }) with { Precondition = "A > 1" };
        Assert.Contains("Formula \"Fuel Use\": the precondition cannot use dice()", FormulaMsgs(WithFormula(f)));
    }

    [Fact]
    public void Validator_guard_only_table_row_unknown_variable_and_no_unused_warning()
    {
        var t = new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t1", RowExpr = "Zork" };
        var msgs = FormulaMsgs(WithFormula(Fx("A", In("A"), t) with { Precondition = "T > 1" }));
        Assert.Contains("Formula \"Fuel Use\": variable \"Zork\" has no binding", msgs);
        Assert.DoesNotContain(msgs, m => m.Contains("bound but not used"));
    }

    [Fact]
    public void Validator_unbound_name_in_text_and_guard_reported_once()
    {
        var msgs = FormulaMsgs(WithFormula(Fx("Zork") with { Precondition = "Zork > 1" }));
        Assert.Single(msgs, m => m == "Formula \"Fuel Use\": variable \"Zork\" has no binding");
    }

    [Fact]
    public void Validator_guards_ok_and_blank_ignored()
    {
        var f = Fx("A + R", In("A"), Roll("R") with { When = "A > 1" }) with { Precondition = "A < 9", RefusalMessage = "no" };
        Assert.DoesNotContain(FormulaMsgs(WithFormula(f)), m => m.Contains("precondition") || m.Contains("no binding") || m.Contains("only-when"));
        var blank = Fx("A", In("A") with { When = " " }) with { Precondition = "  " };
        Assert.Empty(FormulaMsgs(WithFormula(blank)));
    }

    [Fact]
    public void Validator_OnEnd_checked_like_OnStart()
    {
        var ph = Ph("a", "Alpha") with
        {
            OnStart = new() { new StartEntry { FormulaId = "zz", UnitFilter = "Zork > 1" } },
            OnEnd = new() { new StartEntry { FormulaId = "zz", UnitFilter = "Zork > 1" } },
        };
        var msgs = V(Clean() with { Phases = new() { ph } }).Where(i => i.EntityId == "a").Select(i => i.Message).ToList();
        Assert.Equal(2, msgs.Count(m => m == "Phase \"Alpha\": formula \"zz\" does not exist"));
        Assert.Contains("Phase \"Alpha\": on-start filter refers to unknown property \"Zork\"", msgs);
        Assert.Contains("Phase \"Alpha\": on-end filter refers to unknown property \"Zork\"", msgs);
    }

    [Fact]
    public void Validator_formula_unknown_phase()
    {
        var pack = Clean() with { Phases = new() { Ph("a", "Alpha") }, Formulas = Clean().Formulas.Select(f => f with { Phases = new() { "a", "gone" } }).ToList() };
        var issues = V(pack).Where(i => i.Message.Contains("phase list")).ToList();
        Assert.NotEmpty(issues);
        Assert.All(issues, i => Assert.Equal(RulesIssueSeverity.Error, i.Severity));
        var f = pack.Formulas[0];
        Assert.Contains(issues, i => i.EntityId == f.Id && i.Message == $"Formula \"{f.Name}\": phase list names unknown phase \"gone\"");
        Assert.DoesNotContain(issues, i => i.Message.Contains("\"a\""));
    }

    [Fact]
    public void Validator_hook_outside_formula_phases()
    {
        var f = Clean().Formulas[0] with { Phases = new() { "other" } };
        var other = Ph("other", "Other");
        var ph = Ph("a", "Alpha") with
        {
            OnStart = new() { new StartEntry { FormulaId = f.Id } },
            OnMove = new() { new MoveEntry { FormulaId = f.Id } },
            OnEnd = new() { new StartEntry { FormulaId = f.Id } },
        };
        var pack = Clean() with { Phases = new() { ph, other }, Formulas = Clean().Formulas.Skip(1).Prepend(f).ToList() };
        var warns = V(pack).Where(i => i.Severity == RulesIssueSeverity.Warning && i.Message.Contains("limited to other phases")).ToList();
        foreach (var kind in new[] { "start", "move", "end" })
            Assert.Contains(warns, i => i.EntityId == "a" && i.Message == $"Phase \"Alpha\": {kind} entry runs \"{f.Name}\", which is limited to other phases");
        Assert.Equal(3, warns.Count);   // "Other" itself lists the formula, and runs nothing
        // in its own list, or no list: no warning
        var ok = V(pack with { Phases = new() { ph with { Id = "other", Name = "Other" } } });
        Assert.DoesNotContain(ok, i => i.Message.Contains("limited to other phases"));
    }

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

    private static AlertDef A(string id, string name, string cond, string? side = null) =>
        new() { Id = id, Name = name, Condition = cond, Message = "m", SideId = side };

    [Fact]
    public void Validator_LookupTableInFilters_IsNotAProperty()
    {
        const string f = "lookup(Rates, Steps) > 1";
        var ph = Ph("a", "Alpha") with
        {
            Warnings = new() { new PhaseWarning { Condition = f, Message = "m" } },
            OnStart = new() { new StartEntry { FormulaId = "f1", UnitFilter = f } },
            OnMove = new() { new MoveEntry { FormulaId = "f1", UnitFilter = f } },
        };
        var fx = Clean().Formulas[0] with { AppliesTo = f };
        var pack = Clean() with
        {
            Phases = new() { ph },
            Alerts = new() { A("a1", "Low", f) },
            Sides = new() { new SideDef { Id = "s", Name = "Red", UnitFilter = f } },
            Formulas = new() { fx },
        };
        pack = pack with { Properties = pack.Properties.Select(x => x.Name == "Fuel" ? x with { AppliesTo = f } : x).ToList() };
        var msgs = V(pack).Select(i => i.Message).ToList();
        Assert.DoesNotContain(msgs, m => m.Contains("Rates"));
    }

    [Fact]
    public void Validator_LookupUnknownTableInFilter_Error()
    {
        var ph = Ph("a", "Alpha") with { Warnings = new() { new PhaseWarning { Condition = "lookup(Nope, Steps) > 1", Message = "m" } } };
        var msgs = V(Clean() with { Phases = new() { ph } }).Select(i => i.Message).ToList();
        Assert.Contains("Phase \"Alpha\": warning condition refers to unknown table \"Nope\"", msgs);
        Assert.DoesNotContain(msgs, m => m.Contains("unknown property \"Nope\""));
    }

    private static List<string> AlertMsgs(string filter) =>
        V(Clean() with { Alerts = new() { A("a1", "Low", filter) } }).Select(i => i.Message).ToList();

    [Fact]
    public void Validator_filter_holder_unknown_holder()
    {
        var msgs = AlertMsgs("holder(Ghost, Fuel) > 1");
        Assert.Contains("Alert \"Low\": condition refers to unknown holder \"Ghost\"", msgs);
        Assert.DoesNotContain(msgs, m => m.Contains("unknown property"));
    }

    [Fact]
    public void Validator_filter_holder_unknown_property()
    {
        var msgs = AlertMsgs("holder(Depot, Ghost) > 1");
        Assert.Contains("Alert \"Low\": condition refers to unknown property \"Ghost\"", msgs);
        Assert.DoesNotContain(msgs, m => m.Contains("unknown holder"));
    }

    [Fact]
    public void Validator_filter_holder_pieces_property_is_not_a_holder_property()
    {
        var msgs = AlertMsgs("holder(Depot, Steps) > 1");
        Assert.Contains("Alert \"Low\": condition refers to \"Steps\", which is not a holder property", msgs);
    }

    [Theory]
    [InlineData("holder(Ghost, Fuel) + 1", "Formula \"Fuel Use\": refers to unknown holder \"Ghost\"")]
    [InlineData("holder(Depot, Ghost) + 1", "Formula \"Fuel Use\": refers to unknown property \"Ghost\"")]
    [InlineData("holder(Depot, Steps) + 1", "Formula \"Fuel Use\": refers to \"Steps\", which is not a holder property")]
    public void Validator_formula_text_holder_problems(string text, string expected)
    {
        var issues = V(WithFormula(Fx(text))).Where(i => i.Message.Contains("refers to")).ToList();
        var i = Assert.Single(issues);
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        Assert.Equal("f1", i.EntityId);
        Assert.Equal(expected, i.Message);
    }

    [Fact]
    public void Validator_repeated_holder_call_is_reported_once()
    {
        var b = new VariableBinding { Variable = "X", Kind = BindingKind.Input, AutoValue = "holder(Ghost, Fuel) + holder(Ghost, Fuel)" };
        var msgs = V(WithFormula(Fx("X + holder(Ghost, Fuel) * holder(Ghost, Fuel)", b))).Select(i => i.Message).ToList();
        // Once for the text and once for the auto value, never once per call.
        Assert.Equal(2, msgs.Count(m => m == "Formula \"Fuel Use\": refers to unknown holder \"Ghost\""));
    }

    [Fact]
    public void Validator_auto_value_holder_problems_and_clean_call()
    {
        var b = new VariableBinding { Variable = "X", Kind = BindingKind.Input, AutoValue = "holder(Ghost, Steps)" };
        var msgs = V(WithFormula(Fx("X", b))).Select(i => i.Message).ToList();
        Assert.Contains("Formula \"Fuel Use\": refers to unknown holder \"Ghost\"", msgs);
        Assert.Contains("Formula \"Fuel Use\": refers to \"Steps\", which is not a holder property", msgs);
        Assert.Empty(V(WithFormula(Fx("holder(Depot, Fuel) + 1"))));
    }

    private static RulesPack WithFlag(RulesPack p) =>
        p with { Properties = p.Properties.Append(new PropertyDef { Id = "p3", Name = "Flag", Scope = PropertyScope.Holders }).ToList() };

    [Fact]
    public void Validator_game_level_unit_message_is_a_notice()
    {
        var ph = Ph("a", "Alpha") with { Warnings = new() { new PhaseWarning { Condition = "Flag == 1", Message = "{unit}: flag is up" } } };
        var al = A("a1", "Low", "Flag == 1") with { Message = "Flag up on {unit}" };
        var issues = V(WithFlag(Clean()) with { Phases = new() { ph }, Alerts = new() { al } });
        Assert.All(issues, i => Assert.Equal(RulesIssueSeverity.Warning, i.Severity));
        Assert.Equal(new[]
        {
            "Phase \"Alpha\": warning shows once (its condition never reads the unit), so {unit} is left out",
            "Alert \"Low\": shows once (its condition never reads the unit), so {unit} is left out",
        }, issues.Select(i => i.Message).ToArray());

        // A condition that reads the unit, or a message without {unit}: no notice.
        var perUnit = Ph("a", "Alpha") with { Warnings = new() { new PhaseWarning { Condition = "Flag == 1 and Steps > 0", Message = "{unit}" } } };
        var plain = A("a1", "Low", "Flag == 1") with { Message = "Flag up" };
        Assert.Empty(V(WithFlag(Clean()) with { Phases = new() { perUnit }, Alerts = new() { plain } }));
    }

    [Fact]
    public void Validator_filter_holder_clean()
    {
        Assert.Empty(V(Clean() with { Alerts = new() { A("a1", "Low", "holder(Depot, Fuel) > 1") } }));
    }

    [Fact]
    public void Validator_side_filter_uses_side()
    {
        var side = new SideDef { Id = "s", Name = "Red", UnitFilter = "side() == \"Red\"" };
        var issue = Assert.Single(V(Clean() with { Sides = new() { side } }));
        Assert.Equal(RulesIssueSeverity.Error, issue.Severity);
        Assert.Equal("s", issue.EntityId);
        Assert.Equal("Side \"Red\": unit filter may not use side()", issue.Message);
    }

    [Fact]
    public void Validator_filter_new_functions_clean()
    {
        var f = "side() == \"North\" and map() == \"Main\" and phaseside() == \"\"";
        var fx = Clean().Formulas[0] with { AppliesTo = f };
        Assert.Empty(V(Clean() with { Formulas = new() { fx } }));
    }

    [Fact]
    public void Validator_LookupRowArgumentStillChecked()
    {
        var msgs = V(Clean() with { Alerts = new() { A("a1", "Low", "lookup(Rates, Ghost) > 1") } }).Select(i => i.Message).ToList();
        Assert.Contains("Alert \"Low\": condition refers to unknown property \"Ghost\"", msgs);
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
        var f = Fx("D", new VariableBinding { Variable = "D", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 });
        var w = Assert.Single(V(WithFormula(f) with { Phases = new() { ph } }), i => i.Severity == RulesIssueSeverity.Warning);
        Assert.Equal("Phase \"Alpha\": on-move formula \"Fuel Use\" has no Moved binding", w.Message);
        Assert.Equal("a", w.EntityId);
    }

    [Fact]
    public void Validator_OnMoveWithInputOrPropertyBinding_NoMovedWarning()
    {
        var ph = Ph("a", "Alpha") with { OnMove = new() { new MoveEntry { FormulaId = "f1" } } };
        Assert.DoesNotContain(V(Clean() with { Phases = new() { ph } }), i => i.Message.Contains("Moved binding"));
        var f = Fx("F", new VariableBinding { Variable = "F", Kind = BindingKind.Unit, Property = "p2" });
        Assert.DoesNotContain(V(WithFormula(f) with { Phases = new() { ph } }), i => i.Message.Contains("Moved binding"));
    }

    [Fact]
    public void Validator_VariableUsedOnlyInAutoValue_IsUsed()
    {
        var f = Fx("A", In("A") with { AutoValue = "B + 1" }, new VariableBinding { Variable = "B", Kind = BindingKind.Unit, Property = "p2" });
        Assert.Empty(V(WithFormula(f)));
    }

    [Fact]
    public void Validator_AutoValueOfUnusedBinding_DoesNotCountAsUse()
    {
        var f = Fx("A", In("A"), In("X") with { AutoValue = "B" }, new VariableBinding { Variable = "B", Kind = BindingKind.Unit, Property = "p2" });
        var msgs = V(WithFormula(f)).Select(i => i.Message).ToList();
        Assert.Contains("Formula \"Fuel Use\": variable \"X\" is bound but not used in the formula", msgs);
        Assert.Contains("Formula \"Fuel Use\": variable \"B\" is bound but not used in the formula", msgs);
    }

    [Fact]
    public void Validator_AutoValueSelfReference_NotUse()
    {
        var f = Fx("1", In("A") with { AutoValue = "A" });
        Assert.Contains(V(WithFormula(f)), i => i.Message.Contains("variable \"A\" is bound but not used in the formula"));
    }

    [Fact]
    public void Validator_AutoValueUnboundName_Error()
    {
        var f = Fx("A", In("A") with { AutoValue = "Ghost" });
        Assert.Contains(V(WithFormula(f)), i => i.Severity == RulesIssueSeverity.Error && i.Message == "Formula \"Fuel Use\": variable \"Ghost\" has no binding");
    }

    [Fact]
    public void Validator_AutoValueUnknownTable_Error()
    {
        var f = Fx("A", In("A") with { AutoValue = "lookup(Nope, 1)" });
        Assert.Contains(V(WithFormula(f)), i => i.Severity == RulesIssueSeverity.Error && i.Message.Contains("no table named \"Nope\""));
    }

    [Fact]
    public void Validator_BadAutoValue_Error()
    {
        var f = Fx("A", new VariableBinding { Variable = "A", Kind = BindingKind.Input, AutoValue = "1 +" });
        var i = Assert.Single(V(WithFormula(f)));
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        Assert.StartsWith("Formula \"Fuel Use\": variable \"A\" auto value ", i.Message);
    }

    // ---- phase conditions ----

    private static RulesPack CondPack(string? condition) => Clean() with
    {
        Properties = new()
        {
            new PropertyDef { Id = "pf", Name = "Flag", Scope = PropertyScope.Holders },
            new PropertyDef { Id = "pb", Name = "Both", Scope = PropertyScope.Both },
            new PropertyDef { Id = "pp", Name = "Steps", Scope = PropertyScope.Pieces },
        },
        Tables = new()
        {
            new TableDef { Id = "t1", Name = "T", Kind = TableKind.OneD, RowKeys = new() { "1-3", "4+" }, ColumnKeys = new() { "Value" },
                Cells = new() { new() { "1" }, new() { "2" } } },
        },
        Formulas = new(),
        Phases = new() { Ph("a", "Alpha") with { Condition = condition } },
    };

    private static List<string> CondMsgs(string? c) => V(CondPack(c)).Select(i => i.Message).ToList();

    [Fact]
    public void Validator_PhaseCondition_ParseError()
    {
        var issues = V(CondPack("1 + * 2"));
        var i = Assert.Single(issues);
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        Assert.Equal("a", i.EntityId);
        Assert.StartsWith("Phase \"Alpha\": condition ", i.Message);
    }

    [Fact]
    public void Validator_PhaseCondition_UnknownName()
    {
        var i = Assert.Single(V(CondPack("Nope > 1")));
        Assert.Equal(RulesIssueSeverity.Error, i.Severity);
        Assert.Equal("a", i.EntityId);
        Assert.Equal("Phase \"Alpha\": condition uses \"Nope\", which is not a global property", i.Message);
    }

    [Fact]
    public void Validator_PhaseCondition_PieceProperty_IsUnknown()
    {
        var i = Assert.Single(V(CondPack("Steps > 1")));
        Assert.Equal("Phase \"Alpha\": condition uses \"Steps\", which is not a global property", i.Message);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("marker")]
    [InlineData("has")]
    [InlineData("dice")]
    public void Validator_PhaseCondition_ForbiddenFunction(string fn)
    {
        var arg = fn == "dice" ? "1, 6" : fn == "name" ? "" : "\"x\"";
        var msgs = CondMsgs($"{fn}({arg}) == 1");
        Assert.Contains($"Phase \"Alpha\": condition may not use {fn}()", msgs);
    }

    [Theory]
    [InlineData("side() == \"x\"", "side")]
    [InlineData("map() == \"x\"", "map")]
    [InlineData("holder(H, X) > 1", "holder")]
    public void Validator_PhaseCondition_ReadFunctionsForbidden(string cond, string fn)
    {
        Assert.Contains($"Phase \"Alpha\": condition may not use {fn}()", CondMsgs(cond));
    }

    [Fact]
    public void Validator_PhaseCondition_Holder_NamesAreNotVariables()
    {
        Assert.Equal(new[] { "Phase \"Alpha\": condition may not use holder()" }, CondMsgs("holder(H, X) > 1"));
    }

    [Fact]
    public void Validator_PhaseCondition_Phaseside_Clean()
    {
        Assert.Empty(CondMsgs("phaseside() == \"North\""));
    }

    [Fact]
    public void Validator_PhaseCondition_ConstantFalse_Warning()
    {
        var i = Assert.Single(V(CondPack("1 > 2")));
        Assert.Equal(RulesIssueSeverity.Warning, i.Severity);
        Assert.Equal("a", i.EntityId);
        Assert.Equal("Phase \"Alpha\" never runs (its condition is always false)", i.Message);
        Assert.Empty(CondMsgs("1 < 2"));
        Assert.Empty(CondMsgs("turn() > 99"));
        Assert.Empty(CondMsgs("1 / 0 > 2"));
    }

    [Fact]
    public void Validator_PhaseCondition_Clean()
    {
        Assert.Empty(CondMsgs("Flag == 1 and mod(turn(), 4) == 1"));
        Assert.Empty(CondMsgs("lookup(T, turn()) > 0"));
        Assert.Empty(CondMsgs("Both > 0 or stage() == 2"));
        Assert.Empty(CondMsgs(null));
        Assert.Empty(CondMsgs("  "));
    }

    [Fact]
    public void Validator_PhaseCondition_UnknownTable()
    {
        Assert.Contains("Phase \"Alpha\": condition refers to unknown table \"Zip\"", CondMsgs("lookup(Zip, turn()) > 0"));
    }
}
