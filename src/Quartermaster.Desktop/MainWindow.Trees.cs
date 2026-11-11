using System.Drawing;
using Avalonia.Controls;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop;

/// <summary>
/// The left column (module tree over tables tree, View > Module Tree, the remembered split) and the floating log, ported from WinForms
/// MainForm / LogDock. Layout memory (split fraction, the log window's bounds per module) goes through <see cref="WindowLayoutStore"/>.
/// </summary>
public partial class MainWindow
{
    WindowLayoutStore _layout = null!;
    MenuItem _viewTree = null!;
    PanelDockState _logState = null!;
    PanelFloat _logFloat = null!;
    double _leftWidth = 220;       // follows the column splitter
    bool _applyingSplit;
    bool _logFloated;              // the log window was shown for this module: its DPI is the current one

    /// <summary>Test seam: the screens the floating windows are fitted onto (null = the platform's).</summary>
    internal Func<IReadOnlyList<ScreenInfo>>? ScreensProvider { get; set; }

    /// <summary>The module tree of the left column.</summary>
    public ModuleTree ModuleTree => ModuleTreeHost;

    /// <summary>The tables tree of the left column.</summary>
    public TablesTree TablesTree => TablesTreeHost;

    /// <summary>True while View > Module Tree is checked (the left column shows).</summary>
    public bool ModuleTreeShown => _viewTree.IsChecked;

    internal Grid LeftColumn => LeftColumnGrid;
    internal double LeftColumnWidth => BodyWithTree.ColumnDefinitions[0].Width.Value;

    /// <summary>Fraction of the left column's height the module tree takes (0.2 - 0.8).</summary>
    public double LeftSplit
    {
        get
        {
            var rows = LeftColumnGrid.RowDefinitions;
            double a = rows[0].Height.Value, b = rows[2].Height.Value;
            return a + b > 0 && double.IsFinite(a + b)
                ? Math.Clamp(a / (a + b), WindowLayoutStore.MinLeftSplit, WindowLayoutStore.MaxLeftSplit)
                : WindowLayoutStore.DefaultLeftSplit;
        }
    }

    /// <summary>Where the log lives: floating window (null while docked).</summary>
    public FloatPanelWindow? LogFloat => _logFloat.Window;

    internal Control? LogHostChild => LogHost.Child;

    /// <summary>Set up before the menu is built (see the constructor).</summary>
    void InitTrees(string? layoutRoot)
    {
        _layout = new WindowLayoutStore(layoutRoot ?? AppDataRoot.Path);
        _viewTree = new MenuItem { Header = MenuText.FromWinForms("Module &Tree"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
        // Avalonia's menu flips a CheckBox item's IsChecked itself on click (WinForms CheckOnClick); follow the flag.
        _viewTree.PropertyChanged += (_, e) => { if (e.Property == MenuItem.IsCheckedProperty) ApplyLeftColumn(); };

        SetRows(_layout.LoadLeftSplit());
        foreach (var i in new[] { 0, 2 })
            LeftColumnGrid.RowDefinitions[i].PropertyChanged += (_, e) =>
            {
                if (!_applyingSplit && e.Property == RowDefinition.HeightProperty) _layout.SaveLeftSplit(LeftSplit);
            };
        ModuleTreeHost.NodeActivated += n => Guard(() => OnTreeNodeActivated(n));
        // WinForms MainForm: activating a table or formula opens the workbench at it.
        TablesTreeHost.TableActivated += id => Guard(() => ShowWorkbenchItem(w => w.ShowTable(id)));
        TablesTreeHost.FormulaActivated += id => Guard(() => ShowWorkbenchItem(w => w.ShowFormula(id)));

        _logState = new PanelDockState("log", () => PanelDockState.LogDefault(MainRect()));
        _logFloat = new PanelFloat(_logState, "Log", LogHost, () => this, () => ScreensProvider?.Invoke() ?? PanelFloat.ScreensOf(this));
        _logState.Changed += () =>
        {
            if (_logState.Floating) _logFloated = true;
            _log?.SetFloating(_logState.Floating);
            ApplyLog();
        };
    }

    RectangleI MainRect()
    {
        var s = RenderScaling;
        return new RectangleI(Position.X, Position.Y, (int)Math.Round(Bounds.Width * s), (int)Math.Round(Bounds.Height * s));
    }

    void SetRows(double fraction)
    {
        var f = double.IsFinite(fraction) ? Math.Clamp(fraction, WindowLayoutStore.MinLeftSplit, WindowLayoutStore.MaxLeftSplit) : WindowLayoutStore.DefaultLeftSplit;
        var rows = LeftColumnGrid.RowDefinitions;
        _applyingSplit = true;
        try
        {
            rows[0].Height = new GridLength(f, GridUnitType.Star);
            rows[2].Height = new GridLength(1 - f, GridUnitType.Star);
        }
        finally { _applyingSplit = false; }
    }

    /// <summary>Sets and remembers the split (what dragging the splitter does).</summary>
    internal void SetLeftSplit(double fraction)
    {
        SetRows(fraction);
        _layout.SaveLeftSplit(LeftSplit);
    }

    void ApplyLeftColumn()
    {
        var shown = _viewTree.IsChecked;
        var cols = BodyWithTree.ColumnDefinitions;
        if (!shown && cols[0].Width.Value >= 1) _leftWidth = cols[0].Width.Value;   // keep what the user dragged it to
        LeftColumnGrid.IsVisible = LeftColumnSplitter.IsVisible = shown;
        cols[0].Width = new GridLength(shown ? _leftWidth : 0);
        cols[1].Width = new GridLength(shown ? 4 : 0);
    }

    void LoadTrees(ModuleSession s)
    {
        ModuleTreeHost.Load(s.Model, id => s.CanSeeMap(id));
        TablesTreeHost.Bind(s);
    }

    void ClearTrees()
    {
        ModuleTreeHost.Clear();
        TablesTreeHost.Bind(null);
    }

    /// <summary>WinForms MainForm.OnNodeActivated.</summary>
    void OnTreeNodeActivated(WindowTreeNode n)
    {
        if (_session is not { } s) return;
        if (n.Kind == WindowNodeKind.Setup && n.Ref is SetupDef)
        {
            if (s.IsGuest) { SetStatus(ModuleSession.GuestBusyText); return; }   // a guest plays the host's game only
            // Same difficulty choice as File > Load Setup.
            RunAsync(LoadSetupAsync);
            return;
        }
        if (n.Kind is WindowNodeKind.Map or WindowNodeKind.OrgDisplay && n.Ref is MapDef map)
        {
            var view = Maps.Views.FirstOrDefault(v => v.Map.Id == map.Id);
            if (view != null) Maps.Activate(view);
            return;
        }
        if (n.Kind == WindowNodeKind.Chart && n.Ref is ChartDef chart)
        {
            OpenChart(chart);
            return;
        }
        if (n.Kind == WindowNodeKind.Document && n.Ref is DocumentDef doc) OpenDocument(doc);
    }

    // ---------- the log's place (docked at the bottom or floating) ----------

    /// <summary>The module's layout memory for the log, then the panel in its place (dock host or floating window).</summary>
    void PlaceLog(ModuleSession s, LogPanel log)
    {
        _logFloated = false;
        _logState.FloatBounds = null;
        _logState.Load(_layout.LoadModule(s.Archive.Sha256).TryGetValue("log", out var saved) ? saved : null);
        log.PopToggleRequested += () => Guard(_logFloat.Toggle);
        log.SetFloating(_logState.Floating);
        _logFloat.Attach(log);
    }

    /// <summary>Docks the log back (the game is closing) and remembers where its window was, per module.</summary>
    void ReleaseLog()
    {
        if (_log == null) return;
        _logFloat.Detach();
        if (_session is { } s && _logState.Save() is { } saved)
        {
            if (_logFloated) saved = saved with { Dpi = (int)Math.Round(RenderScaling * 96) };
            var all = new Dictionary<string, SavedWindow>(_layout.LoadModule(s.Archive.Sha256)) { ["log"] = saved };
            _layout.SaveModule(s.Archive.Sha256, all);
        }
    }
}
