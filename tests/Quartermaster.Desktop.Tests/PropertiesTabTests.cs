using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4a task 9: the workbench Properties &amp; Holders tab. Headless, fake dialogs, invented properties.</summary>
[Collection("MacFlag")]
public sealed class PropertiesTabTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-propstab-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Properties Fuel (sheet field Fuel) and Water, formulas using them, holders Depot and Yard.</summary>
    ModuleSession Session(Func<RulesPack, RulesPack>? tweak = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        s.NewRules("Invented");
        var fp = PhaseFixture.Pack("x");
        s.UpdateRules(p =>
        {
            p = p with
            {
                Properties = fp.Properties, Formulas = fp.Formulas,
                Holders = new() { new HolderDef { Id = "depot", Name = "Depot", Notes = "Main stock" }, new HolderDef { Id = "yard", Name = "Yard" } },
            };
            return tweak?.Invoke(p) ?? p;
        });
        return s;
    }

    PropertiesTab Tab(ModuleSession s)
    {
        var tab = new PropertiesTab(s, _dialogs);
        var w = new Window { Width = 1000, Height = 700, Content = tab };
        _windows.Add(w);
        w.Show();
        Pump();
        return tab;
    }

    static void Type(EditGrid g, int row, int col, string text)
    {
        g.BeginEdit(row, col, text);
        g.CommitEdit();
        Pump();
    }

    [AvaloniaFact]
    public void Lists_properties_and_holders_with_global_first()
    {
        var s = Session();
        var tab = Tab(s);
        Assert.Equal(2, tab.PropertyGrid.RowCount);
        Assert.Equal("Fuel", tab.PropertyGrid.Cell(0, 0));
        Assert.Equal("Pieces", tab.PropertyGrid.Cell(0, 1));
        Assert.Equal("Fuel", tab.PropertyGrid.Cell(0, 3));
        Assert.Equal("10", tab.PropertyGrid.Cell(1, 2));
        Assert.Equal(3, tab.HolderGrid.RowCount);
        Assert.Equal(HolderDef.GlobalId, tab.HolderGrid.Cell(0, 0));
        Assert.Equal("Built in; always exists.", tab.HolderGrid.Cell(0, 1));
        Assert.Equal("Main stock", tab.HolderGrid.Cell(1, 1));
        tab.HolderGrid.BeginEdit(0, 0);
        Assert.False(tab.HolderGrid.IsEditing);   // the built-in row is read-only
        Assert.Same(EditGrid.ReadOnlyBrush, tab.HolderGrid.CellBackground(0, 1));   // (0,0) is the current cell, drawn highlighted
        Assert.NotNull(tab.PropertyGrid.CellToolTip(0, 3));
        Assert.NotNull(tab.PropertyGrid.CellToolTip(0, 5));
        Assert.NotNull(tab.PropertyGrid.HeaderToolTip(3));
        Assert.NotNull(tab.PropertyGrid.HeaderToolTip(5));
    }

    [AvaloniaFact]
    public void Add_property_unique_name()
    {
        var s = Session();
        var tab = Tab(s);
        Click(tab.AddPropertyButton);
        Assert.Equal("Property 3", s.Rules!.Properties[^1].Name);
        Assert.Equal(PropertyScope.Pieces, s.Rules.Properties[^1].Scope);
        Assert.Equal(32, s.Rules.Properties[^1].Id.Length);
        Assert.Equal((2, 0), tab.PropertyGrid.CurrentCell);
        Click(tab.AddPropertyButton);
        Assert.Equal("Property 4", s.Rules.Properties[^1].Name);
        Assert.Equal(4, tab.PropertyGrid.RowCount);
        Click(tab.AddHolderButton);
        Assert.Equal("Holder 3", s.Rules.Holders[^1].Name);
        Assert.Equal((3, 0), tab.HolderGrid.CurrentCell);
        Assert.Equal(4, tab.HolderGrid.RowCount);
    }

    [AvaloniaFact]
    public void Property_cells_commit()
    {
        var s = Session();
        var tab = Tab(s);
        var g = tab.PropertyGrid;
        Type(g, 1, 0, "Aqua");
        Type(g, 1, 2, "12.5");
        Type(g, 1, 6, "Drinking water");
        g.BeginEdit(1, 1);
        ((ComboBox)g.Editor!).SelectedItem = "Holders";
        g.CommitEdit();
        g.BeginEdit(1, 5);
        ((ComboBox)g.Editor!).SelectedItem = nameof(OutlinerAggregate.Sum);
        g.CommitEdit();
        Pump();
        var w = s.Rules!.Properties[1];
        Assert.Equal("Aqua", w.Name);
        Assert.Equal(12.5, w.Default);
        Assert.Equal("Drinking water", w.Notes);
        Assert.Equal(PropertyScope.Holders, w.Scope);
        Assert.Equal(OutlinerAggregate.Sum, w.Outliner);
        Assert.Equal("water", w.Id);
        g.BeginEdit(0, 3);   // sheet field: an empty choice first, the current value kept in the list
        var items = g.EditorList!.Items.Cast<string>().ToList();
        Assert.Equal("", items[0]);
        Assert.Contains("Fuel", items);
        g.CancelEdit();
        Type(g, 1, 6, "");
        Assert.Null(s.Rules.Properties[1].Notes);
    }

    [AvaloniaFact]
    public void Bad_default_is_refused_and_redrawn()
    {
        var s = Session();
        var tab = Tab(s);
        Type(tab.PropertyGrid, 1, 2, "ten");
        Assert.Equal(10, s.Rules!.Properties[1].Default);
        Assert.Equal("10", tab.PropertyGrid.Cell(1, 2));
        Assert.Equal("The default must be a number (use a dot for decimals).", tab.Status.Text);
        Type(tab.PropertyGrid, 1, 0, "fuel");   // duplicate of Fuel, case-insensitive
        Assert.Equal("Water", s.Rules.Properties[1].Name);
        Assert.Equal("Water", tab.PropertyGrid.Cell(1, 0));
        Assert.EndsWith("The name was not changed.", tab.Status.Text);
    }

    [AvaloniaFact]
    public void PropertiesTab_expression_column_greys_default()
    {
        var s = Session();
        var tab = Tab(s);
        var g = tab.PropertyGrid;
        var ex = PropertiesTab.ExpressionColumn;
        Assert.Equal("", g.Cell(1, ex));
        Assert.False(g.IsCellReadOnly(1, 2));
        Assert.False(g.IsCellReadOnly(1, 3));

        Type(g, 1, ex, "Fuel * 2");
        Assert.Equal("Fuel * 2", s.Rules!.Properties[1].Expr);
        Assert.Equal("Fuel * 2", g.Cell(1, ex));
        Assert.True(g.IsCellReadOnly(1, 2));   // Default and Sheet field are ignored while it is computed
        Assert.True(g.IsCellReadOnly(1, 3));
        Assert.Same(EditGrid.ReadOnlyBrush, g.CellBackground(1, 2));
        Assert.Equal(PropertiesTab.ComputedIgnoredTip, g.CellToolTip(1, 2));
        Assert.False(g.IsCellReadOnly(0, 2));   // the other property is still stored

        Type(g, 1, ex, "Fuel *");   // kept, flagged
        var problem = FormulaEdit.SyntaxProblem("Fuel *");
        Assert.NotNull(problem);
        Assert.Equal("Fuel *", s.Rules.Properties[1].Expr);
        Assert.Same(EditGrid.ErrorBrush, g.CellBackground(1, ex));
        Assert.Equal("Expression: " + problem, tab.Status.Text);

        Type(g, 1, ex, "  ");   // cleared: stored again
        Assert.Null(s.Rules.Properties[1].Expr);
        Assert.False(g.IsCellReadOnly(1, 2));
        Assert.False(g.IsCellReadOnly(1, 3));
    }

    [AvaloniaFact]
    public void PropertiesTab_override_pickers()
    {
        var s = Session(p => p with
        {
            Tables = new()
            {
                new TableDef { Id = "tu", Name = "Rafts", Kind = TableKind.TwoD, RowKeys = new() { "Raft A" }, ColumnKeys = new() { "Speed", "Load" }, Cells = new() { new() { "3", "4" } } },
                new TableDef { Id = "tc", Name = "Costs", Kind = TableKind.OneD, RowKeys = new() { "Raft A" }, ColumnKeys = new() { "Cost" }, Cells = new() { new() { "5" } } },
            },
        });
        var tab = Tab(s);
        var g = tab.PropertyGrid;
        var (tc, cc) = (PropertiesTab.OverridesTableColumn, PropertiesTab.OverridesColumnColumn);
        Assert.Equal("", g.Cell(1, tc));
        Assert.True(g.IsCellReadOnly(1, cc));   // no table: no column to pick
        Assert.NotNull(g.HeaderToolTip(tc));
        Assert.NotNull(g.HeaderToolTip(cc));

        g.BeginEdit(1, tc);
        Assert.Equal(new[] { "", "Rafts", "Costs" }, ((ComboBox)g.Editor!).ItemsSource!.Cast<string>());
        ((ComboBox)g.Editor!).SelectedItem = "Rafts";
        g.CommitEdit();
        Pump();
        Assert.Equal("tu", s.Rules!.Properties[1].OverridesTable);
        Assert.Null(s.Rules.Properties[1].OverridesColumn);
        Assert.Equal("Rafts", g.Cell(1, tc));
        Assert.False(g.IsCellReadOnly(1, cc));
        g.BeginEdit(1, cc);
        Assert.Equal(new[] { "", "Speed", "Load" }, ((ComboBox)g.Editor!).ItemsSource!.Cast<string>());
        ((ComboBox)g.Editor!).SelectedItem = "Speed";
        g.CommitEdit();
        Pump();
        Assert.Equal("Speed", s.Rules.Properties[1].OverridesColumn);
        Assert.Equal("Speed", g.Cell(1, cc));

        g.BeginEdit(1, tc);   // a one-dimension table: no column
        ((ComboBox)g.Editor!).SelectedItem = "Costs";
        g.CommitEdit();
        Pump();
        Assert.Equal("tc", s.Rules.Properties[1].OverridesTable);
        Assert.Null(s.Rules.Properties[1].OverridesColumn);
        Assert.Equal("", g.Cell(1, cc));
        Assert.True(g.IsCellReadOnly(1, cc));

        g.BeginEdit(1, tc);   // blank clears both
        ((ComboBox)g.Editor!).SelectedItem = "";
        g.CommitEdit();
        Pump();
        Assert.Null(s.Rules.Properties[1].OverridesTable);
        Assert.Null(s.Rules.Properties[1].OverridesColumn);
        Assert.Null(s.Rules.Properties[0].OverridesTable);   // the other row untouched
    }

    [AvaloniaFact]
    public void Applies_to_error_cell_pink_with_tooltip()
    {
        var s = Session();
        var tab = Tab(s);
        var g = tab.PropertyGrid;
        Assert.NotSame(EditGrid.ErrorBrush, g.CellBackground(0, 4));
        Type(g, 0, 4, "Fuel <");
        var problem = FormulaEdit.SyntaxProblem("Fuel <");
        Assert.NotNull(problem);
        Assert.Equal("Fuel <", s.Rules!.Properties[0].AppliesTo);   // the text is kept; only flagged
        Assert.Same(EditGrid.ErrorBrush, g.CellBackground(0, 4));
        Assert.Equal(problem, g.CellToolTip(0, 4));
        Assert.Equal("Applies to: " + problem, tab.Status.Text);
        Type(g, 0, 4, "startsWith(name(), \"Red\")");
        Assert.NotSame(EditGrid.ErrorBrush, g.CellBackground(0, 4));
        Assert.Equal("", tab.Status.Text);

        var bad = Session(p => p with { Properties = p.Properties.Select(x => x.Id == "water" ? x with { AppliesTo = "((" } : x).ToList() });
        var tab2 = Tab(bad);
        Assert.Same(EditGrid.ErrorBrush, tab2.PropertyGrid.CellBackground(1, 4));   // flagged when loaded, too
    }

    [AvaloniaFact]
    public void Holder_name_rule_refused_keeps_old()
    {
        var s = Session();
        var tab = Tab(s);
        var g = tab.HolderGrid;
        Type(g, 1, 0, "yard");   // duplicate of Yard
        Assert.Equal("Depot", s.Rules!.Holders[0].Name);
        Assert.Equal("Depot", g.Cell(1, 0));
        Assert.Contains("There is already an item named", tab.Status.Text);
        Assert.EndsWith("The name was not changed.", tab.Status.Text);
        Type(g, 1, 0, "global");   // the built-in holder's name is taken too
        Assert.Equal("Depot", s.Rules.Holders[0].Name);
        Type(g, 1, 0, "  ");
        Assert.Equal("Depot", s.Rules.Holders[0].Name);
        Assert.Contains("cannot be empty", tab.Status.Text);
        Type(g, 1, 0, "Store");
        Assert.Equal("Store", s.Rules.Holders[0].Name);
        Type(g, 1, 1, "");
        Assert.Null(s.Rules.Holders[0].Notes);
        Assert.Equal("", tab.Status.Text);
    }

    [AvaloniaFact]
    public void Delete_leaves_the_id_in_a_choice_list_and_the_validator_reports_it()
    {
        var s = Session(p => p with
        {
            Formulas = p.Formulas.Append(new FormulaDef
            {
                Id = "f-pick", Name = "Pick supply", Text = "1",
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, PropertyChoice = new() { "fuel", "water" }, PropertySelect = "2" } },
            }).ToList(),
        });
        var tab = Tab(s);
        tab.SelectProperty("fuel");
        Pump();
        _dialogs.Answer = true;
        Click(tab.DeletePropertyButton);
        Assert.DoesNotContain(s.Rules!.Properties, p => p.Id == "fuel");
        // the list is not renumbered: a number pick would otherwise silently change which property it writes
        Assert.Equal(new[] { "fuel", "water" }, s.Rules.Formulas.Single(f => f.Id == "f-pick").Outputs[0].PropertyChoice);
        Assert.Contains(RulesValidator.Validate(s.Rules), i => i.Message == "Formula \"Pick supply\": output 1 refers to a property that no longer exists");
    }

    [AvaloniaFact]
    public void Delete_asks_with_formula_users_default_no()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectProperty("water");
        Pump();
        var before = s.Rules!.Properties.Count;
        var expected = WorkbenchRules.DeleteQuestion("property", "Water", FormulaEdit.FormulasUsingProperty(s.Rules, "water"));
        Assert.Contains("Daily water", expected);
        _dialogs.Answer = false;
        Click(tab.DeletePropertyButton);
        Assert.Equal(before, s.Rules.Properties.Count);
        Assert.Equal(expected, _dialogs.Questions[^1].Message);
        Assert.Equal("Delete property", _dialogs.Questions[^1].Caption);
        _dialogs.Answer = true;
        Click(tab.DeletePropertyButton);
        Assert.DoesNotContain(s.Rules.Properties, p => p.Id == "water");
        Assert.Equal(before - 1, tab.PropertyGrid.RowCount);

        tab.SelectHolder("yard");
        Pump();
        Click(tab.DeleteHolderButton);
        Assert.Equal("Delete the holder \"Yard\"?", _dialogs.Questions[^1].Message);
        Assert.Equal("Delete holder", _dialogs.Questions[^1].Caption);
        Assert.DoesNotContain(s.Rules.Holders, h => h.Id == "yard");
        Assert.All(_dialogs.YesNoDefaultNo, d => Assert.True(d));
        Assert.Equal(3, _dialogs.Questions.Count);
    }

    [AvaloniaFact]
    public void Global_row_cannot_be_deleted()
    {
        var s = Session();
        var tab = Tab(s);
        tab.HolderGrid.SelectCell(0, 0);
        _dialogs.Answer = true;
        Click(tab.DeleteHolderButton);
        Assert.Empty(_dialogs.Questions);
        Assert.Equal(2, s.Rules!.Holders.Count);
    }

    [AvaloniaFact]
    public void Select_property_and_holder()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectProperty("water");
        Assert.Equal((1, 0), tab.PropertyGrid.CurrentCell);
        tab.SelectHolder("yard");
        Assert.Equal((2, 0), tab.HolderGrid.CurrentCell);   // row 0 is the global holder
        tab.SelectHolder("nope");
        tab.SelectProperty("nope");
        Assert.Equal((2, 0), tab.HolderGrid.CurrentCell);
        Assert.Equal((1, 0), tab.PropertyGrid.CurrentCell);
    }

    [AvaloniaFact]
    public void Edits_round_trip_through_qrules()
    {
        Directory.CreateDirectory(_tmp);
        var s = Session();
        var tab = Tab(s);
        Click(tab.AddPropertyButton);
        Type(tab.PropertyGrid, 2, 0, "Morale");
        Type(tab.PropertyGrid, 2, 2, "0.25");
        Type(tab.PropertyGrid, 2, 4, "Fuel > 3");
        Type(tab.PropertyGrid, 2, 6, "n");
        Click(tab.AddHolderButton);
        tab.HolderGrid.BeginEdit(3, 0, "Dump");   // left open: CommitPending takes it
        tab.CommitPending();
        Pump();
        var path = Path.Combine(_tmp, "p.qrules");
        RulesPackIo.Save(s.Rules!, path);
        var back = RulesPackIo.Load(path);
        Assert.Equal(4, back.Format);
        // the file stores entries in id order
        Assert.Equal(s.Rules!.Properties.Select(p => (p.Id, p.Name, p.Scope, p.Default, p.SheetField, p.AppliesTo, p.Outliner, p.Notes)).OrderBy(x => x.Id, StringComparer.Ordinal),
            back.Properties.Select(p => (p.Id, p.Name, p.Scope, p.Default, p.SheetField, p.AppliesTo, p.Outliner, p.Notes)).OrderBy(x => x.Id, StringComparer.Ordinal));
        Assert.Equal(s.Rules.Holders.Select(h => (h.Id, h.Name, h.Notes)).OrderBy(x => x.Id, StringComparer.Ordinal), back.Holders.Select(h => (h.Id, h.Name, h.Notes)).OrderBy(x => x.Id, StringComparer.Ordinal));
        var morale = back.Properties.Single(p => p.Name == "Morale");
        Assert.Equal(0.25, morale.Default);
        Assert.Equal("Fuel > 3", morale.AppliesTo);
        Assert.Equal("n", morale.Notes);
        Assert.Contains(back.Holders, h => h.Name == "Dump");
    }

    [AvaloniaFact]
    public void Opening_and_selecting_leaves_pack_untouched()
    {
        var s = Session();
        var pack = s.Rules;
        var dirty = s.RulesDirty;
        var tab = Tab(s);
        tab.SelectProperty("water");
        tab.SelectHolder("yard");
        tab.PropertyGrid.BeginEdit(0, 0);
        tab.CommitPending();   // an edit opened and left as it was
        tab.PropertyGrid.BeginEdit(0, 3);
        tab.CommitPending();
        Pump();
        Assert.Same(pack, s.Rules);
        Assert.Equal(dirty, s.RulesDirty);
    }

    [AvaloniaFact]
    public void Refused_edit_in_guest_mode_redraws_from_the_pack()
    {
        var s = Session();
        var tab = Tab(s);
        s.EnterGuestMode();
        Type(tab.PropertyGrid, 0, 0, "Zed");
        Assert.Equal("Fuel", s.Rules!.Properties[0].Name);
        Assert.Equal("Fuel", tab.PropertyGrid.Cell(0, 0));
        Assert.Contains(s.Log.Items, e => e.Message.StartsWith("Property edit failed"));
        Type(tab.HolderGrid, 1, 0, "Zed");
        Assert.Equal("Depot", tab.HolderGrid.Cell(1, 0));
    }

    [AvaloniaFact]
    public void Detaching_flushes_an_open_cell_edit()
    {
        var s = Session();
        var tab = Tab(s);
        tab.PropertyGrid.BeginEdit(1, 6, "typed");
        _windows[0].Content = null;
        Pump();
        Assert.Equal("typed", s.Rules!.Properties[1].Notes);
    }

    [AvaloniaFact]
    public void External_change_rebuilds()
    {
        var s = Session();
        var tab = Tab(s);
        s.UpdateRules(p => p with { Properties = p.Properties.Select(x => x.Id == "fuel" ? x with { Name = "Petrol" } : x).ToList() });
        Pump();
        Assert.Equal("Petrol", tab.PropertyGrid.Cell(0, 0));
    }

    [AvaloniaFact]
    public void SheetField_free_text_when_no_sheet_rows()
    {
        var s = Session();
        var tab = Tab(s);
        tab.PropertyGrid.BeginEdit(1, 3, "Brand new field");   // not offered by any piece
        Assert.NotNull(tab.PropertyGrid.EditorText);
        tab.PropertyGrid.CommitEdit();
        Pump();
        Assert.Equal("Brand new field", s.Rules!.Properties[1].SheetField);
        Type(tab.PropertyGrid, 1, 3, "");
        Assert.Null(s.Rules.Properties[1].SheetField);
    }

    /// <summary>Forwards everything to the fake dialogs but makes the Yes/No question wait for a gate.</summary>
    public class GatedDialogs : System.Reflection.DispatchProxy
    {
        public object Inner = null!;
        public TaskCompletionSource<bool>? Gate;
        protected override object? Invoke(System.Reflection.MethodInfo? m, object?[]? args)
        {
            if (m!.Name == "AskYesNoAsync" && Gate is { } g) return g.Task;
            return m.Invoke(Inner, args);
        }
    }

    [AvaloniaFact]
    public void Delete_answer_is_dropped_when_the_row_changed_meanwhile()
    {
        var s = Session();
        var proxy = System.Reflection.DispatchProxy.Create<Quartermaster.Desktop.Shell.IDialogs, GatedDialogs>();
        var gated = (GatedDialogs)(object)proxy;
        gated.Inner = _dialogs;
        gated.Gate = new();
        var tab = new PropertiesTab(s, proxy);
        var w = new Window { Width = 1000, Height = 700, Content = tab };
        _windows.Add(w);
        w.Show();
        Pump();
        tab.SelectProperty("water");
        Pump();
        Click(tab.DeletePropertyButton);   // waiting
        tab.SelectProperty("fuel");        // another row is current now
        Pump();
        gated.Gate.SetResult(true);
        Pump();
        Assert.Equal(2, s.Rules!.Properties.Count);

        gated.Gate = new();
        tab.SelectProperty("water");
        Pump();
        Click(tab.DeletePropertyButton);
        s.UpdateRules(p => p with { Properties = p.Properties.Where(x => x.Id != "water").ToList() });   // deleted elsewhere
        Pump();
        gated.Gate.SetResult(true);
        Pump();
        Assert.Equal(["fuel"], s.Rules.Properties.Select(x => x.Id).ToList());
        Assert.DoesNotContain(s.Log.Items, e => e.Message.StartsWith("Property edit failed"));
    }
    static void Pick(EditGrid g, int row, int col, string item)
    {
        g.BeginEdit(row, col);
        ((ComboBox)g.Editor!).SelectedItem = item;
        g.CommitEdit();
        Pump();
    }

    /// <summary>Rule refs "Ferry rules" p.4 and "Weather rules" p.7; tables "Weather names" (1-D) and "Grid" (2-D); a holder property Tolls.</summary>
    static RulesPack B1(RulesPack p) => p with
    {
        RuleRefs = new() { new RuleRef { Id = "r-ferry", Label = "Ferry rules", Page = 4 }, new RuleRef { Id = "r-wx", Label = "Weather rules", Page = 7 } },
        Tables = new()
        {
            new TableDef { Id = "t-wx", Name = "Weather names", Kind = TableKind.OneD, RowKeys = new() { "1" }, ColumnKeys = new() { "Value" }, Cells = new() { new() { "Clear" } } },
            new TableDef { Id = "t-grid", Name = "Grid", Kind = TableKind.TwoD, RowKeys = new() { "1" }, ColumnKeys = new() { "a", "b" }, Cells = new() { new() { "1", "2" } } },
        },
        Properties = p.Properties.Append(new PropertyDef { Id = "tolls", Name = "Tolls", Scope = PropertyScope.Holders }).ToList(),
    };

    [AvaloniaFact]
    public void PropertiesTab_new_columns_commit()
    {
        var s = Session(B1);
        var tab = Tab(s);
        var g = tab.PropertyGrid;
        Assert.Equal("Rule ref", g.HeaderText(PropertiesTab.RuleRefColumn));
        Assert.Equal("Labels", g.HeaderText(PropertiesTab.LabelsColumn));
        Assert.Equal("Holders", g.HeaderText(PropertiesTab.HoldersColumn));
        Assert.Equal("Clone as", g.HeaderText(PropertiesTab.CloneAsColumn));
        Assert.Equal(PropertiesTab.LabelsTip, g.HeaderToolTip(PropertiesTab.LabelsColumn));
        Assert.Equal(PropertiesTab.HoldersTip, g.HeaderToolTip(PropertiesTab.HoldersColumn));
        Assert.Equal(PropertiesTab.CloneAsTip, g.HeaderToolTip(PropertiesTab.CloneAsColumn));
        Assert.Equal("A one-column table whose row for a value gives the text shown next to it, e.g. \"3 (Fog)\". Values stay numbers. Blank = none.", PropertiesTab.LabelsTip);
        Assert.Equal("Holders that keep this property, comma-separated names. Blank = every holder.", PropertiesTab.HoldersTip);
        Assert.Equal("What a copy made with the counter's Clone command gets: Copy = this counter's current value; Default = what a new counter starts with.", PropertiesTab.CloneAsTip);

        // Rule ref: "(none)" then every ref as "{label}, page {page}".
        Assert.Equal(["(none)", "Ferry rules, page 4", "Weather rules, page 7"], g.CellItems(2, PropertiesTab.RuleRefColumn)!.ToList());
        Assert.Equal("(none)", g.Cell(2, PropertiesTab.RuleRefColumn));
        Pick(g, 2, PropertiesTab.RuleRefColumn, "Ferry rules, page 4");
        Assert.Equal("r-ferry", s.Rules!.ById<PropertyDef>("tolls")!.RuleRefId);
        Pick(g, 2, PropertiesTab.RuleRefColumn, "(none)");
        Assert.Null(s.Rules!.ById<PropertyDef>("tolls")!.RuleRefId);

        // Labels: only one-column tables.
        Assert.Equal(["", "Weather names"], g.CellItems(0, PropertiesTab.LabelsColumn)!.ToList());
        Pick(g, 0, PropertiesTab.LabelsColumn, "Weather names");
        Assert.Equal("t-wx", s.Rules!.Properties[0].Labels);
        Assert.Equal("Weather names", g.Cell(0, PropertiesTab.LabelsColumn));
        Pick(g, 0, PropertiesTab.LabelsColumn, "");
        Assert.Null(s.Rules!.Properties[0].Labels);

        // Holders: names, comma-separated; "global" is the built-in holder; blank = every holder.
        Type(g, 2, PropertiesTab.HoldersColumn, "Depot, global");
        Assert.Equal(["depot", HolderDef.GlobalId], s.Rules!.ById<PropertyDef>("tolls")!.Holders);
        Assert.Equal("Depot, global", g.Cell(2, PropertiesTab.HoldersColumn));
        Type(g, 2, PropertiesTab.HoldersColumn, " ");
        Assert.Null(s.Rules!.ById<PropertyDef>("tolls")!.Holders);

        // Clone as: Copy / Default.
        Assert.Equal(["Copy", "Default"], g.CellItems(0, PropertiesTab.CloneAsColumn)!.ToList());
        Assert.Equal("Copy", g.Cell(0, PropertiesTab.CloneAsColumn));
        Pick(g, 0, PropertiesTab.CloneAsColumn, "Default");
        Assert.Equal(CloneValue.Default, s.Rules!.Properties[0].CloneAs);
        Assert.Equal(PropertiesTab.CloneAsTip, g.CellToolTip(0, PropertiesTab.CloneAsColumn));
    }

    [AvaloniaFact]
    public void PropertiesTab_unknown_holder_refused()
    {
        var s = Session(p => B1(p) with
        {
            Properties = B1(p).Properties.Select(x => x.Id == "tolls" ? x with { Holders = new() { "yard" } } : x).ToList(),
        });
        var tab = Tab(s);
        Type(tab.PropertyGrid, 2, PropertiesTab.HoldersColumn, "Depot, Harbour");
        Assert.Equal("No holder named \"Harbour\"", tab.Status.Text);
        Assert.Equal(["yard"], s.Rules!.ById<PropertyDef>("tolls")!.Holders);   // unchanged
        Assert.Equal("Yard", tab.PropertyGrid.Cell(2, PropertiesTab.HoldersColumn));
    }

    [AvaloniaFact]
    public void HolderGrid_rule_ref_column()
    {
        var s = Session(p => B1(p) with { Holders = p.Holders.Select(h => h.Id == "yard" ? h with { RuleRefId = "r-gone" } : h).ToList() });
        var tab = Tab(s);
        var g = tab.HolderGrid;
        Assert.Equal("Rule ref", g.HeaderText(PropertiesTab.HolderRuleRefColumn));
        Assert.True(g.IsCellReadOnly(0, PropertiesTab.HolderRuleRefColumn));   // the built-in global holder
        Assert.Equal("(none)", g.Cell(1, PropertiesTab.HolderRuleRefColumn));
        Assert.Equal("r-gone (missing)", g.Cell(2, PropertiesTab.HolderRuleRefColumn));
        Assert.Equal(["(none)", "Ferry rules, page 4", "Weather rules, page 7"], g.CellItems(1, PropertiesTab.HolderRuleRefColumn)!.ToList());
        Pick(g, 1, PropertiesTab.HolderRuleRefColumn, "Weather rules, page 7");
        Assert.Equal("r-wx", s.Rules!.ById<HolderDef>("depot")!.RuleRefId);
        Assert.Equal("Main stock", s.Rules!.ById<HolderDef>("depot")!.Notes);
        Pick(g, 2, PropertiesTab.HolderRuleRefColumn, "(none)");
        Assert.Null(s.Rules!.ById<HolderDef>("yard")!.RuleRefId);
    }
}
