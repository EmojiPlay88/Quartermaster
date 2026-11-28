using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>B9 task 6: the Workbench editors for owner expressions, holder choices, draw bindings and holder-variable outputs. Headless, invented Riverland data.</summary>
[Collection("MacFlag")]
public sealed class B9WorkbenchTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b9wb-" + Guid.NewGuid().ToString("N"));
    readonly List<FixtureModule> _fixtures = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<Window> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        Dispatcher.UIThread.RunJobs();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void Type(EditGrid g, int row, int col, string text) { g.BeginEdit(row, col, text); g.CommitEdit(); Pump(); }

    static void Pick(EditGrid g, int row, int col, string item)
    {
        g.BeginEdit(row, col);
        ((ComboBox)g.Editor!).SelectedItem = item;
        g.CommitEdit();
        Pump();
    }

    static void Toggle(EditGrid g, int row, int col)
    {
        g.SelectCell(row, col);
        g.Focus();
        var w = (Window)TopLevel.GetTopLevel(g)!;
        w.KeyPress(Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.None, (Avalonia.Input.PhysicalKey)0, null);
        Pump();
    }

    /// <summary>Rows: 0 Shots (Input), 1 Hits (Draw), 2 Dump (Holder picker), 3 Foe (Piece). Outputs: 0 Piece Hits, 1 Holder Dump, 2 Unit.</summary>
    static FormulaDef Battery() => new()
    {
        Id = "f-battery", Name = "Shore battery", Text = "Shots + Hits + Dump + Foe",
        Bindings = new()
        {
            new() { Variable = "Shots", Kind = BindingKind.Input, Min = 0, Max = 6 },
            new() { Variable = "Hits", Kind = BindingKind.Draw, Property = "p-boats", PieceFilter = "side() == \"Southern Compact\"", DrawCount = "Shots" },
            new() { Variable = "Dump", Kind = BindingKind.Holder, Property = "p-stores", HolderChoices = new() { "h-ford", "h-mill" } },
            new() { Variable = "Foe", Kind = BindingKind.Piece, Property = "p-boats" },
        },
        Outputs = new()
        {
            new() { Target = OutputTarget.Piece, PieceVariable = "Hits", Property = "p-boats", Op = OutputOp.Subtract, Expr = "Hits" },
            new() { Target = OutputTarget.Holder, HolderVariable = "Dump", Property = "p-stores", Op = OutputOp.Subtract, Expr = "Shots" },
            new() { Target = OutputTarget.Unit, Property = "p-boats", Op = OutputOp.Subtract, Expr = "1" },
        },
    };

    ModuleSession Session(FormulaDef? formula = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.NewRules("Invented");
        s.UpdateRules(p => p with
        {
            Properties = new()
            {
                new PropertyDef { Id = "p-boats", Name = "Boats", Scope = PropertyScope.Pieces, Visibility = Visibility.Private },
                new PropertyDef { Id = "p-stores", Name = "Stores", Scope = PropertyScope.Holders, Holders = new() { "h-ford", "h-mill", "h-north" } },
                new PropertyDef { Id = "p-hidden", Name = "Hidden", Scope = PropertyScope.Holders, Holders = new() { "global" }, Visibility = Visibility.Private },
            },
            Holders = new()
            {
                new HolderDef { Id = "h-ford", Name = "Ford Dump", SideId = "north" },
                new HolderDef { Id = "h-mill", Name = "Mill Dump" },
                new HolderDef { Id = "h-north", Name = "North Depot", SideId = "north" },
            },
            Sides = new() { new SideDef { Id = "north", Name = "Northern League" }, new SideDef { Id = "south", Name = "Southern Compact" } },
            Formulas = new() { formula ?? Battery() },
        });
        return s;
    }

    T Host<T>(T tab) where T : Control
    {
        var win = new Window { Width = 1400, Height = 900, Content = tab };
        _windows.Add(win);
        win.Show();
        Pump();
        return tab;
    }

    FormulasTab Formulas(ModuleSession s) => Host(new FormulasTab(s, _dialogs, _ => null, null));
    VisibilityTab VisTab(ModuleSession s) => Host(new VisibilityTab(s));

    static FormulaDef F(ModuleSession s) => s.Rules!.Formulas.Single();
    static VariableBinding B(ModuleSession s, string v) => F(s).Bindings.Single(b => b.Variable == v);

    const string OwnerTip = "The holder's owner as play goes on: a side's name or id, or a number N for the Nth side (0 = no owner). It may read this holder's and the global holder's Public values. Blank = the owner side.";
    const string ChoicesTip = "Holder binding: the holders the player chooses from when the formula runs, separated by commas; only those the player may act on are offered. Blank = the fixed holder.";
    const string CountTip = "Draw binding: how many members to draw at random, a whole number from 0 to 1000; it may use earlier variables.";
    const string DrawHint = "Which counters to draw from (a unit filter; blank = every counter). Target: the property giving each counter's number of members (one each when none).";
    const string HolderVarTip = "Target Holder: the Holder variable whose chosen holder this output writes.";

    [AvaloniaFact]
    public void Owner_expression_column_commits()
    {
        var s = Session();
        var tab = VisTab(s);
        Assert.Equal("Owner expression", tab.HolderGrid.HeaderText(2));
        Assert.Equal("Owner side", tab.HolderGrid.HeaderText(1));
        Assert.Equal("", tab.HolderGrid.Cell(0, 2));
        Type(tab.HolderGrid, 0, 2, "  \"Southern Compact\" ");
        Assert.Equal("\"Southern Compact\"", s.Rules!.Holders[0].OwnerExpr);
        Assert.Equal("\"Southern Compact\"", tab.HolderGrid.Cell(0, 2));
        Assert.Equal("north", s.Rules.Holders[0].SideId);   // the fixed owner stays: the fallback
        Type(tab.HolderGrid, 0, 2, "   ");
        Assert.Null(s.Rules!.Holders[0].OwnerExpr);
        Pick(tab.HolderGrid, 1, 1, "Southern Compact");      // the owner side column still works
        Assert.Equal("south", s.Rules!.Holders[1].SideId);
        Assert.Equal(1, tab.MapGrid.RowCount);
        Assert.ThrowsAny<Exception>(() => tab.MapGrid.HeaderText(2));   // the maps grid has no owner expression
    }

    [AvaloniaFact]
    public void Owner_expression_problem_shown_in_tab()
    {
        var s = Session();
        var tab = VisTab(s);
        Assert.DoesNotContain("owner expression", tab.Issues.Text ?? "");
        Type(tab.HolderGrid, 0, 2, "Hidden");
        Assert.Contains("Holder \"Ford Dump\": owner expression reads \"Hidden\", which is not Public; every player must be able to tell who owns the holder", tab.Issues.Text ?? "");
        Type(tab.HolderGrid, 0, 2, "1 +");
        Assert.Contains("Holder \"Ford Dump\": owner expression ", tab.Issues.Text ?? "");
    }

    [AvaloniaFact]
    public void Draw_kind_columns_enabled()
    {
        var s = Session();
        var tab = Formulas(s);
        var g = tab.Bindings;
        Assert.Contains("Draw", g.CellItems(0, 1)!);
        // Draw row (1): same hex, draw count and details open; holder choices closed; target lists (one each) and piece properties
        Assert.Equal("Draw", g.Cell(1, 1));
        Assert.False(g.IsCellReadOnly(1, 7));
        Assert.False(g.IsCellReadOnly(1, 11));
        Assert.True(g.IsCellReadOnly(1, 10));
        Assert.False(g.IsCellReadOnly(1, 3));
        Assert.Equal(DrawHint, g.CellToolTip(1, 3));
        Assert.Equal(new[] { "(one each)", "Boats" }, g.CellItems(1, 2));
        Assert.Equal("Boats", g.Cell(1, 2));
        Assert.Equal("Shots", g.Cell(1, 11));
        Assert.Equal("side() == \"Southern Compact\"", g.Cell(1, 3));
        // Holder picker row (2): holder choices open; draw count and same hex closed
        Assert.False(g.IsCellReadOnly(2, 10));
        Assert.True(g.IsCellReadOnly(2, 11));
        Assert.True(g.IsCellReadOnly(2, 7));
        // Piece row (3): same hex still open; neither new column
        Assert.False(g.IsCellReadOnly(3, 7));
        Assert.True(g.IsCellReadOnly(3, 10));
        Assert.True(g.IsCellReadOnly(3, 11));
        // Input row (0): none of the new cells
        Assert.True(g.IsCellReadOnly(0, 10));
        Assert.True(g.IsCellReadOnly(0, 11));

        Type(g, 1, 11, "  Shots + 1 ");
        Assert.Equal("Shots + 1", B(s, "Hits").DrawCount);
        Type(g, 1, 11, " ");
        Assert.Null(B(s, "Hits").DrawCount);
        Type(g, 1, 11, "1 +");   // a syntax error is refused
        Assert.Null(B(s, "Hits").DrawCount);
        Assert.StartsWith("Draw count: ", tab.ErrorLabel);
        Toggle(g, 1, 7);
        Assert.True(B(s, "Hits").SameHex);
        Pick(g, 1, 2, "(one each)");
        Assert.Null(B(s, "Hits").Property);
        Assert.Equal("(one each)", g.Cell(1, 2));

        // Piece -> Draw keeps the pool
        Pick(g, 3, 1, "Draw");
        Assert.Equal(BindingKind.Draw, B(s, "Foe").Kind);
        Assert.Equal("p-boats", B(s, "Foe").Property);
        Assert.False(g.IsCellReadOnly(3, 11));
    }

    [AvaloniaFact]
    public void Holder_choices_column_commits()
    {
        var s = Session();
        var tab = Formulas(s);
        var g = tab.Bindings;
        Assert.Equal("Ford Dump, Mill Dump", g.Cell(2, 10));
        Assert.Equal("Stores of (chosen holder)", g.Cell(2, 2));
        Assert.Equal(new[] { "Stores of (chosen holder)" }, g.CellItems(2, 2));
        Type(g, 2, 10, " north depot , Ford Dump ");
        Assert.Equal(new[] { "h-north", "h-ford" }, B(s, "Dump").HolderChoices);
        Assert.Equal("North Depot, Ford Dump", g.Cell(2, 10));
        Type(g, 2, 10, "Ford Dump, Hill Fort");
        Assert.Equal("No holder is named \"Hill Fort\"", tab.ErrorLabel);
        Assert.Equal(new[] { "h-north", "h-ford" }, B(s, "Dump").HolderChoices);   // refused: unchanged
        Type(g, 2, 10, "  ");
        Assert.Null(B(s, "Dump").HolderChoices);
        Assert.Contains("Ford Dump: Stores", g.CellItems(2, 2)!);   // a fixed holder again
    }

    [AvaloniaFact]
    public void Holder_variable_combo_lists_picker_variables()
    {
        var s = Session();
        var tab = Formulas(s);
        var g = tab.Outputs;
        Assert.Equal("Holder variable", g.HeaderText(14));
        Assert.Equal(new[] { "", "Dump" }, g.CellItems(1, 14));
        Assert.Equal("Dump", g.Cell(1, 14));
        Assert.False(g.IsCellReadOnly(1, 14));
        Assert.True(g.IsCellReadOnly(0, 14));
        Assert.True(g.IsCellReadOnly(2, 14));
        Pick(g, 1, 14, "");
        Assert.Null(F(s).Outputs[1].HolderVariable);
        Pick(g, 1, 14, "Dump");
        Assert.Equal("Dump", F(s).Outputs[1].HolderVariable);
        Assert.Equal(new[] { "Stores" }, g.CellItems(1, 2));   // what every listed holder keeps
        Pick(g, 1, 0, "Unit");
        Assert.Null(F(s).Outputs[1].HolderVariable);
        Assert.True(g.IsCellReadOnly(1, 14));
    }

    [AvaloniaFact]
    public void Piece_combo_lists_draw_variables()
    {
        var s = Session();
        var tab = Formulas(s);
        Assert.Equal(new[] { "Hits", "Foe" }, tab.Outputs.CellItems(0, 8));
        Assert.Equal("Hits", tab.Outputs.Cell(0, 8));
        Pick(tab.Outputs, 0, 8, "Foe");
        Assert.Equal("Foe", F(s).Outputs[0].PieceVariable);
    }

    [AvaloniaFact]
    public void Older_column_indexes_unchanged()
    {
        var s = Session();
        var tab = Formulas(s);
        Assert.Equal(["Variable", "Kind", "Target", "Details", "Caption", "Only when", "Dice count", "Same hex", "Choices table", "Choices filter", "Holder choices", "Draw count"],
            Enumerable.Range(0, 12).Select(tab.Bindings.HeaderText).ToList());
        Assert.Equal(["Target", "Holder", "Property", "Op", "Value", "Choose from", "Pick by", "Action", "Piece", "Palette entry", "Layer", "To map", "Key", "Margin", "Holder variable"],
            Enumerable.Range(0, 15).Select(tab.Outputs.HeaderText).ToList());
        var v = VisTab(s);
        Assert.Equal(["Holder", "Owner side", "Owner expression"], Enumerable.Range(0, 3).Select(v.HolderGrid.HeaderText).ToList());
    }

    [AvaloniaFact]
    public void Tooltips_text()
    {
        var s = Session();
        var tab = Formulas(s);
        Assert.Equal(ChoicesTip, tab.Bindings.HeaderToolTip(10));
        Assert.Equal(CountTip, tab.Bindings.HeaderToolTip(11));
        Assert.Equal(HolderVarTip, tab.Outputs.HeaderToolTip(14));
        Assert.Equal(DrawHint, FormulaEdit.DrawDetailsHint);
        var v = VisTab(s);
        Assert.Equal(OwnerTip, v.HolderGrid.HeaderToolTip(2));
    }

    [AvaloniaFact]
    public void Problems_list_shows_b9_errors()
    {
        var f = Battery();
        f = f with { Bindings = f.Bindings.Select(b => b.Variable == "Hits" ? b with { DrawCount = null } : b).ToList() };
        var s = Session(f);
        s.UpdateRules(p => p with { Holders = p.Holders.Select(h => h.Id == "h-ford" ? h with { OwnerExpr = "Hidden" } : h).ToList() });
        var wb = new WorkbenchWindow(s, _dialogs, () => Task.CompletedTask, () => Task.CompletedTask);
        _windows.Add(wb);
        wb.Show();
        Pump();
        wb.CommitPending();
        Pump();
        Assert.Contains(wb.IssueRows, r => r.Problem.Contains("variable \"Hits\" draws at random but has no draw count", StringComparison.Ordinal));
        Assert.Contains(wb.IssueRows, r => r.Problem.Contains("owner expression reads \"Hidden\"", StringComparison.Ordinal));
    }
}
