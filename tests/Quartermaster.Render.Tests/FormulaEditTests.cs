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

    [Fact]
    public void SetKind_resets_kind_specific_fields_but_keeps_caption()
    {
        var f = F("A", new VariableBinding { Variable = "A", Kind = BindingKind.Table, TableId = "t", RowExpr = "X", ColExpr = "Y", Caption = "cap", Min = 1 });
        var g = FormulaEdit.SetKind(f, "A", BindingKind.Roll);
        var b = g.Bindings[0];
        Assert.Equal(BindingKind.Roll, b.Kind);
        Assert.Null(b.TableId); Assert.Null(b.RowExpr); Assert.Null(b.Min);
        Assert.Equal("cap", b.Caption);
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
    public void RemoveUnused_drops_every_stale_binding_only()
    {
        var f = F("C", new VariableBinding { Variable = "A", Kind = BindingKind.Table, TableId = "t" }, new VariableBinding { Variable = "C", Kind = BindingKind.Input });
        var g = FormulaEdit.RemoveUnused(f);
        Assert.Equal(new[] { "C" }, g.Bindings.Select(b => b.Variable));
        Assert.Equal(new[] { "A" }, FormulaEdit.Unused(f).Select(b => b.Variable));
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
