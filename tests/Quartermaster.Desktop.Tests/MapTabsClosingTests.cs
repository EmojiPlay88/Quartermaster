using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 6c task 3: only the main map opens with a module; map tabs close (×, middle click, Close / Close Others / Close All)
/// and reopen from the module tree; the open tabs are remembered per module; a map the player may not see never gets a tab. Headless, on an
/// invented module with three maps.</summary>
[Collection("MacFlag")]
public sealed class MapTabsClosingTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-maptabs-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly FakeDialogs _dialogs = new();
    readonly FixtureModule _fx;

    public MapTabsClosingTests()
    {
        ShortcutGestures.IsMac = false;
        Directory.CreateDirectory(_tmp);
        _fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            MapXml("Chart A") + MapXml("Chart B") + "</VASSAL.build.GameModule>"));
    }

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        _fx.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string MapXml(string name) =>
        $"<VASSAL.build.module.Map mapName=\"{name}\"><VASSAL.build.module.map.BoardPicker>" +
        $"<VASSAL.build.module.map.boardPicker.Board image=\"board.png\" name=\"{name} Board\" reversible=\"false\"/>" +
        "</VASSAL.build.module.map.BoardPicker></VASSAL.build.module.Map>";

    static void Pump() => Dispatcher.UIThread.RunJobs();

    string LayoutRoot => Path.Combine(_tmp, "layout");

    MainWindow NewWindow()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache" + _windows.Count), null, LayoutRoot) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    MainWindow Open(bool game = false)
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(_fx.Build()));
        if (game) w.StartSetup(null);
        Pump();
        return w;
    }

    static List<string> Names(MainWindow w) => w.MapTabs.Views.Select(v => v.Map.Name).ToList();

    static MapView View(MainWindow w, string name) => w.MapTabs.Views.Single(v => v.Map.Name == name);

    static WindowTreeNode Node(MainWindow w, string name) =>
        w.ModuleTree.Nodes.Select(i => i.Tag).OfType<WindowTreeNode>().Single(n => n.Ref is MapDef m && m.Name == name);

    static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    static MenuItem MenuItemOf(MapTabs tabs, MapView v, string header) =>
        tabs.MenuOf(v)!.Items.OfType<MenuItem>().Single(i => (string?)i.Header == header);

    [AvaloniaFact]
    public void Only_the_main_map_opens_with_the_module()
    {
        var w = Open();
        Assert.Equal(new[] { "Main Map" }, Names(w));
        Assert.Equal("Main Map", w.MapTabs.ActiveView!.Map.Name);
        Assert.Equal(new[] { "Main Map" }, w.MapTabs.OpenMapIds);
        // The other maps stay in the module tree.
        Assert.NotNull(Node(w, "Chart A"));
        Assert.NotNull(Node(w, "Chart B"));
    }

    [AvaloniaFact]
    public void Tree_double_click_opens_a_closed_map_tab()
    {
        var w = Open();
        var changes = 0;
        w.MapTabs.OpenMapsChanged += () => changes++;
        w.ModuleTree.Activate(Node(w, "Chart A"));
        Assert.Equal(new[] { "Main Map", "Chart A" }, Names(w));
        Assert.Equal("Chart A", w.MapTabs.ActiveView!.Map.Name);
        Assert.True(changes > 0);
        // An open map's node only selects its tab.
        w.ModuleTree.Activate(Node(w, "Main Map"));
        Assert.Equal(new[] { "Main Map", "Chart A" }, Names(w));
        Assert.Equal("Main Map", w.MapTabs.ActiveView!.Map.Name);
        // A closed main map comes back the same way.
        w.MapTabs.Close(View(w, "Main Map"));
        Assert.Equal(new[] { "Chart A" }, Names(w));
        w.ModuleTree.Activate(Node(w, "Main Map"));
        Assert.Equal(new[] { "Chart A", "Main Map" }, Names(w));
        Assert.Equal("Main Map", w.MapTabs.ActiveView!.Map.Name);
    }

    [AvaloniaFact]
    public void Tab_close_button_closes_the_tab()
    {
        var w = Open(game: true);
        w.MapTabs.Open("Chart A");
        w.MapTabs.Open("Chart B");
        var a = View(w, "Chart A");
        var button = w.MapTabs.CloseButtonOf(a)!;
        Assert.Equal("Close", ToolTip.GetTip(button));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Equal(new[] { "Main Map", "Chart B" }, Names(w));
        Assert.Equal(new[] { "Main Map", "Chart B" }, w.MapTabs.OpenMapIds);
        Assert.False(w.MapTabs.IsPreparingTiles(a));
        Assert.Null(TopLevel.GetTopLevel(a));

        // A middle click on a tab header closes it too.
        var b = View(w, "Chart B");
        var tab = w.MapTabs.TabOf(b)!;
        w.UpdateLayout();
        var at = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), w)!.Value;
        w.MouseDown(at, MouseButton.Middle);
        w.MouseUp(at, MouseButton.Middle);
        Pump();
        Assert.Equal(new[] { "Main Map" }, Names(w));
    }

    [AvaloniaFact]
    public void Close_others_and_close_all()
    {
        var w = Open();
        w.MapTabs.Open("Chart A");
        w.MapTabs.Open("Chart B");
        var a = View(w, "Chart A");
        Assert.Equal(new[] { "Close", "Close Others", "Close All" }, w.MapTabs.MenuOf(a)!.Items.OfType<MenuItem>().Select(i => (string?)i.Header));
        Click(MenuItemOf(w.MapTabs, a, "Close Others"));
        Assert.Equal(new[] { "Chart A" }, Names(w));
        Assert.Same(a, w.MapTabs.ActiveView);
        w.MapTabs.Open("Main Map");
        Click(MenuItemOf(w.MapTabs, View(w, "Main Map"), "Close"));
        Assert.Equal(new[] { "Chart A" }, Names(w));
        Click(MenuItemOf(w.MapTabs, a, "Close All"));
        Assert.Empty(w.MapTabs.Views);
        Assert.Empty(w.MapTabs.OpenMapIds);
    }

    [AvaloniaFact]
    public void Closing_every_tab_leaves_the_backdrop_and_status_dash()
    {
        var w = Open(game: true);
        w.MapTabs.Open("Chart A");
        Assert.NotEqual("-", w.ZoomText);
        w.MapTabs.CloseAll();
        Pump();
        Assert.Empty(w.MapTabs.Views);
        Assert.Null(w.MapTabs.ActiveView);
        Assert.True(w.MapTabs.ShowsBackdrop);
        Assert.True(w.MapTabs.IsVisible);
        Assert.Equal("-", w.LocationText);
        Assert.Equal("-", w.ZoomText);
        Assert.False(w.StatusTilingVisible);
        // Commands on the active map do nothing without one.
        w.ClickMenu("Zoom to Fit");
        w.Commands.Execute("tool.los");
        // The tree brings the main map back.
        w.ModuleTree.Activate(Node(w, "Main Map"));
        Assert.Equal(new[] { "Main Map" }, Names(w));
        Assert.False(w.MapTabs.ShowsBackdrop);
        Assert.NotEqual("-", w.ZoomText);
    }

    [AvaloniaFact]
    public void Open_tabs_are_remembered_per_module()
    {
        var w = Open();
        w.ModuleTree.Activate(Node(w, "Chart B"));
        Assert.Equal(new[] { "Main Map", "Chart B" }, Names(w));
        w.ForceClose();
        var again = Open();
        Assert.Equal(new[] { "Main Map", "Chart B" }, Names(again));
        Assert.Equal("Chart B", again.MapTabs.ActiveView!.Map.Name);
        // The selection is remembered as it changes, without closing.
        again.MapTabs.Activate(View(again, "Main Map"));
        Pump();
        var third = Open();
        Assert.Equal(new[] { "Main Map", "Chart B" }, Names(third));
        Assert.Equal("Main Map", third.MapTabs.ActiveView!.Map.Name);
    }

    [AvaloniaFact]
    public void Remembered_map_that_no_longer_exists_is_ignored()
    {
        var w = Open();
        var key = w.Session!.Archive.Sha256;
        w.ForceClose();
        var store = new WindowLayoutStore(LayoutRoot);
        store.SaveOpenMaps(key, new[] { "Gone Map", "Chart A" }, "Gone Map");
        var again = Open();
        Assert.Equal(new[] { "Chart A" }, Names(again));
        Assert.Equal("Chart A", again.MapTabs.ActiveView!.Map.Name);
        again.ForceClose();
        // Nothing left of the remembered tabs: the main map opens.
        store.SaveOpenMaps(key, new[] { "Gone Map" }, null);
        Assert.Equal(new[] { "Main Map" }, Names(Open()));
    }

    [AvaloniaFact]
    public void Hidden_map_never_gets_a_tab_even_if_remembered()
    {
        using var games = new HotSeatGames("qm-maptabs-hs-");
        var w = games.Game();   // Anna (red) at the screen: Blue Map is Ben's
        w.MapTabs.Open("Blue Map");
        Assert.DoesNotContain("Blue Map", Names(w));
        w.MapTabs.Open("Red Map");
        Assert.Contains("Red Map", Names(w));
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        Assert.DoesNotContain("Red Map", Names(w));
        Assert.Contains("Red Map", w.MapTabs.OpenMapIds);   // still the user's, back when Anna returns
        w.MapTabs.Open("Blue Map");
        Assert.Contains("Blue Map", Names(w));
        HotSeatGames.SwitchTo(w, HotSeatGames.Anna);
        Assert.Contains("Red Map", Names(w));
        Assert.DoesNotContain("Blue Map", Names(w));

        // A second game of the same module remembers Blue Map as open; Anna still gets no tab for it.
        var second = games.Game();
        Assert.Contains("Blue Map", second.MapTabs.OpenMapIds);
        Assert.DoesNotContain("Blue Map", Names(second));
        Assert.Contains("Red Map", Names(second));
    }

    [AvaloniaFact]
    public void Close_others_and_close_all_keep_maps_the_player_cannot_see()
    {
        using var games = new HotSeatGames("qm-maptabs-hs-");
        var w = games.Game();   // Anna
        w.MapTabs.Open("Red Map");
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        w.MapTabs.Open("Blue Map");
        Assert.Equal(new[] { "Main Map", "Blue Map" }, Names(w));
        Click(MenuItemOf(w.MapTabs, View(w, "Blue Map"), "Close Others"));
        Assert.Equal(new[] { "Blue Map" }, Names(w));
        Assert.Equal(new[] { "Red Map", "Blue Map" }, w.MapTabs.OpenMapIds);   // Anna's map stays hers
        HotSeatGames.SwitchTo(w, HotSeatGames.Anna);
        Assert.Equal(new[] { "Red Map" }, Names(w));

        Click(MenuItemOf(w.MapTabs, View(w, "Red Map"), "Close All"));
        Assert.Empty(w.MapTabs.Views);
        Assert.Equal(new[] { "Blue Map" }, w.MapTabs.OpenMapIds);
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        Assert.Equal(new[] { "Blue Map" }, Names(w));
    }

    [AvaloniaFact]
    public void Player_whose_open_maps_are_all_hidden_gets_the_main_map()
    {
        using var games = new HotSeatGames("qm-maptabs-hs-");
        var w = games.Game();   // Anna
        HotSeatGames.SwitchTo(w, HotSeatGames.Ben);
        w.MapTabs.Open("Blue Map");
        Click(MenuItemOf(w.MapTabs, View(w, "Blue Map"), "Close Others"));
        Assert.Equal(new[] { "Blue Map" }, w.MapTabs.OpenMapIds);
        // Anna closed nothing: she gets the main map, not an empty map area.
        HotSeatGames.SwitchTo(w, HotSeatGames.Anna);
        Assert.Equal(new[] { "Main Map" }, Names(w));
        Assert.False(w.MapTabs.ShowsBackdrop);

        // Bound while Anna is at the screen with only Ben's map remembered: the main map too.
        w.MapTabs.Bind(w.Session, null, new[] { "Blue Map" }, "Blue Map");
        Assert.Equal(new[] { "Main Map" }, Names(w));
        Assert.Equal("Main Map", w.MapTabs.ActiveView!.Map.Name);

        // A second game of the same module (Ben's map remembered) opens with Anna at the screen.
        var second = games.Game();
        Assert.Equal(new[] { "Main Map" }, Names(second));
    }

    [AvaloniaFact]
    public void Tab_strip_is_one_row()
    {
        var w = Open();
        w.MapTabs.Open("Chart A");
        w.MapTabs.Open("Chart B");
        w.UpdateLayout();
        var presenter = w.MapTabs.GetVisualDescendants().OfType<ItemsPresenter>().First();
        var panel = Assert.IsType<StackPanel>(presenter.Panel);
        Assert.Equal(Avalonia.Layout.Orientation.Horizontal, panel.Orientation);
        var scroll = presenter.GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, scroll.VerticalScrollBarVisibility);
        Assert.Empty(w.MapTabs.GetVisualDescendants().OfType<WrapPanel>());
        // Every tab header sits on the same row and the selected map is shown.
        var tabs = w.MapTabs.Items.OfType<TabItem>().ToList();
        var tops = tabs.Select(t => t.TranslatePoint(default, panel)!.Value.Y).ToList();
        Assert.True(tops.Max() - tops.Min() < tabs.Min(t => t.Bounds.Height));
        Assert.NotNull(TopLevel.GetTopLevel(w.MapTabs.ActiveView!));
    }
}
