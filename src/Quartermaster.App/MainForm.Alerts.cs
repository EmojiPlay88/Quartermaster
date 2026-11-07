using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// The alert bar wiring. <see cref="AlertsUpdated"/> is the shared hub: after every game change, rewind, rules change or game replacement
/// (debounced 250 ms, on the UI thread, never on the drag path since a drag only raises Changed on drop) the alerts are evaluated once and
/// published here. The outliner subscribes (MainForm.Outliner.cs) and refreshes from it instead of running its own evaluation, so there is
/// one alert evaluation per change; <see cref="AlertHits"/> holds the latest result.
/// </summary>
sealed partial class MainForm
{
    const int AlertDebounceMs = 250;

    readonly AlertBar _alertBar = new();
    PhaseWatcher? _alertWatch;
    readonly Dictionary<string, int> _alertCycle = new();
    IReadOnlyList<AlertHit> _alertHits = Array.Empty<AlertHit>();

    /// <summary>Raised on the UI thread each time the alerts were re-evaluated (also with an empty list).</summary>
    public event Action<IReadOnlyList<AlertHit>>? AlertsUpdated;

    /// <summary>The latest evaluated alerts (Critical first).</summary>
    public IReadOnlyList<AlertHit> AlertHits => _alertHits;

    void InitAlertBar()
    {
        _alertBar.BannerClicked += () => Guard(() => _phaseDock?.Reveal());
        _alertBar.ChipClicked += h => Guard(() => GoToAlert(h));
        _alertBar.OverflowClicked += (hs, at) => Guard(() => ShowOverflowMenu(hs, at));
        _alertBar.ChipContext += (h, at) => Guard(() => ShowAlertMenu(h, at));
    }

    void CreateAlerts(ModuleSession session)
    {
        _alertWatch = new PhaseWatcher(this, session, RecomputeAlerts, AlertDebounceMs);
        RecomputeAlerts();
    }

    void DestroyAlerts()
    {
        _alertWatch?.Dispose();
        _alertWatch = null;
        _alertCycle.Clear();
        _alertHits = Array.Empty<AlertHit>();
        _alertBar.SetHits(_alertHits);
        _alertBar.Visible = false;
        AlertsUpdated?.Invoke(_alertHits);
    }

    /// <summary>Shown whenever a game is open (Settings permitting), also without a rules pack: the banner then says it is free play.</summary>
    void UpdateAlertBarVisible()
    {
        _alertBar.Visible = _settings.Current.AlertBarVisible && _session?.Game != null && _phases != null;
    }

    void RecomputeAlerts()
    {
        if (_session?.Game is not { } game || _phases == null)
        {
            _alertHits = Array.Empty<AlertHit>();
            AlertsUpdated?.Invoke(_alertHits);
            return;
        }
        var pack = _session.Rules;
        var view = _phases.View();
        var acting = pack != null && view.Current != null ? Sequencer.ActingSide(pack, view.Current)?.Id : null;
        // Alerts are computed from what this player may see (the game itself in solo).
        _alertHits = AlertEngine.Evaluate(_session.View ?? game.State, pack, _session.Model, view.Current, acting);
        _alertBar.VisibleOnly = _session.ViewFiltered;
        var (text, tip) = Banner.For(view);
        _alertBar.SetBanner(text, tip);
        _alertBar.SetHits(_alertHits);
        UpdateAlertBarVisible();
        AlertsUpdated?.Invoke(_alertHits);
    }

    /// <summary>Chip click: cycles through the matches that can be shown (pieces centre on the map, holders select in the Ledger window).</summary>
    void GoToAlert(AlertHit h)
    {
        var targets = h.Matches.Where(m => m.PieceId != null || m.HolderId != null).ToList();
        if (targets.Count == 0) return;
        var next = _alertCycle.TryGetValue(h.Id, out var at) ? (at + 1) % targets.Count : 0;
        _alertCycle[h.Id] = next;
        GoToMatch(targets[next]);
    }

    void GoToMatch(AlertMatch m)
    {
        if (m.PieceId != null) CentreOnPiece(m.PieceId);
        else if (m.HolderId != null && _session != null)
        {
            ShowLedger();
            if (_ledgers.TryGetValue("ledger", out var f) && !f.IsDisposed && f is LedgerWindow lw) lw.SelectHolder(m.HolderId);
        }
    }

    /// <summary>Lists every match (the menu scrolls when taller than the screen) and the Hide item.</summary>
    void ShowAlertMenu(AlertHit h, Point at)
    {
        var menu = NewAlertMenu();
        foreach (var m in h.Matches)
        {
            var mm = m;
            var item = new ToolStripMenuItem((m.Text ?? "").Replace("&", "&&")) { Enabled = m.PieceId != null || m.HolderId != null };
            item.Click += (_, _) => Guard(() => GoToMatch(mm));
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(HideItem(h));
        menu.Show(_alertBar, new Point(at.X, _alertBar.Height));
    }

    /// <summary>The "+N" chip: one entry per alert that did not fit, each with its own go-to and Hide items.</summary>
    void ShowOverflowMenu(IReadOnlyList<AlertHit> hits, Point at)
    {
        var menu = NewAlertMenu();
        foreach (var h in hits)
        {
            var hh = h;
            var sub = new ToolStripMenuItem((h.Matches.Count > 1 ? $"{h.Name} ({h.Matches.Count})" : h.Name ?? "").Replace("&", "&&"));
            var go = new ToolStripMenuItem("Go to next match") { Enabled = h.Matches.Any(m => m.PieceId != null || m.HolderId != null) };
            go.Click += (_, _) => Guard(() => GoToAlert(hh));
            sub.DropDownItems.Add(go);
            sub.DropDownItems.Add(HideItem(h));
            menu.Items.Add(sub);
        }
        menu.Show(_alertBar, new Point(at.X, _alertBar.Height));
    }

    ContextMenuStrip NewAlertMenu()
    {
        var menu = new ContextMenuStrip { MaximumSize = new Size(0, Math.Max(200, Screen.FromControl(this).WorkingArea.Height * 2 / 3)) };
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        return menu;
    }

    ToolStripMenuItem HideItem(AlertHit h)
    {
        var hide = new ToolStripMenuItem("Hide this alert until next turn");
        hide.Click += (_, _) => Guard(() => HideAlert(h.Id, h.Name));
        return hide;
    }

    void HideAlert(string alertId, string? name)
    {
        if (_session?.Game is not { } game) return;
        var cmd = CommandLog.Prepare(new HideAlert(alertId, Name: string.IsNullOrWhiteSpace(name) ? null : name), game.State, _session.Model);
        game.Execute(cmd);
        _alertWatch?.Flush();
    }

    /// <summary>Smoke (--screenshot-alerts): a piece with a negative ledger value gives a chip; screenshots the main window.</summary>
    void SmokeAlerts()
    {
        if (_phases == null || _session?.Game is not { } game || _session.Rules == null) { SmokeFail("alerts smoke needs a game with a rules pack"); return; }
        CloseFormulaDialogs();
        if (_phases.View().BeforeStart) { _phases.Start(); Application.DoEvents(); }
        var piece = game.State.Pieces.Values.FirstOrDefault(p => p.StackId != null && game.State.Stacks.ContainsKey(p.StackId));
        if (piece == null) { SmokeFail("no piece in a stack"); return; }
        var key = LedgerKey.Piece(piece.Id);
        var prop = _session.Rules.Properties.FirstOrDefault()?.Id ?? "fuel";
        game.Execute(new LedgerDelta(key, prop, Ledger.Get(game.State, key, prop) ?? 0, -3, "manual", null, null, "smoke", null));
        _alertWatch?.Flush();
        Application.DoEvents();
        var ok = _alertHits.Count > 0 && _alertBar.Visible && _alertBar.ChipTexts.Count > 0;
        SaveScreenshot(this, _opts.ScreenshotAlerts!, true);
        Program.AppLog($"smoke: alerts {(ok ? "ok" : "FAILED")} (banner '{_alertBar.BannerText}', chips {string.Join(" | ", _alertBar.ChipTexts)})");
        if (!ok) Program.ExitCode = 1;
    }
}
