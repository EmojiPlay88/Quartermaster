using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Phases;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop;

/// <summary>The turn sequencer's UI wiring (WinForms MainForm.Phases): the tracker strip under the toolbar, the docked phase panel and View > Phase panel.</summary>
public partial class MainWindow
{
    readonly MenuItem _viewPhases = new() { Header = MenuText.FromWinForms("&Phase panel"), ToggleType = MenuItemToggleType.CheckBox };
    readonly PanelDockState _phaseDock = new("phases", () => PanelDockState.PhasesDefault(new RectangleI(0, 0, 1024, 768)));
    PhasePanel? _phasePanel;
    PhaseWatch? _stripWatch;
    PhaseRunDispatch? _dispatch;
    Autosaver? _autosaver;
    string _dataRoot = "";   // app data root (layout memory, autosaves)
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

    void InitPhaseUi(string? layoutRoot)
    {
        _dataRoot = layoutRoot ?? AppDataRoot.Path;
        _viewPhases.Click += (_, _) => Guard(_phaseDock.Toggle);
        _phaseDock.Changed += UpdatePhaseUi;
        StripHost.StartRequested += () => Guard(NextPhaseFromUi);
    }

    /// <summary>Builds the strip watcher and the phase panel for a freshly opened session.</summary>
    void CreatePhaseUi(ModuleSession s, PhaseController c)
    {
        c.PhaseBoundary += OnPhaseBoundaryAnyThread;
        c.RunsQueued += OnPhaseRunsQueued;
        c.DifficultyChanged += OnDifficultyChangedAnyThread;
        CreateDispatch(s, c);
        _stripWatch = new PhaseWatch(s, RefreshStrip);
        StripHost.FlushHook = _stripWatch.Flush;
        _phasePanel = new PhasePanel(s, c, () => _actions!, CentreOnPiece);
        _phasePanel.NextRequested += () => Guard(NextPhaseFromUi);
        _phasePanel.DecisionActivated += d => Guard(() => _dispatch?.JumpToDecision(d));
        PhaseHost.Child = _phasePanel;
        UpdatePhaseUi();
    }

    void DestroyPhaseUi()
    {
        if (_phases is { } c)
        {
            c.PhaseBoundary -= OnPhaseBoundaryAnyThread;
            c.RunsQueued -= OnPhaseRunsQueued;
            c.DifficultyChanged -= OnDifficultyChangedAnyThread;
        }
        _dispatch = null;
        _autosaver = null;
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

    /// <summary>Triggers queued runs: each opens its calculation UI. Always posted, never inside Advance / ExecuteMove (as WinForms BeginInvoke).</summary>
    void OnPhaseRunsQueued(IReadOnlyList<PendingRun> runs)
    {
        RequestPhaseRefresh();
        var d = _dispatch;
        Dispatcher.UIThread.Post(() => { if (d != null && ReferenceEquals(d, _dispatch)) Guard(() => d.OnRunsQueued(runs)); });
    }

    /// <summary>The phase just changed: write an autosave now (AutosaveMinutes = 0 is off).</summary>
    void OnPhaseBoundaryAnyThread()
    {
        RequestPhaseRefresh();
        OnUiThread(d => d.OnPhaseBoundary());
    }

    /// <summary>The difficulty changed: the formula windows of the old mode close; pending runs stay pending.</summary>
    void OnDifficultyChangedAnyThread()
    {
        RequestPhaseRefresh();
        OnUiThread(d => d.OnDifficultyChanged());
    }

    /// <summary>Runs <paramref name="act"/> on the dispatch now when on the UI thread, else posts it there (if the session is still open).</summary>
    void OnUiThread(Action<PhaseRunDispatch> act)
    {
        var d = _dispatch;
        if (d == null) return;
        if (Dispatcher.UIThread.CheckAccess()) { Guard(() => act(d)); return; }
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(d, _dispatch)) Guard(() => act(d)); });
    }

    /// <summary>The shared Next Phase / run dispatch (WinForms MainForm.CreatePhases) with a phase-boundary autosaver under the app data root.</summary>
    void CreateDispatch(ModuleSession s, PhaseController c)
    {
        // A guest's game and a viewer copy are never written (CanSave): no module hash means no autosave.
        _autosaver = new Autosaver(() => _session is { CanSave: true } cur ? cur.Archive.Sha256 : null, p => s.SaveNative(p), _dataRoot,
            OnAutosaveError, () => _session?.Game?.State.Turn.Turn);
        _dispatch = new PhaseRunDispatch(s, c, _history!, () => _actions!, new DesktopPhaseUi(this, s, () => _dispatch), _autosaver, () => DateTimeOffset.UtcNow)
        {
            AutosaveMinutes = () => AutosaveMinutes,
            Failed = OnDispatchFailed,
            Log = m => System.Diagnostics.Trace.WriteLine(m),
        };
        _dispatch.Advanced += FlushPhaseViews;
    }

    /// <summary>No Settings dialog in the Avalonia app yet: the WinForms default interval (<see cref="Settings.AutosaveMinutes"/>).</summary>
    static int AutosaveMinutes => new Settings().AutosaveMinutes;

    void OnAutosaveError(string message)
    {
        System.Diagnostics.Trace.WriteLine(message);
        _session?.Log.AddOnce("autosave:failed", DiagnosticSeverity.Warning, "autosave", message);
    }

    /// <summary>Opening a queued run threw: logged and shown as a warning; the other runs still open.</summary>
    void OnDispatchFailed(Exception ex)
    {
        System.Diagnostics.Trace.WriteLine("phase run: " + ex);
        _session?.Log.Add(DiagnosticSeverity.Warning, "phases", "Opening a calculation failed: " + ex.Message);
        ShowError(ex.Message);
    }

    void FlushPhaseViews()
    {
        _stripWatch?.Flush();
        _phasePanel?.Flush();
    }

    /// <summary>Shows the phase panel (docked or floating) so a checklist item can be selected.</summary>
    internal void RevealPhasePanel() => _phaseDock.Reveal();

    internal IDialogs Dialogs => _dialogs;

    /// <summary><see cref="Guard"/> for the phase UI adapter.</summary>
    internal void GuardUi(Action a) => Guard(a);

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

    /// <summary>Start game, or advance one phase. Open decisions only warn: the dialog lists them and the user may continue anyway.</summary>
    void NextPhaseFromUi()
    {
        if (_session?.Game == null || _phases == null || _dispatch is not { } d) return;
        RunAsync(d.NextPhase);
    }
}
