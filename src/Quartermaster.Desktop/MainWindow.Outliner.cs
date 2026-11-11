using Avalonia.Controls;
using Quartermaster.Core;
using Quartermaster.Desktop.Outliner;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop;

/// <summary>
/// The outliner dock's wiring (WinForms MainForm.Outliner / OutlinerDock): View > Outliner, the column at the right of the maps after the phase
/// panel, and the floating "Outliner" window (bounds remembered per module under "outliner"). Hidden until asked for, as WinForms
/// (OutlinerVisible defaults to false); visibility and grouping are kept in the settings (OutlinerVisible / OutlinerGrouping).
/// </summary>
public partial class MainWindow
{
    readonly MenuItem _viewOutliner = new() { Header = MenuText.FromWinForms("&Outliner"), ToggleType = MenuItemToggleType.CheckBox };
    PanelDockState _outlinerState = null!;
    PanelFloat _outlinerFloat = null!;
    OutlinerPanel? _outlinerPanel;
    bool _outlinerFloated;       // the outliner window was shown for this module: its DPI is the current one
    double _outlinerWidth = 440; // WinForms default of the docked outliner

    ColumnDefinition OutlinerCol => Body.ColumnDefinitions[Grid.GetColumn(OutlinerHost)];
    ColumnDefinition OutlinerSplitCol => Body.ColumnDefinitions[Grid.GetColumn(OutlinerSplitter)];

    /// <summary>The outliner of the open module (null without a game).</summary>
    public OutlinerPanel? Outliner => _outlinerPanel;

    /// <summary>The floating outliner window (null while docked).</summary>
    public FloatPanelWindow? OutlinerFloat => _outlinerFloat.Window;

    /// <summary>True while the outliner is docked and showing.</summary>
    public bool OutlinerShown => OutlinerHost.IsVisible;

    internal Control? OutlinerHostChild => OutlinerHost.Child;

    void InitOutliner()
    {
        _outlinerState = new PanelDockState("outliner", () => PanelDockState.OutlinerDefault(MainRect())) { Wanted = false };
        _outlinerFloat = new PanelFloat(_outlinerState, "Outliner", OutlinerHost, () => this, () => ScreensProvider?.Invoke() ?? PanelFloat.ScreensOf(this));
        _viewOutliner.Click += (_, _) => Guard(_outlinerState.Toggle);
        Gate("view.outliner", _viewOutliner);
        _outlinerState.Changed += () =>
        {
            if (_outlinerState.Floating) _outlinerFloated = true;
            // WinForms SaveOutlinerVisible: the wish to see it is remembered between runs.
            if (_outlinerPanel != null && SettingsStore.Current.OutlinerVisible != _outlinerState.Wanted)
                SettingsStore.Update(x => x with { OutlinerVisible = _outlinerState.Wanted });
            _outlinerPanel?.SetFloating(_outlinerState.Floating);
            UpdateOutlinerUi();
        };
    }

    void CreateOutliner(ModuleSession s)
    {
        var panel = new OutlinerPanel(s, () => _phases, _alertHits, CentreOnPiece, OpenOutlinerMap, d => Guard(() => _dispatch?.JumpToDecision(d)));
        _outlinerPanel = panel;
        // The remembered grouping goes in before the change handler is hooked, so loading it writes nothing.
        panel.Grouping = SettingsStore.Current.OutlinerGrouping;
        panel.GroupingBox.SelectionChanged += (_, _) =>
        {
            var g = panel.Grouping;
            if (SettingsStore.Current.OutlinerGrouping != g) SettingsStore.Update(x => x with { OutlinerGrouping = g });
        };
        _outlinerState.Wanted = SettingsStore.Current.OutlinerVisible;
        AlertsUpdated += panel.AlertsChanged;   // one alert evaluation per change, shared with the alert bar
        _outlinerFloated = false;
        _outlinerState.FloatBounds = null;
        _outlinerState.Load(_layout.LoadModule(s.Archive.Sha256).TryGetValue("outliner", out var saved) ? saved : null);
        panel.PopToggleRequested += () => Guard(_outlinerFloat.Toggle);
        panel.SetFloating(_outlinerState.Floating);
        _outlinerFloat.Attach(panel);
        UpdateOutlinerUi();
    }

    void OpenOutlinerMap(MapDef map)
    {
        var view = Maps.Views.FirstOrDefault(v => v.Map.Id == map.Id);
        if (view != null) Maps.Activate(view);
    }

    /// <summary>Docks the panel back (the game is closing), remembers where its window was per module and lets the panel go.</summary>
    void DestroyOutliner()
    {
        if (_outlinerPanel is not { } panel) return;
        AlertsUpdated -= panel.AlertsChanged;
        _outlinerFloat.Detach();
        if (_session is { } s && _outlinerState.Save() is { } saved)
        {
            if (_outlinerFloated) saved = saved with { Dpi = (int)Math.Round(RenderScaling * 96) };
            var all = new Dictionary<string, SavedWindow>(_layout.LoadModule(s.Archive.Sha256)) { ["outliner"] = saved };
            _layout.SaveModule(s.Archive.Sha256, all);
        }
        panel.Detach();
        _outlinerPanel = null;
        OutlinerHost.Child = null;
        UpdateOutlinerUi();
    }

    /// <summary>Docked column and the View > Outliner check, from the dock state (WinForms OutlinerDock.Apply).</summary>
    void UpdateOutlinerUi()
    {
        var has = _outlinerPanel != null && _session?.Game != null;
        var shown = has && _outlinerState.Wanted && !_outlinerState.Floating;
        _viewOutliner.IsChecked = has && (_outlinerState.Floating || _outlinerState.Wanted);
        if (OutlinerHost.IsVisible && OutlinerCol.Width.Value >= 1) _outlinerWidth = OutlinerCol.Width.Value;   // keep what the user dragged it to (shown or hiding)
        OutlinerHost.IsVisible = OutlinerSplitter.IsVisible = shown;
        OutlinerSplitCol.Width = new GridLength(shown ? 4 : 0);
        OutlinerCol.Width = new GridLength(shown ? _outlinerWidth : 0);
        _outlinerPanel?.ShownChanged();
    }
}
