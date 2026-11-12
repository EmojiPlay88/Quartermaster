using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 5b task 3: the hot-seat handover screen and its wiring in the main window. Invented PhaseFixture pack (Combat given
/// to Blue), players Anna (Red) and Ben (Blue); headless, fake dialogs, temp roots.</summary>
public sealed class HandoverTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-handover-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    static readonly PlayerDef Anna = new("p1", "Anna", new[] { "red" });
    static readonly PlayerDef Ben = new("p2", "Ben", new[] { "blue" });

    public void Dispose()
    {
        foreach (var w in _windows)
        {
            if (w.Session is { } s) FormulaWindows.CloseAllFor(s);
            w.ForceClose();
        }
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void PumpUntil(Func<bool> done, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            Pump();
            if (done()) return;
            Assert.True(DateTime.UtcNow < until, "timed out");
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
    }

    FixtureModule Module()
    {
        var fx = PhaseFixture.Module()
            .EditBuildFile(x => x.Replace("</VASSAL.build.GameModule>",
                "<VASSAL.build.module.ChartWindow name=\"Charts\"><VASSAL.build.widget.Chart chartName=\"Odds Table\" fileName=\"odds.png\"/>" +
                "</VASSAL.build.module.ChartWindow></VASSAL.build.GameModule>"))
            .AddImage("odds.png", 120, 80, new SKColor(0xC0, 0x60, 0x30));
        _fixtures.Add(fx);
        return fx;
    }

    /// <summary>A game of the phase fixture with players Anna and Ben; with <paramref name="atScreen"/> Anna has taken over (the first
    /// handover, raised when the game became multi-player, was continued).</summary>
    MainWindow Game(Difficulty difficulty = Difficulty.FullyAuto, bool combatRun = false, bool atScreen = true)
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(Module().Build()));
        var s = w.Session!;
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        w.StartSetup(0, difficulty);
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            return p with
            {
                Phases = p.Phases.Select(x => x.Id != "ph-combat" ? x : x with
                {
                    SideId = "blue",
                    OnStart = combatRun ? new() { new StartEntry { FormulaId = "f-water", ForEach = ForEachMode.Units } } : x.OnStart,
                }).ToList(),
            };
        });
        Pump();
        s.Game!.Execute(new SetPlayers(new[] { Anna, Ben }));
        Pump();
        Assert.True(w.HandoverShowing);
        Assert.Equal("Pass to Anna (Red)", w.Handover!.Message);
        if (atScreen) Continue(w);
        return w;
    }

    static void Continue(MainWindow w)
    {
        w.Handover!.ContinueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    /// <summary>Start (Weather), Supply, Movement (Red), then Combat (Blue).</summary>
    static void ToCombat(MainWindow w)
    {
        for (var i = 0; i < 4; i++) w.Actions!.NextPhase();
        Pump();
    }

    static (Window Ledger, Window Chart) OpenWindows(MainWindow w)
    {
        var ledger = w.OpenLedger()!;
        w.OpenChart(w.Session!.Model.Charts[0]);
        Pump();
        var chart = w.ChartWindows.Values.Single();
        Assert.True(ledger.IsVisible);
        Assert.True(chart.IsVisible);
        return (ledger, chart);
    }

    [AvaloniaFact]
    public void Handover_window_layout()
    {
        var h = new HandoverWindow(Ben, "Blue");
        try
        {
            Assert.Equal("Quartermaster", h.Title);
            Assert.Equal((360d, 220d), (h.MinWidth, h.MinHeight));
            Assert.Equal("Pass to Ben (Blue)", h.Message);
            Assert.Equal("Continue", h.ContinueButton.Content);
            Assert.True(h.ContinueButton.IsDefault);
            h.SetPlayer(Anna, "Red");
            Assert.Same(Anna, h.Player);
            Assert.Equal("Pass to Anna (Red)", h.Message);
            h.Show();
            Pump();
            Assert.True(h.ContinueButton.IsFocused);
            var asked = 0;
            h.CloseRequested += () => asked++;
            h.Close();
            Pump();
            Assert.Equal(1, asked);
            Assert.True(h.IsVisible);
        }
        finally { h.CloseForReal(); }
        Assert.False(h.IsVisible);
    }

    [AvaloniaFact]
    public void Next_phase_to_other_player_shows_handover_and_hides_windows()
    {
        var w = Game();
        Assert.False(w.HandoverShowing);
        Assert.True(w.IsVisible);
        var (ledger, chart) = OpenWindows(w);
        ToCombat(w);
        Assert.True(w.HandoverShowing);
        var h = w.Handover!;
        Assert.True(h.IsVisible);
        Assert.Equal("Pass to Ben (Blue)", h.Message);
        Assert.Same(Ben.Id, h.Player.Id);
        Assert.False(ledger.IsVisible);
        Assert.False(chart.IsVisible);
        Assert.False(w.IsVisible);
        Assert.Equal(w.Position, h.Position);
        Assert.Equal((w.Width, w.Height), (h.Width, h.Height));
    }

    [AvaloniaFact]
    public void Handover_takes_maximised_state()
    {
        var w = Game();
        w.WindowState = WindowState.Maximized;
        Pump();
        ToCombat(w);
        Assert.Equal(WindowState.Maximized, w.Handover!.WindowState);
    }

    [AvaloniaFact]
    public void View_is_public_only_until_continue()
    {
        var w = Game();
        Assert.Equal(new[] { "red" }, w.Session!.ViewSides!.ToArray());
        ToCombat(w);
        Assert.True(w.HandoverShowing);
        Assert.NotNull(w.Session.ViewSides);
        Assert.Empty(w.Session.ViewSides!);
        Assert.Equal(Anna.Id, w.HotSeat!.Current?.Id);
    }

    [AvaloniaFact]
    public void Continue_takes_over_and_restores_windows()
    {
        var w = Game();
        var (ledger, chart) = OpenWindows(w);
        ToCombat(w);
        var h = w.Handover!;
        Continue(w);
        Assert.False(w.HandoverShowing);
        Assert.Null(w.Handover);
        Assert.False(h.IsVisible);
        Assert.Equal(new[] { "blue" }, w.Session!.ViewSides!.ToArray());
        Assert.Equal(Ben.Id, w.HotSeat!.Current?.Id);
        Assert.True(w.IsVisible);
        Assert.True(ledger.IsVisible);
        Assert.True(chart.IsVisible);
        Assert.Same(w, ledger.Owner);
        Assert.Same(w, chart.Owner);
        Assert.Equal("Player: Ben", w.StatusText);
    }

    [AvaloniaFact]
    public void Handover_waits_for_modal_dialog()
    {
        var w = Game();
        var dlg = new Window { Width = 200, Height = 100 };
        var shown = dlg.ShowDialogTracked<object>(w);
        Pump();
        Assert.True(ModalTracker.Open);
        ToCombat(w);
        Pump();
        Assert.True(w.HandoverShowing);   // due: waiting for the dialog
        Assert.Null(w.Handover);
        Assert.Empty(w.Session!.ViewSides!);   // the view is already public-only
        Assert.True(w.IsVisible);
        dlg.Close();
        Pump();
        Assert.True(shown.IsCompleted);
        Assert.False(ModalTracker.Open);
        PumpUntil(() => w.Handover != null);   // the 200 ms retry
        Assert.Equal("Pass to Ben (Blue)", w.Handover!.Message);
        Assert.False(w.IsVisible);
    }

    [AvaloniaFact]
    public void Dialog_over_hidden_main_window_is_owned_by_handover()
    {
        var w = Game();
        ToCombat(w);
        Assert.False(w.IsVisible);
        var dlg = new Window { Width = 200, Height = 100 };
        var shown = dlg.ShowDialogTracked<object>(w);
        Pump();
        Assert.Same(w.Handover, dlg.Owner);
        dlg.Close();
        Pump();
        Assert.True(shown.IsCompletedSuccessfully);
    }

    [AvaloniaFact]
    public void No_untracked_ShowDialog()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Quartermaster.slnx"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var src = Path.Combine(dir!, "src", "Quartermaster.Desktop");
        var call = new Regex(@"\.ShowDialog\s*[(<]");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (Path.GetFileName(file) == "ModalTracker.cs") continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (call.IsMatch(lines[i])) offenders.Add($"{rel}:{i + 1}");
        }
        Assert.True(offenders.Count == 0, "untracked ShowDialog: " + string.Join(", ", offenders));
    }

    [AvaloniaFact]
    public void Closing_handover_asks_main_window_and_No_keeps_it()
    {
        var w = Game();
        ToCombat(w);
        Assert.True(w.HasUnsavedChanges);
        var h = w.Handover!;
        _dialogs.Answer = false;
        h.Close();
        Pump();
        Pump();
        Assert.Equal(new[] { "The current game has unsaved changes. Discard them?" }, _dialogs.Questions.Select(q => q.Message));
        Assert.True(h.IsVisible);
        Assert.Same(h, w.Handover);
        Assert.True(w.HandoverShowing);
        Assert.False(w.IsVisible);
        Assert.NotNull(w.Session);

        // Yes (and No to saving the rules pack): the main window closes and the handover screen goes with it, nothing comes back.
        _dialogs.Answer = true;
        _dialogs.YesNoCancel.Enqueue(false);
        h.Close();
        PumpUntil(() => w.Session == null, 5);
        Assert.False(h.IsVisible);
        Assert.Null(w.Handover);
        Assert.Null(w.Session);
        Assert.False(w.IsVisible);
    }

    [AvaloniaFact]
    public void Rewind_cancels_handover_and_restores_windows()
    {
        var w = Game();
        var (ledger, _) = OpenWindows(w);
        ToCombat(w);
        Assert.True(w.HandoverShowing);
        w.History!.Undo();   // back to Movement: Anna's own phase
        Pump();
        Assert.False(w.HandoverShowing);
        Assert.Null(w.Handover);
        Assert.True(w.IsVisible);
        Assert.True(ledger.IsVisible);
        Assert.Equal(new[] { "red" }, w.Session!.ViewSides!.ToArray());
        Assert.Equal("Handover no longer needed", w.StatusText);
    }

    [AvaloniaFact]
    public void Runs_wait_for_continue()
    {
        var w = Game(Difficulty.Hardcore, combatRun: true);
        var s = w.Session!;
        ToCombat(w);
        PumpUntil(() => w.Handover != null);
        Pump();
        var run = s.Game!.State.Pending.Single(r => r.Units.Any(u => u.PieceId == PhaseFixture.BlueOne));
        Assert.DoesNotContain(FormulaWindows.OpenFor(s), f => f.Link?.RunId == run.Id);
        Continue(w);
        Pump();
        Assert.Contains(FormulaWindows.OpenFor(s), f => f.Link?.RunId == run.Id);
    }

    [AvaloniaFact]
    public void Formula_windows_close_on_handover()
    {
        var w = Game();
        var s = w.Session!;
        Assert.NotNull(FormulaWindows.Show(w, s, "f-water", PhaseFixture.RedOne, null));
        Pump();
        Assert.Single(FormulaWindows.OpenFor(s));
        ToCombat(w);
        Assert.Empty(FormulaWindows.OpenFor(s));
        Continue(w);
        Assert.Empty(FormulaWindows.OpenFor(s));   // closed, not hidden and brought back
    }

    [AvaloniaFact]
    public void Retargets_when_second_handover_due()
    {
        // The screen asks for Anna; nobody has taken over yet. The phases move on underneath (as a load or a rewind would: Next Phase
        // itself is refused while nobody is at the screen) until Combat, Ben's.
        var w = Game(atScreen: false);
        var h = w.Handover!;
        for (var i = 0; i < 4; i++) w.PhaseControl!.Advance();
        Pump();
        Assert.Same(h, w.Handover);
        Assert.Equal("Combat", w.PhaseControl!.View().Current?.Name);
        Assert.Equal("Pass to Ben (Blue)", h.Message);
        Assert.Equal(Ben.Id, h.Player.Id);
        Continue(w);
        Assert.Equal(Ben.Id, w.HotSeat!.Current?.Id);
    }

    [AvaloniaFact]
    public void Becoming_solo_dismisses_handover()
    {
        var w = Game();
        var (ledger, _) = OpenWindows(w);
        ToCombat(w);
        Assert.True(w.HandoverShowing);
        w.Session!.Game!.Execute(new SetPlayers(Array.Empty<PlayerDef>()));
        Pump();
        Assert.False(w.HandoverShowing);
        Assert.Null(w.Handover);
        Assert.True(w.IsVisible);
        Assert.True(ledger.IsVisible);
        Assert.Null(w.Session.ViewSides);
    }

    [AvaloniaFact]
    public void Guest_session_stops_hot_seat()
    {
        var w = Game();
        var s = w.Session!;
        var state = s.Game!.State;
        s.EnterGuestMode();
        s.ApplySnapshot(state, new HashSet<string> { "blue" });   // a two-player snapshot: the host owns the view
        Pump();
        Assert.False(w.HandoverShowing);
        Assert.Null(w.Handover);
        Assert.Null(w.HotSeat);
        Assert.True(w.IsVisible);
        Assert.Equal(new[] { "blue" }, s.GuestSides!.ToArray());
    }

    [AvaloniaFact]
    public void Guest_stop_while_up_restores_windows()
    {
        var w = Game();
        var (ledger, chart) = OpenWindows(w);
        ToCombat(w);
        Assert.True(w.HandoverShowing);
        Assert.False(ledger.IsVisible);
        var h = w.Handover!;
        w.Session!.EnterGuestMode();   // the game is not closing: its windows come back
        Pump();
        Assert.Null(w.HotSeat);
        Assert.False(w.HandoverShowing);
        Assert.Null(w.Handover);
        Assert.False(h.IsVisible);
        Assert.True(w.IsVisible);
        Assert.True(ledger.IsVisible);
        Assert.True(chart.IsVisible);
        Assert.Same(w, ledger.Owner);
    }

    [AvaloniaFact]
    public void Handover_closed_elsewhere_brings_main_window_back()
    {
        var w = Game();
        var (ledger, _) = OpenWindows(w);
        ToCombat(w);
        var h = w.Handover!;
        h.CloseForReal();   // as an application / OS shutdown closes it: not through DismissHandover, no CloseRequested
        Assert.False(h.IsVisible);
        Assert.Null(w.Handover);
        Assert.True(w.IsVisible);
        Assert.True(ledger.IsVisible);
        Assert.Empty(w.Session!.ViewSides!);   // still public-only: the handover is pending in the controller
        Assert.Equal(Ben.Id, w.HotSeat!.Pending?.Id);
        // The shutdown did not end the app (its discard question answered No, say) and no modal is open: the screen comes back for Ben.
        Pump();
        Assert.True(w.HandoverShowing);
        var again = w.Handover!;
        Assert.NotSame(h, again);
        Assert.True(again.IsVisible);
        Assert.Equal("Pass to Ben (Blue)", again.Message);
        Assert.False(w.IsVisible);
        Assert.False(ledger.IsVisible);
        Assert.Empty(w.Session!.ViewSides!);
        Continue(w);
        Assert.Equal(new[] { "blue" }, w.Session!.ViewSides!.ToArray());
        Assert.True(w.IsVisible);
        Assert.True(ledger.IsVisible);
    }

    [AvaloniaFact]
    public void Closing_module_while_up_dismisses()
    {
        var w = Game();
        var (ledger, chart) = OpenWindows(w);
        ToCombat(w);
        var h = w.Handover!;
        Assert.True(w.OpenModule(Module().Build()));
        Pump();
        Assert.False(w.HandoverShowing);
        Assert.Null(w.Handover);
        Assert.False(h.IsVisible);
        Assert.True(w.IsVisible);
        Assert.False(ledger.IsVisible);
        Assert.False(chart.IsVisible);
        Assert.Null(w.Ledger);
        Assert.Empty(w.ChartWindows);
        Assert.False(w.HotSeat!.Active);   // the new module's controller
    }
}
