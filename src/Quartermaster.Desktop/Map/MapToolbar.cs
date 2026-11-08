using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Quartermaster.Desktop.Shell;

namespace Quartermaster.Desktop.Map;

/// <summary>
/// The map toolbar of the WinForms MapWindow (BuildToolbar): Line of sight, Last moved, Hide counters (toggle buttons that follow the
/// view, so the L and H shortcuts and Esc keep them in step), Centre on selected, and the Zoom presets drop-down. It acts on the active
/// tab's view and is hidden while no map is shown. Buttons never take the keyboard focus, so Esc and the letter shortcuts stay with the map.
/// </summary>
public sealed class MapToolbar : Border
{
    /// <summary>The WinForms tooltips.</summary>
    public const string LosTip = "Drag to measure range between two locations (Esc or right-click cancels)";
    public const string LastMovedTip = "Outline the counters of the most recent move on this map";
    public const string HideTip = "Show only the board (H)";
    public const string CentreTip = "Scroll to the selected counters";
    public const string ZoomTip = "Zoom presets";

    readonly ToggleButton _los = Toggle("Line of sight", LosTip);
    readonly ToggleButton _last = Toggle("Last moved", LastMovedTip);
    readonly ToggleButton _hide = Toggle("Hide counters", HideTip);
    readonly Button _centre = Plain("Centre on selected", CentreTip);
    readonly Button _zoom;
    readonly List<MenuItem> _zoomItems = new();
    MapTabs? _tabs;
    MapView? _view;
    bool _syncing;

    public MapToolbar()
    {
        _zoom = Plain("Zoom ▾", ZoomTip);
        var menu = new MenuFlyout();
        foreach (var pct in new[] { 25, 50, 100, 200 })
        {
            var f = pct / 100f;
            Add(menu, pct + "%", () => _view?.ZoomCentered(f));
        }
        Add(menu, "Fit", () => _view?.ZoomToFit());
        _zoom.Flyout = menu;
        _zoom.Content = "Zoom ▾";

        _los.IsCheckedChanged += (_, _) => { if (!_syncing && _view != null) _view.LosEnabled = _los.IsChecked == true; };
        _last.IsCheckedChanged += (_, _) => { if (!_syncing && _view != null) _view.HighlightLastMoved = _last.IsChecked == true; };
        _hide.IsCheckedChanged += (_, _) => { if (!_syncing && _view != null) _view.HideCounters = _hide.IsChecked == true; };
        _centre.Click += (_, _) => _view?.CentreOnSelection();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var c in new Control[] { _los, _last, _hide, _centre, _zoom }) bar.Children.Add(c);
        Child = bar;
        Padding = new Thickness(2);
        BorderThickness = new Thickness(0, 0, 0, 1);
        IsVisible = false;
        Classes.Add("toolbar");
        this.Bind(BackgroundProperty, this.GetResourceObservable("Classic.FaceBrush"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Classic.BorderBrush"));
    }

    /// <summary>The toolbar's buttons by their WinForms texts (tests).</summary>
    internal ToggleButton LosButton => _los;
    internal ToggleButton LastMovedButton => _last;
    internal ToggleButton HideButton => _hide;
    internal Button CentreButton => _centre;
    internal Button ZoomButton => _zoom;
    internal IReadOnlyList<MenuItem> ZoomItems => _zoomItems;

    /// <summary>Follows the tabs' active view.</summary>
    public void Bind(MapTabs tabs)
    {
        _tabs = tabs;
        tabs.ActiveViewChanged += Follow;
        Follow();
    }

    void Follow()
    {
        var next = _tabs?.ActiveView;
        if (!ReferenceEquals(next, _view))
        {
            if (_view != null) { _view.LosChanged -= Sync; _view.HideCountersChanged -= SyncHide; }
            _view = next;
            if (_view != null) { _view.LosChanged += Sync; _view.HideCountersChanged += SyncHide; }
        }
        IsVisible = _view != null;
        Sync();
    }

    void SyncHide(bool _) => Sync();

    void Sync()
    {
        _syncing = true;
        try
        {
            _los.IsChecked = _view?.LosEnabled == true;
            _last.IsChecked = _view?.HighlightLastMoved == true;
            _hide.IsChecked = _view?.HideCounters == true;
        }
        finally { _syncing = false; }
    }

    void Add(MenuFlyout menu, string label, Action act)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => act();
        menu.Items.Add(item);
        _zoomItems.Add(item);
    }

    static ToggleButton Toggle(string text, string tip)
    {
        var b = new ToggleButton { Content = text, Focusable = false };
        ToolTip.SetTip(b, tip);
        return b;
    }

    static Button Plain(string text, string tip)
    {
        var b = new Button { Content = text, Focusable = false };
        ToolTip.SetTip(b, tip);
        return b;
    }
}
