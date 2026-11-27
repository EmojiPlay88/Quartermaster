using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using IntentResult = Quartermaster.Render.IntentResult;

namespace Quartermaster.Render.Tests;

/// <summary>B8 #2 online: the host sends and checks with a seat's held set (its sides and their allies); seats stay as they are.</summary>
public class AllianceOnlineTests : AllianceTestBase
{
    private static readonly Dictionary<string, string> None = new();
    private static readonly PlayerDef HostNorth = new("p1", "Alice", new[] { "north" });
    private static readonly PlayerDef HostSouth = new("p1", "Alice", new[] { "south" });

    private (ModuleSession S, PhaseController C, IntentExecutor X) Host(bool sealedToll = false, Difficulty d = Difficulty.FullyAuto)
    {
        var s = Session(sealedToll: sealedToll, d: d);
        var c = Controller(s);
        return (s, c, new IntentExecutor(s, c));
    }

    private (ModuleSession S, HostSession H, MemoryRelay R, QueueDispatcher D) Hosting(PlayerDef host)
    {
        var s = Session(players: new[] { host });
        var c = Controller(s);
        var relay = new MemoryRelay();
        var d = new QueueDispatcher();
        var listing = new LobbyListing("", "Test lobby", host.Name, s.Archive.Sha256, "Fixture",
            new[] { "Northern League", "Southern Compact", "Hill Clans" }, new[] { "north", "south", "hills" }.Except(host.Sides).ToArray(), 1, 3, false,
            Protocol.ProtocolVersion);   // the host's seat is the listing's sides that are not free
        var h = new HostSession(s, new IntentExecutor(s, c), relay.Host, "pw", "abc-234", listing, d.Post);
        d.Pump();
        return (s, h, relay, d);
    }

    private static void Send(QueueDispatcher d, MemoryTransport guest, GamePayload p) { guest.SendPayload(p); d.Pump(); }

    private static GameState SnapshotState(Snapshot snap) =>
        JsonSerializer.Deserialize<GameState>(Gzip.DecompressBounded(snap.StateJsonGz, int.MaxValue), QuartermasterJson.Options)!;

    private static IntentResult Water(IntentExecutor x, IReadOnlySet<string> sides, string pieceId) =>
        x.Execute("p2", sides, new FormulaIntent("f-water", pieceId, null, None, true));

    [Fact]
    public void Guest_snapshot_includes_ally_data_and_view_sides()
    {
        var (s, h, r, d) = Hosting(HostSouth);
        var bob = r.Guest("m1");
        Send(d, bob, new JoinRequest("Bob", null, s.Archive.Sha256));
        Send(d, bob, new ChooseSides(new[] { "north" }));
        try
        {
            Assert.Equal(new[] { "north" }, h.Seats.Guests.Single().Sides);   // the seat is unchanged
            var view = SnapshotState(bob.Of<Snapshot>().Last());
            Assert.Equal(new[] { "hills", "north" }, view.ViewSides!);
            Assert.Equal(CampSupply, Supply(view, "hillcamp"));
            Assert.Null(Supply(view, "southdepot"));
        }
        finally { h.Dispose(); }
    }

    [Fact]
    public void Guest_sides_include_allies()
    {
        var host = Session();
        var guest = GuestSession();
        guest.LoadGuestRules(Pack(host.Archive.Sha256, alliance: false));
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, new HashSet<string> { "north", "hills" }), North);
        Assert.Equal(new[] { "north" }, guest.GuestSides!);
        guest.LoadGuestRules(Pack(host.Archive.Sha256));   // the host's pack now has the alliance
        Assert.Equal(new[] { "hills", "north" }, guest.GuestSides!.OrderBy(x => x, StringComparer.Ordinal));
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, new HashSet<string> { "north", "hills" }), North);
        Assert.Equal(new[] { "hills", "north" }, guest.GuestSides!.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Null(guest.PlayerSides);   // the guest's raw sides stay private to the session
        Assert.Null(guest.ViewSides);   // already filtered
        Assert.Empty(FormulaMasking.HiddenFor(guest, guest.Rules!.ById<FormulaDef>("f-camp")!, null));   // the ally's camp is not masked
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, South), South);
        Assert.Equal(new[] { "south" }, guest.GuestSides!);
        Assert.NotEmpty(FormulaMasking.HiddenFor(guest, guest.Rules!.ById<FormulaDef>("f-camp")!, null));
    }

    [Fact]
    public void Guest_intent_on_ally_unit_applies()
    {
        var (s, _, x) = Host();
        var r = Water(x, North, PhaseFixture.RedTwo);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(9, Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.RedTwo), "water"));
    }

    [Fact]
    public void Guest_intent_on_non_ally_unit_refused_old_text()
    {
        var (s, _, x) = Host();
        var r = Water(x, North, PhaseFixture.BlueOne);
        Assert.False(r.Ok);
        Assert.Equal(OtherSideUnit, r.Message);
        var back = Water(x, South, PhaseFixture.RedTwo);
        Assert.False(back.Ok);
        Assert.Equal(OtherSideUnit, back.Message);
        Assert.NotEqual<double?>(9, Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        Assert.NotEqual<double?>(9, Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.RedTwo), "water"));
    }

    [Fact]
    public void Guest_next_phase_in_ally_phase_allowed()
    {
        var (s, c, x) = Host(d: Difficulty.SemiAuto);
        To(s, c, "ph-combat");   // the Hill Clans' phase
        var refused = x.Execute("p2", South, new NextPhaseIntent());
        Assert.False(refused.Ok);
        Assert.Equal("ph-combat", s.Game!.State.Position?.PhaseId);
        var r = x.Execute("p1", North, new NextPhaseIntent());
        Assert.True(r.Ok, r.Message);
        Assert.Equal("ph-end", s.Game.State.Position?.PhaseId);
    }

    [Fact]
    public void Guest_edit_ally_holder_allowed_non_ally_refused()
    {
        var (s, _, x) = Host();
        var ok = x.Execute("p1", North, new LedgerEditIntent(LedgerKey.Holder("hillcamp"), "p-supply", 5, "count"));
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal(5, Supply(s.Game!.State, "hillcamp"));
        var no = x.Execute("p1", North, new LedgerEditIntent(LedgerKey.Holder("southdepot"), "p-supply", 5, "count"));
        Assert.False(no.Ok);
        Assert.Equal(SouthSupply, Supply(s.Game.State, "southdepot"));
    }

    [Fact]
    public void Guest_preview_of_ally_hidden_input_unmasked()
    {
        var (s, c, x) = Host();
        c.Start();   // Weather: no side acts
        var ally = x.Execute("p1", North, new FormulaIntent("f-camp", null, null, None, true, Preview: true));
        Assert.True(ally.Verdict != null, ally.Message);
        Assert.Equal("47474", ally.Verdict!.Values!["S"]);
        var other = x.Execute("p2", South, new FormulaIntent("f-camp", null, null, None, true, Preview: true));
        Assert.False((other.Verdict?.Values ?? new Dictionary<string, string>()).ContainsKey("S"));
        Assert.Null(Gate(s.Game!.State));   // a preview applies nothing
    }

    [Fact]
    public void Sealed_ally_may_answer_ally_run_once()
    {
        var (s, c, x) = Host(sealedToll: true);
        c.Start();
        var runs = s.Game!.State.Pending.Where(r => r.FormulaId == "f-toll").ToList();
        Assert.Equal(new[] { "north", "south", "hills" }, runs.Select(r => r.AskSideId));   // one run per side, as before
        var clans = runs.Single(r => r.AskSideId == "hills").Id;
        FormulaIntent Bid(string runId, int bid) => new("f-toll", null, runId, new Dictionary<string, string> { ["bid"] = bid.ToString() }, false);

        var first = x.Execute("p1", North, Bid(clans, 3));   // the League player answers the Clans' run
        Assert.True(first.Ok, first.Message);
        var again = x.Execute("p1", North, Bid(clans, 8));
        Assert.False(again.Ok);
        Assert.Equal(AlreadyIn, again.Message);
        Assert.True(x.Execute("p1", North, Bid(runs.Single(r => r.AskSideId == "north").Id, 6)).Ok);
        Assert.Equal("3", s.Game.State.Pending.Single(r => r.Id == clans).SealedAnswer!["bid"]);

        s.SetViewSides(Hills);   // an ally sees both answers before release
        Assert.Equal(2, s.View!.Pending.Count(r => r.SealedAnswer != null));
        s.SetViewSides(South);   // the other side sees neither
        Assert.DoesNotContain(s.View!.Pending, r => r.SealedAnswer != null);
        var south = PlayerView.For(s.Game.State, s.Rules, s.Model, South);
        Assert.DoesNotContain(south.Pending, r => r.SealedAnswer != null);

        Assert.True(x.Execute("p2", South, Bid(runs.Single(r => r.AskSideId == "south").Id, 4)).Ok);   // the release
        var bids = s.Game.State.Keyed![LedgerKey.Holder(HolderDef.GlobalId)]["p-bid"];
        Assert.Equal(3, bids["Hill Clans"]);   // askside() was the run's side
        Assert.Equal(6, bids["Northern League"]);
        Assert.Equal(4, bids["Southern Compact"]);
    }

    [Fact]
    public void Host_view_unchanged_by_resync()
    {
        var (s, h, r, d) = Hosting(HostNorth);
        try
        {
            Assert.Equal(new[] { "north" }, s.PlayerSides!);
            Assert.Equal(new[] { "hills", "north" }, s.ViewSides!.OrderBy(x => x, StringComparer.Ordinal));
            var before = s.ViewSides;
            var bob = r.Guest("m1");
            Send(d, bob, new JoinRequest("Bob", null, s.Archive.Sha256));
            Send(d, bob, new ChooseSides(new[] { "south" }));   // a seat change: SyncPlayers runs again
            Assert.Same(before, s.ViewSides);
            Assert.Equal(new[] { "north" }, s.PlayerSides!);
        }
        finally { h.Dispose(); }
    }
}
