using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>B10 online: the host runs a phase's rows for allied counters; a seat outside the alliance gets nothing of them.</summary>
public class AllianceScopeOnlineTests : AllianceScopeTestBase
{
    private static void Send(QueueDispatcher d, MemoryTransport guest, GamePayload p) { guest.SendPayload(p); d.Pump(); }

    private static GameState SnapshotState(Snapshot snap) =>
        JsonSerializer.Deserialize<GameState>(Gzip.DecompressBounded(snap.StateJsonGz, int.MaxValue), QuartermasterJson.Options)!;

    [Fact]
    public void Guest_ally_move_fires_acting_entry_on_host()
    {
        var s = Session();
        var c = Controller(s);
        var x = new IntentExecutor(s, c);
        ToFoot(s, c);
        var mark = s.Game!.Log.Count;

        var r = x.Execute("p3", Wagons, new MoveIntent(new[] { Move("nw1", 130) }));

        Assert.True(r.Ok, r.Message);
        Assert.Equal(new PointD(130, 50), s.Game.State.Pieces["nw1"].Pos);
        var d = Assert.Single(FuelDeltas(s, mark));   // the Acting row, once
        Assert.Equal("p:nw1", d.Key);
        Assert.Equal(36, Fuel(s.Game.State, "nw1"));
    }

    [Fact]
    public void Guest_south_snapshot_unchanged_by_allied_runs()
    {
        var s = Session(players: new[] { Alice });
        var c = Controller(s);
        var relay = new MemoryRelay();
        var q = new QueueDispatcher();
        var listing = new LobbyListing("", "Test lobby", Alice.Name, s.Archive.Sha256, "Fixture",
            new[] { "Northern Foot", "Southern Compact", "Northern Wagons", "Northern Balloons" }, new[] { "south", "north-supply", "north-air" }, 1, 4, false,
            Protocol.ProtocolVersion);
        var h = new HostSession(s, new IntentExecutor(s, c), relay.Host, "pw", "abc-234", listing, q.Post);
        q.Pump();
        try
        {
            var bob = relay.Guest("m1");
            Send(q, bob, new JoinRequest("Bob", null, s.Archive.Sha256));
            Send(q, bob, new ChooseSides(new[] { "south" }));
            c.Start();
            h.FlushSnapshots();
            q.Pump();
            var before = SnapshotState(bob.Of<Snapshot>().Last());

            c.Advance();   // Foot Supply: water for every Northern Pact counter
            Assert.Equal(9, Water(s.Game!.State, "nw1"));
            Assert.Equal(9, Water(s.Game.State, "nb1"));
            h.FlushSnapshots();
            q.Pump();
            var after = SnapshotState(bob.Of<Snapshot>().Last());

            Assert.Equal(new[] { "south" }, after.ViewSides!);
            AssertNothingOfNorth(after);
            foreach (var id in new[] { "sc1", "nw2", "crate" }) Assert.Equal(Water(before, id), Water(after, id));
            Assert.Null(Water(after, "sc1"));
            Assert.Null(Water(after, "nw2"));
        }
        finally { h.Dispose(); }
    }
}
