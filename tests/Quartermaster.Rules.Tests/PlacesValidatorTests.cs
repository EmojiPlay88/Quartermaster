using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Rules.Tests;

/// <summary>B6: the validator's keyed-property, keyed-output, keyed(), distance() and hex() checks, and the module overload.</summary>
public class PlacesValidatorTests
{
    // ---- a module with hex grids (invented): map "North" has two hex boards, map "South" a square one ----

    private const string V = "VASSAL.build.";
    private const string BP = "VASSAL.build.module.map.boardPicker.";
    private const string HexNum = "first=\"H\" hType=\"N\" vType=\"N\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\" stagger=\"true\"";

    private static string HexBoard(string name, string image) =>
        $"<{BP}Board name=\"{name}\" image=\"{image}\"><{BP}board.HexGrid x0=\"0\" y0=\"0\" dx=\"60\" dy=\"70\">" +
        $"<{BP}board.mapgrid.HexGridNumbering {HexNum}/></{BP}board.HexGrid></{BP}Board>";

    private static readonly Lazy<(ModuleModel Model, string Hex)> Fx = new(() =>
    {
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <{{V}}GameModule name="Fix" version="1" VassalVersion="3.7.1">
              <{{V}}module.Map mapName="North"><{{V}}module.map.BoardPicker>
                {{HexBoard("Riverside", "river.png")}}
                {{HexBoard("Delta", "delta.png")}}
              </{{V}}module.map.BoardPicker></{{V}}module.Map>
              <{{V}}module.Map mapName="South"><{{V}}module.map.BoardPicker>
                {{HexBoard("Shore", "shore.png")}}
              </{{V}}module.map.BoardPicker></{{V}}module.Map>
            </{{V}}GameModule>
            """;
        var fx = new FixtureModule().WithBuildFile(xml).AddImage("river.png", 600, 500).AddImage("delta.png", 300, 200).AddImage("shore.png", 300, 300);
        var archive = VmodArchive.Open(fx.Build());
        var model = ModuleModelBuilder.Build(archive, new DiagnosticLog());
        var hex = model.Maps[0].Boards[0].Grid!.LocationName(new PointD(120, 105));
        return (model, hex);
    });

    private static ModuleModel Module => Fx.Value.Model;
    private static string RealHex => Fx.Value.Hex;

    // ---- packs ----

    private static RulesPack Base() => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "p-ford", Name = "Ford", Scope = PropertyScope.Holders, KeyedBy = "hex", Holders = new() { "global" }, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-due", Name = "Due", Scope = PropertyScope.Holders, KeyedBy = "turn" },
            new PropertyDef { Id = "p-hulls", Name = "Hulls", Scope = PropertyScope.Holders, KeyedBy = "table:t-hulls", Holders = new() { "h1" } },
            new PropertyDef { Id = "p-load", Name = "Load", Scope = PropertyScope.Pieces, KeyedBy = "turn" },
            new PropertyDef { Id = "p-fuel", Name = "Fuel", Scope = PropertyScope.Pieces },
            new PropertyDef { Id = "p-supply", Name = "Supply", Scope = PropertyScope.Holders },
        },
        Holders = new() { new HolderDef { Id = "h1", Name = "Yard" }, new HolderDef { Id = "h2", Name = "Quay" } },
        Tables = new()
        {
            new TableDef { Id = "t-hulls", Name = "Hull types", Kind = TableKind.OneD, RowKeys = new() { "Barge", "Tug" }, Cells = new() { new() { "1" }, new() { "2" } } },
            new TableDef { Id = "t-range", Name = "Ranges", Kind = TableKind.OneD, RowKeys = new() { "1-3", "4-6" }, Cells = new() { new() { "1" }, new() { "2" } } },
        },
    };

    private static RulesPack WithProperty(string id, Func<PropertyDef, PropertyDef> edit)
    {
        var p = Base();
        return p with { Properties = p.Properties.Select(x => x.Id == id ? edit(x) : x).ToList() };
    }

    private static RulesPack WithFormula(string text, string? precondition = null, params FormulaOutput[] outputs)
    {
        var p = Base();
        return p with { Formulas = new() { new FormulaDef { Id = "f1", Name = "Shelling", Text = text, Precondition = precondition, Outputs = outputs.ToList() } } };
    }

    private static FormulaOutput W(string property, string? key, OutputTarget target = OutputTarget.Global, string? holder = null, OutputOp op = OutputOp.Set) =>
        new() { Target = target, HolderId = holder, Property = property, Op = op, Expr = "1", Key = key };

    private static IReadOnlyList<RulesIssue> Run(RulesPack p, ModuleModel? module = null) => RulesValidator.Validate(p, null, null, module);
    private static List<string> Errors(RulesPack p, ModuleModel? module = null) =>
        Run(p, module).Where(i => i.Severity == RulesIssueSeverity.Error).Select(i => i.Message).ToList();
    private static List<string> Warnings(RulesPack p) =>
        Run(p).Where(i => i.Severity == RulesIssueSeverity.Warning).Select(i => i.Message).ToList();
    private static void Has(List<string> errors, string tail) => Assert.Contains($"Formula \"Shelling\": {tail}", errors);

    [Fact]
    public void Base_pack_has_no_issues() => Assert.Empty(Run(Base()));

    // ---- property messages ----

    [Fact]
    public void Property_malformed_keyed_by() =>
        Assert.Contains("Property \"Ford\" is keyed by \"side\"; use table:<id>, turn or hex", Errors(WithProperty("p-ford", p => p with { KeyedBy = "side" })));

    [Fact]
    public void Property_keyed_by_unknown_table() =>
        Assert.Contains("Property \"Hulls\" is keyed by table \"nope\", which does not exist", Errors(WithProperty("p-hulls", p => p with { KeyedBy = "table:nope" })));

    [Fact]
    public void Property_keyed_by_range_table() =>
        Assert.Contains("Property \"Hulls\" is keyed by table \"Ranges\", whose row keys are ranges", Errors(WithProperty("p-hulls", p => p with { KeyedBy = "table:t-range" })));

    [Fact]
    public void Property_keyed_and_computed() =>
        Assert.Contains("Property \"Due\" is keyed and computed; remove one", Errors(WithProperty("p-due", p => p with { Expr = "1" })));

    [Fact]
    public void Property_keyed_with_starting_value_or_sheet_field()
    {
        var msg = "Property \"Load\" is keyed, so it cannot have a starting value or a sheet field";
        Assert.Contains(msg, Errors(WithProperty("p-load", p => p with { DefaultExpr = "1" })));
        Assert.Contains(msg, Errors(WithProperty("p-load", p => p with { SheetField = "Cargo" })));
    }

    [Fact]
    public void Property_keyed_cannot_override_table() =>
        Assert.Contains("Property \"Load\" is keyed, so it cannot override a table", Errors(WithProperty("p-load", p => p with { OverridesTable = "t-hulls" })));

    [Fact]
    public void Property_keyed_by_hex_must_be_holder_property() =>
        Assert.Contains("Property \"Load\" is keyed by hex, so it must be a holder property", Errors(WithProperty("p-load", p => p with { KeyedBy = "hex" })));

    [Fact]
    public void Property_keyed_by_hex_only_global_keeps_it()
    {
        var msg = "Property \"Ford\" is keyed by hex, so only the Global holder keeps it";
        Assert.Contains(msg, Errors(WithProperty("p-ford", p => p with { Holders = null })));
        Assert.Contains(msg, Errors(WithProperty("p-ford", p => p with { Holders = new() { "global", "h1" } })));
        Assert.DoesNotContain(msg, Errors(Base()));
    }

    [Fact]
    public void Property_keyed_outliner_warning()
    {
        Assert.Contains("Property \"Due\" is keyed, so the outliner does not total it", Warnings(WithProperty("p-due", p => p with { Outliner = OutlinerAggregate.Sum })));
        Assert.DoesNotContain(Warnings(Base()), w => w.Contains("outliner"));
    }

    // ---- output messages ----

    [Fact]
    public void Output_keyed_without_key() =>
        Has(Errors(WithFormula("1", null, W("p-ford", null))), "output 1 writes keyed property \"Ford\" but has no key");

    [Fact]
    public void Output_key_on_unkeyed_property() =>
        Has(Errors(WithFormula("1", null, W("p-supply", "1"))), "output 1 has a key but writes property \"Supply\", which is not keyed");

    [Fact]
    public void Output_key_without_a_written_value() =>
        Has(Errors(WithFormula("1", null, new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Remove, Key = "1" })), "output 1 has a key but does not write a value");

    [Fact]
    public void Output_property_list_with_keyed_property()
    {
        var o = new FormulaOutput { Target = OutputTarget.Global, Op = OutputOp.Set, Expr = "1", PropertyChoice = new() { "p-supply", "p-due" }, PropertySelect = "1" };
        Has(Errors(WithFormula("1", null, o)), "output 1 lists keyed property \"Due\"; a property list cannot hold keyed properties");
    }

    [Fact]
    public void Output_place_property_to_other_holder() =>
        Has(Errors(WithFormula("1", null, W("p-ford", "\"a\"", OutputTarget.Holder, "h1"))), "output 1 writes place property \"Ford\" to a holder other than Global");

    [Fact]
    public void Output_key_parse_error()
    {
        var e = Errors(WithFormula("1", null, W("p-due", "1 +")));
        Assert.Contains(e, m => m.StartsWith("Formula \"Shelling\": output 1 key ") && m.Contains("unexpectedly"));
    }

    [Fact]
    public void Output_constant_key_tails()
    {
        Has(Errors(WithFormula("1", null, W("p-hulls", "\"Ship\"", OutputTarget.Holder, "h1"))), "output 1 key \"Ship\" is not a row of table \"Hull types\"");
        Assert.DoesNotContain(Errors(WithFormula("1", null, W("p-hulls", "\"tug\"", OutputTarget.Holder, "h1"))), e => e.Contains("is not a row"));
        foreach (var k in new[] { "0", "1.5", "-2", "100001", "\"x\"" })
            Has(Errors(WithFormula("1", null, W("p-due", k))), "output 1 key must be a whole turn number of at least 1");
        Assert.DoesNotContain(Errors(WithFormula("1", null, W("p-due", "3"))), e => e.Contains("whole turn"));
    }

    [Fact]
    public void Output_constant_hex_key_checked_only_with_module()
    {
        var p = WithFormula("1", null, W("p-ford", "\"zzz\""));
        Has(Errors(p, Module), "output 1 key \"zzz\" is not a hex of any board");
        Assert.DoesNotContain(Errors(p), e => e.Contains("is not a hex"));
        Assert.DoesNotContain(Errors(WithFormula("1", null, W("p-ford", $"\"{RealHex}\"")), Module), e => e.Contains("is not a hex"));
        Assert.DoesNotContain(Errors(WithFormula("1", null, W("p-ford", "hex()")), Module), e => e.Contains("is not a hex"));
    }

    [Fact]
    public void Turn_key_expression_is_not_folded()
    {
        // turn() has no constant value: turn() - 5 is no error however small the current turn.
        Assert.Empty(Errors(WithFormula("1", null, W("p-due", "turn() - 5"))));
        Assert.Empty(Errors(WithFormula("keyed(Due, turn() - 5)")));
    }

    // ---- bindings, filters, aggregates ----

    [Fact]
    public void Binding_reads_keyed_property()
    {
        var p = WithFormula("F");
        p = p with { Formulas = new() { p.Formulas[0] with { Bindings = new() { new VariableBinding { Variable = "F", Kind = BindingKind.Global, Property = "p-ford" } } } } };
        Has(Errors(p), "variable \"F\" reads keyed property \"Ford\"; use keyed() in the formula");
        foreach (var kind in new[] { BindingKind.Unit, BindingKind.Piece })
        {
            var q = WithFormula("F");
            q = q with { Formulas = new() { q.Formulas[0] with { Bindings = new() { new VariableBinding { Variable = "F", Kind = kind, Property = "p-load" } } } } };
            Has(Errors(q), "variable \"F\" reads keyed property \"Load\"; use keyed() in the formula");
        }
    }

    [Fact]
    public void Filter_bare_keyed_name() =>
        Assert.Contains("Property \"Fuel\": applies-to reads keyed property \"Ford\"; use keyed()", Errors(WithProperty("p-fuel", p => p with { AppliesTo = "Ford > 1" })));

    [Fact]
    public void Aggregate_over_keyed() =>
        Has(Errors(WithFormula("sum(Load, \"\")")), "sum() cannot total keyed property \"Load\"");

    // ---- keyed() ----

    [Fact]
    public void Keyed_unknown_property()
    {
        Has(Errors(WithFormula("keyed(Nope, 1)")), "refers to unknown property \"Nope\"");
        Has(Errors(WithFormula("keyed(\"Ford\", 1)")), "keyed() needs a property name");
    }

    [Fact]
    public void Keyed_not_keyed() =>
        Has(Errors(WithFormula("keyed(Fuel, 1)")), "keyed() reads \"Fuel\", which is not keyed");

    [Fact]
    public void Keyed_unknown_holder() =>
        Has(Errors(WithFormula("keyed(Hulls, \"Barge\", Nope)")), "refers to unknown holder \"Nope\"");

    [Fact]
    public void Keyed_holder_not_kept() =>
        Has(Errors(WithFormula("keyed(Hulls, \"Barge\", Quay)")), "refers to \"Hulls\", which holder \"Quay\" does not keep");

    [Fact]
    public void Keyed_constant_key_tails()
    {
        Has(Errors(WithFormula("keyed(Hulls, \"Ship\", Yard)")), "keyed() key \"Ship\" is not a row of table \"Hull types\"");
        Has(Errors(WithFormula("keyed(Due, 0)")), "keyed() key must be a whole turn number of at least 1");
        Has(Errors(WithFormula("keyed(Ford, \"zzz\")"), Module), "keyed() key \"zzz\" is not a hex of any board");
        Assert.DoesNotContain(Errors(WithFormula("keyed(Ford, \"zzz\")")), e => e.Contains("is not a hex"));
        Assert.Empty(Errors(WithFormula($"keyed(Ford, \"{RealHex}\")"), Module));
        Assert.Empty(Errors(WithFormula("keyed(Hulls, \"Tug\", Yard) + keyed(Due, 7)")));
    }

    [Fact]
    public void Keyed_reads_inside_a_key_are_checked() =>
        Has(Errors(WithFormula("1", null, W("p-due", "keyed(Nope, 1)"))), "refers to unknown property \"Nope\"");

    // ---- distance() ----

    [Fact]
    public void Distance_number_argument() =>
        Has(Errors(WithFormula("1", "distance(1, \"a\") > 0")), "distance() needs hex names as text");

    [Fact]
    public void Distance_constant_unknown_hex_with_module()
    {
        var p = WithFormula("1", $"distance(\"zzz\", \"{RealHex}\") > 0");
        Has(Errors(p, Module), "distance() names hex \"zzz\", which is not on any board");
        Assert.DoesNotContain(Errors(p), e => e.Contains("names hex"));
        Assert.Empty(Errors(WithFormula("1", $"distance(hex(), \"{RealHex}\") <= 3"), Module));
    }

    // ---- where the functions may not be used ----

    private static RulesPack WithCondition(string condition) =>
        Base() with { Phases = new() { new PhaseDef { Id = "ph1", Name = "Dawn", Condition = condition } } };

    [Fact]
    public void Hex_in_phase_condition_forbidden()
    {
        Assert.Contains("Phase \"Dawn\": condition may not use hex()", Errors(WithCondition("hex() == \"a\"")));
        Assert.Contains("Phase \"Dawn\": condition may not use distance()", Errors(WithCondition("distance(\"a\", \"b\") > 1")));
    }

    [Fact]
    public void Keyed_global_in_phase_condition_ok()
    {
        Assert.Empty(Errors(WithCondition("keyed(Due, turn()) > 0")));
        Assert.Empty(Errors(WithCondition("keyed(Hulls, \"Barge\") > 0")));
    }

    [Fact]
    public void Keyed_piece_property_in_phase_condition_error() =>
        Assert.Contains("Phase \"Dawn\": condition uses \"Load\", which is not a global property", Errors(WithCondition("keyed(Load, 1) > 0")));

    [Fact]
    public void Hex_in_property_applies_to_forbidden()
    {
        var e = Errors(WithProperty("p-fuel", p => p with { AppliesTo = "distance(hex(), \"a\") > 1" }));
        Assert.Contains("Property \"Fuel\": hex() is not available in a property's applies-to", e);
        Assert.Contains("Property \"Fuel\": distance() is not available in a property's applies-to", e);
    }

    [Fact]
    public void Hex_in_side_filter_forbidden()
    {
        var p = Base() with { Sides = new() { new SideDef { Id = "s1", Name = "Red", UnitFilter = "distance(hex(), \"a\") > 1" } } };
        var e = Errors(p);
        Assert.Contains("Side \"Red\": hex() is not available in a side's unit filter", e);
        Assert.Contains("Side \"Red\": distance() is not available in a side's unit filter", e);
    }

    [Fact]
    public void Hex_in_starting_value_forbidden() =>
        Assert.Contains("Property \"Fuel\": hex() is not available in a starting value", Errors(WithProperty("p-fuel", p => p with { DefaultExpr = "hex()" })));

    // ---- Remove ToMapId ----

    private static RulesPack Remover(string toMap) =>
        WithFormula("1", null, new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Remove, ToMapId = toMap });

    [Fact]
    public void ToMapId_by_name_valid_with_module()
    {
        Assert.DoesNotContain(Errors(Remover("North"), Module), e => e.Contains("map"));
        Assert.DoesNotContain(Errors(Remover("South"), Module), e => e.Contains("map"));
    }

    [Fact]
    public void ToMapId_with_spaces_valid()
    {
        Assert.DoesNotContain(RulesValidator.Validate(Remover("  m1 "), new[] { "m1" }, null).Select(i => i.Message), m => m.Contains("map"));
        Assert.DoesNotContain(Errors(Remover("  North "), Module), e => e.Contains("map"));
    }

    [Fact]
    public void ToMapId_unknown_with_module_error()
    {
        Has(Errors(Remover("Nowhere"), Module), "output 1 moves the piece to map \"Nowhere\", which does not exist in the module");
        Has(RulesValidator.Validate(Remover("Nowhere"), new[] { "m1" }, null).Select(i => i.Message).ToList(),
            "output 1 moves the piece to map \"Nowhere\", which does not exist in the module");
    }

    // ---- the module overload ----

    [Fact]
    public void Module_does_not_leak_between_calls()
    {
        var p = WithFormula("keyed(Ford, \"zzz\")");
        Assert.Contains(Run(p, Module), i => i.Message.Contains("is not a hex"));
        Assert.DoesNotContain(RulesValidator.Validate(p, null, null), i => i.Message.Contains("is not a hex"));
        Assert.DoesNotContain(RulesValidator.Validate(p), i => i.Message.Contains("is not a hex"));
        Assert.DoesNotContain(RulesValidator.Validate(p, null), i => i.Message.Contains("is not a hex"));
    }

    private static RulesPack OldPack() => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "p1", Name = "Fuel", Scope = PropertyScope.Pieces, AppliesTo = "Ghost > 1" },
            new PropertyDef { Id = "p2", Name = "fuel", Scope = PropertyScope.Holders, Labels = "t-none" },
            new PropertyDef { Id = "p3", Name = "Supply", Scope = PropertyScope.Holders, Holders = new() { "h9" } },
        },
        Holders = new() { new HolderDef { Id = "h1", Name = "Depot" } },
        Tables = new() { new TableDef { Id = "t1", Name = "Rates", Kind = TableKind.OneD, RowKeys = new() { "1", "" }, Cells = new() { new() { "a" }, new() { "b" } } } },
        Phases = new() { new PhaseDef { Id = "ph1", Name = "Dawn", Condition = "holder(Depot, Supply) > 1 or hexsum(Fuel) > 2" } },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f1", Name = "Shelling", Text = "A + Q", Precondition = "1 +",
                Bindings = new() { new VariableBinding { Variable = "A", Kind = BindingKind.Unit, Property = "p1" }, new VariableBinding { Variable = "Z", Kind = BindingKind.Input } },
                Outputs = new()
                {
                    new FormulaOutput { Target = OutputTarget.Unit, Property = "p1", Op = OutputOp.Subtract },
                    new FormulaOutput { Target = OutputTarget.Holder, HolderId = "h7", Property = "p3", Op = OutputOp.Add, Expr = "lookup(Nope, 1)" },
                    new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Remove, ToMapId = "m1" },
                },
            },
        },
    };

    // The issue list the pre-B6 validator gave for OldPack (commit 05076f3), with knownMapIds { "m2" } and knownSlots { "s1" }.
    private static readonly string[] OldIssues =
    {
        "Error|p2|fuel|Property \"fuel\": another property already has this name",
        "Error|p1|Fuel|Property \"Fuel\": applies-to refers to unknown property \"Ghost\"",
        "Error|p2|fuel|Property \"fuel\" takes its labels from a table that no longer exists",
        "Error|p3|Supply|Property \"Supply\" names holder \"h9\", which does not exist",
        "Error|t1|Rates|Table \"Rates\": a row key is empty",
        "Error|f1|Shelling|Formula \"Shelling\": precondition Formula ended unexpectedly at position 3",
        "Error|f1|Shelling|Formula \"Shelling\": variable \"Q\" has no binding",
        "Warning|f1|Shelling|Formula \"Shelling\": variable \"Z\" is bound but not used in the formula",
        "Error|f1|Shelling|Formula \"Shelling\": output refers to unknown holder \"h7\"",
        "Error|f1|Shelling|Formula \"Shelling\": no table named \"Nope\"",
        "Error|f1|Shelling|Formula \"Shelling\": output 3 moves the piece to map \"m1\", which does not exist in the module",
        "Error|ph1|Dawn|Phase \"Dawn\": condition may not use holder()",
        "Error|ph1|Dawn|Phase \"Dawn\": condition may not use hexsum()",
    };

    private static List<string> Lines(IReadOnlyList<RulesIssue> issues) => issues.Select(i => $"{i.Severity}|{i.EntityId}|{i.EntityName}|{i.Message}").ToList();

    [Fact]
    public void Three_arg_validate_unchanged_for_old_pack()
    {
        Assert.Equal(OldIssues, Lines(RulesValidator.Validate(OldPack(), new[] { "m2" }, new[] { "s1" })));
        Assert.Equal(OldIssues, Lines(RulesValidator.Validate(OldPack(), new[] { "m2" }, new[] { "s1" }, null)));
    }

    // ---- a pack using every B6 feature is clean ----

    private static RulesPack B6Pack(string hex) => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "p-ford", Name = "Ford", Scope = PropertyScope.Holders, KeyedBy = "hex", Labels = "t-states", Holders = new() { "global" }, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-due", Name = "Due", Scope = PropertyScope.Holders, KeyedBy = "turn" },
            new PropertyDef { Id = "p-hulls", Name = "Hulls", Scope = PropertyScope.Holders, KeyedBy = "table:t-hulls", Holders = new() { "h1" } },
            new PropertyDef { Id = "p-fuel", Name = "Fuel", Scope = PropertyScope.Pieces },
        },
        Holders = new() { new HolderDef { Id = "h1", Name = "Yard" } },
        Tables = new()
        {
            new TableDef { Id = "t-hulls", Name = "Hull types", Kind = TableKind.OneD, RowKeys = new() { "Barge", "Tug" }, Cells = new() { new() { "1" }, new() { "2" } } },
            new TableDef { Id = "t-states", Name = "Ford states", Kind = TableKind.OneD, RowKeys = new() { "1", "2", "3" }, Cells = new() { new() { "Shallow" }, new() { "Deep" }, new() { "Flooded" } } },
        },
        Phases = new()
        {
            new PhaseDef
            {
                Id = "ph1", Name = "Dawn", Condition = "keyed(Due, turn()) >= 0",
                OnStart = new() { new StartEntry { FormulaId = "f2" } },
                Warnings = new() { new PhaseWarning { Condition = $"keyed(Ford, \"{hex}\") > 2", Message = "The ford is flooded" } },
            },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f1", Name = "Flood the ford", Text = "U",
                Bindings = new() { new VariableBinding { Variable = "U", Kind = BindingKind.Unit, Property = "p-fuel" } },
                Outputs = new()
                {
                    new FormulaOutput { Target = OutputTarget.Global, Property = "p-ford", Op = OutputOp.Set, Expr = "3", Key = "hex()" },
                    new FormulaOutput { Target = OutputTarget.Global, Property = "p-ford", Op = OutputOp.Set, Expr = "2", Key = $"\"{hex}\"" },
                },
            },
            new FormulaDef
            {
                Id = "f2", Name = "Schedule barge", Text = "keyed(Hulls, \"Tug\", Yard) + keyed(Due, turn())",
                Outputs = new()
                {
                    new FormulaOutput { Target = OutputTarget.Global, Property = "p-due", Op = OutputOp.Add, Expr = "1", Key = "turn() + 2" },
                    new FormulaOutput { Target = OutputTarget.Holder, HolderId = "h1", Property = "p-hulls", Op = OutputOp.Add, Expr = "1", Key = "\"Barge\"" },
                },
            },
            new FormulaDef { Id = "f3", Name = "Signal the lighthouse", Text = "1", Precondition = $"distance(hex(), \"{hex}\") <= 3" },
        },
    };

    [Fact]
    public void Valid_b6_pack_has_no_issues()
    {
        Assert.Empty(Run(B6Pack("0507")));
        Assert.Empty(Run(B6Pack(RealHex), Module));
    }
}
