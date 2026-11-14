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
    MenuItem _viewPalette = null!;
    double _treeFraction = WindowLayoutStore.DefaultTreeFraction, _paletteFraction = WindowLayoutStore.DefaultPaletteFraction;   // of the column height, palette showing
    const string PaletteNeedsGameMaster = "Turn on game master mode in Settings > General to place counters";
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

    /// <summary>The palette is switched on in View > Counter Palette (remembered in ui.json; default on). It still needs a game and game master mode to show.</summary>
    public bool PaletteOn => _viewPalette.IsChecked;

    /// <summary>The tables tree's grid row: 4 under the palette, or 2 (right under the visible splitter) while the palette is hidden, so that splitter borders the two trees.</summary>
    int TablesRow => Grid.GetRow(TablesTreeHost);

    bool PaletteShowing => _session?.Game != null && GameMaster && PaletteOn;

    /// <summary>Fractions of the left column's height the module tree and the palette take with the palette showing (the tables tree gets the rest).</summary>
    public (double Tree, double Palette) LeftSplits => (_treeFraction, _paletteFraction);

    /// <summary>The module tree's share of module tree + tables tree (0.2 - 0.8), whether or not the palette shows.</summary>
    public double LeftSplit
    {
        get
        {
            var rows = LeftColumnGrid.RowDefinitions;
            double a = rows[0].Height.Value, b = rows[TablesRow].Height.Value;
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

        _viewPalette = new MenuItem { Header = MenuText.FromWinForms("Counter &Palette"), ToggleType = MenuItemToggleType.CheckBox, IsEnabled = false, IsChecked = _layout.LoadPaletteShown() };
        _viewPalette.PropertyChanged += (_, e) =>
        {
            if (e.Property != MenuItem.IsCheckedProperty) return;
            _layout.SavePaletteShown(_viewPalette.IsChecked);
            UpdatePaletteUi();
        };
        (_treeFraction, _paletteFraction) = _layout.LoadLeftSplits();
        SetRows();
        foreach (var i in new[] { 0, 2, 4 })
            LeftColumnGrid.RowDefinitions[i].PropertyChanged += (_, e) =>
            {
                if (!_applyingSplit && e.Property == RowDefinition.HeightProperty) RememberRows();
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

    /// <summary>Lays the rows out from the remembered fractions: palette showing = tree / palette / tables; hidden = its rows are 0 and the trees split as before.</summary>
    void SetRows()
    {
        var rows = LeftColumnGrid.RowDefinitions;
        var palette = PaletteShowing;
        _applyingSplit = true;
        try
        {
            Grid.SetRow(TablesTreeHost, palette ? 4 : 2);
            rows[0].Height = new GridLength(_treeFraction, GridUnitType.Star);
            rows[2].Height = new GridLength(palette ? _paletteFraction : 1 - _treeFraction - _paletteFraction, GridUnitType.Star);
            rows[3].Height = new GridLength(palette ? 4 : 0);
            rows[4].Height = palette ? new GridLength(1 - _treeFraction - _paletteFraction, GridUnitType.Star) : new GridLength(0);
        }
        finally { _applyingSplit = false; }
    }

    /// <summary>A splitter was dragged: read the rows back into the fractions and remember them (palette hidden: its share is kept, the trees share the rest).</summary>
    void RememberRows()
    {
        var rows = LeftColumnGrid.RowDefinitions;
        if (PaletteShowing)
        {
            double a = rows[0].Height.Value, p = rows[2].Height.Value, c = rows[4].Height.Value, total = a + p + c;
            if (!(total > 0) || !double.IsFinite(total)) return;
            (_treeFraction, _paletteFraction) = WindowLayoutStore.NormalizeSplits(a / total, p / total);
        }
        else
        {
            double a = rows[0].Height.Value, c = rows[2].Height.Value;
            if (!(a + c > 0) || !double.IsFinite(a + c)) return;
            _treeFraction = TreeForShare(a / (a + c));
        }
        _layout.SaveLeftSplits(_treeFraction, _paletteFraction);
    }

    /// <summary>The tree's fraction of the column for a share of the two trees; the palette's fraction stays as it is.</summary>
    double TreeForShare(double share) =>
        Math.Clamp(share * (1 - _paletteFraction), WindowLayoutStore.MinPartFraction, WindowLayoutStore.MaxSplitSum - _paletteFraction);

    /// <summary>Sets and remembers the module tree's share of the two trees (what dragging the splitter does with the palette hidden).</summary>
    internal void SetLeftSplit(double fraction)
    {
        var f = double.IsFinite(fraction) ? Math.Clamp(fraction, WindowLayoutStore.MinLeftSplit, WindowLayoutStore.MaxLeftSplit) : WindowLayoutStore.DefaultLeftSplit;
        _treeFraction = TreeForShare(f);
        SetRows();
        _layout.SaveLeftSplits(_treeFraction, _paletteFraction);
    }

    /// <summary>Sets and remembers both fractions of the column height.</summary>
    internal void SetLeftSplits(double tree, double palette)
    {
        (_treeFraction, _paletteFraction) = WindowLayoutStore.NormalizeSplits(tree, palette);
        SetRows();
        _layout.SaveLeftSplits(_treeFraction, _paletteFraction);
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
            Maps.Open(map.Id);   // a closed map's tab opens again (WinForms OpenMap)
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
        // The saved entry (possibly written by WinForms with Floating=true) is for the float bounds only: the log always opens docked and shown.
        _logState.DockBack();
        _logState.Wanted = true;
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
