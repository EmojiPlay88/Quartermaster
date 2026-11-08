using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Hot-seat wiring: the handover screen (every Quartermaster window hidden while it is up, restored after Continue), the status-bar
/// player switcher and the player list edits from Settings → Game → Players. Hot-seat undo stays global (spec 8a.3).
/// </summary>
sealed partial class MainForm
{
    HotSeat? _hotSeat;
    HandoverForm? _handover;
    PlayerDef? _handoverFor;
    readonly List<Form> _hiddenForHandover = new();
    readonly List<PendingRun> _runsAfterHandover = new();
    bool _mainHiddenForHandover, _shuttingDown;
    int _rightWidthBeforeHandover;
    System.Windows.Forms.Timer? _handoverRetry;
    readonly ToolStripDropDownButton _sbPlayer = new("Player:")
    {
        Visible = false, ShowDropDownArrow = true, DisplayStyle = ToolStripItemDisplayStyle.Text, Name = "status.player",
        ToolTipText = "Who is at the screen. Choose a player to hand over.",
    };

    /// <summary>The hot-seat controller of the open game (null without a module).</summary>
    public HotSeat? HotSeat => _hotSeat;

    /// <summary>True while the handover screen is up (or waiting for a modal dialog to close).</summary>
    public bool HandoverShowing => _handoverFor != null;

    void InitHotSeat()
    {
        _sbPlayer.DropDownOpening += (_, _) => Guard(BuildPlayerMenu);
        _sbPlayer.DropDownItems.Add(new ToolStripMenuItem("(no players)") { Enabled = false });   // keeps the arrow active before the first open
    }

    void CreateHotSeat(ModuleSession s, PhaseController phases, HistoryController history)
    {
        var hs = new HotSeat(s, phases);
        hs.Watch(history);
        hs.HandoverRequired += OnHandoverRequired;
        hs.HandoverCancelled += OnHandoverCancelled;
        hs.CurrentChanged += OnCurrentPlayerChanged;
        _hotSeat = hs;
        UpdatePlayerButton();
    }

    void DestroyHotSeat()
    {
        if (_hotSeat is { } hs)
        {
            hs.HandoverRequired -= OnHandoverRequired;
            hs.HandoverCancelled -= OnHandoverCancelled;
            hs.CurrentChanged -= OnCurrentPlayerChanged;
        }
        _hotSeat = null;
        DismissHandover();
        _runsAfterHandover.Clear();
        UpdatePlayerButton();
    }

    void OnUiThread(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { if (IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) Guard(a); }); }
        else Guard(a);
    }

    // ---------- handover ----------

    void OnHandoverRequired(PlayerDef p) => OnUiThread(() =>
    {
        // The leaving player's formula dialogs, batch grids and cheat sheets close at once; the screen itself comes up once the command
        // that raised this has finished (a load, a rewind or Next Phase may still be opening windows).
        CloseFormulaWindows($"Pass to {p.Name}");
        RequestHandover(p);
    });

    void OnHandoverCancelled() => OnUiThread(() =>
    {
        if (_handoverFor == null) return;
        DismissHandover();
        SetStatus("Handover no longer needed");
        OpenRunsAfterHandover();
    });

    void OnCurrentPlayerChanged() => OnUiThread(() =>
    {
        UpdatePlayerButton();
        // The game became solo while a deliberate swap was on screen: nothing to hand over any more.
        if (_hotSeat is { Active: false } && _handoverFor != null) { DismissHandover(); OpenRunsAfterHandover(); }
    });

    /// <summary>Shows (or retargets) the handover screen for this player; deferred while a modal dialog is open.</summary>
    void RequestHandover(PlayerDef p)
    {
        _handoverFor = p;
        if (_handover is { IsDisposed: false } h)
        {
            h.SetPlayer(p, HandoverForm.SidesText(p, _session?.Rules));
            return;
        }
        if (IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) Guard(ShowHandoverWhenFree); });
        else ShowHandoverWhenFree();
    }

    void ShowHandoverWhenFree()
    {
        if (_handoverFor is not { } p || _handover is { IsDisposed: false }) return;
        if (ModalOpen())
        {
            // A modal dialog (Settings, say) is open: the view is already public-only; the screen comes up once it closes.
            _handoverRetry ??= new System.Windows.Forms.Timer { Interval = 200 };
            _handoverRetry.Tick -= OnHandoverRetry;
            _handoverRetry.Tick += OnHandoverRetry;
            _handoverRetry.Start();
            return;
        }
        ShowHandover(p);
    }

    void OnHandoverRetry(object? sender, EventArgs e)
    {
        _handoverRetry?.Stop();
        Guard(ShowHandoverWhenFree);
    }

    /// <summary>A modal dialog is up: one of ours, or a native one (message box, file dialog), which disables the main window.</summary>
    bool ModalOpen() => !Enabled || Application.OpenForms.Cast<Form>().ToList().Any(f => f != this && f is not HandoverForm && f.Modal && f.Visible);

    /// <summary>Hides every Quartermaster window (not just covers it) and shows the handover screen in the main window's place.</summary>
    void ShowHandover(PlayerDef p)
    {
        _tour?.Close();
        foreach (var f in Application.OpenForms.Cast<Form>().ToList())
            if (f is CounterDetailPopup or TourOverlay && !f.IsDisposed) { try { f.Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("handover close popup: " + ex.Message); } }
        _hiddenForHandover.Clear();
        foreach (var f in Application.OpenForms.Cast<Form>().ToList())
        {
            if (f == this || f is HandoverForm || f.IsMdiChild || !f.Visible || f.IsDisposed) continue;
            _hiddenForHandover.Add(f);
            f.Hide();
        }
        var form = new HandoverForm(p, HandoverForm.SidesText(p, _session?.Rules)) { Icon = Icon };
        var normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        form.Bounds = normal.IsEmpty ? Bounds : normal;
        if (WindowState == FormWindowState.Maximized) form.WindowState = FormWindowState.Maximized;
        form.Continued += () => Guard(ContinueHandover);
        form.CloseRequested += () => BeginInvoke(() => { if (!IsDisposed) Close(); });
        _handover = form;
        form.Show();
        // The main window goes too: its docks, tabs and MDI children are hidden with it (the right column keeps its width).
        _rightWidthBeforeHandover = _rightHost.Width;
        _mainHiddenForHandover = Visible;
        if (Visible) Hide();
        form.Activate();
        Program.AppLog($"handover: {form.Message}");
    }

    void ContinueHandover()
    {
        if (_handoverFor is not { } p) return;
        // The view switches while everything is still hidden, so windows reappear already showing this player's view.
        _hotSeat?.TakeOver(p);
        DismissHandover();
        SetStatus($"Player: {p.Name}");
        OpenRunsAfterHandover();
    }

    /// <summary>Closes the handover screen and brings back what it hid. No-op when it is not up.</summary>
    void DismissHandover()
    {
        _handoverFor = null;
        _handoverRetry?.Stop();
        if (_handover is { } h)
        {
            _handover = null;
            try { if (!h.IsDisposed) h.CloseForReal(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("handover close: " + ex.Message); }
            h.Dispose();
        }
        if (_mainHiddenForHandover && !_shuttingDown && !IsDisposed && !Disposing)
        {
            _mainHiddenForHandover = false;
            Show();
            if (_rightWidthBeforeHandover > 0) _rightHost.Width = _rightWidthBeforeHandover;
            Activate();
        }
        foreach (var f in _hiddenForHandover)
            if (!f.IsDisposed && !_shuttingDown) { try { f.Show(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("handover restore: " + ex.Message); } }
        _hiddenForHandover.Clear();
    }

    /// <summary>Calculations queued by the phase the new player starts in open once they are at the screen.</summary>
    void OpenRunsAfterHandover()
    {
        if (_runsAfterHandover.Count == 0) return;
        var runs = _runsAfterHandover.ToList();
        _runsAfterHandover.Clear();
        if (_session?.Game is not { } g) return;
        foreach (var r in runs)
            if (g.State.Pending.Any(x => x.Id == r.Id)) Guard(() => OpenPendingRun(r.Id));
    }

    // ---------- player switcher (status bar) ----------

    void UpdatePlayerButton()
    {
        var active = _hotSeat?.Active == true;
        _sbPlayer.Visible = active;
        if (!active) return;
        _sbPlayer.Text = $"Player: {_hotSeat!.Current?.Name ?? "—"}";
    }

    void BuildPlayerMenu()
    {
        _sbPlayer.DropDownItems.Clear();
        if (_session?.Game is not { } g || _hotSeat is not { } hs) return;
        foreach (var p in Players.Effective(g.State, _session.Rules))
        {
            var target = p;
            var mi = new ToolStripMenuItem($"{p.Name} ({HandoverForm.SidesText(p, _session.Rules)})") { Checked = hs.Current?.Id == p.Id };
            mi.Click += (_, _) => Guard(() => SwitchPlayer(target));
            _sbPlayer.DropDownItems.Add(mi);
        }
        if (_sbPlayer.DropDownItems.Count == 0) _sbPlayer.DropDownItems.Add(new ToolStripMenuItem("(no players)") { Enabled = false });
    }

    /// <summary>A deliberate swap: the handover screen comes first, for the chosen player too (spec 4).</summary>
    void SwitchPlayer(PlayerDef p)
    {
        if (_session == null || _hotSeat is not { Active: true }) return;
        _session.SetViewSides(new HashSet<string>());   // nothing of the leaving player stays on screen
        CloseFormulaWindows($"Pass to {p.Name}");
        RequestHandover(p);
    }

    // ---------- player list (Settings → Game → Players) ----------

    (Quartermaster.Rules.RulesPack? Pack, IReadOnlyList<PlayerDef> Players)? GamePlayers() =>
        _session?.Game is { } g ? (_session.Rules, g.State.Players.ToList()) : null;

    /// <summary>Executes SetPlayers when the list changed and is valid (the editor validated it already; this re-checks).</summary>
    void SetGamePlayers(IReadOnlyList<PlayerDef> players)
    {
        if (_session?.Game is not { } g) return;
        if (PlayersEditor.Same(g.State.Players, players)) return;
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
        UpdatePlayerButton();
    }

    // ---------- smoke ----------

    /// <summary>
    /// Smoke (--screenshot-handover): gives the open game two players splitting the pack's sides, checks the handover screen comes up
    /// with every other window hidden, saves its picture, clicks Continue and checks the view, then puts the game back to solo.
    /// </summary>
    void SmokeHandover()
    {
        var shot = _opts.ScreenshotHandover;
        if (_session?.Game is not { } g || _session.Rules is not { } pack || _hotSeat is not { } hs) { Program.AppLog("smoke: handover skipped (no game, rules pack or hot-seat)"); return; }
        var sides = pack.Sides.Where(s => s != null && !string.IsNullOrEmpty(s.Id)).Select(s => s.Id).ToList();
        if (sides.Count < 2) { Program.AppLog("smoke: handover skipped (the rules pack has fewer than two sides)"); return; }
        var a = new PlayerDef("p1", "Player 1", sides.Where((_, i) => i % 2 == 0).ToList());
        var b = new PlayerDef("p2", "Player 2", sides.Where((_, i) => i % 2 == 1).ToList());
        SetGamePlayers(new[] { a, b });
        Application.DoEvents();
        Application.DoEvents();
        var up = _handover is { IsDisposed: false, Visible: true } h ? h : null;
        var othersHidden = !Visible && Application.OpenForms.Cast<Form>().All(f => f is HandoverForm || f == this || f.IsMdiChild || !f.Visible);
        var publicOnly = _session.ViewSides is { Count: 0 };
        var okUp = up != null && up.Message.StartsWith("Pass to ", StringComparison.Ordinal) && othersHidden && publicOnly;
        Program.AppLog($"smoke: handover screen {(okUp ? "ok" : "FAILED")} ('{up?.Message}', others hidden {othersHidden}, public-only view {publicOnly})");
        if (!okUp) Program.ExitCode = 1;
        if (up != null && shot != null) up.SaveScreenshot(shot);
        var target = up?.Player;
        up?.SmokeContinue();
        Application.DoEvents();
        var okAfter = up != null && Visible && _handover == null && target != null && hs.Current?.Id == target.Id
            && _session.ViewSides is { } vs && vs.SetEquals(target.Sides) && _sbPlayer.Text == $"Player: {target.Name}";
        Program.AppLog($"smoke: handover Continue {(okAfter ? "ok" : "FAILED")} (main visible {Visible}, status '{_sbPlayer.Text}')");
        if (!okAfter) Program.ExitCode = 1;
        SetGamePlayers(Array.Empty<PlayerDef>());
        Application.DoEvents();
        var okSolo = _session.ViewSides == null && !_sbPlayer.Visible && _handover == null && Visible;
        Program.AppLog($"smoke: back to solo {(okSolo ? "ok" : "FAILED")}");
        if (!okSolo) Program.ExitCode = 1;
    }

    /// <summary>
    /// Smoke (--screenshot-playerview): two players; a piece gets a private Fuel change with a marker value; the player who does not own
    /// the piece takes over. Checks that their view does not hold the marker and the owner's does, then saves the ledger and the main window.
    /// </summary>
    void SmokePlayerView()
    {
        var shot = _opts.ScreenshotPlayerView!;
        if (_session?.Game is not { } g || _session.Rules is not { } pack || _hotSeat is not { } hs) { Program.AppLog("smoke: playerview skipped (no game, rules pack or hot-seat)"); return; }
        var sides = pack.Sides.Where(s => s != null && !string.IsNullOrEmpty(s.Id)).Select(s => s.Id).ToList();
        var first = pack.Properties.FirstOrDefault(p => p.Scope == Quartermaster.Rules.PropertyScope.Pieces);
        if (first != null && first.Visibility != Quartermaster.Rules.Visibility.Private)
        {
            // Older packs load every property Public; make one private in memory (never saved by the smoke).
            _session.UpdateRules(p => p with { Properties = p.Properties.Select(x => x.Id == first.Id ? x with { Visibility = Quartermaster.Rules.Visibility.Private } : x).ToList() });
            pack = _session.Rules!;
        }
        var fuel = pack.Properties.FirstOrDefault(p => p.Scope == Quartermaster.Rules.PropertyScope.Pieces && p.Visibility == Quartermaster.Rules.Visibility.Private);
        var piece = g.State.Pieces.Values.FirstOrDefault(p => p.StackId != null);
        if (sides.Count < 2 || fuel == null || piece == null) { Program.AppLog("smoke: playerview skipped (needs two sides, a private piece property and a piece)"); return; }
        var a = new PlayerDef("p1", "Player 1", new[] { sides[0] });
        var b = new PlayerDef("p2", "Player 2", new[] { sides[1] });
        SetGamePlayers(new[] { a, b });
        var key = LedgerKey.Piece(piece.Id);
        g.Execute(new LedgerDelta(key, fuel.Id, Ledger.Get(g.State, key, fuel.Id) ?? 0, 7731, "manual", null, null, "smoke", null, null, "Smoke", null, null, piece.Id, null));
        string Json(string side) => System.Text.Json.JsonSerializer.Serialize(
            PlayerView.For(g.State, pack, _session.Model, new HashSet<string> { side }), QuartermasterJson.Options);
        var seeing = sides.Take(2).Where(s => Json(s).Contains("7731", StringComparison.Ordinal)).ToList();
        var viewer = seeing.Count == 1 ? (seeing[0] == sides[0] ? b : a) : b;
        hs.TakeOver(viewer);
        if (_handover != null) DismissHandover();
        Application.DoEvents();
        var leaked = _session.View is { } v && System.Text.Json.JsonSerializer.Serialize(v, QuartermasterJson.Options).Contains("7731", StringComparison.Ordinal);
        var ok = seeing.Count == 1 && !leaked && _session.ViewSides is { } vs && vs.SetEquals(viewer.Sides);
        Program.AppLog($"smoke: playerview {(ok ? "ok" : "FAILED")} (owners seeing the marker: {seeing.Count}, {viewer.Name}'s view leaks: {leaked})");
        if (!ok) Program.ExitCode = 1;
        ShowLedger();
        for (var i = 0; i < 40; i++) { Thread.Sleep(50); Application.DoEvents(); } // let the debounced view refresh run and the ledger and map draw
        if (_ledgers.TryGetValue("ledger", out var w) && !w.IsDisposed && w is LedgerWindow lw)
        {
            lw.Activate(); lw.Update(); Application.DoEvents();
            lw.SaveScreenshot(Path.ChangeExtension(shot, null) + "-ledger.png");
            lw.Close(); // the main window picture below shows the map, not the ledger
            Application.DoEvents();
            FirstMap()?.Activate();
        }
        for (var i = 0; i < 40; i++) { Thread.Sleep(50); Application.DoEvents(); } // the map redraws its tiles after the view change
        SaveScreenshot(this, shot, true);
        SetGamePlayers(Array.Empty<PlayerDef>());
        Application.DoEvents();
    }
}
