using Avalonia.Controls;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop;

/// <summary>
/// The alert bar wiring (WinForms MainForm.Alerts). <see cref="AlertsUpdated"/> is the shared hub: after every game change, rewind, rules change,
/// viewer change or game replacement (debounced 250 ms, UI thread) the alerts are evaluated once and published here.
/// </summary>
public partial class MainWindow
{
    AlertWatch? _alertWatch;
    AlertBarModel? _alertModel;
    IReadOnlyList<AlertHit> _alertHits = Array.Empty<AlertHit>();
    int _alertRuns;

    /// <summary>Raised on the UI thread each time the alerts were re-evaluated (also with an empty list).</summary>
    public event Action<IReadOnlyList<AlertHit>>? AlertsUpdated;

    /// <summary>The latest evaluated alerts (Critical first).</summary>
    public IReadOnlyList<AlertHit> AlertHits => _alertHits;

    /// <summary>The bar under the phase strip.</summary>
    public AlertBar Alerts => AlertBarHost;

    /// <summary>How many times the alerts were evaluated (tests: the debounce).</summary>
    public int AlertRecomputeCount => _alertRuns;

    void InitAlertUi()
    {
        AlertBarHost.BannerClicked += () => Guard(RevealPhasePanel);
        AlertBarHost.ChipClicked += c => Guard(() => GoToAlert(c.Id));
        AlertBarHost.ChipContext += (c, at) => Guard(() => AlertMenuFor(c.Id).Open(at));
        AlertBarHost.OverflowClicked += (chips, at) => Guard(() => OverflowMenuFor(chips).Open(at));
    }

    void CreateAlerts(ModuleSession s)
    {
        _alertModel = new AlertBarModel(s);
        _alertWatch = new AlertWatch(s, RecomputeAlerts);
        RecomputeAlerts();
    }

    void DestroyAlerts()
    {
        _alertWatch?.Dispose();
        _alertWatch = null;
        _alertModel?.Reset();
        _alertModel = null;
        _alertHits = Array.Empty<AlertHit>();
        AlertBarHost.SetChips(Array.Empty<AlertChip>());
        AlertBarHost.IsVisible = false;
        AlertsUpdated?.Invoke(_alertHits);
    }

    /// <summary>Shown whenever a game is open (Settings permitting), also without a rules pack: the banner then says it is free play.
    /// A notice (a waiting turn) shows even when the alert bar is switched off in Settings.</summary>
    void UpdateAlertBarVisible() =>
        AlertBarHost.IsVisible = _session?.Game != null && _phases != null && (SettingsStore.Current.AlertBarVisible || AlertBarHost.NoticeText != null);

    void RecomputeAlerts()
    {
        _alertRuns++;
        if (_session?.Game == null || _phases == null || _alertModel == null)
        {
            _alertHits = Array.Empty<AlertHit>();
            AlertsUpdated?.Invoke(_alertHits);
            return;
        }
        var pack = _session.Rules;
        var view = _phases.View();
        var acting = pack != null && view.Current != null ? Sequencer.ActingSide(pack, view.Current)?.Id : null;
        // Alerts are computed from what this player may see (the game itself in solo).
        var chips = _alertModel.Recompute(view.Current, acting, new HashSet<string>());
        _alertHits = _alertModel.Hits;
        var (text, tip) = Banner.For(view);
        AlertBarHost.SetBanner(text, tip);
        AlertBarHost.SetChips(chips);
        UpdateAlertBarVisible();
        AlertsUpdated?.Invoke(_alertHits);
    }

    /// <summary>Chip click: cycles through the matches that can be shown (pieces centre on the map, holders select in the Ledger window).</summary>
    void GoToAlert(string alertId)
    {
        if (_alertModel?.NextTarget(alertId) is { } target) GoToMatch(target);
    }

    void GoToMatch(AlertMatch m)
    {
        if (m.PieceId != null) CentreOnPiece(m.PieceId);
        else if (m.HolderId != null && _session != null)
            OpenLedger()?.SelectHolder(m.HolderId);   // not ShowLedger: a depot alert must not jump to the counters selected on the map
    }

    static MenuItem PlainItem(string text, bool enabled = true) => new() { Header = new TextBlock { Text = text }, IsEnabled = enabled };

    /// <summary>The right-click menu of a chip: every match, then Hide (WinForms ShowAlertMenu).</summary>
    public ContextMenu AlertMenuFor(string alertId)
    {
        var menu = new ContextMenu();
        var hit = _alertHits.FirstOrDefault(h => h.Id == alertId);
        if (hit == null) return menu;
        foreach (var m in hit.Matches)
        {
            var mm = m;
            var item = PlainItem(m.Text ?? "", m.PieceId != null || m.HolderId != null);
            item.Click += (_, _) => Guard(() => GoToMatch(mm));
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(HideItem(hit));
        return menu;
    }

    /// <summary>The "+N" chip menu: one entry per alert that did not fit, each with its own go-to and Hide items.</summary>
    public ContextMenu OverflowMenuFor(IReadOnlyList<AlertChip> chips)
    {
        var menu = new ContextMenu();
        foreach (var chip in chips)
        {
            var hit = _alertHits.FirstOrDefault(h => h.Id == chip.Id);
            if (hit == null) continue;
            var sub = PlainItem(chip.Label);
            sub.Header = new TextBlock { Text = chip.Label };
            var go = PlainItem(AlertBarModel.GoToNextMatchText, hit.Matches.Any(m => m.PieceId != null || m.HolderId != null));
            go.Click += (_, _) => Guard(() => GoToAlert(hit.Id));
            sub.Items.Add(go);
            sub.Items.Add(HideItem(hit));
            menu.Items.Add(sub);
        }
        return menu;
    }

    MenuItem HideItem(AlertHit h)
    {
        var hide = PlainItem(AlertBarModel.HideUntilNextTurnText);
        hide.Click += (_, _) => Guard(() => HideAlert(h.Id, h.Name));
        return hide;
    }

    void HideAlert(string alertId, string? name)
    {
        if (_session?.Game == null || _actions == null) return;
        _actions.HideAlert(alertId, name);
        _alertWatch?.Flush();
    }

    /// <summary>Re-evaluates now (tests, right after a change).</summary>
    public void FlushAlerts() => _alertWatch?.Flush();
}
