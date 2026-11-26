using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Invented module and pack for the B5 schedule (#62) on the Render side: the phase fixture (Red 1, Red 2, Blue 1 on the Main Map; phases
/// Weather, Supply (Red), Movement (Red), Combat, End; the Round block over Movement–Combat) without Supply's start entry, plus a
/// "Holding box" map with one board holding Red Relief 1 (rr1), Red Relief 2 (rr2), Blue Relief 1 (br1) and Blue Spy (bs1, hidden by
/// Blue). Side "red" = Red, "blue" = Blue. Temp roots only.
/// </summary>
public abstract class ScheduleTestBase : IDisposable
{
    protected readonly string Tmp = Path.Combine(Path.GetTempPath(), "qm-sched-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    protected readonly List<ModuleSession> Sessions = new();

    protected static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    protected static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });
    protected static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    protected static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };

    protected const string Hide = "hide;I;Hide it;;;0.3;;false";
    protected const string Holding = "Holding box";
    protected const string RedRelief = "startsWith(name(), \"Red Relief\")";
    protected const string BlueRelief = "startsWith(name(), \"Blue Relief\")";
    protected const string RedAll = "startsWith(name(), \"Red\")";
    protected const string Refused = "That schedule row can't be done now.";

    protected ScheduleTestBase() => Directory.CreateDirectory(Tmp);

    public void Dispose()
    {
        foreach (var s in Sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(Tmp, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    protected FixtureModule Module()
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
        return fx;
    }

    /// <summary>The module file of the last <see cref="Open"/> (a guest opens the same module).</summary>
    protected string ModulePath { get; private set; } = "";

    protected sealed record Table(ModuleSession S, PhaseController C, LocalActions L, IntentExecutor X, HistoryController H);

    protected static string HoldId(ModuleSession s) => s.Model.Maps.First(m => m.Name == Holding).Id;

    protected static PointD HoldCentre(ModuleSession s)
    {
        var b = s.Model.Maps.First(m => m.Name == Holding).Boards[0];
        return new PointD(b.Offset.X + b.Width / 2.0, b.Offset.Y + b.Height / 2.0);
    }

    /// <param name="players">Seated players; null = solo.</param>
    protected Table Open(IEnumerable<ScheduleRow> rows, Func<RulesPack, RulesPack>? tweak = null, PlayerDef[]? players = null,
        Difficulty difficulty = Difficulty.FullyAuto)
    {
        var fx = Module();
        ModulePath = fx.Build();
        var s = ModuleSession.Open(ModulePath, Path.Combine(Tmp, "cache" + Sessions.Count), Path.Combine(Tmp, "root" + Sessions.Count));
        Sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], difficulty, players ?? Array.Empty<PlayerDef>());
        var g = s.Game!;
        var hold = HoldId(s);
        Add(g, "rr1", "Red Relief 1", hold, 20);
        Add(g, "rr2", "Red Relief 2", hold, 40);
        Add(g, "br1", "Blue Relief 1", hold, 60);
        Add(g, "bs1", "Blue Spy", hold, 80, (Hide, "blue"));
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            p = p with
            {
                Phases = p.Phases.Select(x => x.Id == "ph-supply" ? x with { OnStart = new() } : x).ToList(),
                Schedule = rows.ToList(),
            };
            return tweak == null ? p : tweak(p);
        });
        g.Rebase();
        Func<GameCommand, GameState, GameCommand> prepare = (cmd, st) => CommandLog.Prepare(cmd, st, s);
        var c = new PhaseController(s, prepare);
        var h = new HistoryController(s, c, prepare);
        return new Table(s, c, new LocalActions(s, c, h, prepare), new IntentExecutor(s, c), h);
    }

    private static void Add(GameSession g, string id, string name, string mapId, double x, params (string Trait, string State)[] traits)
    {
        var type = PieceTypeParser.EncodeState(traits.Select(t => t.Trait).Append("piece;;;unit.svg;" + name).ToList());
        var state = PieceTypeParser.EncodeState(traits.Select(t => t.State).Append("").ToList());
        g.Execute(new AddPiece(id, type, state, mapId, new PointD(x, 30), null, id + " placed"));
    }

    protected static ScheduleRow Row(string id, string piece, ScheduleAction action = ScheduleAction.Arrive, string phase = "ph-supply",
        int turn = 1, string? zone = Holding, string? formula = null, string? label = null, int? stage = null) => new()
    {
        Id = id, Turn = turn, Stage = stage, PhaseId = phase, Piece = piece, Action = action, ZoneMapId = zone, FormulaId = formula, Label = label,
    };

    protected static string Json(GameState? st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    protected static double Water(ModuleSession s, string id) =>
        Ledger.GetOrDefault(s.Game!.State, LedgerKey.Piece(id), s.Rules!.Properties.First(p => p.Id == "water"));

    /// <summary>Start (Weather), then Next Phase to Supply (Red).</summary>
    protected static void ToSupply(Table t)
    {
        t.C.Start();
        t.C.Advance();
        Assert.Equal("ph-supply", t.S.Game!.State.Position!.PhaseId);
    }
}

/// <summary>B5 #62 locally: Place and Withdraw through <see cref="PhaseController.ActOnSchedule"/>, its refusals, and the due list model.</summary>
public class ScheduleActionTests : ScheduleTestBase
{
    [Fact]
    public void Place_marks_done_logs_label_one_undo()
    {
        var t = Open(new[] { Row("r-red", RedRelief, label: "Red relief arrives") });
        ToSupply(t);
        var g = t.S.Game!;
        var before = Json(g.State);
        var actions = g.Actions.Count;

        Assert.Null(t.C.ActOnSchedule("r-red"));
        Assert.Equal(new[] { "r-red" }, g.State.ScheduleDone);
        Assert.Equal(actions + 1, g.Actions.Count);
        Assert.Equal("Red relief arrives", g.UndoLabel);
        Assert.Single(g.State.Log, l => l.Kind == LogKind.Turn && l.Text == "Schedule: Red relief arrives");
        Assert.Equal(HoldId(t.S), g.State.Pieces["rr1"].MapId);   // the counters stay on the zone map: the player drags them

        t.H.Undo();
        Assert.Null(g.State.ScheduleDone);
        Assert.Equal(before, Json(g.State));
    }

    [Fact]
    public void Place_runs_formula_per_counter_semi_auto_queues()
    {
        var t = Open(new[] { Row("r-red", RedRelief, formula: "f-water") }, difficulty: Difficulty.SemiAuto);
        ToSupply(t);
        var g = t.S.Game!;
        var actions = g.Actions.Count;
        IReadOnlyList<PendingRun>? raised = null;
        var inAction = true;
        t.C.RunsQueued += runs => { raised = runs; inAction = g.InAction; };

        Assert.Null(t.C.ActOnSchedule("r-red"));
        var run = Assert.Single(raised!);
        Assert.False(inAction);   // raised after the action closed
        Assert.Equal(("f-water", "schedule"), (run.FormulaId, run.Trigger));
        Assert.Equal(new[] { "rr1", "rr2" }, run.Units.Select(u => u.PieceId).OrderBy(x => x));
        Assert.Contains(g.State.Pending, r => r.Id == run.Id);
        Assert.Equal(10, Water(t.S, "rr1"));   // queued, not applied
        Assert.Equal(actions + 1, g.Actions.Count);
    }

    [Fact]
    public void Place_runs_formula_fully_auto_applies()
    {
        var t = Open(new[] { Row("r-red", RedRelief, formula: "f-water") });
        ToSupply(t);
        var g = t.S.Game!;
        var actions = g.Actions.Count;
        Assert.Null(t.C.ActOnSchedule("r-red"));
        Assert.Equal(9, Water(t.S, "rr1"));
        Assert.Equal(9, Water(t.S, "rr2"));
        Assert.Equal(10, Water(t.S, "br1"));
        Assert.Empty(g.State.Pending);
        Assert.Equal(actions + 1, g.Actions.Count);
    }

    [Fact]
    public void Withdraw_moves_to_zone_centre()
    {
        var t = Open(new[] { Row("w-red", RedAll, ScheduleAction.Leave, label: "Red withdraws") });
        ToSupply(t);
        var g = t.S.Game!;
        var actions = g.Actions.Count;
        var logs = g.State.Log.Count;
        Assert.Null(t.C.ActOnSchedule("w-red"));
        foreach (var id in new[] { PhaseFixture.RedOne, PhaseFixture.RedTwo })
        {
            Assert.Equal(HoldId(t.S), g.State.Pieces[id].MapId);
            Assert.Equal(HoldCentre(t.S), g.State.Pieces[id].Pos);
        }
        Assert.Equal(actions + 1, g.Actions.Count);
        Assert.Equal("Red withdraws", g.UndoLabel);
        var added = g.State.Log.Skip(logs).ToList();
        Assert.Single(added, l => l.Kind == LogKind.Move);   // one move with its own move text
        Assert.Single(added, l => l.Text == "Schedule: Red withdraws");
        Assert.Equal(new[] { "w-red" }, g.State.ScheduleDone);
    }

    [Fact]
    public void Withdraw_without_zone_deletes()
    {
        var t = Open(new[] { Row("w-one", "name() == \"Red 1\"", ScheduleAction.Leave, zone: null) });
        ToSupply(t);
        var g = t.S.Game!;
        Assert.Null(t.C.ActOnSchedule("w-one"));
        Assert.False(g.State.Pieces.ContainsKey(PhaseFixture.RedOne));
        Assert.True(g.State.Pieces.ContainsKey(PhaseFixture.RedTwo));
        Assert.Contains(g.State.Log, l => l.Text == "Schedule: Withdrawals, turn 1");
        Assert.Equal(new[] { "w-one" }, g.State.ScheduleDone);
    }

    [Fact]
    public void Withdraw_then_formula_on_moved()
    {
        var t = Open(new[] { Row("w-red", RedAll, ScheduleAction.Leave, formula: "f-water") });
        ToSupply(t);
        Assert.Null(t.C.ActOnSchedule("w-red"));
        Assert.Equal(9, Water(t.S, PhaseFixture.RedOne));
        Assert.Equal(9, Water(t.S, PhaseFixture.RedTwo));
        Assert.Equal(10, Water(t.S, "rr1"));   // already on the zone map: not a counter of the row
        Assert.Equal(HoldId(t.S), t.S.Game!.State.Pieces[PhaseFixture.RedOne].MapId);
    }

    [Fact]
    public void Withdraw_no_on_move_trigger()
    {
        var t = Open(new[] { Row("w-red", RedAll, ScheduleAction.Leave, phase: "ph-move") });
        ToSupply(t);
        t.C.Advance();   // Movement: its OnMove runs Fuel for movement on moved Red units
        var g = t.S.Game!;
        var fuel = t.S.Rules!.Properties.First(p => p.Id == "fuel");
        var before = Ledger.GetOrDefault(g.State, LedgerKey.Piece(PhaseFixture.RedOne), fuel);
        Assert.Null(t.C.ActOnSchedule("w-red"));
        Assert.Equal(HoldId(t.S), g.State.Pieces[PhaseFixture.RedOne].MapId);
        Assert.Equal(before, Ledger.GetOrDefault(g.State, LedgerKey.Piece(PhaseFixture.RedOne), fuel));
        Assert.DoesNotContain(g.State.Log, l => l.Text.Contains("Fuel for movement"));
        Assert.Empty(g.State.Pending);
    }

    [Fact]
    public void Done_row_refused()
    {
        var t = Open(new[] { Row("r-red", RedRelief) });
        ToSupply(t);
        Assert.Null(t.C.ActOnSchedule("r-red"));
        var before = Json(t.S.Game!.State);
        var actions = t.S.Game.Actions.Count;
        Assert.Equal(Refused, t.C.ActOnSchedule("r-red"));
        Assert.Equal(before, Json(t.S.Game.State));
        Assert.Equal(actions, t.S.Game.Actions.Count);
    }

    [Fact]
    public void Unreached_row_refused()
    {
        var t = Open(new[] { Row("r-later", RedRelief, turn: 2), Row("r-move", RedRelief, phase: "ph-move") });
        ToSupply(t);
        var before = Json(t.S.Game!.State);
        Assert.Equal(Refused, t.C.ActOnSchedule("r-later"));
        Assert.Equal(Refused, t.C.ActOnSchedule("r-move"));
        Assert.Equal(Refused, t.C.ActOnSchedule("nope"));
        Assert.Equal(Refused, t.C.ActOnSchedule(""));
        Assert.Equal(before, Json(t.S.Game.State));
    }

    [Fact]
    public void Game_over_refused()
    {
        var t = Open(new[] { Row("r-red", RedRelief) }, p => p with { LastTurn = 1 });
        t.C.Start();
        for (var i = 0; i < 20 && t.S.Game!.State.GameOver == null; i++) t.C.Advance();
        Assert.NotNull(t.S.Game!.State.GameOver);
        var before = Json(t.S.Game.State);
        Assert.Equal("The game is over.", t.C.ActOnSchedule("r-red"));
        Assert.Equal(before, Json(t.S.Game.State));
    }

    [Fact]
    public void Other_sides_phase_refused()
    {
        var t = Open(new[] { Row("r-blue", BlueRelief) }, players: new[] { Alice, Bob });
        ToSupply(t);   // Red's Supply
        t.S.SetViewSides(Blue);
        var before = Json(t.S.Game!.State);
        Assert.Equal("That can't be done during another side's phase.", t.C.ActOnSchedule("r-blue"));
        Assert.Equal(before, Json(t.S.Game.State));
    }

    [Fact]
    public void Reacting_side_may_act()
    {
        var t = Open(new[] { Row("r-blue", BlueRelief, formula: "f-water") },
            p => p with { Phases = p.Phases.Select(x => x.Id == "ph-supply" ? x with { Reactors = new() { "blue" } } : x).ToList() },
            new[] { Alice, Bob });
        ToSupply(t);   // Red's Supply; Blue reacts
        t.S.SetViewSides(Blue);
        Assert.Null(t.C.ActOnSchedule("r-blue"));
        Assert.Equal(new[] { "r-blue" }, t.S.Game!.State.ScheduleDone);
        Assert.Equal(9, Water(t.S, "br1"));
    }

    [Fact]
    public void Hot_seat_acts_only_on_own_counters()
    {
        var t = Open(new[] { Row("r-both", RedRelief + " or " + BlueRelief, phase: "ph-weather", formula: "f-water") },
            players: new[] { Alice, Bob });
        t.C.Start();   // Weather: no side
        t.S.SetViewSides(Red);
        Assert.Null(t.C.ActOnSchedule("r-both"));
        Assert.Equal(9, Water(t.S, "rr1"));
        Assert.Equal(9, Water(t.S, "rr2"));
        Assert.Equal(10, Water(t.S, "br1"));   // Blue's counter: not Red's to act on
    }

    [Fact]
    public void Due_model_texts_and_buttons()
    {
        var t = Open(new[]
        {
            Row("a", RedRelief),
            Row("b", RedAll, ScheduleAction.Leave, phase: "ph-weather", label: "Red withdraws"),
            Row("c", "name() == \"Blue 1\"", ScheduleAction.Leave, zone: null),
            Row("d", "name() == \"Nobody\""),
            Row("e", BlueRelief, ScheduleAction.Return),
        });
        ToSupply(t);
        var rows = DueModel.Rows(t.S);
        Assert.Equal(new[] { "b", "a", "c", "d", "e" }, rows.Select(r => r.RowId));
        var hold = HoldId(t.S);

        var b = rows[0];
        Assert.Equal(("Red withdraws", "Overdue since turn 1", "Withdraw", "Move these counters to Holding box.", hold),
            (b.Label, b.StateText, b.ButtonText, b.Tooltip, b.ZoneMapId));
        Assert.Equal(new[] { PhaseFixture.RedOne, PhaseFixture.RedTwo }, b.PieceIds.OrderBy(x => x));

        var a = rows[1];
        Assert.Equal(("Arrivals, turn 1", "Due now", "Place", "Mark these arrivals placed and show the counters on Holding box; drag them to their hexes.", hold),
            (a.Label, a.StateText, a.ButtonText, a.Tooltip, a.ZoneMapId));
        Assert.Equal(new[] { "rr1", "rr2" }, a.PieceIds.OrderBy(x => x));

        var c = rows[2];
        Assert.Equal(("Withdrawals, turn 1", "Due now", "Withdraw", "Remove these counters from the game.", (string?)null),
            (c.Label, c.StateText, c.ButtonText, c.Tooltip, c.ZoneMapId));

        var d = rows[3];   // solo: listed without counters and without a button
        Assert.Equal(("Arrivals, turn 1", "Due now, no counter matches", (string?)null, (string?)null), (d.Label, d.StateText, d.ButtonText, d.Tooltip));
        Assert.Empty(d.PieceIds);

        var e = rows[4];
        Assert.Equal(("Returns, turn 1", "Place"), (e.Label, e.ButtonText));

        t.C.ActOnSchedule("a");
        Assert.DoesNotContain(DueModel.Rows(t.S), r => r.RowId == "a");
    }

    [Fact]
    public void Due_model_hides_other_side_rows()
    {
        var t = Open(new[] { Row("r-red", RedRelief), Row("r-blue", BlueRelief), Row("r-spy", "name() == \"Blue Spy\"") },
            players: new[] { Alice, Bob });
        ToSupply(t);
        t.S.SetViewSides(Red);
        Assert.Equal(new[] { "r-red" }, DueModel.Rows(t.S).Select(r => r.RowId));
        t.S.SetViewSides(Blue);
        Assert.Equal(new[] { "r-blue", "r-spy" }, DueModel.Rows(t.S).Select(r => r.RowId));
    }

    [Fact]
    public void Decision_listed_for_due_now()
    {
        var t = Open(new[] { Row("r-red", RedRelief, label: "Red relief arrives") });
        ToSupply(t);
        var dec = Assert.Single(t.C.View().Decisions, x => x.Kind == DecisionKind.Schedule);
        Assert.Equal("Due: Red relief arrives", dec.Text);
        Assert.Equal(new[] { "rr1", "rr2" }, dec.PieceIds.OrderBy(x => x));

        t.C.Advance();   // Movement: the row is overdue, listed but no longer a decision
        Assert.DoesNotContain(t.C.View().Decisions, x => x.Kind == DecisionKind.Schedule);
        Assert.Equal("Overdue since turn 1", Assert.Single(DueModel.Rows(t.S)).StateText);
    }
}
