using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 2b task 5: Next Phase (Ctrl+Right), the open-decisions dialog, phase-boundary autosave and the run dispatch. Invented
/// pack from PhaseFixture; headless, fake dialogs.</summary>
[Collection("MacFlag")]
public sealed class NextPhaseTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-nextphase-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public NextPhaseTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var w in _windows)
        {
            if (w.Session is { } s) FormulaWindows.CloseAllFor(s);
            w.ForceClose();
        }
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string LayoutRoot => Path.Combine(_tmp, "layout");

    static void Pump() => Dispatcher.UIThread.RunJobs();

    MainWindow PhaseGame(bool withPhases = true, Difficulty difficulty = Difficulty.FullyAuto)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, LayoutRoot) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        w.StartSetup(0, difficulty);
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            return withPhases ? p : p with { Phases = new(), Blocks = new() };
        });
        Pump();
        Flush(w);
        return w;
    }

    static void Flush(MainWindow w) { w.Strip.Flush(); w.Phases.Flush(); }

    static void CtrlRight(MainWindow w)
    {
        w.KeyPress(Key.Right, RawInputModifiers.Control, PhysicalKey.ArrowRight, null);
        Pump();
        Flush(w);
    }

    /// <summary>Start (Weather), Supply, Movement, Combat: Combat has the open checklist item "Resolve attacks".</summary>
    static void ToCombat(MainWindow w)
    {
        for (var i = 0; i < 4; i++) w.Actions!.NextPhase();
        Pump();
        Flush(w);
        Assert.Equal("Combat", w.Phases.TitleText);
    }

    [AvaloniaFact]
    public void Ctrl_right_runs_next_phase_with_phased_pack()
    {
        var w = PhaseGame();
        Assert.True(w.PhaseControl!.View().BeforeStart);
        CtrlRight(w);
        Assert.False(w.PhaseControl.View().BeforeStart);
        Assert.Equal(1, w.Session!.Game!.State.Turn.Turn);
        Assert.Equal("Weather", w.Phases.TitleText);
        CtrlRight(w);
        Assert.Equal("Red player — Supply", w.Phases.TitleText);
        Assert.Equal(1, w.Session.Game.State.Turn.Turn);   // the plain turn counter did not step
        Assert.Empty(_dialogs.DecisionPrompts);
    }

    [AvaloniaFact]
    public void Ctrl_right_steps_turn_without_phases()
    {
        var w = PhaseGame(withPhases: false);
        Assert.Equal(1, w.Session!.Game!.State.Turn.Turn);
        CtrlRight(w);
        Assert.Equal(2, w.Session.Game.State.Turn.Turn);
    }

    [AvaloniaFact]
    public void Open_decisions_ask_and_go_back_stays()
    {
        var w = PhaseGame();
        ToCombat(w);
        _dialogs.ContinueAnyway = false;
        CtrlRight(w);
        var open = Assert.Single(_dialogs.DecisionPrompts);
        Assert.Equal(new[] { "Resolve attacks" }, open.Select(d => d.Text));
        Assert.Equal("Combat", w.Phases.TitleText);

        // Double-clicking the row goes back and shows the checklist item in the phase panel.
        w.ClickMenu("&Phase panel");   // hidden first: the jump reveals it
        Pump();
        Assert.False(w.PhasePanelShown);
        _dialogs.JumpTo = 0;
        CtrlRight(w);
        Assert.Equal(2, _dialogs.DecisionPrompts.Count);
        Assert.Equal("Combat", w.Phases.TitleText);
        Assert.True(w.PhasePanelShown);
    }

    [AvaloniaFact]
    public void Continue_anyway_advances()
    {
        var w = PhaseGame();
        ToCombat(w);
        _dialogs.ContinueAnyway = true;
        CtrlRight(w);
        Assert.Single(_dialogs.DecisionPrompts);
        Assert.NotEqual("Combat", w.Phases.TitleText);
    }

    [AvaloniaFact]
    public void Next_phase_reentry_is_ignored()
    {
        var w = PhaseGame();
        ToCombat(w);
        var gate = new TaskCompletionSource<bool>();
        _dialogs.DecisionsGate = gate;
        CtrlRight(w);
        CtrlRight(w);   // the dialog is still open: ignored
        Assert.Single(_dialogs.DecisionPrompts);
        Assert.Equal("Combat", w.Phases.TitleText);
        gate.SetResult(true);
        Pump();
        Flush(w);
        Assert.Single(_dialogs.DecisionPrompts);
        // One advance only: Combat (round 1) -> Movement (round 2); a second would have reached Combat again.
        Assert.Contains("Movement", w.Phases.TitleText);
    }

    [AvaloniaFact]
    public void Phase_boundary_writes_autosave()
    {
        var w = PhaseGame();
        var dir = Path.Combine(Autosaver.AutosaveRoot(LayoutRoot), w.Session!.Archive.Sha256);
        Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir, "autosave-*.qsave").Length > 0);
        CtrlRight(w);   // Start game: the first phase boundary
        Pump();
        Assert.True(Directory.Exists(dir));
        var first = Directory.GetFiles(dir, "autosave-*.qsave").Length;
        Assert.True(first >= 1);
        CtrlRight(w);
        Pump();
        Assert.True(Directory.GetFiles(dir, "autosave-*.qsave").Length > first);
    }

    [AvaloniaFact]
    public void Runs_queued_status_text()
    {
        var w = PhaseGame(difficulty: Difficulty.Hardcore);
        var s = w.Session!;
        CtrlRight(w);   // Weather: the once-per-phase weather roll is queued and its checked dialog opens
        Pump();
        Assert.Equal("1 calculation(s) waiting in the phase panel", w.StatusText);
        var win = Assert.Single(FormulaWindows.OpenFor(s));
        Assert.NotNull(win.Link);
        Assert.Equal(s.Game!.State.Pending.Single().Id, win.Link!.RunId);
    }

    [AvaloniaFact]
    public void Next_phase_dialog_texts()
    {
        var open = new[]
        {
            new Decision(DecisionKind.Checklist, "Resolve attacks", 0, null, Array.Empty<string>()),
            new Decision(DecisionKind.Checklist, "Check supply", 1, null, Array.Empty<string>()),
        };
        var dlg = new NextPhaseDialog(open);
        Assert.Equal("Next Phase", dlg.Title);
        Assert.Equal(PhaseTexts.NextPhaseHead(2), dlg.HeadText);
        Assert.Equal(new[] { "Resolve attacks", "Check supply" }, dlg.ItemTexts);
        Assert.Equal(new[] { "Continue anyway", "Go back" }, dlg.ButtonTexts);
        Assert.Equal("Go back", dlg.DefaultButtonText);   // Enter goes back
        Assert.Equal("Go back", dlg.CancelButtonText);
        Assert.Null(dlg.JumpTo);
        dlg.ActivateRow(1);
        Assert.Same(open[1], dlg.JumpTo);
    }
}
