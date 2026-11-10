using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 2b task 10: module tree, tables tree, the left column and the floating log. Headless.</summary>
[Collection("MacFlag")]
public sealed class TreesAndFloatTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-trees-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public TreesAndFloatTests() { ShortcutGestures.IsMac = false; Directory.CreateDirectory(_tmp); }

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file = "Riverland-Demo.vmod", [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void PumpUntil(Func<bool> done, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            Pump();
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);   // runs the debounce timers too
        }
    }

    string LayoutRoot => Path.Combine(_tmp, "layout-root");

    MainWindow NewWindow()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, LayoutRoot) { Width = 1024, Height = 768 };
        w.ScreensProvider = () => new[] { new ScreenInfo(new Rectangle(0, 0, 1920, 1080), 96) };
        _windows.Add(w);
        w.Show();
        return w;
    }

    MainWindow Game(bool rules = false)
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(Demo()));
        w.StartSetup(null);
        if (rules) w.Session!.OpenRules(Demo("Riverland-Demo.qrules"));
        Pump();
        return w;
    }

    static List<WindowTreeNode> MapNodes(MainWindow w) =>
        w.ModuleTree.Nodes.Select(i => i.Tag).OfType<WindowTreeNode>().Where(n => n.Kind == WindowNodeKind.Map).ToList();

    [AvaloniaFact]
    public void Module_tree_lists_maps_and_activates_tab()
    {
        var w = Game();
        var s = w.Session!;
        var root = w.ModuleTree.Nodes.First();
        Assert.StartsWith(s.Model.Name, root.Text);
        var maps = MapNodes(w);
        Assert.Equal(s.Model.Maps.Where(m => s.CanSeeMap(m.Id)).Select(m => m.Name).OrderBy(x => x), maps.Select(n => ((MapDef)n.Ref!).Name).OrderBy(x => x));
        Assert.NotEmpty(maps);
        var last = (MapDef)maps[^1].Ref!;
        w.ModuleTree.Activate(maps[^1]);
        Assert.Equal(last.Id, w.MapTabs.ActiveView!.Map.Id);
        // The first map's node brings the first tab back.
        var first = (MapDef)maps[0].Ref!;
        w.ModuleTree.Activate(maps[0]);
        Assert.Equal(first.Id, w.MapTabs.ActiveView!.Map.Id);
    }

    [AvaloniaFact]
    public void Folder_node_does_nothing()
    {
        var w = Game();
        w.SetStatus("");
        w.ModuleTree.Activate(new WindowTreeNode("folder", WindowNodeKind.Folder, null, Array.Empty<WindowTreeNode>()));
        Assert.Equal("", w.StatusText);
    }

    [AvaloniaFact]
    public void Hidden_map_not_in_tree_for_viewer()
    {
        var w = Game(rules: true);
        var s = w.Session!;
        var all = s.Model.Maps.Select(m => m.Id).ToList();
        Assert.Equal(all.Count, MapNodes(w).Count);
        var other = s.Rules!.Sides.First(x => x.Id != "north").Id;
        s.UpdateRules(r => r with { MapOwners = new List<Quartermaster.Rules.MapOwner> { new() { MapId = all[0], SideId = other } } });
        s.SetViewSides(new HashSet<string> { "north" });
        Pump();
        var hidden = all.Where(id => !s.CanSeeMap(id)).ToList();
        Assert.NotEmpty(hidden);   // the demo's rules give some map to another side
        var shown = MapNodes(w).Select(n => ((MapDef)n.Ref!).Id).ToList();
        Assert.DoesNotContain(shown, id => hidden.Contains(id));
        Assert.Equal(all.Count - hidden.Count, shown.Count);
        s.SetViewSides(null);
        Pump();
        Assert.Equal(all.Count, MapNodes(w).Count);
    }

    [AvaloniaFact]
    public void View_module_tree_toggles_left_column()
    {
        var w = NewWindow();
        var item = w.FindMenuItem("Module &Tree")!;
        Assert.NotNull(item);
        Assert.True(item.IsChecked);
        Assert.True(w.ModuleTreeShown);
        Assert.True(w.LeftColumn.IsVisible);
        Assert.True(w.LeftColumnWidth > 0);
        w.ClickMenu("Module &Tree");
        Assert.False(w.ModuleTreeShown);
        Assert.False(w.LeftColumn.IsVisible);
        Assert.False(item.IsChecked);
        Assert.Equal(0, w.LeftColumnWidth);
        w.ClickMenu("Module &Tree");
        Assert.True(w.ModuleTreeShown);
        Assert.True(w.LeftColumn.IsVisible);
        Assert.True(item.IsChecked);
        Assert.True(w.LeftColumnWidth > 0);
    }

    [AvaloniaFact]
    public void Left_split_fraction_persists()
    {
        var w = NewWindow();
        Assert.Equal(WindowLayoutStore.DefaultLeftSplit, w.LeftSplit, 3);
        w.SetLeftSplit(0.3);
        Assert.Equal(0.3, new WindowLayoutStore(LayoutRoot).LoadLeftSplit(), 3);
        w.SetLeftSplit(0.95);   // clamped like the store
        Assert.Equal(WindowLayoutStore.MaxLeftSplit, w.LeftSplit, 3);
        w.SetLeftSplit(0.3);
        var w2 = NewWindow();
        Assert.Equal(0.3, w2.LeftSplit, 3);
    }

    [AvaloniaFact]
    public void Tables_tree_lists_tables_and_formulas()
    {
        var w = NewWindow();
        Assert.Equal("(no rules pack)", Assert.Single(w.TablesTree.Roots).Text);
        Assert.True(w.OpenModule(Demo()));
        Assert.Equal("(no rules pack)", Assert.Single(w.TablesTree.Roots).Text);
        w.Session!.OpenRules(Demo("Riverland-Demo.qrules"));
        PumpUntil(() => w.TablesTree.Roots.Count == 2);
        var pack = w.Session.Rules!;
        Assert.Equal(new[] { "Tables", "Formulas" }, w.TablesTree.Roots.Select(r => r.Text));
        Assert.Equal((pack.Tables.Count, pack.Formulas.Count), w.TablesTree.Counts);
        Assert.NotEqual((0, 0), w.TablesTree.Counts);
        Assert.Equal(pack.Tables.Select(t => t.Name).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase),
            w.TablesTree.Roots[0].Children.Select(c => c.Text));
        // Activation is ignored in this stage: nothing opens, nothing throws.
        w.SetStatus("");
        var first = w.TablesTree.Tree.ItemsSource!.Cast<TreeViewItem>().First();
        Assert.True(first.IsExpanded);
        Assert.Equal("", w.StatusText);
    }

    [AvaloniaFact]
    public void Log_pops_out_and_docks_back()
    {
        var w = Game();
        var log = w.Log!;
        Assert.True(w.LogShown);
        Assert.Null(w.LogFloat);
        Assert.Equal("Pop out", log.PopButton.Content);
        Assert.Same(log, w.LogHostChild);

        log.PopButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        var f = w.LogFloat!;
        Assert.NotNull(f);
        Assert.Equal("Log", f.Title);
        Assert.Same(log, f.Content);
        Assert.Null(w.LogHostChild);
        Assert.False(w.LogShown);   // no bottom dock while floating
        Assert.Equal("Dock", log.PopButton.Content);
        Assert.True(w.FindMenuItem("&Log")!.IsChecked);

        log.PopButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Null(w.LogFloat);
        Assert.Same(log, w.LogHostChild);
        Assert.True(w.LogShown);
        Assert.Equal("Pop out", log.PopButton.Content);

        // Closing the floating window (not the Dock button) also docks it back; View > Log from a float docks it back too.
        log.PopButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        w.LogFloat!.Close();
        Pump();
        Assert.Null(w.LogFloat);
        Assert.Same(log, w.LogHostChild);
        Assert.Equal("Pop out", log.PopButton.Content);
        log.PopButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        w.ClickMenu("&Log");
        Pump();
        Assert.Null(w.LogFloat);
        Assert.True(w.LogShown);
    }

    [AvaloniaFact]
    public void Float_bounds_persist_per_module()
    {
        var w = Game();
        var sha = w.Session!.Archive.Sha256;
        w.Log!.PopButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        var f = w.LogFloat!;
        f.PixelBounds = new RectangleI(200, 150, 640, 280);
        Assert.True(w.OpenModule(Demo()));   // closing the game saves where the window was
        var saved = new WindowLayoutStore(LayoutRoot).LoadModule(sha);
        Assert.True(saved.ContainsKey("log"));
        Assert.Equal(new Rectangle(200, 150, 640, 280), saved["log"].Bounds);
        Assert.False(saved["log"].Floating);   // the panel is docked again when the game closes (WinForms)
        Assert.Null(w.LogFloat);

        // Another window on the same module remembers it: popping out opens at the saved place.
        var w2 = NewWindow();
        Assert.True(w2.OpenModule(Demo()));
        w2.Log!.PopButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Equal(new RectangleI(200, 150, 640, 280), w2.LogFloat!.PixelBounds);
        // A different module key keeps its own (none yet).
        Assert.Empty(new WindowLayoutStore(LayoutRoot).LoadModule("other"));
    }

    [AvaloniaFact]
    public void Saved_float_bounds_off_screen_are_clamped()
    {
        var w0 = NewWindow();
        Assert.True(w0.OpenModule(Demo()));
        var sha = w0.Session!.Archive.Sha256;
        new WindowLayoutStore(LayoutRoot).SaveModule(sha, new Dictionary<string, SavedWindow>
        {
            ["log"] = new SavedWindow(false, new Rectangle(5000, 300, 600, 300), false, 96),
        });
        var w = NewWindow();   // one 1920x1080 screen
        Assert.True(w.OpenModule(Demo()));
        w.Log!.PopButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        var b = w.LogFloat!.PixelBounds;
        var area = new Rectangle(0, 0, 1920, 1080);
        Assert.True(area.Contains(new Rectangle(b.X, b.Y, b.W, b.H)), $"{b} not inside {area}");
    }

    // ---------- the floating phase panel (task 6) ----------

    readonly List<Quartermaster.TestFixtures.FixtureModule> _fixtures = new();

    string PhaseModule()
    {
        var fx = Quartermaster.TestFixtures.PhaseFixture.Module();
        _fixtures.Add(fx);
        return fx.Build();
    }

    MainWindow PhaseGame(string module)
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(module));
        var s = w.Session!;
        w.StartSetup(0);
        s.NewRules("t");
        s.UpdateRules(_ => Quartermaster.TestFixtures.PhaseFixture.Pack(s.Archive.Sha256));
        Pump();
        return w;
    }

    static void ClickPop(MainWindow w)
    {
        w.Phases.PopButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    [AvaloniaFact]
    public void Pack_without_phases_docks_floating_panel_and_hides_column()
    {
        var w = PhaseGame(PhaseModule());
        ClickPop(w);
        Assert.NotNull(w.PhaseFloat);
        var s = w.Session!;
        s.UpdateRules(p => p with { Phases = new(), Blocks = new() });   // swap to a pack without phases
        Pump();
        w.UpdateLayout();
        Assert.Null(w.PhaseFloat);   // docked back (WinForms leaves the float open with the no-phases text: see parity.md)
        Assert.False(w.PhasePanelShown);
        Assert.False(w.PhaseSplitter.IsVisible);
        Assert.Equal(0, w.Body.ColumnDefinitions[Grid.GetColumn(w.PhaseHost)].Width.Value);
        Assert.Equal("Pop out", w.Phases.PopButton.Content);
        var item = w.FindMenuItem("&Phase panel")!;
        Assert.False(item.IsEnabled);
        Assert.False(item.IsChecked);
    }

    [AvaloniaFact]
    public void Phase_panel_pops_out_and_docks_back()
    {
        var w = PhaseGame(PhaseModule());
        var panel = w.Phases;
        Assert.True(w.PhasePanelShown);
        Assert.Null(w.PhaseFloat);
        Assert.Equal("Pop out", panel.PopButton.Content);

        ClickPop(w);
        var f = w.PhaseFloat!;
        Assert.Equal("Phases", f.Title);
        Assert.Same(panel, f.Content);
        Assert.False(w.PhasePanelShown);   // no right-hand dock while floating
        Assert.Equal("Dock", panel.PopButton.Content);
        Assert.True(w.FindMenuItem("&Phase panel")!.IsChecked);
        // Default place: PanelDockState.PhasesDefault of the main window (320 x 520).
        Assert.Equal((320, 520), (f.PixelBounds.W, f.PixelBounds.H));

        ClickPop(w);   // Dock
        Assert.Null(w.PhaseFloat);
        Assert.True(w.PhasePanelShown);
        Assert.Equal("Pop out", panel.PopButton.Content);

        // Closing the floating window docks the panel back, shown (WinForms PhaseFloatForm).
        ClickPop(w);
        w.PhaseFloat!.Close();
        Pump();
        Assert.Null(w.PhaseFloat);
        Assert.True(w.PhasePanelShown);
        Assert.Equal("Pop out", panel.PopButton.Content);

        // View > Phase panel while floating docks it back too.
        ClickPop(w);
        w.ClickMenu("&Phase panel");
        Pump();
        Assert.Null(w.PhaseFloat);
        Assert.True(w.PhasePanelShown);
        Assert.True(w.FindMenuItem("&Phase panel")!.IsChecked);
    }

    [AvaloniaFact]
    public void Phase_float_bounds_persist_per_module()
    {
        var module = PhaseModule();
        var w = PhaseGame(module);
        var sha = w.Session!.Archive.Sha256;
        ClickPop(w);
        w.PhaseFloat!.PixelBounds = new RectangleI(300, 120, 360, 500);
        Assert.True(w.OpenModule(module));   // closing the game saves where the window was
        var saved = new WindowLayoutStore(LayoutRoot).LoadModule(sha);
        Assert.True(saved.ContainsKey("phases"));
        Assert.Equal(new Rectangle(300, 120, 360, 500), saved["phases"].Bounds);
        Assert.False(saved["phases"].Floating);   // docked again when the game closes
        Assert.Null(w.PhaseFloat);

        var w2 = PhaseGame(module);
        ClickPop(w2);
        Assert.Equal(new RectangleI(300, 120, 360, 500), w2.PhaseFloat!.PixelBounds);
    }

    [AvaloniaFact]
    public void Screens_put_the_primary_first()
    {
        var a = new ScreenInfo(new Rectangle(-1920, 0, 1920, 1040), 96);
        var b = new ScreenInfo(new Rectangle(0, 0, 2560, 1400), 144);
        var c = new ScreenInfo(new Rectangle(2560, 0, 1280, 1000), 96);
        Assert.Equal(new[] { b, a, c }, PanelFloat.PrimaryFirst(new[] { a, b, c }, 1));
        Assert.Equal(new[] { a, b, c }, PanelFloat.PrimaryFirst(new[] { a, b, c }, 0));
        Assert.Equal(new[] { a, b, c }, PanelFloat.PrimaryFirst(new[] { a, b, c }, -1));   // no primary reported: as listed
    }
}
