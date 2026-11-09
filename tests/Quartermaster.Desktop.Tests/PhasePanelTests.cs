using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Phases;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 2b task 4: the tracker strip, the docked phase panel and the debounced refresh. Invented pack from PhaseFixture; headless.</summary>
[Collection("MacFlag")]
public sealed class PhasePanelTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-phasepanel-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public PhasePanelTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the dispatcher loop (RunJobs alone runs no timers) in short slices until the condition holds.</summary>
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

    /// <summary>Lets timers run for this long.</summary>
    static void PumpFor(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        PumpUntil(() => DateTime.UtcNow >= end);
    }

    MainWindow NewWindow()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    /// <summary>A window with the phase fixture module, its pack (optionally changed), a new game from the setup and a fixed roller.</summary>
    MainWindow PhaseGame(Func<RulesPack, RulesPack>? tweak = null, bool withPhases = true)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w = NewWindow();
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        w.StartSetup(0, Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            if (!withPhases) p = p with { Phases = new(), Blocks = new() };
            return tweak == null ? p : tweak(p);
        });
        Pump();
        Flush(w);
        return w;
    }

    static void Flush(MainWindow w) { w.Strip.Flush(); w.Phases.Flush(); }

    static void ToCombat(MainWindow w)
    {
        for (var i = 0; i < 4; i++) w.Actions!.NextPhase();   // Start (Weather), Supply, Movement, Combat
        Pump();
        Flush(w);
    }

    [AvaloniaFact]
    public void No_phases_strip_text()
    {
        var w = PhaseGame(withPhases: false);
        Assert.True(w.Strip.IsVisible);
        Assert.Equal(new[] { PhaseTexts.NoPhasesText }, w.Strip.ItemTexts);
        Assert.False(w.Strip.StartVisible);
        Assert.Equal(PhaseTexts.NoPhasesText, w.Phases.NoPhasesLabelText);
        Assert.False(w.Phases.ContentVisible);
        Assert.False(w.FindMenuItem("&Phase panel")!.IsEnabled);
    }

    [AvaloniaFact]
    public void Start_game_button_starts_turn_1()
    {
        var w = PhaseGame();
        Assert.Equal(new[] { "Before turn 1", "Weather", "Supply", "Movement", "Combat", "End" }, w.Strip.ItemTexts);
        Assert.True(w.Strip.StartVisible);
        Assert.Equal(PhaseTexts.StartGameText, w.Strip.StartText);
        Assert.Equal(PhaseTexts.StartGameText, w.Phases.NextText);
        Assert.True(w.Phases.NextEnabled);

        w.Strip.ClickStart();
        Pump();
        Flush(w);
        Assert.Equal(1, w.Session!.Game!.State.Turn.Turn);
        Assert.NotNull(w.Session.Game.State.Position);
        Assert.Equal("Turn 1", w.Strip.ItemTexts[0]);
        Assert.False(w.Strip.StartVisible);
    }

    [AvaloniaFact]
    public void Strip_chips_follow_phase()
    {
        var w = PhaseGame();
        w.Actions!.NextPhase();
        w.Actions.NextPhase();   // Supply, Red's phase
        Pump();
        Flush(w);
        Assert.Equal(new[] { "Turn 1", "Weather", "Red player — Supply", "Movement", "Combat", "End" }, w.Strip.ItemTexts);
        Assert.Equal(new ChipState?[] { null, ChipState.Done, ChipState.Current, ChipState.Upcoming, ChipState.Upcoming, ChipState.Upcoming }, w.Strip.States);
        Assert.Equal("Red player — Supply", w.Phases.TitleText);
        Assert.Equal("Next Phase ▶ Movement", w.Phases.NextText);
    }

    [AvaloniaFact]
    public void Panel_lists_decisions_and_warnings()
    {
        var w = PhaseGame();
        w.Session!.Game!.Execute(new LedgerDelta("p:r1", "fuel", 40, 0, "manual", null, null, null, null));
        ToCombat(w);
        Assert.Equal("Combat", w.Phases.TitleText);
        Assert.Equal(new[] { "Resolve attacks" }, w.Phases.ChecklistTexts);
        var line = Assert.Single(w.Phases.WarningTexts);
        Assert.Equal("Out of fuel — 1 unit (decision)", line);
        Assert.Equal("2 decisions left", w.Phases.CountText);
        Assert.True(w.Phases.CountRed);
        Assert.Equal(new[] { "Out of fuel — 1 unit" }, w.Phases.OpenDecisionTexts);
    }

    [AvaloniaFact]
    public void Acknowledge_warning()
    {
        var w = PhaseGame();
        w.Session!.Game!.Execute(new LedgerDelta("p:r1", "fuel", 40, 0, "manual", null, null, null, null));
        ToCombat(w);
        Assert.False(w.Phases.AckEnabled);
        w.Phases.SelectWarning(0);
        Assert.True(w.Phases.AckEnabled);
        w.Phases.ClickAcknowledge();
        Pump();
        Flush(w);
        Assert.Equal("Out of fuel — 1 unit (acknowledged)", Assert.Single(w.Phases.WarningTexts));
        Assert.Equal("1 decision left", w.Phases.CountText);
        Assert.Contains("Acknowledged: Out of fuel", w.Session.Game.State.Log.Select(l => l.Text));
    }

    [AvaloniaFact]
    public void Checklist_tick_records()
    {
        var w = PhaseGame();
        ToCombat(w);
        Assert.Equal("1 decision left", w.Phases.CountText);
        w.Phases.SetChecked(0, true);
        Pump();
        Flush(w);
        Assert.Equal(new[] { 0 }, w.Session!.Game!.State.Ticked);
        Assert.Contains("Done: Resolve attacks", w.Session.Game.State.Log.Select(l => l.Text));
        Assert.Equal("0 decisions left", w.Phases.CountText);
        Assert.False(w.Phases.CountRed);
        Assert.True(w.Phases.IsChecked(0));
    }

    static IReadOnlyList<CheckBox> ChecklistBoxes(MainWindow w) =>
        w.Phases.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "ChecklistScroll")
            .GetVisualDescendants().OfType<CheckBox>().ToList();

    [AvaloniaFact]
    public void Checklist_keeps_focus_after_tick()
    {
        var w = PhaseGame();
        ToCombat(w);
        w.UpdateLayout();
        var box = Assert.Single(ChecklistBoxes(w));
        Assert.True(box.Focus());
        w.Phases.SetChecked(0, true);   // as Space on the focused box
        Pump();
        Flush(w);
        w.UpdateLayout();
        Assert.Equal(new[] { 0 }, w.Session!.Game!.State.Ticked);
        Assert.Same(box, Assert.Single(ChecklistBoxes(w)));   // the same box, not a rebuilt one (WinForms CheckedListBox)
        Assert.True(box.IsFocused);   // so Tab moves on from it
        Assert.True(box.IsChecked);
        // A change from elsewhere (an undo) still shows in the kept box.
        w.Actions!.Undo();
        Pump();
        Flush(w);
        Assert.Empty(w.Session!.Game!.State.Ticked);
        Assert.Same(box, Assert.Single(ChecklistBoxes(w)));
        Assert.False(box.IsChecked);
        Assert.Empty(w.Session!.Game!.State.Ticked);   // updating the box in place ticks nothing
    }

    [AvaloniaFact]
    public void Checklist_hidden_in_other_sides_phase()
    {
        var w = PhaseGame(p => p with { Phases = p.Phases.Select(x => x.Id == "ph-combat" ? x with { SideId = "blue" } : x).ToList() });
        var s = w.Session!;
        s.Game!.Execute(new SetPlayers(new[] { new PlayerDef("p1", "Alice", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) }));
        s.SetViewSides(new HashSet<string> { "red" });
        ToCombat(w);
        Assert.Empty(w.Phases.ChecklistTexts);
        Assert.False(w.Phases.NextEnabled);
        Assert.NotNull(w.Phases.NextTip);
        Assert.Equal("", w.Phases.InstructionsText);

        s.SetViewSides(new HashSet<string> { "blue" });
        Pump();
        Flush(w);
        Assert.Equal(new[] { "Resolve attacks" }, w.Phases.ChecklistTexts);
        Assert.Equal("Blue player — Combat", w.Phases.TitleText);
    }

    [AvaloniaFact]
    public void View_phase_panel_toggles()
    {
        var w = PhaseGame();
        var item = w.FindMenuItem("&Phase panel")!;
        Assert.True(item.IsEnabled);
        Assert.True(w.PhasePanelShown);
        Assert.True(item.IsChecked);
        Assert.True(w.Phases.IsEffectivelyVisible);
        Assert.Equal(new[] { "Module &Tree", "Show &Grid", "&Log", "&Phase panel" }, w.MenuLabels()["&View"].Take(4));

        w.ClickMenu("&Phase panel");
        Assert.False(w.PhasePanelShown);
        Assert.False(w.Phases.IsEffectivelyVisible);
        Assert.False(item.IsChecked);
        w.ClickMenu("&Phase panel");
        Assert.True(w.PhasePanelShown);
        Assert.True(w.Phases.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Panel_refresh_is_debounced()
    {
        var w = PhaseGame();
        ToCombat(w);
        var before = w.Phases.ReloadCount;
        for (var i = 0; i < 4; i++) w.Actions!.Tick(0, i % 2 == 0);   // several state changes in a row
        Pump();
        Assert.Equal(before, w.Phases.ReloadCount);   // nothing yet: waiting out the 150 ms
        PumpUntil(() => w.Phases.ReloadCount > before);
        PumpFor(300);
        Assert.Equal(before + 1, w.Phases.ReloadCount);
    }

    [AvaloniaFact]
    public void Debouncer_collapses_requests_and_flushes()
    {
        var runs = 0;
        using var d = new Debouncer(TimeSpan.FromMilliseconds(150), () => runs++);
        d.Request(); d.Request(); d.Request();
        Pump();
        Assert.Equal(0, runs);
        d.Flush();
        Assert.Equal(1, runs);
        d.Request();
        PumpUntil(() => runs == 2);
        PumpFor(300);
        Assert.Equal(2, runs);
        d.Request();
        d.Dispose();
        PumpFor(300);
        Assert.Equal(2, runs);
    }

    [AvaloniaFact]
    public void Debouncer_reports_errors_and_keeps_working()
    {
        var runs = 0;
        var errors = new List<Exception>();
        using var d = new Debouncer(TimeSpan.FromMilliseconds(150), () => { if (++runs == 1) throw new InvalidOperationException("boom"); }, errors.Add);
        d.Flush();
        Assert.Equal(1, runs);
        Assert.Equal("boom", Assert.Single(errors).Message);
        d.Request();
        PumpUntil(() => runs == 2);
        Assert.Single(errors);
    }
}
