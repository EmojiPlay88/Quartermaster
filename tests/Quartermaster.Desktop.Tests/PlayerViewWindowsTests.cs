using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Outliner;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using LedgerWindow = Quartermaster.Desktop.Ledgers.LedgerWindow;
using WorkbenchWindow = Quartermaster.Desktop.Workbench.WorkbenchWindow;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// Hot-seat TEST games for the stage 5b task 4 tests: the phase fixture (Combat given to Blue) plus two side-owned maps, "Red Map" with
/// "Red Scout" (sheet row Radar) and "Blue Map" with "Blue Scout" (sheet row Sonar), alerts "Red scouts" / "Blue scouts", and one private
/// log line per side. Players Anna (Red) and Ben (Blue). Invented content only; headless, fake dialogs, temp roots.
/// </summary>
internal sealed class HotSeatGames : IDisposable
{
    public const string RedScout = "rs", BlueScout = "bs";
    public const string RedLine = "red secret working 17", BlueLine = "blue secret working 42";
    public static readonly PlayerDef Anna = new("p1", "Anna", new[] { "red" });
    public static readonly PlayerDef Ben = new("p2", "Ben", new[] { "blue" });

    readonly string _tmp;
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();

    public HotSeatGames(string prefix) => _tmp = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    public FakeDialogs Dialogs { get; } = new();

    public void Dispose()
    {
        foreach (var w in _windows)
        {
            if (w.Session is { } s) FormulaWindows.CloseAllFor(s);
            w.ForceClose();
        }
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the dispatcher loop (timers too) in short slices until the condition holds.</summary>
    public static void PumpUntil(Func<bool> done, int seconds = 10)
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

    static string Piece(string id, string name, string map, int x, string extra) =>
        "+/" + SequenceEncoder.Encode(new[] { id, $"propertysheet;0Fuel~0{extra};Sheet;\tpiece;;;unit.svg;{name}", $"40~3\t{map};{x};50;1;0" }, '/');

    static string MapXml(string name) =>
        $"<VASSAL.build.module.Map mapName=\"{name}\"><VASSAL.build.module.map.BoardPicker>" +
        $"<VASSAL.build.module.map.boardPicker.Board image=\"board.png\" name=\"{name} Board\" reversible=\"false\"/>" +
        "</VASSAL.build.module.map.BoardPicker></VASSAL.build.module.Map>";

    FixtureModule Module()
    {
        var fx = PhaseFixture.Module()
            .EditBuildFile(x => x.Replace("</VASSAL.build.GameModule>", MapXml("Red Map") + MapXml("Blue Map") + "</VASSAL.build.GameModule>"))
            .AddSave("s.vsav", new[]
            {
                "begin_save", "",
                Piece(PhaseFixture.RedOne, "Red 1", "Main Map", 30, "Fuel"),
                Piece(PhaseFixture.RedTwo, "Red 2", "Main Map", 80, "Fuel"),
                Piece(PhaseFixture.BlueOne, "Blue 1", "Main Map", 130, "Fuel"),
                Piece(RedScout, "Red Scout", "Red Map", 30, "Radar"),
                Piece(BlueScout, "Blue Scout", "Blue Map", 30, "Sonar"),
                "end_save",
            });
        _fixtures.Add(fx);
        return fx;
    }

    public MainWindow NewWindow()
    {
        var w = new MainWindow(Dialogs, Path.Combine(_tmp, "cache" + _windows.Count), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    /// <summary>A game with players Anna and Ben (unless <paramref name="players"/> is false); with <paramref name="atScreen"/> Anna has
    /// taken over (the first handover, raised when the game became multi-player, was continued).</summary>
    public MainWindow Game(Difficulty difficulty = Difficulty.FullyAuto, bool atScreen = true, bool players = true)
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(Module().Build()));
        var s = w.Session!;
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        w.StartSetup(0, difficulty);
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            return p with
            {
                Properties = p.Properties.Select(x => x.Id == "fuel" ? x with { Outliner = OutlinerAggregate.Sum } : x).ToList(),
                Phases = p.Phases.Select(x => x.Id != "ph-combat" ? x : x with { SideId = "blue" }).ToList(),
                MapOwners = new() { new MapOwner { MapId = "Red Map", SideId = "red" }, new MapOwner { MapId = "Blue Map", SideId = "blue" } },
                Alerts = new()
                {
                    new AlertDef { Id = "a-rs", Name = "Red scouts", Condition = "startsWith(name(), \"Red Scout\")", Severity = AlertSeverity.Warning },
                    new AlertDef { Id = "a-bs", Name = "Blue scouts", Condition = "startsWith(name(), \"Blue Scout\")", Severity = AlertSeverity.Warning },
                },
            };
        });
        var log = s.Game!.State.Log;
        log.Add(new LogEntry(9001, LogKind.Ledger, RedLine, "local", null, null, DateTimeOffset.UtcNow, SideId: "red"));
        log.Add(new LogEntry(9002, LogKind.Ledger, BlueLine, "local", null, null, DateTimeOffset.UtcNow, SideId: "blue"));
        Pump();
        if (!players) return w;
        s.Game.Execute(new SetPlayers(new[] { Anna, Ben }));
        Pump();
        Assert.True(w.HandoverShowing);
        if (atScreen) Continue(w);
        return w;
    }

    public static void Continue(MainWindow w)
    {
        w.Handover!.ContinueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    /// <summary>Start (Weather), Supply, Movement (Red), then Combat (Blue): the handover to Ben comes up.</summary>
    public static void ToCombat(MainWindow w)
    {
        for (var i = 0; i < 4; i++) w.Actions!.NextPhase();
        Pump();
    }

    /// <summary>Anna's view, then the handover to Ben (Combat) and Continue.</summary>
    public static void ToBen(MainWindow w)
    {
        ToCombat(w);
        Assert.True(w.HandoverShowing);
        Continue(w);
        Assert.Equal(new[] { "blue" }, w.Session!.ViewSides!.ToArray());
    }

    /// <summary>A deliberate swap from the status-bar menu, then Continue.</summary>
    public static void SwitchTo(MainWindow w, PlayerDef p)
    {
        var item = w.PlayerMenuItems().Single(i => ((string)i.Header!).StartsWith(p.Name + " (", StringComparison.Ordinal));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        Assert.True(w.HandoverShowing);
        Continue(w);
        Assert.Equal(p.Sides, w.Session!.ViewSides!.ToArray());
    }
}

/// <summary>Port stage 5b task 4: every window follows the player at the screen. Each test reads the window as Anna (Red), then after the
/// handover to Ben (Blue): the Blue piece / value / line absent before and present after, the Red one the reverse. The handover is mostly
/// the status-bar switch, which changes only the view (no game command), so a window passes only through its own ViewChanged handling;
/// nothing is force-reloaded after it.</summary>
public sealed class PlayerViewWindowsTests : IDisposable
{
    readonly HotSeatGames _games = new("qm-playerview-");

    public void Dispose() => _games.Dispose();

    static void Pump() => HotSeatGames.Pump();

    static IReadOnlyList<string> MapNames(MainWindow w) => w.Maps.Views.Select(v => v.Map.Name).ToList();

    static IReadOnlyList<string> TreeMapNames(MainWindow w) =>
        w.ModuleTree.Nodes.Select(i => i.Tag).OfType<WindowTreeNode>().Where(n => n.Kind == WindowNodeKind.Map).Select(n => ((MapDef)n.Ref!).Name).ToList();

    [AvaloniaFact]
    public void Map_tabs_and_module_tree_follow_current_player()
    {
        var w = _games.Game();
        Assert.Contains("Red Map", MapNames(w));
        Assert.DoesNotContain("Blue Map", MapNames(w));
        Assert.Contains("Red Map", TreeMapNames(w));
        Assert.DoesNotContain("Blue Map", TreeMapNames(w));
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        Assert.Contains("Blue Map", MapNames(w));
        Assert.DoesNotContain("Red Map", MapNames(w));
        Assert.Contains("Blue Map", TreeMapNames(w));
        Assert.DoesNotContain("Red Map", TreeMapNames(w));
        Assert.Contains("Main Map", MapNames(w));
    }

    /// <summary>Moves the pointer off the map, then onto the piece, until its popup is up. The alert bar settles first and the point is
    /// taken afresh on each try: a chip appearing moves the map down.</summary>
    static CounterPopup Hover(MainWindow w, string pieceId)
    {
        var view = w.Maps.Views.Single(v => v.Map.Name == "Main Map");
        w.Maps.Activate(view);
        view.HoverDelayMs = 30;
        w.FlushAlerts();
        Pump();
        bool Shown() => view.Popup?.Layout.Details.FirstOrDefault()?.PieceId == pieceId;
        var at = default(Point);
        for (var attempt = 0; attempt < 5 && !Shown(); attempt++)
        {
            w.UpdateLayout();
            var st = w.Session!.View!;
            var sp = view.View.ToScreen(st.Stacks[st.Pieces[pieceId].StackId!].Pos);
            at = view.TranslatePoint(new Point(sp.X, sp.Y), w)!.Value;
            w.MouseMove(new Point(1, 1));
            Pump();
            w.MouseMove(at);
            var until = DateTime.UtcNow.AddSeconds(2);
            while (!Shown() && DateTime.UtcNow < until)
            {
                using var slice = new CancellationTokenSource(10);
                Dispatcher.UIThread.MainLoop(slice.Token);
                Pump();
            }
        }
        Assert.True(Shown(), $"the popup did not open (at {at}, hover {view.HoverStackId}, popup of {view.Popup?.Layout.Details.FirstOrDefault()?.PieceId})");
        return view.Popup!;
    }

    [AvaloniaFact]
    public void Map_hover_popup_follows_view_and_closes_on_handover()
    {
        var w = _games.Game();
        Assert.Contains("Fuel: 40", Hover(w, PhaseFixture.RedOne).Layout.Lines);
        var popup = Hover(w, PhaseFixture.BlueOne);
        Assert.DoesNotContain(popup.Layout.Lines, l => l.StartsWith("Fuel", StringComparison.Ordinal));
        var view = w.Maps.Views.Single(v => v.Map.Name == "Main Map");
        HotSeatGames.ToCombat(w);
        Assert.True(w.HandoverShowing);
        Assert.Null(view.Popup);   // nothing of Anna's stays up behind the screen
        HotSeatGames.Continue(w);
        Assert.Contains("Fuel: 40", Hover(w, PhaseFixture.BlueOne).Layout.Lines);
        Assert.DoesNotContain(Hover(w, PhaseFixture.RedOne).Layout.Lines, l => l.StartsWith("Fuel", StringComparison.Ordinal));
    }

    static int Col(LedgerWindow lw, string header) => lw.Headers.ToList().IndexOf(header);

    static string? FuelOf(LedgerWindow lw, string name)
    {
        var i = lw.Rows.Select((r, k) => (r, k)).FirstOrDefault(x => x.r.Row.Name == name);
        return i.r == null ? null : lw.CellText(i.k, Col(lw, "Fuel"));
    }

    [AvaloniaFact]
    public void Ledger_follows_current_player()
    {
        var w = _games.Game();
        var lw = w.OpenLedger()!;
        lw.ShowEmptyButton.IsChecked = true;
        lw.Flush();
        var names = lw.Rows.Select(r => r.Row.Name).ToList();
        Assert.Contains("Red Scout", names);
        Assert.DoesNotContain("Blue Scout", names);
        Assert.Equal("40", FuelOf(lw, "Red 1"));
        Assert.Equal("", FuelOf(lw, "Blue 1"));

        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        lw.Flush();
        names = lw.Rows.Select(r => r.Row.Name).ToList();
        Assert.Contains("Blue Scout", names);
        Assert.DoesNotContain("Red Scout", names);
        Assert.Equal("40", FuelOf(lw, "Blue 1"));
        Assert.Equal("", FuelOf(lw, "Red 1"));
        Assert.All(lw.Rows.Where(r => r.PieceId != null), r => Assert.True(w.Session!.View!.Pieces.ContainsKey(r.PieceId!)));
    }

    [AvaloniaFact]
    public void Ledger_tabs_follow_current_player()
    {
        var w = _games.Game();
        var lw = w.OpenLedger()!;
        lw.ShowEmptyButton.IsChecked = true;
        var keys = lw.TabKeys;
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            lw.SelectTab(key);
            lw.Flush();
            Assert.DoesNotContain(lw.Rows, r => r.Row.Name == "Blue Scout" || r.PieceId == HotSeatGames.BlueScout);
        }
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        foreach (var key in keys)
        {
            lw.SelectTab(key);
            lw.Flush();
            Assert.DoesNotContain(lw.Rows, r => r.Row.Name == "Red Scout" || r.PieceId == HotSeatGames.RedScout);
        }
    }

    [AvaloniaFact]
    public void Counter_thumbnails_and_preview_follow_view()
    {
        var w = _games.Game();
        var lw = w.OpenLedger()!;
        lw.ShowEmptyButton.IsChecked = true;
        lw.Flush();
        var size = new PixelSize(24, 18);
        HotSeatGames.PumpUntil(() => lw.Thumbnails.Get(HotSeatGames.RedScout, size, null) != null);
        Assert.Null(lw.Thumbnails.Get(HotSeatGames.BlueScout, size, null));
        lw.Preview.Show(lw, new Point(10, 10), HotSeatGames.RedScout, "Red Scout");
        HotSeatGames.PumpUntil(() => lw.Preview.IsOpen);

        w.PlayerMenuItems().Single(i => (string)i.Header! == "Ben (Blue)").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        Assert.True(w.HandoverShowing);
        Assert.False(lw.Preview.IsOpen);   // Anna's counter does not stay up over the handover screen
        HotSeatGames.Continue(w);
        lw.Flush();
        Assert.False(lw.Preview.IsOpen);
        Assert.Null(lw.Thumbnails.Get(HotSeatGames.RedScout, size, null));   // cached, but no longer the viewer's to see
        HotSeatGames.PumpUntil(() => lw.Thumbnails.Get(HotSeatGames.BlueScout, size, null) != null);
        lw.Preview.Show(lw, new Point(10, 10), HotSeatGames.RedScout, "Red Scout");
        Pump();
        Assert.False(lw.Preview.IsOpen);
    }

    [AvaloniaFact]
    public void Inventory_follows_current_player()
    {
        var w = _games.Game();
        w.ClickMenu("&Inventory");
        var inv = w.InventoryTool!;
        inv.SelectKey(InventoryKey.Map);
        inv.Flush();
        Assert.Contains(inv.Rows, r => r.Group == "Red Map");
        Assert.DoesNotContain(inv.Rows, r => r.Group == "Blue Map");
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        HotSeatGames.PumpUntil(() => inv.Rows.Any(r => r.Group == "Blue Map"));   // its own debounced refresh, no forced reload
        Assert.DoesNotContain(inv.Rows, r => r.Group == "Red Map");
    }

    [AvaloniaFact]
    public void Log_redacts_other_sides_private_lines()
    {
        var w = _games.Game();
        var p = w.Log!;
        p.Reload();
        Assert.Contains(p.Rows, r => r.Text == HotSeatGames.RedLine);
        Assert.DoesNotContain(p.Rows, r => (r.Text ?? "").Contains("blue secret"));
        Assert.Contains(p.Rows, r => r.Text == "Blue ledger changed");
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        HotSeatGames.PumpUntil(() => p.Rows.Any(r => r.Text == HotSeatGames.BlueLine));
        Assert.DoesNotContain(p.Rows, r => (r.Text ?? "").Contains("red secret"));
        Assert.Contains(p.Rows, r => r.Text == "Red ledger changed");
        Assert.DoesNotContain("red secret", p.CopyText());
    }

    static int RowOf(OutlinerPanel o, string name) => o.RowNodes.Select((n, i) => (n, i)).FirstOrDefault(x => x.n.Name == name) is var r && r.n != null ? r.i : -1;

    [AvaloniaFact]
    public void Outliner_follows_current_player()
    {
        var w = _games.Game();
        w.ClickMenu("&Outliner");
        var o = w.Outliner!;
        w.FlushAlerts();
        Pump();
        o.Flush();
        o.ExpandAll();
        Assert.True(RowOf(o, "Red Scout") >= 0);
        Assert.Equal(-1, RowOf(o, "Blue Scout"));
        Assert.Equal("", o.CellText(RowOf(o, "Blue 1"), 3));
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        HotSeatGames.PumpUntil(() => { o.ExpandAll(); return RowOf(o, "Blue Scout") >= 0; });   // the alert hub's refresh, no forced reload
        Assert.Equal(-1, RowOf(o, "Red Scout"));
        Assert.Equal("", o.CellText(RowOf(o, "Red 1"), 3));
        Assert.Equal("40", o.CellText(RowOf(o, "Blue 1"), 3));
    }

    [AvaloniaFact]
    public void Phase_panel_and_strip_follow_current_player()
    {
        var w = _games.Game();
        HotSeatGames.ToBen(w);   // Combat, Blue's: Ben sees its checklist and may go on
        HotSeatGames.PumpUntil(() => w.Phases.ChecklistTexts.Count == 1 && w.Strip.ItemTexts.Any(t => t.EndsWith("Combat", StringComparison.Ordinal)));
        Assert.Equal(new[] { "Resolve attacks" }, w.Phases.ChecklistTexts);
        Assert.True(w.Phases.NextEnabled);
        Assert.Contains(w.Strip.ItemTexts, t => t.EndsWith("Combat", StringComparison.Ordinal));
        Assert.DoesNotContain(w.Strip.ItemTexts, t => t.Contains(" is playing ", StringComparison.Ordinal));
        HotSeatGames.SwitchTo(w, HotSeatGames.Anna);
        HotSeatGames.PumpUntil(() => w.Phases.ChecklistTexts.Count == 0 && w.Strip.ItemTexts.Contains("Blue is playing Combat"));
        Assert.Empty(w.Phases.ChecklistTexts);
        Assert.False(w.Phases.NextEnabled);
        Assert.Equal("", w.Phases.InstructionsText);
        Assert.Contains("Blue is playing Combat", w.Strip.ItemTexts);   // another side's phase: who plays it, none of its decisions
    }

    [AvaloniaFact]
    public void Alert_bar_follows_current_player()
    {
        var w = _games.Game();
        w.FlushAlerts();
        Pump();
        Assert.Contains(w.Alerts.ChipTexts, t => t.StartsWith("Red scouts", StringComparison.Ordinal));
        Assert.DoesNotContain(w.Alerts.ChipTexts, t => t.StartsWith("Blue scouts", StringComparison.Ordinal));
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        HotSeatGames.PumpUntil(() => w.Alerts.ChipTexts.Any(t => t.StartsWith("Blue scouts", StringComparison.Ordinal)));   // the watch's own debounce
        Assert.DoesNotContain(w.Alerts.ChipTexts, t => t.StartsWith("Red scouts", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Formula_window_closes_and_refuses_other_side()
    {
        var w = _games.Game();
        var s = w.Session!;
        Assert.NotNull(FormulaWindows.Show(w, s, "f-water", PhaseFixture.RedOne, null));
        Pump();
        Assert.Null(FormulaWindows.Show(w, s, "f-water", PhaseFixture.BlueOne, null));
        HotSeatGames.ToBen(w);
        Assert.Empty(FormulaWindows.OpenFor(s));
        Assert.Null(FormulaWindows.Show(w, s, "f-water", PhaseFixture.RedOne, null));
        Assert.Equal("That unit belongs to a side you don't hold.", w.StatusText);
        Assert.NotNull(FormulaWindows.Show(w, s, "f-water", PhaseFixture.BlueOne, null));
    }

    [AvaloniaFact]
    public void Batch_grid_closes_on_handover()
    {
        var w = _games.Game(Difficulty.Hardcore);
        var s = w.Session!;
        var c = w.PhaseControl!;
        c.Start(); c.Advance();   // Supply (Red): Daily water for the red units
        Pump();
        var run = s.Game!.State.Pending.Single(r => r.FormulaId == "f-water");
        var win = FormulaWindows.ShowBatch(w, s, w.Actions!, run.Id, null)!;
        Pump();
        Assert.Contains(Enumerable.Range(0, win.RowCount), r => win.CellText(r, 0) == "Red Scout");
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        Assert.Empty(FormulaWindows.OpenBatchFor(s));
        Assert.False(win.IsVisible);
    }

    WorkbenchWindow Bench(MainWindow w, Func<WorkbenchWindow, Control> page)
    {
        w.ShowWorkbench();
        Pump();
        var wb = w.WorkbenchTool!;
        wb.Tabs.SelectedItem = wb.Tabs.Items.Cast<TabItem>().Single(t => t.Content == page(wb));
        Pump();
        return wb;
    }

    [AvaloniaFact]
    public void Workbench_alerts_tab_follows_view()
    {
        var w = _games.Game();
        var wb = Bench(w, b => b.Alerts);
        wb.Alerts.SelectAlert("a-bs");
        Pump();
        Assert.Equal("0 matches now", wb.Alerts.MatchStatusText.Text);
        wb.Alerts.SelectAlert("a-rs");
        Pump();
        var redMatches = wb.Alerts.MatchStatusText.Text;
        Assert.NotEqual("0 matches now", redMatches);
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        Pump();
        Assert.Equal("0 matches now", wb.Alerts.MatchStatusText.Text);
        wb.Alerts.SelectAlert("a-bs");
        Pump();
        Assert.Equal(redMatches, wb.Alerts.MatchStatusText.Text);
    }

    [AvaloniaFact]
    public void Workbench_formulas_preview_follows_view()
    {
        var w = _games.Game();
        var wb = Bench(w, b => b.Formulas);
        wb.ShowFormula("f-water");
        Pump();
        static IReadOnlyList<string> Units(WorkbenchWindow wb) => wb.Formulas.Preview.PieceBox.Items.OfType<string>().ToList();
        Assert.Contains(Units(wb), u => u.StartsWith("Red Scout", StringComparison.Ordinal));
        Assert.DoesNotContain(Units(wb), u => u.StartsWith("Blue", StringComparison.Ordinal));
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        Pump();
        Assert.Contains(Units(wb), u => u.StartsWith("Blue Scout", StringComparison.Ordinal));
        Assert.Contains(Units(wb), u => u.StartsWith("Blue 1", StringComparison.Ordinal));
        Assert.DoesNotContain(Units(wb), u => u.StartsWith("Red", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Workbench_properties_tab_follows_view()
    {
        var w = _games.Game();
        var wb = Bench(w, b => b.Properties);
        var grid = wb.Properties.PropertyGrid;
        Assert.Contains("Radar", grid.CellItems(0, 3)!);
        Assert.DoesNotContain("Sonar", grid.CellItems(0, 3)!);
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        Pump();
        Assert.Contains("Sonar", grid.CellItems(0, 3)!);
        Assert.DoesNotContain("Radar", grid.CellItems(0, 3)!);
    }
}
