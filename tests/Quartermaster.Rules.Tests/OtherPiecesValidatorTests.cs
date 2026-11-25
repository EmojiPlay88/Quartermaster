namespace Quartermaster.Rules.Tests;

public class OtherPiecesValidatorTests
{
    private static VariableBinding Piece(string v, bool sameHex = false) =>
        new() { Variable = v, Kind = BindingKind.Piece, Property = "fuel", SameHex = sameHex };

    private static RulesPack PackWith(string text, VariableBinding[] bindings, params FormulaOutput[] outputs) => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces },
            new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders },
        },
        Holders = new() { new HolderDef { Id = "h1", Name = "Depot" } },
        Formulas = new() { new FormulaDef { Id = "f1", Name = "Shelling", Text = text, Bindings = bindings.ToList(), Outputs = outputs.ToList() } },
    };

    private static RulesPack Simple(params FormulaOutput[] outputs) =>
        PackWith("R", new[] { Piece("R"), new VariableBinding { Variable = "X", Kind = BindingKind.Input } }, outputs);

    private static FormulaOutput W(OutputTarget t, string? variable = null, OutputOp op = OutputOp.Add) =>
        new() { Target = t, Property = "fuel", Op = op, PieceVariable = variable, Expr = "R" };

    private static List<string> Errors(RulesPack p, IReadOnlyCollection<string>? maps = null, IReadOnlyCollection<string>? slots = null) =>
        RulesValidator.Validate(p, maps, slots).Where(i => i.Severity == RulesIssueSeverity.Error).Select(i => i.Message).ToList();

    private static void Has(List<string> errors, string tail) => Assert.Contains($"Formula \"Shelling\": {tail}", errors);

    [Fact]
    public void Piece_target_without_variable() =>
        Has(Errors(Simple(W(OutputTarget.Piece))), "output 1 acts on another piece but names no Piece variable");

    [Fact]
    public void Variable_not_a_piece_binding()
    {
        Has(Errors(Simple(W(OutputTarget.Piece, "X"))), "output 1 names variable \"X\", which is not a Piece binding");
        Has(Errors(Simple(W(OutputTarget.Piece, "Nope"))), "output 1 names variable \"Nope\", which is not a Piece binding");
    }

    [Fact]
    public void Piece_variable_without_piece_target() =>
        Has(Errors(Simple(W(OutputTarget.Unit, "R"))), "output 1 names a Piece variable but its target is not Piece");

    [Fact]
    public void Action_cannot_target()
    {
        Has(Errors(Simple(new FormulaOutput { Target = OutputTarget.Global, Action = OutputAction.Remove })), "output 1: Remove cannot target Global");
        Has(Errors(Simple(new FormulaOutput { Target = OutputTarget.Placed, Action = OutputAction.Place, Slot = "s" })), "output 1: Place cannot target Placed");
        Has(Errors(Simple(new FormulaOutput { Target = OutputTarget.Holder, HolderId = "h1", Action = OutputAction.SetMask })), "output 1: SetMask cannot target Holder");
    }

    [Fact]
    public void Place_without_slot() =>
        Has(Errors(Simple(new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Place })), "output 1 places a piece but names no palette entry");

    [Fact]
    public void Place_unknown_slot_with_known_slots()
    {
        var p = Simple(new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Place, Slot = "wreck" });
        Has(Errors(p, null, new[] { "bridge" }), "output 1 places palette entry \"wreck\", which does not exist in the module");
        Assert.DoesNotContain(Errors(p, null, new[] { "wreck" }), e => e.Contains("palette entry"));
    }

    [Fact]
    public void Place_unknown_slot_ignored_without_known_slots() =>
        Assert.DoesNotContain(Errors(Simple(new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Place, Slot = "wreck" })), e => e.Contains("palette entry"));

    [Fact]
    public void Slot_without_place() =>
        Has(Errors(Simple(new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Slot = "wreck" })), "output 1 names a palette entry but does not place a piece");

    [Fact]
    public void Second_place()
    {
        var place = new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Place, Slot = "wreck" };
        var errors = Errors(Simple(place, place));
        Has(errors, "output 2 places a second piece; only one output may place a piece");
        Assert.DoesNotContain(errors, e => e.Contains("output 1 places a second"));
    }

    [Fact]
    public void Placed_without_place() =>
        Has(Errors(Simple(W(OutputTarget.Placed, op: OutputOp.Set))), "output 1 writes the placed piece, but no output places one");

    [Fact]
    public void Placed_write_must_set()
    {
        var place = new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Place, Slot = "wreck" };
        Has(Errors(Simple(place, W(OutputTarget.Placed, op: OutputOp.Add))), "output 2 writes the placed piece, so its operation must be Set");
        Assert.DoesNotContain(Errors(Simple(place, W(OutputTarget.Placed, op: OutputOp.Set))), e => e.Contains("operation must be Set"));
    }

    [Fact]
    public void SetLayer_without_layer() =>
        Has(Errors(Simple(new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.SetLayer })), "output 1 sets a layer but names none");

    [Fact]
    public void Layer_without_setlayer() =>
        Has(Errors(Simple(new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Layer = "Levels" })), "output 1 names a layer but does not set one");

    [Fact]
    public void Remove_unknown_map()
    {
        var p = Simple(new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Remove, ToMapId = "dead" });
        Has(Errors(p, new[] { "main" }), "output 1 moves the piece to map \"dead\", which does not exist in the module");
        Assert.DoesNotContain(Errors(p, new[] { "dead" }), e => e.Contains("moves the piece"));
        Assert.DoesNotContain(Errors(p), e => e.Contains("moves the piece"));
    }

    [Fact]
    public void Map_without_remove() =>
        Has(Errors(Simple(new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", ToMapId = "dead" })), "output 1 names a map but does not remove a piece");

    [Fact]
    public void Property_list_on_non_write() =>
        Has(Errors(Simple(new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Remove, PropertyChoice = new() { "fuel" } })),
            "output 1 has a property list but does not write a value");

    [Fact]
    public void Piece_write_scope_message()
    {
        var errors = Errors(Simple(new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "R", Property = "supply", Op = OutputOp.Add, Expr = "R" }));
        Has(errors, "output property \"Supply\" cannot be written to a piece (its scope is Holders)");
        var unit = Errors(Simple(new FormulaOutput { Target = OutputTarget.Unit, Property = "supply", Op = OutputOp.Add, Expr = "R" }));
        Has(unit, "output property \"Supply\" cannot be written to a unit (its scope is Holders)");
    }

    [Fact]
    public void SameHex_on_non_piece_warns()
    {
        var p = PackWith("X", new[] { new VariableBinding { Variable = "X", Kind = BindingKind.Input, SameHex = true } });
        Assert.Contains(RulesValidator.Validate(p), i => i.Severity == RulesIssueSeverity.Warning
            && i.Message == "Formula \"Shelling\": variable \"X\" has \"same hex\" but is not a Piece binding");
        var ok = PackWith("R", new[] { Piece("R", sameHex: true) });
        Assert.DoesNotContain(RulesValidator.Validate(ok), i => i.Message.Contains("same hex"));
    }

    [Fact]
    public void Layer_in_filter_needs_quotes()
    {
        var p = new RulesPack
        {
            Properties = new() { new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, AppliesTo = "layer(Levels) > 0" } },
        };
        Assert.Contains(RulesValidator.Validate(p), i => i.Severity == RulesIssueSeverity.Error
            && i.Message == "Property \"Fuel\": applies-to layer() needs a layer name in quotes");
        var good = p with { Properties = new() { p.Properties[0] with { AppliesTo = "layer(\"Levels\") > 0" } } };
        Assert.DoesNotContain(RulesValidator.Validate(good), i => i.Message.Contains("layer()"));
    }

    [Fact]
    public void Layer_in_phase_condition_forbidden()
    {
        var p = new RulesPack { Phases = new() { new PhaseDef { Id = "ph1", Name = "Supply", Condition = "layer(\"Levels\") > 0" } } };
        Assert.Contains(RulesValidator.Validate(p), i => i.Message == "Phase \"Supply\": condition may not use layer()");
    }

    [Fact]
    public void Remove_output_skips_property_checks()
    {
        var errors = Errors(Simple(new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "R", Action = OutputAction.Remove, Property = "gone" }));
        Assert.DoesNotContain(errors, e => e.Contains("no longer exists") || e.Contains("cannot be written"));
    }

    [Fact]
    public void Valid_b3_formula_has_no_issues()
    {
        var p = PackWith("R", new[] { Piece("R", sameHex: true) },
            new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Place, Slot = "wreck" },
            new FormulaOutput { Target = OutputTarget.Placed, Property = "fuel", Op = OutputOp.Set, Expr = "R" },
            new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "R", Property = "fuel", Op = OutputOp.Add, Expr = "R" },
            new FormulaOutput { Target = OutputTarget.Placed, Action = OutputAction.SetLayer, Layer = "Levels", Expr = "1" },
            new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "R", Action = OutputAction.SetMask, Expr = "0" },
            new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "R", Action = OutputAction.Remove, ToMapId = "dead" });
        var issues = RulesValidator.Validate(p, new[] { "dead" }, new[] { "wreck" });
        Assert.DoesNotContain(issues, i => i.EntityId == "f1");
    }

    [Fact]
    public void Two_arg_validate_unchanged()
    {
        var p = Simple(new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Place, Slot = "wreck" },
            new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Remove, ToMapId = "dead" });
        var two = RulesValidator.Validate(p, new[] { "main" }).Select(i => i.Message).ToList();
        var three = RulesValidator.Validate(p, new[] { "main" }, null).Select(i => i.Message).ToList();
        Assert.Equal(two, three);
        Assert.Equal(RulesValidator.Validate(p).Select(i => i.Message), RulesValidator.Validate(p, null, null).Select(i => i.Message));
        Assert.DoesNotContain(two, m => m.Contains("palette entry \"wreck\", which"));
    }

    [Fact]
    public void Target_only_piece_binding_is_used()
    {
        var f = new FormulaDef
        {
            Id = "f1", Name = "Shelling", Text = "X",
            Bindings = new() { new VariableBinding { Variable = "X", Kind = BindingKind.Input }, Piece("T") with { When = "W > 0" }, new VariableBinding { Variable = "W", Kind = BindingKind.Input } },
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "T", Action = OutputAction.Remove } },
        };
        Assert.Equal(new[] { "X", "T", "W" }, FormulaVariables.Used(f));
        var p = PackWith("X", f.Bindings.ToArray(), f.Outputs.ToArray());
        Assert.DoesNotContain(RulesValidator.Validate(p), i => i.Message.Contains("bound but not used"));
        // a name that is not a Piece binding is not added (its output error is enough)
        Assert.Equal(new[] { "X" }, FormulaVariables.Used(f with { Outputs = new() { new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "Nope", Action = OutputAction.Remove } } }));
    }
}
