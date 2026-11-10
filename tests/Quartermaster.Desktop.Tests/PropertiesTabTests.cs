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
        var items = ((IEnumerable<string>)((ComboBox)g.Editor!).ItemsSource!).ToList();
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
}
