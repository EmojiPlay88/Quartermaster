using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using IntentResult = Quartermaster.Render.IntentResult;

namespace Quartermaster.Render.Tests;

/// <summary>B9 #13a online and across machines: the host sends and checks every guest intent with Ford Dump's current owner; a turn file's
/// receiver and a pack test see by it; saves are unchanged.</summary>
[Collection(AppDataRootEnv.Name)]
public class HolderOwnerOnlineTests : HolderOwnerTestBase
{
    private static readonly Dictionary<string, string> None = new();
    private static readonly PlayerDef HostNorth = new("p1", "Alice", new[] { "north" });
    private const string OtherSideValue = "That value belongs to a side you don't hold.";
    private const string OtherSideHolder = "That formula writes to a holder that belongs to a side you don't hold.";

    private (ModuleSession S, PhaseController C, IntentExecutor X) Host()
    {
        var s = B9Session();
        var c = Controller(s);
        c.Start();   // Weather: no side acts
        return (s, c, new IntentExecutor(s, c));
    }

    private (ModuleSession S, HostSession H, MemoryRelay R, QueueDispatcher D) Hosting()
    {
        var s = B9Session(players: new[] { HostNorth });
        var c = Controller(s);
        var relay = new MemoryRelay();
        var d = new QueueDispatcher();
        var listing = new LobbyListing("", "Test lobby", HostNorth.Name, s.Archive.Sha256, "Fixture",
            new[] { "Northern League", "Southern Compact", "Hill Clans" }, new[] { "south", "hills" }, 1, 3, false, Protocol.ProtocolVersion);
        var h = new HostSession(s, new IntentExecutor(s, c), relay.Host, "pw", "abc-234", listing, d.Post);
        d.Pump();
        return (s, h, relay, d);
    }

    private static void Send(QueueDispatcher d, MemoryTransport guest, GamePayload p) { guest.SendPayload(p); d.Pump(); }

    private static GameState SnapshotState(Snapshot snap) =>
        JsonSerializer.Deserialize<GameState>(Gzip.DecompressBounded(snap.StateJsonGz, int.MaxValue), QuartermasterJson.Options)!;

    private static IntentResult Edit(IntentExecutor x, string who, IReadOnlySet<string> sides, double after) =>
        x.Execute(who, sides, new LedgerEditIntent(LedgerKey.Holder(Ford), "p-stores", after, "count"));

    [Fact]
    public void Guest_snapshot_follows_current_owner()
    {
        var (s, h, r, d) = Hosting();
        var bob = r.Guest("m1");
        Send(d, bob, new JoinRequest("Bob", null, s.Archive.Sha256));
        Send(d, bob, new ChooseSides(new[] { "south" }));
        try
        {
            var before = SnapshotState(bob.Of<Snapshot>().Last());
            Assert.Null(Stores(before));
            Assert.Equal("north", Ownership.SideOfHolder(Ford, s.Rules!, before));   // the guest works the owner out from its own view
            var count = bob.Of<Snapshot>().Count();
            using (s.Game!.BeginAction()) s.Game.Execute(Capture(s.Rules!));
            h.FlushSnapshots();   // the host's next snapshot (normally after the spacing)
            d.Pump();
            Assert.True(bob.Of<Snapshot>().Count() > count);
            var after = SnapshotState(bob.Of<Snapshot>().Last());
            Assert.Equal(FordStores, Stores(after));
            Assert.Equal("south", Ownership.SideOfHolder(Ford, s.Rules!, after));
            Assert.Null(Stores(s.View!));   // the host (north) no longer sees them
        }
        finally { h.Dispose(); }
    }

    [Fact]
    public void Guest_manual_edit_of_captured_holder_refused_old_text()
    {
        var (s, _, x) = Host();
        var ok = Edit(x, "p1", North, 5);
        Assert.True(ok.Ok, ok.Message);
        s.Game!.Execute(Capture(s.Rules!));
        var no = Edit(x, "p1", North, 6);
        Assert.False(no.Ok);
        Assert.Equal(OtherSideValue, no.Message);
        Assert.Equal(5, Stores(s.Game.State));
        var bob = Edit(x, "p2", South, 7);
        Assert.True(bob.Ok, bob.Message);
        Assert.Equal(7, Stores(s.Game.State));
    }

    [Fact]
    public void Guest_holder_output_to_captured_holder_refused()
    {
        var (s, _, x) = Host();
        s.Game!.Execute(Capture(s.Rules!));
        var no = x.Execute("p1", North, new FormulaIntent("f-haul", null, null, None, true));
        Assert.False(no.Ok);
        Assert.Equal(OtherSideHolder, no.Message ?? no.Verdict?.Message);
        Assert.Equal(FordStores, Stores(s.Game.State));
    }

    [Fact]
    public void Guest_holder_output_after_capture_allowed_for_new_owner()
    {
        var (s, _, x) = Host();
        var before = x.Execute("p2", South, new FormulaIntent("f-haul", null, null, None, true));
        Assert.False(before.Ok);
        Assert.Equal(OtherSideHolder, before.Message ?? before.Verdict?.Message);
        s.Game!.Execute(Capture(s.Rules!));
        var ok = x.Execute("p2", South, new FormulaIntent("f-haul", null, null, None, true));
        Assert.True(ok.Ok, ok.Message ?? ok.Verdict?.Message);
        Assert.Equal(FordStores + 2, Stores(s.Game.State));
    }

    [Fact]
    public void Host_preview_verdict_masked_by_current_owner()
    {
        var (s, _, x) = Host();
        FormulaIntent Preview() => new("f-ford", null, null, None, true, Preview: true);
        var north = x.Execute("p1", North, Preview());
        Assert.Equal(FordStores.ToString(System.Globalization.CultureInfo.InvariantCulture), north.Verdict!.Values!["S"]);
        Assert.False((x.Execute("p2", South, Preview()).Verdict?.Values ?? new Dictionary<string, string>()).ContainsKey("S"));
        s.Game!.Execute(Capture(s.Rules!));
        Assert.False((x.Execute("p1", North, Preview()).Verdict?.Values ?? new Dictionary<string, string>()).ContainsKey("S"));
        Assert.Equal(FordStores.ToString(System.Globalization.CultureInfo.InvariantCulture), x.Execute("p2", South, Preview()).Verdict!.Values!["S"]);
        Assert.Null(Gate(s.Game.State));   // a preview applies nothing
    }

    [Fact]
    public void Turn_file_view_follows_current_owner()
    {
        using var fx = PhaseFixture.Module();
        var vmod = fx.Build();
        string sha;
        using (var a = Quartermaster.Vassal.VmodArchive.Open(vmod)) sha = a.Sha256;
        var rules = Path.Combine(Tmp, "p.qrules");
        var pack = B9Pack(sha);
        RulesPackIo.Save(pack, rules);
        using var h = PackHarness.Open(vmod, rules, Path.Combine(Tmp, "hcache"));
        h.NewGame();
        h.Session.Game!.Execute(new LedgerDelta(LedgerKey.Holder(Ford), "p-stores", 0, FordStores, "manual", null, null, null, null));
        Assert.Equal(FordStores, Stores(h.ViewFor("north")));
        Assert.Null(Stores(h.ViewFor("south")));
        h.Session.Game.Execute(Capture(pack));
        Assert.Null(Stores(h.ViewFor("north")));
        Assert.Equal(FordStores, Stores(h.ViewFor("south")));
        Assert.Equal(FordStores, Stores(h.ViewFor("hills")));   // the Delta Pact

        // A turn file: Alice (north) captures the dump and sends the turn to Bob (south), who sees its stores on his machine.
        var alice = B9Session();
        var g = alice.Game!;
        using (g.BeginAction()) g.Execute(Capture(alice.Rules!));
        var t = TurnSender.Build(alice, Bob.Id, Identity("alice", g.State.GameId!, Alice.Id));
        var bob = B9Session();
        var model = bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
        model.PlayAll();
        Assert.Null(model.Error);
        Assert.Equal(new[] { "hills", "south" }, bob.ViewSides!.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("south", Owner(bob));
        Assert.Equal(FordStores, Stores(bob.View!));
        Assert.True(bob.CanEditLedger(LedgerKey.Holder(Ford)));
    }

    [Fact]
    public void Save_bytes_unchanged_with_owner_expr_pack()
    {
        var s = B9Session();
        using (s.Game!.BeginAction()) s.Game.Execute(Capture(s.Rules!));
        var first = Path.Combine(Tmp, "a.qsave");
        var second = Path.Combine(Tmp, "b.qsave");
        NativeSave.Write(first, s.Game);
        var back = NativeSave.Read(first);
        Assert.Equal("south", Ownership.SideOfHolder(Ford, s.Rules!, back.State));
        NativeSave.Write(second, back);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        Assert.DoesNotContain("\"owner", File.ReadAllText(first), StringComparison.OrdinalIgnoreCase);   // no owner is stored: it is worked out
    }

    private TurnIdentity Identity(string who, string gameId, string? player = null)
    {
        var id = TurnIdentity.Load(gameId, Path.Combine(Tmp, "turns-" + who), TestProtector.Create());
        if (player != null) id.LocalPlayerId = player;
        return id;
    }
}
