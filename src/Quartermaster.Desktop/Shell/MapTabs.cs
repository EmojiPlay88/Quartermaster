using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// One tab per map the player may see (<see cref="ModuleSession.CanSeeMap"/>), each a <see cref="MapView"/>, as the WinForms map windows:
/// board tiles prepared in the background (MapWindow.PrepareTilesAsync), drops go through the main window's action port, map command
/// errors go to the session log (MapWindow.Run), palette counters dropped on a view are placed (MapCanvas.OnDragDrop), and the cursor's
/// map location and the zoom are reported for the status bar.
/// </summary>
public sealed class MapTabs : TabControl
{
    protected override Type StyleKeyOverride => typeof(TabControl);

    readonly List<MapView> _views = new();
    readonly Dictionary<MapView, (Task Task, CancellationTokenSource Cts)> _tiling = new();
    ModuleSession? _s;
    Func<IActionPort?>? _actions;
    CancellationTokenSource _cts = new();
    bool _showGrid;
    Settings? _settings;
    (double Min, double Max)? _tabWidths;

    public MapTabs()
    {
        SelectionChanged += (_, e) =>
        {
            if (e.Source != this) return;   // a ListBox inside a tab must not count
            if (ActiveView is { } v) StatusChanged?.Invoke(v, "-", MapStatus.Zoom(v.View));
            ActiveViewChanged?.Invoke();
        };
    }

    /// <summary>The views in tab order.</summary>
    public IReadOnlyList<MapView> Views => _views;

    /// <summary>Selects the tab of a view.</summary>
    public void Activate(MapView v)
    {
        if (Items.OfType<TabItem>().FirstOrDefault(t => ViewOf(t) == v) is { } tab) SelectedItem = tab;
    }

    /// <summary>The selected tab's view.</summary>
    public MapView? ActiveView => ViewOf(SelectedItem as TabItem);

    /// <summary>The map view inside a tab (each tab holds the map toolbar above its view, as a WinForms map window).</summary>
    static MapView? ViewOf(TabItem? t) => (t?.Content as DockPanel)?.Children.OfType<MapView>().FirstOrDefault();

    /// <summary>The toolbar of a view's tab (tests).</summary>
    internal MapToolbar? ToolbarOf(MapView v) => Items.OfType<TabItem>().Select(t => t.Content as DockPanel).FirstOrDefault(p => p?.Children.Contains(v) == true)?.Children.OfType<MapToolbar>().FirstOrDefault();

    /// <summary>A view's status: (view, location text, zoom text). Location is "-" after a fit/centre/menu zoom or a tab switch (cursor
    /// location unknown, as MapCanvas.ReportStatus(null)) and null when only the zoom changed (keep the shown location).</summary>
    public event Action<MapView, string?, string>? StatusChanged;

    /// <summary>A short message for the status bar (palette drops, "Select a counter first"), as MapCanvas.DropMessage.</summary>
    public event Action<string>? DropMessage;

    /// <summary>A view's tile preparation progress 0..1; null when done.</summary>
    public event Action<MapView, double?>? TilingProgress;

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

    /// <summary>Shows the session's maps (none for null). <paramref name="actions"/> is the main window's action port.</summary>
    public void Bind(ModuleSession? s, Func<IActionPort?>? actions)
    {
        Clear();
        _s = s;
        _actions = actions;
        if (s == null) return;
        Rebuild();
        var main = s.Model.Maps.FirstOrDefault(m => !m.IsOrgDisplay) ?? s.Model.Maps.FirstOrDefault();
        var tab = Items.OfType<TabItem>().FirstOrDefault(t => ViewOf(t)?.Map == main) ?? Items.OfType<TabItem>().FirstOrDefault();
        if (tab != null) SelectedItem = tab;
    }

    /// <summary>Brings the tabs in line with the maps the player may see now (hot-seat viewers can hide maps). Kept views stay as they are.</summary>
    public void Rebuild()
    {
        if (_s is not { } s) return;
        var wanted = s.Model.Maps.Where(m => s.CanSeeMap(m.Id)).ToList();
        var have = _views.ToDictionary(v => v.Map);
        if (wanted.SequenceEqual(_views.Select(v => v.Map))) return;
        var selected = ActiveView?.Map;
        var tabs = new List<TabItem>();
        var views = new List<MapView>();
        foreach (var m in wanted)
        {
            var v = have.TryGetValue(m, out var kept) ? kept : Create(s, m);
            views.Add(v);
            tabs.Add(Items.OfType<TabItem>().FirstOrDefault(t => ViewOf(t) == v) ?? new TabItem { Header = m.Name, Content = Host(v) });
        }
        foreach (var gone in _views.Except(views).ToList())
            if (_tiling.Remove(gone, out var t)) t.Cts.Cancel();   // a map the player may no longer see stops preparing its tiles
        _views.Clear();
        _views.AddRange(views);
        Items.Clear();
        foreach (var t in tabs) { SizeTab(t); Items.Add(t); }
        SelectedItem = tabs.FirstOrDefault(t => ViewOf(t)?.Map == selected) ?? tabs.FirstOrDefault();
    }

    /// <summary>Removes every tab and stops tile preparation.</summary>
    public void Clear()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        foreach (var t in _tiling.Values) t.Cts.Dispose();
        _tiling.Clear();
        _views.Clear();
        Items.Clear();
        _s = null;
        _actions = null;
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
