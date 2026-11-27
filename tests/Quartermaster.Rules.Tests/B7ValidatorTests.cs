using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Rules.Tests;

/// <summary>B7: the validator's Reveal output, margin, sealed entry, filter warning and schedule zone map checks.</summary>
public class B7ValidatorTests
{
    // A module with two maps (invented): id and name of each are looked up by MapRefs.Find.
    private static readonly Lazy<ModuleModel> Fx = new(() =>
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <VASSAL.build.GameModule name="Fix" version="1" VassalVersion="3.7.1">
              <VASSAL.build.module.Map mapName="Reserve Yard"/>
              <VASSAL.build.module.Map mapName="Ferry"/>
            </VASSAL.build.GameModule>
            """;
        var archive = VmodArchive.Open(new FixtureModule().WithBuildFile(xml).Build());
        return ModuleModelBuilder.Build(archive, new DiagnosticLog());
    });

    private static RulesPack Base() => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "p-strength", Name = "Strength", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-secret", Name = "Secret", Scope = PropertyScope.Pieces, Visibility = Visibility.Private },
            new PropertyDef { Id = "p-reserve", Name = "Reserve", Scope = PropertyScope.Holders, Holders = new() { "h-north" }, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-pool", Name = "Pool", Scope = PropertyScope.Holders, Holders = new() { "global" }, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-grain", Name = "Grain", Scope = PropertyScope.Holders, Holders = new() { "h-south" }, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-tollbid", Name = "TollBid", Scope = PropertyScope.Holders, KeyedBy = "table:t-sides", Holders = new() { "global" }, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-mines", Name = "Mines", Scope = PropertyScope.Holders, KeyedBy = "turn", Holders = new() { "global" }, Visibility = Visibility.Private },
            new PropertyDef { Id = "p-total", Name = "Total", Scope = PropertyScope.Pieces, Visibility = Visibility.Public, Expr = "Strength + 1" },
        },
        Holders = new()
        {
            new HolderDef { Id = "h-north", Name = "NorthDepot", SideId = "north" },
            new HolderDef { Id = "h-south", Name = "SouthDepot", SideId = "south" },
            new HolderDef { Id = "h-open", Name = "OpenDepot" },
        },
        Tables = new()
        {
            new TableDef { Id = "t-sides", Name = "Sides", Kind = TableKind.OneD, RowKeys = new() { "Northern League", "Southern Compact" }, Cells = new() { new() { "1" }, new() { "2" } } },
        },
        Sides = new() { new SideDef { Id = "north", Name = "Northern League", UnitFilter = "Strength > 0" }, new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "Strength > 0" } },
        Phases = new() { new PhaseDef { Id = "ph-toll", Name = "Toll" }, new PhaseDef { Id = "ph-move", Name = "March", SideId = "north" } },
        Formulas = new() { Bid() },
    };

    private static FormulaDef Bid(params FormulaOutput[] outputs) => new()
    {
        Id = "f-bid", Name = "Bid", Text = "amount",
        Bindings = new() { new VariableBinding { Variable = "amount", Kind = BindingKind.Input, Min = 0, Max = 9 } },
        Outputs = outputs.ToList(),
    };

    private static RulesPack With(RulesPack p, FormulaDef f) => p with { Formulas = p.Formulas.Select(x => x.Id == f.Id ? f : x).ToList() };
    private static RulesPack WithOutput(FormulaOutput o) => With(Base(), Bid(o));

    private static FormulaOutput R(string property = "p-strength", OutputTarget target = OutputTarget.Unit, string? margin = null) =>
        new() { Target = target, Property = property, Action = OutputAction.Reveal, Margin = margin };

    private static RulesPack WithEntry(StartEntry e, string phase = "ph-toll", Func<FormulaDef, FormulaDef>? edit = null, bool end = false)
    {
        var p = Base();
        if (edit != null) p = With(p, edit(p.Formulas[0]));
        return p with
        {
            Phases = p.Phases.Select(ph => ph.Id != phase ? ph : end ? ph with { OnEnd = new() { e } } : ph with { OnStart = new() { e } }).ToList(),
        };
    }

    private static IReadOnlyList<RulesIssue> Run(RulesPack p, ModuleModel? module = null) => RulesValidator.Validate(p, null, null, module);
    private static List<string> Errors(RulesPack p, ModuleModel? module = null) =>
        Run(p, module).Where(i => i.Severity == RulesIssueSeverity.Error).Select(i => i.Message).ToList();
    private static List<string> Warnings(RulesPack p) =>
        Run(p).Where(i => i.Severity == RulesIssueSeverity.Warning).Select(i => i.Message).ToList();
    private static void FormulaHas(List<string> list, string tail) => Assert.True(list.Contains($"Formula \"Bid\": {tail}"), $"missing: {tail}; got: {string.Join(" | ", list)}");
    private static void PhaseHas(List<string> list, string tail) => Assert.Contains($"Phase \"Toll\": {tail}", list);

    // ---- Reveal outputs ----

    [Fact] public void Reveal_no_property() => FormulaHas(Errors(WithOutput(R(property: ""))), "output 1 reveals no property");

    [Fact]
    public void Reveal_keyed_property() =>
        FormulaHas(Errors(WithOutput(R(property: "p-mines", target: OutputTarget.Global))), "output 1 reveals keyed property \"Mines\"; a reveal shows one value");

    [Fact]
    public void Reveal_with_value_expression() =>
        FormulaHas(Errors(WithOutput(R() with { Expr = "1" })), "output 1 reveals a value, so it takes no value expression");

    [Fact]
    public void Reveal_with_property_list() =>
        FormulaHas(Errors(WithOutput(R() with { PropertyChoice = new() { "p-strength" }, PropertySelect = "1" })),
            "output 1 reveals one property; a property list cannot be revealed");

    [Fact]
    public void Reveal_of_placed_counter() =>
        FormulaHas(Errors(WithOutput(R(target: OutputTarget.Placed))), "output 1 cannot reveal a value of the placed counter");

    [Fact]
    public void Margin_without_reveal() =>
        FormulaHas(Errors(WithOutput(new FormulaOutput { Target = OutputTarget.Unit, Property = "p-strength", Op = OutputOp.Set, Expr = "1", Margin = "2" })),
            "output 1 has a margin but does not reveal");

    [Fact]
    public void Margin_parse_problem() =>
        Assert.Contains(Errors(WithOutput(R(margin: "1 +"))), m => m.StartsWith("Formula \"Bid\": output 1 margin "));

    [Theory]
    [InlineData("-1")]
    [InlineData("1001")]
    [InlineData("2.5")]
    [InlineData("\"wide\"")]
    public void Margin_must_be_whole_number_in_range(string margin) =>
        FormulaHas(Errors(WithOutput(R(margin: margin))), "output 1 margin must be a whole number from 0 to 1000");

    [Fact]
    public void Margin_constant_folds()
    {
        Assert.DoesNotContain(Errors(WithOutput(R(margin: "1 + 1"))), m => m.Contains("margin"));
        Assert.DoesNotContain(Errors(WithOutput(R(margin: "1000"))), m => m.Contains("margin"));
        Assert.DoesNotContain(Errors(WithOutput(R(margin: "0"))), m => m.Contains("margin"));
        FormulaHas(Errors(WithOutput(R(margin: "500 + 501"))), "output 1 margin must be a whole number from 0 to 1000");
        // not constant: a variable, askside(); judged at run time
        Assert.DoesNotContain(Errors(WithOutput(R(margin: "amount"))), m => m.Contains("margin"));
        Assert.DoesNotContain(Errors(WithOutput(R(margin: "askside()"))), m => m.Contains("margin"));
    }

    [Fact]
    public void Margin_variable_needs_binding() =>
        FormulaHas(Errors(WithOutput(R(margin: "nope"))), "variable \"nope\" has no binding");

    [Fact]
    public void Reveal_property_checks_as_write()
    {
        FormulaHas(Errors(WithOutput(R(property: "p-gone"))), "output 1 refers to a property that no longer exists");
        FormulaHas(Errors(WithOutput(R(property: "p-grain", target: OutputTarget.Holder) with { HolderId = "h-north" })),
            "output 1 writes \"Grain\", which holder \"NorthDepot\" does not keep");
        // scope as a Write's: a holder property cannot be revealed on a unit
        FormulaHas(Errors(WithOutput(R(property: "p-reserve"))), "output property \"Reserve\" cannot be written to a unit (its scope is Holders)");
    }

    [Fact]
    public void Reveal_of_computed_property_is_valid() =>
        Assert.True(Run(WithOutput(R(property: "p-total"))).Count == 0, string.Join(" | ", Run(WithOutput(R(property: "p-total"))).Select(i => i.Message)));

    [Fact]
    public void Reveal_of_holder_and_global_values_is_valid()
    {
        Assert.Empty(Run(WithOutput(R(property: "p-reserve", target: OutputTarget.Holder) with { HolderId = "h-north" })));
        Assert.Empty(Run(WithOutput(R(property: "p-pool", target: OutputTarget.Global))));
    }

    [Fact]
    public void Reveal_with_key_reports_existing_key_text() =>
        FormulaHas(Errors(WithOutput(R() with { Key = "1" })), "output 1 has a key but does not write a value");

    // ---- sealed entries ----

    private static StartEntry S() => new() { FormulaId = "f-bid", Sealed = true };

    [Fact]
    public void Sealed_for_each_unit() =>
        PhaseHas(Errors(WithEntry(S() with { ForEach = ForEachMode.Units })), "start entry 1 is sealed, so it must run once, not for each unit");

    [Fact]
    public void Sealed_in_phase_with_side() =>
        Assert.Contains("Phase \"March\": start entry 1 is sealed, but only a phase without a side may have one", Errors(WithEntry(S(), phase: "ph-move")));

    [Fact]
    public void Sealed_with_asks_side() =>
        PhaseHas(Errors(WithEntry(S() with { AskSideId = "north" })), "start entry 1 is sealed, so it asks every side; leave Asks side empty");

    [Fact]
    public void Sealed_end_entry() =>
        PhaseHas(Errors(WithEntry(S(), end: true)), "end entry 1 is sealed; only a start entry can be sealed");

    [Fact]
    public void Sealed_formula_rolls_dice()
    {
        var withRoll = Errors(WithEntry(S(), edit: f => f with { Bindings = f.Bindings.Append(new VariableBinding { Variable = "d", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 }).ToList(), Text = "amount + d" }));
        PhaseHas(withRoll, "start entry 1 is sealed, but formula \"Bid\" rolls dice");
        var withCall = Errors(WithEntry(S(), edit: f => f with { Text = "amount + dice(1, 6)" }));
        PhaseHas(withCall, "start entry 1 is sealed, but formula \"Bid\" rolls dice");
    }

    [Fact]
    public void Sealed_formula_picks_a_piece() =>
        PhaseHas(Errors(WithEntry(S(), edit: f => f with { Bindings = f.Bindings.Append(new VariableBinding { Variable = "target", Kind = BindingKind.Piece, Property = "p-strength" }).ToList(), Text = "amount + target" })),
            "start entry 1 is sealed, but formula \"Bid\" picks a piece");

    [Fact]
    public void Sealed_formula_asks_for_nothing_warns()
    {
        var p = WithEntry(S(), edit: f => f with { Bindings = new() { new VariableBinding { Variable = "amount", Kind = BindingKind.Unit, Property = "p-strength" } } });
        PhaseHas(Warnings(p), "start entry 1 is sealed, but formula \"Bid\" asks for nothing");
        Assert.DoesNotContain(Errors(p), m => m.Contains("sealed"));
    }

    [Fact]
    public void Sealed_with_fewer_than_two_sides_warns()
    {
        var p = WithEntry(S());
        p = p with { Sides = p.Sides.Take(1).ToList() };
        PhaseHas(Warnings(p), "start entry 1 is sealed, but the pack has fewer than two sides");
    }

    [Fact]
    public void Valid_sealed_entry_has_no_issues() => Assert.Empty(Run(WithEntry(S())));

    // ---- filter warnings ----

    private static RulesPack WithBinding(VariableBinding b)
    {
        var p = Base();
        var f = p.Formulas[0];
        return With(p, f with { Bindings = new() { new VariableBinding { Variable = "amount", Kind = BindingKind.Input, Min = 0, Max = 9 }, b }, Text = "amount + other" });
    }

    private static VariableBinding Choice(string filter) =>
        new() { Variable = "other", Kind = BindingKind.Input, ChoicesTable = "t-sides", ChoicesFilter = filter };

    private static VariableBinding Pick(string filter) =>
        new() { Variable = "other", Kind = BindingKind.Piece, Property = "p-strength", PieceFilter = filter };

    private const string ChoicesWarning = "variable \"other\": its choices filter reads values another side may not see; each player chooses from what they can see";
    private const string PieceWarning = "variable \"other\": its piece filter reads values another side may not see; each player picks from what they can see";

    [Fact]
    public void Filter_warning_choices_reads_private()
    {
        FormulaHas(Warnings(WithBinding(Choice("keyed(Mines, 1) == 0"))), ChoicesWarning);
        FormulaHas(Warnings(WithBinding(Choice("Secret > 0"))), ChoicesWarning);
    }

    /// <summary>The pack with <paramref name="propertyId"/> made Private.</summary>
    private static RulesPack Private(RulesPack p, string propertyId) =>
        p with { Properties = p.Properties.Select(x => x.Id == propertyId ? x with { Visibility = Visibility.Private } : x).ToList() };

    [Fact]
    public void Filter_warning_piece_reads_sided_holder() =>
        FormulaHas(Warnings(Private(WithBinding(Pick("holder(NorthDepot, Reserve) > 0")), "p-reserve")), PieceWarning);

    [Fact]
    public void No_filter_warning_public_value_of_sided_holder()
    {
        // Final review F4: a Public value of a sided holder is seen by every side (#74a).
        Assert.DoesNotContain(Warnings(WithBinding(Pick("holder(NorthDepot, Reserve) > 0"))), m => m.Contains("may not see"));
        Assert.DoesNotContain(Warnings(WithBinding(Choice("keyed(TollBid, row(), SouthDepot) == 0"))), m => m.Contains("may not see"));
    }

    [Fact]
    public void No_filter_warning_for_unknown_keyed_property() =>
        Assert.DoesNotContain(Warnings(WithBinding(Choice("keyed(Nowhere, row()) == 0"))), m => m.Contains("may not see"));

    [Fact]
    public void Filter_warning_piece_reads_private_property() =>
        FormulaHas(Warnings(WithBinding(Pick("Secret > 1"))), PieceWarning);

    [Fact]
    public void Filter_warning_keyed_holder_argument() =>
        FormulaHas(Warnings(Private(WithBinding(Choice("keyed(TollBid, row(), SouthDepot) == 0")), "p-tollbid")), ChoicesWarning);

    [Fact]
    public void Filter_warning_aggregate_over_private_property() =>
        FormulaHas(Warnings(WithBinding(Pick("sum(Secret, \"\") > 1"))), PieceWarning);

    [Fact]
    public void No_filter_warning_single_side()
    {
        var p = WithBinding(Choice("keyed(Mines, 1) == 0"));
        p = p with { Sides = p.Sides.Take(1).ToList() };
        Assert.DoesNotContain(Warnings(p), m => m.Contains("may not see"));
    }

    [Fact]
    public void No_filter_warning_public_reads()
    {
        Assert.DoesNotContain(Warnings(WithBinding(Choice("keyed(TollBid, row()) == 0"))), m => m.Contains("may not see"));
        Assert.DoesNotContain(Warnings(WithBinding(Pick("Strength > 1"))), m => m.Contains("may not see"));
        Assert.DoesNotContain(Warnings(WithBinding(Pick("holder(OpenDepot, Reserve) > 0"))), m => m.Contains("may not see"));
        Assert.DoesNotContain(Warnings(WithBinding(Pick("sum(Strength, \"\") > 1"))), m => m.Contains("may not see"));
    }

    // ---- schedule zone map ----

    private static RulesPack WithRow(string? zone)
    {
        var p = Base();
        return p with
        {
            Schedule = new()
            {
                new ScheduleRow { Id = "r1", Turn = 1, PhaseId = "ph-toll", Piece = "Strength > 0", ZoneMapId = zone },
            },
        };
    }

    private const string NotFound = "Schedule row 1: zone map \"{0}\" was not found in the module";

    [Fact]
    public void Schedule_zone_map_by_name_valid_with_module() =>
        Assert.DoesNotContain(Errors(WithRow("Ferry"), Fx.Value), m => m.Contains("zone map"));

    [Fact]
    public void Schedule_zone_map_with_spaces_valid() =>
        Assert.DoesNotContain(Errors(WithRow("  Reserve Yard "), Fx.Value), m => m.Contains("zone map"));

    [Fact]
    public void Schedule_zone_map_unknown_with_module_error() =>
        Assert.Contains(string.Format(NotFound, "Nowhere"), Errors(WithRow("Nowhere"), Fx.Value));

    [Fact]
    public void Schedule_zone_map_known_ids_without_module_trimmed()
    {
        var known = new[] { "reserve" };
        Assert.DoesNotContain(RulesValidator.Validate(WithRow(" reserve "), known).Select(i => i.Message), m => m.Contains("zone map"));
        Assert.Contains(string.Format(NotFound, "other"), RulesValidator.Validate(WithRow("other"), known).Select(i => i.Message));
    }

    // ---- whole packs ----

    [Fact]
    public void Valid_b7_pack_has_no_issues()
    {
        var p = Base();
        var bid = Bid(
            new FormulaOutput { Target = OutputTarget.Global, Property = "p-tollbid", Op = OutputOp.Set, Expr = "amount", Key = "askside()" },
            R(margin: "askside() == \"x\""));
        p = With(p, bid with { Outputs = new() { bid.Outputs[0], R(margin: "2") } });
        p = p with { Phases = p.Phases.Select(ph => ph.Id == "ph-toll" ? ph with { OnStart = new() { S() } } : ph).ToList() };
        Assert.Empty(Run(p));
    }

    [Fact]
    public void Old_fixture_issue_list_unchanged()
    {
        // A pre-B7 style pack with known problems: the issue list is exactly what the pre-B7 validator gave.
        var p = Base() with
        {
            Formulas = new()
            {
                Bid(new FormulaOutput { Target = OutputTarget.Unit, Property = "p-gone", Op = OutputOp.Set, Expr = "1" }),
            },
            Phases = new() { new PhaseDef { Id = "ph-toll", Name = "Toll", OnStart = new() { new StartEntry { FormulaId = "f-nope", AskSideId = "north" } } } },
        };
        var messages = Run(p).Select(i => $"{i.Severity}: {i.Message}").ToList();
        Assert.Equal(new[]
        {
            "Error: Formula \"Bid\": output 1 refers to a property that no longer exists",
            "Error: Phase \"Toll\": formula \"f-nope\" does not exist",
        }, messages.OrderBy(m => m, StringComparer.Ordinal).ToArray());
    }
}
