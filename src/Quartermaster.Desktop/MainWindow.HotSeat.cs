using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop;

/// <summary>
/// Hot-seat (port of WinForms MainForm.HotSeat): the handover screen (every Quartermaster window hidden while it is up, restored after
/// Continue) and the player list edits from Settings > Game > Players. Hot-seat undo stays global (spec 8a.3).
/// </summary>
public partial class MainWindow
{
    /// <summary>Placeholder until stage 5c brings the online session (WinForms MainForm.IsOnline); the Desktop app is never online yet.</summary>
    bool IsOnline => false;

    HotSeat? _hotSeat;
    Action<PlayerDef>? _onHandoverRequired;
    Action? _onHandoverCancelled, _onCurrentChanged;
    HandoverWindow? _handover;
    PlayerDef? _handoverFor;
    readonly List<(Window Window, Window Owner)> _hiddenForHandover = new();
    readonly List<PendingRun> _runsAfterHandover = new();
    bool _mainHiddenForHandover;
    DispatcherTimer? _handoverRetry;

    /// <summary>The hot-seat controller of the open module (null without one).</summary>
    public HotSeat? HotSeat => _hotSeat;

    /// <summary>True while the handover screen is up (or waiting for a modal dialog to close).</summary>
    public bool HandoverShowing => _handoverFor != null;

    /// <summary>The handover screen while it is up (tests).</summary>
    internal HandoverWindow? Handover => _handover;

    void CreateHotSeat(ModuleSession s, PhaseController phases, HistoryController history)
    {
        var hs = new HotSeat(s, phases);
        hs.Watch(history);
        // Events may arrive off the UI thread: each runs there, and only while this controller is still the window's.
        _onHandoverRequired = p => OnHotSeatUi(hs, () => OnHandoverRequired(p));
        _onHandoverCancelled = () => OnHotSeatUi(hs, OnHandoverCancelled);
        _onCurrentChanged = () => OnHotSeatUi(hs, OnCurrentPlayerChanged);
        hs.HandoverRequired += _onHandoverRequired;
        hs.HandoverCancelled += _onHandoverCancelled;
        hs.CurrentChanged += _onCurrentChanged;
        _hotSeat = hs;
    }

    /// <summary>The module is closing (or the window): the handover screen goes without bringing back the windows of the closed game.</summary>
    void DestroyHotSeat()
    {
        if (_hotSeat is { } hs)
        {
            hs.HandoverRequired -= _onHandoverRequired;
            hs.HandoverCancelled -= _onHandoverCancelled;
            hs.CurrentChanged -= _onCurrentChanged;
            hs.Detach();
        }
        _hotSeat = null;
        _onHandoverRequired = null;
        _onHandoverCancelled = _onCurrentChanged = null;
        DismissHandover(restoreWindows: false);
        _runsAfterHandover.Clear();
    }

    void OnHotSeatUi(HotSeat hs, Action a)
    {
        void Run()
        {
            if (!ReferenceEquals(_hotSeat, hs)) return;
            // A guest's view is the host's snapshot (WinForms StopHotSeatForOnline; stage 5c brings the online session): hot-seat stops for good.
            if (_session?.IsGuest == true) { DestroyHotSeat(); return; }
            Guard(a);
        }
        if (Dispatcher.UIThread.CheckAccess()) Run();
        else Dispatcher.UIThread.Post(Run);
    }

    // ---------- handover ----------

    void OnHandoverRequired(PlayerDef p)
    {
        // The leaving player's formula windows, batch grids and cheat sheets close at once; the screen itself comes up once the command
        // that raised this has finished (a load, a rewind or Next Phase may still be opening windows).
        CloseFormulaWindows($"Pass to {p.Name}");
        RequestHandover(p);
    }

    void OnHandoverCancelled()
    {
        if (_handoverFor == null) return;
        DismissHandover();
        SetStatus("Handover no longer needed");
        OpenRunsAfterHandover();
    }

    void OnCurrentPlayerChanged()
    {
        // The game became solo while a deliberate swap was on screen: nothing to hand over any more.
        if (_hotSeat is { Active: false } && _handoverFor != null) { DismissHandover(); OpenRunsAfterHandover(); }
    }

    /// <summary>WinForms MainForm.CloseFormulaWindows: every formula window, batch grid and cheat sheet of the session closes; the status says why.</summary>
    void CloseFormulaWindows(string status)
    {
        if (_session == null) return;
        FormulaWindows.CloseAllFor(_session);
        SetStatus(status);
    }

    /// <summary>Shows (or retargets) the handover screen for this player; deferred while a modal dialog is open.</summary>
    void RequestHandover(PlayerDef p)
    {
        _handoverFor = p;
        if (_handover is { } h)
        {
            h.SetPlayer(p, HandoverText.SidesText(p, _session?.Rules));
            return;
        }
        var hs = _hotSeat;
        Dispatcher.UIThread.Post(() => { if (!_closed && ReferenceEquals(_hotSeat, hs)) Guard(ShowHandoverWhenFree); });
    }

    void ShowHandoverWhenFree()
    {
        if (_closed || _handoverFor is not { } p || _handover != null) return;
        if (ModalTracker.Open)
        {
            // A modal dialog (Settings, say) is open: the view is already public-only; the screen comes up once it closes.
            if (_handoverRetry == null)
            {
                _handoverRetry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _handoverRetry.Tick += (_, _) =>
                {
                    _handoverRetry?.Stop();
                    Guard(ShowHandoverWhenFree);
                };
            }
            _handoverRetry.Start();
            return;
        }
        ShowHandover(p);
    }

    /// <summary>Every visible window <paramref name="owner"/> owns (and the windows they own), parents first.</summary>
    static void CollectOwned(Window owner, List<(Window Window, Window Owner)> into)
    {
        foreach (var w in owner.OwnedWindows.ToList())
        {
            if (w is HandoverWindow) continue;
            if (w.IsVisible) into.Add((w, owner));
            CollectOwned(w, into);
        }
    }

    /// <summary>Hides every Quartermaster window (not just covers it) and shows the handover screen in the main window's place.</summary>
    void ShowHandover(PlayerDef p)
    {
        _tour?.Close();
        foreach (var v in Maps.Views) v.HidePopup();
        if (_session != null) FormulaWindows.CloseAllFor(_session);
        _hiddenForHandover.Clear();
        CollectOwned(this, _hiddenForHandover);
        // Children first. Avalonia drops a hidden window's owner, so each comes back with the owner remembered here.
        for (var i = _hiddenForHandover.Count - 1; i >= 0; i--)
        {
            var w = _hiddenForHandover[i].Window;
            w.Closed += OnHiddenWindowClosed;
            try { w.Hide(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("handover hide: " + ex.Message); }
        }
        var h = new HandoverWindow(p, HandoverText.SidesText(p, _session?.Rules))
        {
            Icon = Icon,
            Position = Position,
            Width = Bounds.Width > 0 ? Bounds.Width : Width,
            Height = Bounds.Height > 0 ? Bounds.Height : Height,
        };
        if (WindowState == WindowState.Maximized) h.WindowState = WindowState.Maximized;
        h.Continued += () => Guard(ContinueHandover);
        h.CloseRequested += () => Dispatcher.UIThread.Post(() => { if (!_closed) Close(); });
        _handover = h;
        h.Show();
        // The main window goes too.
        _mainHiddenForHandover = IsVisible;
        if (IsVisible) Hide();
        h.Activate();
        System.Diagnostics.Trace.WriteLine($"handover: {h.Message}");
    }

    void OnHiddenWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window w) return;
        w.Closed -= OnHiddenWindowClosed;
        _hiddenForHandover.RemoveAll(x => ReferenceEquals(x.Window, w));
    }

    void ContinueHandover()
    {
        if (_handoverFor is not { } p) return;
        // The view switches while everything is still hidden, so windows reappear already showing this player's view.
        _hotSeat?.TakeOver(p);
        DismissHandover();
        SetStatus(HandoverText.StatusText(p));
        OpenRunsAfterHandover();
    }

    /// <summary>Closes the handover screen and brings back what it hid (not the windows of a closed game, and nothing while the main window
    /// closes). No-op when it is not up.</summary>
    void DismissHandover(bool restoreWindows = true)
    {
        _handoverFor = null;
        _handoverRetry?.Stop();
        if (_handover is { } h)
        {
            _handover = null;
            try { h.CloseForReal(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("handover close: " + ex.Message); }
        }
        var hidden = _hiddenForHandover.ToList();
        _hiddenForHandover.Clear();
        foreach (var (w, _) in hidden) w.Closed -= OnHiddenWindowClosed;
        if (_mainHiddenForHandover)
        {
            _mainHiddenForHandover = false;
            if (!_closed)
            {
                Show();
                Activate();
            }
        }
        if (!restoreWindows || _closed) return;
        foreach (var (w, owner) in hidden)
        {
            try
            {
                if (owner.IsVisible) w.Show(owner);
                else w.Show();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("handover restore: " + ex.Message); }
        }
    }

    /// <summary>Calculations queued by the phase the new player starts in open once they are at the screen.</summary>
    void OpenRunsAfterHandover()
    {
        if (_runsAfterHandover.Count == 0) return;
        var runs = _runsAfterHandover.ToList();
        _runsAfterHandover.Clear();
        if (_session?.Game is not { } g || _dispatch is not { } d) return;
        foreach (var r in runs)
            if (g.State.Pending.Any(x => x.Id == r.Id)) RunAsync(() => d.OpenPendingRun(r.Id));
    }

    /// <summary>WinForms PhaseRunDispatch.HoldRuns: the phase belongs to the next player; their calculations open once they are at the screen.</summary>
    bool HoldRunsForHandover(IReadOnlyList<PendingRun> runs)
    {
        if (_handoverFor == null && _hotSeat?.Pending == null) return false;
        _runsAfterHandover.AddRange(runs);
        return true;
    }

    // ---------- player list (Settings > Game > Players) ----------

    (Quartermaster.Rules.RulesPack? Pack, IReadOnlyList<PlayerDef> Players)? GamePlayers() =>
        _session?.Game is { } g ? (_session.Rules, g.State.Players.ToList()) : null;

    /// <summary>Executes SetPlayers when the list changed and is valid (the editor validated it already; this re-checks).</summary>
    void SetGamePlayers(IReadOnlyList<PlayerDef> players)
    {
        if (_session?.Game is not { } g) return;
        if (PlayersEditModel.Same(g.State.Players, players)) return;
        if (_session.IsReplaying) { ShowError(ModuleSession.ReplayingText); return; }
        if (IsOnline || _session.IsGuest)
        {
            // Spec 8a.10: during a live session only the host's Players dialog changes who holds what.
            ShowError(_session.IsGuest ? IntentExecutor.HostOnlyText : "While the game is online, change seats with Game → Players….");
            return;
        }
        if (players.Count >= 2)
        {
            var problem = _session.Rules is { } pack ? Players.Validate(players, pack) : "Open a rules pack to set up players.";
            if (problem != null) { ShowError(problem); return; }
        }
        g.Execute(CommandLog.Prepare(new SetPlayers(players.ToList()), g.State, _session));
    }
}
