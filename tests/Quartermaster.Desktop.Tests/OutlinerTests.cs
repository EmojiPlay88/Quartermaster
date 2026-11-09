using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Outliner;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 3a task 6: the Outliner dock (View > Outliner), floating via Pop out / Dock. Invented pack from PhaseFixture; headless.</summary>
[Collection("MacFlag")]
public sealed class OutlinerTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-outliner-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public OutlinerTests() { ShortcutGestures.IsMac = false; Directory.CreateDirectory(_tmp); }

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static string Demo(string file = "Riverland-Demo.vmod", [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    MainWindow NewWindow(string tag)
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache" + tag), null, Path.Combine(_tmp, "layout" + tag)) { Width = 1024, Height = 768 };
        w.ScreensProvider = () => new[] { new ScreenInfo(new System.Drawing.Rectangle(0, 0, 1920, 1080), 96) };
        _windows.Add(w);
        w.Show();
        return w;
    }

    /// <summary>The phase fixture with Fuel (sum) and Water (lowest) in the outliner, optional alerts and phases.</summary>
    MainWindow PhaseGame(bool phases = true, params AlertDef[] alerts)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w = NewWindow("");
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        w.StartSetup(0, Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            var props = p.Properties.Select(x => x.Id == "fuel" ? x with { Outliner = OutlinerAggregate.Sum, Notes = "Fuel on hand" }
                : x.Id == "water" ? x with { Outliner = OutlinerAggregate.Min } : x).ToList();
            p = p with { Properties = props, Alerts = alerts.ToList() };
            return phases ? p : p with { Phases = new(), Blocks = new() };
        });
        Pump();
        w.FlushAlerts();
        w.UpdateLayout();
        return w;
    }

    static void ClickOutlinerMenu(MainWindow w)
    {
        w.FindMenuItem("&Outliner")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        w.UpdateLayout();
    }

    static void ShowOutliner(MainWindow w)
    {
        ClickOutlinerMenu(w);
        w.Outliner!.Flush();
        w.UpdateLayout();
    }

    static int RowOf(OutlinerPanel o, string name) => o.RowNodes.Select((n, i) => (n, i)).First(x => x.n.Name == name).i;

    [AvaloniaFact]
    public void View_outliner_toggles_dock()
    {
        var w = PhaseGame();
        var item = w.FindMenuItem("&Outliner")!;
        Assert.True(item.IsEnabled);
        Assert.False(w.OutlinerShown);
        Assert.False(item.IsChecked);
        ClickOutlinerMenu(w);
        Assert.True(w.OutlinerShown);
        Assert.True(item.IsChecked);
        Assert.Same(w.Outliner, w.OutlinerHostChild);
        ClickOutlinerMenu(w);
        Assert.False(w.OutlinerShown);
        Assert.False(item.IsChecked);
    }

    [AvaloniaFact]
    public void Reload_is_deferred_while_hidden()
    {
        var w = PhaseGame();
        var o = w.Outliner!;
        var before = o.ReloadCount;
        w.Session!.Game!.Execute(new LedgerDelta("p:r1", "fuel", 40, 41, "manual", null, null, null, null));
        w.FlushAlerts();
        Pump();
        Assert.Equal(before, o.ReloadCount);
        Assert.True(o.Stale);
        ClickOutlinerMenu(w);   // showing it does the put-off refresh
        Assert.False(o.Stale);
        Assert.True(o.ReloadCount > before);
        o.ExpandAll();
        Assert.Equal("121", o.CellText(0, 3));
    }

    [AvaloniaFact]
    public void Groups_by_map_and_by_side()
    {
        var w = PhaseGame();
        ShowOutliner(w);
        var o = w.Outliner!;
        Assert.Equal(new[] { "", "Name", "Units", "Fuel", "Water" }, o.HeaderTexts);
        Assert.Equal("Fuel on hand", o.HeaderNotes(3));
        var map = Assert.Single(o.RowNodes);
        Assert.Equal(("Main Map", OutlinerNodeKind.Group, 3), (map.Name, map.Kind, map.Units));
        o.ExpandAll();
        Assert.Equal(new[] { "Main Map", "Blue 1", "Red 1", "Red 2" }, o.RowNodes.Select(n => n.Name));
        Assert.Equal(new[] { 0, 1, 1, 1 }, Enumerable.Range(0, 4).Select(o.DepthOf));

        o.GroupingBox.SelectedIndex = 1;
        Pump();
        Assert.Equal(OutlinerGrouping.BySide, o.Grouping);
        Assert.Equal(new[] { ("Red", 2), ("Blue", 1) }, o.RowNodes.Select(n => (n.Name, n.Units)));
        o.GroupingBox.SelectedIndex = 0;
        Assert.Equal(OutlinerGrouping.OrgDisplays, o.Grouping);
        Assert.Equal(new[] { "Main Map", "Blue 1", "Red 1", "Red 2" }, o.RowNodes.Select(n => n.Name));   // the map stays open
    }

    [AvaloniaFact]
    public void Aggregate_columns_and_breakdown_tip()
    {
        var w = PhaseGame();
        ShowOutliner(w);
        var o = w.Outliner!;
        o.ExpandAll();
        w.UpdateLayout();
        Assert.Equal("3", o.CellText(0, 2));
        Assert.Equal("120", o.CellText(0, 3));   // Fuel total
        Assert.Equal("", o.CellText(0, 4));      // Water has no ledger value yet: blank
        Assert.Equal("40", o.CellText(RowOf(o, "Red 1"), 3));
        var b = o.BreakdownFor(0, 3)!;
        Assert.Equal("Fuel total 120  (Main Map)", b.Title);
        Assert.Equal(3, b.Lines.Count);
        Assert.Null(o.BreakdownFor(0, 2));   // Units has no explanation
        Assert.Null(o.BreakdownFor(0, 1));
        Assert.NotNull(o.BreakdownFor(RowOf(o, "Red 1"), 3));
        o.HoverCell(0, 3);
        Assert.StartsWith("Fuel total 120", o.TipText);
        o.HideTip();
        Assert.Null(o.TipText);
        // Column widths: the WinForms defaults, then remembered per column across a refresh.
        Assert.Equal(new double[] { 26, 190, 44, 60, 60 }, Enumerable.Range(0, 5).Select(o.ColumnWidth));
        o.SetColumnWidth(3, 97);
        o.Flush();
        Assert.Equal(97, o.ColumnWidth(3));
    }

    [AvaloniaFact]
    public void Alert_markers_follow_alert_bar()
    {
        var red = new AlertDef { Id = "a-red", Name = "Red units", Condition = "startsWith(name(), \"Red\")", Message = "Red is here", Severity = AlertSeverity.Warning };
        var w = PhaseGame(true, red);
        ShowOutliner(w);
        var o = w.Outliner!;
        o.ExpandAll();
        Assert.Equal(new[] { AlertSeverity.Warning }, o.AlertsAt(RowOf(o, "Red 1")));
        Assert.Equal(new[] { AlertSeverity.Warning }, o.AlertsAt(RowOf(o, "Red 2")));
        Assert.Empty(o.AlertsAt(RowOf(o, "Blue 1")));
        Assert.Equal(new[] { AlertSeverity.Warning }, o.AlertsAt(0));   // the group carries its pieces' alerts
        // A critical negative-value alert joins; the worst comes first.
        w.Session!.Game!.Execute(new LedgerDelta("p:r1", "fuel", 40, -3, "manual", null, null, null, null));
        w.FlushAlerts();
        Pump();
        o.ExpandAll();
        Assert.Equal(new[] { AlertSeverity.Critical, AlertSeverity.Warning }, o.AlertsAt(RowOf(o, "Red 1")));
        Assert.Equal(new[] { AlertSeverity.Warning }, o.AlertsAt(RowOf(o, "Red 2")));
    }

    [AvaloniaFact]
    public void Activate_piece_centres_and_org_display_opens_map()
    {
        var w = PhaseGame();
        ShowOutliner(w);
        var o = w.Outliner!;
        o.ExpandAll();
        o.ActivateRow(RowOf(o, "Red 2"));
        Assert.Equal(new[] { PhaseFixture.RedTwo }, w.Maps.Views.First().SelectedPieceIds);
        // A plain map group toggles instead of opening anything.
        var open = o.IsOpen(0);
        o.ActivateRow(0);
        Assert.NotEqual(open, o.IsOpen(0));

        // An org display group opens its map tab.
        var fx = OrgModule();
        _fixtures.Add(fx);
        var d = NewWindow("2");
        Assert.True(d.OpenModule(fx.Build()));
        d.StartSetup(0, Difficulty.FullyAuto);
        d.Session!.NewRules("t");
        Pump();
        ShowOutliner(d);
        var od = d.Outliner!;
        var org = od.RowNodes.Select((n, i) => (n, i)).First(x => x.n.Name == "Org A");
        Assert.Equal("map:Org A", org.n.Key);
        Assert.NotEqual("Org A", d.Maps.ActiveView!.Map.Name);
        od.ActivateRow(org.i);
        Assert.Equal("Org A", d.Maps.ActiveView!.Map.Name);
        Assert.False(od.IsOpen(org.i));   // it opened the map, it did not toggle
    }

    /// <summary>The phase fixture's map plus an org display "Org A" (a chart-window WidgetMap) holding one piece.</summary>
    static FixtureModule OrgModule()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.PredefinedSetup name=\"Start\" file=\"s.vsav\" useFile=\"true\" isMenu=\"false\"/>"
            + "<VASSAL.build.module.ChartWindow name=\"Charts\"><VASSAL.build.widget.TabWidget entryName=\"Tab\">"
            + "<VASSAL.build.widget.WidgetMap mapName=\"Org A\"/></VASSAL.build.widget.TabWidget></VASSAL.build.module.ChartWindow></VASSAL.build.GameModule>");
        static string Add(string id, string name, string map) =>
            "+/" + Quartermaster.Vassal.SequenceEncoder.Encode(new[] { id, $"propertysheet;0Fuel;Sheet;	piece;;;unit.svg;{name}", $"40	{map};30;50;1;0" }, '/');
        return new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg")
            .AddSave("s.vsav", new[] { "begin_save", "", Add("o1", "Org 1", "Org A"), Add("m1", "Main 1", "Main Map"), "end_save" });
    }

    [AvaloniaFact]
    public void Now_list_jumps_to_decision()
    {
        var w = PhaseGame();
        ShowOutliner(w);
        var o = w.Outliner!;
        Assert.True(o.NowVisible);
        Assert.Equal(new[] { "(nothing waiting)" }, o.NowTexts);
        Assert.Equal("Now", o.NowLabelText);
        for (var i = 0; i < 4; i++) w.Actions!.NextPhase();   // Start (Weather), Supply, Movement, Combat
        w.Session!.Game!.Execute(new LedgerDelta("p:r1", "fuel", 40, 0, "manual", null, null, null, null));
        w.FlushAlerts();
        Pump();
        o.Flush();
        var texts = o.NowTexts;
        Assert.Contains("Out of fuel — 1 unit", texts);
        Assert.Equal("Now — " + texts.Count, o.NowLabelText);
        o.ActivateNow(texts.ToList().IndexOf("Out of fuel — 1 unit"));
        Assert.Equal(new[] { PhaseFixture.RedOne }, w.Maps.Views.First().SelectedPieceIds);
        o.ActivateNow(99);   // out of range: nothing happens
    }

    [AvaloniaFact]
    public void Now_section_hidden_without_phases()
    {
        var w = PhaseGame(phases: false);
        ShowOutliner(w);
        Assert.False(w.Outliner!.NowVisible);
        Assert.True(w.Outliner.VisibleRowCount > 0);
    }

    [AvaloniaFact]
    public void Outliner_viewer_sees_only_own_values()
    {
        var w = PhaseGame();
        var s = w.Session!;
        ShowOutliner(w);
        var o = w.Outliner!;
        o.ExpandAll();
        Assert.Equal("120", o.CellText(0, 3));
        s.SetViewSides(new HashSet<string> { "red" });
        w.FlushAlerts();
        Pump();
        o.Flush();
        o.ExpandAll();
        Assert.Equal("80", o.CellText(0, 3));   // only Red's pieces' fuel
        Assert.Equal("", o.CellText(RowOf(o, "Blue 1"), 3));
        Assert.Equal("40", o.CellText(RowOf(o, "Red 1"), 3));
        Assert.Contains("visible units only", o.BreakdownFor(0, 3)!.Title);
        Assert.Null(o.BreakdownFor(RowOf(o, "Blue 1"), 3));
        // Only pieces the viewer may see get a picture frame.
        var nodes = o.RowNodes.ToList();
        foreach (var n in nodes.Where(n => n.Kind == OutlinerNodeKind.Piece))
            Assert.Equal(s.View!.Pieces.ContainsKey(n.PieceId!), o.HasPicture(nodes.IndexOf(n)));
    }

    [AvaloniaFact]
    public void Pieces_show_24x18_pictures()
    {
        var w = PhaseGame();
        ShowOutliner(w);
        var o = w.Outliner!;
        o.ExpandAll();
        w.UpdateLayout();
        var row = RowOf(o, "Red 1");
        Assert.True(o.HasPicture(row));
        Assert.False(o.HasPicture(0));   // a group has none
        var until = DateTime.UtcNow.AddSeconds(20);
        while (o.PictureAt(row) == null && DateTime.UtcNow < until)
        {
            Pump();
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        var bmp = o.PictureAt(row);
        Assert.NotNull(bmp);
        Assert.Equal(new Avalonia.PixelSize(24, 18), bmp!.PixelSize);
        Assert.Null(o.PictureAt(0));
    }

    [AvaloniaFact]
    public void Outliner_pops_out_and_docks_back()
    {
        var w = PhaseGame();
        ShowOutliner(w);
        var o = w.Outliner!;
        Assert.Equal("Pop out", o.PopButton.Content);
        o.PopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        var f = w.OutlinerFloat!;
        Assert.NotNull(f);
        Assert.Equal("Outliner", f.Title);
        Assert.Same(o, f.Content);
        Assert.Equal("Dock", o.PopButton.Content);
        Assert.False(w.OutlinerShown);
        Assert.True(w.FindMenuItem("&Outliner")!.IsChecked);
        // Opens at the WinForms default: beside the main window's right edge.
        Assert.Equal(440, f.PixelBounds.W);
        Assert.Equal(520, f.PixelBounds.H);
        // The menu while floating docks it back.
        ClickOutlinerMenu(w);
        Assert.Null(w.OutlinerFloat);
        Assert.True(w.OutlinerShown);
        Assert.Equal("Pop out", o.PopButton.Content);
        // Pop out and close the window: docks back too.
        o.PopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        w.OutlinerFloat!.Close();
        Pump();
        Assert.Null(w.OutlinerFloat);
        Assert.True(w.OutlinerShown);
        Assert.Same(o, w.OutlinerHostChild);
    }

    [AvaloniaFact]
    public void Floating_bounds_are_remembered_per_module()
    {
        var w = PhaseGame();
        ShowOutliner(w);
        w.Outliner!.PopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        w.OutlinerFloat!.PixelBounds = new RectangleI(200, 150, 400, 300);
        var sha = w.Session!.Archive.Sha256;
        Assert.True(w.OpenModule(Demo()));   // closing the game saves where the window was
        var saved = new WindowLayoutStore(Path.Combine(_tmp, "layout")).LoadModule(sha);
        Assert.True(saved.TryGetValue("outliner", out var sw));
        Assert.Equal(new System.Drawing.Rectangle(200, 150, 400, 300), sw!.Bounds);
        Assert.False(sw.Floating);   // docked again when the game closes (WinForms)
        Assert.Null(w.OutlinerFloat);
        Assert.False(w.OutlinerShown);
    }

    [AvaloniaFact]
    public void Expanded_state_kept_across_reload()
    {
        var w = PhaseGame();
        ShowOutliner(w);
        var o = w.Outliner!;
        Assert.Single(o.RowNodes);   // groups start closed
        o.Toggle(0);
        Assert.Equal(4, o.VisibleRowCount);
        // A game change reloads through the alert hub; the open group stays open.
        w.Session!.Game!.Execute(new LedgerDelta("p:r1", "fuel", 40, 45, "manual", null, null, null, null));
        w.FlushAlerts();
        Pump();
        o.Flush();
        Assert.Equal(4, o.VisibleRowCount);
        Assert.Equal("125", o.CellText(0, 3));
        o.Toggle(0);
        Assert.Equal(1, o.VisibleRowCount);
    }
}
