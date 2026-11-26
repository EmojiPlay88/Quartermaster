using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>B5 task 9: the Workbench editors for reacting sides, open-ended blocks, the end of the game and the schedule. Headless, invented Red/Blue fixture.</summary>
[Collection("MacFlag")]
public sealed class TurnStructureWorkbenchTests : IDisposable
{
    readonly Action<string> _prune = WorkbenchWindow.PruneCaches;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-tsworkbench-" + Guid.NewGuid().ToString("N"));
    readonly List<FixtureModule> _fixtures = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<Window> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public TurnStructureWorkbenchTests() => WorkbenchWindow.PruneCaches = _ => { };

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        Dispatcher.UIThread.RunJobs();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        WorkbenchWindow.PruneCaches = _prune;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }

    static void Type(EditGrid g, int row, int col, string text) { g.BeginEdit(row, col, text); g.CommitEdit(); Pump(); }

    static void Pick(EditGrid g, int row, int col, string item)
    {
        g.BeginEdit(row, col);
        ((ComboBox)g.Editor!).SelectedItem = item;
        g.CommitEdit();
        Pump();
    }

    /// <summary>Weather, Supply (Red), Movement (Red), Combat, End; a block "Round" x2 over Movement..Combat; sides Red and Blue.</summary>
    ModuleSession Session(bool withGame = false, Func<RulesPack, RulesPack>? tweak = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        if (withGame) s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("Invented");
        var fp = PhaseFixture.Pack("x");
        s.UpdateRules(p =>
        {
            p = p with { Formulas = fp.Formulas, Sides = fp.Sides, Phases = fp.Phases, Blocks = fp.Blocks };
            return tweak?.Invoke(p) ?? p;
        });
        return s;
    }

    T Host<T>(T tab) where T : Control
    {
        var w = new Window { Width = 1000, Height = 800, Content = tab };
        _windows.Add(w);
        w.Show();
        Pump();
        return tab;
    }

    PhasesTab PhasesFor(ModuleSession s) => Host(new PhasesTab(s, _dialogs, _ => null, null));
    EndOfGameTab EndFor(ModuleSession s) => Host(new EndOfGameTab(s, _dialogs));
    ScheduleTab ScheduleFor(ModuleSession s) => Host(new ScheduleTab(s, _dialogs));

    WorkbenchWindow Bench(ModuleSession s)
    {
        var wb = new WorkbenchWindow(s, _dialogs, () => Task.CompletedTask, () => Task.CompletedTask);
        _windows.Add(wb);
        wb.Show();
        Pump();
        return wb;
    }

    // ---------- Phases tab ----------

    [AvaloniaFact]
    public void Phases_reactor_checkboxes_commit()
    {
        var s = Session();
        var tab = PhasesFor(s);
        tab.SelectPhase("ph-weather");   // a phase without a side: both sides are offered
        Pump();
        Assert.Equal(["Red", "Blue"], tab.ReactorBoxes.Select(b => b.Box.Content).ToList());
        Assert.All(tab.ReactorBoxes, b => Assert.Equal(PhasesTab.ReactorsTip, Avalonia.Controls.ToolTip.GetTip(b.Box)));
        tab.ReactorBoxes[1].Box.IsChecked = true;
        Pump();
        Assert.Equal(["blue"], s.Rules!.Phases.First(p => p.Id == "ph-weather").Reactors);
        tab.ReactorBoxes[0].Box.IsChecked = true;
        Pump();
        Assert.Equal(["red", "blue"], s.Rules.Phases.First(p => p.Id == "ph-weather").Reactors);
        tab.ReactorBoxes[0].Box.IsChecked = false;
        tab.ReactorBoxes[1].Box.IsChecked = false;
        Pump();
        Assert.Null(s.Rules.Phases.First(p => p.Id == "ph-weather").Reactors);
    }

    [AvaloniaFact]
    public void Phases_reactor_list_excludes_own_side()
    {
        var s = Session(tweak: p => PhaseEdit.SetReactors(p, "ph-supply", new[] { "blue" }));
        var tab = PhasesFor(s);
        tab.SelectPhase("ph-supply");   // Red's phase
        Pump();
        Assert.Equal(["Blue"], tab.ReactorBoxes.Select(b => b.Box.Content).ToList());
        Assert.True(tab.ReactorBoxes[0].Box.IsChecked);
        // making Blue the phase's own side takes Blue out of the list and out of the reacting sides
        tab.SideBox.SelectedIndex = 2;
        Pump();
        Assert.Equal(["Red"], tab.ReactorBoxes.Select(b => b.Box.Content).ToList());
        Assert.False(tab.ReactorBoxes[0].Box.IsChecked);
        Assert.Null(s.Rules!.Phases.First(p => p.Id == "ph-supply").Reactors);
    }

    [AvaloniaFact]
    public void Block_dialog_until_stopped_gives_zero()
    {
        var w = new Window();
        _windows.Add(w);
        var d = new BlockDialog();
        Assert.Equal("Until a player stops", d.UntilStoppedBox.Content);
        Assert.Equal("Repeat these phases until the acting player chooses Continue at the last one.", Avalonia.Controls.ToolTip.GetTip(d.UntilStoppedBox));
        d.CountBox.Value = 4;
        d.LabelBox.Text = " Day ";
        Assert.Equal((4, "Day"), d.Result());
        d.UntilStoppedBox.IsChecked = true;
        Assert.False(d.CountBox.IsEnabled);
        Assert.Equal((0, "Day"), d.Result());
        d.UntilStoppedBox.IsChecked = false;
        Assert.True(d.CountBox.IsEnabled);
        Assert.Equal((4, "Day"), d.Result());
    }

    [AvaloniaFact]
    public void Block_dialog_zero_makes_an_open_ended_block()
    {
        var s = Session(tweak: p => p with { Blocks = new() });
        var tab = PhasesFor(s);
        tab.SelectPhase("ph-move");
        tab.PhaseList.Selection.Select(3);
        Pump();
        _dialogs.BlockAnswer = (0, "Day");
        Click(tab.BlockButton);
        var b = Assert.Single(s.Rules!.Blocks);
        Assert.Equal((0, "Day"), (b.Count, b.Label));
    }

    [AvaloniaFact]
    public void Block_marker_until_stopped()
    {
        var s = Session(tweak: p => p with
        {
            Blocks = new()
            {
                new PhaseBlock { FirstPhaseId = "ph-supply", LastPhaseId = "ph-end", Count = 0, Label = "Day" },
                new PhaseBlock { FirstPhaseId = "ph-move", LastPhaseId = "ph-combat", Count = 2, Label = "Round" },
            },
        });
        var tab = PhasesFor(s);
        Assert.Equal(
            ["Weather", "┃ Day (until stopped)  Supply", "┃ Day (until stopped)  ┃ Round ×2  Movement", "┃ Day (until stopped)  ┃ Round ×2  Combat", "┃ Day (until stopped)  End"],
            tab.PhaseRows.Select(r => r.Text).ToList());
    }

    // ---------- End of game tab ----------

    [AvaloniaFact]
    public void EndOfGame_last_turn_commits()
    {
        var s = Session();
        var tab = EndFor(s);
        Assert.Equal("The game ends after turn", tab.LastTurnCheck.Content);
        Assert.Equal("Last stage", tab.LastStageCheck.Content);
        Assert.False(tab.LastTurnBox.IsEnabled);
        tab.LastTurnCheck.IsChecked = true;
        Pump();
        Assert.Equal(1, s.Rules!.LastTurn);
        tab.LastTurnBox.Value = 12;
        Pump();
        Assert.Equal(12, s.Rules.LastTurn);
        tab.LastStageCheck.IsChecked = true;
        tab.LastStageBox.Value = 3;
        Pump();
        Assert.Equal(3, s.Rules.LastStage);
        tab.LastStageCheck.IsChecked = false;
        tab.LastTurnCheck.IsChecked = false;
        Pump();
        Assert.Null(s.Rules.LastStage);
        Assert.Null(s.Rules.LastTurn);
        Assert.Equal(999m, tab.LastTurnBox.Maximum);
    }

    [AvaloniaFact]
    public void EndOfGame_victory_fields_commit()
    {
        var s = Session();
        var tab = EndFor(s);
        Click(tab.AddButton);
        Assert.Equal("victory-1", s.Rules!.Victory!.Single().Id);
        tab.ConditionBox.Text = "turn() > 3";
        tab.MessageBox.Text = "Red holds the river";
        tab.CheckedInBox.Text = "Movement, combat";
        tab.SideBox.SelectedIndex = 1;   // Red
        Pump();
        tab.CommitPending();
        var v = s.Rules.Victory!.Single();
        Assert.Equal("turn() > 3", v.Condition);
        Assert.Equal("Red holds the river", v.Message);
        Assert.Equal("red", v.SideId);
        Assert.Equal(["ph-move", "ph-combat"], v.PhaseIds);
        Assert.Equal(["(no winner)", "Red", "Blue"], tab.SideBox.ItemsSource!.Cast<string>().ToList());
        Assert.Equal(["1. turn() > 3"], tab.Rows.Select(r => r.Text).ToList());

        // an unknown phase name is reported and nothing is committed for it
        tab.CheckedInBox.Text = "Movement, Nowhere";
        tab.CommitPending();
        Assert.Equal("No phase named \"Nowhere\"", tab.CheckedInError.Text);
        Assert.Equal(["ph-move", "ph-combat"], s.Rules.Victory!.Single().PhaseIds);

        // blank = every phase; blank side and message are stored as null
        tab.CheckedInBox.Text = "";
        tab.MessageBox.Text = "";
        tab.SideBox.SelectedIndex = 0;
        Pump();
        tab.CommitPending();
        v = s.Rules.Victory!.Single();
        Assert.Null(v.PhaseIds);
        Assert.Null(v.Message);
        Assert.Null(v.SideId);

        Click(tab.RemoveButton);
        Assert.Null(s.Rules.Victory);
    }

    [AvaloniaFact]
    public void EndOfGame_condition_live_line()
    {
        var s = Session(withGame: true);
        var tab = EndFor(s);
        Click(tab.AddButton);
        tab.ConditionBox.Text = "turn() >= 1";
        Pump();
        Assert.Equal("true now", tab.LiveText.Text);
        tab.ConditionBox.Text = "turn() > 500";
        Pump();
        Assert.Equal("false now", tab.LiveText.Text);
        tab.ConditionBox.Text = "turn() >";
        Pump();
        Assert.StartsWith("Does not parse", tab.LiveText.Text);
    }

    // ---------- Schedule tab ----------

    [AvaloniaFact]
    public void Schedule_grid_row_commits()
    {
        var s = Session();
        var tab = ScheduleFor(s);
        Assert.Equal(["Turn", "Stage", "Phase", "Action", "Counters", "Zone map", "Formula", "Label"], Enumerable.Range(0, 8).Select(tab.Grid.HeaderText).ToList());
        Click(tab.AddButton);
        Assert.Equal("sched-1", s.Rules!.Schedule!.Single().Id);
        Type(tab.Grid, 0, 0, "4");
        Type(tab.Grid, 0, 1, "2");
        Pick(tab.Grid, 0, 2, "Combat");
        Pick(tab.Grid, 0, 3, "Return");
        Type(tab.Grid, 0, 4, "Fuel > 0");
        Pick(tab.Grid, 0, 6, "Daily water");
        Type(tab.Grid, 0, 7, "Boats");
        var r = s.Rules.Schedule!.Single();
        Assert.Equal((4, 2, "ph-combat", ScheduleAction.Return, "Fuel > 0", "f-water", "Boats"), (r.Turn, r.Stage, r.PhaseId, r.Action, r.Piece, r.FormulaId, r.Label));
        Assert.Null(r.ZoneMapId);
        Assert.Equal(["Arrive", "Leave", "Return"], tab.Grid.CellItems(0, 3)!.ToList());

        Type(tab.Grid, 0, 1, "");   // blank stage = any
        Assert.Null(s.Rules.Schedule!.Single().Stage);
        Type(tab.Grid, 0, 7, "");
        Assert.Null(s.Rules.Schedule!.Single().Label);

        Click(tab.AddButton);
        Click(tab.UpButton);
        Assert.Equal(["sched-2", "sched-1"], s.Rules.Schedule!.Select(x => x.Id).ToList());
        Click(tab.RemoveButton);
        Click(tab.RemoveButton);
        Assert.Null(s.Rules.Schedule);
    }

    [AvaloniaFact]
    public void Schedule_filter_error_shown()
    {
        var s = Session();
        var tab = ScheduleFor(s);
        Click(tab.AddButton);
        Type(tab.Grid, 0, 4, "Fuel >");
        Assert.Same(EditGrid.ErrorBrush, tab.Grid.CellBackground(0, 4));
        Assert.NotNull(tab.Grid.CellToolTip(0, 4));
        Assert.Contains("Schedule row 1: counters", tab.IssuesText.Text);
        Type(tab.Grid, 0, 4, "Fuel > 0");
        Assert.NotSame(EditGrid.ErrorBrush, tab.Grid.CellBackground(0, 4));
    }

    // ---------- the window ----------

    [AvaloniaFact]
    public void Problems_jump_to_schedule_and_end_tabs()
    {
        var s = Session(tweak: p => p with
        {
            LastStage = 2,   // a last stage needs a last turn
            Victory = new() { new VictoryCondition { Id = "victory-1", Condition = "" } },
            Schedule = new() { new ScheduleRow { Id = "sched-1", PhaseId = "ph-combat", Piece = "" }, new ScheduleRow { Id = "sched-2", PhaseId = "ph-end", Piece = "" } },
        });
        var wb = Bench(s);
        wb.CommitPending();
        Pump();
        int Find(string text) => wb.IssueRows.ToList().FindIndex(r => r.Problem.Contains(text, StringComparison.Ordinal));

        wb.GoToIssue(Find("A last stage needs a last turn"));
        Pump();
        Assert.Same(wb.EndOfGame, ((TabItem)wb.Tabs.SelectedItem!).Content);

        wb.GoToIssue(Find("Schedule row 2: has no counter filter"));
        Pump();
        Assert.Same(wb.Schedule, ((TabItem)wb.Tabs.SelectedItem!).Content);
        Assert.Equal(1, wb.Schedule.Grid.CurrentCell!.Value.Row);

        wb.GoToIssue(Find("Victory condition 1: has no condition"));
        Pump();
        Assert.Same(wb.EndOfGame, ((TabItem)wb.Tabs.SelectedItem!).Content);
        Assert.Equal("victory-1", wb.EndOfGame.SelectedVictoryId);

        var pack = s.Rules!;
        Assert.Equal(WorkbenchTab.Schedule, WorkbenchRules.IssueTarget(pack, "sched-1")!.Value.Tab);
        Assert.Equal(WorkbenchTab.EndOfGame, WorkbenchRules.IssueTarget(pack, "victory-1")!.Value.Tab);
        Assert.Equal(WorkbenchTab.EndOfGame, WorkbenchRules.IssueTarget(pack, pack.Id, "The last turn must be at least 1")!.Value.Tab);
        Assert.Null(WorkbenchRules.IssueTarget(pack, pack.Id, "Something else"));
    }

    [AvaloniaFact]
    public void Guest_tabs_read_only()
    {
        var s = Session();
        var pack = s.Rules!;
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var g = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "gcache"), null);
        _sessions.Add(g);
        g.EnterGuestMode();
        var wb = Bench(g);
        g.LoadGuestRules(pack with { LastTurn = 5, Victory = new() { new VictoryCondition { Id = "victory-1", Condition = "turn() > 2" } } });
        Pump();
        Assert.True(wb.ReadOnlyShown);
        Assert.False(wb.Schedule.IsEnabled);
        Assert.False(wb.EndOfGame.IsEnabled);
        Assert.False(wb.Phases.IsEnabled);
        Assert.Equal(8, wb.Tabs.Items.Count);
        Assert.Equal("", wb.EndOfGame.LiveText.Text);   // a guest never evaluates a victory condition
    }
}
