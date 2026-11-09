using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 2b task 9: the alert bar and the "what now" line. Invented pack from PhaseFixture; headless.</summary>
[Collection("MacFlag")]
public sealed class AlertBarTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-alertbar-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public AlertBarTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void PumpUntil(Func<bool> done, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            Pump();
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Pump();
    }

    static AlertDef Red(string id = "a-red", string name = "Red units", string? side = null) =>
        new() { Id = id, Name = name, Condition = "startsWith(name(), \"Red\")", Message = "Red is here", Severity = AlertSeverity.Warning, SideId = side };

    MainWindow Game(params AlertDef[] alerts)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        w.StartSetup(0, Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256) with { Alerts = alerts.ToList() });
        Pump();
        w.FlushAlerts();
        w.UpdateLayout();
        return w;
    }

    static void Settle(MainWindow w) { w.FlushAlerts(); Pump(); w.UpdateLayout(); }

    [AvaloniaFact]
    public void Banner_texts_before_start_and_in_phase()
    {
        var w = Game();
        Assert.True(w.Alerts.IsVisible);
        Assert.Equal("Press Start game ▶ to begin turn 1.", w.Alerts.BannerText);
        w.Actions!.NextPhase();   // Start (Weather)
        Settle(w);
        Assert.StartsWith("Now: ", w.Alerts.BannerText);
        Assert.Contains("Weather", w.Alerts.BannerText);
    }

    [AvaloniaFact]
    public void Banner_click_reveals_phase_panel()
    {
        var w = Game();
        w.FindMenuItem("&Phase panel")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));   // hide it
        Pump();
        Assert.False(w.PhasePanelShown);
        w.Alerts.ClickBanner();
        Pump();
        Assert.True(w.PhasePanelShown);
    }

    [AvaloniaFact]
    public void Banner_click_raises_floating_phase_panel()
    {
        var w = Game();
        w.Phases.PopButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));   // float it
        Pump();
        var f = w.PhaseFloat!;
        Assert.NotNull(f);
        w.Activate();   // the main window in front
        Pump();
        var raised = 0;
        f.Activated += (_, _) => raised++;
        w.Alerts.ClickBanner();
        Pump();
        Assert.Same(f, w.PhaseFloat);   // still floating, not docked back
        Assert.True(f.IsVisible);
        Assert.Equal(1, raised);   // brought to the front (headless keeps IsActive on every shown window, so count activations)
        Assert.Same(w.Phases, f.Content);
        Assert.True(w.Phases.IsVisible);
    }

    [AvaloniaFact]
    public void Negative_value_alert_chip_appears()
    {
        var w = Game();
        Assert.Empty(w.Alerts.ChipTexts);
        w.Session!.Game!.Execute(new LedgerDelta("p:r1", "fuel", 40, -3, "manual", null, null, null, null));
        Settle(w);
        var text = Assert.Single(w.Alerts.ChipTexts);
        Assert.Contains("Fuel", text);
        Assert.Equal(AlertEngine.NegativeId("fuel"), Assert.Single(w.AlertHits).Id);
    }

    [AvaloniaFact]
    public void Chip_click_cycles_matches_and_centres()
    {
        var w = Game(Red());
        Assert.Equal(new[] { "Red units (2)" }, w.Alerts.ChipTexts);
        var view = w.Maps.Views.First();
        w.Alerts.ClickChip("a-red");
        var first = Assert.Single(view.SelectedPieceIds);
        w.Alerts.ClickChip("a-red");
        var second = Assert.Single(view.SelectedPieceIds);
        Assert.NotEqual(first, second);
        w.Alerts.ClickChip("a-red");
        Assert.Equal(first, Assert.Single(view.SelectedPieceIds));   // wraps
    }

    [AvaloniaFact]
    public void Chip_menu_lists_matches_go_to_and_hide()
    {
        var w = Game(Red());
        var menu = w.AlertMenuFor("a-red");
        var headers = menu.Items.OfType<MenuItem>().Select(m => ((TextBlock)m.Header!).Text).ToList();
        Assert.Equal(new[] { "Red 1", "Red 2", AlertBarModel.HideUntilNextTurnText }, headers);
    }

    [AvaloniaFact]
    public void Hide_until_next_turn_removes_chip_then_returns()
    {
        var w = Game(Red());
        Assert.Single(w.Alerts.ChipTexts);
        var hide = w.AlertMenuFor("a-red").Items.OfType<MenuItem>().Last();
        hide.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Settle(w);
        Assert.Empty(w.Alerts.ChipTexts);
        for (var i = 0; i < 12 && w.Session!.Game!.State.Turn.Turn < 2; i++) w.Actions!.NextPhase();
        Assert.Equal(2, w.Session!.Game!.State.Turn.Turn);
        Settle(w);
        Assert.Single(w.Alerts.ChipTexts);
    }

    [AvaloniaFact]
    public void Overflow_chip_when_narrow()
    {
        var w = Game(Red("a1", "Alpha alert"), Red("a2", "Bravo alert"), Red("a3", "Charlie alert"), Red("a4", "Delta alert"));
        w.Width = 300;
        Settle(w);
        var texts = w.Alerts.ChipTexts;
        Assert.StartsWith("+", texts[^1]);
        Assert.True(texts.Count < 5);
        var hidden = w.Alerts.OverflowChips;
        Assert.Equal(int.Parse(texts[^1][1..]), hidden.Count);
        var menu = w.OverflowMenuFor(hidden);
        Assert.Equal(hidden.Count, menu.Items.Count);
        var sub = (MenuItem)menu.Items[0]!;
        Assert.Equal(new[] { AlertBarModel.GoToNextMatchText, AlertBarModel.HideUntilNextTurnText }, sub.Items.OfType<MenuItem>().Select(m => ((TextBlock)m.Header!).Text));
        w.Width = 1024;
        Settle(w);
        Assert.Equal(4, w.Alerts.ChipTexts.Count);
        Assert.DoesNotContain(w.Alerts.ChipTexts, t => t.StartsWith('+'));
    }

    [AvaloniaFact]
    public void Alerts_for_acting_side_only()
    {
        var w = Game(Red(side: "red"));
        Assert.Empty(w.Alerts.ChipTexts);   // before the start no side acts
        w.Actions!.NextPhase();
        w.Actions.NextPhase();   // Supply, Red's phase
        Settle(w);
        Assert.Single(w.Alerts.ChipTexts);
        w.Actions.NextPhase();
        w.Actions.NextPhase();   // Combat: no acting side
        Settle(w);
        Assert.Contains(w.Strip.ItemTexts, t => t == "Combat");
        Assert.Empty(w.Alerts.ChipTexts);
    }

    [AvaloniaFact]
    public void Recompute_is_debounced()
    {
        var w = Game(Red());
        var before = w.AlertRecomputeCount;
        for (var i = 0; i < 5; i++) w.Session!.Game!.Execute(new LedgerDelta("p:r1", "fuel", 40 - i, 39 - i, "manual", null, null, null, null));
        Assert.Equal(before, w.AlertRecomputeCount);
        PumpUntil(() => w.AlertRecomputeCount > before);
        Assert.Equal(before + 1, w.AlertRecomputeCount);
    }
}
