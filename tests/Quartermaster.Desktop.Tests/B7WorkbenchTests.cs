using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>B7 task 10: the Workbench editors for the Reveal margin, sealed start entries and the alert side tooltip. Headless, invented data.</summary>
[Collection("MacFlag")]
public sealed class B7WorkbenchTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b7wb-" + Guid.NewGuid().ToString("N"));
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

    static void Toggle(EditGrid g, int row, int col)
    {
        g.SelectCell(row, col);
        g.Focus();
        var w = (Window)TopLevel.GetTopLevel(g)!;
        w.KeyPress(Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.None, (Avalonia.Input.PhysicalKey)0, null);
        Pump();
    }

    static void Pick(EditGrid g, int row, int col, string item)
    {
        g.BeginEdit(row, col);
        ((ComboBox)g.Editor!).SelectedItem = item;
        g.CommitEdit();
        Pump();
    }

    static FormulaDef Reveal(OutputAction action = OutputAction.Reveal, string? margin = null) => new()
    {
        Id = "f-rev", Name = "Show", Text = "a",
        Bindings = new() { new() { Variable = "a", Kind = BindingKind.Input } },
        Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "p-str", Action = action, Margin = margin } },
    };

    ModuleSession Session(FormulaDef? formula = null, bool phases = false)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.NewRules("Invented");
        var fp = PhaseFixture.Pack("x");
        s.UpdateRules(p => p with
        {
            Properties = new() { new PropertyDef { Id = "p-str", Name = "Strength", Scope = PropertyScope.Both } },
            Sides = fp.Sides,
            Formulas = formula == null ? fp.Formulas : new() { formula },
            Phases = phases ? fp.Phases : p.Phases,
            Blocks = phases ? fp.Blocks : p.Blocks,
        });
        return s;
    }

    T Host<T>(T tab) where T : Control
    {
        var win = new Window { Width = 1100, Height = 800, Content = tab };
        _windows.Add(win);
        win.Show();
        Pump();
        return tab;
    }

    FormulasTab Formulas(ModuleSession s) => Host(new FormulasTab(s, _dialogs, _ => null, null));
    PhasesTab Phases(ModuleSession s) => Host(new PhasesTab(s, _dialogs, _ => null, null));

    static FormulaOutput Out(ModuleSession s, string id = "f-rev") => s.Rules!.Formulas.Single(f => f.Id == id).Outputs[0];

    [AvaloniaFact]
    public void Outputs_margin_column_commits()
    {
        var s = Session(Reveal());
        var tab = Formulas(s);
        Assert.Equal("Margin", tab.Outputs.HeaderText(13));
        Assert.Equal(FormulasTab.MarginHint, tab.Outputs.HeaderToolTip(13));
        Assert.Equal("Reveal only: how wide the revealed range is. 0 or blank shows the exact value; 2 shows a range such as between 3 and 5 that holds the value.", FormulasTab.MarginHint);
        Type(tab.Outputs, 0, 13, " 2 ");
        Assert.Equal("2", Out(s).Margin);
        Assert.Equal("2", tab.Outputs.Cell(0, 13));
    }

    [AvaloniaFact]
    public void Margin_blank_becomes_null()
    {
        var s = Session(Reveal(margin: "3"));
        var tab = Formulas(s);
        Assert.Equal("3", tab.Outputs.Cell(0, 13));
        Type(tab.Outputs, 0, 13, "  ");
        Assert.Null(Out(s).Margin);
    }

    [AvaloniaFact]
    public void Reveal_action_keeps_property_cell()
    {
        var s = Session(Reveal(OutputAction.Write));
        var tab = Formulas(s);
        Assert.True(tab.Outputs.IsCellReadOnly(0, 13));   // closed unless Reveal, as Key is unless Write
        Assert.False(tab.Outputs.IsCellReadOnly(0, 12));
        Pick(tab.Outputs, 0, 7, "Reveal");
        Assert.Equal(OutputAction.Reveal, Out(s).Action);
        Assert.False(tab.Outputs.IsCellReadOnly(0, 13));
        Assert.True(tab.Outputs.IsCellReadOnly(0, 12));
        Assert.Equal("p-str", Out(s).Property);
        Assert.Equal("Strength", tab.Outputs.Cell(0, 2));
        Type(tab.Outputs, 0, 13, "1");
        Assert.Equal("1", Out(s).Margin);
        Pick(tab.Outputs, 0, 7, "Write");   // leaving Reveal drops the margin
        Assert.Null(Out(s).Margin);
        Assert.True(tab.Outputs.IsCellReadOnly(0, 13));
    }

    [AvaloniaFact]
    public void Start_grid_sealed_check_commits()
    {
        var s = Session(phases: true);
        var tab = Phases(s);
        tab.SelectPhase("ph-weather");
        Pump();
        Assert.Equal("Sealed", tab.StartGrid.HeaderText(4));
        Assert.Equal(PhasesTab.SealedTip, tab.StartGrid.HeaderToolTip(4));
        Assert.Equal("Every side answers this row in secret; the answers are applied together, in side order, once every side has answered.", PhasesTab.SealedTip);
        Assert.Equal("False", tab.StartGrid.Cell(0, 4));
        Toggle(tab.StartGrid, 0, 4);
        Assert.True(s.Rules!.Phases.First(p => p.Id == "ph-weather").OnStart![0].Sealed);
        Assert.Equal("True", tab.StartGrid.Cell(0, 4));
        Toggle(tab.StartGrid, 0, 4);
        Assert.False(s.Rules.Phases.First(p => p.Id == "ph-weather").OnStart![0].Sealed);
    }

    [AvaloniaFact]
    public void End_grid_has_no_sealed_column()
    {
        var s = Session(phases: true);
        s.UpdateRules(p => p with { Phases = p.Phases.Select(ph => ph.Id == "ph-weather" ? ph with { OnEnd = new() { new StartEntry { FormulaId = "f-weather", ForEach = ForEachMode.Once } } } : ph).ToList() });
        var tab = Phases(s);
        tab.SelectPhase("ph-weather");
        Pump();
        Assert.Equal(["Formula", "For each", "Filter (units)", "Asks side"], Enumerable.Range(0, 4).Select(tab.EndGrid.HeaderText).ToList());
        Assert.ThrowsAny<Exception>(() => tab.EndGrid.HeaderText(4));
    }

    [AvaloniaFact]
    public void Alert_side_tooltip_text()
    {
        var s = Session();
        var tab = Host(new AlertsTab(s, _dialogs));
        Assert.Equal("Only players holding this side or an allied side see the alert, in every phase.", ToolTip.GetTip(tab.SideBox));
    }

    [AvaloniaFact]
    public void Problems_list_shows_sealed_error()
    {
        var s = Session(phases: true);
        s.UpdateRules(p => p with
        {
            Phases = p.Phases.Select(ph => ph.Id == "ph-supply"
                ? ph with { OnStart = new() { new StartEntry { FormulaId = "f-water", ForEach = ForEachMode.Once, Sealed = true } } } : ph).ToList(),
        });
        var wb = new WorkbenchWindow(s, _dialogs, () => Task.CompletedTask, () => Task.CompletedTask);
        _windows.Add(wb);
        wb.Show();
        Pump();
        wb.CommitPending();
        Pump();
        Assert.Contains(wb.IssueRows, r => r.Problem.Contains("start entry 1 is sealed, but only a phase without a side may have one", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Old_pack_columns_unchanged()
    {
        var s = Session(Reveal(OutputAction.Write), phases: true);
        var f = Formulas(s);
        Assert.Equal(["Target", "Holder", "Property", "Op", "Value", "Choose from", "Pick by", "Action", "Piece", "Palette entry", "Layer", "To map", "Key", "Margin"],
            Enumerable.Range(0, 14).Select(f.Outputs.HeaderText).ToList());
        var p = Phases(s);
        Assert.Equal(["Formula", "For each", "Filter (units)", "Asks side", "Sealed"], Enumerable.Range(0, 5).Select(p.StartGrid.HeaderText).ToList());
    }
}
