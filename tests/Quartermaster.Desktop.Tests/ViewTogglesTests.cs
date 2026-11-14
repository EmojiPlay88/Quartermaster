using System.Drawing;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 6c task 4b: every part of the screen can be shown or hidden (View > Show and the View items already there), remembered
/// app-wide in ui.json; View > Show Everything resets. Invented phase fixture module; headless.</summary>
[Collection("MacFlag")]
public sealed class ViewTogglesTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-viewtoggles-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();
    string? _module;

    public ViewTogglesTests() { ShortcutGestures.IsMac = false; Directory.CreateDirectory(_tmp); }

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    string LayoutRoot => Path.Combine(_tmp, "layout-root");

    /// <summary>Every part id with its menu label (WinForms notation).</summary>
    static readonly (string Id, string Label)[] All =
    {
        ("toolbar", "Main &Toolbar"),
        ("phaseStrip", "Phase &Strip"),
        ("alertBar", "&Alert Bar"),
        ("statusBar", "Stat&us Bar"),
        ("mapToolbar", "&Map Toolbar"),
        ("moduleTree", "Module &Tree"),
        ("tablesTree", "&Charts && Tables"),
        ("palette", "Counter &Palette"),
        ("log", "&Log"),
        ("phases", "&Phase panel"),
        ("outliner", "&Outliner"),
    };

    public static TheoryData<string, string> Parts()
    {
        var d = new TheoryData<string, string>();
        foreach (var (id, label) in All) d.Add(id, label);
        return d;
    }

    static readonly string[] ShowLabels = { "Main &Toolbar", "Phase &Strip", "&Alert Bar", "Stat&us Bar", "&Map Toolbar", "&Charts && Tables" };

    MainWindow NewWindow()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, LayoutRoot) { Width = 1024, Height = 768 };
        w.ScreensProvider = () => new[] { new ScreenInfo(new Rectangle(0, 0, 1920, 1080), 96) };
        _windows.Add(w);
        w.Show();
        return w;
    }

    void GameMasterSettings()
    {
        Directory.CreateDirectory(LayoutRoot);
        var path = Path.Combine(LayoutRoot, "settings.json");
        if (!File.Exists(path)) File.WriteAllText(path, """{ "GameMasterMode": true, "OutlinerVisible": true }""");
    }

    /// <summary>A game master's game with a phased rules pack: every part has something to show (the outliner is switched on).</summary>
    MainWindow Game(bool gameMaster = true)
    {
        if (gameMaster) GameMasterSettings();
        if (_module == null)
        {
            var fx = PhaseFixture.Module();
            _fixtures.Add(fx);
            _module = fx.Build();
        }
        var w = NewWindow();
        Assert.True(w.OpenModule(_module));
        var s = w.Session!;
        w.StartSetup(0);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        Pump();
        w.FlushAlerts();
        Pump();
        w.UpdateLayout();
        return w;
    }

    static Control Part(MainWindow w, string id) => id switch
    {
        "toolbar" => w.ToolbarHost,
        "phaseStrip" => w.StripHost,
        "alertBar" => w.AlertBarHost,
        "statusBar" => w.StatusBarHost,
        "mapToolbar" => w.MapTabs.ToolbarOf(w.MapTabs.ActiveView!)!,
        "moduleTree" => w.ModuleTreeHost,
        "tablesTree" => w.TablesTreeHost,
        "palette" => w.PaletteHost,
        "log" => w.LogHost,
        "phases" => w.PhaseHost,
        "outliner" => w.OutlinerHost,
        _ => throw new ArgumentException(id),
    };

    static readonly string[] LeftParts = { "moduleTree", "palette", "tablesTree" };

    /// <summary>The size the part takes (height, or width for the right-hand docks).</summary>
    static double Size(MainWindow w, string id) => id is "phases" or "outliner" ? Part(w, id).Bounds.Width : Part(w, id).Bounds.Height;

    /// <summary>What its neighbours get: the space a hidden part gives back goes there.</summary>
    static double Room(MainWindow w, string id) => id switch
    {
        "toolbar" or "phaseStrip" or "alertBar" or "statusBar" => w.BodyAndLog.Bounds.Height,
        "mapToolbar" => w.MapTabs.ActiveView!.Bounds.Height,
        "moduleTree" or "tablesTree" or "palette" => LeftParts.Where(p => p != id).Select(p => Part(w, p)).Where(c => c.IsVisible).Sum(c => c.Bounds.Height),
        "log" => w.BodyWithTree.Bounds.Height,
        "phases" or "outliner" => w.MapTabs.Bounds.Width,
        _ => throw new ArgumentException(id),
    };

    static void Click(MainWindow w, string label)
    {
        w.ClickMenu(label);
        Pump();
        w.UpdateLayout();
    }

    static MenuItem Item(MainWindow w, string label) => w.FindMenuItem(label) ?? throw new ArgumentException("no menu item " + label);

    [AvaloniaTheory]
    [MemberData(nameof(Parts))]
    public void Each_part_can_be_hidden_and_shown(string id, string label)
    {
        var w = Game();
        var part = Part(w, id);
        Assert.True(part.IsVisible, id + " shows to begin with");
        Assert.True(Item(w, label).IsChecked);
        var size = Size(w, id);
        var room = Room(w, id);
        Assert.True(size > 0, id);

        Click(w, label);
        Assert.False(part.IsVisible);
        Assert.False(Item(w, label).IsChecked);
        Assert.True(Room(w, id) >= room + size - 0.5, $"{id}: neighbours {room} -> {Room(w, id)}, part was {size}");   // takes no space

        Click(w, label);
        Assert.True(part.IsVisible);
        Assert.True(Item(w, label).IsChecked);
        Assert.Equal(size, Size(w, id), 0);
        Assert.Equal(room, Room(w, id), 0);
    }

    [AvaloniaFact]
    public void Show_menu_lists_the_bars_and_show_everything_follows()
    {
        var w = NewWindow();
        var view = w.MenuLabels()["&View"];
        Assert.Equal(new[] { "&Show", "Show &Everything" }, view.SkipWhile(l => l != "&Show").Take(2));
        Assert.Equal(ShowLabels, Item(w, "&Show").Items.OfType<MenuItem>().Select(i => MenuText.ToWinForms((string)i.Header!)));
        Assert.All(ShowLabels, l => Assert.True(Item(w, l).IsChecked));
        // No duplicates: each part has exactly one item.
        foreach (var label in All.Select(r => r.Label))
            Assert.Single(AllItems(w.MainMenu), i => MenuText.ToWinForms((string)i.Header!) == label);
    }

    static IEnumerable<MenuItem> AllItems(ItemsControl c) => c.Items.OfType<MenuItem>().SelectMany(i => new[] { i }.Concat(AllItems(i)));

    [AvaloniaFact]
    public void Map_toolbar_hides_in_every_tab_including_later_ones()
    {
        var w = Game();
        Click(w, "&Map Toolbar");
        Assert.All(w.MapTabs.Views, v => Assert.False(w.MapTabs.ToolbarOf(v)!.IsVisible));
        var other = w.Session!.Model.Maps.FirstOrDefault(m => w.MapTabs.Views.All(v => v.Map.Id != m.Id));
        if (other != null)
        {
            var v = w.MapTabs.Open(other.Id)!;
            Assert.False(w.MapTabs.ToolbarOf(v)!.IsVisible);
        }
        Click(w, "&Map Toolbar");
        Assert.All(w.MapTabs.Views, v => Assert.True(w.MapTabs.ToolbarOf(v)!.IsVisible));
    }

    [AvaloniaFact]
    public void Hidden_parts_are_remembered()
    {
        var w = Game();
        foreach (var label in All.Select(r => r.Label)) Click(w, label);
        Assert.All(All.Select(r => r.Id), id => Assert.False(Part(w, id).IsVisible, id));
        // The app-wide set in ui.json (the alert bar and outliner keep their Settings switches, which WinForms shares).
        Assert.Equal(new[] { "log", "mapToolbar", "moduleTree", "palette", "phaseStrip", "phases", "statusBar", "tablesTree", "toolbar" },
            new WindowLayoutStore(LayoutRoot).LoadHiddenParts().Order(StringComparer.Ordinal));
        Assert.False(w.SettingsStore.Current.AlertBarVisible);
        Assert.False(w.SettingsStore.Current.OutlinerVisible);

        var w2 = Game();   // a new run (the log opens hidden although Task 2 docks it on every open)
        Assert.All(All.Select(r => r.Id), id => Assert.False(Part(w2, id).IsVisible, id));
        Assert.All(All.Select(r => r.Label), l => Assert.False(Item(w2, l).IsChecked, l));
        Assert.False(w2.LeftColumn.IsVisible);
        Assert.Equal(0, w2.LeftColumnWidth);
        Assert.True(w2.OpenModule(_module!));   // and opening a module again keeps them hidden
        w2.StartSetup(0);
        Pump();
        Assert.False(w2.LogHost.IsVisible);
        Assert.False(w2.LogShown);
    }

    [AvaloniaFact]
    public void Show_everything_restores_all_parts()
    {
        var w = Game();
        w.SetLeftSplits(0.3, 0.45);
        foreach (var label in All.Select(r => r.Label)) Click(w, label);
        Click(w, "Show &Everything");
        Assert.All(All.Select(r => r.Id), id => Assert.True(Part(w, id).IsVisible, id));
        Assert.All(All.Select(r => r.Label), l => Assert.True(Item(w, l).IsChecked, l));
        Assert.Equal((WindowLayoutStore.DefaultTreeFraction, WindowLayoutStore.DefaultPaletteFraction), w.LeftSplits);
        Assert.Equal(220, w.LeftColumnWidth);
        Assert.Empty(new WindowLayoutStore(LayoutRoot).LoadHiddenParts());
        Assert.Equal((WindowLayoutStore.DefaultTreeFraction, WindowLayoutStore.DefaultPaletteFraction), new WindowLayoutStore(LayoutRoot).LoadLeftSplits());
        var w2 = Game();
        Assert.All(All.Select(r => r.Id), id => Assert.True(Part(w2, id).IsVisible, id));
    }

    [AvaloniaFact]
    public void Left_column_collapses_only_when_all_three_are_off()
    {
        var w = Game();
        static int[] Splitters(MainWindow w) => w.LeftColumn.Children.OfType<GridSplitter>().Where(s => s.IsVisible).Select(Grid.GetRow).Order().ToArray();
        Click(w, "Module &Tree");
        Assert.True(w.LeftColumn.IsVisible);
        Assert.Equal(new[] { 0, 2 }, new[] { Grid.GetRow(w.Palette), Grid.GetRow(w.TablesTree) });   // palette | tables, no gap above
        Assert.Equal(new[] { 1 }, Splitters(w));
        Assert.Equal(new[] { 0.0, 0.0 }, w.LeftColumn.RowDefinitions.Skip(3).Select(r => r.Height.Value));

        Click(w, "&Charts && Tables");
        Assert.True(w.LeftColumn.IsVisible);
        Assert.Empty(Splitters(w));
        Assert.Equal(0, Grid.GetRow(w.Palette));
        Assert.True(Math.Abs(w.Palette.Bounds.Height - w.LeftColumn.Bounds.Height) < 1, "the palette fills the column");

        Click(w, "Counter &Palette");
        Assert.False(w.LeftColumn.IsVisible);
        Assert.False(w.LeftColumnSplitter.IsVisible);
        Assert.Equal(0, w.LeftColumnWidth);
        Assert.Equal(0, w.BodyWithTree.ColumnDefinitions[1].Width.Value);

        Click(w, "&Charts && Tables");
        Assert.True(w.LeftColumn.IsVisible);
        Assert.Equal(220, w.LeftColumnWidth);
        Assert.True(Math.Abs(w.TablesTree.Bounds.Height - w.LeftColumn.Bounds.Height) < 1, "the tables tree fills the column");

        // Not game master: the palette is absent, so the two trees off is enough.
        Click(w, "&Charts && Tables");
        Click(w, "Counter &Palette");
        Assert.True(w.LeftColumn.IsVisible);   // the palette alone
        var p = Game(gameMaster: false);
        p.SettingsStore.Update(x => x with { GameMasterMode = false });
        Pump();
        p.UpdateLayout();
        Assert.True(p.PaletteOn);
        Assert.False(p.Palette.IsVisible);
        Assert.False(p.LeftColumn.IsVisible);   // remembered: tree and tables off
        Assert.Equal(0, p.LeftColumnWidth);
        Click(p, "Module &Tree");
        Assert.True(p.LeftColumn.IsVisible);
        Assert.True(Math.Abs(p.ModuleTree.Bounds.Height - p.LeftColumn.Bounds.Height) < 1, "the module tree fills the column");
    }

    [AvaloniaFact]
    public void Splitter_between_tree_and_palette_moves_them_while_the_tables_tree_is_hidden()
    {
        var w = Game();
        Click(w, "&Charts && Tables");
        var tables = w.LeftSplits.Tree + w.LeftSplits.Palette;
        var tree = w.ModuleTree.Bounds.Height;
        var sp = w.LeftColumn.Children.OfType<GridSplitter>().Single(s => s.IsVisible);
        Assert.Equal(1, Grid.GetRow(sp));
        sp.RaiseEvent(new VectorEventArgs { RoutedEvent = Avalonia.Controls.Primitives.Thumb.DragStartedEvent, Vector = new Avalonia.Vector(0, 0) });
        sp.RaiseEvent(new VectorEventArgs { RoutedEvent = Avalonia.Controls.Primitives.Thumb.DragDeltaEvent, Vector = new Avalonia.Vector(0, 40) });
        sp.RaiseEvent(new VectorEventArgs { RoutedEvent = Avalonia.Controls.Primitives.Thumb.DragCompletedEvent, Vector = new Avalonia.Vector(0, 40) });
        Pump(); w.LeftColumn.UpdateLayout();
        Assert.True(w.ModuleTree.Bounds.Height > tree + 20);
        Assert.Equal(tables, w.LeftSplits.Tree + w.LeftSplits.Palette, 6);   // the hidden tables tree's share is untouched
    }

    [AvaloniaFact]
    public void Replay_bar_and_online_banner_are_not_toggles()
    {
        var w = Game();
        var labels = AllItems(w.MainMenu).Select(i => MenuText.ToWinForms((string)i.Header!)).ToList();
        Assert.DoesNotContain(labels, l => l.Contains("Replay", StringComparison.OrdinalIgnoreCase) || l.Contains("Banner", StringComparison.OrdinalIgnoreCase) || l.Contains("Online", StringComparison.OrdinalIgnoreCase) && l.Contains("Bar"));
        Assert.False(w.ReplayBarHost.IsVisible);
        Assert.False(w.OnlineBanner.IsVisible);
        Click(w, "Show &Everything");
        Assert.False(w.ReplayBarHost.IsVisible);   // still automatic: nothing to replay, not online
        Assert.False(w.OnlineBanner.IsVisible);
    }

    [AvaloniaFact]
    public void Shortcuts_and_menus_work_with_the_bars_hidden()
    {
        var w = Game();
        Click(w, "Main &Toolbar");
        Click(w, "Stat&us Bar");
        Assert.True(w.LogShown);
        w.KeyPress(Key.L, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.L, null);   // View > Log's Ctrl+Shift+L
        Pump();
        Assert.False(w.LogShown);
        Assert.False(Item(w, "&Log").IsChecked);
        Assert.True(Item(w, "&Save Game").IsEnabled);   // the toolbar's commands stay in the menus
    }
}
