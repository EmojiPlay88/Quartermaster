using Avalonia;
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
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>B4 task 5: the menu batch grid, Several runs and the counter menu entries. Headless only, on the invented phase fixture.</summary>
[Collection("MacFlag")]
public sealed class MenuBatchGridTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-menubatch-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public MenuBatchGridTests() => ShortcutGestures.IsMac = false;

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
        Func<VariableBinding, VariableBinding>? terrain = null, Func<RulesPack, RulesPack>? pack = null)
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
            if (pack != null) p = pack(p);
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


    BatchGridWindow Menu(MainWindow w, params string[] ids)
    {
        var win = FormulaWindows.ShowMenuBatch(w, w.Session!, w.Actions!, "f-fuel", ids, null)!;
        Pump();
        return win;
    }

    static Button ButtonOf(Window win, string label) =>
        win.GetVisualDescendants().OfType<Button>().Single(b => b.Content is string t && t == label);

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The fuel formula's Moved becomes a typed Input, so a menu run (no measured move) has every value to type.</summary>
    static RulesPack MovedInput(RulesPack p) => p with
    {
        Formulas = p.Formulas.Select(f => f.Id != "f-fuel" ? f : f with
        {
            Bindings = f.Bindings.Select(b => b.Variable == "Moved" ? b with { Kind = BindingKind.Input } : b).ToList(),
        }).ToList(),
    };

    double? Fuel(MainWindow w, string id) => Ledger.Get(w.Session!.Game!.State, LedgerKey.Piece(id), "fuel");

    [AvaloniaFact]
    public void Menu_grid_has_all_rows_line()
    {
        var (w, _) = Make(Difficulty.Hardcore);
        var win = Menu(w, PhaseFixture.RedOne, PhaseFixture.RedTwo);
        Assert.True(win.IsMenuBatch);
        Assert.StartsWith("menu:", win.Key);
        Assert.Equal("Fuel for movement — 2 runs", win.Title);
        Assert.Equal(new[] { "Unit", "Moved", "Terrain", "Result", "Ledger", "Exceptions" }, win.Headers);
        Assert.Equal(2, win.RowCount);
        Assert.Equal(FormulaWindowRules.AllRowsText, win.CellText(-1, 0));
        Assert.Equal("Red 1", win.CellText(0, 0));
        Assert.Equal("Red 2", win.CellText(1, 0));
        // An All rows cell fills every row whose own cell is blank; a row's own cell wins.
        win.TypeCell(-1, 2, "Sand");
        Pump();
        Assert.Equal("Sand", win.CellText(0, 2));
        Assert.Equal("Sand", win.CellText(1, 2));
        win.TypeCell(1, 2, "Clear");
        Pump();
        Assert.Equal("Sand", win.CellText(0, 2));
        Assert.Equal("Clear", win.CellText(1, 2));
        Assert.Equal("Sand", win.CellText(-1, 2));
        Assert.Single(FormulaWindows.OpenBatchFor(w.Session!));
    }

    [AvaloniaFact]
    public void Menu_grid_add_and_remove_row()
    {
        var (w, _) = Make(Difficulty.Hardcore);
        var win = Menu(w, PhaseFixture.RedOne, PhaseFixture.RedTwo);
        var add = ButtonOf(win, "Add row");
        var remove = ButtonOf(win, "Remove row");
        Assert.Equal("Add a run of the selected row's counter.", ToolTip.GetTip(add));
        Assert.Equal("Remove the selected row (not the All rows line).", ToolTip.GetTip(remove));

        win.SelectRow(0);
        Click(add);
        Pump();
        Assert.Equal(3, win.RowCount);
        Assert.Equal("Fuel for movement — 3 runs", win.Title);
        Assert.Equal(new[] { "Red 1", "Red 1", "Red 2" }, Enumerable.Range(0, 3).Select(i => win.CellText(i, 0)));
        Assert.Equal(1, win.SelectedRow);   // the new row is selected

        Click(remove);
        Pump();
        Assert.Equal(2, win.RowCount);
        Assert.Equal(new[] { "Red 1", "Red 2" }, Enumerable.Range(0, 2).Select(i => win.CellText(i, 0)));

        // The All rows line is not a row: nothing to remove there.
        win.SelectRow(-1);
        Click(remove);
        Pump();
        Assert.Equal(2, win.RowCount);
        Assert.Equal(FormulaWindowRules.SelectRowText, win.StatusText);

        // The last row stays.
        win.SelectRow(1);
        Click(remove);
        Pump();
        win.SelectRow(0);
        Click(remove);
        Pump();
        Assert.Equal(1, win.RowCount);
    }

    [AvaloniaFact]
    public void Menu_grid_no_apply_row_button()
    {
        var (w, _) = Make(Difficulty.Hardcore);
        var win = Menu(w, PhaseFixture.RedOne, PhaseFixture.RedTwo);
        Assert.False(win.HasButton("Apply row"));
        foreach (var label in new[] { "Calculate all", "Apply all", "Add row", "Remove row" }) Assert.True(win.HasButton(label), label);
    }

    [AvaloniaFact]
    public void Menu_grid_apply_all_status()
    {
        var (w, _) = Make(Difficulty.Hardcore, pack: MovedInput);
        var win = Menu(w, PhaseFixture.RedOne, PhaseFixture.RedTwo, PhaseFixture.RedOne);
        win.TypeCell(-1, 1, "1");
        win.TypeCell(-1, 2, "Clear");
        Click(ButtonOf(win, "Calculate all"));
        Pump();
        Click(ButtonOf(win, "Apply all"));
        Pump();
        Assert.Equal("Applied 3 run(s) as one step.", win.StatusText);
        Assert.True(Fuel(w, PhaseFixture.RedOne) < 40);
        Assert.True(Fuel(w, PhaseFixture.RedTwo) < 40);
        Assert.True(win.IsVisible);   // the grid stays open with its status
    }

    [AvaloniaFact]
    public void Menu_grid_nothing_applied_status()
    {
        var (w, _) = Make(Difficulty.Hardcore);
        var win = Menu(w, PhaseFixture.RedOne, PhaseFixture.RedTwo);
        Click(ButtonOf(win, "Apply all"));   // hardcore, nothing typed: the first row cannot apply
        Pump();
        Assert.Equal("Nothing applied: row 1 cannot be applied yet.", win.StatusText);
        Assert.Equal(40, Fuel(w, PhaseFixture.RedOne));
        Assert.Equal(40, Fuel(w, PhaseFixture.RedTwo));
    }

    [AvaloniaFact]
    public void Several_runs_opens_grid_with_typed_row()
    {
        var (w, _) = Make(Difficulty.Hardcore);
        var s = w.Session!;
        var win = FormulaWindows.Show(w, s, "f-fuel", PhaseFixture.RedOne, null)!;
        Pump();
        Assert.True(win.SeveralRunsButton.IsVisible);
        Assert.Equal("Open a batch grid for this counter: one row per run, applied in order as one step.", ToolTip.GetTip(win.SeveralRunsButton));
        win.Type("Terrain", "Sand");
        Pump();
        Click(win.SeveralRunsButton);
        Pump();
        var grid = Assert.Single(FormulaWindows.OpenBatchFor(s), g => g.IsMenuBatch);
        Assert.Equal(1, grid.RowCount);
        Assert.Equal("Red 1", grid.CellText(0, 0));
        Assert.Equal("Sand", grid.CellText(0, 2));
        Assert.Equal("Fuel for movement — 1 runs", grid.Title);
        Assert.True(win.IsVisible);
        // Closing the module's windows closes the grid with the rest.
        FormulaWindows.CloseAllFor(s);
        Pump();
        Assert.Empty(FormulaWindows.OpenBatchFor(s));
    }

    [AvaloniaFact]
    public void Several_runs_hidden_for_run_link()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var win = FormulaWindows.Show(w, w.Session!, "f-fuel", PhaseFixture.RedOne, new RunLink(run.Id, null))!;
        Pump();
        Assert.False(win.SeveralRunsButton.IsVisible);
    }

    [AvaloniaFact]
    public void Map_menu_calculate_on_selection_opens_grid()
    {
        var (w, _) = Make(Difficulty.Hardcore);
        var s = w.Session!;
        // The main window wires the hook into the map menu's services.
        var services = w.MapTabs.MenuServices!;
        Assert.NotNull(services.ShowFormulaBatch);
        var view = new MapView(s, s.Model.Maps[0]) { MenuServices = services };
        var holder = new Window { Width = 800, Height = 600, Content = view };
        holder.Show();
        Pump();
        var stack = s.Game!.State.Pieces[PhaseFixture.RedOne].StackId!;
        var sp = view.View.ToScreen(s.Game!.State.Stacks[stack].Pos);
        var at = new Point(sp.X, sp.Y);
        // A marquee around the stack selects both counters in it.
        holder.MouseDown(new Point(at.X - 40, at.Y - 40), MouseButton.Left);
        holder.MouseMove(new Point(at.X + 40, at.Y + 40));
        holder.MouseUp(new Point(at.X + 40, at.Y + 40), MouseButton.Left);
        Pump();
        Assert.Equal(new[] { PhaseFixture.RedOne, PhaseFixture.RedTwo }, view.SelectedPieceIds.Order());
        holder.MouseDown(at, MouseButton.Right);
        holder.MouseUp(at, MouseButton.Right);
        Pump();
        var menu = Assert.IsType<ContextMenu>(view.OpenMenu);
        var sub = menu.Items.OfType<MenuItem>().Single(m => MenuText.ToWinForms((string)m.Header!) == "Calculate on selection");
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => MenuText.ToWinForms((string)m.Header!) == "Calculate on stack");
        var entry = sub.Items.OfType<MenuItem>().Single(m => (string)m.Header! == "Fuel for movement");
        Assert.Equal("Fuel for movement", (string)entry.Header!);
        Assert.Empty(FormulaWindows.OpenBatchFor(s));
        entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Pump();
        var grid = Assert.Single(FormulaWindows.OpenBatchFor(s));
        Assert.True(grid.IsMenuBatch);
        Assert.Equal(2, grid.RowCount);
        Assert.Equal("Fuel for movement — 2 runs", grid.Title);
        holder.Close();
    }

    [AvaloniaFact]
    public void Pending_grid_unchanged()
    {
        var (w, run) = Make(Difficulty.Hardcore);
        var win = FormulaWindows.ShowBatch(w, w.Session!, w.Actions!, run.Id, null)!;
        Pump();
        Assert.False(win.IsMenuBatch);
        Assert.Equal(new[] { "Unit", "Moved", "Terrain", "Result", "Ledger", "Exceptions" }, win.Headers);
        Assert.Equal("Fuel for movement — 0 of 2 applied", win.Title);
        Assert.Equal(2, win.RowCount);
        Assert.Equal("Red 1", win.CellText(0, 0));
        foreach (var label in new[] { "Calculate all", "Apply all", "Apply row" }) Assert.True(win.HasButton(label), label);
        Assert.False(win.HasButton("Add row"));
        Assert.False(win.HasButton("Remove row"));
        win.SelectRow(1);
        Assert.Equal(1, win.SelectedRow);
    }
}
