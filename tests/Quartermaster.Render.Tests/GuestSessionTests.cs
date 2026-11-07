using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using NetResult = Quartermaster.Net.IntentResult;

namespace Quartermaster.Render.Tests;

/// <summary>
/// The guest's online session against a real <see cref="HostSession"/> over an in-memory relay; payloads between them are sealed with
/// the game key exactly as <see cref="RelayTransport"/> does. Invented Red/Blue sides only.
/// </summary>
public class GuestSessionTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-guest-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private readonly List<IDisposable> _online = new();

    internal const string SecretName = "Red Phantom Battery";
    internal const string RedMap = "Red Command Post";
    internal const double SecretStock = 424242;
    internal const int SecretRoll = 8461;
    internal const double SecretFuel = 71717;
    private const string Code = "ABC234";
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });

    public GuestSessionTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var o in _online) o.Dispose();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    internal static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
            }).Select(x => x.Id == "fuel" ? x with { Visibility = Visibility.Private } : x).ToList(),
            Holders = new() { new HolderDef { Id = "reddepot", Name = "Red Depot", SideId = "red" } },
            MapOwners = new() { new MapOwner { MapId = RedMap, SideId = "red" } },
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-secret", Name = "Secret Attack", Text = "R", PrivateRolls = true,
                    Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 10000 } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Set } },
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

    /// <summary>Red's secrets in the host's game: a unit on a Red-owned map, private fuel, a private depot stock, a private roll.</summary>
    internal static void AddRedSecrets(ModuleSession s)
    {
        var g = s.Game!;
        var type = PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;" + SecretName });
        g.Execute(new AddPiece("secret1", type, PieceTypeParser.EncodeState(new[] { "" }), RedMap, new PointD(10, 10), null, SecretName + " placed", Who: "Alice"));
        g.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedOne), "fuel", 40, SecretFuel, "manual", null, null, null, null, null, "Alice"));
        g.Execute(new LedgerDelta(LedgerKey.Holder("reddepot"), "supply", 0, SecretStock, "manual", null, null, null, null, null, "Alice"));
        var runner = new FormulaRunner(s.Rules!, s.Model, (n, _) => Enumerable.Repeat(SecretRoll, n).ToList());
        var trace = runner.Run(g.State, new RunRequest("f-secret", PhaseFixture.RedTwo, new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Null(trace.Error);
        foreach (var cmd in runner.Commands(trace, null, "manual", null, "red")) g.Execute(cmd);
    }

    internal static readonly string[] SecretMarkers =
        { SecretName, SecretStock.ToString(), SecretFuel.ToString(), SecretRoll.ToString(), "secret1" };

    private sealed class Rig
    {
        public required ModuleSession Host { get; init; }
        public required HostSession H { get; init; }
        public required MemoryRelay Relay { get; init; }
        public required QueueDispatcher D { get; init; }
        public required byte[] Key { get; init; }
        public required SealingTransport HostT { get; init; }
        public required string ModulePath { get; init; }
        public TimeSpan Offset;
    }

    private Rig Open(Difficulty difficulty = Difficulty.FullyAuto)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var path = fx.Build();
        var s = Session(path, "host");
        s.SetRoller((n, sides) => Enumerable.Repeat(sides == 10000 ? SecretRoll : 4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], difficulty, new[] { Alice });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        AddRedSecrets(s);
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var relay = new MemoryRelay();
        var d = new QueueDispatcher();
        var key = RandomNumberGenerator.GetBytes(32);
        var listing = new LobbyListing("", "Test lobby", "Alice", s.Archive.Sha256, "Fixture", new[] { "Red", "Blue" },
            new[] { "Blue" }, 1, 3, false, Protocol.ProtocolVersion);
        var hostT = new SealingTransport(relay.Host, key);
        var h = new HostSession(s, new IntentExecutor(s, c), hostT, "pw", Code, listing, d.Post);
        _online.Add(h);
        d.Pump();
        var rig = new Rig { Host = s, H = h, Relay = relay, D = d, Key = key, ModulePath = path, HostT = hostT };
        h.Now = () => DateTime.UtcNow + rig.Offset;
        return rig;
    }

    private ModuleSession Session(string modulePath, string name)
    {
        var s = ModuleSession.Open(modulePath, Path.Combine(_tmp, "cache-" + name + _sessions.Count), Path.Combine(_tmp, "root-" + name + _sessions.Count));
        _sessions.Add(s);
        return s;
    }

    private sealed record Guest(ModuleSession S, GuestSession G, RemoteActions A, SealingTransport T, MemoryTransport Raw, List<string> Notices);

    private Guest Join(Rig r, string member = "m1", string name = "Bob", string? tokensRoot = null, byte[]? key = null, ModuleSession? reuse = null)
    {
        var s = reuse ?? Session(r.ModulePath, name);
        var raw = r.Relay.Guest(member);
        var t = new SealingTransport(raw, key ?? r.Key);
        var g = new GuestSession(s, t, Code, name, r.D.Post, new RejoinTokens(tokensRoot ?? Path.Combine(_tmp, "tokens-" + name)));
        _online.Add(g);
        var notices = new List<string>();
        g.Notice += notices.Add;
        var a = new RemoteActions(g);
        a.Refused += notices.Add;
        r.D.Pump();
        return new Guest(s, g, a, t, raw, notices);
    }

    private static void Pick(Rig r, Guest g, string side = "blue")
    {
        Assert.True(g.G.ChooseSides(new[] { side }));
        r.D.Pump();
    }

    private static string Json(GameState? st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    private static T Await<T>(Rig r, Task<T> task)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < end) { r.D.Pump(); Thread.Sleep(5); }
        Assert.True(task.IsCompleted, "the task did not complete");
        return task.Result;
    }

    // ---- privacy ----

    [Fact]
    public void Guest_ReceivedBytes_ContainNoPrivateData()
    {
        var r = Open(Difficulty.Hardcore);
        var bob = Join(r);
        Assert.Equal(GuestState.ChoosingSide, bob.G.State);
        Pick(r, bob);
        Assert.Equal(GuestState.Playing, bob.G.State);

        // A scripted game: Bob moves, edits, rolls, chats, runs a formula with a hidden input; the host plays Red privately.
        var b1 = bob.S.Game!.State.Pieces[PhaseFixture.BlueOne];
        bob.A.Move(new[] { new MovePieces(new[] { PhaseFixture.BlueOne }, b1.MapId, new PointD(150, 50), null) });
        bob.A.EditLedger(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 7, "refilled");
        bob.A.Roll(new DiceSpec(2, 6, 0), "Scouting");
        bob.A.Chat("Blue ready");
        r.D.Pump();
        var verdict = Await(r, bob.A.ApplyFormulaAsync(new FormulaIntent("f-mix", PhaseFixture.BlueOne, null,
            new Dictionary<string, string> { ["W"] = "7", ["S"] = "1" }, false)));
        Assert.Equal(FormulaBox.Hidden, verdict.Boxes.Single(b => b.Var == "S").State);
        // Another side's unit and a hidden one: refused, nothing about them comes back.
        bob.A.EditLedger(LedgerKey.Piece(PhaseFixture.RedOne), "fuel", 1, "drain");
        bob.A.Move(new[] { new MovePieces(new[] { "secret1" }, "Main Map", new PointD(1, 1), null) });
        r.D.Pump();
        Assert.True(bob.Notices.Count >= 2);

        // Red plays on: more private data, a rules change, a new snapshot.
        var hg = r.Host.Game!;
        hg.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedOne), "fuel", SecretFuel, SecretFuel + 1, "manual", null, null, null, null, null, "Alice"));
        r.Host.UpdateRules(p => p with { Name = "Edited" });
        r.H.FlushSnapshots();
        r.D.Pump();
        // Blue's own values arrive (a formula output computed from a hidden input is Blue's data; the input itself never travels).
        Assert.Equal(Ledger.Get(hg.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"), Ledger.Get(bob.S.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        Assert.Equal("Edited", bob.S.Rules!.Name);
        Assert.Contains(bob.S.Game!.State.Log, l => l.Kind == LogKind.Chat && l.Text.Contains("Blue ready"));

        // Every byte Bob received: sealed, opened, unzipped snapshots, results, verdicts; plus his own copy of the game.
        var all = bob.T.AllReceivedText() + "\n" + Json(bob.S.Game!.State) + "\n" + string.Join("\n", bob.Notices);
        Assert.True(bob.T.SealedReceived.Count > 5);
        foreach (var marker in SecretMarkers.Append((SecretFuel + 1).ToString()))
        {
            Assert.DoesNotContain(marker, all);
            Assert.Contains(marker, Json(r.Host.Game!.State));   // the test would prove nothing otherwise
        }
        Assert.Contains("Blue 1", all);   // the guest did see its own side
    }

    [Fact]
    public void PlayerView_FormulaWithHiddenHolderInput_WorkingRedacted()
    {
        var r = Open();
        var s = r.Host;
        var g = s.Game!;
        var trace = s.Runner!.Run(g.State, new RunRequest("f-mix", PhaseFixture.BlueOne, new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Null(trace.Error);
        Assert.Contains("424242", trace.Working);
        foreach (var cmd in s.Runner.Commands(trace, null, sideId: "blue")) g.Execute(cmd);

        var blueView = PlayerView.For(g.State, s.Rules, s.Model, new HashSet<string> { "blue" });
        Assert.DoesNotContain("424242", Json(blueView));
        Assert.Contains(blueView.Log, l => l.Text.Contains("water 10 → 424252"));   // Blue's own result stays visible
        Assert.Contains(blueView.LedgerHistory, e => e.FormulaId == "f-mix" && e.Working == null && e.After == 424252);
        var red = Json(PlayerView.For(g.State, s.Rules, s.Model, new HashSet<string> { "red", "blue" }));
        Assert.Contains("424242", red);                                  // a viewer holding the depot sees the working
    }

    // ---- guest mode ----

    [Fact]
    public void Guest_Snapshot_UndoDisabled_SaveDisabled()
    {
        var r = Open();
        var bob = Join(r);
        Pick(r, bob);
        var s = bob.S;
        Assert.True(s.IsGuest);
        Assert.NotNull(s.Game);
        Assert.Null(s.ViewSides);                                   // the snapshot is already filtered
        Assert.Equal(new[] { "blue" }, s.GuestSides!.ToArray());
        Assert.Equal(Json(PlayerView.For(r.Host.Game!.State, r.Host.Rules, r.Host.Model, new HashSet<string> { "blue" })), Json(s.Game!.State));
        Assert.Same(s.Game!.State, s.View);
        Assert.False(s.Game!.CanUndo);
        Assert.Empty(s.Game!.Log);
        Assert.False(s.CanSave);
        var ex = Assert.Throws<InvalidOperationException>(() => s.SaveNative(Path.Combine(_tmp, "x.qmsave")));
        Assert.Equal(ModuleSession.GuestSaveText, ex.Message);
        Assert.False(File.Exists(Path.Combine(_tmp, "x.qmsave")));
        Assert.Null(s.RulesPath);                                   // the host's pack is in memory only
        Assert.Equal(r.Host.Rules!.Id, s.Rules!.Id);

        bob.A.Undo();
        bob.A.StepBack(new CheckpointRow(0, 1, null, null, "game start", null, 0));
        bob.A.SetTurn(3, null);
        Assert.Equal(new[] { ModuleSession.GuestUndoText, ModuleSession.GuestUndoText, IntentExecutor.HostOnlyText }, bob.Notices);
        Assert.Throws<InvalidOperationException>(() => s.StartNewGame());

        // A change on the host arrives as a newer snapshot; still no history.
        bob.A.EditLedger(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 5, "x");
        r.D.Pump();
        r.H.FlushSnapshots();
        r.D.Pump();
        Assert.Equal(5, Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        Assert.False(s.Game!.CanUndo);

        // Stale snapshots are ignored.
        var version = bob.G.SnapshotVersion;
        var old = Gzip.Compress(JsonSerializer.SerializeToUtf8Bytes(new GameState(), QuartermasterJson.Options));
        bob.T.InjectPlain(Framing.Encode<GamePayload>(new Snapshot(version, old)));
        r.D.Pump();
        Assert.Equal(5, Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public void Guest_ViewToggles_LocalOnly_SurviveSnapshots_FreshIdTrusted()
    {
        var r = Open();
        var bob = Join(r);
        Pick(r, bob);
        var hostBefore = Json(r.Host.Game!.State);
        var sentBefore = bob.Raw.SentCount;
        bob.A.PieceCommand(new ToggleTrail(PhaseFixture.BlueOne));
        Assert.Equal(sentBefore, bob.Raw.SentCount);                // never sent
        Assert.True(bob.S.Game!.State.Pieces[PhaseFixture.BlueOne].TrailVisible);
        var stackId = bob.S.Game!.State.Pieces[PhaseFixture.BlueOne].StackId;
        if (stackId != null) bob.A.PieceCommands(new GameCommand[] { new SetStackExpanded(stackId, true) });
        Assert.Equal(hostBefore, Json(r.Host.Game!.State));

        // The next snapshot keeps Bob's own toggles.
        r.Host.Game!.Execute(new AddLogLine(LogKind.System, "later"));
        r.H.FlushSnapshots();
        r.D.Pump();
        Assert.Contains(bob.S.Game!.State.Log, l => l.Text == "later");
        Assert.True(bob.S.Game!.State.Pieces[PhaseFixture.BlueOne].TrailVisible);
        if (stackId != null) Assert.True(bob.S.Game!.State.Stacks[stackId].Expanded);

        // An added piece whose id is taken comes back under the host's fresh id: the snapshot is the truth.
        var b1 = bob.S.Game!.State.Pieces[PhaseFixture.BlueOne];
        bob.A.PieceCommand(new AddPiece(PhaseFixture.BlueOne, b1.TypeString, "", b1.MapId, new PointD(170, 50), null));
        r.D.Pump();
        Assert.Empty(bob.Notices);
        r.H.FlushSnapshots();
        r.D.Pump();
        Assert.Equal(2, r.Host.Game!.State.Pieces.Count(p => p.Value.TypeString == b1.TypeString));
        Assert.Equal(r.Host.Game!.State.Pieces.Count(p => p.Value.TypeString == b1.TypeString),
            bob.S.Game!.State.Pieces.Count(p => p.Value.TypeString == b1.TypeString));
    }

    // ---- rejoin, host drop ----

    [Fact]
    public void Guest_Rejoin_GetsCurrentView()
    {
        var r = Open();
        var tokens = Path.Combine(_tmp, "tokens-shared");
        var bob = Join(r, tokensRoot: tokens);
        Pick(r, bob);
        var playerId = bob.G.PlayerId;
        Assert.NotNull(new RejoinTokens(tokens).Get(Code));
        Assert.True(File.Exists(Path.Combine(tokens, "online", "rejoin-" + Code + ".token")));

        // Bob's app goes away; the game moves on; Bob comes back on a new connection with the stored token.
        bob.G.Dispose();
        r.Relay.Drop("m1");
        r.D.Pump();
        r.Host.Game!.Execute(new AddLogLine(LogKind.System, "while you were away"));
        r.Offset += TimeSpan.FromSeconds(5);
        var again = Join(r, member: "m2", tokensRoot: tokens);
        Assert.Equal(playerId, again.G.PlayerId);
        Assert.Equal(GuestState.Playing, again.G.State);
        Assert.Equal(new[] { "blue" }, again.S.GuestSides!.ToArray());
        Assert.Contains(again.S.Game!.State.Log, l => l.Text == "while you were away");
        Assert.Equal(2, r.H.Seats.All.Count);

        // The guest's own relay connection drops and comes back: it rejoins with the token and gets a fresh snapshot.
        again.T.RaiseDisconnected("lost");
        r.D.Pump();
        Assert.Equal(GuestState.Paused, again.G.State);
        Assert.Equal(GuestSession.RelayLostText, again.G.StatusText);
        r.Host.Game!.Execute(new AddLogLine(LogKind.System, "after the blip"));
        r.Offset += TimeSpan.FromSeconds(5);
        again.T.RaiseReconnected();
        r.D.Pump();
        Assert.Equal(GuestState.Playing, again.G.State);
        Assert.Contains(again.S.Game!.State.Log, l => l.Text == "after the blip");
    }

    [Fact]
    public void Host_Drop_GuestsPaused()
    {
        var r = Open();
        var bob = Join(r);
        Pick(r, bob);
        var changes = 0;
        bob.G.StateChanged += () => changes++;

        // A formula waiting on the host when it drops completes with "didn't answer".
        r.H.Dispose();   // the host stops answering
        var pending = bob.A.ApplyFormulaAsync(new FormulaIntent("f-water", PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true));
        bob.T.RaiseHostLeft();
        r.D.Pump();
        Assert.Equal(GuestState.Paused, bob.G.State);
        Assert.Equal(GuestSession.HostGoneText, bob.G.StatusText);
        Assert.True(changes > 0);
        Assert.Equal(GuestSession.NoAnswerText, Await(r, pending).Message);
        Assert.NotNull(bob.S.Game);                                       // the last view stays on screen

        // Actions while paused are refused locally, nothing is sent.
        var sent = bob.Raw.SentCount;
        bob.A.Chat("anyone?");
        r.D.Pump();
        Assert.Equal(sent, bob.Raw.SentCount);
        Assert.Contains(GuestSession.HostGoneText, bob.Notices);

        // The host re-attaches (a new host session over the same seats is the App's job; here the same room re-hosts): rejoin with the token.
        var c = new PhaseController(r.Host, (cmd, st) => CommandLog.Prepare(cmd, st, r.Host));
        var listing = new LobbyListing("", "Test lobby", "Alice", r.Host.Archive.Sha256, "Fixture", new[] { "Red", "Blue" },
            new[] { "Blue" }, 1, 3, false, Protocol.ProtocolVersion);
        var h2 = new HostSession(r.Host, new IntentExecutor(r.Host, c), new SealingTransport(r.Relay.Host, r.Key), "pw", Code, listing, r.D.Post);
        _online.Add(h2);
        r.D.Pump();
        bob.T.RaiseHostBack();
        r.D.Pump();
        // A brand-new host session does not know the old token: the guest is welcomed as a new player and chooses again.
        Assert.Equal(GuestState.ChoosingSide, bob.G.State);
        Assert.Null(bob.G.StatusText);
    }

    [Fact]
    public void Host_Back_SameHostSession_RejoinSameSeat_FreshSnapshot()
    {
        var r = Open();
        var bob = Join(r);
        Pick(r, bob);
        var playerId = bob.G.PlayerId;
        bob.T.RaiseHostLeft();
        r.D.Pump();
        Assert.Equal(GuestState.Paused, bob.G.State);
        r.Host.Game!.Execute(new AddLogLine(LogKind.System, "host is back"));
        r.Offset += TimeSpan.FromSeconds(5);
        bob.T.RaiseHostBack();
        r.D.Pump();
        Assert.Equal(GuestState.Playing, bob.G.State);
        Assert.Equal(playerId, bob.G.PlayerId);
        Assert.Contains(bob.S.Game!.State.Log, l => l.Text == "host is back");
        Assert.True(bob.G.SnapshotVersion > 0);
        Assert.Equal(2, r.H.Seats.All.Count);
    }

    [Fact]
    public void Formula_Timeout_HostDidntAnswer_LateResultIgnored()
    {
        var r = Open();
        var bob = Join(r);
        Pick(r, bob);
        bob.G.IntentTimeout = TimeSpan.FromMilliseconds(50);
        // Hold the host's replies back: the guest's transport loses incoming payloads for a while.
        bob.T.Hold = true;
        var task = bob.A.ApplyFormulaAsync(new FormulaIntent("f-water", PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true));
        r.D.Pump();
        var v = Await(r, task);
        Assert.False(v.Applied);
        Assert.Equal(GuestSession.NoAnswerText, v.Message);
        var dropped = bob.G.Dropped;
        bob.T.Release();
        r.D.Pump();
        Assert.True(bob.G.Dropped > dropped);   // the late answer was ignored
    }

    // ---- refusals and garbage ----

    [Fact]
    public void GarbageFrame_Ignored()
    {
        var r = Open();
        var bob = Join(r);
        Pick(r, bob);
        var view = Json(bob.S.Game!.State);
        var dropped = bob.G.Dropped;
        var malformed = bob.G.Malformed;
        var version = bob.G.SnapshotVersion;

        bob.T.InjectSealed(RandomNumberGenerator.GetBytes(200));                      // not sealed with the key: dropped by the transport
        bob.T.InjectSealed(Secrets.Seal(RandomNumberGenerator.GetBytes(32), Encoding.UTF8.GetBytes("{}")));
        bob.T.InjectPlain(Array.Empty<byte>());
        bob.T.InjectPlain(Encoding.UTF8.GetBytes("{\"$t\":\"Nope\"}"));
        bob.T.InjectPlain(RandomNumberGenerator.GetBytes(300));
        bob.T.InjectPlain(Framing.Encode<GamePayload>(new IntentMsg(Guid.NewGuid(), "{}")));          // guest-bound never
        bob.T.InjectPlain(Framing.Encode<GamePayload>(new Snapshot(version + 1, new byte[] { 1, 2, 3 })));   // not gzip
        bob.T.InjectPlain(Framing.Encode<GamePayload>(new Snapshot(version + 2, Gzip.Compress(Encoding.UTF8.GetBytes("[1,2]")))));
        bob.T.InjectPlain(Framing.Encode<GamePayload>(new Snapshot(version + 3, Gzip.Compress(Encoding.UTF8.GetBytes("{\"pieces\":null}")))));
        // A gzip bomb: inflates past the frame cap.
        bob.T.InjectPlain(Framing.Encode<GamePayload>(new Snapshot(version + 4, Gzip.Compress(new byte[Framing.MaxFrame + 1024]))));
        bob.T.InjectPlain(Framing.Encode<GamePayload>(new RulesChanged(new byte[] { 9, 9, 9 })));
        bob.T.InjectPlain(Framing.Encode<GamePayload>(new NetResult(Guid.NewGuid(), true, null, null)));   // unknown id
        r.D.Pump();

        Assert.Equal(3, bob.T.DroppedSealed);   // random bytes, another key, an empty payload
        Assert.Equal(dropped + 4, bob.G.Dropped);
        Assert.Equal(malformed + 5, bob.G.Malformed);
        Assert.Equal(version, bob.G.SnapshotVersion);
        Assert.Equal(view, Json(bob.S.Game!.State));
        Assert.Equal(GuestState.Playing, bob.G.State);
        Assert.Equal(r.Host.Rules!.Id, bob.S.Rules!.Id);
    }

    [Fact]
    public void WrongPassword_Refused()
    {
        // The relay refuses a wrong verifier (RelayEndToEndTests); should frames ever arrive with another key, nothing is readable either way.
        var r = Open();
        var eve = Join(r, member: "m6", name: "Eve", key: RandomNumberGenerator.GetBytes(32));
        Assert.Equal(GuestState.Joining, eve.G.State);
        Assert.Single(r.H.Seats.All);                    // the host could not read the join
        Assert.Equal(1, r.HostT.DroppedSealed);
        Assert.Null(eve.G.PlayerId);
        Assert.Null(eve.S.Game);
        Assert.Empty(eve.T.Opened);
    }

    [Fact]
    public void ProtocolMismatch_Refused()
    {
        var r = Open();
        // A newer guest (protocol 2) reaching this host: refused with the clear message, no retry.
        var newer = r.Relay.Guest("m3");
        var sealedNewer = new SealingTransport(newer, r.Key);
        sealedNewer.Send(null, Framing.Encode<GamePayload>(new JoinRequest("Zed", null, r.Host.Archive.Sha256, 2)));
        r.D.Pump();
        var kicked = Assert.IsType<Kicked>(Framing.Decode<GamePayload>(Assert.Single(sealedNewer.Opened)));
        Assert.Equal(Protocol.MismatchMessage(2), kicked.Reason);

        // This guest, told it is refused, shows the reason and never retries.
        var bob = Join(r);
        bob.T.InjectPlain(Framing.Encode<GamePayload>(new Kicked(Protocol.MismatchMessage(2))));
        r.D.Pump();
        Assert.Equal(GuestState.Refused, bob.G.State);
        Assert.Equal(Protocol.MismatchMessage(2), bob.G.StatusText);
        var sent = bob.Raw.SentCount;
        bob.T.RaiseHostBack();
        bob.T.RaiseReconnected();
        r.D.Pump();
        Assert.Equal(sent, bob.Raw.SentCount);
        Assert.Equal(GuestState.Refused, bob.G.State);
    }

    [Fact]
    public void Join_DifferentModule_Refused_ReasonShown()
    {
        var r = Open();
        var other = PhaseFixture.Module(numbered: true);
        _fixtures.Add(other);
        var s = Session(other.Build(), "other");
        Assert.NotEqual(s.Archive.Sha256, r.Host.Archive.Sha256);
        var bob = Join(r, reuse: s);
        Assert.Equal(GuestState.Refused, bob.G.State);
        Assert.Equal(HostSession.DifferentModuleText, bob.G.StatusText);
    }

    [Fact]
    public void ChooseSide_Refusal_Notice_ChatAllowedBeforeSide()
    {
        var r = Open();
        var bob = Join(r);
        bob.A.EditLedger(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 2, "x");
        Assert.Equal(HostSession.ChooseSideFirstText, bob.Notices.Last());
        bob.A.Chat("hello");
        r.D.Pump();
        Assert.Contains(r.Host.Game!.State.Log, l => l.Kind == LogKind.Chat && l.Text == "Bob: hello");
        Assert.True(bob.G.ChooseSides(new[] { "red" }));   // held by the host
        r.D.Pump();
        Assert.Equal("Red is held by more than one player.", bob.Notices.Last());
        Assert.Equal(GuestState.ChoosingSide, bob.G.State);
    }

    [Fact]
    public void ViewerCopy_OpensReadOnly()
    {
        var r = Open();
        var bob = Join(r);
        Pick(r, bob);
        var path = Path.Combine(_tmp, "view.qmsave");
        bob.S.ExportViewerCopy(path);
        var text = File.ReadAllText(path);
        foreach (var marker in SecretMarkers) Assert.DoesNotContain(marker, text);
        Assert.Contains("\"viewerCopy\": true".Replace(" ", ""), text.Replace(" ", ""));

        // Opened anywhere (here a solo session of the same module): read-only, a banner, every action blocked.
        var s = Session(r.ModulePath, "viewer");
        s.NewRules("v");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        s.LoadNative(path);
        Assert.True(s.IsViewerCopy);
        Assert.Equal(ModuleSession.ViewerCopyText, s.ViewerBanner);
        Assert.False(s.CanSave);
        Assert.Throws<InvalidOperationException>(() => s.SaveNative(Path.Combine(_tmp, "again.qmsave")));
        Assert.Null(s.ViewSides);
        Assert.False(s.Game!.CanUndo);
        var before = Json(s.Game!.State);
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var port = new LocalActions(s, c, new HistoryController(s, c, (cmd, st) => CommandLog.Prepare(cmd, st, s)));
        var b1 = s.Game!.State.Pieces[PhaseFixture.BlueOne];
        port.Move(new[] { new MovePieces(new[] { PhaseFixture.BlueOne }, b1.MapId, new PointD(160, 50), null) });
        port.PieceCommand(new ToggleTrail(PhaseFixture.BlueOne));
        port.EditLedger(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 1, "x");
        port.Roll(new DiceSpec(1, 6, 0), "r");
        port.Chat("hi");
        port.SetNotes("n");
        Assert.Null(port.NextPhase());
        Assert.False(port.ApplyFormula(new FormulaIntent("f-water", PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true)).Applied);
        s.SeedLedgers();
        Assert.Equal(before, Json(s.Game!.State));
        Assert.Empty(s.Game!.Log);

        // An ordinary save is unaffected: no flag in the file, loads as before.
        r.Host.SaveNative(Path.Combine(_tmp, "host.qmsave"));
        Assert.DoesNotContain("viewerCopy", File.ReadAllText(Path.Combine(_tmp, "host.qmsave")));
        var s2 = Session(r.ModulePath, "plain");
        s2.LoadNative(Path.Combine(_tmp, "host.qmsave"));
        Assert.False(s2.IsViewerCopy);
        Assert.True(s2.CanSave);
        Assert.Null(s2.ViewerBanner);
    }

    [Fact]
    public void Solo_Unchanged_NotGuest()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = Session(fx.Build(), "solo");
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        Assert.False(s.IsGuest);
        Assert.False(s.IsViewerCopy);
        Assert.True(s.CanSave);
        Assert.Null(s.GuestSides);
        Assert.Throws<InvalidOperationException>(() => s.ApplySnapshot(new GameState()));
        Assert.Throws<InvalidOperationException>(() => s.ExportViewerCopy(Path.Combine(_tmp, "nope.qmsave")));
    }

    [Fact]
    public void RejoinTokens_PerCode_Atomic_Tolerant()
    {
        var root = Path.Combine(_tmp, "tok");
        var t = new RejoinTokens(root);
        Assert.Null(t.Get("ABC234"));
        t.Set("abc-234", "00ff");
        Assert.Equal("00ff", t.Get("ABC234"));
        Assert.Null(t.Get("XYZ999"));
        t.Set("../evil", "11");
        Assert.Null(t.Get("../evil"));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "online"), "*.tmp"));
        File.WriteAllText(Path.Combine(root, "online", "rejoin-ABC234.token"), "not a token!");
        Assert.Null(t.Get("ABC234"));
        t.Forget("ABC234");
        Assert.Null(t.Get("ABC234"));
    }
}

/// <summary>
/// Wraps a <see cref="MemoryTransport"/> so payloads travel sealed with the game key, as over the relay: outgoing payloads are sealed,
/// incoming ones opened with <see cref="RelayTransport.TryOpen"/> (undecryptable frames dropped). Records every sealed and opened byte.
/// </summary>
internal sealed class SealingTransport : ITransport
{
    private readonly MemoryTransport _inner;
    private readonly byte[] _key;
    private readonly List<(string From, byte[] Sealed)> _held = new();

    public SealingTransport(MemoryTransport inner, byte[] key)
    {
        _inner = inner;
        _key = key;
        // The memory relay hands over what the other end sent: sealed bytes.
        inner.Received += (from, sealed_) => Arrive(from, sealed_);
        inner.MemberLeft += id => MemberLeft?.Invoke(id);
        inner.Disconnected += why => Disconnected?.Invoke(why);
        inner.Reconnected += () => Reconnected?.Invoke();
        inner.MembersListed += ids => MembersListed?.Invoke(ids);
        inner.HostLeft += () => HostLeft?.Invoke();
        inner.HostBack += () => HostBack?.Invoke();
    }

    public List<byte[]> SealedReceived { get; } = new();
    public List<byte[]> Opened { get; } = new();
    public int DroppedSealed { get; private set; }

    /// <summary>While set, arriving frames are held back (a slow host); <see cref="Release"/> delivers them.</summary>
    public bool Hold { get; set; }

    public void Release()
    {
        Hold = false;
        foreach (var (from, s) in _held.ToList()) Arrive(from, s);
        _held.Clear();
    }

    private void Arrive(string from, byte[] sealed_)
    {
        if (Hold) { _held.Add((from, sealed_)); return; }
        SealedReceived.Add(sealed_);
        if (RelayTransport.TryOpen(_key, sealed_) is not { } plain) { DroppedSealed++; return; }
        Opened.Add(plain);
        Received?.Invoke(from, plain);
    }

    public Task Send(string? to, byte[] payload) => _inner.Send(to, Secrets.Seal(_key, payload));

    public Task UpdateListing(LobbyListing listing) => _inner.UpdateListing(listing);

    public void InjectSealed(byte[] sealed_) => Arrive(MemoryRelay.HostMember, sealed_);

    public void InjectPlain(byte[] plain) => Arrive(MemoryRelay.HostMember, Secrets.Seal(_key, plain));

    public void RaiseDisconnected(string why) => _inner.RaiseDisconnected(why);
    public void RaiseReconnected() => _inner.RaiseReconnected();
    public void RaiseHostLeft() => _inner.RaiseHostLeft();
    public void RaiseHostBack() => _inner.RaiseHostBack();

    /// <summary>Everything received as text: the sealed bytes, the opened payloads, unzipped snapshots and the unpacked rules packs.</summary>
    public string AllReceivedText()
    {
        var sb = new StringBuilder();
        foreach (var s in SealedReceived) sb.Append(Encoding.UTF8.GetString(s)).Append('\n');
        foreach (var raw in Opened)
        {
            sb.Append(Encoding.UTF8.GetString(raw)).Append('\n');
            GamePayload msg;
            try { msg = Framing.Decode<GamePayload>(raw); }
            catch (ProtocolException) { continue; }
            switch (msg)
            {
                case Snapshot snap:
                    try { sb.Append(Encoding.UTF8.GetString(Gzip.DecompressBounded(snap.StateJsonGz, Framing.MaxFrame))).Append('\n'); }
                    catch (CryptographicException) { }
                    break;
                case Welcome w: AppendZip(sb, w.RulesPack); break;
                case RulesChanged rc: AppendZip(sb, rc.RulesPack); break;
            }
        }
        return sb.ToString();
    }

    private static void AppendZip(StringBuilder sb, byte[] zip)
    {
        try
        {
            using var z = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            foreach (var e in z.Entries)
                using (var rd = new StreamReader(e.Open())) sb.Append(rd.ReadToEnd()).Append('\n');
        }
        catch (InvalidDataException) { }
    }

    public event Action<string, byte[]>? Received;
    public event Action<string>? MemberLeft;
    public event Action<string>? Disconnected;
    public event Action? Reconnected;
    public event Action<IReadOnlyList<string>>? MembersListed;
    public event Action? HostLeft;
    public event Action? HostBack;
}
