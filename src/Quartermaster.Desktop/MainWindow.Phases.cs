using Avalonia.Controls;
using Quartermaster.Core;
using Quartermaster.Desktop.Phases;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop;

/// <summary>The turn sequencer's UI wiring (WinForms MainForm.Phases): the tracker strip under the toolbar, the docked phase panel and View > Phase panel.</summary>
public partial class MainWindow
{
    readonly MenuItem _viewPhases = new() { Header = MenuText.FromWinForms("&Phase panel"), ToggleType = MenuItemToggleType.CheckBox };
    readonly PanelDockState _phaseDock = new("phases", () => PanelDockState.PhasesDefault(new RectangleI(0, 0, 1024, 768)));
    PhasePanel? _phasePanel;
    PhaseWatch? _stripWatch;
    double _phaseWidth = 320;   // WinForms default; follows the splitter

    // Looked up by the controls' columns, so other docks may add columns around these.
    ColumnDefinition PhaseCol => Body.ColumnDefinitions[Grid.GetColumn(PhaseHost)];
    ColumnDefinition PhaseSplitCol => Body.ColumnDefinitions[Grid.GetColumn(PhaseSplitter)];

    /// <summary>The tracker strip under the toolbar (shown while a game and a rules pack exist).</summary>
    public PhaseStrip Strip => StripHost;

    /// <summary>The phase panel of the open module.</summary>
    public PhasePanel Phases => _phasePanel ?? throw new InvalidOperationException("Open a module first.");

    /// <summary>True while the panel is docked and showing (View > Phase panel checked).</summary>
    public bool PhasePanelShown => PhaseHost.IsVisible;

    void InitPhaseUi()
    {
        _viewPhases.Click += (_, _) => Guard(_phaseDock.Toggle);
        _phaseDock.Changed += UpdatePhaseUi;
        StripHost.StartRequested += () => Guard(NextPhaseFromUi);
    }

    /// <summary>Builds the strip watcher and the phase panel for a freshly opened session.</summary>
    void CreatePhaseUi(ModuleSession s, PhaseController c)
    {
        c.PhaseBoundary += RequestPhaseRefresh;
        c.RunsQueued += OnPhaseRunsQueued;
        c.DifficultyChanged += RequestPhaseRefresh;
        _stripWatch = new PhaseWatch(s, RefreshStrip);
        StripHost.FlushHook = _stripWatch.Flush;
        _phasePanel = new PhasePanel(s, c, () => _actions!, CentreOnPiece);
        _phasePanel.NextRequested += () => Guard(NextPhaseFromUi);
        _phasePanel.DecisionActivated += d => Guard(() => { if (d.PieceIds.FirstOrDefault() is { } id) CentreOnPiece(id); });
        PhaseHost.Child = _phasePanel;
        UpdatePhaseUi();
    }

    void DestroyPhaseUi()
    {
        if (_phases is { } c)
        {
            c.PhaseBoundary -= RequestPhaseRefresh;
            c.RunsQueued -= OnPhaseRunsQueued;
            c.DifficultyChanged -= RequestPhaseRefresh;
        }
        _stripWatch?.Dispose();
        _stripWatch = null;
        StripHost.FlushHook = null;
        _phasePanel?.Detach();
        _phasePanel = null;
        PhaseHost.Child = null;
    }

    // Controller events may arrive off the UI thread: the watchers marshal their own requests.
    void RequestPhaseRefresh()
    {
        _stripWatch?.Request();
        _phasePanel?.RequestRefresh();
    }

    void OnPhaseRunsQueued(IReadOnlyList<PendingRun> _) => RequestPhaseRefresh();

    void RefreshStrip()
    {
        StripHost.StartEnabled = _session?.IsReplaying != true;
        if (_phases != null && _session?.Game != null) StripHost.SetView(_phases.View());
    }

    /// <summary>Shows the strip while a game and a rules pack exist and the docked panel when the pack has phases and the user wants it.</summary>
    void UpdatePhaseUi()
    {
        StripHost.IsVisible = _session?.Game != null && _session.Rules != null;
        var has = _phasePanel != null && _session?.Game != null && _session.Rules is { Phases.Count: > 0 };
        var shown = has && _phaseDock.Wanted && !_phaseDock.Floating;
        _viewPhases.IsEnabled = has;
        _viewPhases.IsChecked = has && (_phaseDock.Floating || _phaseDock.Wanted);
        if (!shown && PhaseHost.IsVisible && PhaseCol.Width.Value >= 1) _phaseWidth = PhaseCol.Width.Value;   // keep what the user dragged it to
        PhaseHost.IsVisible = PhaseSplitter.IsVisible = shown;
        PhaseSplitCol.Width = new Avalonia.Controls.GridLength(shown ? 4 : 0);
        PhaseCol.Width = new Avalonia.Controls.GridLength(shown ? _phaseWidth : 0);
        if (StripHost.IsVisible) RefreshStrip();
        if (shown) _phasePanel!.Reload();
    }

    /// <summary>Start game / Next Phase. The phase flow with its open-decisions question replaces this in the run dispatch.</summary>
    void NextPhaseFromUi()
    {
        if (_session?.Game == null || _phases == null) return;
        if (_session.IsReplaying) { SetStatus(ModuleSession.ReplayingText); return; }   // the sender's turn replays as sent
        var v = _phases.View();
        if (v.NoPhases) return;
        if (v.PlayingText is { } playing) { SetStatus(playing); return; }   // hot-seat: another side's phase
        if (v.BeforeStart) _phases.Start();
        else if (_actions!.NextPhase() is { } pass) SetStatus(pass);
        _stripWatch?.Flush();
        _phasePanel?.Flush();
    }
}
