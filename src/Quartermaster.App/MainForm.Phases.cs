using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>The turn sequencer's UI wiring: tracker strip, phase dock, Next Phase flow, phase-boundary autosave and the phase smoke.</summary>
sealed partial class MainForm
{
    readonly PhaseStrip _strip = new() { Visible = false };
    readonly Panel _phaseHost = new() { Dock = DockStyle.Right, Width = 300, Visible = false };
    readonly Splitter _phaseSplitter = new() { Dock = DockStyle.Right, Width = 4, Visible = false };
    readonly ToolStripMenuItem _viewPhases = new("&Phase panel");
    PhaseDock? _phaseDock;
    PhaseController? _phases;
    PhaseWatcher? _stripWatch;

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
        c.RunsQueued += runs => { if (!IsDisposed) SetStatus($"{runs.Count} calculation(s) waiting in the phase panel"); };
        _phases = c;
        _stripWatch = new PhaseWatcher(_strip, session, RefreshStrip);
        _phaseDock!.Create(new PhasePanel(session, c, () => Guard(NextPhase), CentreOnPiece, OpenPendingRun, OpenDocumentAtPage));
        RefreshStrip();
        UpdatePhaseUi();
    }

    void DestroyPhases()
    {
        _stripWatch?.Dispose();
        _stripWatch = null;
        _phaseDock?.Destroy();
        if (_phases != null) _phases.PhaseBoundary -= OnPhaseBoundary;
        _phases = null;
        _strip.Visible = false;
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
        Controls.SetChildIndex(_strip, Controls.Count - 3);
    }

    /// <summary>Shows the strip while a game and a rules pack exist; shows the dock only when the pack has phases.</summary>
    void UpdatePhaseUi()
    {
        _strip.Visible = _session?.Game != null && _session.Rules != null;
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
        else if (d.PieceIds.FirstOrDefault() is { } id) CentreOnPiece(id);
        if (_phaseDock?.Float is { } f) f.Activate();
    }

    /// <summary>Opens the calculation UI of a pending run. Until the batch window lands this only says so in the status bar.</summary>
    public void OpenPendingRun(string runId) => SetStatus("Pending calculation " + runId[..Math.Min(8, runId.Length)] + ": the batch window is not available yet.");

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

        var steps = 0;
        for (; steps < pack.Phases.Count + 2; steps++)
        {
            var before = _session.Game.State.Position;
            var moved = _handlers.TryGetValue("turn.next", out var h) && h();
            if (!moved || Equals(before, _session.Game.State.Position)) break;
        }
        RefreshPhaseViews();
        var after = _phases.View();
        var advOk = steps > 0 && !after.BeforeStart && _session.Game.State.Log.Any(e => e.Text.Contains(" → ", StringComparison.Ordinal) && e.Text.StartsWith("Turn ", StringComparison.Ordinal));
        Program.AppLog($"smoke: phases advanced {steps} step(s) via turn.next {(advOk ? "ok" : "FAILED")}; now turn {after.Turn}, {string.Join(" | ", _strip.ItemTexts)}");
        if (!advOk) Program.ExitCode = 1;

        using (var dlg = new NextPhaseDialog(new[] { new Decision(DecisionKind.Checklist, "Smoke item", 0, null, Array.Empty<string>()) }))
            Program.AppLog($"smoke: next phase dialog built with {dlg.Controls.Count} control(s)");
        if (_opts.ScreenshotPhases != null)
        {
            SaveScreenshot(this, Path.ChangeExtension(_opts.ScreenshotPhases, null) + "-started.png", true);
            _phaseDock?.Panel?.SaveScreenshot(Path.ChangeExtension(_opts.ScreenshotPhases, null) + "-started-panel.png");
        }
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
