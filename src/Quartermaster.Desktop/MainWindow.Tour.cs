using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop;

/// <summary>The interface tour (WinForms MainForm.Tour): Help → Interface Tour, and the one-time automatic start when a game is first opened
/// from the launcher loop.</summary>
public partial class MainWindow
{
    static readonly TimeSpan AutoTourDelay = TimeSpan.FromMilliseconds(500);
    TourOverlay? _tour;

    /// <summary>The open tour balloon (null when none is open).</summary>
    internal TourOverlay? Tour => _tour;

    /// <summary>A tour target id's rectangle in this window's coordinates (DIPs), or null when that control is not on screen.
    /// A panel floating in its own window is still found (its rectangle is then outside this window).</summary>
    internal Rect? ResolveTourTarget(string id)
    {
        Rect? Of(Control? c)
        {
            if (c is not { IsEffectivelyVisible: true, Bounds: { Width: > 0, Height: > 0 } } || TopLevel.GetTopLevel(c) is not { } top) return null;
            if (ReferenceEquals(top, this)) return c.TranslatePoint(default, this) is { } p ? new Rect(p, c.Bounds.Size) : null;
            if (top is not Window { IsVisible: true } other || c.TranslatePoint(default, other) is not { } q) return null;
            return new Rect(this.PointToClient(other.PointToScreen(q)), c.Bounds.Size);
        }
        Rect? Menu(string caption) =>
            MainMenu.IsEffectivelyVisible ? Of(MainMenu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header is string h && MenuText.ToWinForms(h) == caption)) : null;

        if (_session?.Game == null) return null;
        return id switch
        {
            "ModuleTree" => Of(ModuleTreeHost),
            "MapWindow" => Of(Maps.ActiveView),
            "PhaseStrip" => Of(StripHost),
            "PhasePanel" => Of(_phasePanel),
            "AlertBar" => Of(AlertBarHost),
            "OutlinerPanel" => Of(_outlinerPanel),
            "LedgerMenu" => _session.Rules != null ? Menu("&View") : null,
            "EditMenu" => Menu("&Edit"),
            _ => null,
        };
    }

    /// <summary>Starts the tour on the first visible step. Does nothing while a tour is already open or no game is open.</summary>
    internal void ShowTour()
    {
        if (_tour != null || _session?.Game == null || HandoverShowing) return;   // never over the handover screen (which closes the tour)
        var steps = TourOverlay.VisibleSteps(ResolveTourTarget);
        if (steps.Count == 0) return;
        var tour = new TourOverlay(steps, ResolveTourTarget, _ => MarkTourShown());
        tour.Closed += (_, _) => { if (ReferenceEquals(_tour, tour)) _tour = null; };
        _tour = tour;
        tour.Show(this);
        tour.Go(0);
    }

    void MarkTourShown()
    {
        if (!SettingsStore.Current.TourShown) SettingsStore.Update(s => s with { TourShown = true });
    }

    /// <summary>The first game ever opened (setting not yet true) in a window the launcher loop started tours once the window has settled.</summary>
    void MaybeAutoTour()
    {
        if (!AutoTour || SettingsStore.Current.TourShown || _session?.Game == null) return;
        // After the alerts' 250 ms debounce, so the alert bar is on screen when the steps are chosen.
        DispatcherTimer.RunOnce(() => Guard(() => { if (!_closed && !SettingsStore.Current.TourShown) ShowTour(); }), AutoTourDelay);
    }
}
