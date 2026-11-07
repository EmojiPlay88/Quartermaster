using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>The turn sequencer's UI wiring: tracker strip, phase dock, Next Phase flow, phase-boundary autosave and the phase smoke.</summary>
sealed partial class MainForm
{
    readonly PhaseStrip _strip = new() { Visible = false };
    // The right-hand column: phase panel on top, palette below. The docks only toggle their own host (and a private splitter that is never
    // parented); the column's width and the real splitters follow from which hosts are showing.
    readonly Panel _rightHost = new() { Dock = DockStyle.Right, Width = 0 };
    readonly Splitter _rightSplitter = new() { Dock = DockStyle.Right, Width = 4, Visible = false };
    readonly Splitter _stackSplit = new() { Dock = DockStyle.Top, Height = 4, Visible = false };
    bool _outlinerWasShown;
    readonly Splitter _stackSplit2 = new() { Dock = DockStyle.Top, Height = 4, Visible = false };
    readonly Panel _outlinerHost = new() { Dock = DockStyle.Top, Height = 300, Visible = false };
    readonly Panel _phaseHost = new() { Dock = DockStyle.Top, Height = 330, Visible = false };
    readonly Splitter _phaseSplitter = new() { Visible = false };
    readonly ToolStripMenuItem _viewPhases = new("&Phase panel");
    PhaseDock? _phaseDock;
    PhaseController? _phases;
    PhaseWatcher? _stripWatch;

    void BuildRightStack()
    {
        _rightHost.Controls.Add(_paletteHost);  // docks last (Fill)
        _rightHost.Controls.Add(_stackSplit2);
        _rightHost.Controls.Add(_outlinerHost);
        _rightHost.Controls.Add(_stackSplit);
        _rightHost.Controls.Add(_phaseHost);    // docks first (Top)
        _phaseHost.VisibleChanged += (_, _) => SyncRightStack();
        _outlinerHost.VisibleChanged += (_, _) => SyncRightStack();
        _paletteHost.VisibleChanged += (_, _) => SyncRightStack();
    }

    /// <summary>Phase panel, outliner and palette share the column top to bottom; the last one showing fills the rest.</summary>
    void SyncRightStack()
    {
        var hosts = new[] { _phaseHost, _outlinerHost, _paletteHost }.Where(h => h.Visible).ToList();
        for (var i = 0; i < hosts.Count; i++) hosts[i].Dock = i == hosts.Count - 1 ? DockStyle.Fill : DockStyle.Top;
        _stackSplit.Visible = _phaseHost.Visible && (_outlinerHost.Visible || _paletteHost.Visible);
        _stackSplit2.Visible = _outlinerHost.Visible && _paletteHost.Visible;
        var any = hosts.Count > 0;
        _rightSplitter.Visible = any;
        var outShown = _outlinerHost.Visible && !_outlinerWasShown;
        _outlinerWasShown = _outlinerHost.Visible;
        if (any && _rightHost.Width < 50) _rightHost.Width = _outlinerHost.Visible ? 440 : 300;
        else if (outShown && _rightHost.Width < 440) _rightHost.Width = 440;
        else if (!any) _rightHost.Width = 0;
    }

    void InitPhases()
    {
        _phaseDock = new PhaseDock(this, _wm, _phaseHost, _phaseSplitter, _viewPhases, () => _session?.Rules is { Phases.Count: > 0 }, Guard);
        _viewPhases.Click += (_, _) => Guard(() => _phaseDock!.Toggle());
        _strip.StartRequested += () => Guard(NextPhase);
    }

    /// <summary>Builds the controller, strip watcher and phase panel for a freshly opened session.</summary>
    void CreatePhases(ModuleSession session)
    {
        var c = new PhaseController(session, (cmd, st) => CommandLog.Prepare(cmd, st, session.Model));
        c.PhaseBoundary += OnPhaseBoundary;
        c.RunsQueued += OnRunsQueued;
        c.DifficultyChanged += OnDifficultyChanged;
        _phases = c;
        _history = new HistoryController(session, c, (cmd, st) => CommandLog.Prepare(cmd, st, session.Model));
        _history.Rewound += OnHistoryRewound;
        CreateHotSeat(session, c, _history);
        _stripWatch = new PhaseWatcher(_strip, session, RefreshStrip);
        _phaseDock!.Create(new PhasePanel(session, c, () => Guard(NextPhase), CentreOnPiece, OpenPendingRun, OpenDocumentAtPage));
        CreateAlerts(session);
        RefreshStrip();
        UpdatePhaseUi();
    }

    void DestroyPhases()
    {
        DestroyHotSeat();
        _stripWatch?.Dispose();
        _stripWatch = null;
        _phaseDock?.Destroy();
        if (_phases != null) { _phases.PhaseBoundary -= OnPhaseBoundary; _phases.RunsQueued -= OnRunsQueued; _phases.DifficultyChanged -= OnDifficultyChanged; }
        _phases = null;
        if (_history != null) _history.Rewound -= OnHistoryRewound;
        _history = null;
        RefreshHistoryUi();
        _strip.Visible = false;
        DestroyAlerts();
    }

    void RefreshStrip()
    {
        if (_phases != null) _strip.SetView(_phases.View());
    }

    /// <summary>Docking goes from the highest child index down: menu first, then toolbar, then the strip under it. Showing the strip can reorder it, so this is repeated.</summary>
    void FixTopOrder()
    {
        Controls.SetChildIndex(_menu, Controls.Count - 1);
        Controls.SetChildIndex(_toolbar, Controls.Count - 2);
        Controls.SetChildIndex(_alertBar, Controls.Count - 3);
        Controls.SetChildIndex(_strip, Controls.Count - 4);
    }

    /// <summary>Shows the strip while a game and a rules pack exist; shows the dock only when the pack has phases.</summary>
    void UpdatePhaseUi()
    {
        _strip.Visible = _session?.Game != null && _session.Rules != null;
        UpdateAlertBarVisible();
        FixTopOrder();
        _viewPhases.Enabled = _session?.Game != null && _session.Rules is { Phases.Count: > 0 };
        _phaseDock?.Apply();
        if (_strip.Visible) RefreshStrip();
    }

    /// <summary>After a load or rules change: moves a game whose phase vanished to the phase now at that place and says so.</summary>
    void ReconcilePhases()
    {
        if (_phases == null || _session?.Game == null) return;
        try
        {
            if (_phases.Reconcile() is { } notice) SetStatus(notice);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("reconcile phases: " + ex.Message);
        }
    }

    /// <summary>The phase just changed: write an autosave now (AutosaveMinutes = 0 is off).</summary>
    void OnPhaseBoundary()
    {
        try
        {
            if (_autosaver?.SaveNow(DateTimeOffset.UtcNow, _settings.Current.AutosaveMinutes) != null) _autosavedSeq = _changeSeq;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("phase autosave: " + ex.Message);
        }
    }

    bool HasPhases => _session?.Game != null && _session.Rules is { Phases.Count: > 0 };

    /// <summary>Ctrl+Right: Next Phase when the pack has phases, otherwise the plain turn counter.</summary>
    bool NextTurnCommand()
    {
        if (_session?.Game == null) return false;
        if (HasPhases) NextPhase();
        else _dice!.StepTurn(1);
        return true;
    }

    /// <summary>Start game, or advance one phase. Open decisions only warn: the dialog lists them and the user may continue anyway.</summary>
    void NextPhase()
    {
        if (_phases == null || _session?.Game == null) return;
        var v = _phases.View();
        if (v.NoPhases) return;
        if (v.PlayingText is { } playing) { SetStatus(playing); return; }   // hot-seat: another side's phase
        if (v.BeforeStart)
        {
            _phases.Start();
            RefreshPhaseViews();
            return;
        }
        if (v.Decisions.Count > 0)
        {
            if (Program.Smoke) Program.AppLog($"smoke: Next Phase with {v.Decisions.Count} open decision(s): continuing without the dialog");
            else
            {
                using var dlg = new NextPhaseDialog(v.Decisions);
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    if (dlg.JumpTo is { } d) JumpToDecision(d);
                    return;
                }
            }
        }
        if (_phases.Advance() is { } pass) SetStatus(pass);
        RefreshPhaseViews();
    }

    void RefreshPhaseViews()
    {
        _stripWatch?.Flush();
        _phaseDock?.Panel?.Flush();
    }

    void JumpToDecision(Decision d)
    {
        if (d.Kind == DecisionKind.Pending && d.RunId != null) OpenPendingRun(d.RunId);
        else if (d.Kind == DecisionKind.Checklist)
        {
            _phaseDock?.Reveal();
            _phaseDock?.Panel?.SelectChecklist(d.Index);
        }
        else if (d.PieceIds.FirstOrDefault() is { } id) CentreOnPiece(id);
    }

    /// <summary>The session's phase controller (null without a game); map windows route move gestures through it.</summary>
    public PhaseController? Phases => _phases;

    /// <summary>Triggers queued runs: each opens its calculation UI at once. Always on the UI thread, never throws.</summary>
    void OnRunsQueued(IReadOnlyList<PendingRun> runs)
    {
        void Open()
        {
            if (IsDisposed) return;
            SetStatus($"{runs.Count} calculation(s) waiting in the phase panel");
            // Hot-seat: the phase belongs to the next player; their calculations open once they are at the screen.
            if (_handoverFor != null || _hotSeat?.Pending != null) { _runsAfterHandover.AddRange(runs); return; }
            foreach (var r in runs) Guard(() => OpenPendingRun(r.Id));
        }
        if (IsHandleCreated) BeginInvoke(Open);   // never inside Advance / ExecuteMove: the autosave and drag end finish first
    }

    /// <summary>
    /// Opens the calculation UI of a pending run in the CURRENT difficulty, whatever mode it was queued in (spec 7.3).
    /// Fully automatic: the run is finished from the auto values, asking once (missing-input dialog) only for what has no auto value.
    /// Hardcore / semi-auto: the checked dialog for one unit or the checked batch grid for several (semi-auto adds the cheat sheet).
    /// </summary>
    public void OpenPendingRun(string runId)
    {
        if (_session?.Game is not { } game || _phases == null) return;
        var run = game.State.Pending.FirstOrDefault(r => r.Id == runId);
        if (run == null) { SetStatus("That calculation is no longer waiting."); return; }
        var open = run.Units.Where(u => !u.Applied).ToList();
        if (open.Count == 0) return;
        // Hot-seat: a run with no unit of a held side (or of no side) is not this player's to calculate.
        if (!open.Any(u => _session.CanAct(u.PieceId))) { SetStatus("That calculation " + ModuleSession.OtherSideText + "."); return; }
        if (game.State.Difficulty == Difficulty.FullyAuto)
        {
            OpenPendingRunAutomatic(run);
            return;
        }
        OpenPendingRunAsUnits(run, open);
    }

    void OpenPendingRunAutomatic(PendingRun run)
    {
        if (_session == null || _phases == null) return;
        if (MissingInputs.For(_session, run).Count == 0)
        {
            // Nothing to ask (for example a run left over from a manual mode): finish it from the auto values, no window.
            var (applied, failed) = MissingInputs.ApplyAll(_session, _phases, run, new Dictionary<string, string>());
            SetStatus($"{run.FormulaName}: {applied} unit(s) calculated automatically" + (failed.Count > 0 ? $", {failed.Count} could not be" : ""));
            return;
        }
        if (Program.Smoke) { Program.AppLog($"smoke: run {run.FormulaName} needs input; dialog skipped in smoke"); return; }
        using var dlg = new MissingInputDialog(_session, _phases, run);
        if (dlg.ShowDialog(this) == DialogResult.Retry
            && _session?.Game?.State.Pending.FirstOrDefault(r => r.Id == run.Id) is { } fresh   // re-read: the dialog may have applied some units
            && fresh.Units.Where(u => !u.Applied).ToList() is { Count: > 0 } left)
            OpenPendingRunAsUnits(fresh, left);   // "Per unit..."
    }

    /// <summary>The checked UI for a run: the batch grid for several units, the unit dialog for one. Also "Per unit..." from the missing-input dialog.</summary>
    void OpenPendingRunAsUnits(PendingRun run, List<PendingUnit> open)
    {
        if (_session == null || _phases == null) return;
        if (open.Count > 1)
        {
            var model = new BatchModel(_session, _phases, run.Id);
            OpenChild(_docs, "batch:" + run.Id, () => new BatchGridWindow(model, _session, run.Id, row => Guard(() => OpenBatchRow(run.Id, row))));
        }
        else OpenUnitDialog(run, open[0]);
    }

    /// <summary>The difficulty changed: every open formula dialog, batch grid and cheat sheet belongs to the old mode, so they close. Pending runs stay pending.</summary>
    void OnDifficultyChanged()
    {
        if (IsDisposed || _session == null) return;
        CloseFormulaWindows("Difficulty changed — reopen from the phase panel");
    }

    /// <summary>Double-click in the batch grid: that unit's formula dialog, linked to the run so applying it counts for the batch.</summary>
    void OpenBatchRow(string runId, int row)
    {
        if (_session?.Game is not { } game) return;
        var run = game.State.Pending.FirstOrDefault(r => r.Id == runId);
        if (run == null || row < 0 || row >= run.Units.Count || run.Units[row].Applied) return;
        OpenUnitDialog(run, run.Units[row]);
    }

    void OpenUnitDialog(PendingRun run, PendingUnit unit)
    {
        if (_session?.Game is not { } game) return;
        if (unit.PieceId != null && !game.State.Pieces.ContainsKey(unit.PieceId)) { SetStatus($"{unit.Label} is no longer on the map."); return; }
        FormulaDialog.Show(_session, run.FormulaId, unit.PieceId, this, this, new RunLink(run.Id, unit.Moved));
    }

    // ---------- smoke ----------

    /// <summary>
    /// Smoke: checks the strip against the pack (no-phases text, or Start game before the game starts), saves the --screenshot-phases picture,
    /// and with --smoke-phases starts the game, steps through every phase through the Ctrl+Right handler and checks the autosave hook.
    /// </summary>
    void SmokePhases()
    {
        if (_phases == null || _session?.Game == null || _session.Rules == null)
        {
            Program.AppLog("smoke: phases FAILED: no game, rules pack or controller");
            Program.ExitCode = 1;
            return;
        }
        RefreshPhaseViews();
        Update();
        var v = _phases.View();
        var texts = _strip.ItemTexts;
        if (v.NoPhases)
        {
            var ok = texts.Count == 1 && texts[0] == PhaseStrip.NoPhasesText && !_strip.StartVisible && !_phaseHost.Visible;
            Program.AppLog($"smoke: phases strip no-phases text {(ok ? "ok" : "FAILED")} ({string.Join(" | ", texts)})");
            if (!ok) Program.ExitCode = 1;
        }
        else
        {
            var ok = v.BeforeStart && _strip.StartVisible && _phaseHost.Visible && _phaseDock!.Panel!.NextButtonText == "Start game ▶";
            Program.AppLog($"smoke: phases strip before start {(ok ? "ok" : "FAILED")} (start button {_strip.StartVisible}; items {string.Join(" | ", texts)})");
            if (!ok) Program.ExitCode = 1;
        }
        if (_opts.ScreenshotPhases != null)
        {
            SaveScreenshot(this, _opts.ScreenshotPhases, true);
            _phaseDock?.Panel?.SaveScreenshot(Path.ChangeExtension(_opts.ScreenshotPhases, null) + "-panel.png");
        }
        if (!_opts.SmokePhases || v.NoPhases) return;

        var autosaves = CountAutosaves();
        var pack = _session.Rules;
        _phases.Start();
        var state = _session.Game.State;
        var startOk = state.Position != null && !_phases.View().BeforeStart && (CountAutosaves() > autosaves) == (_settings.Current.AutosaveMinutes > 0);
        Program.AppLog($"smoke: phases start {(startOk ? "ok" : "FAILED")} (autosaves {autosaves} -> {CountAutosaves()}, interval {_settings.Current.AutosaveMinutes} min)");
        if (!startOk) Program.ExitCode = 1;

        using (var dlg = new NextPhaseDialog(new[] { new Decision(DecisionKind.Checklist, "Smoke item", 0, null, Array.Empty<string>()) }))
            Program.AppLog($"smoke: next phase dialog built with {dlg.Controls.Count} control(s)");
        if (_opts.ScreenshotPhases != null)
        {
            SaveScreenshot(this, Path.ChangeExtension(_opts.ScreenshotPhases, null) + "-started.png", true);
            _phaseDock?.Panel?.SaveScreenshot(Path.ChangeExtension(_opts.ScreenshotPhases, null) + "-started-panel.png");
        }
        SmokeTurnWrap(pack);
        if (_opts.ScreenshotPhases != null)
        {
            SaveScreenshot(this, Path.ChangeExtension(_opts.ScreenshotPhases, null) + "-wrapped.png", true);
            _phaseDock?.Panel?.SaveScreenshot(Path.ChangeExtension(_opts.ScreenshotPhases, null) + "-wrapped-panel.png");
        }
        SmokeBatchFlow();
        SmokeMoveFlow();
    }

    bool SmokeUiOpen() => OwnedForms.OfType<FormulaDialog>().Any() || _docs.Any(kv => kv.Key.StartsWith("batch:", StringComparison.Ordinal) && kv.Value is { IsDisposed: false });

    /// <summary>Manual modes: Calculate + Apply in every opened dialog, Calculate all + Apply all in every batch grid, then tick the checklist.</summary>
    void SmokeClearDecisions(PhaseDef cur, bool leaveChecklist)
    {
        if (_session?.Game is not { } game || _phases == null) return;
        Application.DoEvents();
        foreach (var run in game.State.Pending.ToList())
        {
            if (_docs.TryGetValue("batch:" + run.Id, out var form) && form is BatchGridWindow { IsDisposed: false } win)
            {
                win.SmokeCalculateAll();
                win.SmokeApplyAll();
            }
            foreach (var d in OwnedForms.OfType<FormulaDialog>().Where(d => d.Link?.RunId == run.Id).ToList())
            {
                d.SmokeCalculate();
                d.SmokeClickApply();
            }
            Application.DoEvents();
            if (game.State.Pending.Any(r => r.Id == run.Id))
            {
                Program.AppLog($"smoke: turn wrap FAILED: run {run.FormulaName} still pending in {cur.Name}");
                Program.ExitCode = 1;
            }
        }
        CloseFormulaDialogs();
        if (leaveChecklist) return;
        for (var i = 0; i < (cur.Checklist?.Count ?? 0); i++) _phases.Tick(i, true);
    }

    /// <summary>
    /// Smoke: plays turn 1 to the wrap into turn 2 through the Ctrl+Right handler, repeating the block. Manual modes clear the decisions as they go
    /// (the first Combat visit leaves the checklist open so Next Phase takes the Continue-anyway path); fully automatic must never open a dialog or grid.
    /// </summary>
    void SmokeTurnWrap(RulesPack pack)
    {
        if (_session?.Game is not { } game || _phases == null) return;
        var auto = game.State.Difficulty == Difficulty.FullyAuto;
        var sawUi = false;
        var combatVisits = 0;
        var visits = 0;
        for (; visits < pack.Phases.Count * 4 && game.State.Position is { Turn: 1 }; visits++)
        {
            Application.DoEvents();
            if (_phases.View().Current is not { } cur) break;
            var firstCombat = cur.Checklist is { Count: > 0 } && ++combatVisits == 1;
            if (cur.OnMove is { Count: > 0 } && game.State.Pieces.Values.FirstOrDefault(p => PieceInfo.Name(p) == "Red 1") is { } piece)
            {
                _phases.ExecuteMove(new[] { new MovePieces(new[] { piece.Id }, piece.MapId, new PointD(piece.Pos.X + (visits % 2 == 0 ? 60 : -60), piece.Pos.Y), null) });
                Application.DoEvents();
            }
            sawUi |= SmokeUiOpen();
            if (!auto) SmokeClearDecisions(cur, firstCombat);
            else CloseFormulaDialogs();
            var before = game.State.Position;
            var moved = _handlers.TryGetValue("turn.next", out var h) && h();
            if (!moved || Equals(before, game.State.Position)) break;
        }
        Application.DoEvents();
        sawUi |= SmokeUiOpen();
        RefreshPhaseViews();
        var after = _phases.View();
        var blockLines = game.State.Log.Count(e => e.Text.StartsWith("Turn 1 · ", StringComparison.Ordinal) && e.Text.EndsWith("Movement → Combat", StringComparison.Ordinal));
        var autosaves = CountAutosaves();
        var ok = after.Turn == 2 && game.State.Position?.Turn == 2 && blockLines == 2
            && (autosaves > 0 || _settings.Current.AutosaveMinutes <= 0) && (!auto || !sawUi);
        Program.AppLog($"smoke: turn wrap {(ok ? "ok" : "FAILED")} in {visits} step(s): turn {after.Turn}, Movement → Combat lines {blockLines}, autosaves {autosaves}, dialog or grid opened {sawUi} ({(auto ? "must be false" : "allowed")})");
        if (!ok) Program.ExitCode = 1;
        CloseFormulaDialogs();
    }

    void BatchFail(string text)
    {
        Program.AppLog("smoke: batch grid FAILED: " + text);
        Program.ExitCode = 1;
    }

    /// <summary>
    /// Smoke: goes to a phase whose OnStart runs per unit; hardcore / semi-automatic open the batch grid (virtual rows, cheat sheet in semi-automatic).
    /// A wrong value must be skipped by Apply all, Calculate all must fix it, and the second Apply all finishes the run (window closes, summary logged).
    /// Fully automatic applies the run itself and logs the summary.
    /// </summary>
    void SmokeBatchFlow()
    {
        if (_phases == null || _session?.Game is not { } game || _session.Rules is not { } pack) return;
        var target = pack.Phases.FirstOrDefault(p => p.OnStart.Any(e => e.ForEach == ForEachMode.Units));
        if (target == null) { Program.AppLog("smoke: batch grid skipped (no phase starts a per-unit run)"); return; }
        var pendingBefore = game.State.Pending.Select(r => r.Id).ToHashSet();
        for (var i = 0; i < pack.Phases.Count * 4 && _phases.View().Current?.Id != target.Id; i++) _phases.Advance();
        Application.DoEvents();
        if (_phases.View().Current?.Id != target.Id) { BatchFail("could not reach the per-unit phase"); return; }
        var formulaId = target.OnStart.First(e => e.ForEach == ForEachMode.Units).FormulaId;
        var run = game.State.Pending.LastOrDefault(r => !pendingBefore.Contains(r.Id) && r.FormulaId == formulaId);
        if (game.State.Difficulty == Difficulty.FullyAuto)
        {
            var ok = run == null && game.State.Log.Any(e => e.Kind == LogKind.Calc && e.Text.Contains(" units, ", StringComparison.Ordinal));
            Program.AppLog($"smoke: batch (fully automatic) {(ok ? "ok" : "FAILED")}: run pending {run != null}, summary line logged {game.State.Log.Any(e => e.Kind == LogKind.Calc)}");
            if (!ok) Program.ExitCode = 1;
            return;
        }
        if (run == null) { BatchFail("the phase queued no run"); return; }
        if (!_docs.TryGetValue("batch:" + run.Id, out var form) || form is not BatchGridWindow win || win.IsDisposed) { BatchFail("no batch grid opened for a run of " + run.Units.Count + " units"); return; }
        win.Update();
        var rowsOk = win.SmokeRowCount == run.Units.Count && win.Text.StartsWith(run.FormulaName, StringComparison.Ordinal);
        Program.AppLog($"smoke: batch grid opened: title '{win.Text}', {win.SmokeRowCount} row(s) for {run.Units.Count} unit(s) {(rowsOk ? "ok" : "FAILED")}");
        if (!rowsOk) Program.ExitCode = 1;
        win.SmokeSelectRow(0);
        if (game.State.Difficulty == Difficulty.SemiAuto)
        {
            var cheatOk = win.CheatSheet is { IsDisposed: false, Visible: true } && win.CheatSheet.RowTexts.Count > 0;
            Program.AppLog($"smoke: batch cheat sheet follows the row {(cheatOk ? "ok" : "FAILED")} ({win.CheatSheet?.RowTexts.Count ?? 0} row(s))");
            if (!cheatOk) Program.ExitCode = 1;
        }
        else if (win.CheatSheet != null) BatchFail("hardcore must not show the cheat sheet");

        win.SmokeCalculateAll();
        var wrongAfterCalc = win.SmokeWrongCells;
        win.SmokeSetCell(0, 1, "999");
        var wrongTyped = win.SmokeWrongCells;
        var okWrong = wrongAfterCalc == 0 && wrongTyped == 1;
        Program.AppLog($"smoke: batch Calculate all then a wrong value: wrong cells {wrongAfterCalc} -> {wrongTyped} {(okWrong ? "ok" : "FAILED")}");
        if (!okWrong) Program.ExitCode = 1;
        if (_opts.ScreenshotBatch != null) win.SaveScreenshot(_opts.ScreenshotBatch);

        win.SmokeApplyAll();
        var stillPending = game.State.Pending.FirstOrDefault(r => r.Id == run.Id);
        var appliedNow = stillPending?.Units.Count(u => u.Applied) ?? run.Units.Count;
        var skipOk = stillPending != null && appliedNow == run.Units.Count - 1 && !stillPending.Units[0].Applied && !win.IsDisposed;
        Program.AppLog($"smoke: batch Apply all skipped the wrong row {(skipOk ? "ok" : "FAILED")} ({appliedNow} of {run.Units.Count} applied)");
        if (!skipOk) { Program.ExitCode = 1; return; }

        win.SmokeCalculateAll();
        var logMark = game.State.Log.Count;
        win.SmokeApplyAll();
        Application.DoEvents();
        var summary = game.State.Log.Skip(logMark).Any(e => e.Kind == LogKind.Calc && e.Text.Contains(" units, ", StringComparison.Ordinal));
        var doneOk = !game.State.Pending.Any(r => r.Id == run.Id) && (win.IsDisposed || !win.Visible) && summary;
        Program.AppLog($"smoke: batch Apply all finished the run {(doneOk ? "ok" : "FAILED")} (decision gone {!game.State.Pending.Any(r => r.Id == run.Id)}, window closed {win.IsDisposed || !win.Visible}, summary line {summary})");
        if (!doneOk) Program.ExitCode = 1;
        CloseFormulaDialogs();
    }

    void SmokeFail(string text)
    {
        Program.AppLog("smoke: move flow FAILED: " + text);
        Program.ExitCode = 1;
    }

    void CloseFormulaDialogs()
    {
        Application.DoEvents();   // dialogs for queued runs open through BeginInvoke
        foreach (var d in OwnedForms.OfType<FormulaDialog>().ToList()) d.Close();
    }

    /// <summary>
    /// Smoke: goes to a phase that has an OnMove entry, drags a piece two cells through ExecuteMove, and checks the mode's calculation UI:
    /// hardcore / semi-auto open a checked dialog with an empty Moved box (wrong value disables Apply, Calculate enables it, Apply logs the
    /// calculation and resolves the run; semi-auto also shows the cheat sheet); fully automatic applies it without any dialog.
    /// </summary>
    void SmokeMoveFlow()
    {
        if (_phases == null || _session?.Game is not { } game || _session.Rules is not { } pack) return;
        var target = pack.Phases.FirstOrDefault(p => p.OnMove is { Count: > 0 });
        if (target == null) { Program.AppLog("smoke: move flow skipped (no phase has an OnMove entry)"); return; }
        CloseFormulaDialogs();
        for (var i = 0; i < pack.Phases.Count * 4 && _phases.View().Current?.Id != target.Id; i++) _phases.Advance();
        CloseFormulaDialogs();
        if (_phases.View().Current?.Id != target.Id) { SmokeFail("could not reach the move phase"); return; }
        var piece = game.State.Pieces.Values.FirstOrDefault(p => PieceInfo.Name(p) == "Red 1") ?? game.State.Pieces.Values.FirstOrDefault();
        if (piece == null) { SmokeFail("no piece to move"); return; }

        var mode = game.State.Difficulty;
        var pendingBefore = game.State.Pending.Select(r => r.Id).ToHashSet();
        var logBefore = game.State.Log.Count;
        _phases.ExecuteMove(new[] { new MovePieces(new[] { piece.Id }, piece.MapId, new PointD(piece.Pos.X + 100, piece.Pos.Y), null) });
        Application.DoEvents();
        var run = game.State.Pending.LastOrDefault(r => !pendingBefore.Contains(r.Id) && r.Trigger == "move");
        var dialogs = OwnedForms.OfType<FormulaDialog>().Where(d => d.Link != null).ToList();
        if (mode == Difficulty.FullyAuto)
        {
            var ok = dialogs.Count == 0 && game.State.Log.Count > logBefore;
            Program.AppLog($"smoke: move flow (fully automatic) {(ok ? "ok" : "FAILED")}: {dialogs.Count} dialog(s), {game.State.Log.Count - logBefore} new log line(s), run pending {run != null}");
            if (!ok) Program.ExitCode = 1;
            SmokeMissingInputDialog();
            return;
        }
        if (run == null) { SmokeFail("the move queued no run"); return; }
        var dlg = dialogs.FirstOrDefault(d => d.Link!.RunId == run.Id);
        if (dlg == null) { SmokeFail("no formula dialog opened for the queued run"); return; }
        var formula = pack.ById<FormulaDef>(run.FormulaId);
        var movedVar = formula?.Bindings.FirstOrDefault(b => b.Kind == BindingKind.Moved)?.Variable;
        if (movedVar == null) { SmokeFail("the move formula has no Moved variable"); dlg.Close(); return; }
        dlg.Update();
        if (!dlg.IsChecked || dlg.SmokeBoxText(movedVar) != "") { SmokeFail($"dialog not checked or Moved box not empty ('{dlg.SmokeBoxText(movedVar)}')"); dlg.Close(); return; }

        dlg.SmokeType(movedVar, "999");
        var wrongOk = dlg.SmokeBoxWrong(movedVar) && !dlg.ApplyEnabled;
        Program.AppLog($"smoke: wrong value disables Apply {(wrongOk ? "ok" : "FAILED")} (wrong {dlg.SmokeBoxWrong(movedVar)}, apply {dlg.ApplyEnabled})");
        if (!wrongOk) Program.ExitCode = 1;
        var shotBase = _opts.ScreenshotCheatSheet ?? _opts.ScreenshotPhases;
        if (shotBase != null) dlg.SaveScreenshot(Path.ChangeExtension(shotBase, null) + "-formula-wrong.png");

        dlg.SmokeCalculate();
        var unit = run.Units[0];
        var calcOk = dlg.ApplyEnabled && !dlg.SmokeBoxWrong(movedVar)
            && double.TryParse(dlg.SmokeBoxText(movedVar), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var moved) && moved == unit.Moved;
        Program.AppLog($"smoke: Calculate enables Apply {(calcOk ? "ok" : "FAILED")} (Moved box '{dlg.SmokeBoxText(movedVar)}', run distance {unit.Moved})");
        if (!calcOk) Program.ExitCode = 1;
        if (shotBase != null) dlg.SaveScreenshot(Path.ChangeExtension(shotBase, null) + "-formula.png");

        if (mode == Difficulty.SemiAuto)
        {
            var cheat = dlg.CheatSheet;
            var cheatOk = cheat is { IsDisposed: false, Visible: true } && cheat.RowTexts.Count > 0;
            Program.AppLog($"smoke: cheat sheet {(cheatOk ? "ok" : "FAILED")} ({cheat?.RowTexts.Count ?? 0} row(s), {cheat?.GoldCells ?? 0} gold cell(s))");
            if (!cheatOk) Program.ExitCode = 1;
            if (_opts.ScreenshotCheatSheet != null) cheat?.SaveScreenshot(_opts.ScreenshotCheatSheet);
            // The fixture formula reads its table through lookup() in the text, so no row carries a table cell; draw one by hand to check the table grid.
            if (pack.Tables.FirstOrDefault() is { } table && table.RowKeys.Count > 0 && table.ColumnKeys.Count > 0)
            {
                using var demo = new CheatSheetWindow { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
                demo.Show();
                demo.Render(new[] { new CheatRow("Smoke", "table " + table.Name, table.Cells[0][0], table, 0, 0) });
                demo.Update();
                var goldOk = demo.GoldCells == 1;
                Program.AppLog($"smoke: cheat sheet table grid {(goldOk ? "ok" : "FAILED")} ({demo.GoldCells} gold cell(s))");
                if (!goldOk) Program.ExitCode = 1;
                if (_opts.ScreenshotCheatSheet != null) demo.SaveScreenshot(Path.ChangeExtension(_opts.ScreenshotCheatSheet, null) + "-table.png");
                demo.Hide();
            }
        }
        else if (dlg.CheatSheet != null) SmokeFail("hardcore must not show the cheat sheet");

        var logMark = game.State.Log.Count;
        dlg.SmokeClickApply();
        Application.DoEvents();
        var added = game.State.Log.Skip(logMark).ToList();
        var applyOk = (dlg.IsDisposed || !dlg.Visible) && !game.State.Pending.Any(r => r.Id == run.Id)
            && added.Any(e => e.Kind == LogKind.Calc);
        Program.AppLog($"smoke: Apply {(applyOk ? "ok" : "FAILED")}: {added.Count} new log line(s) [{string.Join(", ", added.Select(e => e.Kind).Distinct())}], run still pending {game.State.Pending.Any(r => r.Id == run.Id)}");
        if (!applyOk) Program.ExitCode = 1;
        CloseFormulaDialogs();
    }

    /// <summary>Smoke: the missing-input dialog builds for a run (fixture runs never lack input, so this only checks construction).</summary>
    void SmokeMissingInputDialog()
    {
        if (_session?.Game is not { } game || _phases == null) return;
        var piece = game.State.Pieces.Values.FirstOrDefault();
        var formula = _session.Rules?.Formulas.FirstOrDefault();
        if (piece == null || formula == null) return;
        var run = new PendingRun("smoke", formula.Id, formula.Name, "move", new[] { new PendingUnit(piece.Id, PieceInfo.Name(piece), null, false) }, true);
        using var dlg = new MissingInputDialog(_session, _phases, run);
        Program.AppLog($"smoke: missing input dialog built ({dlg.AskedVariables.Count} input(s), {dlg.Controls.Count} control(s))");
    }

    /// <summary>Smoke: renders the setup chooser with its Difficulty group (no ShowDialog) to a PNG.</summary>
    void SmokeSetupDialog()
    {
        if (_session == null) return;
        using var dlg = new SetupChooserDialog(_session.Model, _settings.Current.LastDifficulty, preselectNew: true, pack: _session.Rules);
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(-20000, -20000);
        dlg.Show();
        Application.DoEvents();
        var path = _opts.ScreenshotSetup!;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        void Shot(string to)
        {
            using var bmp = new Bitmap(dlg.Width, dlg.Height);
            dlg.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
            bmp.Save(to, System.Drawing.Imaging.ImageFormat.Png);
        }
        Shot(path);
        Program.AppLog("smoke: setup chooser screenshot saved to " + path);
        // The Players page: one player holding every side by default; two players splitting the sides must validate.
        dlg.ShowPlayersPage();
        var ed = dlg.PlayersForSmoke;
        var defaultOk = ed.RowCount == 1 && ed.Result.Count == 0 && ed.Problem == null;
        ed.SmokeTwoPlayers();
        Application.DoEvents();
        var twoOk = !ed.CanHaveSeveral || (ed.Result.Count == 2 && ed.Problem == null);
        Program.AppLog($"smoke: setup Players page {(defaultOk && twoOk ? "ok" : "FAILED")} (default solo {defaultOk}, two players valid {twoOk})");
        if (!defaultOk || !twoOk) Program.ExitCode = 1;
        Shot(Path.ChangeExtension(path, null) + "-players.png");
        dlg.Hide();
    }

    /// <summary>Smoke: renders the Next Phase dialog with three invented decisions (no ShowDialog) to a PNG.</summary>
    void SmokeNextPhaseDialog()
    {
        var none = Array.Empty<string>();
        using var dlg = new NextPhaseDialog(new[]
        {
            new Decision(DecisionKind.Checklist, "Smoke checklist item", 0, null, none),
            new Decision(DecisionKind.Pending, "Smoke formula — 2 of 3 applied", -1, "run1", none),
            new Decision(DecisionKind.Warning, "Smoke warning — 2 units", 0, null, none),
        });
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(-20000, -20000); // shown off-screen (modeless) so every child paints; never ShowDialog in smoke
        dlg.Show();
        Application.DoEvents();
        using var bmp = new Bitmap(dlg.Width, dlg.Height);
        dlg.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
        dlg.Hide();
        var path = _opts.ScreenshotNextPhase!;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: next phase dialog screenshot saved to " + path);
    }

    int CountAutosaves()
    {
        try
        {
            var dir = Path.Combine(Autosaver.AutosaveRoot(LayoutRoot), _session?.Archive.Sha256 ?? "");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.qsave").Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
