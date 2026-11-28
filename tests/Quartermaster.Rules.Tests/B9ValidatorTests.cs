namespace Quartermaster.Rules.Tests;

/// <summary>B9: the validator's owner expression, holder choices, holder variable and draw checks (invented Riverland data).</summary>
public class B9ValidatorTests
{
    private static RulesPack Base() => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "p-strength", Name = "Strength", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-boats", Name = "Boats", Scope = PropertyScope.Pieces, Visibility = Visibility.Private },
            new PropertyDef { Id = "p-stores", Name = "Stores", Scope = PropertyScope.Holders, Holders = new() { "h-ford", "h-mill", "h-north" }, Visibility = Visibility.Private },
            new PropertyDef { Id = "p-fordpub", Name = "FordPub", Scope = PropertyScope.Holders, Holders = new() { "h-ford" }, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-pool", Name = "Pool", Scope = PropertyScope.Holders, Holders = new() { "global" }, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-hidden", Name = "Hidden", Scope = PropertyScope.Holders, Holders = new() { "global" }, Visibility = Visibility.Private },
            new PropertyDef { Id = "p-dumpowner", Name = "DumpOwner", Scope = PropertyScope.Holders, KeyedBy = "table:t-dumps", Holders = new() { "global" }, Visibility = Visibility.Public },
        },
        Holders = new()
        {
            new HolderDef { Id = "h-ford", Name = "Ford Dump", SideId = "north" },
            new HolderDef { Id = "h-mill", Name = "Mill Dump" },
            new HolderDef { Id = "h-north", Name = "North Depot", SideId = "north" },
            new HolderDef { Id = "h-open", Name = "Open Depot" },
        },
        Tables = new()
        {
            new TableDef { Id = "t-dumps", Name = "Dumps", Kind = TableKind.OneD, RowKeys = new() { "Ford Dump", "Mill Dump" }, Cells = new() { new() { "1" }, new() { "2" } } },
        },
        Sides = new() { new SideDef { Id = "north", Name = "Northern League", UnitFilter = "Strength > 0" }, new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "Strength > 0" } },
        Phases = new() { new PhaseDef { Id = "ph-toll", Name = "Toll" } },
        Formulas = new() { Bid() },
    };

    private static FormulaDef Bid(params FormulaOutput[] outputs) => new()
    {
        Id = "f-bid", Name = "Bid", Text = "amount",
        Bindings = new() { new VariableBinding { Variable = "amount", Kind = BindingKind.Input, Min = 0, Max = 9 } },
        Outputs = outputs.ToList(),
    };

    private static RulesPack With(RulesPack p, FormulaDef f) => p with { Formulas = p.Formulas.Select(x => x.Id == f.Id ? f : x).ToList() };

    private static RulesPack WithHolder(string? ownerExpr, RulesPack? p = null)
    {
        p ??= Base();
        return p with { Holders = p.Holders.Select(h => h.Id == "h-ford" ? h with { OwnerExpr = ownerExpr } : h).ToList() };
    }

    /// <summary>The Bid formula with an extra binding and outputs; the text reads every binding so none is "unused".</summary>
    private static RulesPack WithBinding(VariableBinding b, params FormulaOutput[] outputs)
    {
        var f = Bid(outputs);
        return With(Base(), f with { Bindings = f.Bindings.Append(b).ToList(), Text = "amount + " + b.Variable });
    }

    private static VariableBinding Picker(params string[] ids) =>
        new() { Variable = "Dump", Kind = BindingKind.Holder, Property = "p-stores", HolderChoices = ids.ToList() };

    private static VariableBinding Draw(string? count = "amount", string? property = "p-boats", string? filter = null) =>
        new() { Variable = "Hits", Kind = BindingKind.Draw, Property = property, PieceFilter = filter, DrawCount = count };

    private static FormulaOutput DumpOut(string property = "p-stores") =>
        new() { Target = OutputTarget.Holder, HolderVariable = "Dump", Property = property, Op = OutputOp.Subtract, Expr = "amount" };

    private static FormulaOutput HitOut(OutputAction a = OutputAction.Write) =>
        new() { Target = OutputTarget.Piece, PieceVariable = "Hits", Property = "p-boats", Op = OutputOp.Subtract, Action = a, Expr = a == OutputAction.Write ? "Hits" : null, Slot = a == OutputAction.Place ? "slot" : null };

    private static IReadOnlyList<RulesIssue> Run(RulesPack p) => RulesValidator.Validate(p, null, null, null);
    private static List<string> Errors(RulesPack p) => Run(p).Where(i => i.Severity == RulesIssueSeverity.Error).Select(i => i.Message).ToList();
    private static List<string> Warnings(RulesPack p) => Run(p).Where(i => i.Severity == RulesIssueSeverity.Warning).Select(i => i.Message).ToList();
    private static void Has(List<string> list, string msg) => Assert.True(list.Contains(msg), $"missing: {msg}; got: {string.Join(" | ", list)}");
    private static void FormulaHas(List<string> list, string tail) => Has(list, $"Formula \"Bid\": {tail}");

    // ---- owner expression ----

    [Fact]
    public void Owner_expr_parse_problem() =>
        Assert.Contains(Errors(WithHolder("1 +")), m => m.StartsWith("Holder \"Ford Dump\": owner expression "));

    [Fact]
    public void Owner_expr_reading_private_property_error() =>
        Has(Errors(WithHolder("Hidden")), "Holder \"Ford Dump\": owner expression reads \"Hidden\", which is not Public; every player must be able to tell who owns the holder");

    [Fact]
    public void Owner_expr_reading_unknown_property_error() =>
        Has(Errors(WithHolder("Nowhere")), "Holder \"Ford Dump\": owner expression reads unknown property \"Nowhere\"");

    [Fact]
    public void Owner_expr_reading_this_holders_public_property_ok() =>
        Assert.Empty(Run(WithHolder("FordPub + Pool")));

    [Fact]
    public void Owner_expr_keyed_global_public_ok() =>
        Assert.Empty(Run(WithHolder("keyed(DumpOwner, \"Ford Dump\")")));

    [Fact]
    public void Owner_expr_forbidden_functions_refused()
    {
        var errors = Errors(WithHolder("holder(Ford_Dump, FordPub) + side() + dice(1, 6)"));
        Has(errors, "Holder \"Ford Dump\": owner expression cannot use holder()");
        Has(errors, "Holder \"Ford Dump\": owner expression cannot use side()");
        Has(errors, "Holder \"Ford Dump\": owner expression cannot use dice()");
    }

    [Fact]
    public void Owner_expr_aggregate_refused() =>
        Has(Errors(WithHolder("sum(Strength, \"Strength > 0\") > 3")), "Holder \"Ford Dump\": owner expression cannot use sum()");

    [Fact]
    public void Owner_expr_constant_naming_no_side_warns()
    {
        Has(Warnings(WithHolder("\"Hill Clans\"")), "Holder \"Ford Dump\": owner expression gives \"Hill Clans\", which names no side");
        Has(Warnings(WithHolder("3")), "Holder \"Ford Dump\": owner expression gives \"3\", which names no side");
        Assert.Empty(Run(WithHolder("\"Southern Compact\"")));
        Assert.Empty(Run(WithHolder("2")));
        Assert.Empty(Run(WithHolder("0")));
    }

    /// <summary>Final review I2: FordPub is kept by Ford Dump only, so North Depot's expression would read its Default.</summary>
    [Fact]
    public void Owner_expr_reading_property_kept_by_neither_holder_error()
    {
        var p = Base();
        p = p with { Holders = p.Holders.Select(h => h.Id == "h-north" ? h with { OwnerExpr = "FordPub" } : h).ToList() };
        Has(Errors(p), "Holder \"North Depot\": owner expression reads \"FordPub\", which neither this holder nor the global holder keeps");
        Assert.Empty(Run(WithHolder("FordPub")));   // Ford Dump keeps it
    }

    [Fact]
    public void Owner_expr_without_sides_warns()
    {
        var p = WithHolder("1") with { Sides = new() };
        Has(Warnings(p), "Holder \"Ford Dump\": the pack has no sides, so the owner expression is not used");
    }

    // ---- holder choices ----

    [Fact]
    public void Holder_choices_on_a_non_holder_binding() =>
        FormulaHas(Errors(WithBinding(new VariableBinding { Variable = "Dump", Kind = BindingKind.Global, Property = "p-pool", HolderChoices = new() { "h-ford" } })),
            "variable \"Dump\" has holder choices but is not a Holder binding");

    [Fact]
    public void Holder_choices_unknown_holder() =>
        FormulaHas(Errors(WithBinding(Picker("h-ford", "h-gone"))), "variable \"Dump\" offers unknown holder \"h-gone\"");

    [Fact]
    public void Holder_choices_global_holder() =>
        FormulaHas(Errors(WithBinding(Picker("h-ford", "global") with { Property = "p-pool" })), "variable \"Dump\" offers the global holder; use a Global binding");

    [Fact]
    public void Holder_choices_holder_not_keeping_property() =>
        FormulaHas(Errors(WithBinding(Picker("h-ford", "h-open"))), "variable \"Dump\": holder \"Open Depot\" does not keep property \"Stores\"");

    [Fact]
    public void Holder_choices_with_fixed_holder_warns() =>
        FormulaHas(Warnings(WithBinding(Picker("h-ford", "h-mill") with { HolderId = "h-ford" })), "variable \"Dump\" has holder choices and a fixed holder; the fixed holder is not used");

    [Fact]
    public void Holder_variable_not_a_picker() =>
        FormulaHas(Errors(WithBinding(new VariableBinding { Variable = "Dump", Kind = BindingKind.Holder, Property = "p-stores", HolderId = "h-ford" }, DumpOut())),
            "output 1 names variable \"Dump\", which is not a Holder binding with holder choices");

    [Fact]
    public void Holder_variable_with_non_holder_target() =>
        FormulaHas(Errors(WithBinding(Picker("h-ford", "h-mill"), DumpOut() with { Target = OutputTarget.Global })),
            "output 1 names a Holder variable but its target is not Holder");

    [Fact]
    public void Holder_variable_output_property_not_kept_by_every_choice() =>
        FormulaHas(Errors(WithBinding(Picker("h-ford", "h-mill"), DumpOut("p-fordpub"))), "output 1: holder \"Mill Dump\" chosen by \"Dump\" does not keep property \"FordPub\"");

    [Fact]
    public void Holder_variable_with_fixed_holder_warns() =>
        FormulaHas(Warnings(WithBinding(Picker("h-ford", "h-mill"), DumpOut() with { HolderId = "h-ford" })), "output 1 names both a holder and a Holder variable; the holder is not used");

    [Fact]
    public void Holder_picker_and_output_valid_has_no_issues() =>
        Assert.Empty(Run(WithBinding(Picker("h-ford", "h-mill", "h-north"), DumpOut())));

    // ---- draws ----

    [Fact]
    public void Draw_without_count() =>
        FormulaHas(Errors(WithBinding(Draw(count: null), HitOut())), "variable \"Hits\" draws at random but has no draw count");

    [Fact]
    public void Draw_count_parse_problem() =>
        Assert.Contains(Errors(WithBinding(Draw(count: "1 +"), HitOut())), m => m.StartsWith("Formula \"Bid\": variable \"Hits\" draw count "));

    [Fact]
    public void Draw_count_unknown_property_tail() =>
        FormulaHas(Errors(WithBinding(Draw(count: "amount + Nowhere"), HitOut())), "variable \"Hits\" draw count refers to unknown property \"Nowhere\"");

    [Fact]
    public void Draw_count_reads_itself() =>
        FormulaHas(Errors(WithBinding(Draw(count: "Hits + 1"), HitOut())), "variable \"Hits\" draw count may not read \"Hits\"");

    [Fact]
    public void Draw_count_reads_earlier_binding_ok()
    {
        var p = WithBinding(Draw(count: "amount + 1"), HitOut());
        Assert.DoesNotContain(Errors(p), m => m.Contains("draw count"));
        Assert.Empty(Run(p));
    }

    [Fact]
    public void Draw_count_on_a_non_draw_binding() =>
        FormulaHas(Errors(WithBinding(new VariableBinding { Variable = "Foe", Kind = BindingKind.Piece, Property = "p-strength", DrawCount = "1" })),
            "variable \"Foe\" has a draw count but is not a Draw binding");

    [Fact]
    public void Draw_place_and_reveal_refused()
    {
        FormulaHas(Errors(WithBinding(Draw(), HitOut(OutputAction.Place))), "output 1: Place cannot act on the counters of draw \"Hits\"");
        FormulaHas(Errors(WithBinding(Draw(), HitOut(OutputAction.Reveal))), "output 1: Reveal cannot act on the counters of draw \"Hits\"");
    }

    [Fact]
    public void Draw_without_outputs_warns() =>
        FormulaHas(Warnings(WithBinding(Draw())), "variable \"Hits\" draws at random but no output acts on the drawn counters");

    [Fact]
    public void Draw_variable_accepted_as_piece_variable()
    {
        var errors = Errors(WithBinding(Draw(), HitOut()));
        Assert.DoesNotContain(errors, m => m.Contains("which is not a Piece binding"));
        Assert.Empty(errors);
    }

    [Fact]
    public void Draw_weight_is_optional_and_checked_as_a_piece_binding()
    {
        Assert.Empty(Errors(WithBinding(Draw(property: null), HitOut() with { Property = "p-boats" })));
        FormulaHas(Errors(WithBinding(Draw(property: "p-gone"), HitOut())), "variable \"Hits\" reads unknown property \"p-gone\"");
        FormulaHas(Errors(WithBinding(Draw(property: "p-pool"), HitOut())), "variable \"Hits\" reads holder property \"Pool\"");
        FormulaHas(Errors(WithBinding(Draw(filter: "Strength >"), HitOut())), "variable \"Hits\" \"which pieces\" filter does not parse: " + Errors(WithBinding(Draw(filter: "Strength >"), HitOut())).First(m => m.Contains("\"which pieces\" filter")).Split("does not parse: ")[1]);
    }

    private static RulesPack Sealed(FormulaDef f)
    {
        var p = With(Base(), f);
        return p with { Phases = new() { new PhaseDef { Id = "ph-toll", Name = "Toll", OnStart = new() { new StartEntry { FormulaId = f.Id, Sealed = true } } } } };
    }

    [Fact]
    public void Sealed_formula_draws_error()
    {
        var b = Draw();
        var f = Bid(HitOut()) with { Bindings = new() { new VariableBinding { Variable = "amount", Kind = BindingKind.Input, Min = 0, Max = 9 }, b }, Text = "amount + Hits" };
        Has(Errors(Sealed(f)), "Phase \"Toll\": start entry 1 is sealed, but formula \"Bid\" draws at random");
    }

    // ---- B7 filter warning counts an owner-expression holder as sided ----

    [Fact]
    public void Filter_warning_counts_owner_expr_holder_as_sided()
    {
        const string warning = "variable \"other\": its piece filter reads values another side may not see; each player picks from what they can see";
        var pick = new VariableBinding { Variable = "other", Kind = BindingKind.Piece, Property = "p-strength", PieceFilter = "holder(Quay_Dump, Stores) > 0" };
        // Quay_Dump has neither a side nor an owner expression: no warning.
        var p = WithBinding(pick);
        p = p with { Holders = p.Holders.Append(new HolderDef { Id = "h-quay", Name = "Quay_Dump" }).ToList() };
        Assert.DoesNotContain(Warnings(p), m => m == $"Formula \"Bid\": {warning}");
        p = p with { Holders = p.Holders.Select(h => h.Id == "h-quay" ? h with { OwnerExpr = "keyed(DumpOwner, \"Mill Dump\")" } : h).ToList() };
        FormulaHas(Warnings(p), warning);
    }

    // ---- B9 #55: a draw's filter warning (not "picks"), the masking rule and the caption ----

    [Fact]
    public void Draw_filter_warning_says_draw_not_pick()
    {
        const string warning = "variable \"Hits\": its draw filter reads values another side may not see; the draw is made from every matching counter, and players who cannot see those values see it masked";
        FormulaHas(Warnings(WithBinding(Draw(filter: "Boats > 0"), HitOut())), warning);
        var plain = Warnings(WithBinding(Draw(filter: "Strength > 0"), HitOut()));
        Assert.DoesNotContain(plain, m => m.Contains("its draw filter", StringComparison.Ordinal));
        Assert.DoesNotContain(Warnings(WithBinding(Draw(filter: "Boats > 0"), HitOut())), m => m.Contains("each player picks", StringComparison.Ordinal));
    }

    [Fact]
    public void Draw_bindings_hidden_by_weight_filter_or_masking()
    {
        FormulaDef F(VariableBinding b) => Bid() with { Bindings = new() { b } };
        var p = Base();
        Assert.Equal(new[] { "Hits" }, FormulaVariables.DrawBindingsHidden(p, F(Draw()), false));                          // Private weight
        Assert.Empty(FormulaVariables.DrawBindingsHidden(p, F(Draw(property: "p-strength")), false));                      // Public weight
        Assert.Empty(FormulaVariables.DrawBindingsHidden(p, F(Draw(property: null)), false));                              // one each
        Assert.Equal(new[] { "Hits" }, FormulaVariables.DrawBindingsHidden(p, F(Draw(property: "p-gone")), false));         // unknown: closed
        Assert.Equal(new[] { "Hits" }, FormulaVariables.DrawBindingsHidden(p, F(Draw(property: null, filter: "Boats > 0")), false));
        Assert.Equal(new[] { "Hits" }, FormulaVariables.DrawBindingsHidden(p, F(Draw(property: null)), true));             // pieces may hide
        Assert.Empty(FormulaVariables.DrawBindingsHidden(p, F(Picker("h-ford")), true));                                   // not a draw
        Assert.Empty(FormulaVariables.PieceBindingsHidden(p, F(Draw()), true));                                            // Piece-only, as before
    }

    private static string Caption(VariableBinding b)
    {
        var f = Bid() with { Text = b.Variable, Bindings = new() { b } };
        var layout = FormulaLayout.Build(f, Base());
        return layout.Main.Concat(layout.SubRows.SelectMany(r => r.Items)).OfType<BoxItem>().Single(x => x.Variable == b.Variable).Caption;
    }

    [Fact]
    public void Draw_caption_names_weight()
    {
        Assert.Equal("Boats (drawn)", Caption(Draw()));
        Assert.Equal("Hits (drawn)", Caption(Draw(property: null)));
    }

    /// <summary>Final review M2: a holder picker has no fixed holder, so its default caption does not read "Stores (?)".</summary>
    [Fact]
    public void Holder_picker_caption_reads_chosen_holder() =>
        Assert.Equal("Stores (chosen holder)", Caption(Picker("h-ford", "h-mill")));

    // ---- whole pack ----

    [Fact]
    public void Valid_b9_pack_has_no_issues()
    {
        var p = WithHolder("keyed(DumpOwner, \"Ford Dump\")");
        var drawStores = new FormulaDef
        {
            Id = "f-stores", Name = "Draw stores", Text = "Need + Dump",
            Bindings = new()
            {
                new VariableBinding { Variable = "Need", Kind = BindingKind.Input, Min = 0, Max = 9 },
                Picker("h-ford", "h-mill", "h-north"),
            },
            Outputs = new() { DumpOut() with { Expr = "Need" } },
        };
        var battery = new FormulaDef
        {
            Id = "f-battery", Name = "Shore battery", Text = "Hits",
            Bindings = new()
            {
                new VariableBinding { Variable = "Shots", Kind = BindingKind.Input, Min = 0, Max = 9 },
                new VariableBinding { Variable = "Hits", Kind = BindingKind.Draw, Property = "p-boats", SameHex = true, PieceFilter = "side() == \"Southern Compact\"", DrawCount = "Shots" },
            },
            Outputs = new() { HitOut() },
        };
        var all = p with { Formulas = p.Formulas.Concat(new[] { drawStores, battery }).ToList() };
        Assert.Empty(Run(all));
    }

    [Fact]
    public void Pack_without_b9_members_gets_no_b9_issues()
    {
        // A pack using none of the B9 members: only its own problems (one unknown holder on a fixed Holder output, one unused binding).
        var f = new FormulaDef
        {
            Id = "f-old", Name = "Old", Text = "A",
            Bindings = new()
            {
                new VariableBinding { Variable = "A", Kind = BindingKind.Holder, Property = "p-stores", HolderId = "h-ford" },
                new VariableBinding { Variable = "B", Kind = BindingKind.Unit, Property = "p-strength" },
            },
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = "h-gone", Property = "p-stores", Op = OutputOp.Set, Expr = "A" } },
        };
        var issues = Run(With(Base(), f) with { Formulas = new() { f } }).Select(i => $"{i.Severity}: {i.Message}").ToList();
        Assert.Equal(new[]
        {
            "Warning: Formula \"Old\": variable \"B\" is bound but not used in the formula",
            "Error: Formula \"Old\": output refers to unknown holder \"h-gone\"",
        }, issues);
    }

    // ---- task 6: outputs on the drawn counters ----

    [Fact]
    public void Draw_output_rolling_dice_refused()
    {
        const string tail = "output 1: an output on the drawn counters cannot roll dice()";
        FormulaHas(Errors(WithBinding(Draw(), HitOut() with { Expr = "Hits + dice(1, 6)" })), tail);
        FormulaHas(Errors(WithBinding(Draw(), HitOut() with { Expr = "Hits", Key = "dice(1, 2)" })), tail);
        FormulaHas(Errors(WithBinding(Draw(), HitOut() with { Margin = "dice(1, 2)" })), tail);   // final review: margin and pick-by too
        FormulaHas(Errors(WithBinding(Draw(), HitOut() with { PropertyChoice = new() { "p-boats", "p-strength" }, PropertySelect = "dice(1, 2)" })), tail);
        Assert.DoesNotContain(Errors(WithBinding(Draw(), HitOut())), m => m.Contains("cannot roll dice()", StringComparison.Ordinal));
        // dice() in another output (not on the drawn counters) is fine
        var global = new FormulaOutput { Target = OutputTarget.Global, Property = "p-pool", Op = OutputOp.Add, Expr = "dice(1, 6)" };
        Assert.DoesNotContain(Errors(WithBinding(Draw(), HitOut(), global)), m => m.Contains("cannot roll dice()", StringComparison.Ordinal));
    }

    [Fact]
    public void Draw_trait_output_on_pool_that_may_lack_it_warns()
    {
        const string layer = "Formula \"Bid\": output 1: some counters the draw can pick may have no layer \"Damage\"; a run that draws one is refused";
        const string mask = "Formula \"Bid\": output 1: some counters the draw can pick may have no mask; a run that draws one is refused";
        FormulaOutput SetLayer(string name = "Damage") => new() { Target = OutputTarget.Piece, PieceVariable = "Hits", Action = OutputAction.SetLayer, Layer = name, Expr = "1" };
        FormulaOutput SetMask() => new() { Target = OutputTarget.Piece, PieceVariable = "Hits", Action = OutputAction.SetMask, Expr = "1" };
        Has(Warnings(WithBinding(Draw(), SetLayer())), layer);                                                 // every counter
        Has(Warnings(WithBinding(Draw(filter: "Strength > 0"), SetLayer())), layer);                           // filter does not test the layer
        Has(Warnings(WithBinding(Draw(filter: "Strength > 0 or layer(\"Damage\") >= 0"), SetLayer())), layer);   // an "or" may skip it
        Has(Warnings(WithBinding(Draw(filter: "layer(\"Other\") >= 0"), SetLayer())), layer);                 // another layer
        Has(Warnings(WithBinding(Draw(filter: "not (layer(\"Damage\") >= 0 and Strength > 0)"), SetLayer())), layer);
        // final review M3: a layer read inside an aggregate's filter tests other counters, not the drawn one
        Has(Warnings(WithBinding(Draw(filter: "hexcount(\"layer('Damage') >= 0\") > 0"), SetLayer())), layer);
        Has(Warnings(WithBinding(Draw(filter: "Strength > 0 and hexsum(Strength, \"layer('Damage') >= 0\") > 0"), SetLayer())), layer);
        // a filter that always reads the layer leaves out every counter without it (a missing value does not match)
        Assert.DoesNotContain(Warnings(WithBinding(Draw(filter: "layer(\"damage\") >= 0"), SetLayer())), m => m.Contains("may have no", StringComparison.Ordinal));
        Assert.DoesNotContain(Warnings(WithBinding(Draw(filter: "Strength > 0 and layer(\"Damage\") < 3"), SetLayer())), m => m.Contains("may have no", StringComparison.Ordinal));
        // no filter can test for a mask, so a SetMask on drawn counters always warns
        Has(Warnings(WithBinding(Draw(filter: "Strength > 0"), SetMask())), mask);
        // writes and Remove need no trait; a Piece binding (one counter the player picks) is not a draw
        Assert.DoesNotContain(Warnings(WithBinding(Draw(), HitOut())), m => m.Contains("may have no", StringComparison.Ordinal));
        Assert.DoesNotContain(Warnings(WithBinding(Draw() with { Kind = BindingKind.Piece, DrawCount = null }, SetLayer())), m => m.Contains("may have no", StringComparison.Ordinal));
    }
}
