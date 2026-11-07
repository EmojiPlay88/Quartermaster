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
