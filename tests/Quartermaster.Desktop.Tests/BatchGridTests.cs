using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 2b task 8: the batch grid window (BatchGridWindow) and the missing-input dialog, driven directly (the phase wiring that opens
/// them comes with the phase UI task). Headless only, on the invented phase fixture.</summary>
[Collection("MacFlag")]
public sealed class BatchGridTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-batch-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public BatchGridTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var w in _windows)
        {
            if (w.Session != null) FormulaWindows.CloseAllFor(w.Session);
            w.ForceClose();
        }
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void PumpUntil(Func<bool> done, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            Pump();
            if (done()) return;
            Assert.True(DateTime.UtcNow < until, "timed out");
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
    }

    /// <summary>A game at <paramref name="d"/> in the Movement phase with both red units moved (or only <paramref name="moved"/>): one pending Fuel
    /// run. The phase wiring opens the run's UI as soon as it is queued; unless <paramref name="keepOpened"/>, those windows are closed again so
    /// the tests below drive the windows themselves.</summary>
    (MainWindow W, PendingRun Run) Make(Difficulty d, bool terrainMissing = false, bool keepOpened = false, string[]? moved = null,
        Func<VariableBinding, VariableBinding>? terrain = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache" + _windows.Count), null) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        w.StartSetup(0, d);
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            var tweak = terrain ?? (terrainMissing ? b => b with { AutoValue = null } : null);
            return tweak != null
                ? p with { Formulas = p.Formulas.Select(f => f.Id != "f-fuel" ? f : f with { Bindings = f.Bindings.Select(b => b.Variable == "Terrain" ? tweak(b) : b).ToList() }).ToList() }
                : p;
        });
        Pump();
        var c = w.PhaseControl!;
        c.Start(); c.Advance(); c.Advance();   // Movement
        c.ExecuteMove(new[] { new MovePieces(moved ?? new[] { PhaseFixture.RedOne, PhaseFixture.RedTwo }, "Main Map", new PointD(130, 50), null) });
        Pump();
        if (!keepOpened) FormulaWindows.CloseAllFor(s);
        return (w, Assert.Single(s.Game!.State.Pending, r => r.FormulaId == "f-fuel"));
    }

    BatchGridWindow Batch(MainWindow w, PendingRun run, Action<int>? openRow = null)
    {
        var win = FormulaWindows.ShowBatch(w, w.Session!, w.Actions!, run.Id, openRow)!;
        Pump();
        return win;
    }

    static Button ButtonOf(Window win, string label) =>
        win.GetVisualDescendants().OfType<Button>().Single(b => b.Content is string t && t == label);

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    double? Fuel(MainWindow w, string id) => Ledger.Get(w.Session!.Game!.State, LedgerKey.Piece(id), "fuel");

    [AvaloniaFact]
    public void Grid_has_columns_title_and_buttons()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var win = Batch(w, run);
        Assert.Same(win, FormulaWindows.ShowBatch(w, w.Session!, w.Actions!, run.Id, null));   // one window per run
        Assert.Equal(new[] { "Unit", "Moved", "Terrain", "Result", "Ledger", "Exceptions" }, win.Headers);
        Assert.Equal("Fuel for movement — 0 of 2 applied", win.Title);
        Assert.Equal(2, win.RowCount);
        Assert.Equal("Red 1", win.CellText(0, 0));
        Assert.Equal("", win.CellText(0, 1));   // hardcore: the player types the values
        Assert.True(win.IsChoiceColumn(2));
        Assert.False(win.IsChoiceColumn(1));
        ButtonOf(win, "Calculate all");
        ButtonOf(win, "Apply all");
        ButtonOf(win, "Apply row");
        Assert.Null(win.CheatSheet);   // hardcore: no cheat sheet
    }

    [AvaloniaFact]
    public void Edit_cell_and_calculate_all()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var win = Batch(w, run);
        Click(ButtonOf(win, "Calculate all"));
        Pump();
        win.TypeCell(0, 1, "99");
        Assert.Equal("99", win.CellText(0, 1));
        Assert.True(win.CellWrong(0, 1));
        Click(ButtonOf(win, "Calculate all"));
        Pump();
        Assert.Equal("Calculated 2 row(s).", win.StatusText);
        Assert.NotEqual("", win.CellText(0, 1));
        Assert.NotEqual("99", win.CellText(0, 1));
        Assert.False(win.CellWrong(0, 1));
        Assert.NotEqual("", win.CellText(0, 3));
        Assert.Contains("→", win.CellText(0, 4));
    }

    [AvaloniaFact]
    public void Apply_all_skips_red_rows_with_status()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var win = Batch(w, run);
        Click(ButtonOf(win, "Calculate all"));
        Pump();
        win.TypeCell(0, 1, "99");
        Click(ButtonOf(win, "Apply all"));
        Pump();
        Assert.Equal("Applied 1 row(s); rows with a red cell were skipped.", win.StatusText);
        Assert.Equal(40, Fuel(w, PhaseFixture.RedOne));
        Assert.True(Fuel(w, PhaseFixture.RedTwo) < 40);
        Assert.Equal("Fuel for movement — 1 of 2 applied", win.Title);
        // Nothing left to apply but the red row.
        Click(ButtonOf(win, "Apply all"));
        Pump();
        Assert.Equal(FormulaWindowRules.NothingAppliedText, win.StatusText);
    }

    [AvaloniaFact]
    public void Apply_row_without_selection_status()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var win = Batch(w, run);
        Click(ButtonOf(win, "Apply row"));
        Assert.Equal("Select a row first.", win.StatusText);
        win.SelectRow(0);
        Click(ButtonOf(win, "Apply row"));
        Assert.Equal(FormulaWindowRules.RowNotMatchingText, win.StatusText);   // hardcore, nothing typed yet
        Click(ButtonOf(win, "Calculate all"));
        Click(ButtonOf(win, "Apply row"));
        Pump();
        Assert.Equal("Row applied.", win.StatusText);
        Assert.True(Fuel(w, PhaseFixture.RedOne) < 40);
        Assert.Equal(40, Fuel(w, PhaseFixture.RedTwo));
    }

    [AvaloniaFact]
    public void Batch_row_applied_elsewhere_is_skipped()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var win = Batch(w, run);
        Click(ButtonOf(win, "Calculate all"));
        // Row 0 is finished from another place (a formula window for the unit).
        var other = new BatchModel(w.Session!, w.Actions!, run.Id);
        other.CalculateAll();
        Assert.True(other.ApplyRow(0));
        PumpUntil(() => win.Title == "Fuel for movement — 1 of 2 applied");
        Assert.Equal("Applied", win.CellText(0, 3));
        Click(ButtonOf(win, "Apply all"));
        Pump();
        Assert.Equal("Applied 1 row(s); rows with a red cell were skipped.", win.StatusText);
        Assert.True(Fuel(w, PhaseFixture.RedTwo) < 40);
    }

    [AvaloniaFact]
    public void Double_click_row_opens_unit_dialog()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var rows = new List<int>();
        var win = Batch(w, run, rows.Add);
        var at = win.UnitCellCenter(1);
        win.MouseDown(at, MouseButton.Left);
        win.MouseUp(at, MouseButton.Left);
        win.MouseDown(at, MouseButton.Left);
        win.MouseUp(at, MouseButton.Left);
        Pump();
        Assert.Equal(new[] { 1 }, rows);
    }

    [AvaloniaFact]
    public void Semi_auto_shows_cheat_sheet_following_the_row()
    {
        var (w, run) = Make(Difficulty.SemiAuto);
        var win = Batch(w, run);
        var cheat = Assert.IsType<CheatSheetView>(win.CheatSheet);
        win.SelectRow(0);
        Pump();
        Assert.Contains(cheat.RowTexts, t => t.StartsWith("Terrain"));
    }

    [AvaloniaFact]
    public void Window_closes_when_run_is_gone()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var win = Batch(w, run);
        Assert.Single(FormulaWindows.OpenBatchFor(w.Session!));
        w.PhaseControl!.Advance();   // clears the run
        PumpUntil(() => !win.IsVisible);
        Assert.Empty(FormulaWindows.OpenBatchFor(w.Session!));
    }

    [AvaloniaFact]
    public void Close_all_for_closes_batch()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var win = Batch(w, run);
        Assert.True(win.IsVisible);
        FormulaWindows.CloseAllFor(w.Session!);
        Pump();
        Assert.False(win.IsVisible);
        Assert.Empty(FormulaWindows.OpenBatchFor(w.Session!));
    }

    [AvaloniaFact]
    public void Missing_ok_disabled_until_answered()
    {
        var (w, run) = Make(Difficulty.FullyAuto, terrainMissing: true);
        var dlg = new MissingInputDialog(w.Session!, w.Actions!, run);
        dlg.Show(w);
        Pump();
        Assert.Equal("Fuel for movement", dlg.Title);
        Assert.Equal(new[] { "Terrain" }, dlg.AskedVariables);
        Assert.False(dlg.OkEnabled);
        dlg.Type("Terrain", "   ");
        Assert.False(dlg.OkEnabled);
        dlg.Type("Terrain", "Sand");
        Assert.True(dlg.OkEnabled);
        dlg.Close();
    }

    [AvaloniaFact]
    public void Missing_dialog_ok_applies_all()
    {
        var (w, run) = Make(Difficulty.FullyAuto, terrainMissing: true);
        var dlg = new MissingInputDialog(w.Session!, w.Actions!, run);
        var result = dlg.ShowDialog<MissingInputResult?>(w);
        Pump();
        dlg.Type("Terrain", "Sand");
        dlg.ClickOk();
        Pump();
        Assert.Equal(MissingInputResult.Ok, result.GetAwaiter().GetResult());
        Assert.DoesNotContain(w.Session!.Game!.State.Pending, r => r.Id == run.Id);
        Assert.Equal(34, Fuel(w, PhaseFixture.RedOne));
        Assert.Equal(34, Fuel(w, PhaseFixture.RedTwo));
    }

    [AvaloniaFact]
    public void Missing_dialog_bad_answer_stays_open_with_message()
    {
        var (w, run) = Make(Difficulty.FullyAuto, terrainMissing: true);
        var dlg = new MissingInputDialog(w.Session!, w.Actions!, run);
        var result = dlg.ShowDialog<MissingInputResult?>(w);
        Pump();
        dlg.Type("Terrain", "Swamp");
        dlg.ClickOk();
        Pump();
        Assert.False(result.IsCompleted);
        Assert.StartsWith("0 applied; 2 failed. ", dlg.MessageText);
        dlg.ClickPerUnit();
        Pump();
        Assert.Equal(MissingInputResult.PerUnit, result.GetAwaiter().GetResult());
        Assert.Contains(w.Session!.Game!.State.Pending, r => r.Id == run.Id);
    }

    // ---------- the phase wiring end to end (task 6): queued runs open these windows ----------

    [AvaloniaFact]
    public void Semi_auto_run_with_many_units_opens_batch()
    {
        var (w, run) = Make(Difficulty.SemiAuto, keepOpened: true);
        var win = Assert.Single(FormulaWindows.OpenBatchFor(w.Session!));
        Assert.True(win.IsVisible);
        Assert.Equal(2, win.RowCount);
        Assert.Equal("Fuel for movement — 0 of 2 applied", win.Title);
        Assert.IsType<CheatSheetView>(win.CheatSheet);
        Assert.Empty(FormulaWindows.OpenFor(w.Session!));   // no unit windows for a run of several units
        Assert.Contains(w.Session!.Game!.State.Pending, r => r.Id == run.Id);
    }

    [AvaloniaFact]
    public void Batch_row_double_click_opens_linked_unit_window()
    {
        var (w, run) = Make(Difficulty.Hardcore, keepOpened: true);
        var win = Assert.Single(FormulaWindows.OpenBatchFor(w.Session!));
        var at = win.UnitCellCenter(1);
        win.MouseDown(at, MouseButton.Left);
        win.MouseUp(at, MouseButton.Left);
        win.MouseDown(at, MouseButton.Left);
        win.MouseUp(at, MouseButton.Left);
        Pump();
        var unit = Assert.Single(FormulaWindows.OpenFor(w.Session!));
        Assert.Equal(run.Id, unit.Link!.RunId);
        Assert.Equal(FormulaWindowRules.Key("f-fuel", PhaseFixture.RedTwo, run.Id), unit.Key);
    }

    [AvaloniaFact]
    public void Fully_auto_missing_input_ok_applies_all()
    {
        _dialogs.MissingHandler = new Dialogs().AskMissingAsync;   // the real dialog, driven below
        var (w, run) = Make(Difficulty.FullyAuto, terrainMissing: true, keepOpened: true);
        Assert.Equal(new[] { "Fuel for movement" }, _dialogs.MissingAsked);
        var dlg = Assert.Single(w.OwnedWindows.OfType<MissingInputDialog>());
        dlg.Type("Terrain", "Sand");
        dlg.ClickOk();
        Pump();
        Assert.False(dlg.IsVisible);
        Assert.DoesNotContain(w.Session!.Game!.State.Pending, r => r.Id == run.Id);
        Assert.Equal(34, Fuel(w, PhaseFixture.RedOne));
        Assert.Equal(34, Fuel(w, PhaseFixture.RedTwo));
        Assert.Empty(FormulaWindows.OpenBatchFor(w.Session!));
    }

    [AvaloniaFact]
    public void Per_unit_opens_units()
    {
        _dialogs.Missing.Enqueue(MissingInputResult.PerUnit);
        var (w, run) = Make(Difficulty.FullyAuto, terrainMissing: true, keepOpened: true);
        var win = Assert.Single(FormulaWindows.OpenBatchFor(w.Session!));   // several units: the batch grid
        Assert.Equal(2, win.RowCount);
        Assert.Contains(w.Session!.Game!.State.Pending, r => r.Id == run.Id);
    }

    [AvaloniaFact]
    public void Per_unit_with_one_unit_opens_unit_window()
    {
        _dialogs.Missing.Enqueue(MissingInputResult.PerUnit);
        var (w, run) = Make(Difficulty.FullyAuto, terrainMissing: true, keepOpened: true, moved: new[] { PhaseFixture.RedOne });
        Assert.Empty(FormulaWindows.OpenBatchFor(w.Session!));
        var unit = Assert.Single(FormulaWindows.OpenFor(w.Session!));
        Assert.Equal(run.Id, unit.Link!.RunId);
    }

    [AvaloniaFact]
    public void Step_back_closes_batch_grid()
    {
        var (w, _) = Make(Difficulty.Hardcore, keepOpened: true);
        var win = Assert.Single(FormulaWindows.OpenBatchFor(w.Session!));
        _dialogs.CheckpointPick = 0;
        Assert.True(w.Commands.Execute("phase.stepBack"));
        Pump();
        Assert.Single(_dialogs.CheckpointPrompts);
        Assert.False(win.IsVisible);
        Assert.Empty(FormulaWindows.OpenBatchFor(w.Session!));
        Assert.Equal(PhaseRunDispatch.SteppedBackText, w.StatusText);
    }

    [AvaloniaFact]
    public void Show_batch_returns_null_when_run_is_not_pending()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var s = w.Session!;
        Assert.Null(FormulaWindows.ShowBatch(w, s, w.Actions!, "no-such-run", null));
        w.PhaseControl!.Advance();   // clears the run
        Pump();
        Assert.DoesNotContain(s.Game!.State.Pending, r => r.Id == run.Id);
        Assert.Null(FormulaWindows.ShowBatch(w, s, w.Actions!, run.Id, null));
        Assert.Empty(FormulaWindows.OpenBatchFor(s));
        Assert.Empty(w.OwnedWindows.OfType<BatchGridWindow>());
    }

    [AvaloniaFact]
    public void Missing_ok_enabled_for_out_of_range_answer()
    {
        // WinForms enables OK on any non-blank answer; the engine reports out-of-range values when applying.
        var (w, run) = Make(Difficulty.FullyAuto, terrain: b => b with { AutoValue = null, Min = 1, Max = 5 });
        var dlg = new MissingInputDialog(w.Session!, w.Actions!, run);
        dlg.Show(w);
        Pump();
        Assert.Equal(new[] { "Terrain" }, dlg.AskedVariables);
        dlg.Type("Terrain", "99");
        Assert.True(dlg.OkEnabled);
        dlg.Type("Terrain", " ");
        Assert.False(dlg.OkEnabled);
        dlg.Close();
    }
}
