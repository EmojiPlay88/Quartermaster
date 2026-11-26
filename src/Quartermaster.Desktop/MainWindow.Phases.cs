using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Phases;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop;

/// <summary>The turn sequencer's UI wiring (WinForms MainForm.Phases / PhaseDock): the tracker strip under the toolbar, the phase panel (docked at
/// the right or floating in a "Phases" window, bounds remembered per module under "phases") and View > Phase panel.</summary>
public partial class MainWindow
{
    readonly MenuItem _viewPhases = new() { Header = MenuText.FromWinForms("&Phase panel"), ToggleType = MenuItemToggleType.CheckBox };
    PanelDockState _phaseDock = null!;
    PanelFloat _phaseFloat = null!;
    bool _phaseFloated;   // the phase window was shown for this module: its DPI is the current one
    PhasePanel? _phasePanel;
    PhaseWatch? _stripWatch;
    PhaseRunDispatch? _dispatch;
    Autosaver? _autosaver;
    string _dataRoot = "";   // app data root (layout memory, autosaves)
    const double DefaultPhaseWidth = 320;
    double _phaseWidth = DefaultPhaseWidth;   // WinForms default; follows the splitter

    // Looked up by the controls' columns, so other docks may add columns around these.
    ColumnDefinition PhaseCol => Body.ColumnDefinitions[Grid.GetColumn(PhaseHost)];
    ColumnDefinition PhaseSplitCol => Body.ColumnDefinitions[Grid.GetColumn(PhaseSplitter)];

    /// <summary>The tracker strip under the toolbar (shown while a game and a rules pack exist).</summary>
    public PhaseStrip Strip => StripHost;

    /// <summary>The phase panel of the open module.</summary>
    public PhasePanel Phases => _phasePanel ?? throw new InvalidOperationException("Open a module first.");

    /// <summary>The floating phase panel window (null while docked).</summary>
    public FloatPanelWindow? PhaseFloat => _phaseFloat.Window;

    /// <summary>True while the panel is docked and showing (View > Phase panel checked).</summary>
    public bool PhasePanelShown => PhaseHost.IsVisible;

    void InitPhaseUi(string? layoutRoot)
    {
        _dataRoot = layoutRoot ?? AppDataRoot.Path;
        // The floating window opens at the WinForms default beside the main window's right edge (PanelDockState.PhasesDefault).
        _phaseDock = new PanelDockState("phases", () => PanelDockState.PhasesDefault(MainRect()));
        _phaseFloat = new PanelFloat(_phaseDock, "Phases", PhaseHost, () => this, () => ScreensProvider?.Invoke() ?? PanelFloat.ScreensOf(this));
        _viewPhases.Click += (_, _) => Guard(_phaseDock.Toggle);
        _phaseDock.Wanted = PartShown("phases");   // View > Phase panel is remembered app-wide (ui.json)
        _phaseDock.Changed += () =>
        {
            SetPartHidden("phases", !_phaseDock.Wanted);
            if (_phaseDock.Floating) _phaseFloated = true;
            _phasePanel?.SetFloating(_phaseDock.Floating);
            UpdatePhaseUi();
        };
        StripHost.StartRequested += () => Guard(NextPhaseFromUi);
    }

    /// <summary>Builds the strip watcher and the phase panel for a freshly opened session.</summary>
    void CreatePhaseUi(ModuleSession s, PhaseController c)
    {
        c.PhaseBoundary += OnPhaseBoundaryAnyThread;
        c.RunsQueued += OnPhaseRunsQueued;
        c.DifficultyChanged += OnDifficultyChangedAnyThread;
        CreateDispatch(s, c);
        CreateHotSeat(s, c, _history!);
        _stripWatch = new PhaseWatch(s, RefreshStrip);
        StripHost.FlushHook = _stripWatch.Flush;
        _phasePanel = new PhasePanel(s, c, () => _actions!, CentreOnPiece, OpenDocumentAtPage);
        _phasePanel.NextRequested += () => Guard(NextPhaseFromUi);
        _phasePanel.DecisionActivated += d => Guard(() => _dispatch?.JumpToDecision(d));
        _phasePanel.DueActionRequested += e => Guard(() => ActOnDue(e));
        PlacePhases(s, _phasePanel);
        UpdatePhaseUi();
    }

    /// <summary>The module's layout memory for the phase window, then the panel in its place (dock host or floating window).</summary>
    void PlacePhases(ModuleSession s, PhasePanel panel)
    {
        _phaseFloated = false;
        _phaseDock.FloatBounds = null;
        _phaseDock.Load(_layout.LoadModule(s.Archive.Sha256).TryGetValue("phases", out var saved) ? saved : null);
        panel.PopToggleRequested += () => Guard(_phaseFloat.Toggle);
        panel.SetFloating(_phaseDock.Floating);
        _phaseFloat.Attach(panel);
    }

    /// <summary>Docks the panel back (the game is closing) and remembers where its window was, per module (WinForms: the panel docks
    /// again when the game closes).</summary>
    void ReleasePhases()
    {
        if (_phasePanel == null) return;
        _phaseFloat.Detach();
        if (_session is { } s && _phaseDock.Save() is { } saved)
        {
            if (_phaseFloated) saved = saved with { Dpi = (int)Math.Round(RenderScaling * 96) };
            var all = new Dictionary<string, SavedWindow>(_layout.LoadModule(s.Archive.Sha256)) { ["phases"] = saved };
            _layout.SaveModule(s.Archive.Sha256, all);
        }
    }

    void DestroyPhaseUi()
    {
        DestroyHotSeat();
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
        ReleasePhases();
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
            AutosaveMinutes = () => SettingsStore.Current.AutosaveMinutes,
            HoldRuns = HoldRunsForHandover,
            Failed = OnDispatchFailed,
            Log = m => System.Diagnostics.Trace.WriteLine(m),
        };
        _dispatch.Advanced += FlushPhaseViews;
    }

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
    internal void RevealPhasePanel()
    {
        _phaseDock.Reveal();
        _phaseFloat.Window?.Activate();
    }

    internal IDialogs Dialogs => _dialogs;

    /// <summary><see cref="Guard"/> for the phase UI adapter.</summary>
    internal void GuardUi(Action a) => Guard(a);

    // ---------- schedule rows (B5 #62) ----------

    /// <summary>A guest's Place waiting for the host's answer: the row goes from the due list when the host did it.</summary>
    (string RowId, IReadOnlyList<string> PieceIds, string? MapId)? _placeWait;

    /// <summary>The due row's zone map when the module has it; a raw id of a map the module lacks is no zone map (never shown or selected).</summary>
    MapDef? DueMap(DueEntry e) => _session == null ? null : ScheduleDue.ZoneMap(_session.Model, e.ZoneMapId);

    /// <summary>Place or Withdraw on a due row through the port. A local Place selects the counters and shows their map (the player drags
    /// them to their hexes); a guest's Place does so once the host has answered.</summary>
    void ActOnDue(DueEntry e)
    {
        if (_actions is not { } port || _session == null) return;
        var place = e.ButtonText == DueModel.PlaceText;
        var map = DueMap(e);
        if (_session.IsGuest)
        {
            _placeWait = place ? (e.RowId, e.PieceIds, map?.Id) : null;
            port.ActOnSchedule(e.RowId);
            return;
        }
        if (port.ActOnSchedule(e.RowId) is { } refused) { SetStatus(refused); return; }
        if (place) ShowPlaced(e.PieceIds, map);
    }

    void ShowPlaced(IReadOnlyList<string> pieceIds, MapDef? map)
    {
        if (map == null || Maps.Open(map.Id) is not { } view) return;
        view.SelectPieces(pieceIds);
        SetStatus($"Placed: drag the counters from {map.Name} to their hexes");
    }

    /// <summary>A guest's Place was done by the host (its row left the due list): the counters are selected and their map shown.</summary>
    void CompleteGuestPlace()
    {
        if (_placeWait is not { } w || _session == null) return;
        if (DueModel.Rows(_session).Any(r => r.RowId == w.RowId)) return;
        _placeWait = null;
        ShowPlaced(w.PieceIds, w.MapId == null ? null : _session.Model.Maps.FirstOrDefault(m => m.Id == w.MapId));
    }

    void RefreshStrip()
    {
        CompleteGuestPlace();
        StripHost.StartEnabled = _session?.IsReplaying != true;
        if (_phases != null && _session?.Game != null) StripHost.SetView(_phases.View());
    }

    /// <summary>Shows the strip while a game and a rules pack exist and the docked panel when the pack has phases and the user wants it.</summary>
    void UpdatePhaseUi()
    {
        StripHost.IsVisible = _session?.Game != null && _session.Rules != null && PartShown("phaseStrip");
        var has = _phasePanel != null && _session?.Game != null && _session.Rules is { Phases.Count: > 0 };
        if (!has && _phaseDock.Floating) { _phaseDock.DockBack(); return; }   // no phases any more: no floating window either (Changed re-runs this)
        var shown = has && _phaseDock.Wanted && !_phaseDock.Floating;
        _viewPhases.IsEnabled = has;
        _viewPhases.IsChecked = has && (_phaseDock.Floating || _phaseDock.Wanted);
        if (PhaseHost.IsVisible && PhaseCol.Width.Value >= 1) _phaseWidth = PhaseCol.Width.Value;   // keep what the user dragged it to (shown or hiding)
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
