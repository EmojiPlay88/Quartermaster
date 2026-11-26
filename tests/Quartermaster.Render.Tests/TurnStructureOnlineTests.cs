using System.Security.Cryptography;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B5 through the action ports and online: the repeat choice as a Next Phase flag (local, remote, the intent's JSON), the host's checks of a
/// guest's Next Phase (acting side, game over, a repeat only where the host itself would ask), and reacting guests (unit-less runs and
/// global writes, never tick, acknowledge or Next Phase). Invented PhaseFixture pack (Red, Blue); temp roots only.
/// </summary>
public class TurnStructureOnlineTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-turns-online-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private readonly List<IDisposable> _online = new();

    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };

    public TurnStructureOnlineTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var o in _online) o.Dispose();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    /// <summary>PhaseFixture with Combat played by Blue, a global Morale and a unit formula writing it (as in IntentExecutorTests).</summary>
    private static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Properties = p.Properties.Append(new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Holders }).ToList(),
            Formulas = p.Formulas.Append(new FormulaDef
            {
                Id = "f-gout", Name = "Raise morale", Text = "W",
                Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" } },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "morale", Op = OutputOp.Set } },
            }).ToList(),
            Phases = p.Phases.Select(x => x.Id == "ph-combat" ? x with { SideId = "blue" } : x).ToList(),
        };
    }

    /// <summary>The Round block (Movement–Combat) repeats until a player stops.</summary>
    private static RulesPack Open(RulesPack p) =>
        p with { Blocks = new() { new PhaseBlock { FirstPhaseId = "ph-move", LastPhaseId = "ph-combat", Count = 0, Label = "Round" } } };

    private static RulesPack WithPhase(RulesPack p, string id, Func<PhaseDef, PhaseDef> edit) =>
        p with { Phases = p.Phases.Select(x => x.Id == id ? edit(x) : x).ToList() };

    /// <summary>Blue reacts in Red's Supply.</summary>
    private static RulesPack Reacting(RulesPack p) => WithPhase(p, "ph-supply", x => x with { Reactors = new() { "blue" } });

    private string _modulePath = "";

    /// <param name="players">The seated players (default Alice for Red, Bob for Blue); empty = solo.</param>
    private (ModuleSession S, PhaseController C, IntentExecutor X, LocalActions L) Make(Func<RulesPack, RulesPack>? tweak = null,
        PlayerDef[]? players = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        _modulePath = fx.Build();
        var s = ModuleSession.Open(_modulePath, Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, players ?? new[] { Alice, Bob });
        s.NewRules("t");
        s.UpdateRules(_ => { var p = Pack(s.Archive.Sha256); return tweak == null ? p : tweak(p); });
        Func<GameCommand, GameState, GameCommand> prepare = (cmd, st) => CommandLog.Prepare(cmd, st, s);
        var c = new PhaseController(s, prepare);
        return (s, c, new IntentExecutor(s, c), new LocalActions(s, c, new HistoryController(s, c, prepare), prepare));
    }

    private static PhasePosition Pos(ModuleSession s) => s.Game!.State.Position!;
    private static string Json(GameState? st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    /// <summary>Start, then Next Phase until Combat (Weather, Supply, Movement, Combat).</summary>
    private static void ToCombat(ModuleSession s, PhaseController c)
    {
        c.Start();
        c.Advance();
        c.Advance();
        c.Advance();
        Assert.Equal(("ph-combat", 1), (Pos(s).PhaseId, Pos(s).Repeat));
    }

    private static NextPhaseIntent Again => new() { Repeat = true };

    // ---- ports ----

    [Fact]
    public void Local_next_phase_repeat()
    {
        var (s, c, _, local) = Make(Open, Array.Empty<PlayerDef>());
        ToCombat(s, c);
        Assert.Equal("Pass to Red player", local.NextPhase(true));   // Blue's Combat back to Red's Movement
        Assert.Equal(("ph-move", 2), (Pos(s).PhaseId, Pos(s).Repeat));

        // The switch forwards the flag; false continues.
        var sw = new ActionPortSwitch(local);
        sw.NextPhase(); sw.NextPhase(true);   // Combat 2, then Round 3
        Assert.Equal(("ph-move", 3), (Pos(s).PhaseId, Pos(s).Repeat));
        sw.NextPhase(); sw.NextPhase(false);
        Assert.Equal("ph-end", Pos(s).PhaseId);
    }

    [Fact]
    public void Old_port_default_ignores_repeat()
    {
        var (s, c, _, local) = Make(Open, Array.Empty<PlayerDef>());
        ToCombat(s, c);
        IActionPort old = new OldPort(local);
        old.NextPhase(true);   // a port written before B5 has only NextPhase(): Continue
        Assert.Equal("ph-end", Pos(s).PhaseId);
    }

    [Fact]
    public void Next_phase_intent_without_repeat_keeps_json()
    {
        Assert.Equal("{\"$t\":\"nextPhase\"}", JsonSerializer.Serialize<GameIntent>(new NextPhaseIntent(), QuartermasterJson.Options));
        var json = JsonSerializer.Serialize<GameIntent>(Again, QuartermasterJson.Options);
        Assert.Equal("{\"$t\":\"nextPhase\",\"repeat\":true}", json);
        Assert.True(Assert.IsType<NextPhaseIntent>(JsonSerializer.Deserialize<GameIntent>(json, QuartermasterJson.Options)).Repeat);
        Assert.False(Assert.IsType<NextPhaseIntent>(JsonSerializer.Deserialize<GameIntent>("{\"$t\":\"nextPhase\"}", QuartermasterJson.Options)).Repeat);
    }

    [Fact]
    public void Remote_sends_repeat_flag()
    {
        // A real host and guest over the in-memory relay: Bob (Blue) plays Combat and chooses another Round.
        var (host, c, x, _) = Make(Open, new[] { Alice });
        ToCombat(host, c);
        var relay = new MemoryRelay();
        var d = new QueueDispatcher();
        var key = RandomNumberGenerator.GetBytes(32);
        var listing = new LobbyListing("", "Test lobby", "Alice", host.Archive.Sha256, "Fixture", new[] { "Red", "Blue" },
            new[] { "Blue" }, 1, 3, false, Protocol.ProtocolVersion);
        var h = new HostSession(host, x, new SealingTransport(relay.Host, key), "pw", "ABC234", listing, d.Post);
        _online.Add(h);
        d.Pump();
        var gs = ModuleSession.Open(_modulePath, Path.Combine(_tmp, "cache-bob"), Path.Combine(_tmp, "root-bob"));
        _sessions.Add(gs);
        var g = new GuestSession(gs, new SealingTransport(relay.Guest("m1"), key), "ABC234", "Bob", d.Post,
            new RejoinTokens(Path.Combine(_tmp, "tokens")));
        _online.Add(g);
        d.Pump();
        Assert.True(g.ChooseSides(new[] { "blue" }));
        d.Pump();
        Assert.Equal(GuestState.Playing, g.State);

        var notices = new List<string>();
        g.Notice += notices.Add;
        var remote = new RemoteActions(g);
        Assert.Null(remote.NextPhase(true));
        for (var i = 0; i < 2000 && gs.Game?.State.Position?.Repeat != 2; i++) { d.Pump(); Thread.Sleep(5); }   // the snapshot comes back
        Assert.Empty(notices);
        Assert.Equal(("ph-move", 2), (Pos(host).PhaseId, Pos(host).Repeat));
        Assert.Equal(("ph-move", 2), (gs.Game!.State.Position!.PhaseId, gs.Game.State.Position.Repeat));   // the snapshot followed
    }

    // ---- the host's checks of a guest's Next Phase ----

    [Fact]
    public void Guest_acting_side_repeat_applies()
    {
        var (s, c, x, _) = Make(Open);
        ToCombat(s, c);
        var actions = s.Game!.Actions.Count;
        var r = x.Execute("p2", Blue, Again);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(("ph-move", 2), (Pos(s).PhaseId, Pos(s).Repeat));
        Assert.Equal(actions + 1, s.Game.Actions.Count);   // one Next Phase action
        Assert.Equal("Next Phase", s.Game.UndoLabel);
    }

    [Fact]
    public void Guest_other_side_repeat_refused_by_phase_gate()
    {
        var (s, c, x, _) = Make(Open);
        ToCombat(s, c);
        var before = Json(s.Game!.State);
        var r = x.Execute("p1", Red, Again);
        Assert.False(r.Ok);
        Assert.Equal("Blue is playing Combat; wait for your phase.", r.Message);
        Assert.Equal(before, Json(s.Game.State));
    }

    [Fact]
    public void Guest_repeat_without_a_question_refused()
    {
        // The guest's word is not trusted: a repeat where the host would ask nothing (not the block's last phase, or its end entries
        // still to run) is refused and changes nothing.
        var (s, c, x, _) = Make(p => WithPhase(Open(p), "ph-combat",
            ph => ph with { OnEnd = new() { new StartEntry { FormulaId = "f-weather", ForEach = ForEachMode.Once } } }));
        c.Start(); c.Advance(); c.Advance();   // Movement (Red)
        var before = Json(s.Game!.State);
        var r = x.Execute("p1", Red, Again);
        Assert.False(r.Ok);
        Assert.Equal(IntentExecutor.NotUnderstoodText, r.Message);
        Assert.Equal(before, Json(s.Game.State));

        c.Advance();   // Combat (Blue): its end entries have not run yet
        before = Json(s.Game.State);
        r = x.Execute("p2", Blue, Again);
        Assert.False(r.Ok);
        Assert.Equal(IntentExecutor.NotUnderstoodText, r.Message);
        Assert.Equal(before, Json(s.Game.State));

        Assert.True(x.Execute("p2", Blue, new NextPhaseIntent()).Ok);   // runs the end entries and stays for the question
        Assert.Equal("ph-combat", Pos(s).PhaseId);
        Assert.True(x.Execute("p2", Blue, Again).Ok);
        Assert.Equal(("ph-move", 2), (Pos(s).PhaseId, Pos(s).Repeat));
    }

    [Fact]
    public void Guest_game_end_line_stamped_with_guest()
    {
        var (s, c, x, _) = Make(p => p with { LastTurn = 1 });
        c.Start();
        for (var i = 0; i < 20 && Pos(s).PhaseId != "ph-end"; i++) c.Advance();
        Assert.True(x.Execute("p2", Blue, new NextPhaseIntent()).Ok);   // End has no side: Bob's press ends the game
        Assert.NotNull(s.Game!.State.GameOver);
        var line = s.Game.State.Log[^1];
        Assert.StartsWith("Game over:", line.Text);
        Assert.Equal("p2", line.Who);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, line.At);
    }

    [Fact]
    public void Guest_next_phase_after_game_over_refused()
    {
        var (s, c, x, _) = Make(p => p with { LastTurn = 1 });
        c.Start();
        for (var i = 0; i < 20 && Pos(s).PhaseId != "ph-end"; i++) c.Advance();
        c.Advance();
        Assert.NotNull(s.Game!.State.GameOver);
        var before = Json(s.Game.State);
        foreach (var intent in new[] { new NextPhaseIntent(), Again })
        {
            var r = x.Execute("p1", Red, intent);
            Assert.False(r.Ok);
            Assert.Equal("The game is over.", r.Message);
        }
        Assert.Equal(before, Json(s.Game.State));
    }

    // ---- reacting guests ----

    [Fact]
    public void Reacting_guest_runs_unitless_formula()
    {
        var (s, c, x, _) = Make(Reacting);
        c.Start(); c.Advance();   // Supply (Red), Blue reacts
        var r = x.Execute("p2", Blue, new FormulaIntent("f-weather", null, null, new Dictionary<string, string>(), true));
        Assert.True(r.Ok, r.Message);
        Assert.Contains(s.Game!.State.Log, l => l.Who == "p2" && l.Text.Contains("Weather roll"));
    }

    [Fact]
    public void Reacting_guest_writes_global()
    {
        var (s, c, x, _) = Make(Reacting);
        c.Start(); c.Advance();   // Supply (Red), Blue reacts
        var r = x.Execute("p2", Blue, new FormulaIntent("f-gout", PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Ledger.GetOrDefault(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), s.Rules!.Properties.First(p => p.Id == "water")),
            Ledger.Get(s.Game.State, LedgerKey.Holder(HolderDef.GlobalId), "morale"));
    }

    [Fact]
    public void Reacting_guest_cannot_tick_or_advance()
    {
        var (s, c, x, _) = Make(Reacting);
        c.Start(); c.Advance();   // Supply (Red), Blue reacts
        var before = Json(s.Game!.State);
        foreach (var intent in new GameIntent[] { new TickIntent(0, true), new AckIntent(0), new NextPhaseIntent(), Again })
        {
            var r = x.Execute("p2", Blue, intent);
            Assert.False(r.Ok, intent.GetType().Name);
            Assert.Equal("Red is playing Supply; wait for your phase.", r.Message);
        }
        Assert.Equal(before, Json(s.Game.State));
    }

    [Fact]
    public void Non_reacting_guest_still_refused()
    {
        var (s, c, x, _) = Make(p => WithPhase(p, "ph-supply", ph => ph with { Reactors = new() { "ghost" } }));
        c.Start(); c.Advance();   // Supply (Red); an unknown reacting side widens nothing
        var before = Json(s.Game!.State);
        var run = x.Execute("p2", Blue, new FormulaIntent("f-weather", null, null, new Dictionary<string, string>(), true));
        Assert.False(run.Ok);
        Assert.Equal("That can't be calculated during another side's phase.", run.Message);
        var global = x.Execute("p2", Blue, new FormulaIntent("f-gout", PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true));
        Assert.False(global.Ok);
        Assert.Equal("Global values can't be changed during another side's phase.", global.Message);
        Assert.Equal(before, Json(s.Game.State));
    }

    /// <summary>A port written before B5: it implements only <see cref="IActionPort.NextPhase()"/>.</summary>
    private sealed class OldPort(IActionPort inner) : IActionPort
    {
        public bool IsRemote => inner.IsRemote;
        public void Move(IReadOnlyList<MovePieces> moves) => inner.Move(moves);
        public void PieceCommand(GameCommand command) => inner.PieceCommand(command);
        public void PieceCommands(IReadOnlyList<GameCommand> commands) => inner.PieceCommands(commands);
        public FormulaVerdict ApplyFormula(FormulaIntent intent) => inner.ApplyFormula(intent);
        public Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent) => inner.ApplyFormulaAsync(intent);
        public bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated) => inner.ApplyTrace(runId, pieceId, trace, calculated);
        public (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values) =>
            inner.ApplyMissingInputs(run, values);
        public void EditLedger(string key, string propertyId, double after, string note) => inner.EditLedger(key, propertyId, after, note);
        public void Roll(DiceSpec spec, string purpose) => inner.Roll(spec, purpose);
        public void RecordRoll(RollDice roll) => inner.RecordRoll(roll);
        public void Tick(int index, bool ticked) => inner.Tick(index, ticked);
        public void Ack(int warningIndex) => inner.Ack(warningIndex);
        public string? NextPhase() => inner.NextPhase();
        public void HideAlert(string alertId, string? name) => inner.HideAlert(alertId, name);
        public void Chat(string text) => inner.Chat(text);
        public void SetTurn(int turn, string? phase) => inner.SetTurn(turn, phase);
        public void SetNotes(string text) => inner.SetNotes(text);
        public void Undo() => inner.Undo();
        public void Redo() => inner.Redo();
        public void StepBack(CheckpointRow row) => inner.StepBack(row);
    }
}
