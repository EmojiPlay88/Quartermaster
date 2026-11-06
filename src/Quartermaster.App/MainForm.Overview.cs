using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>Line-of-sight command wiring, the overview window and their smoke checks.</summary>
sealed partial class MainForm
{
    MapWindow? _lastMap;

    /// <summary>The map window that was active most recently (docked or floating); null once it closes.</summary>
    public MapWindow? LastActiveMap => _lastMap is { IsDisposed: false } m ? m : null;

    /// <summary>Raised when the most recently active map window changes (the overview follows it).</summary>
    public event Action? ActiveMapChanged;

    /// <summary>Called from MDI activation and from a map window's own Activation (floating windows never raise MdiChildActivate).</summary>
    public void NoteActiveMap(MapWindow? m)
    {
        if (m == null || m.IsDisposed || ReferenceEquals(m, _lastMap)) return;
        _lastMap = m;
        ActiveMapChanged?.Invoke();
    }

    void InitLos()
    {
        SetCommandHandler("tool.los", () =>
        {
            var map = (ActiveForm as MapWindow) ?? (ActiveMdiChild as MapWindow);
            if (map == null || map.IsDisposed) return false;
            map.ToggleLos();
            return true;
        });
        SetCommandHandler("view.hideCounters", () =>
        {
            var map = (ActiveForm as MapWindow) ?? (ActiveMdiChild as MapWindow) ?? LastActiveMap;
            if (map == null || map.IsDisposed) return false;
            map.ToggleHideCounters();
            return true;
        });
    }

    void ShowOverview()
    {
        if (_session == null) return;
        NoteActiveMap(ActiveMdiChild as MapWindow ?? FirstMap());
        if (_docs.TryGetValue("overview", out var existing) && !existing.IsDisposed) { existing.Activate(); return; }
        var host = this;
        // A mini-map is small and wants to sit beside the map, so it opens floating unless a layout was remembered.
        var w = new OverviewWindow(host);
        _docs["overview"] = w;
        w.FormClosed += (_, _) => { if (_docs.TryGetValue("overview", out var cur) && cur == w) _docs.Remove("overview"); };
        _wm.Register(w, "doc:overview");
        var saved = _wm.Saved("doc:overview");
        if (saved is { Floating: false }) { PrepareDock(w); w.Show(); return; }
        var b = saved is { Bounds.IsEmpty: false } sv ? sv.Bounds
            : new Rectangle(Left + Math.Max(0, Width - 300), Top + 110, 260, 240);
        _wm.ShowFloating(w, new SavedWindow(true, b, false, saved?.Dpi ?? 0));
    }

    /// <summary>Smoke: HexDistance on the first board's grid between two snapped points two cells apart must be 2.</summary>
    void SmokeDistance()
    {
        var board = FirstMap()?.Map.Boards.FirstOrDefault(b => b.Grid != null);
        if (board?.Grid == null) { Program.AppLog("smoke: line-of-sight distance skipped (no grid on the main map)"); return; }
        var g = board.Grid;
        var grid = g is ZonedGrid z ? z.GridFor(new PointD(board.Width / 2.0, board.Height / 2.0), new PointD(board.Width / 2.0, board.Height / 2.0)) : g;
        if (grid is not (SquareGrid or HexGrid)) { Program.AppLog("smoke: line-of-sight distance skipped (grid is not square or hex)"); return; }
        var a = grid.Snap(new PointD(board.Width / 2.0, board.Height / 2.0));
        // Walk right one pixel at a time: the first point two cells away must be preceded by points one cell away.
        PointD? b2 = null;
        var sawOne = false;
        for (var dx = 1; dx < 4000 && b2 == null; dx++)
        {
            var p = new PointD(a.X + dx, a.Y);
            var d = HexDistance.Cells(grid, a, p);
            if (d == 1) sawOne = true;
            else if (d == 2) b2 = grid.Snap(p);
            else if (d is null or > 2) break;
        }
        var n = b2 is { } q ? HexDistance.Cells(grid, a, q) : null;
        var ok = n == 2 && sawOne;
        Program.AppLog($"smoke: line-of-sight distance {(ok ? "ok" : "FAILED")} ({n?.ToString() ?? "none"} cells between {a} and {b2}, expected 2)");
        if (!ok) { Program.ExitCode = 1; return; }
        if (FirstMap() is { } map && b2!.Value.X < board.Width && b2.Value.Y < board.Height)
        {
            var text = map.Canvas.SmokeLine(new PointD(board.Offset.X + a.X, board.Offset.Y + a.Y), new PointD(board.Offset.X + b2!.Value.X, board.Offset.Y + b2.Value.Y));
            var good = text != null && (text.EndsWith(": 2 hexes", StringComparison.Ordinal) || text.EndsWith(": 2 squares", StringComparison.Ordinal));
            Program.AppLog($"smoke: line-of-sight status {(good ? "ok" : "FAILED")}: {text}");
            map.Canvas.LosEnabled = false;
            if (!good) Program.ExitCode = 1;
        }
    }

    /// <summary>Smoke: opens the overview, checks it follows the main map and pans it, and saves a screenshot when asked.</summary>
    void SmokeOverview()
    {
        Guard(SmokeDistance);
        if (_opts.ScreenshotOverview == null) return;
        ShowOverview();
        if (_docs.TryGetValue("overview", out var f) && f is OverviewWindow ov && !ov.IsDisposed && FirstMap() is { } map)
        {
            ov.Update();
            var follows = ReferenceEquals(ov.Target, map);
            var moved = ov.SmokeClick(0.25, 0.25);
            Program.AppLog($"smoke: overview follows main map={follows} click panned map={moved}");
            if (!follows || !moved) Program.ExitCode = 1;
            ov.SaveScreenshot(_opts.ScreenshotOverview);
            ov.Close();
        }
        else
        {
            Program.AppLog("smoke: --screenshot-overview given but the overview did not open.");
            Program.ExitCode = 1;
        }
    }
}
