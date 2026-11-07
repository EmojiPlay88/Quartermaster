using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>Smoke-mode helpers of the main window (screenshots and self-checks driven by command-line flags).</summary>
sealed partial class MainForm
{
    /// <summary>Smoke: opens the first formula for the first piece it applies to (preview mode), optionally saving a screenshot.</summary>
    void SmokeFormulaDialog()
    {
        if (_session is not { Rules: { } rules, Game: { } game } s) return;
        foreach (var piece in game.State.Pieces.Values)
        {
            var f = PieceFilter.FormulasFor(rules, piece, game.State, s.Model).FirstOrDefault();
            if (f == null) continue;
            using var dlg = new FormulaDialog(s, f.Id, piece.Id, previewOnly: true) { Host = this };
            dlg.Show(this);
            dlg.Update();
            Program.AppLog($"smoke: formula dialog '{dlg.Text}' opened");
            if (_opts.ScreenshotFormula != null) { dlg.SmokeFillInputs(); dlg.SaveScreenshot(_opts.ScreenshotFormula); }
            dlg.Close();
            // Modeless dialog owned by an MDI child map, as the Calculate menu opens it; opening twice must reuse it.
            var owner = (Form?)FirstMap() ?? this;
            var live = FormulaDialog.Show(s, f.Id, piece.Id, owner, this);
            var again = FormulaDialog.Show(s, f.Id, piece.Id, owner, this);
            Program.AppLog($"smoke: modeless formula dialog owner={live?.Owner?.Text} deduped={ReferenceEquals(live, again)}");
            if (live == null || !ReferenceEquals(live, again)) Program.ExitCode = 1;
            var problem = live?.SmokeApply();
            Program.AppLog("smoke: formula apply " + (problem ?? "closed the dialog and recorded exactly the previewed entries"));
            if (problem != null) Program.ExitCode = 1;
            if (live is { IsDisposed: false }) live.Close();
            return;
        }
        if (_opts.ScreenshotFormula != null)
        {
            Program.AppLog("smoke: --screenshot-formula given but no formula applies to any piece.");
            Program.ExitCode = 1;
        }
    }

    /// <summary>Smoke: opens the workbench (Tables tab, first table selected) and saves a screenshot when asked.</summary>
    void SmokeWorkbench()
    {
        if (_opts.ScreenshotWorkbench == null) return;
        if (string.Equals(_opts.WorkbenchTab, "phases", StringComparison.OrdinalIgnoreCase) && _session?.Rules is { Phases.Count: 0 } && _opts.NewRulesName != null)
        {
            // The scratch pack is empty; two neutral phases and a block through PhaseEdit exercise the Phases tab.
            _session.UpdateRules(p =>
            {
                p = PhaseEdit.AddSide(p, "Smoke side").Pack;
                p = PhaseEdit.Add(p, "Smoke phase A", 0).Pack;
                p = PhaseEdit.Add(p, "Smoke phase B", 1).Pack;
                p = PhaseEdit.MakeBlock(p, 0, 1, 2, "Smoke block").Pack;
                var sid = p.Sides[0].Id;
                return PhaseEdit.UpdatePhase(p, p.Phases[0].Id, x => x with
                {
                    SideId = sid, Instructions = "Smoke instructions.", Checklist = new() { "Smoke check" },
                    Warnings = new() { new PhaseWarning { Condition = "1 +", Message = "Smoke warning {unit}" } },
                });
            });
        }
        ShowWorkbench();
        if (_benches.TryGetValue("workbench", out var w) && !w.IsDisposed && w is WorkbenchWindow wb)
        {
            wb.SelectTabForSmoke(_opts.WorkbenchTab);
            Program.AppLog($"smoke: workbench opened; selected table {wb.SelectedTableForSmoke ?? "(none)"}; {wb.IssueCountForSmoke} issue(s)");
            wb.SaveScreenshot(_opts.ScreenshotWorkbench);
        }
        else
        {
            Program.AppLog("smoke: --screenshot-workbench given but the workbench did not open.");
            Program.ExitCode = 1;
        }
    }

    /// <summary>Smoke: the Tables tree must list exactly the pack's tables and formulas.</summary>
    void SmokeTablesTree()
    {
        if (_session?.Rules is not { } pack) return;
        if (_opts.NewRulesName != null && pack.Tables.Count == 0 && pack.Formulas.Count == 0)
        {
            // The scratch pack made by --new-rules is empty; give it one invented table and formula so the Tables and Formulas branches are both exercised.
            _session.UpdateRules(p =>
            {
                p.Tables.Add(new TableDef { Id = "t-smoke", Name = "Smoke Table", Kind = TableKind.OneD, RowKeys = new() { "1+" }, ColumnKeys = new() { "Value" }, Cells = new() { new() { "1" } } });
                p.Formulas.Add(new FormulaDef { Id = "f-smoke", Name = "Smoke Formula", Text = "1 + 1" });
                return p;
            });
            _tablesTree.Bind(_session);
            pack = _session.Rules!;
        }
        var (t, f) = _tablesTree.CountsForSmoke;
        var ok = t == pack.Tables.Count && f == pack.Formulas.Count;
        Program.AppLog($"smoke: tables tree {(ok ? "ok" : "FAILED")} ({t}/{pack.Tables.Count} tables, {f}/{pack.Formulas.Count} formulas); left split {_leftFraction:F2}");
        if (!ok) Program.ExitCode = 1;
    }

    /// <summary>Smoke: opens the ledger window (once rules are loaded) and optionally saves a screenshot of it.</summary>
    void SmokeLedger()
    {
        if (_session?.Rules == null)
        {
            if (_opts.ScreenshotLedger != null) { Program.AppLog("smoke: --screenshot-ledger given but no rules pack is loaded."); Program.ExitCode = 1; }
            return;
        }
        ShowLedger();
        if (_ledgers.TryGetValue("ledger", out var w) && !w.IsDisposed && w is LedgerWindow lw)
        {
            Program.AppLog($"smoke: ledger window opened with {lw.RowCountForSmoke} row(s)");
            if (_opts.ScreenshotLedger != null) lw.SaveScreenshot(_opts.ScreenshotLedger);
        }
        else Program.ExitCode = 1;
    }

    // ---------- smoke: wait for tiling, optional screenshot ----------

    DateTime? _smokeWaitStart;
    readonly DateTime _smokeStartedAt = DateTime.UtcNow;

    bool _smokeExtrasOpened;

    /// <summary>Opens the first chart and first text document so the tab strip shows several tabs (no PDF/WebView2 in smoke).</summary>
    void OpenSmokeDocuments()
    {
        if (_session == null) return;
        if (_session.Model.Charts.FirstOrDefault() is { } c) OnNodeActivated(new WindowTreeNode(c.Name, WindowNodeKind.Chart, c, []));
        if (_session.Model.Documents.FirstOrDefault(d => d.Kind == DocumentKind.Text) is { } d)
            OnNodeActivated(new WindowTreeNode(d.Title, WindowNodeKind.Document, d, []));
        if (_maps.Values.FirstOrDefault(w => !w.IsDisposed) is { } m) m.Activate(); // tab strip shows all tabs, map on top
        Update();
        Program.AppLog($"smoke: strip bounds {_tabs.Bounds} visible {_tabs.Visible}; mdi states {string.Join(',', MdiChildren.Select(m => m.WindowState))}");
        Program.AppLog($"smoke: {_tabs.Tabs.Count} document tab(s): " + string.Join(" | ", _tabs.Tabs.Select(t => t.Text)));
    }

    void FinishSmoke()
    {
        var pending = _maps.Values.Any(w => !w.IsDisposed && !w.TilingDone);
        _smokeWaitStart ??= DateTime.UtcNow;
        if (pending && DateTime.UtcNow - _smokeWaitStart < TimeSpan.FromSeconds(60))
        {
            _smokeTimer!.Interval = 200;
            _smokeTimer.Start();
            return;
        }
        if (!_smokeExtrasOpened) { _smokeExtrasOpened = true; Guard(OpenSmokeDocuments); }
        if (pending) Program.AppLog("smoke: tiling did not finish within 60 s.");
        else Program.AppLog($"smoke: tiling finished {(DateTime.UtcNow - _smokeStartedAt).TotalSeconds:F1} s after window start.");
        Program.AppLog($"smoke: peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MB");
        Guard(SmokeTablesTree);
        Guard(SmokeFormulaDialog);
        Guard(SmokeLedger);
        Guard(SmokeShortcuts);
        Guard(SmokeFocusGate);
        Guard(SmokeFloatShortcut);
        Guard(SmokeWorkbench);
        if (_opts.Screenshot != null) Guard(() => SaveScreenshot(this, _opts.Screenshot, true));
        if (_opts.ScreenshotFloat != null)
        {
            var fl = _wm.Floating.FirstOrDefault(f => !f.IsDisposed);
            if (fl == null) Program.AppLog("smoke: --screenshot-float given but no floating window exists.");
            else Guard(() => SaveScreenshot(fl, _opts.ScreenshotFloat, false));
        }
        if (_opts.FloatMainMap) Guard(SmokeDockRoundTrip);
        if (_opts.ScreenshotLog != null) Guard(SmokeLog);
        if (_opts.ScreenshotNextPhase != null) Guard(SmokeNextPhaseDialog);
        if (_opts.ScreenshotSetup != null) Guard(SmokeSetupDialog);
        if (_opts.SmokePhases || _opts.ScreenshotPhases != null) Guard(SmokePhases);
        if (_opts.SmokeUndo || _opts.ScreenshotStepBack != null) Guard(SmokeUndo);
        if (_opts.ScreenshotBreakdown != null) Guard(SmokeBreakdown);
        if (_opts.ScreenshotOutliner != null) Guard(SmokeOutliner);
        if (_opts.ScreenshotAlerts != null) Guard(SmokeAlerts);
        if (_opts.ScreenshotTour != null) Guard(SmokeTour);
        if (_opts.GameMaster || _opts.ScreenshotPalette != null) Guard(SmokePalette);
        if (_opts.ScreenshotSettings != null) Guard(SmokeSettings);
        Guard(SmokeOverview);
        Guard(SmokeInventoryNotes);
        if (_opts.ScreenshotHandover != null) Guard(SmokeHandover);
        if (_opts.ScreenshotPlayerView != null) Guard(SmokePlayerView);
        if (_opts.ScreenshotExport != null) Guard(SmokeExport);
        if (_opts.SmokeLauncher) BackToLauncher(); else Close();
    }

    /// <summary>Docks the floating window back as a tab and pops it out again, checking the tab strip each time.</summary>
    void SmokeDockRoundTrip()
    {
        foreach (var d in _docs.Values.Where(d => !d.IsDisposed).ToList()) RoundTrip(d); // text document: handle recreation
        if (_wm.Floating.FirstOrDefault(f => !f.IsDisposed) is { } f) RoundTrip(f);
    }

    /// <summary>pop out, dock, pop out again (and re-dock if it started docked), verifying tab strip and parent each time.</summary>
    void RoundTrip(Form f)
    {
        var wasFloating = _wm.IsFloating(f);
        const string suffix = " — Quartermaster";
        bool Floated() => !_tabs.Tabs.Contains(f) && f.MdiParent == null && _wm.IsFloating(f) && f.Text.EndsWith(suffix)
            && f.Text.IndexOf(suffix, StringComparison.Ordinal) == f.Text.Length - suffix.Length; // exactly one suffix
        bool Docked() => _tabs.Tabs.Contains(f) && f.MdiParent == this && !_wm.IsFloating(f);
        if (!wasFloating) _wm.PopOut(f);
        var a = Floated();
        _wm.Dock(f);
        var b = Docked();
        _wm.PopOut(f);
        var c = Floated();
        if (!wasFloating) _wm.Dock(f);
        var ok = a && b && c && (wasFloating ? Floated() : Docked());
        Program.AppLog($"smoke: dock/pop-out round trip {(ok ? "ok" : "FAILED")} ({f.Text}; floated {a}; docked {b}; refloated {c})");
        if (!ok) Program.ExitCode = 1;
    }

    /// <summary>Issues a 2d6 roll and one move (both stamped like real UI commands) so the log is not empty, then saves the panel screenshot.</summary>
    void SmokeLog()
    {
        if (_session?.Game is not { } game || _logDock.Panel == null)
        {
            Program.AppLog("smoke: --screenshot-log given but there is no game or log panel.");
            Program.ExitCode = 1;
            return;
        }
        game.Execute(CommandLog.Stamp(new RollDice("Smoke attack", 2, 6, new[] { 3, 4 })));
        if (_dice != null)
        {
            var rolled = _dice.RollSpec(new DiceSpec(2, 6, 0), "Roll");
            if (_session.Model.Dice.FirstOrDefault(d => d.Faces.Count >= 2) is { } die)
            {
                var before = game.State.Log.Count;
                var ok = _dice.RollSpecial(die) && game.State.Log.Count == before + 1 && game.State.Log[^1].Kind == LogKind.Dice
                    && game.State.Log[^1].Text.StartsWith(die.Name + ": ", StringComparison.Ordinal) && die.Faces.Any(f => game.State.Log[^1].Text.EndsWith(" = " + f, StringComparison.Ordinal));
                Program.AppLog($"smoke: special die '{die.Name}' {(ok ? "ok: " + game.State.Log[^1].Text : "FAILED")}");
                rolled &= ok;
            }
            else Program.AppLog("smoke: no special dice in module");
            _dice.StepTurn(1);
            Program.AppLog($"smoke: dice and turn {(rolled && game.State.Turn.Turn == 2 ? "ok" : "FAILED")}; turn label '{_dice.TurnLabel.Text}'");
            if (!rolled || game.State.Turn.Turn != 2) Program.ExitCode = 1;
        }
        var piece = game.State.Pieces.Values.FirstOrDefault();
        if (piece != null)
            game.Execute(CommandLog.Prepare(new MovePieces(new[] { piece.Id }, piece.MapId, new PointD(piece.Pos.X + 50, piece.Pos.Y - 20), null), game.State, _session));
        else Program.AppLog("smoke: no piece to move for the log screenshot.");
        _logDock.PopOut();
        var floated = _logDock.Float is { } lf && _logDock.Panel!.Parent == lf && lf.Text == "Log — Quartermaster" && _wm.IsFloating(lf);
        _logDock.DockBack();
        var docked = _logDock.Float == null && _logDock.Panel!.Parent == _logDock.Host && _logDock.Host.Visible && !_wm.Floating.Any(x => x is LogFloatForm);
        Program.AppLog($"smoke: log pop-out/dock {(floated && docked ? "ok" : "FAILED")} (floated {floated}; docked {docked})");
        if (!floated || !docked) Program.ExitCode = 1;
        _logDock.Wanted = true;
        _logDock.Apply();
        PerformLayout();
        Update();
        Program.AppLog($"smoke: log has {game.State.Log.Count} entr(y/ies)");
        _logDock.Panel!.SaveScreenshot(_opts.ScreenshotLog!);
    }

    /// <summary>
    /// Smoke (game master mode): lists the first folder with counters, places one at the centre of the first board through the drop code path
    /// (ResolveDrop, FromSlot, Execute) and expects exactly one new "place" log entry; then removes it like the Delete counter menu does.
    /// </summary>
    void SmokePalette()
    {
        var panel = _paletteDock.Panel;
        if (_session?.Game is not { } game || panel == null) { SmokePaletteFail("no game or palette panel"); return; }
        if (!_paletteDock.Host.Visible) { SmokePaletteFail("palette is not shown in game master mode"); return; }
        panel.SelectFirstFolderWithSlots();
        var slot = panel.ListedSlots.FirstOrDefault();
        var map = _session.Model.Maps.FirstOrDefault(m => !m.IsOrgDisplay && m.Boards.Count > 0);
        if (slot == null || map == null) { SmokePaletteFail("no counters in the palette or no map with a board"); return; }
        var b = map.Boards[0];
        var places = game.State.Log.Count(e => e.Kind == LogKind.Place);
        var pieces = game.State.Pieces.Count;
        var (placed, msg) = PaletteDrop.Place(_session, map, slot, new PointD(b.Offset.X + b.Width / 2.0, b.Offset.Y + b.Height / 2.0), _settings.Current.SnapOnDrop);
        var newPlaces = game.State.Log.Count(e => e.Kind == LogKind.Place) - places;
        var ok = placed && newPlaces == 1 && game.State.Pieces.Count == pieces + 1 && game.State.Log[^1].Text.Contains(" placed", StringComparison.Ordinal);
        Program.AppLog($"smoke: palette drop {(ok ? "ok" : "FAILED")} ('{msg}'; {newPlaces} place entr(y/ies); last log '{game.State.Log[^1].Text}')");
        if (!ok) Program.ExitCode = 1;
        var outside = PaletteDrop.Place(_session, map, slot, new PointD(-100000, -100000), true);
        if (outside.Placed || outside.Message != "Drop onto a board" || game.State.Pieces.Count != pieces + 1) { SmokePaletteFail("drop outside boards was not rejected"); }
        if (placed && game.State.Pieces.Keys.FirstOrDefault(k => k.StartsWith("p:", StringComparison.Ordinal)) is { } id)
        {
            game.Execute(CommandLog.Prepare(new RemovePiece(id), game.State, _session));
            if (!game.State.Log[^1].Text.EndsWith(" removed", StringComparison.Ordinal) || game.State.Pieces.ContainsKey(id)) SmokePaletteFail("delete counter did not log a removal");
        }
        if (panel.ListedSlots.Count > 600)
        {
            var n = panel.ScrollToEndAndCountThumbs();
            Program.AppLog($"smoke: palette far-down thumbnails {(n > 0 ? "ok" : "FAILED")} ({n} of last 12 of {panel.ListedSlots.Count})");
            if (n == 0) Program.ExitCode = 1;
        }
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); } // let the thumbnail worker finish
        if (_opts.ScreenshotPalette != null) panel.SaveScreenshot(_opts.ScreenshotPalette);
    }

    static void SmokePaletteFail(string why)
    {
        Program.AppLog("smoke: palette FAILED: " + why);
        Program.ExitCode = 1;
    }

    /// <summary>Main form via DrawToBitmap, with each visible map canvas re-rendered by Skia on top of its screen area.</summary>
    void SaveScreenshot(Form form, string path, bool withTabs)
    {
        form.Update();
        using var shot = new Bitmap(Math.Max(form.Width, 1), Math.Max(form.Height, 1));
        form.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
        using (var g = Graphics.FromImage(shot))
        {
            if (withTabs && _tabs.Visible)
            {
                using var tb = new Bitmap(_tabs.Width, _tabs.Height);
                _tabs.DrawToBitmap(tb, new Rectangle(Point.Empty, tb.Size));
                g.DrawImageUnscaled(tb, _tabs.PointToScreen(Point.Empty).X - form.Bounds.X, _tabs.PointToScreen(Point.Empty).Y - form.Bounds.Y);
            }
            // Only the map that is actually on top (the screenshot target itself, or the active MDI child) is composited, so a map never gets pasted over another active window.
            var active = form as MapWindow ?? ActiveMdiChild as MapWindow;
            if (active != null && !active.IsDisposed && active.Visible && (form == this ? !_wm.IsFloating(active) : active == form))
            {
                using var snap = active.Canvas.Snapshot();
                using var bmp = SkiaSharp.Views.Desktop.Extensions.ToBitmap(snap);
                var origin = new Point(active.Canvas.PointToScreen(Point.Empty).X - form.Bounds.X, active.Canvas.PointToScreen(Point.Empty).Y - form.Bounds.Y);
                g.DrawImageUnscaled(bmp, origin);
            }
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: screenshot saved to " + path);
    }

    /// <summary>Smoke: exports the whole main map at 25% and checks the PNG exists and its long side is within 1..8192.</summary>
    void SmokeExport()
    {
        var path = _opts.ScreenshotExport!;
        var ok = false;
        string detail;
        if (FirstMap() is not { } map || _session?.Game == null) detail = "no map or game";
        else
        {
            var err = MapExport.RunWhole(_session, map.Canvas, 0.25, path, null, CancellationToken.None);
            if (err != null) detail = err;
            else
            {
                using var bmp = SkiaSharp.SKBitmap.Decode(path);
                var side = bmp == null ? 0 : Math.Max(bmp.Width, bmp.Height);
                ok = side is > 0 and <= 8192;
                detail = bmp == null ? "PNG did not decode" : $"{bmp.Width}x{bmp.Height}";
            }
        }
        Program.AppLog($"smoke: map export {(ok ? "ok" : "FAILED")} ({detail}) -> {path}");
        if (!ok) Program.ExitCode = 1;
    }
}
