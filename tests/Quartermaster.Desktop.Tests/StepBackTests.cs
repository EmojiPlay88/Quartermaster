using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 2b task 6: Step Back (Edit menu, Ctrl+Left, the toolbar button and the Step Back dialog). Invented pack from PhaseFixture;
/// headless, fake dialogs.</summary>
[Collection("MacFlag")]
public sealed class StepBackTests : IDisposable
{
    const string StepBackItem = "Step &Back to Start of Phase";
    const string StepBackToItem = "Step Back &To...";

    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-stepback-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public StepBackTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var w in _windows)
        {
            if (w.Session is { } s) FormulaWindows.CloseAllFor(s);
            w.ForceClose();
        }
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    MainWindow Game(Difficulty difficulty = Difficulty.FullyAuto)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache" + _windows.Count), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        w.StartSetup(0, difficulty);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        Pump();
        Flush(w);
        return w;
    }

    static void Flush(MainWindow w) { w.Strip.Flush(); w.Phases.Flush(); }

    static void Key(MainWindow w, Key key, PhysicalKey physical)
    {
        w.KeyPress(key, RawInputModifiers.Control, physical, null);
        Pump();
        Flush(w);
    }

    static void CtrlRight(MainWindow w) => Key(w, Avalonia.Input.Key.Right, PhysicalKey.ArrowRight);
    static void CtrlLeft(MainWindow w) => Key(w, Avalonia.Input.Key.Left, PhysicalKey.ArrowLeft);

    static void Move(MainWindow w, string id, double x)
    {
        w.Actions!.Move(new[] { new MovePieces(new[] { id }, "Main Map", new PointD(x, 50), null) });
        Pump();
    }

    static PointD Pos(MainWindow w, string id) => w.Session!.Game!.State.Pieces[id].Pos;

    /// <summary>Started, in Red's Supply phase, with Red 1 moved since the phase began.</summary>
    MainWindow InSupplyWithAMove(Difficulty d = Difficulty.FullyAuto)
    {
        var w = Game(d);
        CtrlRight(w);   // Start game: Weather
        CtrlRight(w);   // Supply
        Assert.Equal("Red player — Supply", w.Phases.TitleText);
        Move(w, PhaseFixture.RedOne, 150);
        return w;
    }

    [AvaloniaFact]
    public void Ctrl_left_steps_back_to_start_of_phase_after_confirm()
    {
        var w = Game();
        CtrlRight(w);
        CtrlRight(w);
        var before = Pos(w, PhaseFixture.RedOne);
        Move(w, PhaseFixture.RedOne, 150);
        Assert.NotEqual(before, Pos(w, PhaseFixture.RedOne));

        _dialogs.CheckpointPick = 0;
        CtrlLeft(w);
        var (rows, confirmOnly) = Assert.Single(_dialogs.CheckpointPrompts);
        Assert.True(confirmOnly);
        var row = Assert.Single(rows);
        Assert.Contains("Supply", row.Phase);
        Assert.Equal(before, Pos(w, PhaseFixture.RedOne));
        Assert.Equal(PhaseRunDispatch.SteppedBackText, w.StatusText);
        Assert.Equal("Red player — Supply", w.Phases.TitleText);
    }

    [AvaloniaFact]
    public void Step_back_to_lists_checkpoints_and_rewinds()
    {
        var w = InSupplyWithAMove();
        var expected = w.History!.Checkpoints();
        _dialogs.CheckpointPick = -1;   // the oldest row
        w.ClickMenu(StepBackToItem);
        Pump();
        Flush(w);
        var (rows, confirmOnly) = Assert.Single(_dialogs.CheckpointPrompts);
        Assert.False(confirmOnly);
        Assert.True(rows.Count >= 2, $"{rows.Count} row(s)");
        Assert.True(rows[0].LogIndex > rows[^1].LogIndex);   // newest first
        Assert.Equal(expected, rows);
        Assert.Equal("game start", rows[^1].Phase);
        Assert.Equal(new PointD(30, 50), Pos(w, PhaseFixture.RedOne));
        Assert.True(w.PhaseControl!.View().BeforeStart);   // back to before the game started
        Assert.Equal(PhaseRunDispatch.SteppedBackText, w.StatusText);
    }

    [AvaloniaFact]
    public void Cancel_keeps_state()
    {
        var w = InSupplyWithAMove();
        var moved = Pos(w, PhaseFixture.RedOne);
        _dialogs.CheckpointPick = null;   // Cancel
        CtrlLeft(w);
        w.ClickMenu(StepBackToItem);
        Pump();
        Assert.Equal(2, _dialogs.CheckpointPrompts.Count);
        Assert.Equal(moved, Pos(w, PhaseFixture.RedOne));
        Assert.NotEqual(PhaseRunDispatch.SteppedBackText, w.StatusText);
        Assert.False(w.History!.CanRedo);
    }

    [AvaloniaFact]
    public void Step_back_disabled_before_start_with_reason()
    {
        var w = Game();
        var item = w.FindMenuItem(StepBackItem)!;
        var to = w.FindMenuItem(StepBackToItem)!;
        var button = w.Toolbar.Find("phase.stepBack")!;
        Assert.False(item.IsEnabled);
        Assert.False(to.IsEnabled);
        Assert.False(button.IsEnabled);
        Assert.Null(ToolTip.GetTip(item));   // nothing done yet: no reason (WinForms)
        Assert.Equal("Step back to start of phase (Ctrl+Left)", ToolTip.GetTip(button));
        Assert.False(w.Commands.Execute("phase.stepBack"));   // the key goes through
        Assert.Empty(_dialogs.CheckpointPrompts);

        // Enabled once something was done in a phase.
        CtrlRight(w);
        Move(w, PhaseFixture.RedOne, 150);
        Assert.True(item.IsEnabled);
        Assert.True(to.IsEnabled);
        Assert.True(button.IsEnabled);
        Assert.Null(ToolTip.GetTip(item));

        // A save rewritten without its undo history: no checkpoints, and the reason is shown (WinForms FloorTip).
        Directory.CreateDirectory(_tmp);
        var path = Path.Combine(_tmp, "t.qsave");
        w.SaveGame(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove("genesis"); json.Remove("actions"); json.Remove("checkpoints");
        json["log"] = new System.Text.Json.Nodes.JsonArray();
        File.WriteAllText(path, json.ToJsonString());
        w.OpenGame(path);
        Pump();
        Assert.False(item.IsEnabled);
        Assert.False(to.IsEnabled);
        Assert.False(button.IsEnabled);
        Assert.Equal(MainWindow.FloorTip, ToolTip.GetTip(item));
        Assert.Equal(MainWindow.FloorTip, ToolTip.GetTip(to));
        Assert.Equal(MainWindow.FloorTip, ToolTip.GetTip(button));
        Assert.True(ToolTip.GetShowOnDisabled(item));
    }

    [AvaloniaFact]
    public void Edit_menu_has_step_back_items_after_redo()
    {
        var w = Game();
        var edit = w.MenuLabels()["&Edit"];
        Assert.Equal(new[] { "&Undo", "&Redo", StepBackItem, StepBackToItem }, edit);
        Assert.Equal("Ctrl+Left", w.FindMenuItem(StepBackItem)!.InputGesture?.ToString());
    }

    [AvaloniaFact]
    public void Toolbar_step_back_button()
    {
        var w = InSupplyWithAMove();
        var ids = w.Toolbar.Buttons.Select(b => b.Id).ToList();
        Assert.Equal(ids.IndexOf("edit.redo") + 1, ids.IndexOf("phase.stepBack"));
        var b = w.Toolbar.Find("phase.stepBack")!;
        Assert.Equal("◀ Step Back", b.Content);
        Assert.True(b.IsEnabled);
        Assert.Equal("Step back to start of phase (Ctrl+Left)", ToolTip.GetTip(b));
        _dialogs.CheckpointPick = 0;
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        var (_, confirmOnly) = Assert.Single(_dialogs.CheckpointPrompts);
        Assert.True(confirmOnly);
        Assert.Equal(new PointD(30, 50), Pos(w, PhaseFixture.RedOne));
    }

    [AvaloniaFact]
    public void Redo_after_step_back()
    {
        var w = InSupplyWithAMove();
        var moved = Pos(w, PhaseFixture.RedOne);
        _dialogs.CheckpointPick = 0;
        CtrlLeft(w);
        Assert.Equal(new PointD(30, 50), Pos(w, PhaseFixture.RedOne));
        var redo = w.FindMenuItem("&Redo", prefix: true)!;
        Assert.True(redo.IsEnabled);
        Assert.True(w.Commands.Execute("edit.redo"));
        Pump();
        Assert.Equal(moved, Pos(w, PhaseFixture.RedOne));
    }

    [AvaloniaFact]
    public void Step_back_closes_formula_windows()
    {
        var w = Game(Difficulty.Hardcore);
        var s = w.Session!;
        CtrlRight(w);   // Weather: the checked weather-roll window opens for the queued run
        Pump();
        var win = Assert.Single(FormulaWindows.OpenFor(s));
        Assert.True(win.IsVisible);
        Move(w, PhaseFixture.RedOne, 150);
        _dialogs.CheckpointPick = 0;
        CtrlLeft(w);
        Pump();
        Assert.Empty(FormulaWindows.OpenFor(s));
        Assert.False(win.IsVisible);
        Assert.Equal(PhaseRunDispatch.SteppedBackText, w.StatusText);
    }

    [AvaloniaFact]
    public void Step_back_dialog_confirm_texts()
    {
        var w = InSupplyWithAMove();
        var h = w.History!;
        var row = h.StartOfPhase()!;
        var dlg = new StepBackDialog(new[] { row }, r => HistoryController.Describe(h.Preview(r)), confirmOnly: true);
        Assert.Equal("Step Back to Start of Phase", dlg.Title);
        Assert.Equal(PhaseTexts.StepBackConfirm(row), dlg.HeadText);
        Assert.Equal(HistoryController.Describe(h.Preview(row)), dlg.SummaryText);
        Assert.NotEqual("", dlg.SummaryText);
        Assert.Equal(new[] { "Step Back", "Cancel" }, dlg.ButtonTexts);
        Assert.Empty(dlg.Headers);   // no list in confirm mode
        Assert.Same(row, dlg.Selected);

        var result = dlg.ShowDialog<CheckpointRow?>(w);
        Pump();
        dlg.ClickStepBack();
        Pump();
        Assert.Same(row, result.GetAwaiter().GetResult());
    }

    [AvaloniaFact]
    public void Step_back_dialog_list_selects_and_cancels()
    {
        var w = InSupplyWithAMove();
        var h = w.History!;
        var rows = h.Checkpoints();
        var dlg = new StepBackDialog(rows, r => HistoryController.Describe(h.Preview(r)), confirmOnly: false);
        Assert.Equal("Step Back To", dlg.Title);
        Assert.Equal(new[] { "Turn", "Stage", "Side", "Phase", "Reached", "Actions" }, dlg.Headers);
        Assert.Equal(rows.Select(r => PhaseTexts.StepBackCells(r)).ToList(), dlg.RowCells);
        Assert.Same(rows[0], dlg.Selected);   // the newest is selected
        Assert.Equal(HistoryController.Describe(h.Preview(rows[0])), dlg.SummaryText);
        dlg.Select(rows.Count - 1);
        Assert.Same(rows[^1], dlg.Selected);
        Assert.Equal(HistoryController.Describe(h.Preview(rows[^1])), dlg.SummaryText);

        var result = dlg.ShowDialog<CheckpointRow?>(w);
        Pump();
        dlg.ClickCancel();
        Pump();
        Assert.Null(result.GetAwaiter().GetResult());
    }
}
