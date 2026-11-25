using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class FormulaEditTests
{
    private static FormulaDef F(string text, params VariableBinding[] b) => new() { Id = "f", Name = "F", Text = text, Bindings = b.ToList() };

    [Fact]
    public void Analyze_reports_variables_or_error_position()
    {
        var ok = FormulaEdit.Analyze("A * B + A");
        Assert.Null(ok.Error);
        Assert.Equal(new[] { "A", "B" }, ok.Variables);
        var bad = FormulaEdit.Analyze("A * ");
        Assert.NotNull(bad.Error);
        Assert.Equal("Error at " + bad.Error!.Position + ": " + bad.Error.Detail, FormulaEdit.ErrorText(bad.Error));
    }

    [Fact]
    public void Sync_keeps_bindings_read_only_by_guards()
    {
        var f = F("A", new VariableBinding { Variable = "A", Kind = BindingKind.Input, When = "W > 0" },
                  new VariableBinding { Variable = "W", Kind = BindingKind.Input },
                  new VariableBinding { Variable = "P", Kind = BindingKind.Input, Caption = "p" }) with { Precondition = "P > 0" };
        Assert.Empty(FormulaEdit.Unused(f));
        var r = FormulaEdit.RemoveUnused(f);
        Assert.Equal(new[] { "A", "P", "W" }, r.Bindings.Select(b => b.Variable).OrderBy(x => x));
        // a guard-only variable with no binding is added
        var g = F("A", new VariableBinding { Variable = "A", Kind = BindingKind.Input }) with { Precondition = "Q > 0" };
        Assert.Contains(FormulaEdit.RemoveUnused(g).Bindings, b => b.Variable == "Q");
    }

    [Fact]
    public void SetKind_clears_dice_count_expr_when_leaving_roll()
    {
        var f = F("R", new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6, DiceCountExpr = "P" });
        Assert.Null(FormulaEdit.SetKind(f, "R", BindingKind.Input).Bindings[0].DiceCountExpr);
        Assert.Equal("P", FormulaEdit.SetKind(f, "R", BindingKind.Roll).Bindings[0].DiceCountExpr);   // same kind: untouched
    }

    [Fact]
    public void IsConfigured_counts_dice_count_expr()
    {
        Assert.True(FormulaEdit.IsConfigured(new VariableBinding { Variable = "R", Kind = BindingKind.Input, DiceCountExpr = "P" }));
    }

    [Fact]
    public void IsConfigured_counts_when_so_sync_keeps_it()
    {
        var b = new VariableBinding { Variable = "Gone", Kind = BindingKind.Input, When = "1 = 1" };
        Assert.True(FormulaEdit.IsConfigured(b));
        var r = FormulaEdit.Sync(F("A"), new[] { "A" }, keepConfigured: true);
        Assert.Empty(r.Removed);
        var r2 = FormulaEdit.Sync(F("A", b), new[] { "A" }, keepConfigured: true);
        Assert.Contains(r2.Formula.Bindings, x => x.Variable == "Gone");
    }

    [Fact]
    public void Sync_adds_new_variables_as_input_and_keeps_existing()
    {
        var f = F("A + B", new VariableBinding { Variable = "A", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 });
        var r = FormulaEdit.Sync(f, new[] { "A", "B" });
        Assert.Equal(new[] { "A", "B" }, r.Formula.Bindings.Select(b => b.Variable));
        Assert.Equal(BindingKind.Roll, r.Formula.Bindings[0].Kind);
        Assert.Equal(BindingKind.Input, r.Formula.Bindings[1].Kind);
        Assert.Empty(r.Removed);
    }

    [Fact]
    public void Sync_drops_removed_and_lists_them()
    {
        var f = F("B", new VariableBinding { Variable = "A", Kind = BindingKind.Input }, new VariableBinding { Variable = "B", Kind = BindingKind.Global, Property = "p" });
        var r = FormulaEdit.Sync(f, new[] { "B" });
        Assert.Single(r.Formula.Bindings);
        Assert.Equal("A", Assert.Single(r.Removed).Variable);
    }

    [Fact]
    public void Sync_can_keep_configured_stale_bindings()
    {
        var f = F("C", new VariableBinding { Variable = "A", Kind = BindingKind.Table, TableId = "t" }, new VariableBinding { Variable = "B", Kind = BindingKind.Input });
        var r = FormulaEdit.Sync(f, new[] { "C" }, keepConfigured: true);
        Assert.Equal(new[] { "C", "A" }, r.Formula.Bindings.Select(b => b.Variable));
        Assert.Equal("B", Assert.Single(r.Removed).Variable);
        Assert.Equal(new[] { "A" }, FormulaEdit.Stale(f, new[] { "C" }).Where(FormulaEdit.IsConfigured).Select(b => b.Variable));
    }

    [Fact]
    public void IsConfigured_only_for_non_default_input()
    {
        Assert.False(FormulaEdit.IsConfigured(new VariableBinding { Variable = "A", Kind = BindingKind.Input }));
        Assert.True(FormulaEdit.IsConfigured(new VariableBinding { Variable = "A", Kind = BindingKind.Input, Min = 0 }));
        Assert.True(FormulaEdit.IsConfigured(new VariableBinding { Variable = "A", Kind = BindingKind.Input, Caption = "x" }));
        Assert.True(FormulaEdit.IsConfigured(new VariableBinding { Variable = "A", Kind = BindingKind.Global }));
    }

    [Theory]
    [InlineData("low, mid | always ask | auto: \"mid\"")]
    [InlineData("always ask")]
    [InlineData("0..9 | always ask")]
    public void Always_ask_details_round_trip(string text)
    {
        var (b, err) = FormulaEdit.ParseDetails(new VariableBinding { Variable = "A", Kind = BindingKind.Input }, text);
        Assert.Null(err);
        Assert.True(b.AlwaysAsk);
        Assert.Equal(text, FormulaEdit.FormatDetails(b));
    }

    [Theory]
    [InlineData("0..9 | auto: 2 | always ask", 9.0, "2")]
    [InlineData("| auto: 2 | ALWAYS ASK ", null, "2")]
    [InlineData("low, mid | auto: \"a | b\" | always ask", null, "\"a | b\"")]
    [InlineData("low | auto: \"a\\\" | b\" | always ask", null, "\"a\\\" | b\"")]
    public void Always_ask_after_auto_value(string text, double? max, string auto)
    {
        var (b, err) = FormulaEdit.ParseDetails(new VariableBinding { Variable = "A", Kind = BindingKind.Input }, text);
        Assert.Null(err);
        Assert.True(b.AlwaysAsk);
        Assert.Equal(max, b.Max);
        Assert.Equal(auto, b.AutoValue);
    }

    [Fact]
    public void Auto_value_keeps_bars_inside_its_text_and_rejects_other_parts()
    {
        var input = new VariableBinding { Variable = "A", Kind = BindingKind.Input };
        var (q, e1) = FormulaEdit.ParseDetails(input, "low, mid | auto: \"x | always ask\"");
        Assert.Null(e1);
        Assert.False(q.AlwaysAsk);
        Assert.Equal("\"x | always ask\"", q.AutoValue);
        var (same, e2) = FormulaEdit.ParseDetails(input, "0..9 | auto: 2 | sometimes");
        Assert.StartsWith("Auto value: ", e2);
        Assert.Same(input, same);
    }

    [Fact]
    public void Always_ask_details_edge_cases()
    {
        var input = new VariableBinding { Variable = "A", Kind = BindingKind.Input };
        var (alone, _) = FormulaEdit.ParseDetails(input, "ALWAYS ASK");
        Assert.True(alone.AlwaysAsk);
        Assert.Null(alone.Choices);
        Assert.Null(alone.Min);

        var (same, err) = FormulaEdit.ParseDetails(input, "0..9 | sometimes");
        Assert.Equal("Use a range like 0..20, or choices separated by commas; add | always ask and/or | auto: expression.", err);
        Assert.Same(input, same);

        var (reset, _) = FormulaEdit.ParseDetails(alone, "0..9");
        Assert.False(reset.AlwaysAsk);

        var f = F("A", alone with { Caption = "c" });
        Assert.False(FormulaEdit.SetKind(f, "A", BindingKind.Roll).Bindings[0].AlwaysAsk);
        Assert.True(FormulaEdit.IsConfigured(alone));

        // A choice equal to "always ask", or holding the words after a bar, stays a choice.
        var (q, _) = FormulaEdit.ParseDetails(input, "low, always ask");
        Assert.Equal(new[] { "low", "always ask" }, q.Choices);
        Assert.False(q.AlwaysAsk);
        Assert.Equal("low, \"always ask\"", FormulaEdit.FormatDetails(q));
        var (back, _) = FormulaEdit.ParseDetails(input, FormulaEdit.FormatDetails(q));
        Assert.Equal(new[] { "low", "always ask" }, back.Choices);
        Assert.False(back.AlwaysAsk);
        var (bar, e2) = FormulaEdit.ParseDetails(input, "low, \"a | always ask\"");
        Assert.Null(e2);
        Assert.Equal(new[] { "low", "a | always ask" }, bar.Choices);
        Assert.False(bar.AlwaysAsk);
        // Old texts parse as before.
        Assert.Equal(new[] { "low", "mid", "high" }, FormulaEdit.ParseDetails(input, "low, mid, high").Binding.Choices);
        var (old, _) = FormulaEdit.ParseDetails(input, "0..20 | auto: 2");
        Assert.Equal(20, old.Max); Assert.Equal("2", old.AutoValue);
        Assert.Equal(new[] { "say \"hi\"" }, FormulaEdit.ParseDetails(input, "say \"hi\"").Binding.Choices);
    }

    [Fact]
    public void SetKind_resets_kind_specific_fields_but_keeps_caption()
    {
        var f = F("A", new VariableBinding { Variable = "A", Kind = BindingKind.Table, TableId = "t", RowExpr = "X", ColExpr = "Y", Caption = "cap", Min = 1, When = "X > 0" });
        var g = FormulaEdit.SetKind(f, "A", BindingKind.Roll);
        var b = g.Bindings[0];
        Assert.Equal(BindingKind.Roll, b.Kind);
        Assert.Null(b.TableId); Assert.Null(b.RowExpr); Assert.Null(b.Min);
        Assert.Equal("cap", b.Caption);
        Assert.Equal("X > 0", b.When);   // the condition does not depend on the kind
        Assert.Equal(BindingKind.Table, f.Bindings[0].Kind);
    }

    [Fact]
    public void Details_round_trip_with_invariant_numbers()
    {
        Assert.Equal("0.5..20", FormulaEdit.FormatDetails(new VariableBinding { Variable = "A", Kind = BindingKind.Input, Min = 0.5, Max = 20 }));
        var (b, err) = FormulaEdit.ParseDetails(new VariableBinding { Variable = "A", Kind = BindingKind.Input }, "1.5..9");
        Assert.Null(err);
        Assert.Equal(1.5, b.Min); Assert.Equal(9, b.Max);
        var (c, _) = FormulaEdit.ParseDetails(b, "low, mid, high");
        Assert.Equal(new[] { "low", "mid", "high" }, c.Choices);
        Assert.Null(c.Min);
        Assert.Equal("low, mid, high", FormulaEdit.FormatDetails(c));
        var (r, rerr) = FormulaEdit.ParseDetails(new VariableBinding { Variable = "R", Kind = BindingKind.Roll }, "2d6");
        Assert.Null(rerr); Assert.Equal(2, r.DiceCount); Assert.Equal(6, r.DiceSides);
        Assert.Equal("2d6", FormulaEdit.FormatDetails(r));
        var (same, bad) = FormulaEdit.ParseDetails(r, "xd6");
        Assert.NotNull(bad);
        Assert.Equal(r, same);
        var (t, _) = FormulaEdit.ParseDetails(new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t" }, "row: Roll + 1 | col: Terrain");
        Assert.Equal("Roll + 1", t.RowExpr); Assert.Equal("Terrain", t.ColExpr);
        Assert.Equal("row: Roll + 1 | col: Terrain", FormulaEdit.FormatDetails(t));
        var (t2, _) = FormulaEdit.ParseDetails(t, "row: A");
        Assert.Null(t2.ColExpr);
    }

    private static RulesPack Pack() => new()
    {
        Properties = new() { new PropertyDef { Id = "p1", Name = "Fuel" }, new PropertyDef { Id = "p2", Name = "Cost", Scope = PropertyScope.Holders } },
        Holders = new() { new HolderDef { Id = "h1", Name = "Depot" } },
        Tables = new() { new TableDef { Id = "t1", Name = "Terrain" } },
    };

    [Fact]
    public void Target_options_store_ids_and_show_names()
    {
        var pack = Pack();
        var props = FormulaEdit.TargetOptions(pack, BindingKind.Unit);
        Assert.Equal(new[] { "Fuel", "Cost" }, props.Select(o => o.Display));
        var chosen = FormulaEdit.ApplyTarget(new VariableBinding { Variable = "A", Kind = BindingKind.Unit }, props[0]);
        Assert.Equal("p1", chosen.Property);
        Assert.Equal("Fuel", FormulaEdit.TargetDisplay(pack, chosen));
        var holder = FormulaEdit.TargetOptions(pack, BindingKind.Holder).Single(o => o.Display == "Depot: Cost");
        var hb = FormulaEdit.ApplyTarget(new VariableBinding { Variable = "A", Kind = BindingKind.Holder }, holder);
        Assert.Equal("h1", hb.HolderId); Assert.Equal("p2", hb.Property);
        Assert.Equal("Depot: Cost", FormulaEdit.TargetDisplay(pack, hb));
        var tb = FormulaEdit.ApplyTarget(new VariableBinding { Variable = "T", Kind = BindingKind.Table }, FormulaEdit.TargetOptions(pack, BindingKind.Table)[0]);
        Assert.Equal("t1", tb.TableId);
        Assert.Empty(FormulaEdit.TargetOptions(pack, BindingKind.Input));
        Assert.Equal("(missing)", FormulaEdit.TargetDisplay(pack, new VariableBinding { Variable = "A", Kind = BindingKind.Unit, Property = "gone" }));
    }

    [Fact]
    public void Outputs_add_set_remove_and_target_change_clears_holder()
    {
        var f = FormulaEdit.AddOutput(F("A"));
        Assert.Single(f.Outputs);
        f = FormulaEdit.SetOutput(f, 0, o => o with { Target = OutputTarget.Holder, HolderId = "h1", Property = "p1" });
        f = FormulaEdit.SetOutput(f, 0, o => FormulaEdit.WithOutputTarget(o, OutputTarget.Unit));
        Assert.Null(f.Outputs[0].HolderId);
        f = FormulaEdit.SetOutput(f, 5, o => o);
        f = FormulaEdit.RemoveOutput(f, 0);
        Assert.Empty(f.Outputs);
    }

    [Fact]
    public void Usage_finds_formulas_by_property_and_holder_id()
    {
        var pack = Pack() with
        {
            Formulas = new()
            {
                F("A", new VariableBinding { Variable = "A", Kind = BindingKind.Unit, Property = "p1" }) with { Id = "x", Name = "X" },
                new FormulaDef { Id = "y", Name = "Y", Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = "h1", Property = "p2" } } },
            },
        };
        Assert.Equal(new[] { "X" }, FormulaEdit.FormulasUsingProperty(pack, "p1").Select(f => f.Name));
        Assert.Equal(new[] { "Y" }, FormulaEdit.FormulasUsingProperty(pack, "p2").Select(f => f.Name));
        Assert.Equal(new[] { "Y" }, FormulaEdit.FormulasUsingHolder(pack, "h1").Select(f => f.Name));
    }

    [Fact]
    public void Formulas_using_property_finds_choice()
    {
        var pack = Pack() with
        {
            Formulas = new()
            {
                new FormulaDef { Id = "z", Name = "Z", Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, PropertyChoice = new() { "p1", "p2" }, PropertySelect = "1" } } },
            },
        };
        Assert.Equal(new[] { "Z" }, FormulaEdit.FormulasUsingProperty(pack, "p2").Select(f => f.Name));
        Assert.Empty(FormulaEdit.FormulasUsingProperty(pack, "p3"));
    }

    [Fact]
    public void Output_value_and_choice_edits_normalise_blanks()
    {
        var f = FormulaEdit.AddOutput(F("A"));
        f = FormulaEdit.SetOutputValue(f, 0, " R * 2 ");
        Assert.Equal("R * 2", f.Outputs[0].Expr);
        f = FormulaEdit.SetOutputValue(f, 0, "  ");
        Assert.Null(f.Outputs[0].Expr);
        f = FormulaEdit.SetOutputChoice(f, 0, new List<string> { "p1" });
        Assert.Equal(new[] { "p1" }, f.Outputs[0].PropertyChoice);
        f = FormulaEdit.SetOutputChoice(f, 0, new List<string>());
        Assert.Null(f.Outputs[0].PropertyChoice);
        f = FormulaEdit.SetOutputPick(f, 0, " Slot ");
        Assert.Equal("Slot", f.Outputs[0].PropertySelect);
        f = FormulaEdit.SetOutputPick(f, 0, "");
        Assert.Null(f.Outputs[0].PropertySelect);
    }

    [Fact]
    public void Choice_names_use_the_quoted_choice_format()
    {
        Assert.Equal("Fuel, \"Fuel, diesel\", Ammo", FormulaEdit.FormatNameList(new[] { "Fuel", "Fuel, diesel", "Ammo" }));
        var (names, err) = FormulaEdit.ParseNameList("Fuel, \"Fuel, diesel\", Ammo");
        Assert.Null(err);
        Assert.Equal(new[] { "Fuel", "Fuel, diesel", "Ammo" }, names);
        Assert.Equal(new[] { "Fuel" }, FormulaEdit.ParseNameList("Fuel").Names);
        Assert.Empty(FormulaEdit.ParseNameList("  ").Names!);
        Assert.NotNull(FormulaEdit.ParseNameList("\"Fuel, diesel").Error);
    }

    [Fact]
    public void SyntaxProblem_checks_filter_expressions()
    {
        Assert.Null(FormulaEdit.SyntaxProblem(""));
        Assert.Null(FormulaEdit.SyntaxProblem("has(Fuel) and 1 > 0"));
        Assert.NotNull(FormulaEdit.SyntaxProblem("1 +"));
    }

    [Fact]
    public void SheetFields_are_distinct_sorted_from_propertysheet_rows()
    {
        var a = PieceTypeParser.EncodeState(new[] { "propertysheet;0Name~0Fuel~0Ammo;Sheet;", "piece;;;unit.svg;Unit" });
        var b = PieceTypeParser.EncodeState(new[] { "propertysheet;0fuel~0Supply;Sheet;", "piece;;;unit.svg;Other" });
        var names = SheetFieldCatalog.Collect(new[] { a, b, "garbage" });
        Assert.Equal(new[] { "Ammo", "Fuel", "Name", "Supply" }, names);
    }

    [Fact]
    public void Piece_entries_list_name_and_map()
    {
        var gs = new GameState();
        var type = PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;Panzer" });
        gs.Pieces["p1"] = new PieceInstance { Id = "p1", TypeString = type, MapId = "Main" };
        var e = PieceChoices.Entries(gs, id => "Board");
        Assert.Equal("Panzer (Board)", Assert.Single(e).Display);
        Assert.Equal("p1", e[0].PieceId);
    }

    [Fact]
    public void Auto_value_variables_are_used()
    {
        var f = F("A", new VariableBinding { Variable = "A", Kind = BindingKind.Input, AutoValue = "B + 1" },
            new VariableBinding { Variable = "B", Kind = BindingKind.Unit, Property = "p2" });
        Assert.Empty(FormulaEdit.Unused(f));
        Assert.Contains(FormulaEdit.RemoveUnused(f).Bindings, b => b.Variable == "B");
    }

    [Fact]
    public void Auto_value_of_unused_binding_does_not_keep_its_variables()
    {
        var f = F("A", new VariableBinding { Variable = "A", Kind = BindingKind.Input },
            new VariableBinding { Variable = "X", Kind = BindingKind.Input, AutoValue = "B" },
            new VariableBinding { Variable = "B", Kind = BindingKind.Unit, Property = "p2" });
        Assert.Equal(new[] { "X", "B" }, FormulaEdit.Unused(f).Select(b => b.Variable));
    }

    [Fact]
    public void Auto_value_naming_its_own_variable_does_not_keep_it()
    {
        var f = F("A", new VariableBinding { Variable = "A", Kind = BindingKind.Input },
            new VariableBinding { Variable = "X", Kind = BindingKind.Input, AutoValue = "X + 1" });
        Assert.Equal(new[] { "X" }, FormulaEdit.Unused(f).Select(b => b.Variable));
        Assert.Equal(new[] { "A" }, FormulaEdit.RemoveUnused(f).Bindings.Select(b => b.Variable));
    }

    [Fact]
    public void RemoveUnused_drops_every_stale_binding_only()
    {
        var f = F("C", new VariableBinding { Variable = "A", Kind = BindingKind.Table, TableId = "t" }, new VariableBinding { Variable = "C", Kind = BindingKind.Input });
        var g = FormulaEdit.RemoveUnused(f);
        Assert.Equal(new[] { "C" }, g.Bindings.Select(b => b.Variable));
        Assert.Equal(new[] { "A" }, FormulaEdit.Unused(f).Select(b => b.Variable));
    }

    [Fact]
    public void Variables_used_only_in_a_table_row_or_column_expression_count_as_used()
    {
        var f = F("min(Ammo + Gain, 4)",
            new VariableBinding { Variable = "Ammo", Kind = BindingKind.Unit, Property = "ammo" },
            new VariableBinding { Variable = "Gain", Kind = BindingKind.Table, TableId = "t", RowExpr = "Hexes", ColExpr = "Terrain" },
            new VariableBinding { Variable = "Hexes", Kind = BindingKind.Input, Min = 0, Max = 20 },
            new VariableBinding { Variable = "Old", Kind = BindingKind.Input, Min = 0, Max = 1 });
        Assert.Equal(new[] { "Ammo", "Gain", "Hexes", "Terrain" }, FormulaEdit.UsedVariables(f));
        Assert.Equal(new[] { "Old" }, FormulaEdit.Unused(f).Select(b => b.Variable));
        var g = FormulaEdit.RemoveUnused(f);
        Assert.Equal(new[] { "Ammo", "Gain", "Hexes", "Terrain" }, g.Bindings.Select(b => b.Variable));
        Assert.Equal(20, g.Bindings[2].Max);                       // the configured row variable is kept as it was
        Assert.Equal(BindingKind.Input, g.Bindings[3].Kind);       // the new column variable gets a binding row
    }

    [Fact]
    public void UsedVariables_ignores_a_bad_row_expression()
    {
        var f = F("Gain", new VariableBinding { Variable = "Gain", Kind = BindingKind.Table, TableId = "t", RowExpr = "Hexes +" });
        Assert.Equal(new[] { "Gain" }, FormulaEdit.UsedVariables(f));
    }

    [Fact]
    public void NameRules_reject_empty_and_duplicate_names_case_insensitively()
    {
        var existing = new[] { "Fuel", "Ammo" };
        Assert.Null(NameRules.Problem(existing, "Supply", "x"));
        Assert.NotNull(NameRules.Problem(existing, "  ", "x"));
        Assert.NotNull(NameRules.Problem(existing, "fuel", "x"));
        Assert.Null(NameRules.Problem(existing, "FUEL", "Fuel")); // unchanged / own name
    }

    [Fact]
    public void AskGate_allows_one_pending_or_active_ask_per_key()
    {
        var g = new AskGate();
        Assert.True(g.TryEnter("a"));
        Assert.False(g.TryEnter("a"));
        Assert.True(g.TryEnter("b"));
        g.Exit("a");
        Assert.True(g.TryEnter("a"));
    }
}

public class FormulaEditAutoTests
{
    [Fact]
    public void ParseDetails_InputWithAuto()
    {
        var b = new VariableBinding { Variable = "Pace", Kind = BindingKind.Input };
        var (nb, err) = FormulaEdit.ParseDetails(b, "Slow, Normal, Fast | auto: \"Normal\"");
        Assert.Null(err);
        Assert.Equal(new[] { "Slow", "Normal", "Fast" }, nb.Choices);
        Assert.Equal("\"Normal\"", nb.AutoValue);
        var (r, e2) = FormulaEdit.ParseDetails(b, "0..20 | auto: 5");
        Assert.Null(e2);
        Assert.Equal(20, r.Max);
        Assert.Equal("5", r.AutoValue);
        var (only, e3) = FormulaEdit.ParseDetails(b, "| auto: 3");
        Assert.Null(e3);
        Assert.Equal("3", only.AutoValue);
        var (bad, e4) = FormulaEdit.ParseDetails(b, "1..5 | auto: 2 +");
        Assert.StartsWith("Auto value: ", e4);
        Assert.Same(b, bad);
        var (cleared, _) = FormulaEdit.ParseDetails(nb, "Slow, Normal");
        Assert.Null(cleared.AutoValue);
    }

    [Fact]
    public void FormatDetails_InputWithAuto_RoundTrip()
    {
        var b = new VariableBinding { Variable = "Pace", Kind = BindingKind.Input, Choices = new() { "Slow", "Normal" }, AutoValue = "\"Normal\"" };
        var text = FormulaEdit.FormatDetails(b);
        Assert.Equal("Slow, Normal | auto: \"Normal\"", text);
        Assert.Equal(b.AutoValue, FormulaEdit.ParseDetails(new VariableBinding { Variable = "Pace", Kind = BindingKind.Input }, text).Binding.AutoValue);
        Assert.Equal("| auto: 3", FormulaEdit.FormatDetails(new VariableBinding { Variable = "x", Kind = BindingKind.Input, AutoValue = "3" }));
        Assert.True(FormulaEdit.IsConfigured(new VariableBinding { Variable = "x", Kind = BindingKind.Input, AutoValue = "3" }));
    }
}

public class FormulaEditQuotedChoiceTests
{
    private static VariableBinding Input() => new() { Variable = "Area", Kind = BindingKind.Input };
    private static readonly string[] Five = ["North, coast", "Inland", "Say \"hi\", then go", "a|b", "1..2"];
    private const string FiveText = "\"North, coast\", Inland, \"Say \"\"hi\"\", then go\", \"a|b\", \"1..2\"";

    [Fact]
    public void Choices_with_commas_round_trip()
    {
        var b = Input() with { Choices = Five.ToList() };
        Assert.Equal(FiveText, FormulaEdit.FormatDetails(b));
        var (p, err) = FormulaEdit.ParseDetails(Input(), FiveText);
        Assert.Null(err);
        Assert.Equal(Five, p.Choices);
        Assert.Null(p.Min);
        Assert.Null(p.Max);
    }

    [Fact]
    public void Choices_with_commas_and_auto_value()
    {
        var b = Input() with { Choices = Five.ToList(), AutoValue = "\"Inland\"" };
        var text = FormulaEdit.FormatDetails(b);
        Assert.Equal(FiveText + " | auto: \"Inland\"", text);
        var (p, err) = FormulaEdit.ParseDetails(Input(), text);
        Assert.Null(err);
        Assert.Equal(Five, p.Choices);
        Assert.Equal("\"Inland\"", p.AutoValue);
    }

    [Fact]
    public void Old_details_text_parses_as_before()
    {
        Assert.Equal(["low", "mid", "high"], FormulaEdit.ParseDetails(Input(), "low, mid, high").Binding.Choices);
        var (r, _) = FormulaEdit.ParseDetails(Input(), "0..20 | auto: 2");
        Assert.Equal(0, r.Min);
        Assert.Equal(20, r.Max);
        Assert.Equal("2", r.AutoValue);
        Assert.Equal(["say \"hi\"", "bye"], FormulaEdit.ParseDetails(Input(), "say \"hi\", bye").Binding.Choices);
    }

    [Theory]
    [InlineData("x| auto: y")]
    [InlineData("x|auto: y")]
    public void Non_leading_quoted_choice_containing_bar_auto_round_trips(string tricky)
    {
        var choices = new List<string> { "Inland", tricky };
        var b = Input() with { Choices = choices };
        var text = FormulaEdit.FormatDetails(b);
        var (p, err) = FormulaEdit.ParseDetails(Input(), text);
        Assert.Null(err);
        Assert.Equal(choices, p.Choices);
        Assert.Null(p.AutoValue);

        var withAuto = FormulaEdit.FormatDetails(b with { AutoValue = "\"Inland\"" });
        var (q, err2) = FormulaEdit.ParseDetails(Input(), withAuto);
        Assert.Null(err2);
        Assert.Equal(choices, q.Choices);
        Assert.Equal("\"Inland\"", q.AutoValue);
    }

    [Fact]
    public void Auto_value_may_contain_commas_after_choices()
    {
        var (p, err) = FormulaEdit.ParseDetails(Input(), "low, mid | auto: max(1, 2)");
        Assert.Null(err);
        Assert.Equal(["low", "mid"], p.Choices);
        Assert.Equal("max(1, 2)", p.AutoValue);
    }

    [Fact]
    public void Unclosed_quote_is_an_error()
    {
        var b = Input();
        var (same, err) = FormulaEdit.ParseDetails(b, "\"North, coast");
        Assert.Equal("A choice has an opening quote but no closing quote.", err);
        Assert.Equal(b, same);
    }

    [Fact]
    public void Text_after_closing_quote_is_an_error()
    {
        Assert.Equal("Put the whole choice inside the quotes.", FormulaEdit.ParseDetails(Input(), "\"a\"b, c").Error);
    }

    [Fact]
    public void Split_outside_quotes_is_generic_over_the_separator()
    {
        Assert.Equal(["\"a|b\"", "auto: 1"], FormulaEdit.SplitOutsideQuotes("\"a|b\" | auto: 1", '|'));
        Assert.Equal("\"always ask\"", FormulaEdit.QuoteChoice("always ask"));
    }

    [Fact]
    public void FormulaEdit_with_action_clears_fields()
    {
        var o = new FormulaOutput
        {
            Target = OutputTarget.Piece, PieceVariable = "T", Property = "fuel", PropertyChoice = new() { "fuel" }, PropertySelect = "1",
            Action = OutputAction.Write, Slot = "Wreck", Layer = "Levels", ToMapId = "map-b",
        };
        var place = FormulaEdit.WithOutputAction(o, OutputAction.Place);
        Assert.Equal(OutputAction.Place, place.Action);
        Assert.Equal("Wreck", place.Slot);
        Assert.Null(place.Layer);
        Assert.Null(place.ToMapId);
        Assert.Null(place.PropertyChoice);
        Assert.Null(place.PropertySelect);

        var remove = FormulaEdit.WithOutputAction(o, OutputAction.Remove);
        Assert.Null(remove.Slot);
        Assert.Equal("map-b", remove.ToMapId);

        var layer = FormulaEdit.WithOutputAction(o, OutputAction.SetLayer);
        Assert.Equal("Levels", layer.Layer);
        Assert.Null(layer.Slot);
        Assert.Null(layer.ToMapId);

        Assert.Null(FormulaEdit.WithOutputAction(place, OutputAction.Write).Slot);

        // A target change drops the Piece variable unless the target is Piece.
        Assert.Null(FormulaEdit.WithOutputTarget(o, OutputTarget.Unit).PieceVariable);
        Assert.Equal("T", FormulaEdit.WithOutputTarget(o, OutputTarget.Piece).PieceVariable);

        // Blank is null, never "".
        Assert.Null(FormulaEdit.WithSlot(o, "  ").Slot);
        Assert.Null(FormulaEdit.WithLayer(o, "").Layer);
        Assert.Null(FormulaEdit.WithToMap(o, " ").ToMapId);
        Assert.Null(FormulaEdit.WithPieceVariable(o, "").PieceVariable);
        Assert.Equal("Fort", FormulaEdit.WithLayer(o, " Fort ").Layer);
        Assert.True(FormulaEdit.WithSameHex(new VariableBinding { Kind = BindingKind.Piece }, true).SameHex);
    }

    [Fact]
    public void Target_only_piece_binding_is_not_unused()
    {
        var f = new FormulaDef
        {
            Id = "f", Name = "F", Text = "A",
            Bindings = new() { new VariableBinding { Variable = "A", Kind = BindingKind.Input }, new VariableBinding { Variable = "T", Kind = BindingKind.Piece, Property = "p2" } },
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "T", Action = OutputAction.Remove } },
        };
        Assert.Empty(FormulaEdit.Unused(f));
        Assert.Equal(new[] { "A", "T" }, FormulaEdit.RemoveUnused(f).Bindings.Select(b => b.Variable));
    }
}
