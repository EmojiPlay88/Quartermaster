using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Xunit;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 5a task 4: Help menu, the Interface Tour balloon and About. Headless, temp roots, a temp copy of Riverland-Demo.</summary>
public sealed class TourTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-tour-" + Guid.NewGuid().ToString("N"));
    readonly string _module;
    readonly FakeDialogs _dialogs = new();
    readonly List<MainWindow> _windows = new();
    readonly List<Window> _others = new();
    int _layouts;

    public TourTests()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "mods"));
        _module = Path.Combine(_tmp, "mods", "Riverland-Demo.vmod");
        File.Copy(Demo("Riverland-Demo.vmod"), _module);
    }

    public void Dispose()
    {
        foreach (var w in _windows) { try { w.Tour?.Close(); w.ForceClose(); } catch (InvalidOperationException) { } }
        foreach (var o in _others) { try { o.Close(); } catch (InvalidOperationException) { } }
        Dispatcher.UIThread.RunJobs();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void PumpUntil(Func<bool> done, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            Pump();
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);   // RunJobs alone runs no timers
        }
        Pump();
    }

    /// <summary>A shown window over its own temp layout root; <paramref name="tourShown"/> is written to its settings first.</summary>
    MainWindow NewWindow(bool tourShown = false)
    {
        var layout = Path.Combine(_tmp, "layout" + _layouts++);
        Directory.CreateDirectory(layout);
        if (tourShown)
        {
            var store = new SettingsStore(layout);
            store.Load(null);
            store.Update(s => s with { TourShown = true });
        }
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, layout) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    /// <summary>Riverland-Demo with a new game; with the rules pack and the outliner every tour target is on screen.</summary>
    MainWindow Game(bool rules = false, bool outliner = false)
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(_module));
        if (rules) w.Session!.OpenRules(Demo("Riverland-Demo.qrules"));
        w.StartSetup(null);
        PumpUntil(() => w.Alerts.IsVisible);   // the alert bar shows once the (debounced) alerts ran
        if (outliner) w.ClickMenu("&Outliner");
        Pump();
        return w;
    }

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static string Label(Button b) => MenuText.ToWinForms((string)b.Content!);

    [AvaloniaFact]
    public void Help_menu_has_tour_and_about()
    {
        var w = NewWindow();
        var menus = w.MenuLabels();
        Assert.Equal("&Help", menus.Keys.Last());
        Assert.Equal(new[] { "Interface &Tour", "&About" }, menus["&Help"]);
        Assert.False(w.FindMenuItem("Interface &Tour")!.IsEnabled);   // help.tour needs a game
        Assert.True(w.FindMenuItem("&About")!.IsEnabled);
        Assert.True(w.OpenModule(_module));
        w.StartSetup(null);
        Pump();
        Assert.True(w.FindMenuItem("Interface &Tour")!.IsEnabled);
    }

    [AvaloniaFact]
    public void Tour_walks_visible_steps_and_done_marks_shown()
    {
        var w = Game(rules: true, outliner: true);
        foreach (var step in TourSteps.All) Assert.NotNull(w.ResolveTourTarget(step.TargetId));
        Assert.False(w.SettingsStore.Current.TourShown);

        w.ClickMenu("Interface &Tour");
        var tour = Assert.IsType<TourOverlay>(w.Tour);
        Assert.Same(w, tour.Owner);
        Assert.True(tour.IsVisible);
        Assert.Equal(8, tour.Count);
        Assert.Equal(0, tour.Index);
        Assert.Equal("Module tree  (1 of 8)", tour.CurrentTitle);
        Assert.False(tour.BackButton.IsEnabled);
        Assert.Equal("< &Back", Label(tour.BackButton));
        Assert.Equal("&Next >", Label(tour.NextButton));
        Assert.Equal("&Skip tour", Label(tour.SkipButton));
        Assert.Equal(330, tour.BodyWidth);
        // The module tree is tall: the balloon sits right of it, the window is body + arrow wide and as high as title, text and buttons.
        Assert.Equal(TourSide.Right, tour.Side);
        Assert.Equal(330 + 12, tour.Width);
        Assert.InRange(tour.Height, 80, 300);
        using (var frame = tour.CaptureRenderedFrame()!)
        {
            var (fw, _, px) = TestHelpers.Pixels(frame);
            Assert.Equal(new SkiaSharp.SKColor(0xFF, 0xFF, 0xE1), px[4 * fw + 20]);   // tooltip yellow inside the body
            Assert.Equal(new SkiaSharp.SKColor(0, 0, 0), px[4 * fw + 12]);           // its black left border
        }

        for (var i = 1; i < 8; i++)
        {
            Click(tour.NextButton);
            Assert.Equal(i, tour.Index);
            Assert.Equal($"{TourSteps.All[i].Title}  ({i + 1} of 8)", tour.CurrentTitle);
            Assert.True(tour.BackButton.IsEnabled);
        }
        Assert.Equal("&Done", Label(tour.NextButton));
        Click(tour.BackButton);
        Assert.Equal(6, tour.Index);
        Assert.Equal("&Next >", Label(tour.NextButton));
        Click(tour.NextButton);
        Assert.False(w.SettingsStore.Current.TourShown);

        Click(tour.NextButton);   // Done
        Pump();
        Assert.True(tour.IsDone);
        Assert.False(tour.IsVisible);
        Assert.Null(w.Tour);
        Assert.True(w.SettingsStore.Current.TourShown);
    }

    [AvaloniaFact]
    public void Tour_skips_missing_targets()
    {
        // No rules pack: no phase strip, phase panel or ledger step; the outliner is hidden by default.
        var w = Game();
        Assert.Null(w.ResolveTourTarget("LedgerMenu"));
        Assert.Null(w.ResolveTourTarget("PhaseStrip"));
        Assert.Null(w.ResolveTourTarget("OutlinerPanel"));
        Assert.Null(w.ResolveTourTarget("NoSuchTarget"));
        Assert.Equal(new[] { "moduleTree", "map", "alertBar", "undo" }, TourOverlay.VisibleSteps(w.ResolveTourTarget).Select(s => s.Id));
        w.ClickMenu("Interface &Tour");
        var tour = w.Tour!;
        Assert.Equal(4, tour.Count);
        Assert.Equal("Module tree  (1 of 4)", tour.CurrentTitle);
        Click(tour.NextButton);
        Assert.Equal("Map  (2 of 4)", tour.CurrentTitle);
        Click(tour.NextButton);
        Assert.Equal("Alert bar  (3 of 4)", tour.CurrentTitle);
        Click(tour.NextButton);
        Assert.Equal("Undo and Step Back  (4 of 4)", tour.CurrentTitle);

        // A target that goes away while the tour is open is passed over in the direction of travel.
        var gone = new HashSet<string>();
        var host = new Window { Width = 800, Height = 600 };
        _others.Add(host);
        host.Show();
        var finished = new List<bool>();
        var overlay = new TourOverlay(TourSteps.All, id => gone.Contains(id) ? null : new Rect(10, 10, 100, 40), finished.Add);
        _others.Add(overlay);
        overlay.Show(host);
        overlay.Go(0);
        gone.Add("MapWindow");
        overlay.Go(1);
        Assert.Equal(2, overlay.Index);
        overlay.Go(1);   // backwards past the gone step
        Assert.Equal(0, overlay.Index);
        gone.Add("EditMenu");
        overlay.Go(7);   // forwards with nothing left: finished, not skipped
        Assert.True(overlay.IsDone);
        Assert.Equal(new[] { false }, finished);
    }

    [AvaloniaFact]
    public void Skip_and_escape_mark_shown()
    {
        var w = Game();
        w.ClickMenu("Interface &Tour");
        var tour = w.Tour!;
        Click(tour.SkipButton);
        Pump();
        Assert.True(tour.IsDone);
        Assert.Null(w.Tour);
        Assert.True(w.SettingsStore.Current.TourShown);

        w.SettingsStore.Update(s => s with { TourShown = false });
        w.ClickMenu("Interface &Tour");
        tour = w.Tour!;
        Click(tour.NextButton);
        tour.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Pump();
        Assert.True(tour.IsDone);
        Assert.False(tour.IsVisible);
        Assert.Null(w.Tour);
        Assert.True(w.SettingsStore.Current.TourShown);

        // Closed from outside (the owner closing) counts as skipped too.
        w.SettingsStore.Update(s => s with { TourShown = false });
        w.ShowTour();
        w.Tour!.Close();
        Pump();
        Assert.Null(w.Tour);
        Assert.True(w.SettingsStore.Current.TourShown);

        // The main window closing takes the balloon with it.
        w.SettingsStore.Update(s => s with { TourShown = false });
        w.ShowTour();
        tour = w.Tour!;
        w.ForceClose();
        Pump();
        Assert.False(tour.IsVisible);
        Assert.True(tour.IsDone);
        Assert.Null(w.Tour);
        Assert.True(w.SettingsStore.Current.TourShown);
    }

    [AvaloniaFact]
    public void Tour_needs_a_game()
    {
        var w = NewWindow();
        w.ShowTour();
        Assert.Null(w.Tour);
        Assert.True(w.OpenModule(_module));
        Pump();
        w.ShowTour();   // a module, no game yet
        Assert.Null(w.Tour);
        Assert.Null(w.ResolveTourTarget("ModuleTree"));

        w.StartSetup(null);
        Pump();
        w.ShowTour();
        var first = w.Tour!;
        Click(first.NextButton);
        w.ShowTour();   // one already open: nothing happens
        Assert.Same(first, w.Tour);
        Assert.Equal(1, first.Index);
    }

    [AvaloniaFact]
    public async Task Auto_tour_only_when_AutoTour_and_not_shown()
    {
        var request = new LaunchRequest(_module, null, LaunchKind.NewGame, new NewGameRequest(GameMode.Offline, null, Difficulty.SemiAuto, null, null, null), null);

        var plain = NewWindow();   // tests and screenshot runs: never
        await plain.StartFromLaunchAsync(request);
        PumpUntil(() => plain.Session?.Game != null);
        Pump();
        Assert.Null(plain.Tour);

        var seen = NewWindow(tourShown: true);
        seen.AutoTour = true;
        await seen.StartFromLaunchAsync(request);
        Pump();
        Assert.Null(seen.Tour);

        var first = NewWindow();
        first.AutoTour = true;
        await first.StartFromLaunchAsync(request);
        PumpUntil(() => first.Tour != null);
        Assert.Equal(0, first.Tour!.Index);
        Assert.Equal(4, first.Tour.Count);   // started after the alerts ran: the alert bar step is in
        Click(first.Tour.SkipButton);
        Pump();
        Assert.True(first.SettingsStore.Current.TourShown);

        // The direct start (a module on the command line) also tours once.
        var direct = NewWindow();
        direct.AutoTour = true;
        direct.ApplyStartup(new StartupArgs(_module, null, true));
        PumpUntil(() => direct.Tour != null);
        direct.Tour!.Close();
        Pump();
        Assert.True(direct.SettingsStore.Current.TourShown);
    }

    [AvaloniaFact]
    public void Balloon_prefers_below_for_wide_target_and_right_for_tall()
    {
        var screen = new Rect(0, 0, 1920, 1080);
        var balloon = new Size(330, 120);

        // Wide (width >= 2 x height): below, the arrow tip on the target's bottom edge.
        var (at, side) = TourOverlay.Place(new Rect(100, 100, 400, 30), balloon, screen);
        Assert.Equal(TourSide.Below, side);
        Assert.Equal(new Point(100 + 160 - 330 / 4.0, 130), at);   // tip x = left + min(w/2, 160); body a quarter to its left; form starts at the arrow

        // Tall: right of it.
        (at, side) = TourOverlay.Place(new Rect(100, 100, 200, 600), balloon, screen);
        Assert.Equal(TourSide.Right, side);
        Assert.Equal(new Point(300, 100 + 120 - 120 / 4.0), at);

        // No room: wide at the bottom goes above, tall at the right edge goes left.
        (at, side) = TourOverlay.Place(new Rect(100, 1000, 400, 30), balloon, screen);
        Assert.Equal(TourSide.Above, side);
        Assert.Equal(1000 - 12 - 120, at.Y);
        (at, side) = TourOverlay.Place(new Rect(1700, 100, 200, 600), balloon, screen);
        Assert.Equal(TourSide.Left, side);
        Assert.Equal(1700 - 12 - 330, at.X);

        // Nowhere fits: the first preference, clamped into the screen.
        (at, side) = TourOverlay.Place(new Rect(0, 0, 1920, 950), balloon, screen);
        Assert.Equal(TourSide.Below, side);
        Assert.True(at.X >= 0 && at.Y >= 0 && at.X + 330 <= 1920 && at.Y <= 1080 - 120);
    }

    [AvaloniaFact]
    public void About_shows_version_text()
    {
        var w = NewWindow();
        w.ClickMenu("&About");
        Pump();
        Assert.Equal(($"Quartermaster {AppVersion.Text}\nA VASSAL module reader and engine.", "About Quartermaster"), Assert.Single(_dialogs.Infos));
    }
}
