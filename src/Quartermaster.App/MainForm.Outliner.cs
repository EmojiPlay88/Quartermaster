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
        if (_settings.Current.OutlinerVisible != visible) _settings.Save(_settings.Current with { OutlinerVisible = visible });
    }

    void CreateOutliner(ModuleSession session)
    {
        var panel = new OutlinerPanel(session, () => _phases, _settings.Current.OutlinerGrouping,
            g => { if (_settings.Current.OutlinerGrouping != g) _settings.Save(_settings.Current with { OutlinerGrouping = g }); },
            CentreOnPiece, map => Guard(() => OpenMap(map, false)), JumpToDecision);
        _outlinerDock!.Create(panel);
        _outlinerDock.Wanted = _settings.Current.OutlinerVisible;
    }

    void DestroyOutliner() => _outlinerDock?.Destroy();

    void SmokeOutliner()
    {
        if (_phases == null || _session?.Game is not { } game || _session.Rules == null || _outlinerDock?.Panel is not { } panel)
        { SmokeFail("outliner smoke needs a game with a rules pack"); return; }
        CloseFormulaDialogs();
        if (_phases.View().BeforeStart) { _phases.Start(); Application.DoEvents(); }
        // The fixture's properties may not feature in the outliner; make the first two do so in the in-memory pack.
        _session.UpdateRules(p =>
        {
            var props = p.Properties.Select((x, i) => i < 2 ? x with { Outliner = OutlinerAggregate.Sum, Notes = x.Notes ?? "Smoke note for " + x.Name } : x).ToList();
            return p with { Properties = props };
        });
        var pieces = game.State.Pieces.Values.Take(3).ToList();
        var first = _session.Rules.Properties.FirstOrDefault();
        if (first != null)
            foreach (var pc in pieces)
            {
                var key = LedgerKey.Piece(pc.Id);
                var before = Ledger.Get(game.State, key, first.Id) ?? 0;
                game.Execute(new LedgerDelta(key, first.Id, before, before + 5, "manual", null, null, "smoke supply", null));
            }
        _outlinerDock.Wanted = true;
        _outlinerDock.Apply();
        Application.DoEvents();
        panel.Flush();
        panel.ExpandAll();
        Application.DoEvents();
        var rows = panel.VisibleRowCount;
        var cols = panel.ValueColumnCount;
        var breakdowns = 0;
        for (var r = 0; r < rows; r++) for (var c = 0; c < cols; c++) if (panel.BreakdownFor(r, 3 + c) != null) breakdowns++;
        panel.SaveScreenshot(_opts.ScreenshotOutliner!);
        var ok = _outlinerDock.Host.Visible && rows > 0 && cols > 0 && breakdowns > 0;
        Program.AppLog($"smoke: outliner screenshot saved to {_opts.ScreenshotOutliner} (rows {rows}, columns {cols}, now {panel.NowCount}, breakdowns {breakdowns})");
        if (!ok) { Program.ExitCode = 1; Program.AppLog("smoke: outliner check FAILED"); }
        _outlinerDock.PopOut();
        var floated = _outlinerDock.Float != null && _wm.IsFloating(_outlinerDock.Float);
        _outlinerDock.DockBack();
        var docked = _outlinerDock.Float == null && _outlinerDock.Host.Visible && panel.Parent == _outlinerDock.Host;
        if (!floated || !docked) { Program.ExitCode = 1; Program.AppLog($"smoke: outliner pop-out round trip FAILED (floated {floated}, docked {docked})"); }
    }
}
