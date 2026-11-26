using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Phases;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// B5 task 8: the Desktop side of the turn structure: the Next Phase question, the reacting line, game over, and the Due section. Invented
/// phase fixture with a "Holding box" map (Red Relief 1 and 2, Blue Relief 1 on it); headless, fake dialogs.
/// </summary>
[Collection("MacFlag")]
public sealed class TurnStructureUiTests : IDisposable
{
    const string Holding = "Holding box";
    const string RedRelief = "startsWith(name(), \"Red Relief\")";
    const string RedAll = "startsWith(name(), \"Red \")";

    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-turnui-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public TurnStructureUiTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();
    static void Flush(MainWindow w) { Pump(); w.Strip.Flush(); w.Phases.Flush(); }

    static ScheduleRow Row(string id, string piece, ScheduleAction action = ScheduleAction.Arrive, string phase = "ph-weather", string? zone = Holding,
        string? label = null) => new() { Id = id, Turn = 1, PhaseId = phase, Piece = piece, Action = action, ZoneMapId = zone, Label = label };

    static RulesPack WithPhase(RulesPack p, string id, Func<PhaseDef, PhaseDef> edit) =>
        p with { Phases = p.Phases.Select(x => x.Id == id ? edit(x) : x).ToList() };

    static RulesPack OpenBlock(RulesPack p) =>
        p with { Blocks = new() { new PhaseBlock { FirstPhaseId = "ph-move", LastPhaseId = "ph-combat", Count = 0, Label = "Round" } } };

    static void AddPiece(GameSession g, string id, string name, string mapId, double x) =>
        g.Execute(new AddPiece(id, PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;" + name }), PieceTypeParser.EncodeState(new[] { "" }), mapId,
            new PointD(x, 30), null, id + " placed"));

    MainWindow Game(Func<RulesPack, RulesPack>? tweak = null)
    {
        const string extra = """
              <VASSAL.build.module.Map mapName="Holding box">
                <VASSAL.build.module.map.BoardPicker>
                  <VASSAL.build.module.map.boardPicker.Board image="hold.png" name="Hold board" reversible="false"/>
                </VASSAL.build.module.map.BoardPicker>
              </VASSAL.build.module.Map>
            </VASSAL.build.GameModule>
            """;
        var fx = PhaseFixture.Module().EditBuildFile(x => x.Replace("</VASSAL.build.GameModule>", extra)).AddImage("hold.png", 120, 80);
        _fixtures.Add(fx);
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        w.StartSetup(0, Difficulty.FullyAuto);
        var hold = s.Model.Maps.First(m => m.Name == Holding).Id;
        AddPiece(s.Game!, "rr1", "Red Relief 1", hold, 20);
        AddPiece(s.Game!, "rr2", "Red Relief 2", hold, 40);
        AddPiece(s.Game!, "br1", "Blue Relief 1", hold, 60);
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            return tweak == null ? p : tweak(p);
        });
        s.Game!.Rebase();
        Flush(w);
        return w;
    }

    static void Press(MainWindow w, int times = 1)
    {
        for (var i = 0; i < times; i++) w.Actions!.NextPhase();
        Flush(w);
    }

    static void CtrlRight(MainWindow w)
    {
        w.KeyPress(Key.Right, RawInputModifiers.Control, PhysicalKey.ArrowRight, null);
        Flush(w);
    }

    static string PhaseId(MainWindow w) => w.Session!.Game!.State.Position!.PhaseId;

    // ---------- the question ----------

    [AvaloniaFact]
    public void Repeat_dialog_texts_and_buttons()
    {
        var w = Game(OpenBlock);
        Press(w, 4);
        Assert.Equal("ph-combat", PhaseId(w));
        _dialogs.ContinueAnyway = true;
        _dialogs.ChooseAnswer = 0;
        CtrlRight(w);
        var (message, buttons) = Assert.Single(_dialogs.ChooseCalls);
        Assert.Equal("End of Round 1: play another Round or continue?", message);
        Assert.Equal(new[] { "Another Round", "Continue", "Cancel" }, buttons);
        Assert.Equal("Next Phase", DesktopPhaseUi.RepeatCaption);
        Assert.Equal(("ph-move", 2), (PhaseId(w), w.Session!.Game!.State.Position!.Repeat));   // Another Round: the block plays again

        // Continue (the default button, index 1) leaves the block.
        Press(w, 1);
        Assert.Equal("ph-combat", PhaseId(w));
        _dialogs.ChooseAnswer = 1;
        CtrlRight(w);
        Assert.Equal("ph-end", PhaseId(w));
    }

    [AvaloniaFact]
    public void Repeat_dialog_cancel_returns_null()
    {
        var w = Game(OpenBlock);
        Press(w, 4);
        _dialogs.ContinueAnyway = true;
        foreach (var cancel in new[] { 2, -1 })   // the Cancel button, and closing the window
        {
            _dialogs.ChooseAnswer = cancel;
            var before = w.Session!.Game!.Actions.Count;
            CtrlRight(w);
            Assert.Equal("ph-combat", PhaseId(w));
            Assert.Equal(before, w.Session.Game.Actions.Count);
        }
        Assert.Equal(2, _dialogs.ChooseCalls.Count);
    }

    // ---------- the panel and the strip ----------

    [AvaloniaFact]
    public void Panel_shows_reacting_line()
    {
        var w = Game(p => WithPhase(p, "ph-move", x => x with { Reactors = new() { "blue" } }));
        Press(w, 2);   // Supply (Red)
        Assert.Null(w.Phases.ReactingText);
        Press(w);   // Movement (Red, Blue reacting)
        Assert.Equal("Reacting: Blue", w.Phases.ReactingText);
        Press(w);   // Combat
        Assert.Null(w.Phases.ReactingText);
    }

    static RulesPack LastTurn(RulesPack p) => p with { LastTurn = 1 };

    static void PlayToTheEnd(MainWindow w)
    {
        for (var i = 0; i < 40 && w.Session!.Game!.State.GameOver == null; i++) w.Actions!.NextPhase();
        Flush(w);
        Assert.NotNull(w.Session!.Game!.State.GameOver);
    }

    [AvaloniaFact]
    public void Panel_shows_game_over()
    {
        var w = Game(LastTurn);
        PlayToTheEnd(w);
        Assert.Equal("Game over: the last turn has been played", w.Phases.NextText);
        Assert.False(w.Phases.NextEnabled);
    }

    [AvaloniaFact]
    public void Strip_shows_game_over()
    {
        var w = Game(LastTurn);
        PlayToTheEnd(w);
        Assert.Contains("Game over: the last turn has been played", w.Strip.ItemTexts);
    }

    // ---------- the Due section ----------

    [AvaloniaFact]
    public void Due_section_hidden_when_empty()
    {
        var w = Game();
        Press(w);
        Assert.False(w.Phases.DueVisible);
        Assert.Empty(w.Phases.DueRows);

        // A row not reached yet is not listed either; reached, it is.
        var v = Game(p => p with { Schedule = new() { Row("r1", RedRelief, phase: "ph-end") } });
        Press(v);
        Assert.False(v.Phases.DueVisible);
        PlayTo(v, "ph-end");
        Assert.True(v.Phases.DueVisible);
    }

    static void PlayTo(MainWindow w, string phaseId)
    {
        for (var i = 0; i < 40 && w.Session!.Game!.State.Position?.PhaseId != phaseId; i++) w.Actions!.NextPhase();
        Flush(w);
    }

    [AvaloniaFact]
    public void Due_section_row_place_button_and_tooltip()
    {
        var w = Game(p => p with { Schedule = new() { Row("r-red", RedRelief, label: "Red relief arrives"), Row("r-none", "startsWith(name(), \"Nobody\")") } });
        Press(w);
        Assert.True(w.Phases.DueVisible);
        Assert.Equal(2, w.Phases.DueRows.Count);
        Assert.Equal("Red relief arrives — Due now", w.Phases.DueRowText(0));
        Assert.Equal("Place", w.Phases.DueButtonText(0));
        Assert.Equal("Mark these arrivals placed and show the counters on Holding box; drag them to their hexes.", w.Phases.DueButtonTip(0));
        // A solo row without counters is listed, with no button.
        Assert.Equal("Arrivals, turn 1 — Due now, no counter matches", w.Phases.DueRowText(1));
        Assert.Null(w.Phases.DueButtonText(1));
    }

    [AvaloniaFact]
    public void Due_row_click_centres_first_counter()
    {
        var w = Game(p => p with { Schedule = new() { Row("r-red", RedRelief) } });
        Press(w);
        w.Phases.ClickDueRow(0);
        Assert.Equal(Holding, w.Maps.ActiveView!.Map.Name);
        Assert.Single(w.Maps.ActiveView.SelectedPieceIds);
        Assert.Contains(w.Maps.ActiveView.SelectedPieceIds.Single(), new[] { "rr1", "rr2" });
    }

    [AvaloniaFact]
    public void Due_withdraw_button_text()
    {
        var w = Game(p => p with { Schedule = new() { Row("w-map", RedAll, ScheduleAction.Leave), Row("w-gone", RedAll, ScheduleAction.Leave, zone: null) } });
        Press(w);
        Assert.Equal(new[] { "Withdraw", "Withdraw" }, new[] { w.Phases.DueButtonText(0), w.Phases.DueButtonText(1) });
        Assert.Equal("Move these counters to Holding box.", w.Phases.DueButtonTip(0));
        Assert.Equal("Remove these counters from the game.", w.Phases.DueButtonTip(1));

        w.Phases.ClickDue(0);
        Flush(w);
        var st = w.Session!.Game!.State;
        Assert.Contains("w-map", st.ScheduleDone!);
        Assert.Equal(w.Session.Model.Maps.First(m => m.Name == Holding).Id, st.Pieces[PhaseFixture.RedOne].MapId);
        Assert.Single(w.Phases.DueRows);   // the done row left the list
    }

    [AvaloniaFact]
    public void Place_selects_counters_and_shows_zone_map()
    {
        var w = Game(p => p with { Schedule = new() { Row("r-red", RedRelief) } });
        Press(w);
        w.Phases.ClickDue(0);
        Flush(w);
        Assert.Contains("r-red", w.Session!.Game!.State.ScheduleDone!);
        Assert.Equal(Holding, w.Maps.ActiveView!.Map.Name);
        Assert.Equal(new[] { "rr1", "rr2" }, w.Maps.ActiveView.SelectedPieceIds.OrderBy(x => x));
        Assert.False(w.Phases.DueVisible);
    }

    [AvaloniaFact]
    public void Place_status_text()
    {
        var w = Game(p => p with { Schedule = new() { Row("r-red", RedRelief) } });
        Press(w);
        w.Phases.ClickDue(0);
        Assert.Equal("Placed: drag the counters from Holding box to their hexes", w.StatusText);
    }

    [AvaloniaFact]
    public void Due_row_of_a_missing_zone_map_shows_and_selects_no_map()
    {
        var w = Game(p => p with { Schedule = new() { Row("w-gone", RedAll, ScheduleAction.Leave, zone: "no-such-map") } });
        Press(w);
        var shown = w.Maps.ActiveView?.Map.Name;
        Assert.Equal("Withdraw", w.Phases.DueButtonText(0));
        w.Phases.ClickDue(0);
        Flush(w);
        Assert.Equal("That schedule row can't be done now.", w.StatusText);   // the engine refuses a map the module lacks
        Assert.Null(w.Session!.Game!.State.ScheduleDone);
        Assert.Equal(shown, w.Maps.ActiveView?.Map.Name);
        Assert.Empty(w.Maps.ActiveView?.SelectedPieceIds ?? new HashSet<string>());
    }
}

/// <summary>B5 task 8 online: a guest's Place goes to the host as an intent, and the guest sees the repeat-stop notice of its own press.</summary>
public sealed class TurnStructureGuestUiTests : IAsyncLifetime
{
    readonly FakeDialogs _hostDialogs = new() { Answer = true };
    readonly FakeDialogs _guestDialogs = new() { Answer = true };
    OnlineRig _rig = null!;

    public async ValueTask InitializeAsync() => _rig = await OnlineRig.StartAsync();
    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    static void PumpUntil(Func<bool> done) => OnlineRig.PumpUntil(done, 20);

    async Task<(MainWindow Host, MainWindow Guest)> Seated(Func<RulesPack, RulesPack> tweak)
    {
        var f = await _rig.ModuleAsync(sha => tweak(PhaseFixture.Pack(sha)));
        var host = await _rig.HostAsync(f, _hostDialogs, new[] { "red" });
        _guestDialogs.SidesAnswer = new[] { "blue" };
        var guest = (await _rig.GuestAsync(f, _guestDialogs, host.HostOnline!.Listing, "Carol")).Window;
        PumpUntil(() => guest.GuestOnline is { State: Quartermaster.Render.GuestState.Playing } && guest.Session?.Game != null);
        return (host, guest);
    }

    [AvaloniaFact]
    public async Task Guest_place_sends_intent()
    {
        var row = new ScheduleRow { Id = "b-arrive", Turn = 1, PhaseId = "ph-weather", Piece = "startsWith(name(), \"Blue\")", Action = ScheduleAction.Arrive, ZoneMapId = "Main Map" };
        var (host, guest) = await Seated(p => p with { Schedule = new() { row } });
        host.Actions!.NextPhase();   // Start: Weather, turn 1
        PumpUntil(() => { guest.Phases.Flush(); return guest.Phases.DueRows.Count == 1; });
        Assert.Equal("Place", guest.Phases.DueButtonText(0));
        Assert.Equal("Arrivals, turn 1 — Due now", guest.Phases.DueRowText(0));

        guest.Phases.ClickDue(0);
        PumpUntil(() => host.Session!.Game!.State.ScheduleDone?.Contains("b-arrive") == true);
        PumpUntil(() => guest.StatusText == "Placed: drag the counters from Main Map to their hexes");   // after the host's answer
        Assert.Equal("Main Map", guest.Maps.ActiveView!.Map.Name);
        Assert.Equal(new[] { PhaseFixture.BlueOne }, guest.Maps.ActiveView.SelectedPieceIds);
    }

    [AvaloniaFact]
    public async Task Guest_sees_repeat_stop_notice_of_its_own_press()
    {
        var weatherOnce = new StartEntry { FormulaId = "f-weather", ForEach = ForEachMode.Once };
        var (host, guest) = await Seated(p => p with
        {
            Blocks = new() { new PhaseBlock { FirstPhaseId = "ph-move", LastPhaseId = "ph-combat", Count = 0, Label = "Round" } },
            Phases = p.Phases.Select(x => x.Id == "ph-combat" ? x with { OnEnd = new() { weatherOnce } } : x).ToList(),
        });
        for (var i = 0; i < 4; i++) host.Actions!.NextPhase();   // Combat
        PumpUntil(() => guest.Session!.Game!.State.Position?.PhaseId == "ph-combat");

        guest.Actions!.NextPhase(false);   // runs the end entries, then stops for the question
        var text = PhaseController.RepeatStopText("Round");
        PumpUntil(() => { guest.Strip.Flush(); return guest.Strip.Notice == text; });
        Assert.Equal("ph-combat", host.Session!.Game!.State.Position!.PhaseId);
        Assert.Equal(text, host.PhaseControl!.View().Notice);   // the host shows the same
    }
}
