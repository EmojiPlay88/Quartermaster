using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Relay;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Piece 6c end to end: Red (Rita) and Blue (Bo) trade turns through a real in-process relay mailbox; separate identity folders model two PCs.</summary>
[Collection(AppDataRootEnv.Name)]
public sealed class Piece6cEndToEndTests : IAsyncLifetime
{
    private const string Password = "table-secret";
    private static readonly PlayerDef Rita = new("p1", "Rita", new[] { "red" });
    private static readonly PlayerDef Bo = new("p2", "Bo", new[] { "blue" });

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-6c-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private readonly List<MailboxService> _services = new();
    private WebApplication? _app;
    private int _port;
    private int _n;

    private sealed class FakeProtector : IPasswordProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).Prepend((byte)0xEE).ToArray();
        public byte[] Unprotect(byte[] p) => p[0] == 0xEE ? p.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray() : throw new InvalidDataException();
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tmp);
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _app = RelayApp.Build([], b =>
        {
            b.Logging.ClearProviders();
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:Port"] = _port.ToString(),
                ["Relay:BindAddress"] = "loopback",
                ["Relay:DataDir"] = Path.Combine(_tmp, "relay"),
            });
        });
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _services) s.Stop();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ModuleSession Open()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        return s;
    }

    private TurnIdentity Identity(string pc, string gameId, string player)
    {
        var id = TurnIdentity.Load(gameId, Path.Combine(_tmp, "pc-" + pc), new FakeProtector());
        id.LocalPlayerId = player;
        id.Relay = $"wss://127.0.0.1:{_port}/qm";
        id.UseMailbox = true;
        return id;
    }

    private MailboxService Service(TimeProvider time)
    {
        var s = new MailboxService(_ => new RelayClient(new PinStore(Path.Combine(_tmp, $"pins{Interlocked.Increment(ref _n)}.json")),
            new[] { TimeSpan.FromMilliseconds(20) }), a => a(), time, _ => true);
        _services.Add(s);
        return s;
    }

    private static string AnyPiece(GameSession g) => g.State.Pieces.Keys.OrderBy(k => k, StringComparer.Ordinal).First();

    private static void MoveBy(GameSession g, double dx)
    {
        var id = AnyPiece(g);
        var p = g.State.Pieces[id];
        using (g.BeginAction()) g.Execute(new MovePieces(new[] { id }, p.MapId, new PointD(p.Pos.X + dx, p.Pos.Y), null));
    }

    private static async Task Until(Func<bool> cond, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }

    /// <summary>The player watches the mailbox once and gets the single waiting turn.</summary>
    private async Task<(TurnFile T, string Item, MailboxService S)> Receive(TurnIdentity id)
    {
        var got = new System.Collections.Concurrent.ConcurrentQueue<(TurnFile, string)>();
        var svc = Service(new FakeTimeProvider());
        svc.TurnWaiting += (t, item) => got.Enqueue((t, item));
        svc.Watch(id.GameId, id, Password);
        await Until(() => !got.IsEmpty);
        await svc.CurrentPoll;
        var (turn, itemId) = Assert.Single(got);
        return (turn, itemId, svc);
    }

    /// <summary>Everything the relay holds in its mailbox folder right now (items are deleted once played).</summary>
    private void SnapshotRelayFiles()
    {
        var dir = Path.Combine(_tmp, "relay", "mailbox");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            _stored.Add(Encoding.Latin1.GetString(File.ReadAllBytes(f)));
    }

    private readonly List<string> _stored = new();

    private async Task<TurnFile> SendTurn(ModuleSession s, TurnIdentity id, string to)
    {
        var t = TurnSender.Build(s, to, id);
        Assert.True(await Service(new FakeTimeProvider()).SendAsync(id, t, Password));
        TurnSender.AfterSend(s);
        SnapshotRelayFiles();
        return t;
    }

    private async Task PlayReceived(ModuleSession s, string player, TurnIdentity id)
    {
        var (t, item, svc) = await Receive(id);
        Assert.Equal(TurnCheck.Ok, TurnOpener.Check(s, t, id));
        var model = s.OpenTurn(t, player, id);
        model.PlayAll();
        Assert.True(model.Done);
        Assert.True(model.EndMatches);
        Assert.Null(model.Error);
        Assert.Equal(t.EndHash, StateHash.Of(s.Game!.State));
        await svc.DeleteAsync(item);
        svc.Stop();
    }

    [Fact]
    public async Task ThreeRounds_ThroughMailbox_StatesAgree_AndRelayHoldsOnlyCiphertext()
    {
        // Red's PC starts the game and plays round 1.
        var red = Open();
        red.LoadSetup(red.Model.Setups[0], Difficulty.FullyAuto, new[] { Rita, Bo });
        var gameId = red.Game!.State.GameId!;
        var redId = Identity("red", gameId, Rita.Id);
        MoveBy(red.Game!, 10);
        using (red.Game!.BeginAction()) red.Game!.Execute(new RollDice("Red attack", 2, 6, new[] { 3, 5 }));
        var t1 = await SendTurn(red, redId, Bo.Id);

        // Blue's PC (no game yet) gets it from the mailbox: NewGame, replays to the same end hash.
        var blue = Open();
        var blueId = Identity("blue", gameId, Bo.Id);
        {
            var (t, item, svc) = await Receive(blueId);
            Assert.Equal("Rita", t.FromName);
            Assert.Equal(TurnCheck.NewGame, TurnOpener.Check(blue, t, blueId));
            var model = blue.OpenTurn(t, Bo.Id, blueId);
            model.PlayAll();
            Assert.True(model.EndMatches);
            await svc.DeleteAsync(item);
            svc.Stop();
        }
        Assert.Equal(StateHash.Of(red.Game!.State), StateHash.Of(blue.Game!.State));

        // Round 2: Blue plays and sends back; Red replays.
        MoveBy(blue.Game!, -4);
        await SendTurn(blue, blueId, Rita.Id);
        await PlayReceived(red, Rita.Id, redId);
        Assert.Equal(StateHash.Of(red.Game!.State), StateHash.Of(blue.Game!.State));

        // Round 3: Red again; Blue replays.
        MoveBy(red.Game!, 7);
        await SendTurn(red, redId, Bo.Id);
        await PlayReceived(blue, Bo.Id, blueId);
        Assert.Equal(StateHash.Of(red.Game!.State), StateHash.Of(blue.Game!.State));

        // Resending an already played turn is recognised.
        Assert.Equal(TurnCheck.AlreadyPlayed, TurnOpener.Check(blue, t1, blueId));

        // Blue moves on, then a turn that starts from the older position arrives: warning, nothing changed.
        MoveBy(blue.Game!, 1);
        MoveBy(red.Game!, 2);
        await SendTurn(red, redId, Bo.Id);
        var (older, item4, svc4) = await Receive(blueId);
        var hash = StateHash.Of(blue.Game!.State);
        Assert.Equal(TurnCheck.OlderPosition, TurnOpener.Check(blue, older, blueId));
        Assert.Equal(hash, StateHash.Of(blue.Game!.State));
        await svc4.DeleteAsync(item4);
        svc4.Stop();

        // The relay's stored files hold only ciphertext: no unit names, ids or the player names.
        Assert.True(_stored.Count >= 4);
        var markers = red.Game!.State.Pieces.Keys.Concat(red.Game!.State.Pieces.Values.Select(p => p.TypeString))
            .Concat(new[] { "Rita", "Red attack", gameId, "table-secret" }).Where(m => m.Length >= 3).Distinct().ToList();
        foreach (var text in _stored)
        {
            foreach (var m in markers) Assert.DoesNotContain(m, text, StringComparison.Ordinal);
        }
    }
}
