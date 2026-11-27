using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using NetResult = Quartermaster.Net.IntentResult;

namespace Quartermaster.Render.Tests;

/// <summary>B8: seats and the lobby read former side ids as the pack's sides. Invented sides only.</summary>
public class FormerSidesOnlineTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-fso-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private readonly List<HostSession> _hosts = new();

    public FormerSidesOnlineTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var h in _hosts) h.Dispose();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static RulesPack Pack(string sha, string? renameBlue = null, string? renameGreen = null, string? renameRed = null)
    {
        var p = PhaseFixture.Pack(sha);
        var sides = p.Sides.ToList();
        sides.Add(new SideDef { Id = "green", Name = "Green", UnitFilter = "startsWith(name(), \"Green\")" });
        sides = sides.Select(x =>
            x.Id == "blue" && renameBlue != null ? x with { Id = renameBlue, FormerIds = new List<string> { "blue" } }
            : x.Id == "green" && renameGreen != null ? x with { Id = renameGreen, FormerIds = new List<string> { "green" } }
            : x.Id == "red" && renameRed != null ? x with { Id = renameRed, FormerIds = new List<string> { "red" } }
            : x).ToList();
        return SideFacts.MapReferences(p with { Sides = sides });
    }

    private (ModuleSession S, HostSession H, MemoryRelay R, QueueDispatcher D) Open()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { new PlayerDef("p1", "Alice", new[] { "red" }) });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var relay = new MemoryRelay();
        var d = new QueueDispatcher();
        var listing = new LobbyListing("", "Test lobby", "Alice", s.Archive.Sha256, "Fixture", new[] { "Red", "Blue", "Green" },
            new[] { "Blue", "Green" }, 1, 3, false, Protocol.ProtocolVersion);
        var h = new HostSession(s, new IntentExecutor(s, c), relay.Host, "pw", "abc-234", listing, d.Post);
        _hosts.Add(h);
        d.Pump();
        return (s, h, relay, d);
    }

    private static void Send(QueueDispatcher d, MemoryTransport guest, GamePayload p) { guest.SendPayload(p); d.Pump(); }

    private static string Intent(GameIntent i) => JsonSerializer.Serialize(i, QuartermasterJson.Options);

    [Fact]
    public void Host_rules_change_translates_seats()
    {
        var (s, h, r, d) = Open();
        var bob = r.Guest("m1");
        Send(d, bob, new JoinRequest("Bob", null, s.Archive.Sha256));
        Send(d, bob, new ChooseSides(new[] { "green" }));
        var seat = h.Seats.Guests.Single();
        Assert.Equal(new[] { "green" }, seat.Sides);

        s.UpdateRules(_ => Pack(s.Archive.Sha256, renameGreen: "south"));
        d.Pump();
        Assert.Equal(new[] { "south" }, seat.Sides);
        Assert.DoesNotContain(s.Game!.State.Players!, x => x.Sides.Contains("green"));
        Assert.Contains(s.Game.State.Players!, x => x.Sides.SequenceEqual(new[] { "south" }));
    }

    [Fact]
    public void Host_rules_change_translates_host_own_sides_stamp_and_chat()
    {
        var (s, h, r, d) = Open();
        Assert.Equal(new[] { "red" }, s.PlayerSides!);
        var seatEvents = 0;
        h.SeatsChanged += () => seatEvents++;
        s.UpdateRules(_ => Pack(s.Archive.Sha256, renameRed: "crimson"));
        d.Pump();
        Assert.Equal(new[] { "crimson" }, h.Seats.Host.Sides);
        Assert.Equal(new[] { "crimson" }, s.PlayerSides!);
        Assert.Equal(new[] { "crimson" }, s.ViewSides!);
        Assert.True(seatEvents > 0, "SeatsChanged was not raised for the translated seat");
        Assert.Null(s.Game!.State.Position);   // before the start: no phase acts, the stamp is the player's own side
        Assert.Equal("crimson", s.ActorSide());
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var actions = new LocalActions(s, c, new HistoryController(s, c, (cmd, st) => CommandLog.Prepare(cmd, st, s)));
        actions.Chat("ready");
        Assert.Equal("Alice: ready", s.Game.State.Log.Last(l => l.Kind == LogKind.Chat).Text);
        d.Pump();
        Assert.DoesNotContain(s.Game.State.Log, l => l.SideId == "red");
    }

    [Fact]
    public void Guest_seat_translation_raises_seats_changed()
    {
        var (s, h, r, d) = Open();
        var bob = r.Guest("m1");
        Send(d, bob, new JoinRequest("Bob", null, s.Archive.Sha256));
        Send(d, bob, new ChooseSides(new[] { "green" }));
        var seatEvents = 0;
        h.SeatsChanged += () => seatEvents++;
        s.UpdateRules(_ => Pack(s.Archive.Sha256, renameGreen: "south"));
        d.Pump();
        Assert.Equal(1, seatEvents);
        s.UpdateRules(p => p with { Name = "again" });   // nothing to translate: no seat event
        d.Pump();
        Assert.Equal(1, seatEvents);
    }

    [Fact]
    public void Guest_intent_from_seat_with_former_id_allowed_on_listing_side_unit()
    {
        var (s, h, r, d) = Open();
        var bob = r.Guest("m1");
        Send(d, bob, new JoinRequest("Bob", null, s.Archive.Sha256));
        Send(d, bob, new ChooseSides(new[] { "blue" }));
        s.UpdateRules(_ => Pack(s.Archive.Sha256, renameBlue: "east"));
        d.Pump();
        Assert.Equal(new[] { "east" }, h.Seats.Guests.Single().Sides);
        var id = Guid.NewGuid();
        Send(d, bob, new IntentMsg(id, Intent(new LedgerEditIntent(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 4, "filled"))));
        var res = bob.Of<NetResult>().Last();
        Assert.Equal(id, res.Id);
        Assert.True(res.Ok, res.Message);
    }

    [Fact]
    public void Suggested_host_sides_translated()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache2"), Path.Combine(_tmp, "root2"));
        _sessions.Add(s);
        s.LoadSetup(s.Model.Setups[0], Difficulty.SemiAuto, new[] { new PlayerDef("p1", "A", new[] { "league" }), new PlayerDef("p2", "B", new[] { "red" }) });
        var save = Path.Combine(_tmp, "old.qmsave");
        s.SaveNative(save);
        var pack = RulesPackIo.CreateEmpty("t", "0") with
        {
            Sides = new()
            {
                new SideDef { Id = "north", Name = "North", FormerIds = new List<string> { "league" } },
                new SideDef { Id = "red", Name = "Red" },
            },
        };
        Assert.Equal(new[] { "north" }, OnlineLobbyModel.SuggestedHostSides(save, pack));
    }
}
