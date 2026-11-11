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
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache" + _windows.Count), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
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

    // ---- stage 3a task 3: counter pictures and the hover preview ----

    static bool PicturesReady(BatchGridWindow win) => Enumerable.Range(0, win.RowCount).All(r => win.PictureOf(r) != null);

    [AvaloniaFact]
    public void Batch_unit_cell_shows_counter_picture()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var win = Batch(w, run);
        Assert.Equal(150 + 64 + 8, win.UnitColumnWidth);   // WinForms: 150 + picture + 8
        Assert.Equal(52, win.RowHeightOf(0));
        PumpUntil(() => PicturesReady(win));
        Assert.Equal(new Avalonia.PixelSize(64, 48), win.PictureOf(0)!.PixelSize);
        Assert.False(win.PlaceholderShown(0));
        Assert.Equal("Red 1", win.CellText(0, 0));
        // The picture sits in the cell's left padding, the unit's name to its right.
        var pic = win.PictureBounds(0);
        var cell = win.UnitCellBounds(0);
        Assert.Equal(64, pic.Width);
        Assert.Equal(48, pic.Height);
        Assert.Equal(cell.X + 2, pic.X, 0);
        Assert.Equal(cell.Y + (cell.Height - 48) / 2, pic.Y, 0);
        Assert.True(win.UnitTextLeft(0) >= pic.Right + 6, "the unit name overlaps the picture");
        // It is really drawn: the fixture counter (a #C04040 square) shows in the picture box.
        using var frame = win.CaptureRenderedFrame();
        var px = TestHelpers.Pixels(frame!);
        var red = 0;
        for (var y = (int)pic.Y; y < (int)pic.Bottom; y++)
            for (var x = (int)pic.X; x < (int)pic.Right; x++)
                if (px.Px[y * px.Width + x] is var c && Math.Abs(c.Red - 0xC0) < 8 && Math.Abs(c.Green - 0x40) < 8 && Math.Abs(c.Blue - 0x40) < 8) red++;
        Assert.True(red > 500, $"the counter picture did not draw ({red} counter pixels)");
    }

    [AvaloniaFact]
    public void Batch_unit_hover_shows_preview_with_label()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var win = Batch(w, run);
        PumpUntil(() => PicturesReady(win));
        Assert.False(win.Preview.IsOpen);
        var pic = win.PictureBounds(1);
        win.MouseMove(pic.Center);
        PumpUntil(() => win.Preview.IsOpen);
        Assert.Equal("Red 2", win.Preview.Label);
        Assert.Equal(PhaseFixture.RedTwo, win.Preview.PieceId);
        var size = win.Preview.PictureSize;
        Assert.True(Math.Max(size.Width, size.Height) >= 200, $"preview {size} is smaller than 200 px on its long side");
        // Cursor + 16, as WinForms (the window is far from the screen edges).
        var cursor = Avalonia.VisualExtensions.PointToScreen(win, pic.Center);
        Assert.Equal(new Avalonia.PixelPoint(cursor.X + 16, cursor.Y + 16), win.Preview.ScreenPosition);
        // Off the picture (over the unit's name): hidden.
        win.MouseMove(new Avalonia.Point(win.UnitTextLeft(1) + 20, pic.Center.Y));
        Pump();
        Assert.False(win.Preview.IsOpen);
        // Back on it, then a click: hidden (WinForms MouseDown).
        win.MouseMove(pic.Center);
        PumpUntil(() => win.Preview.IsOpen);
        win.MouseDown(pic.Center, MouseButton.Left);
        win.MouseUp(pic.Center, MouseButton.Left);
        Pump();
        Assert.False(win.Preview.IsOpen);
    }

    [AvaloniaFact]
    public void Batch_no_picture_for_other_side_hidden_piece()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var s = w.Session!;
        var win = Batch(w, run);
        PumpUntil(() => PicturesReady(win));
        // Main Map becomes Red's and Blue takes the screen: both red units leave the view.
        s.UpdateRules(r => r with { MapOwners = new List<MapOwner> { new() { MapId = s.Model.Maps[0].Id, SideId = "red" } } });
        s.SetViewSides(new HashSet<string> { "blue" });
        PumpUntil(() => win.PictureOf(0) == null && win.PictureOf(1) == null);
        Assert.False(s.View!.Pieces.ContainsKey(PhaseFixture.RedOne));
        Assert.Equal(ModuleSession.HiddenText, win.CellText(0, 0));
        for (var r = 0; r < win.RowCount; r++) Assert.False(win.PlaceholderShown(r));   // no placeholder either: nothing to draw
        win.MouseMove(win.PictureBounds(0).Center);
        Pump();
        Assert.False(win.Preview.IsOpen);
        // Nothing queued for them either, now or later.
        for (var i = 0; i < 20; i++) { Pump(); Thread.Sleep(5); }
        Assert.Null(win.PictureOf(0));
        Assert.Equal(0, win.Thumbnails.PendingCount);
    }

    [AvaloniaFact]
    public void Batch_preview_render_held_then_closed_never_renders()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var s = w.Session!;
        var win = Batch(w, run);
        PumpUntil(() => PicturesReady(win));
        using var gate = new ManualResetEventSlim(false);
        win.Preview.RenderGate = gate;   // the preview's render task waits here
        win.MouseMove(win.PictureBounds(0).Center);
        Pump();
        Assert.False(win.Preview.IsOpen);
        Assert.Equal(0, win.Preview.Renders);

        // Closing the module: the batch grid closes synchronously and its workers stop before the session is disposed.
        _windows.Remove(w);
        w.ForceClose();
        Assert.True(s.Disposed);
        Assert.Empty(FormulaWindows.OpenBatchFor(s));
        Assert.True(win.Preview.Stopped);
        Assert.True(win.Thumbnails.IsDisposed);

        gate.Set();   // the held render wakes up after the session is gone
        for (var i = 0; i < 20; i++) { Pump(); Thread.Sleep(10); }
        Assert.Equal(0, win.Preview.Renders);
        Assert.False(win.Preview.IsOpen);
        win.Preview.Show(win, default, PhaseFixture.RedOne, "Red 1");   // after dispose: nothing, no throw
        Assert.False(win.Preview.IsOpen);
    }
    [AvaloniaFact]
    public void Preview_dispose_waits_for_every_render_in_flight()
    {
        var (w, _) = Make(Difficulty.Hardcore);
        var s = w.Session!;
        var preview = new CounterPreview(s);
        using var first = new ManualResetEventSlim(false);
        using var second = new ManualResetEventSlim(false);
        preview.RenderGate = first;
        preview.Show(w, default, PhaseFixture.RedOne, "Red 1");    // held on its worker
        preview.RenderGate = second;
        preview.Show(w, default, PhaseFixture.RedTwo, "Red 2");    // supersedes the first; held too
        preview.RenderGate = null;
        Assert.Equal(2, preview.InFlight);

        // The newer render is let go soon, the older one later: Dispose must wait for both (the older one may already be drawing).
        var firstReleased = new ManualResetEventSlim(false);
        var releaser = new Thread(() =>
        {
            Thread.Sleep(100);
            second.Set();
            Thread.Sleep(300);
            firstReleased.Set();
            first.Set();
        }) { IsBackground = true };
        releaser.Start();
        preview.Dispose();
        Assert.True(firstReleased.IsSet, "Dispose returned while the first render was still running");
        Assert.Equal(0, preview.InFlight);
        releaser.Join();
        Assert.Equal(0, preview.Renders);   // both were superseded or stopped before drawing
        Assert.False(preview.IsOpen);
    }
}
