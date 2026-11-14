using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Desktop.Map;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Shell;

/// <summary>Status-bar texts of a map view (shared with WinForms: <see cref="MapStatusText"/>).</summary>
public static class MapStatus
{
    public static string Location(MapDef map, PointD m) => MapStatusText.Location(map, m);

    public static string Zoom(Viewport v) => MapStatusText.Zoom(v.Zoom);
}

/// <summary>
/// The open maps as tabs, each a <see cref="MapView"/>, as the WinForms map windows: only the main map opens with a module
/// (MainForm.OpenMainMap), other maps open from the module tree, and every tab closes (× on the tab, middle click, or Close / Close Others /
/// Close All on its right-click menu, as DocumentTabStrip). A map the player may not see (<see cref="ModuleSession.CanSeeMap"/>) never
/// has a tab; it stays in the user's open set and comes back when the player may see it again. The tabs sit on one row that scrolls.
/// Board tiles are prepared in the background (MapWindow.PrepareTilesAsync), drops go through the main window's action port, map command
/// errors go to the session log (MapWindow.Run), palette counters dropped on a view are placed (MapCanvas.OnDragDrop), and the cursor's
/// map location and the zoom are reported for the status bar.
/// </summary>
public sealed class MapTabs : TabControl
{
    protected override Type StyleKeyOverride => typeof(TabControl);

    /// <summary>WinForms SystemColors.AppWorkspace, shown when no tab is open (the empty MDI area).</summary>
    static readonly IBrush Workspace = new SolidColorBrush(Color.FromRgb(0xAB, 0xAB, 0xAB));

    readonly List<MapView> _views = new();
    readonly List<string> _open = new();   // the user's open maps in tab order, including maps the player may not see now
    readonly Dictionary<MapView, (Task Task, CancellationTokenSource Cts)> _tiling = new();
    ModuleSession? _s;
    Func<IActionPort?>? _actions;
    CancellationTokenSource _cts = new();
    bool _showGrid;
    Settings? _settings;
    (double Min, double Max)? _tabWidths;
    int _quiet;   // > 0 while tabs are rebuilt: selection changes on the way are not "the user's open maps changed"

    public MapTabs()
    {
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 0) });
        Template = StripTemplate();
        SelectionChanged += (_, e) =>
        {
            if (e.Source != this) return;   // a ListBox inside a tab must not count
            if (ActiveView is { } v) StatusChanged?.Invoke(v, "-", MapStatus.Zoom(v.View));
            (SelectedItem as TabItem)?.BringIntoView();
            ActiveViewChanged?.Invoke();
            if (_quiet == 0 && _s != null) OpenMapsChanged?.Invoke();
        };
        ApplyBackdrop();
    }

    /// <summary>The classic theme's tab template with the strip on one row in a horizontal scroller (the wheel scrolls it sideways).</summary>
    static FuncControlTemplate<MapTabs> StripTemplate() => new((t, ns) =>
    {
        var items = new ItemsPresenter { Name = "PART_ItemsPresenter" };
        items.Bind(ItemsPresenter.ItemsPanelProperty, t.GetObservable(ItemsPanelProperty));
        ns.Register(items.Name, items);
        var strip = new ScrollViewer
        {
            Name = "PART_TabStrip", Content = items, ZIndex = 1, Margin = new Thickness(0, 0, 0, -1),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        strip.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            if (d == 0) return;
            strip.Offset = strip.Offset.WithX(Math.Max(0, strip.Offset.X - d * 50));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        DockPanel.SetDock(strip, Dock.Top);
        var content = new ContentPresenter { Name = "PART_SelectedContentHost" };
        content.Bind(ContentPresenter.ContentProperty, t.GetObservable(SelectedContentProperty));
        content.Bind(ContentPresenter.ContentTemplateProperty, t.GetObservable(SelectedContentTemplateProperty));
        content.Bind(ContentPresenter.MarginProperty, t.GetObservable(PaddingProperty));
        ns.Register(content.Name, content);
        var page = new Border { BorderThickness = new Thickness(1), Child = content };
        page.Bind(Border.BackgroundProperty, t.GetObservable(BackgroundProperty));
        page.Bind(Border.BorderBrushProperty, t.GetObservable(BorderBrushProperty));
        return new DockPanel { Children = { strip, page } };
    });

    /// <summary>The views in tab order.</summary>
    public IReadOnlyList<MapView> Views => _views;

    /// <summary>The ids of the maps the user keeps open, in tab order. Includes maps the current player may not see (no tab for now).</summary>
    public IReadOnlyList<string> OpenMapIds => _open;

    /// <summary>The user opened or closed a map, or selected another tab (what layout memory keeps per module).</summary>
    public event Action? OpenMapsChanged;

    /// <summary>True while no tab is open: the page shows the workspace grey.</summary>
    internal bool ShowsBackdrop => _views.Count == 0;

    /// <summary>Selects the tab of a view.</summary>
    public void Activate(MapView v)
    {
        if (TabOf(v) is { } tab) SelectedItem = tab;
    }

    /// <summary>The selected tab's view.</summary>
    public MapView? ActiveView => ViewOf(SelectedItem as TabItem);

    /// <summary>The map view inside a tab (each tab holds the map toolbar above its view, as a WinForms map window).</summary>
    static MapView? ViewOf(TabItem? t) => (t?.Content as DockPanel)?.Children.OfType<MapView>().FirstOrDefault();

    /// <summary>A view's tab (tests).</summary>
    internal TabItem? TabOf(MapView v) => Items.OfType<TabItem>().FirstOrDefault(t => ViewOf(t) == v);

    /// <summary>The × button on a view's tab (tests).</summary>
    internal Button? CloseButtonOf(MapView v) => (TabOf(v)?.Header as DockPanel)?.Children.OfType<Button>().FirstOrDefault();

    /// <summary>The right-click menu of a view's tab (tests).</summary>
    internal ContextMenu? MenuOf(MapView v) => TabOf(v)?.ContextMenu;

    /// <summary>The toolbar of a view's tab (tests).</summary>
    internal MapToolbar? ToolbarOf(MapView v) => Items.OfType<TabItem>().Select(t => t.Content as DockPanel).FirstOrDefault(p => p?.Children.Contains(v) == true)?.Children.OfType<MapToolbar>().FirstOrDefault();

    /// <summary>A view's status: (view, location text, zoom text). Location is "-" after a fit/centre/menu zoom or a tab switch (cursor
    /// location unknown, as MapCanvas.ReportStatus(null)) and null when only the zoom changed (keep the shown location).</summary>
    public event Action<MapView, string?, string>? StatusChanged;

    /// <summary>A short message for the status bar (palette drops, "Select a counter first"), as MapCanvas.DropMessage.</summary>
    public event Action<string>? DropMessage;

    /// <summary>A view's tile preparation progress 0..1; null when done.</summary>
    public event Action<MapView, double?>? TilingProgress;

    /// <summary>The selected tab changed (<see cref="ActiveView"/> is null once no tab is open).</summary>
    public event Action? ActiveViewChanged;

    /// <summary>Dialogs and shortcut texts for the views' right-click menus (the main window's).</summary>
    public MapMenuServices? MenuServices
    {
        get;
        set
        {
            field = value;
            foreach (var v in _views) v.MenuServices = value;
        }
    }

    /// <summary>Forces grids on every view (View → Show Grid).</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set
        {
            _showGrid = value;
            foreach (var v in _views) v.ShowGrid = value;
        }
    }

    /// <summary>The map settings for every open view and every view opened later (<see cref="MapView.ApplySettings"/>).</summary>
    public void ApplySettings(Settings s)
    {
        _settings = s;
        foreach (var v in _views) v.ApplySettings(s);
    }

    /// <summary>Tab width limits (Settings TabMinWidth / TabMaxWidth), as WinForms DocumentTabStrip.SetWidths: min at least 20, max at least min.</summary>
    public void SetTabWidths(int min, int max)
    {
        var lo = Math.Max(20, min);
        _tabWidths = (lo, Math.Max(lo, max));
        foreach (var t in Items.OfType<TabItem>()) SizeTab(t);
    }

    void SizeTab(TabItem t)
    {
        if (_tabWidths is not { } w) return;
        t.MinWidth = w.Min;
        t.MaxWidth = w.Max;
    }

    /// <summary>Shows the session's maps (none for null). <paramref name="actions"/> is the main window's action port. The remembered open
    /// maps (layout memory; ids no longer in the module are ignored) open with the remembered one selected; with none left, only the main
    /// map (the first map that is not an org display, as WinForms MainForm.OpenMainMap).</summary>
    public void Bind(ModuleSession? s, Func<IActionPort?>? actions, IReadOnlyList<string>? remembered = null, string? selected = null)
    {
        Clear();
        _s = s;
        _actions = actions;
        if (s == null) return;
        var main = s.Model.Maps.FirstOrDefault(m => !m.IsOrgDisplay) ?? s.Model.Maps.FirstOrDefault();
        foreach (var id in remembered ?? Array.Empty<string>())
            if (!_open.Contains(id) && s.Model.Maps.Any(m => m.Id == id)) _open.Add(id);
        if (_open.Count == 0 && main != null) _open.Add(main.Id);
        _quiet++;
        try
        {
            Sync(null);
            var tabs = Items.OfType<TabItem>().ToList();
            var tab = tabs.FirstOrDefault(t => ViewOf(t)?.Map.Id == selected) ?? tabs.FirstOrDefault(t => ViewOf(t)?.Map == main) ?? tabs.FirstOrDefault();
            if (tab != null) SelectedItem = tab;
        }
        finally { _quiet--; }
    }

    /// <summary>Opens the map's tab (when the player may see it) and selects it; the view, or null when the map is unknown or hidden.</summary>
    public MapView? Open(string mapId)
    {
        if (_s is not { } s || s.Model.Maps.FirstOrDefault(m => m.Id == mapId) is not { } map || !s.CanSeeMap(map.Id)) return null;
        var added = !_open.Contains(map.Id);
        if (added) _open.Add(map.Id);
        Change(map, added);
        return _views.FirstOrDefault(v => v.Map == map);
    }

    /// <summary>Closes a view's tab (its tile preparation stops).</summary>
    public void Close(MapView v)
    {
        if (!_views.Contains(v) || !_open.Remove(v.Map.Id)) return;
        Change(null, true);
    }

    /// <summary>Closes every tab but the view's (open maps hidden from the player now are forgotten too).</summary>
    public void CloseOthers(MapView v)
    {
        if (!_views.Contains(v)) return;
        _open.RemoveAll(id => id != v.Map.Id);
        Change(v.Map, true);
    }

    /// <summary>Closes every tab.</summary>
    public void CloseAll()
    {
        if (_open.Count == 0) return;
        _open.Clear();
        Change(null, true);
    }

    void Change(MapDef? select, bool openChanged)
    {
        var before = SelectedItem;
        _quiet++;
        try { Sync(select); }
        finally { _quiet--; }
        if (openChanged || !ReferenceEquals(before, SelectedItem)) OpenMapsChanged?.Invoke();
    }

    /// <summary>Brings the tabs in line with the maps the player may see now (hot-seat viewers can hide maps): the user's open maps that
    /// are visible. Kept views stay as they are.</summary>
    public void Rebuild()
    {
        _quiet++;
        try { Sync(null); }
        finally { _quiet--; }
    }

    /// <summary>Tabs = open maps the player may see, in open order; <paramref name="select"/> (else the selected map, else the tab now at
    /// its place) is selected.</summary>
    void Sync(MapDef? select)
    {
        if (_s is not { } s) return;
        var wanted = _open.Select(id => s.Model.Maps.FirstOrDefault(m => m.Id == id)).OfType<MapDef>().Where(m => s.CanSeeMap(m.Id)).ToList();
        var activeBefore = ActiveView;
        if (!wanted.SequenceEqual(_views.Select(v => v.Map)))
        {
            var have = _views.ToDictionary(v => v.Map);
            var index = SelectedIndex;
            var tabs = new List<TabItem>();
            var views = new List<MapView>();
            foreach (var m in wanted)
            {
                var v = have.TryGetValue(m, out var kept) ? kept : Create(s, m);
                views.Add(v);
                tabs.Add(TabOf(v) ?? MakeTab(v));
            }
            var gone = _views.Except(views).ToList();
            _views.Clear();
            _views.AddRange(views);
            Items.Clear();
            foreach (var t in tabs) { SizeTab(t); Items.Add(t); }
            foreach (var v in gone) Drop(v);   // a closed or hidden map stops preparing its tiles
            SelectedItem = tabs.FirstOrDefault(t => activeBefore != null && ViewOf(t) == activeBefore)
                           ?? (tabs.Count == 0 ? null : tabs[Math.Clamp(index, 0, tabs.Count - 1)]);
            ApplyBackdrop();
        }
        if (select != null && Items.OfType<TabItem>().FirstOrDefault(t => ViewOf(t)?.Map == select) is { } pick) SelectedItem = pick;
        if (ActiveView == null && activeBefore != null) ActiveViewChanged?.Invoke();   // the last tab went: the status bar clears
    }

    void ApplyBackdrop()
    {
        if (ShowsBackdrop) Background = Workspace;
        else ClearValue(BackgroundProperty);
    }

    /// <summary>A view leaving the tabs: its tiling is cancelled and released, its popup closed (leaving the visual tree unhooks the rest).</summary>
    void Drop(MapView v)
    {
        if (_tiling.Remove(v, out var t))
        {
            t.Cts.Cancel();
            t.Cts.Dispose();
        }
        v.HidePopup();
    }

    /// <summary>A tab: the map's name with a small × (tooltip "Close"); a middle click closes it and its right-click menu offers Close /
    /// Close Others / Close All (WinForms DocumentTabStrip).</summary>
    TabItem MakeTab(MapView v)
    {
        var close = new Button
        {
            Content = "×", Padding = new Thickness(3, 0), Margin = new Thickness(5, 0, 0, 0), MinWidth = 0, MinHeight = 0,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, e) => { e.Handled = true; Close(v); };
        DockPanel.SetDock(close, Dock.Right);
        var name = new TextBlock { Text = v.Map.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var tab = new TabItem { Header = new DockPanel { Children = { close, name } }, Content = Host(v) };
        MenuItem Item(string text, Action a)
        {
            var i = new MenuItem { Header = text };
            i.Click += (_, _) => a();
            return i;
        }
        tab.ContextMenu = new ContextMenu { Items = { Item("Close", () => Close(v)), Item("Close Others", () => CloseOthers(v)), Item("Close All", CloseAll) } };
        tab.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Middle || !OnHeader(tab, e)) return;
            e.Handled = true;
            Close(v);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        return tab;
    }

    /// <summary>True when the pointer is on the tab's header, not on its page (a middle click on the map is the map's).</summary>
    static bool OnHeader(TabItem tab, PointerEventArgs e)
    {
        var p = e.GetPosition(tab);
        return p.X >= 0 && p.Y >= 0 && p.X < tab.Bounds.Width && p.Y < tab.Bounds.Height;
    }

    /// <summary>Removes every tab and stops tile preparation (not reported as a change of the open maps: the module is closing).</summary>
    public void Clear()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        foreach (var t in _tiling.Values) t.Cts.Dispose();
        _tiling.Clear();
        _views.Clear();
        _open.Clear();
        _s = null;
        _actions = null;
        Items.Clear();
        ApplyBackdrop();
    }

    /// <summary>Completes when every current view has its board tiles (or gave up on them).</summary>
    public Task WhenTiled() => Task.WhenAll(_tiling.Values.Select(t => t.Task));

    /// <summary>True while the view's tile preparation may still run (false once cancelled, e.g. its tab was removed).</summary>
    internal bool IsPreparingTiles(MapView v) => _tiling.TryGetValue(v, out var t) && !t.Cts.IsCancellationRequested && !t.Task.IsCompleted;

    /// <summary>Tab content: the toolbar on top, the map filling the rest.</summary>
    static DockPanel Host(MapView v)
    {
        var bar = new MapToolbar(v);
        DockPanel.SetDock(bar, Dock.Top);
        return new DockPanel { Children = { bar, v } };
    }

    MapView Create(ModuleSession s, MapDef map)
    {
        var v = new MapView(s, map) { ShowGrid = _showGrid, Preparing = map.Boards.Count > 0 ? 0 : null, MenuServices = MenuServices };
        if (_settings != null) v.ApplySettings(_settings);
        v.StatusMessage += m => DropMessage?.Invoke(m);
        v.Guarded = a => Run(s, map, a);
        v.Actions = () => _actions?.Invoke();
        // As MapCanvas: pan, resize and tiles arriving update only the zoom; fit/centre/menu zoom clear the location; the pointer and the
        // wheel (after MapView's class handler zoomed) report the cursor's map location.
        v.ViewportChanged += (_, _) => StatusChanged?.Invoke(v, null, MapStatus.Zoom(v.View));
        v.ViewReset += (_, _) => StatusChanged?.Invoke(v, "-", MapStatus.Zoom(v.View));
        void ReportAt(PointerEventArgs e) => StatusChanged?.Invoke(v, MapStatus.Location(map, v.ScreenToMap(e.GetPosition(v))), MapStatus.Zoom(v.View));
        v.AddHandler(PointerMovedEvent, (_, e) => ReportAt(e), RoutingStrategies.Bubble, handledEventsToo: true);
        v.AddHandler(PointerWheelChangedEvent, (_, e) => ReportAt(e), RoutingStrategies.Bubble, handledEventsToo: true);
        DragDrop.SetAllowDrop(v, true);
        v.AddHandler(DragDrop.DragEnterEvent, (_, e) => OnDragOver(v, e));
        v.AddHandler(DragDrop.DragOverEvent, (_, e) => OnDragOver(v, e));
        v.AddHandler(DragDrop.DropEvent, (_, e) => OnDrop(v, e));
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _tiling[v] = (PrepareTilesAsync(s, v, cts.Token), cts);
        return v;
    }

    /// <summary>Map commands that fail are logged, not shown (WinForms MapWindow.Run).</summary>
    static void Run(ModuleSession s, MapDef map, Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("map command failed: " + ex);
            s.Log.Add(DiagnosticSeverity.Error, "command", "A command failed: " + ex.Message, map.Name);
        }
    }

    void OnDragOver(MapView v, DragEventArgs e)
    {
        e.DragEffects = PaletteDrop.SlotFrom(v.Session, e.DataTransfer) != null && v.Session.Game != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(MapView v, DragEventArgs e)
    {
        e.Handled = true;
        if (PaletteDrop.SlotFrom(v.Session, e.DataTransfer) is not { } slot) { e.DragEffects = DragDropEffects.None; return; }
        var at = v.ScreenToMap(e.GetPosition(v));
        v.Guarded!(() =>
        {
            var (_, msg) = PaletteDrop.Place(v.Session, v.Map, slot, at, v.Snap, _actions?.Invoke());
            DropMessage?.Invoke(msg);
        });
        e.DragEffects = DragDropEffects.Copy;
        v.InvalidateVisual();
    }

    /// <summary>MapWindow.PrepareTilesAsync: each board's tiles in turn, a placeholder for a board that fails, progress for the status bar.</summary>
    async Task PrepareTilesAsync(ModuleSession s, MapView v, CancellationToken ct)
    {
        var boards = v.Map.Boards;
        try
        {
            for (var i = 0; i < boards.Count; i++)
            {
                var index = i;
                var progress = new Progress<double>(p => Report(v, (index + p) / boards.Count, ct));
                TileSet ts;
                try
                {
                    ts = await s.Tiler.EnsureTilesAsync(boards[i], progress, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    s.Log.Add(DiagnosticSeverity.Error, "tiles", $"Board '{boards[i].Name}' could not be prepared: {ex.Message}", v.Map.Name);
                    ts = new TileSet("", Math.Max(boards[i].Width, 1), Math.Max(boards[i].Height, 1), 0, isPlaceholder: true);
                }
                if (ct.IsCancellationRequested) return;
                v.SetTiles(BoardTiler.TileKey(boards[i]), ts);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested) return;
        v.Preparing = null;
        TilingProgress?.Invoke(v, null);
    }

    void Report(MapView v, double fraction, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || v.Preparing == null) return;
        v.Preparing = fraction;
        TilingProgress?.Invoke(v, fraction);
    }
}
