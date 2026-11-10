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

/// <summary>Port stage 4a task 8: the workbench Phases tab. Headless, fake dialogs, invented phases.</summary>
[Collection("MacFlag")]
public sealed class PhasesTabTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-phasestab-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Weather, Supply (Red), Movement (Red), Combat, End; a block "Round" x2 over Movement..Combat; sides Red and Blue.</summary>
    ModuleSession Session(bool withPack = true, Func<RulesPack, RulesPack>? tweak = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        if (withPack)
        {
            s.NewRules("Invented");
            var fp = PhaseFixture.Pack("x");
            s.UpdateRules(p =>
            {
                p = p with { Formulas = fp.Formulas, Sides = fp.Sides, Phases = fp.Phases, Blocks = fp.Blocks };
                return tweak?.Invoke(p) ?? p;
            });
        }
        return s;
    }

    PhasesTab Tab(ModuleSession s, Func<string, RuleRef?>? link = null, Action<RuleRef>? show = null)
    {
        var tab = new PhasesTab(s, _dialogs, link ?? (_ => null), show);
        var w = new Window { Width = 1000, Height = 800, Content = tab };
        _windows.Add(w);
        w.Show();
        Pump();
        return tab;
    }

    static List<string> Names(ModuleSession s) => s.Rules!.Phases.Select(p => p.Name).ToList();

    static PhaseDef Ph(ModuleSession s, string id) => s.Rules!.Phases.Single(p => p.Id == id);

    static void Pick(EditGrid g, int row, int col, string item)
    {
        g.BeginEdit(row, col);
        ((ComboBox)g.Editor!).SelectedItem = item;
        g.CommitEdit();
        Pump();
    }

    [AvaloniaFact]
    public void Lists_phases_with_blocks_and_sides()
    {
        var s = Session();
        var tab = Tab(s);
        Assert.Equal(5, tab.PhaseCount);
        Assert.Equal("ph-weather", tab.SelectedPhaseId);
        Assert.Equal(["Weather", "Supply", "┃ Round ×2  Movement", "┃ Round ×2  Combat", "End"], tab.PhaseRows.Select(r => r.Text).ToList());
        Assert.Equal(["", "Red", "Red", "", ""], tab.PhaseRows.Select(r => r.Side).ToList());
        Assert.Equal(["Red", "Blue"], ((IEnumerable<string>)tab.SideList.ItemsSource!).ToList());
        Assert.Equal("startsWith(name(), \"Red\")", tab.SideFilter.Text);
    }

    [AvaloniaFact]
    public void Select_phase_by_id()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectPhase("ph-combat");
        Pump();
        Assert.Equal("ph-combat", tab.SelectedPhaseId);
        Assert.Equal("Combat", tab.NameBox.Text);
        Assert.Equal(["Resolve attacks"], ((IEnumerable<string>)tab.Checklist.ItemsSource!).ToList());
        Assert.Equal("Fuel <= 0", tab.WarnGrid.Cell(0, 0));
        Assert.Equal("True", tab.WarnGrid.Cell(0, 2));
        tab.SelectPhase("nope");
        Assert.Equal("ph-combat", tab.SelectedPhaseId);
        tab.SelectPhase("ph-supply");
        Pump();
        Assert.Equal("Daily water", tab.StartGrid.Cell(0, 0));
        Assert.Equal("Units", tab.StartGrid.Cell(0, 1));
        Assert.Equal(1, tab.SideBox.SelectedIndex);   // Red
    }

    [AvaloniaFact]
    public void Add_remove_duplicate_move_phase()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectPhase("ph-supply");
        Pump();
        _dialogs.Texts.Enqueue("Fog");
        Click(tab.AddButton);
        Assert.Equal("Phase 6", _dialogs.TextPrompts[0].Split('|')[2]);
        Assert.Equal(["Weather", "Supply", "Fog", "Movement", "Combat", "End"], Names(s));
        Assert.Equal(s.Rules!.Phases[2].Id, tab.SelectedPhaseId);

        Click(tab.DuplicateButton);
        Assert.Equal(["Weather", "Supply", "Fog", "Fog (copy)", "Movement", "Combat", "End"], Names(s));
        Assert.Equal(s.Rules.Phases[3].Id, tab.SelectedPhaseId);

        Click(tab.UpButton);
        Assert.Equal(["Weather", "Supply", "Fog (copy)", "Fog", "Movement", "Combat", "End"], Names(s));
        Assert.Equal(s.Rules.Phases[2].Id, tab.SelectedPhaseId);
        Click(tab.DownButton);
        Assert.Equal(["Weather", "Supply", "Fog", "Fog (copy)", "Movement", "Combat", "End"], Names(s));
        Click(tab.UpButton);   // back
        Click(tab.DownButton);
        Assert.Equal(7, tab.PhaseCount);

        var before = Names(s);
        _dialogs.Answer = false;
        Click(tab.RemoveButton);
        Assert.Equal(before, Names(s));
        _dialogs.Answer = true;
        var id = tab.SelectedPhaseId;
        var nm = s.Rules.Phases.Single(x => x.Id == id).Name;
        Click(tab.RemoveButton);
        Assert.Equal(6, tab.PhaseCount);
        Assert.DoesNotContain(s.Rules.Phases, x => x.Id == id);
        Assert.NotNull(tab.SelectedPhaseId);
        Assert.Equal($"Remove the phase \"{nm}\"?", _dialogs.Questions[^1].Message);
        Assert.Equal("Remove phase", _dialogs.Questions[^1].Caption);
        Assert.All(_dialogs.YesNoDefaultNo, d => Assert.True(d));
    }

    [AvaloniaFact]
    public void Move_that_breaks_a_block_warns_and_changes_nothing()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectPhase("ph-move");
        Pump();
        Click(tab.DownButton);   // Movement would pass Combat: first after last
        Assert.Equal(["Weather", "Supply", "Movement", "Combat", "End"], Names(s));
        Assert.Single(_dialogs.Warnings);
        Assert.Equal(PhaseEdit.BlockBreakMessage, _dialogs.Warnings[0].Message);
        Assert.Equal("Phases", _dialogs.Warnings[0].Caption);
    }

    [AvaloniaFact]
    public void Make_block_over_selection_asks_count_and_label()
    {
        var s = Session(true, p => p with { Blocks = new() });
        var tab = Tab(s);
        tab.SelectPhase("ph-supply");
        tab.PhaseList.Selection.Select(2);
        Pump();
        _dialogs.BlockAnswer = (3, "Day");
        Click(tab.BlockButton);
        Assert.Equal(1, _dialogs.BlockPrompts);
        var b = Assert.Single(s.Rules!.Blocks);
        Assert.Equal(("ph-supply", "ph-move", 3, "Day"), (b.FirstPhaseId, b.LastPhaseId, b.Count, b.Label));
        Assert.Equal("┃ Day ×3  Supply", tab.PhaseRows[1].Text);
        Assert.Equal("┃ Day ×3  Movement", tab.PhaseRows[2].Text);

        // cancelled dialog: nothing changes
        s.UpdateRules(p => p with { Blocks = new() });
        Pump();
        tab.SelectPhase("ph-end");
        _dialogs.BlockAnswer = null;
        Click(tab.BlockButton);
        Assert.Empty(s.Rules.Blocks);

        // non-contiguous selection is refused before the dialog
        var prompts = _dialogs.BlockPrompts;
        tab.SelectPhase("ph-weather");
        tab.PhaseList.Selection.Select(4);
        Pump();
        Click(tab.BlockButton);
        Assert.Equal(prompts, _dialogs.BlockPrompts);
        Assert.Equal("Select neighbouring phases", _dialogs.Warnings[^1].Message);
    }

    [AvaloniaFact]
    public void Remove_block()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectPhase("ph-weather");
        Pump();
        Click(tab.UnblockButton);
        Assert.Single(s.Rules!.Blocks);
        Assert.Equal("Select a phase inside a block first.", _dialogs.Warnings[^1].Message);
        tab.SelectPhase("ph-combat");
        Pump();
        Click(tab.UnblockButton);
        Assert.Empty(s.Rules.Blocks);
        Assert.Equal("Combat", tab.PhaseRows[3].Text);
        Assert.Equal("ph-combat", tab.SelectedPhaseId);
    }

    [AvaloniaFact]
    public void Side_add_rename_remove_and_filter_error()
    {
        var s = Session();
        var tab = Tab(s);
        _dialogs.Texts.Enqueue("Green");
        Click(tab.SideAddButton);
        Assert.Equal(["Red", "Blue", "Green"], s.Rules!.Sides.Select(x => x.Name).ToList());
        Assert.Equal(2, tab.SideList.SelectedIndex);
        _dialogs.Texts.Enqueue("red");   // duplicate
        Click(tab.SideAddButton);
        Assert.Equal(3, s.Rules.Sides.Count);
        Assert.Equal("Another side already has this name", _dialogs.Warnings[^1].Message);

        _dialogs.Texts.Enqueue("Grey");
        Click(tab.SideRenameButton);
        Assert.Equal("Grey", s.Rules.Sides[2].Name);
        Assert.Equal("Green", _dialogs.TextPrompts[^1].Split('|')[2]);

        tab.SideFilter.Text = "name() ==";   // does not parse: error line at once, committed on leaving
        Pump();
        Assert.False(string.IsNullOrEmpty(tab.SideFilterError.Text));
        tab.CommitPending();
        Assert.Equal("name() ==", s.Rules.Sides[2].UnitFilter);
        tab.SideFilter.Text = "startsWith(name(), \"G\")";
        Pump();
        Assert.True(string.IsNullOrEmpty(tab.SideFilterError.Text));
        tab.CommitPending();
        Assert.Equal("startsWith(name(), \"G\")", s.Rules.Sides[2].UnitFilter);

        tab.SideList.SelectedIndex = 0;   // Red: used by two phases
        Pump();
        _dialogs.Answer = false;
        Click(tab.SideRemoveButton);
        Assert.Equal(3, s.Rules.Sides.Count);
        _dialogs.Answer = true;
        Click(tab.SideRemoveButton);
        Assert.Equal(["Blue", "Grey"], s.Rules.Sides.Select(x => x.Name).ToList());
        Assert.All(s.Rules.Phases, p => Assert.NotEqual("red", p.SideId));
        Assert.Contains("Remove the side \"Red\"? 2 phase(s) will have no side.", _dialogs.Questions.Select(q => q.Message));
        Assert.Equal("Remove side", _dialogs.Questions[^1].Caption);
    }

    [AvaloniaFact]
    public void Phase_side_combo_commits()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SideBox.SelectedIndex = 2;   // Blue
        Pump();
        Assert.Equal("blue", Ph(s, "ph-weather").SideId);
        Assert.Equal("Blue", tab.PhaseRows[0].Side);
        tab.SideBox.SelectedIndex = 0;
        Pump();
        Assert.Null(Ph(s, "ph-weather").SideId);
    }

    [AvaloniaFact]
    public void Instructions_and_notes_commit()
    {
        var s = Session();
        var tab = Tab(s);
        tab.NameBox.Text = "Weather check";
        tab.Instructions.Text = "Roll once.";
        tab.Notes.Text = "A note";
        tab.CommitPending();
        Assert.Equal(("Weather check", "Roll once.", "A note"), (Ph(s, "ph-weather").Name, Ph(s, "ph-weather").Instructions, Ph(s, "ph-weather").Notes));
        Assert.Equal("Weather check", tab.PhaseRows[0].Text);
        tab.Notes.Text = "";
        tab.CommitPending();
        Assert.Null(Ph(s, "ph-weather").Notes);
    }

    [AvaloniaFact]
    public void Text_commits_after_the_pause_and_on_detach()
    {
        var s = Session();
        var tab = Tab(s);
        var w = (Window)tab.Parent!;
        tab.Instructions.Text = "typed quickly";
        tab.WarnGrid.IsEnabled = true;
        w.Content = null;   // a hidden tab is detached
        Pump();
        Assert.Equal("typed quickly", Ph(s, "ph-weather").Instructions);
        w.Content = tab;
        Pump();
        Assert.Equal("typed quickly", tab.Instructions.Text);
    }

    [AvaloniaFact]
    public void Grid_cells_commit_and_pick_formulas()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectPhase("ph-supply");
        Pump();
        tab.StartGrid.BeginEdit(0, 2, "startsWith(name(), \"R\")");
        tab.StartGrid.CommitEdit();
        Assert.Equal("startsWith(name(), \"R\")", Ph(s, "ph-supply").OnStart[0].UnitFilter);
        Pick(tab.StartGrid, 0, 0, "Weather roll");
        Assert.Equal("f-weather", Ph(s, "ph-supply").OnStart[0].FormulaId);
        Pick(tab.StartGrid, 0, 1, "Once");
        Assert.Equal(ForEachMode.Once, Ph(s, "ph-supply").OnStart[0].ForEach);

        Click(tab.StartAddButton);
        Assert.Equal(2, Ph(s, "ph-supply").OnStart.Count);
        Assert.Equal("f-weather", Ph(s, "ph-supply").OnStart[1].FormulaId);   // the first formula
        tab.StartGrid.SelectCell(1, 0);
        Click(tab.StartRemoveButton);
        Assert.Single(Ph(s, "ph-supply").OnStart);

        tab.SelectPhase("ph-move");
        Pump();
        tab.MoveGrid.BeginEdit(0, 1, "name() != \"\"");
        tab.MoveGrid.CommitEdit();
        Assert.Equal("name() != \"\"", Ph(s, "ph-move").OnMove[0].UnitFilter);
        Click(tab.MoveAddButton);
        Assert.Equal(2, Ph(s, "ph-move").OnMove.Count);

        tab.SelectPhase("ph-combat");
        Pump();
        tab.WarnGrid.BeginEdit(0, 1, "Low on fuel");
        tab.WarnGrid.CommitEdit();
        Assert.Equal("Low on fuel", Ph(s, "ph-combat").Warnings[0].Message);
        tab.WarnGrid.SelectCell(0, 2);
        ((Window)tab.Parent!).KeyPress(Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.None, (Avalonia.Input.PhysicalKey)0, null);
        Assert.False(Ph(s, "ph-combat").Warnings[0].IsDecision);
        Click(tab.WarnAddButton);
        Assert.Equal(2, Ph(s, "ph-combat").Warnings.Count);
    }

    [AvaloniaFact]
    public void Grid_expression_error_cell()
    {
        var s = Session(true, p => p with
        {
            Phases = p.Phases.Select(x => x.Id == "ph-supply"
                ? x with { OnStart = new() { new StartEntry { FormulaId = "f-water", UnitFilter = "name() ==" } }, Warnings = new() { new PhaseWarning { Condition = "1 +" } } } : x).ToList(),
        });
        var tab = Tab(s);
        tab.SelectPhase("ph-supply");
        Pump();
        Assert.Same(EditGrid.ErrorBrush, tab.StartGrid.CellBackground(0, 2));
        Assert.NotNull(tab.StartGrid.CellToolTip(0, 2));
        Assert.NotSame(EditGrid.ErrorBrush, tab.StartGrid.CellBackground(0, 0));
        Assert.Same(EditGrid.ErrorBrush, tab.WarnGrid.CellBackground(0, 0));
        // fixing it clears the pink
        tab.StartGrid.BeginEdit(0, 2, "name() != \"\"");
        tab.StartGrid.CommitEdit();
        Assert.NotSame(EditGrid.ErrorBrush, tab.StartGrid.CellBackground(0, 2));
        tab.StartGrid.BeginEdit(0, 2, "name() ==");
        tab.StartGrid.CommitEdit();
        Assert.Same(EditGrid.ErrorBrush, tab.StartGrid.CellBackground(0, 2));
        tab.WarnGrid.BeginEdit(0, 0, "Fuel > 0");
        tab.WarnGrid.CommitEdit();
        Assert.NotSame(EditGrid.ErrorBrush, tab.WarnGrid.CellBackground(0, 0));
    }

    [AvaloniaFact]
    public void Checklist_add_edit_remove()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectPhase("ph-combat");
        Pump();
        _dialogs.Texts.Enqueue("  Check morale ");
        Click(tab.CheckAddButton);
        Assert.Equal(["Resolve attacks", "Check morale"], Ph(s, "ph-combat").Checklist);
        tab.Checklist.SelectedIndex = 0;
        _dialogs.Texts.Enqueue("Resolve all attacks");
        Pump();
        _ = tab.EditChecklistItem();
        Pump();
        Assert.Equal(["Resolve all attacks", "Check morale"], Ph(s, "ph-combat").Checklist);
        Assert.Equal("Resolve attacks", _dialogs.TextPrompts[^1].Split('|')[2]);
        tab.Checklist.SelectedIndex = 1;
        Click(tab.CheckRemoveButton);
        Assert.Equal(["Resolve all attacks"], Ph(s, "ph-combat").Checklist);
    }

    [AvaloniaFact]
    public void Rule_ref_link()
    {
        var s = Session();
        var r = new RuleRef { Id = "r1", Label = "Combat", DocumentEntry = "rules.pdf", Page = 4 };
        s.UpdateRules(p => p with { RuleRefs = p.RuleRefs.Append(r).ToList() });
        RuleRef? shown = null;
        var tab = Tab(s, name => name == "Weather" ? r : null, x => shown = x);
        Assert.False(tab.RefRow.IsLink);
        Click(tab.RefRow.LinkButton);
        Assert.Equal("r1", Ph(s, "ph-weather").RuleRefId);
        Assert.True(tab.RefRow.IsLink);
        tab.RefRow.ClickLink();
        Assert.Same(r, shown);
        Assert.True(tab.CanUseRuleRef);
        tab.SelectPhase("ph-end");
        Pump();
        Assert.True(tab.UseRuleRef(n => n == "End" ? r : null));
        Assert.Equal("r1", Ph(s, "ph-end").RuleRefId);
        Assert.False(tab.UseRuleRef(_ => null));
        var found = new List<string>();
        tab.FindInRulebook = found.Add;
        Assert.True(tab.RefRow.FindButton.IsVisible);
        Click(tab.RefRow.FindButton);
        Assert.Equal(["End"], found);
    }

    [AvaloniaFact]
    public void Edits_round_trip_through_qrules()
    {
        Directory.CreateDirectory(_tmp);
        var s = Session();
        var tab = Tab(s);
        tab.SelectPhase("ph-supply");
        Pump();
        _dialogs.Texts.Enqueue("Fog");
        Click(tab.AddButton);
        tab.Instructions.Text = "Line one\nLine two";
        tab.Notes.Text = "n";
        tab.CommitPending();
        tab.SelectPhase("ph-move");
        Pump();
        tab.MoveGrid.BeginEdit(0, 1, "name() != \"\"");
        tab.MoveGrid.CommitEdit();
        _dialogs.Texts.Enqueue("Green");
        Click(tab.SideAddButton);
        tab.CommitPending();
        var path = Path.Combine(_tmp, "p.qrules");
        RulesPackIo.Save(s.Rules!, path);
        var back = RulesPackIo.Load(path);
        Assert.Equal(4, back.Format);
        Assert.Equal(s.Rules!.Phases.Select(p => (p.Id, p.Name, p.SideId, p.Instructions, p.Notes)), back.Phases.Select(p => (p.Id, p.Name, p.SideId, p.Instructions, p.Notes)));
        Assert.Equal(s.Rules.Blocks.Select(b => (b.FirstPhaseId, b.LastPhaseId, b.Count, b.Label)), back.Blocks.Select(b => (b.FirstPhaseId, b.LastPhaseId, b.Count, b.Label)));
        Assert.Equal(s.Rules.Sides.Select(x => (x.Id, x.Name, x.UnitFilter)), back.Sides.Select(x => (x.Id, x.Name, x.UnitFilter)));
        Assert.Equal("name() != \"\"", back.Phases.Single(p => p.Id == "ph-move").OnMove[0].UnitFilter);
        Assert.Equal("Line one\nLine two", back.Phases[2].Instructions);
        Assert.Contains(back.Sides, x => x.Name == "Green");
    }

    [AvaloniaFact]
    public void Opening_and_selecting_leaves_pack_untouched()
    {
        var s = Session(true, p => p with { Phases = p.Phases.Select(x => x.Id == "ph-weather" ? x with { Instructions = "a\r\nb", Notes = "c\r\nd" } : x).ToList() });
        var pack = s.Rules;
        var dirty = s.RulesDirty;
        var tab = Tab(s);
        tab.SelectPhase("ph-supply");
        Pump();
        tab.SelectPhase("ph-weather");
        Pump();
        tab.Instructions.Focus();
        tab.NameBox.Focus();   // leaving a CRLF text box with no edit
        Pump();
        tab.SideList.SelectedIndex = 1;
        Pump();
        tab.CommitPending();
        Assert.Same(pack, s.Rules);
        Assert.Equal(dirty, s.RulesDirty);
    }

    [AvaloniaFact]
    public void Refused_edit_in_guest_mode_redraws_from_the_pack()
    {
        var s = Session();
        var tab = Tab(s);
        s.EnterGuestMode();
        tab.SelectPhase("ph-combat");
        Pump();
        tab.WarnGrid.BeginEdit(0, 1, "changed");
        tab.WarnGrid.CommitEdit();
        Pump();
        Assert.Equal("Out of fuel", tab.WarnGrid.Cell(0, 1));
        Assert.Equal("Out of fuel", Ph(s, "ph-combat").Warnings[0].Message);
        Assert.Contains(s.Log.Items, e => e.Message.StartsWith("Phase edit failed"));
        tab.NameBox.Text = "Zed";
        tab.CommitPending();
        Assert.Equal("Combat", tab.NameBox.Text);
    }

    [AvaloniaFact]
    public void Remove_dialog_answer_is_dropped_when_the_phase_changed_meanwhile()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectPhase("ph-end");
        Pump();
        var gate = new TaskCompletionSource<string?>();
        _dialogs.TextGate = gate;
        _dialogs.Texts.Enqueue("never");
        Click(tab.SideAddButton);   // waiting on the name
        s.UpdateRules(p => p with { Phases = p.Phases.Where(x => x.Id != "ph-end").ToList() });
        Pump();
        gate.SetResult("Green");
        Pump();
        Assert.Equal(4, s.Rules!.Phases.Count);
        Assert.Contains(s.Rules.Sides, x => x.Name == "Green");   // a side is independent of the phase list
        Assert.NotNull(tab.SelectedPhaseId);
    }

    [AvaloniaFact]
    public void Tab_without_a_pack_is_empty_and_disabled()
    {
        var tab = Tab(Session(false));
        Assert.Null(tab.SelectedPhaseId);
        Assert.Equal(0, tab.PhaseCount);
        Assert.False(tab.NameBox.IsEffectivelyEnabled);
    }

    [AvaloniaTheory]
    [InlineData("name")]
    [InlineData("instructions")]
    [InlineData("notes")]
    [InlineData("grid")]
    public void Pending_edit_does_not_swallow_phase_click(string what)
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectPhase("ph-supply");
        Pump();
        switch (what)
        {
            case "name": tab.NameBox.Text = "Supply 2"; break;
            case "instructions": tab.Instructions.Text = "do it"; break;
            case "notes": tab.Notes.Text = "n"; break;
            default: tab.StartGrid.BeginEdit(0, 2, "name() != \"\""); break;
        }
        tab.PhaseList.SelectedIndex = 4;   // End
        Pump();
        Assert.Equal("ph-end", tab.SelectedPhaseId);
        Assert.Equal([4], tab.PhaseList.Selection.SelectedIndexes.ToList());
        Assert.Equal("End", tab.NameBox.Text);
        var sup = Ph(s, "ph-supply");
        Assert.Equal(what switch { "name" => "Supply 2", _ => "Supply" }, sup.Name);
        if (what == "instructions") Assert.Equal("do it", sup.Instructions);
        if (what == "notes") Assert.Equal("n", sup.Notes);
        if (what == "grid") Assert.Equal("name() != \"\"", sup.OnStart[0].UnitFilter);
    }

    [AvaloniaFact]
    public void Make_block_overlapping_an_existing_block_is_refused()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectPhase("ph-supply");
        tab.PhaseList.Selection.Select(2);   // Supply..Movement, Movement is in Round
        Pump();
        _dialogs.BlockAnswer = (2, "x");
        var before = s.Rules;
        var rows = tab.PhaseRows.Select(r => r.Text).ToList();
        Click(tab.BlockButton);
        Assert.Equal("These phases are already in a block", _dialogs.Warnings[^1].Message);
        Assert.Equal("Phases", _dialogs.Warnings[^1].Caption);
        Assert.Same(before, s.Rules);
        Assert.Equal(rows, tab.PhaseRows.Select(r => r.Text).ToList());
    }

    /// <summary>Forwards everything to the fake dialogs but makes the Make block dialog wait for a gate.</summary>
    public class GatedDialogs : System.Reflection.DispatchProxy
    {
        public object Inner = null!;
        public TaskCompletionSource<(int Count, string Label)?>? Gate;

        protected override object? Invoke(System.Reflection.MethodInfo? m, object?[]? args)
        {
            if (m!.Name == "AskBlockAsync" && Gate is { } g) return g.Task;
            return m.Invoke(Inner, args);
        }
    }

    PhasesTab GatedTab(ModuleSession s, out GatedDialogs gated)
    {
        var proxy = System.Reflection.DispatchProxy.Create<Quartermaster.Desktop.Shell.IDialogs, GatedDialogs>();
        gated = (GatedDialogs)(object)proxy;
        gated.Inner = _dialogs;
        gated.Gate = new();
        var tab = new PhasesTab(s, proxy, _ => null);
        var w = new Window { Width = 1000, Height = 800, Content = tab };
        _windows.Add(w);
        w.Show();
        Pump();
        return tab;
    }

    [AvaloniaFact]
    public void Block_dialog_answer_is_dropped_when_the_selection_changed_meanwhile()
    {
        var s = Session(true, p => p with { Blocks = new() });
        var tab = GatedTab(s, out var g);
        tab.SelectPhase("ph-supply");
        Pump();
        Click(tab.BlockButton);   // waiting
        tab.SelectPhase("ph-end");
        Pump();
        g.Gate!.SetResult((2, "x"));
        Pump();
        Assert.Empty(s.Rules!.Blocks);
    }

    [AvaloniaFact]
    public void Block_dialog_answer_is_dropped_when_the_phase_was_removed_meanwhile()
    {
        var s = Session(true, p => p with { Blocks = new() });
        var tab = GatedTab(s, out var g);
        tab.SelectPhase("ph-supply");
        Pump();
        Click(tab.BlockButton);
        s.UpdateRules(p => p with { Phases = p.Phases.Where(x => x.Id != "ph-supply").ToList() });
        Pump();
        g.Gate!.SetResult((2, "x"));
        Pump();
        Assert.Empty(s.Rules!.Blocks);
    }
}
