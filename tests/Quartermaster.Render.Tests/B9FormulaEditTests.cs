using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>B9 task 6: the Workbench edits behind owner expressions, holder choices, draw bindings and holder-variable outputs (invented Riverland data).</summary>
public class B9FormulaEditTests
{
    private static RulesPack Pack() => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "p-boats", Name = "Boats", Scope = PropertyScope.Pieces },
            new PropertyDef { Id = "p-crew", Name = "Crew", Scope = PropertyScope.Both },
            new PropertyDef { Id = "p-stores", Name = "Stores", Scope = PropertyScope.Holders, Holders = new() { "h-ford", "h-mill", "h-north" } },
            new PropertyDef { Id = "p-fordonly", Name = "FordOnly", Scope = PropertyScope.Holders, Holders = new() { "h-ford" } },
        },
        Holders = new()
        {
            new HolderDef { Id = "h-ford", Name = "Ford Dump", SideId = "north" },
            new HolderDef { Id = "h-mill", Name = "Mill Dump" },
            new HolderDef { Id = "h-north", Name = "North Depot", SideId = "north" },
            new HolderDef { Id = "h-comma", Name = "South, Depot", SideId = "south" },
        },
    };

    [Fact]
    public void Owner_expr_blank_null_trimmed()
    {
        var p = Pack();
        var set = VisibilityEdit.SetHolderOwnerExpr(p, "h-ford", "  keyed(DumpOwner, \"Ford Dump\") ");
        Assert.Equal("keyed(DumpOwner, \"Ford Dump\")", set.Holders[0].OwnerExpr);
        Assert.Null(p.Holders[0].OwnerExpr);   // pure
        Assert.Same(set, VisibilityEdit.SetHolderOwnerExpr(set, "h-ford", "keyed(DumpOwner, \"Ford Dump\")"));   // no change: same instance
        Assert.Null(VisibilityEdit.SetHolderOwnerExpr(set, "h-ford", "   ").Holders[0].OwnerExpr);
        Assert.Null(VisibilityEdit.SetHolderOwnerExpr(set, "h-ford", null).Holders[0].OwnerExpr);
        Assert.Same(p, VisibilityEdit.SetHolderOwnerExpr(p, "h-nowhere", "1"));
        Assert.Same(p, VisibilityEdit.SetHolderOwnerExpr(p, "h-mill", " "));
    }

    [Fact]
    public void Holder_choices_names_to_ids_unknown_reported()
    {
        var p = Pack();
        var b = new VariableBinding { Variable = "Dump", Kind = BindingKind.Holder, Property = "p-stores", HolderId = "h-ford" };
        var (nb, err) = FormulaEdit.WithHolderChoices(p, b, " ford dump, Mill Dump ,\"South, Depot\", Ford Dump ");
        Assert.Null(err);
        Assert.Equal(new[] { "h-ford", "h-mill", "h-comma" }, nb.HolderChoices);
        Assert.Null(nb.HolderId);   // a picker's fixed holder is not used
        Assert.Equal("p-stores", nb.Property);
        Assert.Equal("Ford Dump, Mill Dump, \"South, Depot\"", FormulaEdit.FormatHolderChoices(p, nb));

        var (bad, badErr) = FormulaEdit.WithHolderChoices(p, b, "Ford Dump, Hill Fort");
        Assert.Equal("No holder is named \"Hill Fort\"", badErr);
        Assert.Equal(new[] { "h-ford" }, bad.HolderChoices);   // the unknown name is kept out

        var (cleared, clearErr) = FormulaEdit.WithHolderChoices(p, nb, "  ");
        Assert.Null(clearErr);
        Assert.Null(cleared.HolderChoices);   // blank = the fixed holder (never an empty list)
        Assert.Equal("", FormulaEdit.FormatHolderChoices(p, cleared));

        var (_, quoteErr) = FormulaEdit.WithHolderChoices(p, b, "\"Ford Dump");
        Assert.NotNull(quoteErr);
    }

    [Fact]
    public void With_kind_clears_unused_fields()
    {
        var all = new VariableBinding
        {
            Variable = "X", Kind = BindingKind.Holder, Property = "p-boats", PieceFilter = "Boats > 0",
            HolderChoices = new() { "h-ford" }, DrawCount = "2", SameHex = true,
        };
        var holder = FormulaEdit.WithKind(all, BindingKind.Holder);
        Assert.Equal(new[] { "h-ford" }, holder.HolderChoices);
        Assert.Null(holder.DrawCount);
        Assert.False(holder.SameHex);

        var draw = FormulaEdit.WithKind(all, BindingKind.Draw);
        Assert.Equal(BindingKind.Draw, draw.Kind);
        Assert.Null(draw.HolderChoices);
        Assert.Equal("2", draw.DrawCount);
        Assert.True(draw.SameHex);

        var piece = FormulaEdit.WithKind(all, BindingKind.Piece);
        Assert.Null(piece.HolderChoices);
        Assert.Null(piece.DrawCount);
        Assert.True(piece.SameHex);

        var input = FormulaEdit.WithKind(all, BindingKind.Input);
        Assert.Null(input.HolderChoices);
        Assert.Null(input.DrawCount);
        Assert.False(input.SameHex);

        // The formula-level kind change: Piece <-> Draw keeps the pool (filter, weight property, same hex); other changes start afresh.
        var f = new FormulaDef
        {
            Id = "f", Name = "Shore battery", Text = "Hits",
            Bindings = new() { new VariableBinding { Variable = "Hits", Kind = BindingKind.Piece, Property = "p-boats", PieceFilter = "Boats > 0", SameHex = true, Caption = "Hit" } },
        };
        var asDraw = FormulaEdit.SetKind(f, "Hits", BindingKind.Draw).Bindings[0];
        Assert.Equal(BindingKind.Draw, asDraw.Kind);
        Assert.Equal("p-boats", asDraw.Property);
        Assert.Equal("Boats > 0", asDraw.PieceFilter);
        Assert.True(asDraw.SameHex);
        Assert.Equal("Hit", asDraw.Caption);
        var asInput = FormulaEdit.SetKind(f, "Hits", BindingKind.Input).Bindings[0];
        Assert.Null(asInput.Property);
        Assert.Null(asInput.PieceFilter);
        Assert.False(asInput.SameHex);
    }

    [Fact]
    public void With_output_target_clears_holder_variable()
    {
        var o = new FormulaOutput { Target = OutputTarget.Holder, HolderVariable = "Dump", Property = "p-stores" };
        Assert.Equal("Dump", FormulaEdit.WithOutputTarget(o, OutputTarget.Holder).HolderVariable);
        Assert.Null(FormulaEdit.WithOutputTarget(o, OutputTarget.Unit).HolderVariable);
        Assert.Null(FormulaEdit.WithOutputTarget(o, OutputTarget.Global).HolderVariable);
        Assert.Equal("Dump", FormulaEdit.WithHolderVariable(o with { HolderVariable = null }, " Dump ").HolderVariable);
        Assert.Null(FormulaEdit.WithHolderVariable(o, " ").HolderVariable);
    }

    [Fact]
    public void Draw_count_blank_null()
    {
        var b = new VariableBinding { Variable = "Hits", Kind = BindingKind.Draw };
        Assert.Equal("Shots + 1", FormulaEdit.WithDrawCount(b, "  Shots + 1 ").DrawCount);
        Assert.Null(FormulaEdit.WithDrawCount(b with { DrawCount = "2" }, "   ").DrawCount);
        Assert.Null(FormulaEdit.WithDrawCount(b with { DrawCount = "2" }, null).DrawCount);
    }

    [Fact]
    public void Draw_target_and_details()
    {
        var p = Pack();
        var b = new VariableBinding { Variable = "Hits", Kind = BindingKind.Draw };
        var opts = FormulaEdit.TargetOptions(p, b);
        Assert.Equal(new[] { "(one each)", "Boats", "Crew" }, opts.Select(o => o.Display));
        Assert.Null(opts[0].Property);
        Assert.Equal("(one each)", FormulaEdit.TargetDisplay(p, b));
        Assert.Equal("Boats", FormulaEdit.TargetDisplay(p, b with { Property = "p-boats" }));
        Assert.Equal("Which counters to draw from (a unit filter; blank = every counter). Target: the property giving each counter's number of members (one each when none).",
            FormulaEdit.DrawDetailsHint);
        var (nb, err) = FormulaEdit.ParseDetails(b, " side() == \"Southern Compact\" ");
        Assert.Null(err);
        Assert.Equal("side() == \"Southern Compact\"", nb.PieceFilter);
        Assert.Equal("side() == \"Southern Compact\"", FormulaEdit.FormatDetails(nb));
        Assert.Null(FormulaEdit.ParseDetails(nb, " ").Binding.PieceFilter);
        Assert.NotNull(FormulaEdit.ParseDetails(b, "1 +").Error);
    }

    [Fact]
    public void Holder_picker_target_options()
    {
        var p = Pack();
        var b = new VariableBinding { Variable = "Dump", Kind = BindingKind.Holder, HolderChoices = new() { "h-ford", "h-mill" } };
        var opts = FormulaEdit.TargetOptions(p, b);
        // only what every listed holder keeps: Crew (Both, no holder list) and Stores; not FordOnly (Ford only), not Boats (pieces only)
        Assert.Equal(new[] { "Crew of (chosen holder)", "Stores of (chosen holder)" }, opts.Select(o => o.Display));
        Assert.All(opts, o => Assert.Null(o.HolderId));
        var set = FormulaEdit.ApplyTarget(b, opts[1]);
        Assert.Equal("p-stores", set.Property);
        Assert.Equal("Stores of (chosen holder)", FormulaEdit.TargetDisplay(p, set));
        // a fixed Holder binding still lists "{holder}: {prop}"
        Assert.Contains(FormulaEdit.TargetOptions(p, b with { HolderChoices = null }), o => o.Display == "Ford Dump: Stores");
        Assert.Empty(FormulaEdit.FormulasUsingHolder(p, "h-mill"));   // no formula: nothing uses the holder yet
        var withF = p with { Formulas = new() { new FormulaDef { Id = "f", Name = "Draw stores", Text = "Dump", Bindings = new() { b } } } };
        Assert.Single(FormulaEdit.FormulasUsingHolder(withF, "h-mill"));
    }
}
