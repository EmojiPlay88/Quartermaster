using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using NetResult = Quartermaster.Net.IntentResult;

namespace Quartermaster.Render.Tests;

/// <summary>The host's online session over an in-memory relay. Invented Red/Blue/Green sides only.</summary>
public class HostSessionTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-host-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private readonly List<HostSession> _hosts = new();

    private const string SecretStock = "61616";      // red depot's private supply
    private const string SecretWater = "73737";      // red unit's private water
    private const string SecretGreen = "52525";      // green depot's private supply
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });

    public HostSessionTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var h in _hosts) h.Dispose();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
            }).ToList(),
            Holders = new()
            {
                new HolderDef { Id = "reddepot", Name = "Red Depot", SideId = "red" },
                new HolderDef { Id = "greendepot", Name = "Green Depot", SideId = "green" },
            },
            Formulas = p.Formulas.Concat(new[]
            {
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
            Sides = new()
            {
                new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" },
                new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\")" },
                new SideDef { Id = "green", Name = "Green", UnitFilter = "startsWith(name(), \"Green\")" },
            },
        };
    }

    private sealed record Rig(ModuleSession S, HostSession H, MemoryRelay Relay, QueueDispatcher D)
    {
        private TimeSpan _offset;

        /// <summary>Moves the host's clock (rate limits, snapshot spacing) forward; real time keeps flowing underneath.</summary>
        public void Advance(TimeSpan by) => _offset += by;

        public void UseOffsetClock() => H.Now = () => HostSession.MonotonicNow() + _offset;
    }

    private Rig Open(IReadOnlyList<PlayerDef>? players = null, Difficulty difficulty = Difficulty.FullyAuto, string hostName = "Alice")
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], difficulty, players ?? new[] { Alice });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        var g = s.Game!;
        g.Execute(new LedgerDelta(LedgerKey.Holder("reddepot"), "supply", 0, double.Parse(SecretStock), "manual", null, null, null, null));
        g.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedOne), "water", 10, double.Parse(SecretWater), "manual", null, null, "secret", null));
        g.Execute(new LedgerDelta(LedgerKey.Holder("greendepot"), "supply", 0, double.Parse(SecretGreen), "manual", null, null, null, null));
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var relay = new MemoryRelay();
        var d = new QueueDispatcher();
        var listing = new LobbyListing("", "Test lobby", hostName, s.Archive.Sha256, "Fixture", new[] { "Red", "Blue", "Green" },
            new[] { "Blue", "Green" }, 1, 3, false, Protocol.ProtocolVersion);
        var h = new HostSession(s, new IntentExecutor(s, c), relay.Host, "pw", "abc-234", listing, d.Post);
        _hosts.Add(h);
        d.Pump();
        var rig = new Rig(s, h, relay, d);
        rig.UseOffsetClock();
        return rig;
    }

    private static void Send(Rig r, MemoryTransport guest, GamePayload p)
    {
        guest.SendPayload(p);
        r.D.Pump();
    }

    private static Welcome JoinAs(Rig r, MemoryTransport guest, string name, string? token = null)
    {
        Send(r, guest, new JoinRequest(name, token, r.S.Archive.Sha256));
        return guest.Of<Welcome>().Last();
    }

    private static string Intent(GameIntent i) => JsonSerializer.Serialize(i, QuartermasterJson.Options);

    private static string Json(GameState st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    private static string SnapshotJson(Snapshot snap) => Encoding.UTF8.GetString(Gzip.DecompressBounded(snap.StateJsonGz, int.MaxValue));

    /// <summary>Every byte the guest received, opened: raw payloads, unzipped snapshots and the unpacked rules pack.</summary>
    private static string AllReceivedText(MemoryTransport guest)
    {
        var sb = new StringBuilder();
        foreach (var raw in guest.Inbox)
        {
            sb.Append(Encoding.UTF8.GetString(raw)).Append('\n');
            switch (Framing.Decode<GamePayload>(raw))
            {
                case Snapshot snap: sb.Append(SnapshotJson(snap)).Append('\n'); break;
                case Welcome w:
                    using (var zip = new ZipArchive(new MemoryStream(w.RulesPack), ZipArchiveMode.Read))
                        foreach (var e in zip.Entries)
                            using (var rd = new StreamReader(e.Open())) sb.Append(rd.ReadToEnd()).Append('\n');
                    break;
            }
        }
        return sb.ToString();
    }

    // ---- joining ----

    [Fact]
    public void Join_WrongModule_Refused()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        Send(r, bob, new JoinRequest("Bob", null, new string('0', 64)));
        Assert.Equal(HostSession.DifferentModuleText, Assert.Single(bob.Messages.Cast<Kicked>()).Reason);
        Assert.Single(r.H.Seats.All);
    }

    [Fact]
    public void Join_ProtocolMismatch_Refused()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        Send(r, bob, new JoinRequest("Bob", null, r.S.Archive.Sha256, 2));
        // The guest is told the HOST's version (the one it has to match), not its own.
        Assert.Equal(Protocol.MismatchMessage(Protocol.ProtocolVersion), Assert.Single(bob.Of<Kicked>()).Reason);
        Assert.Single(r.H.Seats.All);
    }

    [Fact]
    public void Join_NewGuest_GetsWelcomeWithPack()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        var pack = RulesPackIo.FromBytes(w.RulesPack);
        Assert.Equal(r.S.Rules!.Id, pack.Id);
        Assert.Equal(new[] { "red", "blue", "green" }, pack.Sides.Select(x => x.Id));
        Assert.Equal(new[] { "blue", "green" }, w.FreeSides);
        Assert.Matches("^[0-9a-f]{64}$", w.RejoinToken);
        Assert.Equal(2, r.H.Seats.All.Count);
        Assert.Equal("m1", r.H.Seats.ByPlayer(w.PlayerId)!.MemberId);
        Assert.Empty(bob.Of<Snapshot>());   // no side yet: nothing to see
        var listing = r.Relay.Listings.Last();
        Assert.Equal(2, listing.Players);
        Assert.Equal(new[] { "Blue", "Green" }, listing.FreeSides);
        Assert.Equal("ABC234", listing.Code);
        Assert.True(listing.Locked);
        // The host's own seat is in the game's player list; the host sees its own sides.
        Assert.Equal(new[] { r.H.Seats.Host.PlayerId }, r.S.Game!.State.Players.Select(p => p.Id));
        Assert.Equal(new[] { "red" }, r.S.ViewSides!);
    }

    [Fact]
    public void Join_TokenIsStoredOnlyAsHash()
    {
        var r = Open();
        var w = JoinAs(r, r.Relay.Guest("m1"), "Bob");
        var seat = r.H.Seats.ByPlayer(w.PlayerId)!;
        Assert.Equal(SHA256.HashData(Convert.FromHexString(w.RejoinToken)), seat.TokenHash);
        Assert.Same(seat, r.H.Seats.ByToken(w.RejoinToken.ToUpperInvariant()));
        Assert.Null(r.H.Seats.ByToken(new string('a', 64)));
        Assert.Null(r.H.Seats.ByToken("zz"));
    }

    [Fact]
    public void Join_NoFreeSide_Full()
    {
        var r = Open();
        Send(r, r.Relay.Guest("m1"), new ChooseSides(new[] { "blue" }));   // not seated yet: dropped
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue", "green" }));
        var carol = r.Relay.Guest("m2");
        Send(r, carol, new JoinRequest("Carol", null, r.S.Archive.Sha256));
        Assert.Equal(HostSession.FullText, Assert.Single(carol.Of<Kicked>()).Reason);
    }

    [Fact]
    public void Rejoin_WithoutToken_Refused()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        r.Relay.Drop("m1");
        r.D.Pump();

        var thief = r.Relay.Guest("m2");
        Send(r, thief, new JoinRequest("bob", null, r.S.Archive.Sha256));
        r.Advance(TimeSpan.FromSeconds(3));   // one join per member per 2 s
        Send(r, thief, new JoinRequest("Bob", new string('1', 64), r.S.Archive.Sha256));
        var kicks = thief.Of<Kicked>().ToList();
        Assert.Equal(2, kicks.Count);
        Assert.All(kicks, k => Assert.Equal(HostSession.NameTakenText("Bob"), k.Reason));
        Assert.Empty(thief.Of<Welcome>());
        Assert.Empty(thief.Of<Snapshot>());
        var seat = r.H.Seats.ByPlayer(w.PlayerId)!;
        Assert.False(seat.Connected);
        // Someone else's name is refused too, even while the host is connected.
        r.Advance(TimeSpan.FromSeconds(3));
        Send(r, thief, new JoinRequest("ALICE", null, r.S.Archive.Sha256));
        Assert.Equal(HostSession.NameTakenText("Alice"), thief.Of<Kicked>().Last().Reason);
    }

    [Fact]
    public void Rejoin_WithToken_SameSeat()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        r.Relay.Drop("m1");
        r.D.Pump();
        var seat = r.H.Seats.ByPlayer(w.PlayerId)!;
        Assert.False(seat.Connected);
        Assert.Equal(new[] { "blue" }, seat.Sides);   // the seat stays

        var back = r.Relay.Guest("m9");
        var w2 = JoinAs(r, back, "whatever", w.RejoinToken);
        Assert.Equal(w.PlayerId, w2.PlayerId);
        Assert.Equal(2, r.H.Seats.All.Count);
        Assert.Equal("m9", seat.MemberId);
        Assert.Equal(new[] { "blue" }, seat.Sides);
        var snap = Assert.Single(back.Of<Snapshot>());   // a full snapshot straight away
        Assert.Equal(Json(PlayerView.For(r.S.Game!.State, r.S.Rules, r.S.Model, new HashSet<string> { "blue" })), SnapshotJson(snap));
    }

    [Fact]
    public void FreeSeat_OnlyDisconnectedGuests()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        Assert.Equal(new[] { r.H.Seats.Host.PlayerId, w.PlayerId }, r.S.Game!.State.Players.Select(p => p.Id));
        Assert.False(r.H.FreeSeat(w.PlayerId));        // still connected
        Assert.False(r.H.FreeSeat(r.H.Seats.Host.PlayerId));              // the host's own seat
        r.Relay.Drop("m1");
        r.D.Pump();
        Assert.True(r.H.FreeSeat(w.PlayerId));
        Assert.Single(r.H.Seats.All);
        Assert.Equal(new[] { r.H.Seats.Host.PlayerId }, r.S.Game!.State.Players.Select(p => p.Id));
        Assert.Equal(new[] { "Blue", "Green" }, r.Relay.Listings.Last().FreeSides);
        Assert.Null(r.H.Seats.ByToken(w.RejoinToken));
    }

    [Fact]
    public void AssignSides_GivesFreeSidesToAGuest_WhoGetsItsView()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        Assert.Empty(bob.Of<Snapshot>());
        Assert.Null(r.H.AssignSides(w.PlayerId, new[] { "green" }));
        Assert.Equal(new[] { "green" }, r.H.Seats.ByPlayer(w.PlayerId)!.Sides);
        Assert.Single(bob.Of<Snapshot>());                                    // the view comes at once
        Assert.Equal(new[] { "Blue" }, r.Relay.Listings.Last().FreeSides);
        Assert.Equal(new[] { r.H.Seats.Host.PlayerId, w.PlayerId }, r.S.Game!.State.Players.Select(p => p.Id));
        Assert.DoesNotContain(SecretStock, AllReceivedText(bob));             // green never sees red's private supply
    }

    [Fact]
    public void AssignSides_RefusesSidesHeldByOthersAndUnknownSides()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        Assert.Equal(HostSession.SideHeldText, r.H.AssignSides(w.PlayerId, new[] { "red" }));            // the host's
        Assert.Equal(HostSession.SideHeldText, r.H.AssignSides(r.H.Seats.Host.PlayerId, new[] { "blue" })); // Bob's
        Assert.NotNull(r.H.AssignSides(w.PlayerId, new[] { "purple" }));
        Assert.NotNull(r.H.AssignSides(w.PlayerId, Array.Empty<string>()));
        Assert.Equal(HostSession.NoSuchSeatText, r.H.AssignSides("nobody", new[] { "green" }));
        Assert.Equal(new[] { "blue" }, r.H.Seats.ByPlayer(w.PlayerId)!.Sides);
        Assert.Equal(new[] { "red" }, r.H.Seats.Host.Sides);
    }

    [Fact]
    public void AssignSides_HostTakesAFreeSide_ViewFollows()
    {
        var r = Open();
        Assert.Null(r.H.AssignSides(r.H.Seats.Host.PlayerId, new[] { "red", "green" }));
        Assert.True(r.S.ViewSides!.SetEquals(new[] { "red", "green" }));
        Assert.Equal(new[] { "Blue" }, r.Relay.Listings.Last().FreeSides);
    }

    [Fact]
    public void EndGame_TellsEveryConnectedGuest()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        var cara = r.Relay.Guest("m2");
        JoinAs(r, cara, "Cara");
        r.H.EndGame();
        Assert.Equal(HostSession.HostEndedText, bob.Of<Kicked>().Last().Reason);
        Assert.Equal(HostSession.HostEndedText, cara.Of<Kicked>().Last().Reason);
    }

    [Fact]
    public void Leave_BackToSoloWithTheFullView()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        Assert.True(r.S.ViewSides!.SetEquals(new[] { "red" }));
        Assert.Equal(2, r.S.Game!.State.Players.Count);
        var snaps = bob.Of<Snapshot>().Count();
        r.H.Leave();
        r.D.Pump();
        Assert.Null(r.S.ViewSides);                                   // no lingering one-side filter
        Assert.Empty(r.S.Game!.State.Players);                        // a solo game again
        Assert.Equal(HostSession.HostEndedText, bob.Of<Kicked>().Last().Reason);
        Assert.Equal(snaps, bob.Of<Snapshot>().Count());              // nothing more reaches the guest
        Assert.DoesNotContain(SecretStock, AllReceivedText(bob));
    }

    [Fact]
    public void HostingSavedGame_HostKeepsOwnSides_OthersBecomeOpenSeats()
    {
        var r = Open(new[] { new PlayerDef("p7", "Old Bob", new[] { "blue", "green" }), Alice });
        Assert.Equal("p1", r.H.Seats.Host.PlayerId);   // named like the listing's host
        Assert.Equal(new[] { "red" }, r.H.Seats.Host.Sides);
        Assert.Equal(new[] { "p1" }, r.S.Game!.State.Players.Select(p => p.Id));
        Assert.Equal(new[] { "Blue", "Green" }, r.Relay.Listings.Last().FreeSides);
        Assert.Equal(new[] { "red" }, r.S.ViewSides!);
    }

    // ---- sides ----

    [Fact]
    public void ChooseSides_Invalid_Refused()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        var before = Json(r.S.Game!.State);
        foreach (var bad in new[] { new[] { "red" }, new[] { "purple" }, Array.Empty<string>(), new[] { "blue", "green", "red", "x" } })
            Send(r, bob, new ChooseSides(bad));
        var results = bob.Of<NetResult>().ToList();
        Assert.Equal(4, results.Count);
        Assert.All(results, x => { Assert.False(x.Ok); Assert.Equal(Guid.Empty, x.Id); Assert.False(string.IsNullOrEmpty(x.Message)); });
        Assert.Equal("Red is held by more than one player.", results[0].Message);
        Assert.Equal(before, Json(r.S.Game!.State));
        Assert.Empty(bob.Of<Snapshot>());
    }

    [Fact]
    public void ChooseSides_Valid_SetsPlayersWithHostSeat()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        Assert.True(bob.Of<NetResult>().Single().Ok);
        var players = r.S.Game!.State.Players;
        Assert.Equal(new[] { (r.H.Seats.Host.PlayerId, "Alice", "red"), (w.PlayerId, "Bob", "blue") },
            players.Select(p => (p.Id, p.Name, string.Join(",", p.Sides))));
        Assert.Single(bob.Of<Snapshot>());
        Assert.Equal(new[] { "Green" }, r.Relay.Listings.Last().FreeSides);
    }

    // ---- snapshots ----

    [Fact]
    public void Snapshot_IsPlayerViewForGuestSides()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        var blue = new HashSet<string> { "blue" };
        var first = bob.Of<Snapshot>().Single();
        Assert.Equal(Json(PlayerView.For(r.S.Game!.State, r.S.Rules, r.S.Model, blue)), SnapshotJson(first));

        // A host-local change reaches the guest as a newer filtered snapshot.
        r.S.Game!.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedOne), "water", 73737, 73738, "manual", null, null, "more", null));
        r.H.FlushSnapshots();
        var second = bob.Of<Snapshot>().Last();
        Assert.True(second.Version > first.Version);
        Assert.Equal(Json(PlayerView.For(r.S.Game!.State, r.S.Rules, r.S.Model, blue)), SnapshotJson(second));

        // Nothing the guest received holds red's private values.
        var all = AllReceivedText(bob);
        Assert.DoesNotContain(SecretStock, all);
        Assert.DoesNotContain(SecretWater, all);
        Assert.DoesNotContain("73738", all);
        // ...while the host's full state does (the test would prove nothing otherwise).
        Assert.Contains(SecretStock, Json(r.S.Game!.State));
        Assert.Contains("73738", Json(r.S.Game!.State));
    }

    [Fact]
    public void Snapshot_Debounced_Coalesced()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        var n = bob.Of<Snapshot>().Count();
        for (var i = 0; i < 5; i++) r.S.Game!.Execute(new AddLogLine(LogKind.System, "tick " + i));
        Assert.Equal(n, bob.Of<Snapshot>().Count());               // nothing yet: debounced
        // The timer flushes (spaced at least 200 ms after the choice's snapshot): one coalesced snapshot.
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (bob.Of<Snapshot>().Count() == n && DateTime.UtcNow < end) r.D.WaitAndPump(TimeSpan.FromSeconds(1));
        Assert.Equal(n + 1, bob.Of<Snapshot>().Count());
        r.D.WaitAndPump(TimeSpan.FromMilliseconds(400));
        Assert.Equal(n + 1, bob.Of<Snapshot>().Count());
        Assert.Contains("tick 4", SnapshotJson(bob.Of<Snapshot>().Last()));
    }

    [Fact]
    public void Reattach_MissingDisconnected_PresentGetSnapshots()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var carol = r.Relay.Guest("m2");
        var wb = JoinAs(r, bob, "Bob");
        var wc = JoinAs(r, carol, "Carol");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        Send(r, carol, new ChooseSides(new[] { "green" }));
        var bobSnaps = bob.Of<Snapshot>().Count();
        var carolSnaps = carol.Of<Snapshot>().Count();
        r.Relay.Host.RaiseDisconnected("lost");
        r.D.Pump();
        Assert.False(r.H.RelayConnected);
        r.Relay.Host.RaiseReconnected();
        r.Relay.Host.RaiseMembersListed(new[] { "m1" });
        r.D.Pump();
        Assert.True(r.H.RelayConnected);
        Assert.True(r.H.Seats.ByPlayer(wb.PlayerId)!.Connected);
        Assert.False(r.H.Seats.ByPlayer(wc.PlayerId)!.Connected);
        Assert.Equal(bobSnaps + 1, bob.Of<Snapshot>().Count());
        Assert.Equal(carolSnaps, carol.Of<Snapshot>().Count());
    }

    // ---- intents and chat ----

    [Fact]
    public void Intent_Executed_ResultToSenderOnly()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var carol = r.Relay.Guest("m2");
        JoinAs(r, bob, "Bob");
        JoinAs(r, carol, "Carol");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        Send(r, carol, new ChooseSides(new[] { "green" }));
        var carolBefore = carol.Inbox.Count;

        var id = Guid.NewGuid();
        Send(r, bob, new IntentMsg(id, Intent(new LedgerEditIntent(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 4, "filled"))));
        var res = bob.Of<NetResult>().Last();
        Assert.Equal(id, res.Id);
        Assert.True(res.Ok, res.Message);
        Assert.Equal(4, Ledger.Get(r.S.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        Assert.Equal(carolBefore, carol.Inbox.Count);   // no result for Carol (her next snapshot is all she gets)

        // Another side's unit: refused, to Bob only.
        var id2 = Guid.NewGuid();
        Send(r, bob, new IntentMsg(id2, Intent(new LedgerEditIntent(LedgerKey.Piece(PhaseFixture.RedOne), "water", 1, "drain"))));
        var refused = bob.Of<NetResult>().Last();
        Assert.Equal(id2, refused.Id);
        Assert.False(refused.Ok);
        Assert.Equal(carolBefore, carol.Inbox.Count);

        // Garbage intent JSON is refused, not thrown.
        Send(r, bob, new IntentMsg(Guid.NewGuid(), "{not json"));
        Assert.Equal(IntentExecutor.NotUnderstoodText, bob.Of<NetResult>().Last().Message);
    }

    [Fact]
    public void Intent_FromUnseatedMember_Dropped()
    {
        var r = Open();
        var eve = r.Relay.Guest("m7");
        var before = Json(r.S.Game!.State);
        Send(r, eve, new IntentMsg(Guid.NewGuid(), Intent(new ChatIntent("hi"))));
        Assert.Empty(eve.Inbox);
        Assert.Equal(before, Json(r.S.Game!.State));
        Assert.Equal(1, r.H.Dropped);
    }

    [Fact]
    public void Chat_ReachesAll()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var carol = r.Relay.Guest("m2");
        JoinAs(r, bob, "Bob");
        JoinAs(r, carol, "Carol");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        Send(r, carol, new ChooseSides(new[] { "green" }));

        Send(r, bob, new IntentMsg(Guid.NewGuid(), Intent(new ChatIntent("Hello from blue"))));
        Send(r, carol, new Net.Chat("ignored", "Hello from green"));
        Assert.True(r.H.Chat("Hello from red").Ok);
        r.H.FlushSnapshots();

        foreach (var g in new[] { bob, carol })
        {
            var log = SnapshotJson(g.Of<Snapshot>().Last());
            Assert.Contains("Bob: Hello from blue", log);
            Assert.Contains("Carol: Hello from green", log);
            Assert.Contains("Alice: Hello from red", log);
        }
        Assert.Equal(3, r.S.Game!.State.Log.Count(l => l.Kind == LogKind.Chat));
    }

    [Fact]
    public void RateLimit_ExcessRefused_NoDisconnect()
    {
        var r = Open();
        var now = TimeSpan.FromHours(1);
        r.H.Now = () => now;
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));   // uses one intent slot

        for (var i = 0; i < 8; i++) Send(r, bob, new IntentMsg(Guid.NewGuid(), Intent(new ChatIntent("c" + i))));
        var chats = bob.Of<NetResult>().Skip(1).ToList();
        Assert.Equal(5, chats.Count(x => x.Ok));
        Assert.Equal(1, chats.Count(x => !x.Ok && x.Message == HostSession.SlowDownText));   // one "Slow down." per second; the rest unanswered
        Assert.Equal(6, chats.Count);

        for (var i = 0; i < 20; i++) Send(r, bob, new IntentMsg(Guid.NewGuid(), Intent(new TickIntent(99, true))));
        var ticks = bob.Of<NetResult>().Skip(7).ToList();
        Assert.Equal(Seats.MaxIntentsPerSecond - 9, ticks.Count);   // 9 slots used: 1 choice + 8 chat intents; the rest dropped
        Assert.DoesNotContain(ticks, x => x.Message == HostSession.SlowDownText);
        Assert.True(r.H.Seats.ByPlayer(w.PlayerId)!.Connected);
        Assert.Empty(bob.Of<Kicked>());

        now += TimeSpan.FromSeconds(1.1);   // the window slides: accepted again
        Send(r, bob, new IntentMsg(Guid.NewGuid(), Intent(new ChatIntent("later"))));
        Assert.True(bob.Of<NetResult>().Last().Ok);
    }

    [Fact]
    public void Flood_BoundedHostSends()
    {
        var r = Open();
        var now = TimeSpan.FromHours(1);
        r.H.Now = () => now;
        // An unseated member spamming joins: one answer per 2 s.
        var spam = r.Relay.Guest("m5");
        for (var i = 0; i < 50; i++) Send(r, spam, new JoinRequest("Spam" + i, null, r.S.Archive.Sha256));
        Assert.Single(spam.Inbox);
        // A seated guest spamming intents, chat and side choices for one second: at most 20 answers plus one "Slow down.".
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        var before = bob.Inbox.Count;
        for (var i = 0; i < 300; i++)
        {
            Send(r, bob, new IntentMsg(Guid.NewGuid(), Intent(new TickIntent(99, true))));
            Send(r, bob, new Net.Chat("x", "hi"));
            Send(r, bob, new ChooseSides(new[] { "green" }));
        }
        Assert.InRange(bob.Inbox.Count - before, 1, Seats.MaxIntentsPerSecond + 1);
        Assert.Empty(bob.Of<Kicked>());
    }

    [Fact]
    public void Snapshots_SpacedPerGuest()
    {
        var r = Open();
        var now = TimeSpan.FromHours(1);
        r.H.Now = () => now;
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));   // snapshot at t0
        var n = bob.Of<Snapshot>().Count();
        r.S.Game!.Execute(new AddLogLine(LogKind.System, "one"));
        r.H.FlushDue();
        Assert.Equal(n, bob.Of<Snapshot>().Count());       // within 200 ms of the last one: waits
        now += TimeSpan.FromMilliseconds(150);
        r.H.FlushDue();
        Assert.Equal(n, bob.Of<Snapshot>().Count());
        now += TimeSpan.FromMilliseconds(60);
        r.H.FlushDue();
        Assert.Equal(n + 1, bob.Of<Snapshot>().Count());
        r.H.FlushDue();                                     // nothing new: nothing sent
        Assert.Equal(n + 1, bob.Of<Snapshot>().Count());
    }
    [Fact]
    public void GarbageFrames_Dropped_NeverThrow()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        r.Relay.Host.Inject("m1", Array.Empty<byte>());
        r.Relay.Host.Inject("m1", Encoding.UTF8.GetBytes("{\"$t\":\"Nope\"}"));
        r.Relay.Host.Inject("m1", RandomNumberGenerator.GetBytes(300));
        r.Relay.Host.Inject("m1", Framing.Encode<GamePayload>(new Snapshot(1, new byte[] { 1 })));   // host-bound only
        r.D.Pump();
        Assert.Equal(4, r.H.Dropped);
        Assert.True(r.H.Seats.All[1].Connected);
    }

    [Fact]
    public void ChooseSides_Again_Refused_KeepsSeat()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        Send(r, bob, new ChooseSides(new[] { "green" }));
        var last = bob.Of<NetResult>().Last();
        Assert.False(last.Ok);
        Assert.Equal(HostSession.SidesFixedText, last.Message);
        Assert.Equal(new[] { "blue" }, r.H.Seats.ByPlayer(w.PlayerId)!.Sides);
        Assert.True(r.H.Seats.ByPlayer(w.PlayerId)!.Connected);
        r.S.Game!.Execute(new AddLogLine(LogKind.System, "later"));
        r.H.FlushSnapshots();
        Assert.Equal(Json(PlayerView.For(r.S.Game!.State, r.S.Rules, r.S.Model, new HashSet<string> { "blue" })),
            SnapshotJson(bob.Of<Snapshot>().Last()));
        Assert.DoesNotContain(SecretGreen, AllReceivedText(bob));
        Assert.DoesNotContain(SecretStock, AllReceivedText(bob));
    }

    [Fact]
    public void NoSideYet_IntentsRefused_ChatAllowed()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        var before = Ledger.Get(r.S.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water");
        Send(r, bob, new IntentMsg(Guid.NewGuid(), Intent(new LedgerEditIntent(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 4, "x"))));
        Send(r, bob, new IntentMsg(Guid.NewGuid(), Intent(new DiceIntent(new DiceSpec(1, 6, 0), "Roll"))));
        Assert.All(bob.Of<NetResult>(), x => Assert.Equal(HostSession.ChooseSideFirstText, x.Message));
        Assert.Equal(before, Ledger.Get(r.S.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        Send(r, bob, new IntentMsg(Guid.NewGuid(), Intent(new ChatIntent("hello"))));
        Assert.True(bob.Of<NetResult>().Last().Ok);
        Assert.Contains(r.S.Game!.State.Log, l => l.Kind == LogKind.Chat && l.Text == "Bob: hello");
    }

    [Fact]
    public void FreeSeat_ConnectedWithoutSides_Allowed()
    {
        var r = Open();
        var carol = r.Relay.Guest("m2");
        var w = JoinAs(r, carol, "Carol");
        Assert.True(r.H.FreeSeat(w.PlayerId));
        Assert.Equal(HostSession.SeatFreedText, carol.Of<Kicked>().Single().Reason);
        Assert.Single(r.H.Seats.All);
        Send(r, carol, new IntentMsg(Guid.NewGuid(), Intent(new ChatIntent("still here?"))));
        Assert.DoesNotContain(r.S.Game!.State.Log, l => l.Kind == LogKind.Chat);
    }

    [Fact]
    public void Rejoin_WhileOldConnectionBound_OldKicked()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        var again = r.Relay.Guest("m9");
        var w2 = JoinAs(r, again, "Bob", w.RejoinToken);
        Assert.Equal(w.PlayerId, w2.PlayerId);
        Assert.Equal(HostSession.SignedInElsewhereText, bob.Of<Kicked>().Single().Reason);
        Assert.Equal("m9", r.H.Seats.ByPlayer(w.PlayerId)!.MemberId);
        Assert.Null(r.H.Seats.ByMember("m1"));
        var dropped = r.H.Dropped;
        Send(r, bob, new IntentMsg(Guid.NewGuid(), Intent(new ChatIntent("ghost"))));
        Assert.Equal(dropped + 1, r.H.Dropped);
        Assert.Single(again.Of<Snapshot>());
    }

    [Fact]
    public void RulesApplied_SentToGuests_BeforeNextSnapshot()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        r.S.UpdateRules(p => p with { Name = "Edited rules" });
        r.H.FlushSnapshots();
        var msgs = bob.Messages.ToList();
        var rc = msgs.FindLastIndex(m => m is RulesChanged);
        Assert.True(rc >= 0);
        Assert.Equal("Edited rules", RulesPackIo.FromBytes(((RulesChanged)msgs[rc]).RulesPack).Name);
        Assert.True(msgs.FindLastIndex(m => m is Snapshot) > rc);
    }

    [Fact]
    public void FormulaVerdict_HiddenInput_NoPrivateBytes()
    {
        var r = Open(difficulty: Difficulty.Hardcore);
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        var id = Guid.NewGuid();
        Send(r, bob, new IntentMsg(id, Intent(new FormulaIntent("f-mix", PhaseFixture.BlueOne, null,
            new Dictionary<string, string> { ["W"] = "999", ["S"] = "1" }, false))));
        var res = bob.Of<NetResult>().Single(x => x.Id == id);
        Assert.False(res.Ok);
        var verdict = JsonSerializer.Deserialize<FormulaVerdict>(res.VerdictJson!, QuartermasterJson.Options)!;
        Assert.Equal(FormulaBox.Hidden, verdict.Boxes.Single(b => b.Var == "S").State);
        Assert.Equal(FormulaBox.Wrong, verdict.Boxes.Single(b => b.Var == "W").State);
        r.H.FlushSnapshots();
        var all = AllReceivedText(bob);
        Assert.DoesNotContain(SecretStock, all);
        Assert.DoesNotContain(SecretWater, all);
        Assert.DoesNotContain(SecretGreen, all);
    }

    [Fact]
    public void Names_NormalisedBeforeCompare_NullHostNameSafe()
    {
        var r = Open();
        JoinAs(r, r.Relay.Guest("m1"), "Bob");
        foreach (var (member, name) in new[] { ("m2", "B​ob"), ("m3", "Ｂｏｂ"), ("m4", "‮Bob‬") })
        {
            var g = r.Relay.Guest(member);
            Send(r, g, new JoinRequest(name, null, r.S.Archive.Sha256));
            Assert.Equal(HostSession.NameTakenText("Bob"), Assert.Single(g.Of<Kicked>()).Reason);
        }
        var zw = r.Relay.Guest("m5");
        Send(r, zw, new JoinRequest("​‍", null, r.S.Archive.Sha256));
        Assert.Equal(HostSession.NoNameText, Assert.Single(zw.Of<Kicked>()).Reason);
        Assert.Equal("Carol", ChatText.CleanName("  Ca⁦rol⁩ "));
        Assert.Equal(ChatText.MaxName, ChatText.CleanName(new string('x', 100))!.Length);
        Assert.Null(ChatText.CleanName(null));

        var anon = Open(hostName: null!);
        Assert.Equal("Host", anon.H.Seats.Host.Name);
    }
    [Fact]
    public void RelayTransport_TryOpen_DropsGarbage()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = Encoding.UTF8.GetBytes("payload");
        Assert.Equal(plain, RelayTransport.TryOpen(key, Secrets.Seal(key, plain)));
        Assert.Null(RelayTransport.TryOpen(RandomNumberGenerator.GetBytes(32), Secrets.Seal(key, plain)));
        Assert.Null(RelayTransport.TryOpen(key, RandomNumberGenerator.GetBytes(64)));
        Assert.Null(RelayTransport.TryOpen(key, new byte[3]));
        Assert.Null(RelayTransport.TryOpen(key, null));
        Assert.Null(RelayTransport.TryOpen(key, new byte[Framing.MaxFrame + 1]));
        var tampered = Secrets.Seal(key, plain);
        tampered[^1] ^= 1;
        Assert.Null(RelayTransport.TryOpen(key, tampered));
    }

    [Fact]
    public void RulesPack_Bytes_RoundTrip()
    {
        var pack = Pack(new string('a', 64));
        var back = RulesPackIo.FromBytes(RulesPackIo.ToBytes(pack));
        Assert.Equal(RulesPackIo.ComputeSha256(pack), RulesPackIo.ComputeSha256(back));
        Assert.Throws<InvalidDataException>(() => RulesPackIo.FromBytes(new byte[] { 1, 2, 3 }));
    }

    // ---- final review fixes ----

    [Fact]
    public void HostUndoesSetPlayers_GuestKeepsSides_CanAct_ListRestoredOnNextChange()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        var g = r.S.Game!;
        Assert.Contains(g.State.Players, p => p.Id == w.PlayerId);

        g.Undo();   // the host undoes the SetPlayers that seated Bob
        r.D.Pump();
        Assert.DoesNotContain(g.State.Players, p => p.Id == w.PlayerId);
        Assert.True(g.CanRedo);   // still walking back: nothing appended, the redo stays
        r.H.FlushSnapshots();
        var view = JsonSerializer.Deserialize<GameState>(SnapshotJson(bob.Of<Snapshot>().Last()), QuartermasterJson.Options)!;
        Assert.Equal(new[] { "blue" }, view.Players.Single(p => p.Id == w.PlayerId).Sides);   // the seats are the truth for guests

        var id = Guid.NewGuid();
        Send(r, bob, new IntentMsg(id, Intent(new LedgerEditIntent(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 4, "filled"))));
        var res = bob.Of<NetResult>().Last();
        Assert.Equal(id, res.Id);
        Assert.True(res.Ok, res.Message);
        r.D.Pump();
        // The forward change cleared redo; the game's player list follows the seats again (a new SetPlayers).
        Assert.Equal(new[] { "blue" }, g.State.Players.Single(p => p.Id == w.PlayerId).Sides);
        Assert.False(g.InAction);
    }

    [Fact]
    public void Intent_Replayed_IgnoredWithoutReply()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        var id = Guid.NewGuid();
        var msg = new IntentMsg(id, Intent(new ChatIntent("once")));
        Send(r, bob, msg);
        var answers = bob.Of<NetResult>().Count();
        var dropped = r.H.Dropped;
        Send(r, bob, msg);   // a replay of the same message
        Send(r, bob, msg);
        Assert.Equal(answers, bob.Of<NetResult>().Count());
        Assert.Equal(dropped + 2, r.H.Dropped);
        Assert.Equal(1, r.S.Game!.State.Log.Count(l => l.Text?.Contains("once") == true));
        // The memory is bounded: after 256 other ids the first is forgotten (and would run again).
        var seat = r.H.Seats.Guests.Single();
        for (var i = 0; i < HostSession.IntentIdMemory; i++) seat.RememberIntent(Guid.NewGuid());
        Assert.False(seat.SeenIntent(id));
    }

    [Fact]
    public void Snapshot_TooLarge_GuestPaused_WarningWithoutContent()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        JoinAs(r, bob, "Bob");
        r.H.MaxSnapshotJson = 1000;   // any real view is larger
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        Assert.Empty(bob.Of<Snapshot>());
        Assert.Equal(HostSession.TooLargeText, Assert.Single(bob.Of<Paused>()).Reason);
        var warning = Assert.Single(r.S.Log.Items, e => e.Category == "online" && e.Message.Contains("too large", StringComparison.Ordinal));
        Assert.Equal(Quartermaster.Vassal.DiagnosticSeverity.Warning, warning.Severity);
        Assert.DoesNotContain(SecretWater, warning.Message);
        r.H.FlushSnapshots();
        Assert.Single(bob.Of<Paused>());   // told once until a view goes through
        r.H.MaxSnapshotJson = Secrets.MaxPlain;
        r.H.FlushSnapshots();
        Assert.Single(bob.Of<Snapshot>());
    }

    [Fact]
    public void Rejoin_WelcomeCarriesHeldSides_NewSeatNone()
    {
        var r = Open();
        var bob = r.Relay.Guest("m1");
        var w = JoinAs(r, bob, "Bob");
        Assert.Empty(w.Sides ?? Array.Empty<string>());
        Send(r, bob, new ChooseSides(new[] { "blue" }));
        r.Relay.Drop("m1");
        r.D.Pump();
        var back = r.Relay.Guest("m9");
        var w2 = JoinAs(r, back, "Bob", w.RejoinToken);
        Assert.Equal(new[] { "blue" }, w2.Sides);
    }

    [Fact]
    public void RelayTransport_TooLargeToSeal_FaultsWithProtocolException()
    {
        Assert.True(RelayTransport.FitsRelayFrame("m1", 1000));
        Assert.False(RelayTransport.FitsRelayFrame("m1", Framing.MaxFrame));
        Assert.False(RelayTransport.FitsRelayFrame("m1", Framing.MaxFrame / 4 * 3));   // base64 would pass the frame
        using var t = new RelayTransport(new RelayClient(new PinStore(Path.Combine(_tmp, "pins.json"))), RandomNumberGenerator.GetBytes(32));
        // Random bytes do not compress: sealed, this is just under a frame, and base64 makes it larger than one.
        var task = t.Send("m1", RandomNumberGenerator.GetBytes(Framing.MaxFrame - 64 * 1024));
        Assert.True(task.IsFaulted);
        Assert.IsType<ProtocolException>(task.Exception!.InnerException);
        Assert.Equal(1, t.SendFailures);
    }

    [Fact]
    public void NewGameRequest_PasswordHidden_AndDroppedOnward()
    {
        var req = new NewGameRequest(GameMode.Online, null, Difficulty.SemiAuto, null, "Lobby", "hunter2-secret");
        Assert.DoesNotContain("hunter2-secret", req.ToString());
        Assert.Contains("(hidden)", req.ToString());
        var onward = req.WithoutPassword();
        Assert.Null(onward.Password);
        Assert.Equal("Lobby", onward.LobbyName);
    }
}
