using Avalonia.Controls;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop;

/// <summary>
/// Every part of the screen can be shown or hidden (port stage 6c, the user's "as customizable as possible"). One check item per part:
/// View > Module Tree / Log / Phase panel / Outliner / Counter Palette where they always were, View > Charts & Tables next to Module Tree,
/// the bars (and Charts & Tables again, the same state) in View > Show, and View > Show Everything to reset (every part shown, splits and dock sizes back to the defaults). Remembered app-wide: the hidden part
/// ids in ui.json (<see cref="WindowLayoutStore.LoadHiddenParts"/>), except the alert bar and the outliner, whose switches were already in
/// the settings (AlertBarVisible, OutlinerVisible; shared with WinForms and the Settings window). The replay bar and the online banner stay
/// automatic. Hidden parts take no space; keyboard shortcuts and menu commands work whatever is hidden.
/// </summary>
public partial class MainWindow
{
    /// <summary>The part ids, in View > Show order then the panels.</summary>
    public static readonly IReadOnlyList<string> PartIds =
        ["toolbar", "phaseStrip", "alertBar", "statusBar", "mapToolbar", "moduleTree", "tablesTree", "palette", "log", "phases", "outliner"];

    HashSet<string> _hidden = null!;
    MenuItem? _showAlertBar;
    readonly List<MenuItem> _partItems = new();   // the ui.json-backed check items, re-synced by Show Everything
    readonly Dictionary<string, List<MenuItem>> _partTwins = new();   // a part with two items (Charts & Tables): both show one state
    bool _syncingTwins;
    MenuItem _gameLastMoved = null!;
    MapView? _lastMovedView;
    bool _syncingLastMoved;

    /// <summary>Loads the hidden set (before the panels and menus that read it are made).</summary>
    void InitViewParts()
    {
        _layout = new WindowLayoutStore(_layoutRoot);
        _hidden = new HashSet<string>(_layout.LoadHiddenParts(), StringComparer.Ordinal);
    }

    /// <summary>Whether the user wants this part shown (some still need a game, a rules pack or game master mode to appear).</summary>
    internal bool PartShown(string id) => id switch
    {
        "alertBar" => SettingsStore.Current.AlertBarVisible,
        "outliner" => SettingsStore.Current.OutlinerVisible,
        _ => !_hidden.Contains(id),
    };

    /// <summary>Remembers a ui.json-backed part's wish (written only when it changes).</summary>
    void SetPartHidden(string id, bool hidden)
    {
        if (hidden ? !_hidden.Add(id) : !_hidden.Remove(id)) return;
        _layout.SaveHiddenParts(_hidden);
    }

    /// <summary>A check item for a ui.json-backed part: checked while shown; checking it (Avalonia flips it on click) remembers and applies.</summary>
    MenuItem PartItem(string id, string label, Action apply)
    {
        var mi = new MenuItem { Header = MenuText.FromWinForms(label), ToggleType = MenuItemToggleType.CheckBox, IsChecked = PartShown(id) };
        if (!_partTwins.TryGetValue(id, out var twins)) _partTwins[id] = twins = new();
        twins.Add(mi);
        mi.PropertyChanged += (_, e) =>
        {
            if (e.Property != MenuItem.IsCheckedProperty || _syncingTwins) return;
            SetPartHidden(id, !mi.IsChecked);
            _syncingTwins = true;
            try { foreach (var t in twins) t.IsChecked = mi.IsChecked; }
            finally { _syncingTwins = false; }
            Guard(apply);
        };
        _partItems.Add(mi);
        return mi;
    }

    /// <summary>Game > Last Moved: the selected map's last-moved outline (the map toolbar's Last moved), so it works with the toolbar hidden.
    /// Checked while the selected map outlines; off without a map tab.</summary>
    MenuItem BuildLastMovedItem()
    {
        _gameLastMoved = new MenuItem { Header = MenuText.FromWinForms("Last &Moved"), ToggleType = MenuItemToggleType.CheckBox };
        ToolTip.SetTip(_gameLastMoved, MapToolbar.LastMovedTip);
        _gameLastMoved.PropertyChanged += (_, e) =>
        {
            if (e.Property != MenuItem.IsCheckedProperty || _syncingLastMoved) return;
            if (Maps.ActiveView is { } v) Guard(() => v.HighlightLastMoved = _gameLastMoved.IsChecked);
            SyncLastMovedItem();
        };
        Maps.ActiveViewChanged += FollowActiveViewLastMoved;
        SyncLastMovedItem();
        return _gameLastMoved;
    }

    /// <summary>Game > Last Moved follows the selected tab's view (the toolbar button or another tab can change it).</summary>
    void FollowActiveViewLastMoved()
    {
        if (_lastMovedView != null) _lastMovedView.HighlightLastMovedChanged -= OnLastMovedChanged;
        _lastMovedView = Maps.ActiveView;
        if (_lastMovedView != null) _lastMovedView.HighlightLastMovedChanged += OnLastMovedChanged;
        SyncLastMovedItem();
    }

    void OnLastMovedChanged(bool _) => SyncLastMovedItem();

    void SyncLastMovedItem()
    {
        _syncingLastMoved = true;
        try
        {
            _gameLastMoved.IsEnabled = Maps.ActiveView != null;
            _gameLastMoved.IsChecked = Maps.ActiveView?.HighlightLastMoved == true;
        }
        finally { _syncingLastMoved = false; }
    }

    /// <summary>View > Show (the bars and the tables tree) and View > Show Everything.</summary>
    void BuildShowMenu(MenuItem view)
    {
        var show = new MenuItem { Header = MenuText.FromWinForms("&Show") };
        show.Items.Add(PartItem("toolbar", "Main &Toolbar", ApplyBars));
        show.Items.Add(PartItem("phaseStrip", "Phase &Strip", UpdatePhaseUi));
        // The alert bar's switch is Settings > Display "alert bar" (AlertBarVisible); UpdateAlertBarVisible keeps this item in step with it.
        _showAlertBar = new MenuItem { Header = MenuText.FromWinForms("&Alert Bar"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = SettingsStore.Current.AlertBarVisible };
        _showAlertBar.PropertyChanged += (_, e) =>
        {
            if (e.Property != MenuItem.IsCheckedProperty) return;
            var on = _showAlertBar.IsChecked;
            if (SettingsStore.Current.AlertBarVisible != on) Guard(() => SettingsStore.Update(x => x with { AlertBarVisible = on }));
        };
        show.Items.Add(_showAlertBar);
        show.Items.Add(PartItem("statusBar", "Stat&us Bar", ApplyBars));
        show.Items.Add(PartItem("mapToolbar", "&Map Toolbar", ApplyBars));
        show.Items.Add(PartItem("tablesTree", "&Charts && Tables", SetRows));   // also View > Charts & Tables, one state
        view.Items.Add(show);
        var all = new MenuItem { Header = MenuText.FromWinForms("Show &Everything") };
        all.Click += (_, _) => Guard(ShowEverything);
        view.Items.Add(all);
        ApplyBars();
    }

    /// <summary>The main toolbar, the status bar and every tab's map toolbar.</summary>
    void ApplyBars()
    {
        ToolbarHost.IsVisible = PartShown("toolbar");
        StatusBarHost.IsVisible = PartShown("statusBar");
        Maps.ToolbarsVisible = PartShown("mapToolbar");
    }

    /// <summary>View > Show Everything: every part shown (the ones that need a game, a pack or game master mode as soon as they can),
    /// the left column's splits and width and the docks' sizes back to their defaults.</summary>
    void ShowEverything()
    {
        _hidden.Clear();
        _layout.SaveHiddenParts(_hidden);
        var s = SettingsStore.Current;
        if (!s.AlertBarVisible || !s.OutlinerVisible) SettingsStore.Update(x => x with { AlertBarVisible = true, OutlinerVisible = true });

        (_treeFraction, _paletteFraction) = (WindowLayoutStore.DefaultTreeFraction, WindowLayoutStore.DefaultPaletteFraction);
        _layout.SaveLeftSplits(_treeFraction, _paletteFraction);
        _leftWidth = DefaultLeftWidth;
        BodyWithTree.ColumnDefinitions[0].Width = new GridLength(_leftWidth);
        _logHeight = DefaultLogHeight;
        if (LogHost.IsVisible) BodyAndLog.RowDefinitions[2].Height = new GridLength(_logHeight);
        _phaseWidth = DefaultPhaseWidth;
        if (PhaseHost.IsVisible) PhaseCol.Width = new GridLength(_phaseWidth);
        _outlinerWidth = DefaultOutlinerWidth;
        if (OutlinerHost.IsVisible) OutlinerCol.Width = new GridLength(_outlinerWidth);

        foreach (var mi in _partItems) mi.IsChecked = true;   // each applies its part
        _logState.Wanted = true;
        _phaseDock.Wanted = true;
        _outlinerState.Wanted = true;
        ApplyBars();
        SetRows();
        ApplyLog();
        UpdatePhaseUi();
        UpdateOutlinerUi();
        UpdateAlertBarVisible();
        UpdatePaletteUi();
    }
}
