using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Tools;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 3a task 7: the Inventory, Notes and Diagnostics windows, their menu items, toolbar buttons and the status-bar link. Headless on the Riverland demo.</summary>
[Collection("MacFlag")]
public sealed class ToolsWindowsTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-tools-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public ToolsWindowsTests() { ShortcutGestures.IsMac = false; Directory.CreateDirectory(_tmp); }

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file = "Riverland-Demo.vmod", [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the dispatcher loop (RunJobs alone runs no timers) in short slices until the condition holds or the time is up.</summary>
    static bool WaitFor(Func<bool> condition, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    MainWindow NewWindow()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    MainWindow Game(bool rules = true)
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(Demo()));
        w.StartSetup(null);
        if (rules) w.Session!.OpenRules(Demo("Riverland-Demo.qrules"));
        Pump();
        return w;
    }

    static void SetLedger(MainWindow w, string id, string prop, double value)
    {
        var st = w.Session!.Game!.State;
        var key = LedgerKey.Piece(id);
        w.Session.Game.Execute(new LedgerDelta(key, prop, Ledger.Get(st, key, prop) ?? 0, value, "manual", null, null, "test", null));
        Pump();
    }

    static int Sum(IEnumerable<InventoryRow> rows) => rows.Sum(r => r.Count);

    // ---------- inventory ----------

    [AvaloniaFact]
    public void Inventory_groups_and_totals()
    {
        var w = Game();
        w.ClickMenu("&Inventory");
        var inv = Assert.IsType<InventoryWindow>(w.InventoryTool);
        Assert.Equal("Inventory", inv.Title);
        Assert.Equal((520d, 440d), (inv.Width, inv.Height));
        Assert.Equal(new[] { "Piece name", "Map", "Marker", "Ledger property" }, inv.KeyBox.Items.Cast<string>());

        var s = w.Session!;
        var byName = InventoryQuery.Run(s.View!, s.Model, s.Rules, InventoryKey.Name, null, "");
        Assert.NotEmpty(byName);
        Assert.Equal(byName.Select(r => (r.Group, r.Count)), inv.Rows.Select(r => (r.Group, r.Count)));
        Assert.False(inv.KeyNameBox.IsEnabled);   // a name has no second choice

        // Group by a ledger property: the key combo lists the pack's piece properties, Total sums them.
        inv.SelectKey(InventoryKey.LedgerProperty);
        var names = InventoryRules.KeyNames(s.Rules, InventoryKey.LedgerProperty, s.Model, s.View);
        Assert.Contains("Fuel", names);
        Assert.Equal(names, inv.KeyNameBox.Items.Cast<string>());
        inv.KeyNameBox.SelectedItem = "Fuel";
        inv.Flush();
        var baseline = inv.Rows.Sum(r => r.Total ?? 0);   // the demo seeds some Fuel
        var pieces = s.Game!.State.Pieces.Keys.Where(id => Ledger.Get(s.Game.State, LedgerKey.Piece(id), "fuel") == null).Take(2).ToList();
        SetLedger(w, pieces[0], "fuel", 3);
        SetLedger(w, pieces[1], "fuel", 4.5);
        inv.Flush();
        Assert.Equal(baseline + 7.5, inv.Rows.Sum(r => r.Total ?? 0));
        var row = inv.Rows.First(r => r.Total is > 0);
        inv.HoverTotal(row.Group);
        Assert.Equal(InventoryRules.TotalTip(row, "Fuel", s.View!, s.Rules!, s.ViewFiltered), inv.TipText);
        Assert.False(string.IsNullOrEmpty(inv.TipText));

        // The filter applies after the 300 ms pause.
        inv.FilterBox.Text = "zzz-no-such-piece";
        Pump();
        Assert.True(inv.DebouncePending);
        Assert.True(WaitFor(() => inv.Rows.Count == 0));
        Assert.False(inv.DebouncePending);

        // One window only: the menu again brings the same one to the front.
        w.ClickMenu("&Inventory");
        Assert.Same(inv, w.InventoryTool);
    }

    [AvaloniaFact]
    public void Inventory_counts_only_visible_pieces()
    {
        var w = Game();
        var s = w.Session!;
        foreach (var id in s.Game!.State.Pieces.Keys.ToList()) SetLedger(w, id, "readiness", 1);   // Readiness is private to the owning side
        w.ClickMenu("&Inventory");
        var inv = w.InventoryTool!;
        inv.SelectKey(InventoryKey.LedgerProperty);
        inv.KeyNameBox.SelectedItem = "Readiness";
        inv.Flush();
        var solo = inv.Rows.Sum(r => r.Count);
        Assert.Equal(s.Game.State.Pieces.Count, solo);
        Assert.False(s.ViewFiltered);

        s.SetViewSides(new HashSet<string> { "north" });
        Pump();
        inv.Flush();
        var seen = InventoryQuery.Run(s.View!, s.Model, s.Rules, InventoryKey.LedgerProperty, "Readiness", "");
        Assert.True(s.ViewFiltered);
        Assert.True(seen.Sum(r => r.Count) < solo, "the other side's private values are not counted");
        Assert.Equal(seen.Select(r => (r.Group, r.Count, r.Total)), inv.Rows.Select(r => (r.Group, r.Count, r.Total)));
        var row = inv.Rows.First(r => r.Total != null);
        inv.HoverTotal(row.Group);
        Assert.EndsWith(ModuleSession.VisibleOnlyText, inv.TipText!.Split('\n')[0].Trim());
    }

    [AvaloniaFact]
    public void Inventory_double_click_cycles_pieces()
    {
        var w = Game();
        var s = w.Session!;
        var centred = new List<string>();
        var inv = new InventoryWindow(s, centred.Add);
        inv.Show(w);
        inv.SelectKey(InventoryKey.Map);
        Pump();
        var index = inv.Rows.ToList().FindIndex(r => r.PieceIds.Count >= 2);
        Assert.True(index >= 0, "the demo has a group with several pieces");
        var group = inv.Rows[index];
        inv.Select(index);
        inv.ActivateSelected();
        inv.ActivateSelected();
        inv.ActivateSelected();
        Assert.Equal(new[] { group.PieceIds[0], group.PieceIds[1], group.PieceIds[2 % group.PieceIds.Count] }, centred);

        // A real double click on the group's row does the same.
        centred.Clear();
        var item = inv.List.ContainerFromIndex(index)!;
        var at = item.TranslatePoint(new Avalonia.Point(10, item.Bounds.Height / 2), inv)!.Value;
        inv.MouseDown(at, MouseButton.Left);
        inv.MouseUp(at, MouseButton.Left);
        inv.MouseDown(at, MouseButton.Left);
        inv.MouseUp(at, MouseButton.Left);
        Assert.Equal(new[] { group.PieceIds[3 % group.PieceIds.Count] }, centred);   // the cycle goes on where it was
        inv.Close();
    }

    // ---------- notes ----------

    static int NoteEntries(MainWindow w) => w.Session!.Game!.State.Log.Count(e => e.Kind == LogKind.Note);

    [AvaloniaFact]
    public void Notes_commit_after_debounce()
    {
        var w = Game();
        w.ClickMenu("&Notes");
        var notes = Assert.IsType<NotesWindow>(w.NotesTool);
        Assert.Equal("Notes", notes.Title);
        Assert.Equal((420d, 360d), (notes.Width, notes.Height));
        Assert.True(notes.Box.AcceptsReturn);
        Assert.Equal(TimeSpan.FromSeconds(1), notes.CommitDelay);   // as WinForms; shortened for the test
        notes.CommitDelay = TimeSpan.FromMilliseconds(80);

        var before = NoteEntries(w);
        notes.Type("Bridge at 0304 is out.");
        Pump();
        Assert.True(notes.CommitPending);
        Assert.NotEqual("Bridge at 0304 is out.", w.Session!.Game!.State.Notes);   // not yet
        Assert.True(WaitFor(() => w.Session.Game.State.Notes == "Bridge at 0304 is out."));
        Assert.False(notes.CommitPending);
        Assert.Equal(before + 1, NoteEntries(w));

        // Unchanged text never commits again.
        notes.FlushNow();
        Assert.Equal(before + 1, NoteEntries(w));
    }

    [AvaloniaFact]
    public void Notes_flushed_on_close_and_module_change()
    {
        var w = Game();
        w.ClickMenu("&Notes");
        var notes = w.NotesTool!;
        notes.Type("Typed just before closing.");
        var game = w.Session!.Game!;
        notes.Close();
        Assert.Equal("Typed just before closing.", game.State.Notes);
        Pump();
        Assert.Null(w.NotesTool);

        // Pending text when another module (a replaced session) is opened: committed into the old game first, the window closes with it.
        w.ClickMenu("&Notes");
        notes = w.NotesTool!;
        notes.Type("Typed just before the module changes.");
        var old = w.Session!.Game!;
        Assert.True(w.OpenModule(Demo()));
        Assert.Equal("Typed just before the module changes.", old.State.Notes);
        Assert.Null(w.NotesTool);

        // And before a new game replaces the game of the same module.
        w.StartSetup(null);
        w.ClickMenu("&Notes");
        w.NotesTool!.Type("Typed just before New Game.");
        var first = w.Session!.Game!;
        var entries = first.State.Log.Count(e => e.Kind == LogKind.Note);
        w.StartSetup(null);
        Assert.Equal("Typed just before New Game.", first.State.Notes);
        Assert.Equal(entries + 1, first.State.Log.Count(e => e.Kind == LogKind.Note));
        Assert.NotSame(first, w.Session.Game);
    }

    [AvaloniaFact]
    public void Notes_reload_when_not_focused()
    {
        var w = Game();
        w.ClickMenu("&Notes");
        var notes = w.NotesTool!;

        // Not focused, nothing pending: the box follows the game.
        notes.FocusedForTest = false;
        w.Actions!.SetNotes("from elsewhere");
        Pump();
        Assert.Equal("from elsewhere", notes.Box.Text);

        // Focused: the box is left alone, even without pending text.
        notes.FocusedForTest = true;
        w.Actions.SetNotes("while focused");
        Pump();
        Assert.Equal("from elsewhere", notes.Box.Text);

        // Not focused but with pending text of its own: never replaced.
        notes.FocusedForTest = false;
        notes.Type("my own pending text");
        Pump();
        Assert.True(notes.CommitPending);
        w.Actions.SetNotes("from elsewhere again");
        Pump();
        Assert.Equal("my own pending text", notes.Box.Text);
        notes.FlushNow();
        Assert.Equal("my own pending text", w.Session!.Game!.State.Notes);

        // A new game shows its own (empty) notes.
        w.StartSetup(null);
        Pump();
        Assert.Equal("", notes.Box.Text);
    }

    [AvaloniaFact]
    public void Notes_commit_on_focus_loss()
    {
        var w = Game();
        w.ClickMenu("&Notes");
        var notes = w.NotesTool!;
        // A second control in the window to move the focus to (the notes box is the window's only control otherwise).
        var other = new TextBox();
        var panel = new StackPanel();
        notes.Content = null;
        panel.Children.Add(notes.Box);
        panel.Children.Add(other);
        notes.Content = panel;
        Pump();
        notes.Box.Focus();
        Pump();
        Assert.True(notes.Box.IsFocused);
        notes.Type("committed by leaving the box");
        Pump();
        Assert.True(notes.CommitPending);
        Assert.NotEqual("committed by leaving the box", w.Session!.Game!.State.Notes);
        other.Focus();
        Pump();
        Assert.False(notes.Box.IsFocused);
        Assert.Equal("committed by leaving the box", w.Session.Game.State.Notes);
        Assert.False(notes.CommitPending);
    }

    // ---------- diagnostics ----------

    [AvaloniaFact]
    public void Diagnostics_lists_and_filters()
    {
        var w = Game(false);
        var log = w.Session!.Log;
        log.Add(DiagnosticSeverity.Info, "load", "info entry");
        log.Add(DiagnosticSeverity.Warning, "rules", "warning entry", "ctx");
        log.Add(DiagnosticSeverity.Error, "net", "error entry");
        w.ClickMenu("&Diagnostics");
        var d = Assert.IsType<DiagnosticsWindow>(w.DiagnosticsTool);
        Assert.Equal("Diagnostics", d.Title);
        Assert.Equal(new[] { "Severity", "Category", "Message", "Context" }, DiagnosticsWindow.ColumnHeaders);
        Assert.Equal(new double[] { 70, 90, 520, 160 }, DiagnosticsWindow.ColumnWidths);
        Assert.Equal(DiagnosticsView.FilterNames, d.FilterBox.Items.Cast<string>());
        Assert.Equal(log.Items, d.Shown);

        d.FilterBox.SelectedIndex = 1;
        Assert.Equal(log.Items.Where(x => x.Severity >= DiagnosticSeverity.Warning), d.Shown);
        d.FilterBox.SelectedIndex = 2;
        Assert.All(d.Shown, x => Assert.Equal(DiagnosticSeverity.Error, x.Severity));
        Assert.Contains(d.Shown, x => x.Message == "error entry");
        Assert.DoesNotContain(d.Shown, x => x.Message == "warning entry");

        // One window: the menu again, and the toolbar button, bring it to the front.
        w.Toolbar.Find("tool.diagnostics")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Same(d, w.DiagnosticsTool);
        d.Close();
        Pump();
        Assert.Null(w.DiagnosticsTool);
    }

    [AvaloniaFact]
    public void Diagnostics_live_update_from_other_thread()
    {
        var w = Game(false);
        w.ClickMenu("&Diagnostics");
        var d = w.DiagnosticsTool!;
        d.FilterBox.SelectedIndex = 1;   // warnings and errors
        var log = w.Session!.Log;
        var task = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++) log.Add(DiagnosticSeverity.Warning, "thread", "from another thread " + i);
            log.Add(DiagnosticSeverity.Info, "thread", "info is filtered out");
        });
        Assert.True(WaitFor(() => d.Shown.Count(x => x.Category == "thread") == 20));
        task.Wait();
        Assert.DoesNotContain(d.Shown, x => x.Message == "info is filtered out");
        // Closed: no more updates, and nothing throws back into the logger.
        d.Close();
        Pump();
        log.Add(DiagnosticSeverity.Error, "thread", "after close");
        Pump();
    }

    [AvaloniaFact]
    public void Diagnostics_repopulate_never_duplicates_an_entry_logged_meanwhile()
    {
        var w = Game(false);
        var log = w.Session!.Log;
        w.ClickMenu("&Diagnostics");
        var d = w.DiagnosticsTool!;
        // Logged between the queue being looked at and the snapshot: in both the snapshot and the queue, shown once.
        d.BeforeSnapshot = () => log.Add(DiagnosticSeverity.Warning, "race", "before snapshot");
        // Logged right after the snapshot: only in the queue, shown once, after the snapshot's entries.
        d.AfterSnapshot = () => log.Add(DiagnosticSeverity.Warning, "race", "after snapshot");
        d.RepopulateForTest();
        d.BeforeSnapshot = d.AfterSnapshot = null;
        Assert.Single(d.Shown, x => x.Message == "before snapshot");
        Assert.Single(d.Shown, x => x.Message == "after snapshot");
        Pump();
        Assert.Single(d.Shown, x => x.Message == "before snapshot");
        Assert.Single(d.Shown, x => x.Message == "after snapshot");
        Assert.Equal(log.Items.Count, d.Shown.Count);
    }

    [AvaloniaFact]
    public void Diagnostics_drain_never_duplicates_an_entry_already_shown()
    {
        var w = Game(false);
        var log = w.Session!.Log;
        // A logger whose event reaches the window late: the entry is in the log (and so in a snapshot) before the window's handler queues it.
        using var inHandler = new ManualResetEventSlim(false);
        using var go = new ManualResetEventSlim(false);
        log.Added += d => { if (d.Message == "slow") { inHandler.Set(); go.Wait(10000); } };   // runs before the window's handler
        w.ClickMenu("&Diagnostics");
        var dw = w.DiagnosticsTool!;
        var logger = new Thread(() => log.Add(DiagnosticSeverity.Warning, "race", "slow")) { IsBackground = true };
        logger.Start();
        Assert.True(inHandler.Wait(10000));
        dw.RepopulateForTest();   // the snapshot already has it; nothing is queued yet
        Assert.Single(dw.Shown, x => x.Message == "slow");
        go.Set();
        logger.Join();   // now the window's handler queued it and posted a drain
        Pump();
        Assert.Single(dw.Shown, x => x.Message == "slow");
        Assert.Equal(log.Items.Count, dw.Shown.Count);
    }

    [AvaloniaFact]
    public async Task Diagnostics_copy_all()
    {
        var w = Game(false);
        var log = w.Session!.Log;
        log.Add(DiagnosticSeverity.Warning, "rules", "warning entry", "ctx");
        log.Add(DiagnosticSeverity.Error, "net", "error entry");
        w.ClickMenu("&Diagnostics");
        var d = w.DiagnosticsTool!;
        d.FilterBox.SelectedIndex = 2;   // errors only
        await d.CopyAllAsync();
        var clip = TopLevel.GetTopLevel(d)!.Clipboard!;
        var text = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clip);
        Assert.Equal(DiagnosticsView.CopyText(DiagnosticsView.Apply(log.Items, DiagnosticsFilter.ErrorsOnly)), text);
        Assert.Contains("Error\tnet\terror entry\t", text);
        Assert.DoesNotContain("warning entry", text);
    }

    [AvaloniaFact]
    public void Status_link_opens_diagnostics()
    {
        var w = Game(false);
        w.SetStatus("");
        var log = w.Session!.Log;
        log.Add(DiagnosticSeverity.Info, "load", "just info");
        Pump();
        Assert.False(w.StatusIsLink);
        Assert.Equal("", w.StatusText);

        log.Add(DiagnosticSeverity.Warning, "rules", "a warning");
        Pump();
        Assert.True(w.StatusIsLink);
        Assert.Equal(DiagnosticsView.StatusLinkText(log.Items.Count(x => x.Severity == DiagnosticSeverity.Warning), log.Items.Count(x => x.Severity > DiagnosticSeverity.Warning)), w.StatusText);
        Assert.EndsWith("warning(s)/error(s) - click here to view diagnostics", w.StatusText);

        Assert.Null(w.DiagnosticsTool);
        var msg = w.FindControl<TextBlock>("SbMessage")!;
        var at = msg.TranslatePoint(new Avalonia.Point(5, msg.Bounds.Height / 2), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Pump();
        Assert.NotNull(w.DiagnosticsTool);
    }

    [AvaloniaFact]
    public void Status_link_survives_set_status_as_winforms()
    {
        // WinForms SetStatus only replaces the text; the link flag stays and a click still opens the diagnostics.
        var w = Game(false);
        w.Session!.Log.Add(DiagnosticSeverity.Error, "x", "boom");
        Pump();
        Assert.True(w.StatusIsLink);
        w.SetStatus("some other message");
        Assert.Equal("some other message", w.StatusText);
        Assert.True(w.StatusIsLink);
        var msg = w.FindControl<TextBlock>("SbMessage")!;
        var at = msg.TranslatePoint(new Avalonia.Point(5, msg.Bounds.Height / 2), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Pump();
        Assert.NotNull(w.DiagnosticsTool);
    }

    [AvaloniaFact]
    public void Ctrl_D_opens_diagnostics()
    {
        var w = NewWindow();
        // Needs a module (the WinForms gate): nothing before one is open.
        w.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, null);
        Assert.Null(w.DiagnosticsTool);
        Assert.True(w.OpenModule(Demo()));
        Pump();
        var item = w.FindMenuItem("&Diagnostics")!;
        Assert.Equal(new KeyGesture(Key.D, KeyModifiers.Control), item.InputGesture);
        w.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, null);
        Pump();
        Assert.NotNull(w.DiagnosticsTool);
    }

    // ---------- menus and toolbar ----------

    [AvaloniaFact]
    public void Toolbar_has_stage3_buttons()
    {
        var w = NewWindow();
        var ids = w.Toolbar.Buttons.Select(b => b.Id).ToList();
        // WinForms order (MainForm.cs): Diagnostics, Notes, Overview, Inventory; Overview is checked in OverviewAndExportTests.
        Assert.True(ids.IndexOf("tool.diagnostics") > ids.IndexOf("tool.saveGame"));
        Assert.True(ids.IndexOf("tool.notes") > ids.IndexOf("tool.diagnostics"));
        Assert.True(ids.IndexOf("tool.inventory") > ids.IndexOf("tool.notes"));
        Assert.True(ids.IndexOf("tool.los") > ids.IndexOf("tool.inventory"));
        Assert.Equal(new[] { "Diagnostics", "Notes", "Inventory" }, new[] { "tool.diagnostics", "tool.notes", "tool.inventory" }.Select(i => w.Toolbar.Buttons.First(b => b.Id == i).Text));

        // Gates: Diagnostics needs a module, Notes and Inventory a game; menu items follow the same rules.
        Assert.False(w.Toolbar.Find("tool.diagnostics")!.IsEnabled);
        Assert.False(w.FindMenuItem("&Diagnostics")!.IsEnabled);
        Assert.True(w.OpenModule(Demo()));
        Assert.True(w.Toolbar.Find("tool.diagnostics")!.IsEnabled);
        Assert.True(w.FindMenuItem("&Diagnostics")!.IsEnabled);
        Assert.False(w.Toolbar.Find("tool.notes")!.IsEnabled);
        Assert.False(w.Toolbar.Find("tool.inventory")!.IsEnabled);
        Assert.False(w.FindMenuItem("&Inventory")!.IsEnabled);
        Assert.False(w.FindMenuItem("&Notes")!.IsEnabled);
        w.StartSetup(null);
        Pump();
        foreach (var id in new[] { "tool.notes", "tool.inventory" }) Assert.True(w.Toolbar.Find(id)!.IsEnabled, id);
        Assert.True(w.FindMenuItem("&Inventory")!.IsEnabled);
        Assert.True(w.FindMenuItem("&Notes")!.IsEnabled);

        // The buttons open the windows.
        w.Toolbar.Find("tool.notes")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        w.Toolbar.Find("tool.inventory")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(w.NotesTool);
        Assert.NotNull(w.InventoryTool);
        Assert.Equal(new[] { "&Rules Workbench", "&Inventory", "&Notes" }, w.MenuLabels()["&Tools"].Take(3));
    }
}
