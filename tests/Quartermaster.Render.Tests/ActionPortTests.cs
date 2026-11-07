using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>The local action port is a pure refactor: every action through it equals the direct calls the UI made before. Invented data only.</summary>
public class ActionPortTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-port-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public ActionPortTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private (ModuleSession S, PhaseController C, HistoryController H, Func<GameCommand, GameState, GameCommand> Prepare) Open()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256) with
        {
            Alerts = new() { new AlertDef { Id = "a-low", Name = "Low water", Condition = "water < 100" } },
        });
        // The App's prepare (CommandLog.Prepare) with a fixed clock so two sessions compare equal.
        Func<GameCommand, GameState, GameCommand> prepare = (c, st) => CommandLog.Prepare(CommandLog.Stamp(c, null, T0), st, s);
        var phases = new PhaseController(s, prepare);
        var history = new HistoryController(s, phases, prepare);
        return (s, phases, history, prepare);
    }

    private static string Json(GameState st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    private static void Align(ModuleSession from, ModuleSession to)
    {
        to.Game!.State.GameId = from.Game!.State.GameId;
        to.Game.State.Log[0] = from.Game.State.Log[0];
    }

    [Fact]
    public void LocalActions_BehaveAsBefore()
    {
        var (a, ca, ha, pa) = Open();
        var (b, cb, hb, pb) = Open();
        Align(a, b);
        Assert.Equal(Json(a.Game!.State), Json(b.Game!.State));
        var port = new LocalActions(b, cb, hb, pb);
        Assert.False(port.IsRemote);
        var ga = a.Game!;
        var gb = b.Game!;

        // Next Phase three times: start (Weather), Supply, Movement.
        ca.Start(); ca.Advance(); ca.Advance();
        Assert.Null(port.NextPhase()); port.NextPhase(); port.NextPhase();

        // A drag (one gesture of two groups) and its OnMove trigger.
        var start = ga.State.Pieces[PhaseFixture.RedOne];
        var r1 = (MapId: start.MapId, Pos: start.Pos);   // values: the piece itself moves
        IReadOnlyList<MovePieces> Moves(GameState st) => new[]
        {
            new MovePieces(new[] { PhaseFixture.RedOne }, r1.MapId, new PointD(r1.Pos.X + 50, r1.Pos.Y), null),
            new MovePieces(new[] { PhaseFixture.RedTwo }, r1.MapId, new PointD(r1.Pos.X + 100, r1.Pos.Y), null),
        };
        using (ga.BeginAction()) ca.ExecuteMove(Moves(ga.State));
        port.Move(Moves(gb.State));

        // A map-menu batch (MapWindow.Execute): a move then a removal, one action.
        GameCommand[] Batch() => new GameCommand[]
        {
            new MovePieces(new[] { PhaseFixture.RedTwo }, r1.MapId, new PointD(r1.Pos.X + 10, r1.Pos.Y), null),
            new RemovePiece(PhaseFixture.BlueOne),
        };
        using (ga.BeginAction())
        {
            var gesture = new List<MovePieces>();
            foreach (var cmd in Batch())
            {
                if (cmd is MovePieces m) { gesture.Add(m); continue; }
                if (gesture.Count > 0) { ca.ExecuteMove(gesture.ToList()); gesture.Clear(); }
                ga.Execute(pa(cmd, ga.State));
            }
            if (gesture.Count > 0) ca.ExecuteMove(gesture.ToList());
        }
        port.PieceCommands(Batch());

        // Palette drop: a logged AddPiece, then seeding.
        var type = PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;Red 9" });
        AddPiece Add() => new("p:new", type, "", r1.MapId, new PointD(70, 50), null);
        ga.Execute(pa(Add(), ga.State));
        a.SeedLedgers();
        port.PieceCommand(Add());

        // Manual ledger edit with a note.
        var water = a.Rules!.Properties.First(p => p.Id == "water");
        var key = LedgerKey.Piece(PhaseFixture.RedOne);
        ga.Execute(pa(new LedgerDelta(key, "water", Ledger.GetOrDefault(ga.State, key, water), 3, "manual", null, null, "spilt", null), ga.State));
        port.EditLedger(key, "water", 3, "spilt");

        // Dice: a toolbar roll (session roller) and a recorded roll (physical or special die).
        var spec = new DiceSpec(2, 6, 1);
        ga.Execute(pa(new RollDice("Roll", 2, 6, a.Roll(2, 6), DiceExpr.Format(spec), null, 1), ga.State));
        port.Roll(spec, "Roll");
        RollDice Special() => new("Weather die", 1, 3, new[] { 2 }, null, new[] { "Rain" });
        ga.Execute(pa(Special(), ga.State));
        port.RecordRoll(Special());

        // A formula applied from the dialog (the engine's run).
        var trA = a.Runner!.Run(ga.State, new RunRequest("f-water", PhaseFixture.RedOne, new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()));
        var trB = b.Runner!.Run(gb.State, new RunRequest("f-water", PhaseFixture.RedOne, new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()));
        Assert.True(ca.ApplyFormula(null, PhaseFixture.RedOne, trA, false));
        Assert.True(port.ApplyTrace(null, PhaseFixture.RedOne, trB, false));

        // Combat: checklist tick, warning acknowledgement.
        ca.Advance();
        port.NextPhase();
        ca.Tick(0, true);
        port.Tick(0, true);
        ca.Acknowledge(0);
        port.Ack(0);

        // Alert hide, turn, notes, chat-free host actions.
        ga.Execute(pa(new HideAlert("a-low", Name: "Low water"), ga.State));
        port.HideAlert("a-low", "Low water");
        ga.Execute(pa(new SetTurn(3, "Dusk"), ga.State));
        port.SetTurn(3, "Dusk");
        ga.Execute(pa(new SetNotes("remember the bridge"), ga.State));
        port.SetNotes("remember the bridge");

        // Undo and redo.
        ha.Undo();
        port.Undo();
        ha.Redo();
        port.Redo();

        Assert.Equal(Json(ga.State), Json(gb.State));
        Assert.Equal(ga.Actions.Select(x => x.Label), gb.Actions.Select(x => x.Label));
        Assert.Equal(ga.Log.Count, gb.Log.Count);
    }

    [Fact]
    public void LocalActions_ApplyFormulaIntent_Solo_AppliesLikeTheDialog()
    {
        var (s, c, h, p) = Open();
        var port = new LocalActions(s, c, h, p);
        var before = Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.RedOne), "water");
        var v = port.ApplyFormula(new FormulaIntent("f-water", PhaseFixture.RedOne, null, new Dictionary<string, string>(), false));
        Assert.True(v.Applied, v.Message);
        Assert.Equal((before ?? 10) - 1, Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.RedOne), "water"));
        Assert.All(v.Boxes, b => Assert.Equal(FormulaBox.Ok, b.State));
    }

    [Fact]
    public void LocalActions_Chat_IsLoggedAsChat()
    {
        var (s, c, h, p) = Open();
        new LocalActions(s, c, h, p).Chat("good luck");
        var last = s.Game!.State.Log.Last();
        Assert.Equal(LogKind.Chat, last.Kind);
        Assert.Contains("good luck", last.Text);
    }
}
