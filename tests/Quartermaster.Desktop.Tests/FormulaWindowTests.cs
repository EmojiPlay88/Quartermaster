using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 2b task 7: the formula window (FormulaDialog), its cheat sheet and the counter menu's Calculate submenu. Headless only,
/// on the invented phase fixture with a few extra formulas.</summary>
[Collection("MacFlag")]
public sealed class FormulaWindowTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-formula-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly List<ModuleSession> _sessions = new();
    readonly FakeDialogs _dialogs = new();

    static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };

    public FormulaWindowTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the dispatcher loop (RunJobs alone runs no timers, so the 150 ms debounce needs the main loop) until the condition holds.</summary>
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

    /// <summary>The phase fixture pack plus formulas for typing, tables, inline dice and a private holder.</summary>
    static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
            }).ToList(),
            Holders = new() { new HolderDef { Id = "reddepot", Name = "Red Depot", SideId = "red" } },
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-add", Name = "Add water", Text = "N + W",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "N", Kind = BindingKind.Input },
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-tab", Name = "Terrain water", Text = "T + W",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t-cost", RowExpr = "\"Clear\"" },
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-inline", Name = "Inline roll", Text = "dice(1, 6) + W",
                    Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-mix", Name = "Mixed supply", Text = "W + S",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                        new VariableBinding { Variable = "S", Kind = BindingKind.Holder, HolderId = "reddepot", Property = "supply" },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
            }).ToList(),
        };
    }

    MainWindow NewWindow()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache" + _windows.Count), null) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    /// <summary>A window with the phase fixture's setup at <paramref name="d"/>, the test pack and a counting roller (1, 2, 3, ...).</summary>
    MainWindow Game(Difficulty d)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w = NewWindow();
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        var next = 0;
        s.SetRoller((n, _) => Enumerable.Range(0, n).Select(_ => next++ % 6 + 1).ToList());
        w.StartSetup(0, d);
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        Pump();
        return w;
    }

    static FormulaWindow Open(MainWindow w, string formulaId, string? pieceId, RunLink? link = null)
    {
        var win = FormulaWindows.Show(w, w.Session!, formulaId, pieceId, link);
        Assert.NotNull(win);
        Pump();
        return win!;
    }

    static Button ButtonOf(Window win, string winFormsLabel) =>
        win.GetVisualDescendants().OfType<Button>().Single(b => b.Content is string t && MenuText.ToWinForms(t) == winFormsLabel);

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static void TypeAndWait(FormulaWindow win, string variable, string text, Func<bool> done)
    {
        win.Type(variable, text);
        PumpUntil(done);
    }

    double? Water(MainWindow w, string pieceId) => Ledger.Get(w.Session!.Game!.State, LedgerKey.Piece(pieceId), "water");

    [AvaloniaFact]
    public void Calculate_menu_opens_formula_window()
    {
        var w = Game(Difficulty.FullyAuto);
        var view = Assert.IsType<MapView>(w.MapTabs.ActiveView);
        var menu = MapContextMenu.Build(view, PhaseFixture.RedOne)!;
        var calc = menu.Items.OfType<MenuItem>().Single(m => MenuText.ToWinForms((string)m.Header!) == "Calculate");
        var water = calc.Items.OfType<MenuItem>().Single(m => MenuText.ToWinForms((string)m.Header!) == "Daily water");
        water.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();

        var win = Assert.Single(FormulaWindows.OpenFor(w.Session!));
        Assert.Single(FormulaWindows.OpenFor(w.Session!));
        Assert.Equal("Daily water: Red 1", win.Title);
        Assert.Same(w, win.Owner);
        Assert.True(win.IsVisible);
        Assert.Equal("9", win.ResultText);
        // Groups, buttons and the physical dice box as WinForms.
        var texts = win.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Formula", texts);
        Assert.Contains("Working", texts);
        Assert.True(ButtonOf(win, "&Apply to ledger").IsEnabled);
        Assert.False(ButtonOf(win, "&Calculate").IsVisible);   // fully automatic: no Calculate
        Assert.True(ButtonOf(win, "Cancel").IsVisible);
        var ruleRef = ButtonOf(win, "&Rule ref…");
        Assert.False(ruleRef.IsEnabled);
        Assert.Equal("Rulebook viewer comes in a later version", ToolTip.GetTip(ruleRef));
        Assert.Contains(win.GetVisualDescendants().OfType<CheckBox>(), c => (string?)c.Content == "Physical dice");

        Click(ButtonOf(win, "Cancel"));
        Pump();
        Assert.False(win.IsVisible);
        Assert.Empty(FormulaWindows.OpenFor(w.Session!));
    }

    [AvaloniaFact]
    public void Same_formula_piece_run_focuses_existing()
    {
        var w = Game(Difficulty.FullyAuto);
        var a = Open(w, "f-water", PhaseFixture.RedOne);
        var again = Open(w, "f-water", PhaseFixture.RedOne);
        Assert.Same(a, again);
        Assert.Single(FormulaWindows.OpenFor(w.Session!));
        var other = Open(w, "f-water", PhaseFixture.RedTwo);
        Assert.NotSame(a, other);
        Assert.Equal(2, FormulaWindows.OpenFor(w.Session!).Count);
        FormulaWindows.CloseAllFor(w.Session!);
        Pump();
        Assert.Empty(FormulaWindows.OpenFor(w.Session!));
    }

    [AvaloniaFact]
    public void Typing_box_updates_result()
    {
        var w = Game(Difficulty.FullyAuto);
        var win = Open(w, "f-add", PhaseFixture.RedOne);
        Assert.Equal("", win.ResultText);
        Assert.False(ButtonOf(win, "&Apply to ledger").IsEnabled);
        Assert.StartsWith("Waiting for: N", win.StatusText);
        TypeAndWait(win, "N", "5", () => win.ResultText == "15");
        Assert.Equal("All inputs filled", win.StatusText);
        Assert.Contains("15", win.WorkingText);
        Assert.True(ButtonOf(win, "&Apply to ledger").IsEnabled);
    }

    [AvaloniaFact]
    public void Apply_writes_ledger_and_closes()
    {
        var w = Game(Difficulty.FullyAuto);
        var win = Open(w, "f-water", PhaseFixture.RedOne);
        Click(ButtonOf(win, "&Apply to ledger"));
        Pump();
        Assert.False(win.IsVisible);
        Assert.Empty(FormulaWindows.OpenFor(w.Session!));
        Assert.Equal(9, Water(w, PhaseFixture.RedOne));
    }

    [AvaloniaFact]
    public void Hardcore_shows_calculate_and_checks()
    {
        var w = Game(Difficulty.Hardcore);
        var win = Open(w, "f-water", PhaseFixture.RedOne);
        var calc = ButtonOf(win, "&Calculate");
        var apply = ButtonOf(win, "&Apply to ledger");
        Assert.True(calc.IsVisible);
        Assert.False(apply.IsEnabled);   // the box is empty: the player types the value
        Assert.Equal("", win.BoxText("W"));

        TypeAndWait(win, "W", "3", () => win.StatusText.StartsWith("Doesn't match"));
        Assert.True(win.BoxWrong("W"));
        Assert.False(apply.IsEnabled);

        Click(calc);
        Pump();
        Assert.Equal("10", win.BoxText("W"));
        Assert.False(win.BoxWrong("W"));
        Assert.Equal("9", win.ResultText);
        Assert.True(apply.IsEnabled);
        Click(apply);
        Pump();
        Assert.False(win.IsVisible);
        Assert.Equal(9, Water(w, PhaseFixture.RedOne));
    }

    [AvaloniaFact]
    public void Semi_auto_shows_cheat_sheet()
    {
        var w = Game(Difficulty.SemiAuto);
        var win = Open(w, "f-tab", PhaseFixture.RedOne);
        var cheat = win.CheatSheet;
        Assert.NotNull(cheat);
        Assert.True(cheat!.IsVisible);
        Assert.Equal("Cheat sheet", cheat.Title);
        Assert.Same(win, cheat.Owner);
        Assert.True(ButtonOf(win, "&Calculate").IsVisible);
        Assert.Equal(new[] { "Variable", "Source", "Value" }, cheat.Headers);
        Assert.Contains(cheat.RowTexts, r => r.StartsWith("T | "));
        Assert.Contains(cheat.RowTexts, r => r.StartsWith("W | "));
        Assert.Contains("TestTerrainCost", cheat.TableNames);
        Assert.Equal(1, cheat.GoldCells);   // the used cell (Clear) in gold

        win.Close();
        Pump();
        Assert.False(cheat.IsVisible);   // owned by the dialog: closes with it
    }

    [AvaloniaFact]
    public void Physical_dice_box()
    {
        var w = Game(Difficulty.FullyAuto);
        var win = Open(w, "f-weather", null);
        Assert.Equal("Weather roll", win.Title);
        Assert.Equal("1", win.ResultText);   // the counting roller's first die
        var physical = win.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "Physical dice");
        Assert.True(physical.IsVisible);
        physical.IsChecked = true;
        Pump();
        Assert.Equal("", win.BoxText("R"));   // a typed box now
        Assert.Equal("", win.ResultText);
        TypeAndWait(win, "R", "4", () => win.ResultText == "4");
    }

    [AvaloniaFact]
    public void Reroll_inline()
    {
        var w = Game(Difficulty.FullyAuto);
        var win = Open(w, "f-inline", PhaseFixture.RedOne);
        var reroll = Assert.Single(win.InlineRollButtons);
        Assert.StartsWith("↻ ", (string)reroll.Content!);
        Assert.Equal("Roll again", ToolTip.GetTip(reroll));
        var before = (string)reroll.Content!;
        var result = win.ResultText;
        Click(reroll);
        Pump();
        var after = Assert.Single(win.InlineRollButtons);
        Assert.NotEqual(before, (string)after.Content!);
        Assert.NotEqual(result, win.ResultText);
    }

    [AvaloniaFact]
    public void Formula_for_other_side_unit_refused()
    {
        var w = Game(Difficulty.FullyAuto);
        w.Session!.SetViewSides(Blue);
        Pump();
        Assert.Null(FormulaWindows.Show(w, w.Session!, "f-water", PhaseFixture.RedOne, null));
        Assert.Equal("That unit belongs to a side you don't hold.", w.StatusText);
        Assert.Empty(FormulaWindows.OpenFor(w.Session!));
    }

    /// <summary>Hardcore: Start (Weather) then Supply, which queues Daily water for Red 1 and Red 2.</summary>
    static PendingRun ToSupply(MainWindow w)
    {
        w.Actions!.NextPhase();
        w.Actions!.NextPhase();
        Pump();
        return w.Session!.Game!.State.Pending.Single(r => r.FormulaId == "f-water");
    }

    [AvaloniaFact]
    public void Dialog_closes_when_run_no_longer_pending()
    {
        var w = Game(Difficulty.Hardcore);
        var run = ToSupply(w);
        var win = Open(w, "f-water", PhaseFixture.RedOne, new RunLink(run.Id, null));
        var other = Open(w, "f-water", PhaseFixture.RedTwo, new RunLink(run.Id, null));
        var s = w.Session!;
        var none = new Dictionary<string, string>();
        var trace = s.Runner!.Run(s.Game!.State, new RunRequest("f-water", PhaseFixture.RedOne, none, none,
            new Dictionary<string, IReadOnlyList<int>>(), null, true));
        Assert.True(w.Actions!.ApplyTrace(run.Id, PhaseFixture.RedOne, trace, true));
        Pump();
        Assert.False(win.IsVisible);   // its unit of the run is done
        Assert.True(other.IsVisible);  // Red 2 is still waiting
        Assert.Single(FormulaWindows.OpenFor(w.Session!));
    }

    [AvaloniaFact]
    public void Step_back_closes_formula_windows()
    {
        var w = Game(Difficulty.Hardcore);
        var run = ToSupply(w);
        var win = Open(w, "f-water", PhaseFixture.RedOne, new RunLink(run.Id, null));
        var row = w.History!.Checkpoints().Last();
        w.Actions!.StepBack(row);
        FormulaWindows.CloseAllFor(w.Session!);   // as the phase UI does after a step back
        Pump();
        Assert.DoesNotContain(w.Session!.Game!.State.Pending, r => r.Id == run.Id);
        Assert.False(win.IsVisible);
        Assert.Empty(FormulaWindows.OpenFor(w.Session!));
    }

    [AvaloniaFact]
    public void Step_back_alone_closes_window_whose_run_vanished()
    {
        var w = Game(Difficulty.Hardcore);
        var run = ToSupply(w);
        var win = Open(w, "f-water", PhaseFixture.RedOne, new RunLink(run.Id, null));
        w.Actions!.StepBack(w.History!.Checkpoints().Last());
        Pump();
        Assert.False(win.IsVisible);
        Assert.Empty(FormulaWindows.OpenFor(w.Session!));
    }

    /// <summary>Hardcore at Supply with an unrelated ledger edit to take back: a Calculate-menu window (no run link) and the run's batch grid
    /// stay open on their own through it, so only the rewind handler can close them.</summary>
    (FormulaWindow Win, BatchGridWindow Batch) OpenAfterSupply(MainWindow w, PendingRun run)
    {
        var view = Assert.IsType<MapView>(w.MapTabs.ActiveView);
        var menu = MapContextMenu.Build(view, PhaseFixture.RedOne)!;
        var calc = menu.Items.OfType<MenuItem>().Single(m => MenuText.ToWinForms((string)m.Header!) == "Calculate");
        calc.Items.OfType<MenuItem>().Single(m => MenuText.ToWinForms((string)m.Header!) == "Daily water").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        var win = Assert.Single(FormulaWindows.OpenFor(w.Session!));
        var batch = FormulaWindows.ShowBatch(w, w.Session!, w.Actions!, run.Id, null)!;
        Pump();
        Assert.True(win.IsVisible);
        Assert.True(batch.IsVisible);
        Assert.Single(FormulaWindows.OpenBatchFor(w.Session!));
        return (win, batch);
    }

    [AvaloniaFact]
    public void Undo_closes_formula_windows()
    {
        var w = Game(Difficulty.Hardcore);
        var run = ToSupply(w);
        w.Actions!.EditLedger(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 4, "test");
        Pump();
        var (win, batch) = OpenAfterSupply(w, run);
        w.Actions!.Undo();
        PumpUntil(() => !win.IsVisible && !batch.IsVisible);
        Assert.Contains(w.Session!.Game!.State.Pending, r => r.Id == run.Id);   // the run itself is still waiting
        Assert.Empty(FormulaWindows.OpenFor(w.Session!));
        Assert.Empty(FormulaWindows.OpenBatchFor(w.Session!));
        Assert.Null(win.CheatSheet);   // hardcore: no cheat sheet to leave behind
    }

    [AvaloniaFact]
    public void Redo_closes_formula_windows()
    {
        var w = Game(Difficulty.Hardcore);
        var run = ToSupply(w);
        w.Actions!.EditLedger(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 4, "test");
        w.Actions!.Undo();
        Pump();
        var (win, batch) = OpenAfterSupply(w, run);
        w.Actions!.Redo();
        PumpUntil(() => !win.IsVisible && !batch.IsVisible);
        Assert.Equal(4, Water(w, PhaseFixture.BlueOne));
        Assert.Empty(FormulaWindows.OpenFor(w.Session!));
        Assert.Empty(FormulaWindows.OpenBatchFor(w.Session!));
    }

    /// <summary>A remote port whose formula intents wait for the test to answer.</summary>
    sealed class PendingHostPort : IActionPort
    {
        public readonly List<FormulaIntent> Sent = new();
        public TaskCompletionSource<FormulaVerdict> Answer = new();
        public bool IsRemote => true;
        public FormulaVerdict ApplyFormula(FormulaIntent intent) => throw new NotSupportedException();
        public Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent) { Sent.Add(intent); return Answer.Task; }
        public void Move(IReadOnlyList<MovePieces> moves) => throw new NotSupportedException();
        public void PieceCommand(GameCommand command) => throw new NotSupportedException();
        public void PieceCommands(IReadOnlyList<GameCommand> commands) => throw new NotSupportedException();
        public bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated) => throw new NotSupportedException();
        public (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values) =>
            throw new NotSupportedException();
        public void EditLedger(string key, string propertyId, double after, string note) => throw new NotSupportedException();
        public void Roll(DiceSpec spec, string purpose) => throw new NotSupportedException();
        public void RecordRoll(RollDice roll) => throw new NotSupportedException();
        public void Tick(int index, bool ticked) => throw new NotSupportedException();
        public void Ack(int warningIndex) => throw new NotSupportedException();
        public string? NextPhase() => throw new NotSupportedException();
        public void HideAlert(string alertId, string? name) => throw new NotSupportedException();
        public void Chat(string text) => throw new NotSupportedException();
        public void SetTurn(int turn, string? phase) => throw new NotSupportedException();
        public void SetNotes(string text) => throw new NotSupportedException();
        public void Undo() => throw new NotSupportedException();
        public void Redo() => throw new NotSupportedException();
        public void StepBack(CheckpointRow row) => throw new NotSupportedException();
    }

    [AvaloniaFact]
    public void Guest_masked_inputs_asks_host()
    {
        // The host's full game, and a window whose session is a guest holding Blue with only its filtered snapshot.
        var hostFx = PhaseFixture.Module();
        _fixtures.Add(hostFx);
        var host = ModuleSession.Open(hostFx.Build(), Path.Combine(_tmp, "hcache"), Path.Combine(_tmp, "hroot"));
        _sessions.Add(host);
        host.LoadSetup(host.Model.Setups[0], Difficulty.Hardcore,
            new[] { new PlayerDef("p1", "Alice", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) });
        host.NewRules("t");
        host.UpdateRules(_ => Pack(host.Archive.Sha256));
        host.Game!.Execute(new LedgerDelta(LedgerKey.Holder("reddepot"), "supply", 0, 61616, "manual", null, null, null, null));

        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w = NewWindow();
        Assert.True(w.OpenModule(fx.Build()));
        var guest = w.Session!;
        guest.EnterGuestMode();
        guest.LoadGuestRules(host.Rules!);
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, Blue), Blue);
        Pump();
        var port = new PendingHostPort();
        ((ActionPortSwitch)w.Actions!).Inner = port;

        var win = Open(w, "f-mix", PhaseFixture.BlueOne);
        Assert.Equal(FormulaMasking.HostCalculatesText, win.BoxText("S"));
        Assert.False(win.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "Physical dice").IsVisible);   // the host rolls
        var calc = ButtonOf(win, "&Calculate");
        Click(calc);
        Pump();
        Assert.Equal(FormulaWindowRules.AskingHostText, win.StatusText);
        Assert.False(calc.IsEnabled);
        Assert.False(ButtonOf(win, "&Apply to ledger").IsEnabled);
        var intent = Assert.Single(port.Sent);
        Assert.DoesNotContain("S", intent.Typed.Keys);   // a hidden box is never sent

        port.Answer.SetResult(FormulaVerdict.Refused(GuestSession.NoAnswerText));
        PumpUntil(() => win.StatusText != FormulaWindowRules.AskingHostText);
        Assert.Equal(GuestSession.NoAnswerText, win.StatusText);
        Assert.True(calc.IsEnabled);
        Assert.True(win.IsVisible);
        Assert.DoesNotContain("61616", string.Join("|", win.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text))
            + string.Join("|", win.GetVisualDescendants().OfType<TextBox>().Select(t => t.Text)));
    }
}
