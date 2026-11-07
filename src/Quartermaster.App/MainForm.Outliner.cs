using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>The outliner dock's wiring (View, Outliner) and its smoke (--screenshot-outliner).</summary>
sealed partial class MainForm
{
    readonly ToolStripMenuItem _viewOutliner = new("&Outliner");
    OutlinerDock? _outlinerDock;

    void InitOutliner()
    {
        _outlinerDock = new OutlinerDock(this, _wm, _outlinerHost, _viewOutliner, false, SaveOutlinerVisible, Guard);
        _viewOutliner.Click += (_, _) => Guard(() => _outlinerDock!.Toggle());
    }

    void SaveOutlinerVisible(bool visible)
    {
        if (_settings.Current.OutlinerVisible != visible) _settings.Update(s => s with { OutlinerVisible = visible });
    }

    void CreateOutliner(ModuleSession session)
    {
        var panel = new OutlinerPanel(session, () => _phases, AlertHits, _settings.Current.OutlinerGrouping,
            g => { if (_settings.Current.OutlinerGrouping != g) _settings.Update(s => s with { OutlinerGrouping = g }); },
            CentreOnPiece, map => Guard(() => OpenMap(map, false)), JumpToDecision);
        AlertsUpdated += panel.AlertsChanged; // one alert evaluation per change, shared with the alert bar
        _outlinerDock!.Create(panel);
        _outlinerDock.Wanted = _settings.Current.OutlinerVisible;
    }

    void DestroyOutliner()
    {
        if (_outlinerDock?.Panel is { } panel) AlertsUpdated -= panel.AlertsChanged;
        _outlinerDock?.Destroy();
    }

    void SmokeOutliner()
    {
        if (_phases == null || _session?.Game == null || _session.Rules == null || _outlinerDock?.Panel is not { } panel || _session.Model.Setups.Count == 0)
        { SmokeFail("outliner smoke needs a game with a rules pack and a setup"); return; }
        CloseFormulaDialogs();
        if (_phases.View().BeforeStart) { _phases.Start(); Application.DoEvents(); }
        var fails = new List<string>();
        // The fixture's properties may not feature in the outliner; make the first two do so in the in-memory pack.
        _session.UpdateRules(p =>
        {
            var props = p.Properties.Select((x, i) => i < 2 ? x with { Outliner = OutlinerAggregate.Sum, Notes = x.Notes ?? "Smoke note for " + x.Name } : x).ToList();
            return p with { Properties = props };
        });
        var first = _session.Rules.Properties.FirstOrDefault();

        // Hidden panel: replace the game and change data while it is not showing, then show it. No Flush.
        _outlinerDock.Wanted = false;
        _outlinerDock.Apply();
        Application.DoEvents();
        _session.LoadSetup(_session.Model.Setups[0], _opts.Difficulty);
        Application.DoEvents();
        var game = _session.Game!;
        if (_phases.View().BeforeStart) { _phases.Start(); Application.DoEvents(); }
        double expected = 0;
        if (first != null)
        {
            foreach (var pc in game.State.Pieces.Values.Take(3))
            {
                var key = LedgerKey.Piece(pc.Id);
                var before = Ledger.Get(game.State, key, first.Id) ?? 0;
                game.Execute(new LedgerDelta(key, first.Id, before, before + 5, "manual", null, null, "smoke supply", null));
            }
            expected = game.State.Pieces.Values.Sum(pc => Ledger.Get(game.State, LedgerKey.Piece(pc.Id), first.Id) ?? 0);
        }
        Application.DoEvents();
        _outlinerDock.Wanted = true;
        _outlinerDock.Apply();
        Application.DoEvents();
        var shown = panel.CellText(0, 3);
        var expectedHidden = expected;
        if (panel.VisibleRowCount == 0 || !double.TryParse(shown, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var got) || Math.Abs(got - expected) > 0.01)
            fails.Add($"hidden replace: shown '{shown}', expected {expected}");

        // A user column width survives a refresh driven by a game change (through the shared alert evaluation).
        panel.SetColumnWidth(3, 97);
        if (first != null && game.State.Pieces.Values.FirstOrDefault() is { } wp)
        {
            var key = LedgerKey.Piece(wp.Id);
            var before = Ledger.Get(game.State, key, first.Id) ?? 0;
            game.Execute(new LedgerDelta(key, first.Id, before, before + 1, "manual", null, null, "smoke width", null));
            expected += 1;
        }
        _alertWatch?.Flush();
        Application.DoEvents();
        var afterChange = panel.CellText(0, 3);
        if (panel.ColumnWidth(3) != 97) fails.Add($"column width lost on refresh ({panel.ColumnWidth(3)})");
        if (!double.TryParse(afterChange, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var got2) || Math.Abs(got2 - expected) > 0.01)
            fails.Add($"alert-driven refresh: shown '{afterChange}', expected {expected}");

        panel.ExpandAll();
        Application.DoEvents();
        var rows = panel.VisibleRowCount;
        var cols = panel.ValueColumnCount;
        var breakdowns = 0;
        for (var r = 0; r < rows; r++) for (var c = 0; c < cols; c++) if (panel.BreakdownFor(r, 3 + c) != null) breakdowns++;
        panel.SaveScreenshot(_opts.ScreenshotOutliner!);
        if (!(_outlinerDock.Host.Visible && rows > 0 && cols > 0 && breakdowns > 0)) fails.Add("rows/columns/breakdowns missing");
        if (_phases.View().NoPhases || !panel.NowVisible) fails.Add("Now should show while the pack has phases");

        _outlinerDock.PopOut();
        var floated = _outlinerDock.Float != null && _wm.IsFloating(_outlinerDock.Float);
        _outlinerDock.DockBack();
        var docked = _outlinerDock.Float == null && _outlinerDock.Host.Visible && panel.Parent == _outlinerDock.Host;
        if (!floated || !docked) fails.Add($"pop-out round trip (floated {floated}, docked {docked})");

        // No phases: the Now section disappears.
        _session.UpdateRules(p => p with { Phases = new() });
        Application.DoEvents();
        panel.Flush();
        var hidden = !panel.NowVisible;
        panel.SaveScreenshot(Path.ChangeExtension(_opts.ScreenshotOutliner!, null) + "-nophases.png");
        if (!hidden) fails.Add("Now still visible without phases");

        Program.AppLog($"smoke: outliner screenshot saved to {_opts.ScreenshotOutliner} (rows {rows}, columns {cols}, breakdowns {breakdowns}, hidden-replace shown '{shown}' expected {expectedHidden}, after change '{afterChange}' expected {expected}, width kept {panel.ColumnWidth(3) == 97}, now hidden without phases {hidden})");
        if (fails.Count > 0) { Program.ExitCode = 1; Program.AppLog("smoke: outliner check FAILED: " + string.Join("; ", fails)); }
    }
}
