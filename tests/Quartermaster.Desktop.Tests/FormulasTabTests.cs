using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4a task 7: the workbench Formulas tab. Headless, fake dialogs, invented formulas.</summary>
[Collection("MacFlag")]
public sealed class FormulasTabTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-formulastab-" + Guid.NewGuid().ToString("N"));
    readonly List<FixtureModule> _fixtures = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<Window> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }

    /// <summary>Fires the 400 ms debounce tick (the typed text is pending until then).</summary>
    static void Debounce(FormulasTab tab) { tab.FireDebounce(); Pump(); }

    static FormulaDef Attack() => new()
    {
        Id = "f-att", Name = "Attack", Text = "a + b",
        Bindings = new() { new() { Variable = "a", Kind = BindingKind.Input }, new() { Variable = "b", Kind = BindingKind.Input } },
    };

    static FormulaDef Defend() => new() { Id = "f-def", Name = "Defend", Text = "x * 2", Bindings = new() { new() { Variable = "x", Kind = BindingKind.Input } } };

    ModuleSession Session(bool withPack = true, params FormulaDef[] formulas) => SessionWith(withPack, false, formulas);

    ModuleSession SessionWith(bool withPack, bool game, params FormulaDef[] formulas)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        if (withPack)
        {
            s.NewRules("Invented");
            s.UpdateRules(p => p with
            {
                Properties = new() { new PropertyDef { Id = "p-str", Name = "Strength" }, new PropertyDef { Id = "p-mov", Name = "Moves" } },
                Holders = new() { new HolderDef { Id = "h-depot", Name = "Depot" } },
                Tables = new() { new TableDef { Id = "t-cost", Name = "Cost" } },
                Formulas = formulas.ToList(),
            });
        }
        if (game) s.LoadSetup(s.Model.Setups[0], null);
        return s;
    }

    FormulasTab Tab(ModuleSession s, Func<string, RuleRef?>? link = null, Action<RuleRef>? show = null)
    {
        var tab = new FormulasTab(s, _dialogs, link ?? (_ => null), show);
        var w = new Window { Width = 900, Height = 700, Content = tab };
        _windows.Add(w);
        w.Show();
        Pump();
        return tab;
    }

    static FormulaDef F(ModuleSession s, string id) => s.Rules!.Formulas.Single(f => f.Id == id);

    static VariableBinding B(ModuleSession s, string id, string v) => F(s, id).Bindings.Single(b => b.Variable == v);

    static void Pick(EditGrid g, int row, int col, string choice)
    {
        g.BeginEdit(row, col);
        ((ComboBox)g.Editor!).SelectedItem = choice;
        g.CommitEdit();
        Pump();
    }

    [AvaloniaFact]
    public void FormulasTab_only_in_phases_commits_and_clears()
    {
        var s = Session(true, Attack(), Defend());
        s.UpdateRules(p => p with { Phases = PhaseFixture.Pack("x").Phases });
        var tab = Tab(s);
        Assert.Equal(["Weather", "Supply", "Movement", "Combat", "End"], tab.PhaseChecks.Select(c => (string)c.Content!).ToList());
        Assert.All(tab.PhaseChecks, c => Assert.NotEqual(true, c.IsChecked));

        tab.PhaseChecks[2].IsChecked = true;   // Movement
        Pump();
        Assert.Equal(["ph-move"], F(s, "f-att").Phases);
        tab.PhaseChecks[3].IsChecked = true;   // Combat
        Pump();
        Assert.Equal(["ph-move", "ph-combat"], F(s, "f-att").Phases);
        Assert.Null(F(s, "f-def").Phases);

        // another formula shows its own list; coming back shows what was saved
        tab.SelectFormula("f-def");
        Pump();
        Assert.All(tab.PhaseChecks, c => Assert.NotEqual(true, c.IsChecked));
        tab.SelectFormula("f-att");
        Pump();
        Assert.Equal([false, false, true, true, false], tab.PhaseChecks.Select(c => c.IsChecked == true).ToList());

        tab.PhaseChecks[2].IsChecked = false;
        tab.PhaseChecks[3].IsChecked = false;
        Pump();
        Assert.Null(F(s, "f-att").Phases);   // none checked: null, never an empty list
    }

    [AvaloniaFact]
    public void FormulasTab_precondition_commits_and_clears()
    {
        var s = Session(true, Attack(), Defend());
        var tab = Tab(s);
        Assert.Equal("", tab.Precondition.Text ?? "");
        Assert.Equal("", tab.RefusalMessage.Text ?? "");

        tab.Precondition.Text = "c > 0";
        tab.RefusalMessage.Text = "Needs a charge";
        Debounce(tab);
        Assert.Equal("c > 0", F(s, "f-att").Precondition);
        Assert.Equal("Needs a charge", F(s, "f-att").RefusalMessage);
        Assert.Null(F(s, "f-def").Precondition);
        Assert.Contains(F(s, "f-att").Bindings, b => b.Variable == "c");   // a variable only the guard reads gets a binding row
        Assert.Equal(3, tab.Bindings.RowCount);

        tab.SelectFormula("f-def");
        Pump();
        Assert.Equal("", tab.Precondition.Text ?? "");
        tab.SelectFormula("f-att");
        Pump();
        Assert.Equal("c > 0", tab.Precondition.Text);
        Assert.Equal("Needs a charge", tab.RefusalMessage.Text);

        tab.Precondition.Text = "  ";
        tab.RefusalMessage.Text = "";
        Debounce(tab);
        Assert.Null(F(s, "f-att").Precondition);   // blank: null, never ""
        Assert.Null(F(s, "f-att").RefusalMessage);
    }

    [AvaloniaFact]
    public void FormulasTab_dice_count_commits_and_clears()
    {
        var planes = new FormulaDef
        {
            Id = "f-sq", Name = "Squadron", Text = "R + P",
            Bindings = new() { new() { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 }, new() { Variable = "P", Kind = BindingKind.Input } },
        };
        var s = Session(true, planes);
        var tab = Tab(s);
        tab.Bindings.BeginEdit(0, 6, "P * 2");
        tab.Bindings.CommitEdit();
        Pump();
        Assert.Equal("P * 2", B(s, "f-sq", "R").DiceCountExpr);
        Assert.Equal("P * 2", tab.Bindings.Cell(0, 6));

        tab.Bindings.BeginEdit(0, 6, "z + 1");   // a variable only the count reads gets a row
        tab.Bindings.CommitEdit();
        Pump();
        Assert.Contains(F(s, "f-sq").Bindings, b => b.Variable == "z");

        tab.Bindings.BeginEdit(0, 6, "P *");   // syntax error: rejected, old value kept
        tab.Bindings.CommitEdit();
        Pump();
        Assert.Equal("z + 1", B(s, "f-sq", "R").DiceCountExpr);

        tab.Bindings.BeginEdit(0, 6, "  ");
        tab.Bindings.CommitEdit();
        Pump();
        Assert.Null(B(s, "f-sq", "R").DiceCountExpr);   // blank is null, never ""
    }

    static void Type(EditGrid g, int row, int col, string text) { g.BeginEdit(row, col, text); g.CommitEdit(); Pump(); }

    static FormulaDef OneOutput() => Attack() with { Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "p-str", Op = OutputOp.Subtract } } };

    [AvaloniaFact]
    public void FormulasTab_output_value_commits_and_clears()
    {
        var s = Session(true, OneOutput());
        var tab = Tab(s);
        Type(tab.Outputs, 0, 4, " a * 2 ");
        Assert.Equal("a * 2", F(s, "f-att").Outputs[0].Expr);
        Assert.Equal("a * 2", tab.Outputs.Cell(0, 4));

        Type(tab.Outputs, 0, 4, "a *");   // syntax error: rejected, old value kept
        Assert.Equal("a * 2", F(s, "f-att").Outputs[0].Expr);

        Type(tab.Outputs, 0, 4, "z + 1");   // a variable only the output reads gets a row
        Assert.Contains(F(s, "f-att").Bindings, b => b.Variable == "z");

        Type(tab.Outputs, 0, 4, "  ");
        Assert.Null(F(s, "f-att").Outputs[0].Expr);   // blank is null, never ""
    }

    [AvaloniaFact]
    public void FormulasTab_output_choice_commits_and_clears()
    {
        var s = Session(true, OneOutput());
        var tab = Tab(s);
        Type(tab.Outputs, 0, 5, "Strength, Moves");
        Assert.Equal(new[] { "p-str", "p-mov" }, F(s, "f-att").Outputs[0].PropertyChoice);
        Assert.Equal("Strength, Moves", tab.Outputs.Cell(0, 5));

        Type(tab.Outputs, 0, 6, "a");
        Assert.Equal("a", F(s, "f-att").Outputs[0].PropertySelect);
        Type(tab.Outputs, 0, 6, "a +");   // syntax error: rejected
        Assert.Equal("a", F(s, "f-att").Outputs[0].PropertySelect);

        Type(tab.Outputs, 0, 5, "Strength, Nope");   // an unknown property: rejected, old list kept
        Assert.Equal(new[] { "p-str", "p-mov" }, F(s, "f-att").Outputs[0].PropertyChoice);

        Type(tab.Outputs, 0, 5, "");
        Assert.Null(F(s, "f-att").Outputs[0].PropertyChoice);   // empty is null, never []
        Type(tab.Outputs, 0, 6, " ");
        Assert.Null(F(s, "f-att").Outputs[0].PropertySelect);
    }

    [AvaloniaFact]
    public void FormulasTab_output_property_cell_is_read_only_with_a_list()
    {
        var s = Session(true, OneOutput());
        var tab = Tab(s);
        Assert.False(tab.Outputs.IsCellReadOnly(0, 2));
        Type(tab.Outputs, 0, 5, "Strength, Moves");
        Assert.True(tab.Outputs.IsCellReadOnly(0, 2));   // the fixed Property is not used while a list is set
        Type(tab.Outputs, 0, 5, "");
        Assert.False(tab.Outputs.IsCellReadOnly(0, 2));
    }

    [AvaloniaFact]
    public void FormulasTab_output_choice_quotes_names_with_commas()
    {
        var s = Session(true, OneOutput());
        s.UpdateRules(p => p with { Properties = p.Properties.Append(new PropertyDef { Id = "p-dsl", Name = "Fuel, diesel" }).ToList() });
        var tab = Tab(s);
        Type(tab.Outputs, 0, 5, "Strength, \"Fuel, diesel\"");
        Assert.Equal(new[] { "p-str", "p-dsl" }, F(s, "f-att").Outputs[0].PropertyChoice);
        Assert.Equal("Strength, \"Fuel, diesel\"", tab.Outputs.Cell(0, 5));   // shown in the same quoted format, so it reads back
        Type(tab.Outputs, 0, 5, "\"Fuel, diesel\"");
        Assert.Equal(new[] { "p-dsl" }, F(s, "f-att").Outputs[0].PropertyChoice);
    }

    static void Toggle(EditGrid g, int row, int col)
    {
        g.SelectCell(row, col);
        g.Focus();
        var w = (Window)TopLevel.GetTopLevel(g)!;
        w.KeyPress(Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.None, (Avalonia.Input.PhysicalKey)0, null);
        Pump();
    }

    static FormulaDef PieceFormula() => Attack() with
    {
        Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Piece, Property = "p-str" }, new VariableBinding { Variable = "a", Kind = BindingKind.Input } },
        Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "p-str", Op = OutputOp.Subtract } },
    };

    [AvaloniaFact]
    public void FormulasTab_output_action_columns_commit()
    {
        var s = Session(true, PieceFormula());
        var tab = Tab(s);
        Assert.Equal("Action", tab.Outputs.HeaderText(7));
        Assert.Equal("Piece", tab.Outputs.HeaderText(8));
        Assert.Equal("Palette entry", tab.Outputs.HeaderText(9));
        Assert.Equal("Layer", tab.Outputs.HeaderText(10));
        Assert.Equal("To map", tab.Outputs.HeaderText(11));
        Assert.Equal("Write", tab.Outputs.Cell(0, 7));

        Pick(tab.Outputs, 0, 0, "Piece");
        Pick(tab.Outputs, 0, 8, "R");
        Assert.Equal("R", F(s, "f-att").Outputs[0].PieceVariable);
        Assert.Equal(OutputTarget.Piece, F(s, "f-att").Outputs[0].Target);

        Pick(tab.Outputs, 0, 7, "Place");
        Type(tab.Outputs, 0, 9, "Wreck");
        Assert.Equal(OutputAction.Place, F(s, "f-att").Outputs[0].Action);
        Assert.Equal("Wreck", F(s, "f-att").Outputs[0].Slot);
        Type(tab.Outputs, 0, 9, "  ");
        Assert.Null(F(s, "f-att").Outputs[0].Slot);   // blank is null

        Pick(tab.Outputs, 0, 7, "SetLayer");
        Type(tab.Outputs, 0, 10, " Levels ");
        Assert.Equal("Levels", F(s, "f-att").Outputs[0].Layer);
    }

    [AvaloniaFact]
    public void FormulasTab_old_output_columns_keep_places()
    {
        var s = Session(true, OneOutput());
        var tab = Tab(s);
        string[] old = ["Target", "Holder", "Property", "Op", "Value", "Choose from", "Pick by"];
        for (var i = 0; i < old.Length; i++) Assert.Equal(old[i], tab.Outputs.HeaderText(i));
        Assert.Equal("Unit", tab.Outputs.Cell(0, 0));
        Assert.Equal("Strength", tab.Outputs.Cell(0, 2));
        Assert.Equal("Subtract", tab.Outputs.Cell(0, 3));
        Assert.Equal("Dice count", tab.Bindings.HeaderText(6));
        Assert.Equal("Same hex", tab.Bindings.HeaderText(7));
    }

    [AvaloniaFact]
    public void FormulasTab_action_change_clears_unused_fields()
    {
        var f = PieceFormula() with
        {
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "R", Action = OutputAction.Remove, ToMapId = "m-two" } },
        };
        var s = Session(true, f);
        var tab = Tab(s);
        Pick(tab.Outputs, 0, 7, "SetMask");
        var o = F(s, "f-att").Outputs[0];
        Assert.Equal(OutputAction.SetMask, o.Action);
        Assert.Null(o.ToMapId);
        Pick(tab.Outputs, 0, 0, "Unit");
        Assert.Null(F(s, "f-att").Outputs[0].PieceVariable);
        Pick(tab.Outputs, 0, 7, "Write");
        Assert.Equal(OutputAction.Write, F(s, "f-att").Outputs[0].Action);
    }

    [AvaloniaFact]
    public void FormulasTab_same_hex_column()
    {
        var s = Session(true, PieceFormula());
        var tab = Tab(s);
        Assert.Equal("False", tab.Bindings.Cell(0, 7));
        Assert.False(tab.Bindings.IsCellReadOnly(0, 7));
        Assert.True(tab.Bindings.IsCellReadOnly(1, 7));   // an Input binding has no same hex
        Toggle(tab.Bindings, 0, 7);
        Assert.True(B(s, "f-att", "R").SameHex);
        Toggle(tab.Bindings, 0, 7);
        Assert.False(B(s, "f-att", "R").SameHex);
    }

    [AvaloniaFact]
    public void FormulasTab_to_map_delete_is_null()
    {
        var s = Session(true, PieceFormula() with { Outputs = new() { new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "R", Action = OutputAction.Remove } } });
        var tab = Tab(s);
        Assert.Equal("(delete)", tab.Outputs.Cell(0, 11));
        var map = s.Model.Maps[0];
        Pick(tab.Outputs, 0, 11, map.Name);
        Assert.Equal(map.Id, F(s, "f-att").Outputs[0].ToMapId);
        Assert.Equal(map.Name, tab.Outputs.Cell(0, 11));
        Pick(tab.Outputs, 0, 11, "(delete)");
        Assert.Null(F(s, "f-att").Outputs[0].ToMapId);
    }

    [AvaloniaFact]
    public void FormulasTab_dice_count_is_closed_for_a_non_roll()
    {
        var s = Session(true, Attack());
        var tab = Tab(s);
        tab.Bindings.BeginEdit(0, 6, "3");
        tab.Bindings.CommitEdit();
        Pump();
        Assert.Null(B(s, "f-att", "a").DiceCountExpr);
    }

    [AvaloniaFact]
    public void FormulasTab_binding_when_commits_and_clears()
    {
        var s = Session(true, Attack());
        var tab = Tab(s);
        tab.Bindings.BeginEdit(0, 5, "b > 1");
        tab.Bindings.CommitEdit();
        Pump();
        Assert.Equal("b > 1", B(s, "f-att", "a").When);
        Assert.Equal("b > 1", tab.Bindings.Cell(0, 5));

        tab.Bindings.BeginEdit(0, 5, "z == 2");   // a variable only the condition reads gets a row
        tab.Bindings.CommitEdit();
        Pump();
        Assert.Contains(F(s, "f-att").Bindings, b => b.Variable == "z");

        tab.Bindings.BeginEdit(0, 5, "  ");
        tab.Bindings.CommitEdit();
        Pump();
        Assert.Null(B(s, "f-att", "a").When);
    }

    [AvaloniaFact]
    public void Select_and_rename_formula()
    {
        var s = Session(true, Attack(), Defend());
        var tab = Tab(s);
        Assert.Equal(["Attack", "Defend"], ((IEnumerable<string>)tab.List.ItemsSource!).ToList());
        Assert.Equal("f-att", tab.SelectedFormulaId);
        Assert.Equal("a + b", tab.Text.Text);
        tab.SelectFormula("f-def");
        Pump();
        Assert.Equal("f-def", tab.SelectedFormulaId);
        Assert.Equal("x * 2", tab.Text.Text);
        Assert.Equal(1, tab.Bindings.RowCount);
        tab.SelectFormula("nope");
        Assert.Equal("f-def", tab.SelectedFormulaId);

        _dialogs.Texts.Enqueue("Guard");
        Click(tab.RenameButton);
        Assert.Equal("Guard", F(s, "f-def").Name);
        Assert.Equal("Defend", _dialogs.TextPrompts[^1].Split('|')[2]);
        _dialogs.Texts.Enqueue("ATTACK");   // duplicate, case-insensitive
        Click(tab.RenameButton);
        Assert.Equal("Guard", F(s, "f-def").Name);
        Assert.Equal("Rename formula", _dialogs.Warnings[^1].Caption);

        _dialogs.Texts.Enqueue("Charge");
        Click(tab.NewButton);
        Assert.Equal("Formula 3", _dialogs.TextPrompts[^1].Split('|')[2]);
        Assert.Equal("Charge", s.Rules!.Formulas[2].Name);
        Assert.Equal(s.Rules.Formulas[2].Id, tab.SelectedFormulaId);

        _dialogs.Answer = false;
        Click(tab.DeleteButton);
        Assert.Equal(3, s.Rules.Formulas.Count);
        _dialogs.Answer = true;
        Click(tab.DeleteButton);
        Assert.Equal(2, s.Rules.Formulas.Count);
        Assert.True(_dialogs.YesNoDefaultNo[^1]);
        Assert.Equal("Delete the formula \"Charge\"?", _dialogs.Questions[^1].Message);
    }

    [AvaloniaFact]
    public void Text_error_shown_and_not_committed()
    {
        var s = Session(true, Attack());
        var tab = Tab(s);
        tab.Text.Text = "a + ";
        Assert.StartsWith("Error at ", tab.ErrorLabel);
        Assert.True(tab.GoToErrorVisible);
        Debounce(tab);
        // the text is stored (the validator reports it) but the bindings are not synced to a text that does not parse
        Assert.Equal("a + ", F(s, "f-att").Text);
        Assert.Equal(["a", "b"], F(s, "f-att").Bindings.Select(b => b.Variable).ToList());
        tab.ClickGoToError();
        Assert.Equal(4, tab.Text.CaretIndex);
        tab.Text.Text = "a + b";
        Assert.Equal("", tab.ErrorLabel);
        Assert.False(tab.GoToErrorVisible);
    }

    [AvaloniaFact]
    public void Text_commits_after_debounce_and_syncs_bindings()
    {
        var s = Session(true, Attack());
        var tab = Tab(s);
        tab.Text.Text = "a + c";
        Assert.Equal("a + b", F(s, "f-att").Text);   // not yet
        Debounce(tab);
        Assert.Equal("a + c", F(s, "f-att").Text);
        Assert.Equal(["a", "c"], F(s, "f-att").Bindings.Select(b => b.Variable).ToList());   // b was never configured: dropped
        Assert.Equal(2, tab.Bindings.RowCount);
        Assert.Equal("c", tab.Bindings.Cell(1, 0));
    }

    [AvaloniaFact]
    public void Applies_to_syntax_problem_shown()
    {
        var s = Session(true, Attack());
        var tab = Tab(s);
        tab.Applies.Text = "Strength >";
        Assert.NotEqual("", tab.AppliesError);
        Assert.Equal(FormulaEdit.SyntaxProblem("Strength >"), tab.AppliesError);
        Debounce(tab);
        Assert.Equal("Strength >", F(s, "f-att").AppliesTo);   // stored as typed, the validator reports it
        tab.Applies.Text = "  ";
        Debounce(tab);
        Assert.Equal("", tab.AppliesError);
        Assert.Null(F(s, "f-att").AppliesTo);
    }

    [AvaloniaFact]
    public void Binding_kind_and_target_commit()
    {
        var s = Session(true, Attack());
        var tab = Tab(s);
        Pick(tab.Bindings, 0, 1, "Unit");
        Assert.Equal(BindingKind.Unit, B(s, "f-att", "a").Kind);
        Pick(tab.Bindings, 0, 2, "Moves");
        Assert.Equal("p-mov", B(s, "f-att", "a").Property);
        Assert.Equal("Moves", tab.Bindings.Cell(0, 2));
        Assert.True(tab.Bindings.CellBackground(0, 3) == EditGrid.ReadOnlyBrush);   // Unit has no details

        Pick(tab.Bindings, 1, 1, "Table");
        Pick(tab.Bindings, 1, 2, "Cost");
        Assert.Equal("t-cost", B(s, "f-att", "b").TableId);
        Pick(tab.Bindings, 0, 1, "Holder");
        Pick(tab.Bindings, 0, 2, "Depot: Strength");
        Assert.Equal(("h-depot", "p-str"), (B(s, "f-att", "a").HolderId, B(s, "f-att", "a").Property));
        Assert.Equal("A range such as 0..20, or choices separated by commas (put a choice that holds a comma in \"double quotes\"); add | auto: expression for the automatic value. Add | always ask to ask the player every time, even in Fully automatic.", Details(tab, BindingKind.Input));

        tab.Bindings.BeginEdit(0, 4, "Attack value");
        tab.Bindings.CommitEdit();
        Assert.Equal("Attack value", B(s, "f-att", "a").Caption);
    }

    static string Details(FormulasTab tab, BindingKind k)
    {
        // an Input row carries the hint; switch a row to it to read the tooltip
        var s = tab.Bindings;
        s.BeginEdit(1, 1);
        ((ComboBox)s.Editor!).SelectedItem = k.ToString();
        s.CommitEdit();
        Pump();
        return (string)s.CellToolTip(1, 3)!;
    }

    [AvaloniaFact]
    public void Workbench_piece_binding_details()
    {
        var s = Session(true, Attack());
        s.UpdateRules(p => p with { Properties = p.Properties.Append(new PropertyDef { Id = "p-tally", Name = "Tally", Scope = PropertyScope.Holders }).ToList() });
        var tab = Tab(s);
        Assert.Contains("Piece", tab.Bindings.CellItems(0, 1)!);
        Pick(tab.Bindings, 0, 1, "Piece");
        Assert.Equal(BindingKind.Piece, B(s, "f-att", "a").Kind);
        Assert.Equal(new[] { "Strength", "Moves" }, tab.Bindings.CellItems(0, 2));   // a holder property is never a piece's
        Pick(tab.Bindings, 0, 2, "Strength");
        Assert.Equal("p-str", B(s, "f-att", "a").Property);
        Assert.Equal("Strength", tab.Bindings.Cell(0, 2));
        Assert.False(tab.Bindings.CellBackground(0, 3) == EditGrid.ReadOnlyBrush);   // Piece has details: the "which pieces" filter
        Assert.Equal((object)FormulaEdit.PieceDetailsHint, tab.Bindings.CellToolTip(0, 3));

        Type(tab.Bindings, 0, 3, "startsWith(name(), \"Blue\")");
        Assert.Equal("startsWith(name(), \"Blue\")", B(s, "f-att", "a").PieceFilter);
        Assert.Equal("startsWith(name(), \"Blue\")", tab.Bindings.Cell(0, 3));

        Type(tab.Bindings, 0, 3, "name( ==");   // does not parse: refused, the old filter kept
        Assert.StartsWith("Which pieces: ", tab.ErrorLabel);
        Assert.Equal("startsWith(name(), \"Blue\")", B(s, "f-att", "a").PieceFilter);
        Assert.Equal("startsWith(name(), \"Blue\")", tab.Bindings.Cell(0, 3));

        Type(tab.Bindings, 0, 3, "  ");
        Assert.Null(B(s, "f-att", "a").PieceFilter);   // blank = every piece, stored as null
        Assert.Equal("p-str", B(s, "f-att", "a").Property);
    }

    [AvaloniaFact]
    public void Binding_details_parse_error_keeps_text()
    {
        var s = Session(true, Attack());
        var tab = Tab(s);
        Pick(tab.Bindings, 0, 1, "Roll");
        tab.Bindings.BeginEdit(0, 3, "2d6");
        tab.Bindings.CommitEdit();
        Pump();
        Assert.Equal((2, 6), (B(s, "f-att", "a").DiceCount, B(s, "f-att", "a").DiceSides));
        tab.Bindings.BeginEdit(0, 3, "banana");
        tab.Bindings.CommitEdit();
        Pump();
        Assert.Equal("Use dice notation like 2d6.", tab.ErrorLabel);
        Assert.Equal((2, 6), (B(s, "f-att", "a").DiceCount, B(s, "f-att", "a").DiceSides));   // the binding is unchanged
        Assert.Equal("2d6", tab.Bindings.Cell(0, 3));   // the grid shows what the pack holds
        Assert.Equal("a + b", tab.Text.Text);   // the formula text is untouched
        Assert.Equal("Dice, such as 2d6.", tab.Bindings.CellToolTip(0, 3));
    }

    [AvaloniaFact]
    public void Remove_unused_bindings()
    {
        var f = Attack() with { Text = "a", Bindings = new() { new() { Variable = "a", Kind = BindingKind.Input }, new() { Variable = "z", Kind = BindingKind.Global, Property = "p-str" } } };
        var s = Session(true, f);
        var tab = Tab(s);
        Assert.Equal("z (unused)", tab.Bindings.Cell(1, 0));
        Assert.Equal("a", tab.Bindings.Cell(0, 0));
        Click(tab.RemoveUnusedButton);
        Assert.Equal(["a"], F(s, "f-att").Bindings.Select(b => b.Variable).ToList());
        Assert.Equal(1, tab.Bindings.RowCount);
    }

    [AvaloniaFact]
    public void Piece_target_binding_is_not_unused()
    {
        var f = PieceFormula() with { Text = "a", Outputs = new() { new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "R", Action = OutputAction.Remove } } };
        var s = Session(true, f);
        var tab = Tab(s);
        Assert.Equal(["R", "a"], Enumerable.Range(0, tab.Bindings.RowCount).Select(r => tab.Bindings.Cell(r, 0)).ToList());   // used: no "(unused)"
        Click(tab.RemoveUnusedButton);
        Assert.Contains(F(s, "f-att").Bindings, b => b.Variable == "R");
    }

    [AvaloniaFact]
    public void Unused_question_asked_once_per_variable()
    {
        var f = Attack() with { Text = "a", Bindings = new() { new() { Variable = "a", Kind = BindingKind.Input }, new() { Variable = "z", Kind = BindingKind.Global, Property = "p-str" } } };
        var s = Session(true, f);
        var tab = Tab(s);
        Assert.Empty(_dialogs.Questions);   // opening asks nothing
        _dialogs.Answer = false;
        tab.Text.Focus();
        tab.Notes.Focus();   // leaving the formula box
        Pump();
        Assert.Single(_dialogs.Questions);
        Assert.Equal("Remove unused bindings", _dialogs.Questions[0].Caption);
        Assert.Contains("z are no longer in the formula", _dialogs.Questions[0].Message);
        Assert.True(_dialogs.YesNoDefaultNo[0]);
        Assert.Equal(2, F(s, "f-att").Bindings.Count);
        tab.Text.Focus();
        tab.Notes.Focus();
        Pump();
        Assert.Single(_dialogs.Questions);   // declined: not asked again

        // a second variable is asked about, and a yes removes the settings
        s.UpdateRules(p => p with { Formulas = p.Formulas.Select(x => x with { Bindings = x.Bindings.Append(new VariableBinding { Variable = "y", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 }).ToList() }).ToList() });
        Pump();
        _dialogs.Answer = true;
        tab.Text.Focus();
        tab.Notes.Focus();
        Pump();
        Assert.Equal(2, _dialogs.Questions.Count);
        Assert.Contains("y are no longer", _dialogs.Questions[1].Message);
        Assert.Equal(["a"], F(s, "f-att").Bindings.Select(b => b.Variable).ToList());
    }

    [AvaloniaFact]
    public void Outputs_add_remove_and_edit()
    {
        var s = Session(true, Attack());
        var tab = Tab(s);
        Click(tab.AddOutputButton);
        Click(tab.AddOutputButton);
        Assert.Equal(2, F(s, "f-att").Outputs.Count);
        Assert.Equal(2, tab.Outputs.RowCount);
        Pick(tab.Outputs, 0, 2, "Strength");
        Assert.Equal("p-str", F(s, "f-att").Outputs[0].Property);
        Pick(tab.Outputs, 0, 3, "Add");
        Assert.Equal(OutputOp.Add, F(s, "f-att").Outputs[0].Op);
        Pick(tab.Outputs, 0, 0, "Holder");
        Assert.Equal(OutputTarget.Holder, F(s, "f-att").Outputs[0].Target);
        Pick(tab.Outputs, 0, 1, "Depot");
        Assert.Equal("h-depot", F(s, "f-att").Outputs[0].HolderId);
        Pick(tab.Outputs, 0, 0, "Global");
        Assert.Null(F(s, "f-att").Outputs[0].HolderId);
        Assert.Equal(HolderDef.GlobalId, tab.Outputs.Cell(0, 1));
        tab.Outputs.SelectCell(1, 0);
        Click(tab.RemoveOutputButton);
        Assert.Single(F(s, "f-att").Outputs);
        Assert.Equal(OutputTarget.Global, F(s, "f-att").Outputs[0].Target);
    }

    [AvaloniaFact]
    public void Rule_ref_link_and_find_in_rulebook()
    {
        var s = Session(true, Attack());
        var r = new RuleRef { Id = "r1", Label = "Combat", DocumentEntry = "rules.pdf", Page = 4 };
        s.UpdateRules(p => p with { RuleRefs = p.RuleRefs.Append(r).ToList() });
        string? asked = null;
        RuleRef? shown = null;
        var tab = Tab(s, name => { asked = name; return r; }, x => shown = x);
        Assert.Equal("(none)", tab.RefRow.Text);
        Click(tab.RefRow.LinkButton);
        Assert.Equal("Attack", asked);
        Assert.Equal("r1", F(s, "f-att").RuleRefId);
        Assert.Equal("Combat, page 4", tab.RefRow.Text);
        tab.RefRow.ClickLink();
        Assert.Equal("r1", shown?.Id);
        var r2 = new RuleRef { Id = "r2", Label = "Other", DocumentEntry = "o.pdf", Page = 9 };
        s.UpdateRules(p => p with { RuleRefs = p.RuleRefs.Append(r2).ToList() });
        Assert.True(tab.CanUseRuleRef);
        Assert.True(tab.UseRuleRef(_ => r2));
        Assert.Equal("r2", F(s, "f-att").RuleRefId);
        Assert.False(tab.UseRuleRef(_ => null));

        Assert.False(tab.RefRow.FindButton.IsVisible);
        var queries = new List<string>();
        tab.FindInRulebook = queries.Add;
        Assert.True(tab.RefRow.FindButton.IsVisible);
        Click(tab.RefRow.FindButton);
        Assert.Equal(["Attack"], queries);
        tab.Text.SelectionStart = 0;
        tab.Text.SelectionEnd = 1;
        Click(tab.RefRow.FindButton);
        Assert.Equal("a", queries[^1]);
    }

    [AvaloniaFact]
    public void Edits_round_trip_through_qrules()
    {
        Directory.CreateDirectory(_tmp);
        var s = Session(true, Attack(), Defend());
        var tab = Tab(s);
        tab.Text.Text = "a * b + 1";
        tab.Applies.Text = "Strength > 1";
        tab.Notes.Text = "Notes here";
        tab.CommitPending();
        Pick(tab.Bindings, 0, 1, "Roll");
        tab.Bindings.BeginEdit(0, 3, "3d4");
        tab.Bindings.CommitEdit();
        Click(tab.AddOutputButton);
        Pick(tab.Outputs, 0, 2, "Moves");
        tab.CommitPending();
        var path = Path.Combine(_tmp, "t.qrules");
        RulesPackIo.Save(s.Rules!, path);
        var back = RulesPackIo.Load(path);
        Assert.Equal(4, back.Format);
        Assert.Equal(s.Rules!.Formulas.Count, back.Formulas.Count);
        for (var i = 0; i < back.Formulas.Count; i++)
        {
            var a = s.Rules.Formulas[i];
            var b = back.Formulas[i];
            Assert.Equal((a.Id, a.Name, a.Text, a.AppliesTo, a.Notes, a.RuleRefId), (b.Id, b.Name, b.Text, b.AppliesTo, b.Notes, b.RuleRefId));
            Assert.Equal(a.Bindings, b.Bindings);
            Assert.Equal(a.Outputs, b.Outputs);
        }
        var f = back.Formulas[0];
        Assert.Equal(("a * b + 1", "Strength > 1", "Notes here"), (f.Text, f.AppliesTo, f.Notes));
        Assert.Equal((3, 4), (f.Bindings[0].DiceCount, f.Bindings[0].DiceSides));
        Assert.Equal("p-mov", f.Outputs[0].Property);
    }

    [AvaloniaFact]
    public void Detaching_the_tab_flushes_typed_text()
    {
        var s = Session(true, Attack());
        var tab = Tab(s);
        var w = (Window)tab.Parent!;
        tab.Text.Text = "a - b";
        tab.Notes.Text = "typed quickly";
        w.Content = null;   // a hidden tab is detached
        Pump();
        Assert.Equal("a - b", F(s, "f-att").Text);
        Assert.Equal("typed quickly", F(s, "f-att").Notes);
        w.Content = tab;
        Pump();
        Assert.Equal("a - b", tab.Text.Text);
    }

    [AvaloniaFact]
    public void Opening_and_selecting_leaves_pack_untouched()
    {
        var f1 = Attack() with { Text = "a +\r\nb", Notes = "a\r\nb", AppliesTo = "Strength > 1 " };
        var s = Session(true, f1, Defend());
        var pack = s.Rules;
        var dirty = s.RulesDirty;
        var tab = Tab(s);
        tab.SelectFormula("f-def");
        Pump();
        tab.SelectFormula("f-att");
        Pump();
        tab.Text.Focus();
        tab.Notes.Focus();
        tab.Applies.Focus();
        tab.Text.Focus();
        Pump();
        tab.CommitPending();
        Debounce(tab);
        Assert.Same(pack, s.Rules);
        Assert.Equal(dirty, s.RulesDirty);
        Assert.Empty(_dialogs.Questions);
    }

    [AvaloniaFact]
    public void Refused_edit_in_guest_mode_redraws_from_the_pack()
    {
        var s = Session(true, Attack());
        var tab = Tab(s);
        s.EnterGuestMode();
        tab.Text.Text = "a - b";
        tab.CommitPending();
        Pump();
        Assert.Equal("a + b", tab.Text.Text);
        Assert.Equal("a + b", F(s, "f-att").Text);
        Assert.Contains(s.Log.Items, e => e.Message.StartsWith("Formula edit failed"));
    }

    [AvaloniaFact]
    public void Tab_without_a_pack_is_empty_and_disabled()
    {
        var tab = Tab(Session(false));
        Assert.Null(tab.SelectedFormulaId);
        Assert.Equal(0, tab.Bindings.RowCount);
        Assert.False(tab.Text.IsEnabled);
        Assert.False(tab.AddOutputButton.IsEnabled);
    }

    [AvaloniaFact]
    public void Rename_answer_is_dropped_when_the_formula_changed_meanwhile()
    {
        var s = Session(true, Attack(), Defend());
        var tab = Tab(s);
        var gate = new TaskCompletionSource<string?>();
        _dialogs.TextGate = gate;
        Click(tab.RenameButton);   // waiting on "Attack"
        s.UpdateRules(p => p with { Formulas = p.Formulas.Where(x => x.Id != "f-att").ToList() });
        Pump();
        gate.SetResult("Zed");
        Pump();
        Assert.Equal(["Defend"], s.Rules!.Formulas.Select(x => x.Name).ToList());
    }

    [AvaloniaFact]
    public void Test_piece_lists_matching_pieces()
    {
        var s = SessionWith(true, true, Attack());
        var tab = Tab(s);
        var names = ((IEnumerable<string>)tab.Preview.PieceBox.ItemsSource!).ToList();
        Assert.Equal("(no unit)", names[0]);
        Assert.Equal(1 + s.View!.Pieces.Count, names.Count);
        Assert.Contains(names, n => n.StartsWith("Red 1 ("));
        Assert.Equal(0, tab.Preview.PieceBox.SelectedIndex);
        Assert.Null(tab.Preview.PieceId);
        tab.Preview.PieceBox.SelectedIndex = names.FindIndex(n => n.StartsWith("Red 1 ("));
        Pump();
        Assert.Equal(PhaseFixture.RedOne, tab.Preview.PieceId);
        tab.Preview.PieceBox.SelectedIndex = 0;
        Pump();
        Assert.Null(tab.Preview.PieceId);
    }

    [AvaloniaFact]
    public void Preview_updates_after_text_change()
    {
        var s = Session(true, Attack());
        var tab = Tab(s);
        Assert.True(tab.Preview.HasContent);
        Assert.NotNull(tab.Preview.BoxText("a"));
        Assert.Null(tab.Preview.BoxText("c"));
        tab.Text.Text = "a + c";
        Debounce(tab);
        Pump();
        Assert.NotNull(tab.Preview.BoxText("c"));
        tab.Preview.Type("a", "2");
        tab.Preview.Type("c", "3");
        Pump();
        tab.Preview.FireDebounce();
        Assert.Equal("5", tab.Preview.ResultText);
        tab.SelectFormula("nope");
        tab.SelectFormula("f-att");
        Assert.Equal("f-att", tab.Preview.FormulaId);
    }

    [AvaloniaFact]
    public void Preview_never_changes_pack_state_or_log()
    {
        var s = SessionWith(true, true, Attack() with { Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "p-str", Op = OutputOp.Set } } });
        var tab = Tab(s);
        var pack = s.Rules;
        var dirty = s.RulesDirty;
        var commands = s.Game!.Log.Count;
        var logged = s.Log.Items.Count;
        var state = s.Game.State;
        tab.Preview.PieceBox.SelectedIndex = 1;
        Pump();
        tab.Preview.Type("a", "2");
        tab.Preview.Type("b", "3");
        Pump();
        tab.Preview.FireDebounce();
        Assert.Equal("5", tab.Preview.ResultText);
        tab.SelectFormula("f-att");
        Pump();
        Assert.Same(pack, s.Rules);
        Assert.Equal(dirty, s.RulesDirty);
        Assert.Equal(commands, s.Game.Log.Count);
        Assert.Equal(logged, s.Log.Items.Count);
        Assert.Same(state, s.Game.State);
        Assert.False(s.Game.CanUndo);
    }

    [AvaloniaFact]
    public void External_change_while_editing_drops_stale_edit()
    {
        var s = Session(true, Attack(), Defend());
        var tab = Tab(s);
        tab.Bindings.BeginEdit(0, 4, "stale");
        Assert.True(tab.Bindings.IsEditing);
        s.UpdateRules(p => p with { Formulas = p.Formulas.Select(f => f.Id == "f-att" ? f with { Text = "a * b" } : f).ToList() });
        Pump();
        Assert.False(tab.Bindings.IsEditing);
        Assert.Equal("a * b", tab.Text.Text);
        Assert.Null(B(s, "f-att", "a").Caption);

        tab.Bindings.BeginEdit(0, 4, "stale2");
        s.UpdateRules(p => p with { Formulas = p.Formulas.Where(f => f.Id != "f-att").ToList() });
        Pump();
        Assert.False(tab.Bindings.IsEditing);
        Assert.Equal("f-def", tab.SelectedFormulaId);
        Assert.Equal(Defend().Bindings, F(s, "f-def").Bindings);
        Assert.Null(F(s, "f-def").Bindings[0].Caption);
    }
    [AvaloniaFact]
    public void FormulasTab_holder_property_items_filtered()
    {
        var f = new FormulaDef
        {
            Id = "f-toll", Name = "Toll", Text = "a + b",
            Bindings = new() { new() { Variable = "a", Kind = BindingKind.Holder }, new() { Variable = "b", Kind = BindingKind.Global } },
            Outputs = new()
            {
                new FormulaOutput { Target = OutputTarget.Holder, HolderId = "h-yard", Property = "" },
                new FormulaOutput { Target = OutputTarget.Global, Property = "" },
                new FormulaOutput { Target = OutputTarget.Unit, Property = "" },
            },
        };
        var s = Session(true, f);
        s.UpdateRules(p => p with
        {
            Properties = new()
            {
                new PropertyDef { Id = "p-toll", Name = "Tolls", Scope = PropertyScope.Holders, Holders = new() { "h-depot" } },
                new PropertyDef { Id = "p-wx", Name = "Weather", Scope = PropertyScope.Holders, Holders = new() { HolderDef.GlobalId } },
                new PropertyDef { Id = "p-sto", Name = "Stores", Scope = PropertyScope.Holders },
            },
            Holders = new() { new HolderDef { Id = "h-depot", Name = "Depot" }, new HolderDef { Id = "h-yard", Name = "Yard" } },
        });
        var tab = Tab(s);
        tab.SelectFormula("f-toll");
        Pump();
        // Holder binding: each holder with only the properties it keeps.
        Assert.Equal(["Depot: Tolls", "Depot: Stores", "Yard: Stores"], tab.Bindings.CellItems(0, 2)!.ToList());
        // Global binding: only what the global holder keeps.
        Assert.Equal(["Weather", "Stores"], tab.Bindings.CellItems(1, 2)!.ToList());
        // Outputs: the Yard keeps Stores only; Global keeps Weather and Stores; a unit output lists every property.
        Assert.Equal(["Stores"], tab.Outputs.CellItems(0, 2)!.ToList());
        Assert.Equal(["Weather", "Stores"], tab.Outputs.CellItems(1, 2)!.ToList());
        Assert.Equal(["Tolls", "Weather", "Stores"], tab.Outputs.CellItems(2, 2)!.ToList());
        // Choosing another holder offers its properties.
        Pick(tab.Outputs, 0, 1, "Depot");
        Assert.Equal(["Tolls", "Stores"], tab.Outputs.CellItems(0, 2)!.ToList());
    }

    static FormulaDef ChoiceFormula() => Attack() with
    {
        Bindings = new() { new VariableBinding { Variable = "a", Kind = BindingKind.Input }, new VariableBinding { Variable = "R", Kind = BindingKind.Piece, Property = "p-str" } },
    };

    [AvaloniaFact]
    public void FormulasTab_group_roll_checkbox_commits()
    {
        var s = Session(true, Attack(), Defend());
        var tab = Tab(s);
        Assert.NotEqual(true, tab.GroupRollCheck.IsChecked);
        Assert.Equal("Roll once for the group", (string)tab.GroupRollCheck.Content!);
        Assert.False(F(s, "f-att").GroupRoll);
        tab.GroupRollCheck.IsChecked = true;
        Pump();
        Assert.True(F(s, "f-att").GroupRoll);
        Assert.False(F(s, "f-def").GroupRoll);
        tab.SelectFormula("f-def");
        Pump();
        Assert.NotEqual(true, tab.GroupRollCheck.IsChecked);
        tab.SelectFormula("f-att");
        Pump();
        Assert.Equal(true, tab.GroupRollCheck.IsChecked);
        tab.GroupRollCheck.IsChecked = false;
        Pump();
        Assert.False(F(s, "f-att").GroupRoll);
    }

    [AvaloniaFact]
    public void FormulasTab_choices_columns_commit()
    {
        var s = Session(true, ChoiceFormula());
        var tab = Tab(s);
        Assert.Equal("Choices table", tab.Bindings.HeaderText(8));
        Assert.Equal("Choices filter", tab.Bindings.HeaderText(9));
        Assert.True(tab.Bindings.IsCellReadOnly(1, 8));   // a Piece binding has no choices table
        Assert.True(tab.Bindings.IsCellReadOnly(0, 9));   // no table yet: no filter
        Pick(tab.Bindings, 0, 8, "Cost");
        Assert.Equal("t-cost", B(s, "f-att", "a").ChoicesTable);
        Assert.False(tab.Bindings.IsCellReadOnly(0, 9));
        Type(tab.Bindings, 0, 9, " row() != \"x\" ");
        Assert.Equal("row() != \"x\"", B(s, "f-att", "a").ChoicesFilter);
        Type(tab.Bindings, 0, 9, "row() !=");   // a syntax error is refused
        Assert.Equal("row() != \"x\"", B(s, "f-att", "a").ChoicesFilter);
        Type(tab.Bindings, 0, 9, "");
        Assert.Null(B(s, "f-att", "a").ChoicesFilter);
    }

    [AvaloniaFact]
    public void FormulasTab_old_binding_columns_keep_places()
    {
        var s = Session(true, PieceFormula());
        var tab = Tab(s);
        string[] old = ["Variable", "Kind", "Target", "Details", "Caption", "Only when", "Dice count", "Same hex"];
        for (var i = 0; i < old.Length; i++) Assert.Equal(old[i], tab.Bindings.HeaderText(i));
    }

    [AvaloniaFact]
    public void FormulasTab_clear_choices_table_clears_filter()
    {
        var f = Attack() with { Bindings = new() { new VariableBinding { Variable = "a", Kind = BindingKind.Input, ChoicesTable = "t-cost", ChoicesFilter = "row() != \"x\"" }, new VariableBinding { Variable = "b", Kind = BindingKind.Input } } };
        var s = Session(true, f);
        var tab = Tab(s);
        Assert.Equal("Cost", tab.Bindings.Cell(0, 8));
        Pick(tab.Bindings, 0, 8, "");
        Assert.Null(B(s, "f-att", "a").ChoicesTable);
        Assert.Null(B(s, "f-att", "a").ChoicesFilter);
    }
}
