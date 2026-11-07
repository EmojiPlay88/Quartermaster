using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Turn files between two sessions of the phase fixture module; invented players Alice (red) and Bob (blue); temp roots only.</summary>
[Collection(AppDataRootEnv.Name)]
public class TurnTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-turns-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public TurnTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });

    /// <summary>Reversible fake: tests never touch DPAPI.</summary>
    private sealed class FakeProtector : IPasswordProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).Prepend((byte)0xEE).ToArray();
        public byte[] Unprotect(byte[] p) => p[0] == 0xEE ? p.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray() : throw new InvalidDataException();
    }

    private string Root(string who) => Path.Combine(_tmp, "turns-" + who);

    private TurnIdentity Identity(string who, string gameId, string? player = null)
    {
        var id = TurnIdentity.Load(gameId, Root(who), new FakeProtector());
        if (player != null) id.LocalPlayerId = player;
        return id;
    }

    private ModuleSession Open()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        return s;
    }

    /// <summary>Alice's computer: a fresh two-player game.</summary>
    private ModuleSession NewGame()
    {
        var s = Open();
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        return s;
    }

    private static string AnyPiece(GameSession g) => g.State.Pieces.Keys.OrderBy(k => k, StringComparer.Ordinal).First();

    private static void MoveBy(GameSession g, string id, double dx)
    {
        var p = g.State.Pieces[id];
        using (g.BeginAction()) g.Execute(new MovePieces(new[] { id }, p.MapId, new PointD(p.Pos.X + dx, p.Pos.Y), null));
    }

    /// <summary>Alice plays two moves and a roll, then builds her turn for Bob.</summary>
    private (ModuleSession S, TurnFile T) AliceTurn()
    {
        var s = NewGame();
        var g = s.Game!;
        var id = AnyPiece(g);
        MoveBy(g, id, 10);
        MoveBy(g, id, 5);
        using (g.BeginAction()) g.Execute(new RollDice("Red attack", 2, 6, new[] { 3, 5 }));
        var t = TurnSender.Build(s, Bob.Id, Identity("alice", g.State.GameId!, Alice.Id));
        TurnSender.AfterSend(s);
        return (s, t);
    }

    private (ModuleSession S, PhaseController C, LocalActions A) Ports(ModuleSession s)
    {
        Func<GameCommand, GameState, GameCommand> prepare = (c, _) => c;
        var phases = new PhaseController(s, prepare);
        var history = new HistoryController(s, phases, prepare);
        return (s, phases, new LocalActions(s, phases, history, prepare));
    }

    [Fact]
    public void Check_DifferentModule()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        Assert.Equal(TurnCheck.DifferentModule, TurnOpener.Check(bob, t with { ModuleSha256 = new string('0', 64) }, Identity("bob", t.GameId)));
    }

    [Fact]
    public void Check_NewGame_WhenNoGameWithId()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        Assert.Equal(TurnCheck.NewGame, TurnOpener.Check(bob, t, Identity("bob", t.GameId)));
        bob.LoadSetup(bob.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });   // another game (other id)
        var before = StateHash.Of(bob.Game!.State);
        Assert.Equal(TurnCheck.NewGame, TurnOpener.Check(bob, t, Identity("bob", t.GameId)));
        Assert.Equal(before, StateHash.Of(bob.Game!.State));
    }

    [Fact]
    public void Check_Ok_WhenStateMatchesStart()
    {
        var (alice, t1) = AliceTurn();
        var bob = Open();
        bob.OpenTurn(t1, Bob.Id, Identity("bob", t1.GameId)).PlayAll();
        // Bob plays and sends back; Alice's game is still where she sent from.
        MoveBy(bob.Game!, AnyPiece(bob.Game!), -3);
        var t2 = TurnSender.Build(bob, Alice.Id, Identity("bob", t1.GameId, Bob.Id));
        Assert.Equal(TurnCheck.Ok, TurnOpener.Check(alice, t2, Identity("alice", t2.GameId)));
    }

    [Fact]
    public void Check_OlderPosition_WhenBothMoved()
    {
        var (alice, t1) = AliceTurn();
        var bob = Open();
        bob.OpenTurn(t1, Bob.Id, Identity("bob", t1.GameId)).PlayAll();
        MoveBy(bob.Game!, AnyPiece(bob.Game!), -3);
        var t2 = TurnSender.Build(bob, Alice.Id, Identity("bob", t1.GameId, Bob.Id));
        MoveBy(alice.Game!, AnyPiece(alice.Game!), 1);   // Alice moved on meanwhile
        var hash = StateHash.Of(alice.Game!.State);
        Assert.Equal(TurnCheck.OlderPosition, TurnOpener.Check(alice, t2, Identity("alice", t2.GameId)));
        Assert.Equal(hash, StateHash.Of(alice.Game!.State));   // checking never changes the game
    }

    [Fact]
    public void Check_AlreadyPlayed_ResendOpenedTwice()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        var model = bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
        model.PlayAll();
        Assert.True(model.Done);
        Assert.Contains(t.StartHash, Identity("bob", t.GameId).PlayedStartHashes);   // saved
        var hash = StateHash.Of(bob.Game!.State);
        Assert.Equal(TurnCheck.AlreadyPlayed, TurnOpener.Check(bob, t, Identity("bob", t.GameId)));
        Assert.Equal(hash, StateHash.Of(bob.Game!.State));
    }

    [Fact]
    public void Replay_NextStepsOneCommand_LineShown()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        var model = bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
        int changed = 0;
        model.Changed += () => changed++;
        Assert.Equal((0, 3, false), (model.Step, model.Count, model.Done));
        Assert.Null(model.CurrentLine);
        Assert.True(bob.IsReplaying);
        Assert.Equal(StateHash.Of(t.Start), StateHash.Of(bob.Game!.State));

        model.Next();
        Assert.Equal(1, model.Step);
        Assert.Equal(1, changed);
        Assert.False(string.IsNullOrEmpty(model.CurrentLine));
        Assert.Single(bob.Game!.Log);

        model.Next(); model.Next();
        Assert.Equal(3, model.Step);
        Assert.Contains("Red attack", model.CurrentLine);
        Assert.True(model.Done);
        model.Next();   // nothing left
        Assert.Equal(3, model.Step);
    }

    [Fact]
    public void Replay_StopThenPlayAll_FinishesWithEndHash()
    {
        var (alice, t) = AliceTurn();
        var bob = Open();
        var model = bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
        model.Changed += () => { if (model.Step == 1 && !model.Stopped) model.Stop(); };
        model.PlayAll();
        Assert.Equal(1, model.Step);
        Assert.True(model.Stopped);
        Assert.False(model.Done);
        Assert.True(bob.IsReplaying);

        model.PlayAll();
        Assert.True(model.Done);
        Assert.True(model.EndMatches);
        Assert.Null(model.Error);
        Assert.False(bob.IsReplaying);
        Assert.Equal(t.EndHash, StateHash.Of(bob.Game!.State));
        Assert.Equal(StateHash.Of(alice.Game!.State), StateHash.Of(bob.Game!.State));
        Assert.False(bob.Game!.CanUndo);   // Bob cannot undo Alice's turn
    }

    [Fact]
    public void Replay_ActionsRefusedWhileReplaying()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        var (_, _, port) = Ports(bob);
        var model = bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
        model.Next();
        var hash = StateHash.Of(bob.Game!.State);
        var g = bob.Game!;
        port.SetNotes("Bob sneaks in");
        port.Chat("hello");
        port.PieceCommand(new MovePieces(new[] { AnyPiece(g) }, g.State.Pieces[AnyPiece(g)].MapId, new PointD(0, 0), null));
        port.Undo();
        Assert.Equal(ModuleSession.ReplayingText, port.ApplyMissingInputs(null!, new Dictionary<string, string>()).Failed.Single());
        Assert.Equal("Finish the replay first.", ModuleSession.ReplayingText);
        Assert.Equal(hash, StateHash.Of(bob.Game!.State));
        Assert.Equal(1, model.Step);
        Assert.Throws<InvalidOperationException>(() => TurnSender.Build(bob, Alice.Id, Identity("bob", t.GameId, Bob.Id)));

        model.PlayAll();
        port.SetNotes("Bob's turn");
        Assert.Equal("Bob's turn", bob.Game!.State.Notes);
    }

    [Fact]
    public void Replay_Done_ViewSidesAreLocalPlayers_TurnBaseMarked()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        var model = bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
        Assert.NotNull(bob.ViewSides);
        Assert.Empty(bob.ViewSides!);   // public only while replaying
        model.PlayAll();
        Assert.Equal(new[] { "blue" }, bob.ViewSides!.ToArray());
        Assert.Null(model.ViewNotice);
        Assert.Equal(bob.Game!.Log.Count, bob.Game!.TurnBase);
        Assert.Equal(0, bob.Game!.TurnBase);   // rebased: Alice's turn is genesis

        var carol = Open();
        var m2 = carol.OpenTurn(t, "p9", Identity("carol", t.GameId));
        m2.PlayAll();
        Assert.Empty(carol.ViewSides!);
        Assert.Equal(TurnReplayModel.NotAPlayerText, m2.ViewNotice);
    }

    /// <summary>Commands whose <paramref name="failAt"/>-th read throws a plain (non-data) exception.</summary>
    private sealed class ThrowingCommands(IReadOnlyList<GameCommand> inner, int failAt) : IReadOnlyList<GameCommand>
    {
        private int _reads;
        public GameCommand this[int index] =>
            ++_reads == failAt ? throw new InvalidOperationException("boom") : inner[index];
        public int Count => inner.Count;
        public IEnumerator<GameCommand> GetEnumerator() => inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Replay_UnexpectedStepException_RestoresPreviousGame()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        bob.LoadSetup(bob.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        var mine = bob.Game!;
        var mineHash = StateHash.Of(mine.State);
        var bad = t with { Commands = new ThrowingCommands(t.Commands, 2) };   // the step runs, then the model's own read throws
        var model = bob.OpenTurn(bad, Bob.Id, Identity("bob", t.GameId));
        model.Next();
        Assert.True(model.Done);
        Assert.Contains("boom", model.Error);
        Assert.False(bob.IsReplaying);
        Assert.Same(mine, bob.Game);
        Assert.Equal(mineHash, StateHash.Of(bob.Game!.State));
    }

    [Fact]
    public void Replay_MalformedStep_RestoresPreviousGame()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        bob.LoadSetup(bob.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        var mine = bob.Game!;
        var mineHash = StateHash.Of(mine.State);
        bob.SetViewSides(new HashSet<string> { "blue" });
        var start = t.Start.DeepClone();
        var victim = start.Pieces.Keys.OrderBy(k => k, StringComparer.Ordinal).Last();
        start.Pieces[victim].TraitStates = null!;
        var bad = t with { Start = start, Commands = t.Commands.Append(new SetTraitState(victim, 0, "s")).ToList() };
        var model = bob.OpenTurn(bad, Bob.Id, Identity("bob", t.GameId));
        model.PlayAll();
        Assert.True(model.Done);
        Assert.StartsWith("Malformed turn file:", model.Error);
        Assert.False(model.EndMatches);
        Assert.False(bob.IsReplaying);
        Assert.Same(mine, bob.Game);
        Assert.Equal(mineHash, StateHash.Of(bob.Game!.State));
        Assert.Equal(new[] { "blue" }, bob.ViewSides!.ToArray());
        Assert.DoesNotContain(t.StartHash, Identity("bob", t.GameId).PlayedStartHashes);
    }

    [Fact]
    public void Open_DifferentModule_LeavesSessionUnchanged()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        bob.StartNewGame(Difficulty.FullyAuto, new[] { Alice, Bob });
        var mine = bob.Game;
        var ex = Assert.Throws<InvalidOperationException>(() => bob.OpenTurn(t with { ModuleSha256 = new string('0', 64) }, Bob.Id, Identity("bob", t.GameId)));
        Assert.Equal("This turn file belongs to a different module", ex.Message);
        Assert.Same(mine, bob.Game);
        Assert.False(bob.IsReplaying);
    }

    [Fact]
    public void Send_EmptyTurn_IsAPass()
    {
        var s = NewGame();
        var id = Identity("alice", s.Game!.State.GameId!, Alice.Id);
        var t = TurnSender.Build(s, Bob.Id, id);
        var pass = Assert.IsType<AddLogLine>(Assert.Single(t.Commands));
        Assert.Equal("Alice passed.", pass.Text);
        Assert.Equal(Alice.Id, pass.Who);
        Assert.Equal(LogKind.System, pass.Kind);
        TurnSender.AfterSend(s);
        Assert.Single(TurnSender.Build(s, Bob.Id, id).Commands);   // passing again after sending sends again
    }

    [Fact]
    public void Send_WithoutLocalPlayer_Refuses()
    {
        var s = NewGame();
        Assert.Throws<InvalidOperationException>(() => TurnSender.Build(s, Bob.Id, Identity("alice", s.Game!.State.GameId!)));
    }

    [Fact]
    public void Send_ThenOpponentReplays_BothStatesEqual()
    {
        var (alice, t1) = AliceTurn();
        var bob = Open();
        var gameId = t1.GameId;
        Assert.Equal(TurnCheck.NewGame, TurnOpener.Check(bob, t1, Identity("bob", gameId)));
        var path = Path.Combine(_tmp, "t1.qturn");
        TurnFiles.Write(path, t1);
        var m1 = bob.OpenTurn(TurnFiles.Read(path), Bob.Id, Identity("bob", gameId));
        m1.PlayAll();
        Assert.True(m1.EndMatches);
        Assert.Equal(StateHash.Of(alice.Game!.State), StateHash.Of(bob.Game!.State));

        // Bob's turn 1
        MoveBy(bob.Game!, AnyPiece(bob.Game!), 7);
        var t2 = TurnSender.Build(bob, Alice.Id, Identity("bob", gameId, Bob.Id));
        TurnSender.AfterSend(bob);
        Assert.Equal(TurnCheck.Ok, TurnOpener.Check(alice, t2, Identity("alice", gameId)));
        var m2 = alice.OpenTurn(t2, Alice.Id, Identity("alice", gameId));
        m2.PlayAll();
        Assert.True(m2.EndMatches);
        Assert.Equal(new[] { "red" }, alice.ViewSides!.ToArray());

        // Alice's turn 2 (a pass), Bob's turn 2
        var t3 = TurnSender.Build(alice, Bob.Id, Identity("alice", gameId, Alice.Id));
        TurnSender.AfterSend(alice);
        Assert.Equal(TurnCheck.Ok, TurnOpener.Check(bob, t3, Identity("bob", gameId)));
        bob.OpenTurn(t3, Bob.Id, Identity("bob", gameId)).PlayAll();
        MoveBy(bob.Game!, AnyPiece(bob.Game!), -2);
        using (bob.Game!.BeginAction()) bob.Game!.Execute(new RollDice("Blue attack", 1, 6, new[] { 6 }));
        var t4 = TurnSender.Build(bob, Alice.Id, Identity("bob", gameId, Bob.Id));
        TurnSender.AfterSend(bob);
        Assert.Equal(TurnCheck.Ok, TurnOpener.Check(alice, t4, Identity("alice", gameId)));
        var m4 = alice.OpenTurn(t4, Alice.Id, Identity("alice", gameId));
        m4.PlayAll();
        Assert.True(m4.EndMatches);
        Assert.Equal(StateHash.Of(alice.Game!.State), StateHash.Of(bob.Game!.State));
        Assert.Contains(alice.Game!.State.Log, l => l.Text == "Alice passed.");
    }

    [Fact]
    public void Identity_RoundTrip_UnderLayoutRoot_PasswordProtected()
    {
        var gameId = Guid.NewGuid().ToString("N");
        var old = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _tmp);
            var id = TurnIdentity.Load(gameId, null, new FakeProtector());
            Assert.Equal(Path.Combine(_tmp, "turns", gameId + ".json"), id.FilePath);
            id.LocalPlayerId = "p2";
            id.Relay = "relay.example:7777";
            id.UseMailbox = true;
            id.SetPassword("hunter2");
            for (int i = 0; i < TurnIdentity.MaxPlayed + 5; i++) id.RecordPlayed("h" + i);
            Assert.True(id.SetPassword("hunter2"));
            id.Save();
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(id.FilePath)!, "*.tmp"));
            Assert.DoesNotContain("hunter2", File.ReadAllText(id.FilePath));

            var back = TurnIdentity.Load(gameId, null, new FakeProtector());
            Assert.Equal(("p2", "relay.example:7777", true), (back.LocalPlayerId, back.Relay, back.UseMailbox));
            Assert.Equal("hunter2", back.GetPassword());
            Assert.Equal(TurnIdentity.MaxPlayed, back.PlayedStartHashes.Count);
            Assert.DoesNotContain("h0", back.PlayedStartHashes);   // oldest dropped
            Assert.Contains("h" + (TurnIdentity.MaxPlayed + 4), back.PlayedStartHashes);

            Assert.Throws<InvalidDataException>(() => TurnIdentity.Load(@"..\evil"));
            Assert.Throws<InvalidDataException>(() => TurnIdentity.Load(""));
            File.WriteAllText(id.FilePath, "{ not json");
            Assert.Null(TurnIdentity.Load(gameId, null, new FakeProtector()).LocalPlayerId);   // unreadable = forgotten
        }
        finally { Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, old); }
    }

    [WindowsFact]
    public void Dpapi_RoundTrip_CurrentUser()
    {
        var p = DpapiProtector.Instance.Protect("secret"u8.ToArray());
        Assert.NotEqual("secret"u8.ToArray(), p);
        Assert.Equal("secret"u8.ToArray(), DpapiProtector.Instance.Unprotect(p));
    }

    private sealed class BrokenProtector : IPasswordProtector
    {
        public byte[] Protect(byte[] plain) => throw new System.ComponentModel.Win32Exception(5);
        public byte[] Unprotect(byte[] p) => throw new System.ComponentModel.Win32Exception(5);
    }

    [Fact]
    public void Identity_ProtectorFails_PasswordNotSaved_NeverThrows()
    {
        var id = TurnIdentity.Load(Guid.NewGuid().ToString("N"), Root("x"), new BrokenProtector());
        Assert.False(id.SetPassword("pw"));
        Assert.Null(id.ProtectedPassword);
        id.ProtectedPassword = new byte[] { 1, 2 };
        Assert.Null(id.GetPassword());
        Assert.True(id.SetPassword(""));
        Assert.Null(id.ProtectedPassword);
    }

    [Fact]
    public void Identity_TooLargeFile_ReadsAsEmpty()
    {
        var gameId = Guid.NewGuid().ToString("N");
        var id = Identity("big", gameId, "p1");
        id.Save();
        File.WriteAllText(id.FilePath, "{\"LocalPlayerId\":\"p1\",\"Relay\":\"" + new string('x', (int)TurnIdentity.MaxFileBytes) + "\"}");
        Assert.Null(Identity("big", gameId).LocalPlayerId);
    }

    [Fact]
    public void Identity_TwoInstances_PlayedHashesMerge()
    {
        var gameId = Guid.NewGuid().ToString("N");
        var a = Identity("merge", gameId);
        var b = Identity("merge", gameId);
        a.RecordPlayed("ha");
        a.Save();
        b.RecordPlayed("hb");
        b.Save();
        var back = Identity("merge", gameId);
        Assert.Contains("ha", back.PlayedStartHashes);
        Assert.Contains("hb", back.PlayedStartHashes);
        Assert.Contains("ha", b.PlayedStartHashes);   // the saving instance sees the union too

        for (int i = 0; i < TurnIdentity.MaxPlayed; i++) a.RecordPlayed("n" + i);
        a.Save();
        var capped = Identity("merge", gameId);
        Assert.Equal(TurnIdentity.MaxPlayed, capped.PlayedStartHashes.Count);
        Assert.Contains("n" + (TurnIdentity.MaxPlayed - 1), capped.PlayedStartHashes);
    }

    [Fact]
    public void Replay_EndMismatch_NotRecordedAsPlayed()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        var model = bob.OpenTurn(t with { EndHash = new string('0', 64) }, Bob.Id, Identity("bob", t.GameId));
        model.PlayAll();
        Assert.True(model.Done);
        Assert.False(model.EndMatches);
        Assert.Null(model.Error);
        Assert.DoesNotContain(t.StartHash, Identity("bob", t.GameId).PlayedStartHashes);
        Assert.Equal("The replayed turn does not match the sender's game.", TurnReplayModel.EndMismatchText);
    }

    [Fact]
    public void Replay_RulesChangesRefusedWhilePaused()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        bob.NewRules("t");
        var model = bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
        model.Next();
        var hash = StateHash.Of(bob.Game!.State);
        var rules = bob.Rules;
        var pack = Path.Combine(_tmp, "pack.qrules");
        Assert.Equal(ModuleSession.ReplayingText,
            Assert.Throws<InvalidOperationException>(() => bob.UpdateRules(_ => PhaseFixture.Pack(bob.Archive.Sha256))).Message);
        Assert.Equal(ModuleSession.ReplayingText, Assert.Throws<InvalidOperationException>(() => bob.NewRules("other")).Message);
        Assert.Equal(ModuleSession.ReplayingText, Assert.Throws<InvalidOperationException>(() => bob.OpenRules(pack)).Message);
        bob.SeedLedgers();
        Assert.Same(rules, bob.Rules);
        Assert.Equal(hash, StateHash.Of(bob.Game!.State));
        model.PlayAll();
        Assert.True(model.EndMatches);
    }

    [Fact]
    public void Replay_HalfReplayedGame_CannotBeSaved()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        var model = bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
        model.Next();
        Assert.False(bob.CanSave);
        var path = Path.Combine(_tmp, "half.qsave");
        Assert.Equal(ModuleSession.ReplayingText, Assert.Throws<InvalidOperationException>(() => bob.SaveNative(path)).Message);
        Assert.False(File.Exists(path));
        model.PlayAll();
        Assert.True(bob.CanSave);
        bob.SaveNative(path);
        Assert.True(File.Exists(path));
    }

    // ---- final review fixes ----

    private static readonly PlayerDef Carol = new("p3", "Carol", new[] { "green" });

    private static (TurnFile T, TurnReplayModel M) SendAndReplay(ModuleSession from, ModuleSession to, PlayerDef sender, PlayerDef receiver,
        Func<string, string, TurnIdentity> identity, string senderKey, string receiverKey)
    {
        var gameId = from.Game!.State.GameId!;
        var t = TurnSender.Build(from, receiver.Id, identity(senderKey, gameId));
        TurnSender.AfterSend(from);
        var m = to.OpenTurn(t, receiver.Id, identity(receiverKey, gameId));
        m.PlayAll();
        Assert.True(m.Done);
        Assert.True(m.EndMatches);
        return (t, m);
    }

    [Fact]
    public void Replay_PhaseMissingFromLocalPack_NotReconciled_EndMatches()
    {
        var alice = NewGame();
        var g = alice.Game!;
        // Alice's pack has a phase Bob's lacks: her game sits in it.
        using (g.BeginAction()) g.Execute(new AdvancePhase(null, new PhasePosition(1, 1, "ph-only-alice", 9), "", "Alice's phase"));
        TurnSender.AfterSend(alice);
        MoveBy(g, AnyPiece(g), 6);
        var t = TurnSender.Build(alice, Bob.Id, Identity("alice", g.State.GameId!, Alice.Id));

        var bob = Open();
        bob.LoadSetup(bob.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        bob.NewRules("bob");
        bob.UpdateRules(_ => PhaseFixture.Pack(bob.Archive.Sha256));
        var (_, phases, _) = Ports(bob);
        var model = bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
        var logCount = bob.Game!.Log.Count;
        Assert.Null(phases.Reconcile());   // what the app runs after any game replacement: nothing while replaying
        Assert.Equal(logCount, bob.Game!.Log.Count);
        Assert.Equal("ph-only-alice", bob.Game!.State.Position!.PhaseId);
        model.PlayAll();
        Assert.True(model.EndMatches);
        Assert.Equal(t.EndHash, StateHash.Of(bob.Game!.State));
    }

    [Fact]
    public void ThreePlayers_Chain_NoWarnings_BothMovedStillWarns()
    {
        var alice = Open();
        alice.LoadSetup(alice.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob, Carol });
        var gameId = alice.Game!.State.GameId!;
        TurnIdentity Id(string who, string game) => Identity(who, game, who switch { "alice" => Alice.Id, "bob" => Bob.Id, _ => Carol.Id });

        MoveBy(alice.Game!, AnyPiece(alice.Game!), 4);
        var bob = Open();
        var carol = Open();
        SendAndReplay(alice, bob, Alice, Bob, Id, "alice", "bob");          // A → B (B: new game)
        MoveBy(bob.Game!, AnyPiece(bob.Game!), 3);
        SendAndReplay(bob, carol, Bob, Carol, Id, "bob", "carol");          // B → C (C: new game)
        MoveBy(carol.Game!, AnyPiece(carol.Game!), 2);

        // C → A: Alice's game is still at the end of her own turn (two turns behind), with nothing done since: no warning.
        var t3 = TurnSender.Build(carol, Alice.Id, Id("carol", gameId));
        TurnSender.AfterSend(carol);
        Assert.Equal(TurnCheck.Ok, TurnOpener.Check(alice, t3, Id("alice", gameId)));
        var m3 = alice.OpenTurn(t3, Alice.Id, Id("alice", gameId));
        m3.PlayAll();
        Assert.True(m3.EndMatches);

        // A → B: Bob has done nothing since he sent: no warning either.
        MoveBy(alice.Game!, AnyPiece(alice.Game!), 1);
        var t4 = TurnSender.Build(alice, Bob.Id, Id("alice", gameId));
        TurnSender.AfterSend(alice);
        Assert.Equal(TurnCheck.Ok, TurnOpener.Check(bob, t4, Id("bob", gameId)));
        var m4 = bob.OpenTurn(t4, Bob.Id, Id("bob", gameId));
        m4.PlayAll();
        Assert.True(m4.EndMatches);
        Assert.Equal(StateHash.Of(alice.Game!.State), StateHash.Of(bob.Game!.State));

        // B → C while Carol moved on locally: her own moves would be replaced, so it still warns.
        MoveBy(bob.Game!, AnyPiece(bob.Game!), -1);
        var t5 = TurnSender.Build(bob, Carol.Id, Id("bob", gameId));
        MoveBy(carol.Game!, AnyPiece(carol.Game!), 9);
        Assert.Equal(TurnCheck.OlderPosition, TurnOpener.Check(carol, t5, Id("carol", gameId)));
    }

    [Fact]
    public void OwnSentTurn_ComingBack_IsOlderPosition()
    {
        var red = NewGame();
        var gid = red.Game!.State.GameId!;
        MoveBy(red.Game!, AnyPiece(red.Game!), 4);
        var redId = Identity("alice", gid, Alice.Id);
        var t1 = TurnSender.Build(red, Bob.Id, redId);
        TurnSender.AfterSend(red);
        redId.RecordSent(t1.StartHash);
        redId.Save();

        var blue = Open();
        blue.OpenTurn(t1, Bob.Id, Identity("bob", gid)).PlayAll();
        MoveBy(blue.Game!, AnyPiece(blue.Game!), 2);
        var t2 = TurnSender.Build(blue, Alice.Id, Identity("bob", gid, Bob.Id));
        TurnSender.AfterSend(blue);
        Assert.Equal(TurnCheck.Ok, TurnOpener.Check(red, t2, Identity("alice", gid)));
        var m2 = red.OpenTurn(t2, Alice.Id, Identity("alice", gid));
        m2.PlayAll();
        Assert.True(m2.EndMatches);

        // Red opens her own first turn again: nothing local since Blue's turn, but it is hers and older.
        Assert.Contains(t1.StartHash, Identity("alice", gid).SentStartHashes);   // kept through the replay's identity save
        Assert.Equal(TurnCheck.OlderPosition, TurnOpener.Check(red, t1, Identity("alice", gid)));
        // Even on a computer that did not record it: a turn not addressed to the local player is not Ok.
        Assert.Equal(TurnCheck.OlderPosition, TurnOpener.Check(red, t1, Identity("elsewhere", gid, Alice.Id)));
        Assert.Equal(TurnCheck.OlderPosition, TurnOpener.Check(red, t1, Identity("nobody", gid)));
    }

    [Fact]
    public void Identity_SentHashes_MergeOnSave()
    {
        var gid = Guid.NewGuid().ToString("N");
        var a = Identity("merge", gid);
        var b = Identity("merge", gid);
        a.RecordSent("s1");
        a.Save();
        b.RecordSent("s2");
        b.Save();
        Assert.Equal(new[] { "s1", "s2" }, Identity("merge", gid).SentStartHashes.Order(StringComparer.Ordinal).ToArray());
        for (var i = 0; i < TurnIdentity.MaxPlayed + 10; i++) b.RecordSent("x" + i);
        Assert.Equal(TurnIdentity.MaxPlayed, b.SentStartHashes.Count);
    }

    [Fact]
    public void Check_GameAtStart_IsOk_EvenWhenAlreadyPlayed()
    {
        var alice = NewGame();
        TurnSender.AfterSend(alice);   // her turn starts here
        var start = Path.Combine(_tmp, "start.qsave");
        alice.SaveNative(start);
        MoveBy(alice.Game!, AnyPiece(alice.Game!), 10);
        var t = TurnSender.Build(alice, Bob.Id, Identity("alice", alice.Game!.State.GameId!, Alice.Id));

        // Bob replayed it once, but the replayed game was never saved: his game is back at the turn's start.
        var bob = Open();
        var id = Identity("bob", t.GameId, Bob.Id);
        id.RecordPlayed(t.StartHash);
        Assert.Equal(TurnCheck.AlreadyPlayed, TurnOpener.Check(bob, t, id));
        bob.LoadNative(start);
        Assert.Equal(t.StartHash, StateHash.Of(bob.Game!.State));
        Assert.Equal(TurnCheck.Ok, TurnOpener.Check(bob, t, id));
    }

    [Fact]
    public void Check_StartNotMatchingItsHash_IsMalformed()
    {
        var (_, t) = AliceTurn();
        var bob = Open();
        var bad = t with { StartHash = new string('0', 64) };
        Assert.Equal("Malformed turn file: start does not match its hash.",
            Assert.Throws<InvalidDataException>(() => TurnOpener.Check(bob, bad, Identity("bob", t.GameId))).Message);
        Assert.Equal(TurnOpener.StartMismatchText, Assert.Throws<InvalidDataException>(() =>
            TurnOpener.Check(bob, t with { Start = t.Start.DeepClone().Also(s => s.Notes = "edited") }, Identity("bob", t.GameId))).Message);
    }

    [Fact]
    public void Open_ReplacesAViewerCopy()
    {
        var (_, t) = AliceTurn();
        var guest = Open();
        guest.LoadSetup(guest.Model.Setups[0], Difficulty.FullyAuto);
        guest.EnterGuestMode();
        guest.ApplySnapshot(new GameState());
        var viewer = Path.Combine(_tmp, "viewer.qsave");
        guest.ExportViewerCopy(viewer);

        var bob = Open();
        bob.LoadNative(viewer);
        Assert.True(bob.IsViewerCopy);
        Assert.Equal(TurnCheck.NewGame, TurnOpener.Check(bob, t, Identity("bob", t.GameId)));
        var m = bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
        m.PlayAll();
        Assert.True(m.EndMatches);
        Assert.False(bob.IsViewerCopy);
        Assert.True(bob.CanSave);
    }

    [Fact]
    public void NextShown_SkipsStepsWithoutALogLine()
    {
        var alice = NewGame();
        var g = alice.Game!;
        var piece = AnyPiece(g);
        using (g.BeginAction()) g.Execute(new ToggleTrail(piece));
        using (g.BeginAction()) g.Execute(new ToggleTrail(piece));
        using (g.BeginAction()) g.Execute(new RollDice("Red attack", 1, 6, new[] { 4 }));
        using (g.BeginAction()) g.Execute(new ToggleTrail(piece));
        var t = TurnSender.Build(alice, Bob.Id, Identity("alice", g.State.GameId!, Alice.Id));
        Assert.Equal(4, t.Commands.Count);

        var bob = Open();
        var m = bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
        var changed = 0;
        m.Changed += () => changed++;
        m.NextShown();   // two silent trail toggles and the roll in one timed step
        Assert.Equal(3, m.Step);
        Assert.True(m.LastStepLogged);
        Assert.Contains("Red attack", m.CurrentLine);
        Assert.Equal(1, changed);
        m.NextShown();   // the last silent step ends the turn
        Assert.True(m.Done);
        Assert.Equal(4, m.Step);
        Assert.True(m.EndMatches);
    }

    [Fact]
    public void Undo_BelowSentTurn_Refused_InATurnGameOnly()
    {
        var s = NewGame();
        var g = s.Game!;
        Func<GameCommand, GameState, GameCommand> prepare = (c, _) => c;
        var history = new HistoryController(s, new PhaseController(s, prepare), prepare);
        MoveBy(g, AnyPiece(g), 5);
        TurnSender.Build(s, Bob.Id, Identity("alice", g.State.GameId!, Alice.Id));
        TurnSender.AfterSend(s);
        Assert.True(history.CanUndo);   // not a play-by-email game here (hot-seat): undo as before
        Assert.False(history.UndoBlockedBySentTurn);

        history.IsTurnGame = () => true;
        Assert.False(history.CanUndo);
        Assert.True(history.UndoBlockedBySentTurn);
        Assert.DoesNotContain(history.Checkpoints(), r => r.LogIndex < g.TurnBase);
        var hash = StateHash.Of(g.State);
        history.Undo();
        Assert.Equal(hash, StateHash.Of(g.State));
        Assert.Contains(s.Log.Items, d => d.Message == HistoryController.AlreadySentText);
        Assert.Equal("This turn was already sent.", HistoryController.AlreadySentText);

        MoveBy(g, AnyPiece(g), 2);   // the new turn's own moves can be undone
        Assert.True(history.CanUndo);
        history.Undo();
        Assert.Equal(hash, StateHash.Of(g.State));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void ReceivedTurns_KeptUntilPruned()
    {
        var (_, t) = AliceTurn();
        var root = Root("received");
        var item = new string('a', 32);
        var path = ReceivedTurns.Keep(t, item, out var why, root);
        Assert.Null(why);
        Assert.Equal(Path.Combine(root, t.GameId.ToLowerInvariant(), "received", item + ".qturn"), path);
        Assert.Equal(t.EndHash, TurnFiles.Read(path!).EndHash);
        Assert.Equal(path, ReceivedTurns.Keep(t, item, out _, root));   // kept once
        Assert.Null(ReceivedTurns.Keep(t, "../../x", out why, root));
        Assert.NotNull(why);
        Assert.Single(ReceivedTurns.List(t.GameId, root));
        Assert.Equal(1, ReceivedTurns.Prune(t.GameId, root));
        Assert.Empty(ReceivedTurns.List(t.GameId, root));
        Assert.Equal(0, ReceivedTurns.Prune("not a game id", root));
    }

    [Fact]
    public void SenderName_Clamped_PlayStep_Capped()
    {
        var longName = new string('R', 200);
        Assert.Equal(TurnUiModel.MaxShownName, TurnUiModel.ShownName(longName).Length);
        Assert.EndsWith("…", TurnUiModel.ShownName(longName));
        Assert.Equal("Rita", TurnUiModel.ShownName(" Ri\u0007ta "));
        Assert.True(TurnUiModel.NewTurnWaitingText(longName).Length <= "New turn waiting from ".Length + TurnUiModel.MaxShownName);
        Assert.Equal("New turn waiting from Rita", TurnUiModel.NewTurnWaitingText("Rita"));
        Assert.Equal(300, TurnUiModel.PlayStepMs(1));
        Assert.Equal(300, TurnUiModel.PlayStepMs(20));
        Assert.True(TurnUiModel.PlayStepMs(1000) < 300);
        Assert.True(TurnUiModel.PlayStepMs(int.MaxValue) > 0);
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}

/// <summary>A fact that runs only on Windows (skipped elsewhere).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only (DPAPI).";
    }
}

/// <summary>Tests that set <see cref="AppDataRoot.EnvVar"/> run one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class AppDataRootEnv
{
    public const string Name = "AppDataRoot environment";
}
