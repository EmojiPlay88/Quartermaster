using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Relay;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Host and guest sessions over the real relay (in process, TLS) with real <see cref="RelayClient"/>s and <see cref="RelayTransport"/>s.
/// Every websocket frame the relay reads or writes is captured on the relay side. Invented Red/Blue sides only.
/// </summary>
public sealed class RelayEndToEndTests : IAsyncLifetime
{
    private const string Password = "blue-and-red";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly FrameCapture _capture = new();
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private readonly List<IDisposable> _disposables = new();
    private readonly List<RelayClient> _clients = new();
    private WebApplication _app = null!;
    private Uri _uri = null!;
    private int _n;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _app = RelayApp.Build([], b =>
        {
            b.Logging.ClearProviders();
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:Port"] = port.ToString(),
                ["Relay:DataDir"] = Path.Combine(_dir, "relay"),
            });
            b.Services.AddSingleton<IStartupFilter>(new CaptureFilter(_capture));
        });
        await _app.StartAsync();
        _uri = new Uri($"wss://127.0.0.1:{port}/qm");
    }

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var c in _clients) await c.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<RelayClient> NewClient()
    {
        var c = new RelayClient(new PinStore(Path.Combine(_dir, $"pins{_n++}.json")), new[] { TimeSpan.FromMilliseconds(20) });
        _clients.Add(c);
        await c.ConnectAsync(_uri, _ => true, CancellationToken.None);
        return c;
    }

    private static void Until(QueueDispatcher d, Func<bool> cond, int ms = 60000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            d.Pump();
            if (cond()) return;
            if (DateTime.UtcNow > end) throw new TimeoutException();
            Thread.Sleep(10);
        }
    }

    private ModuleSession Session(string path, string name)
    {
        var s = ModuleSession.Open(path, Path.Combine(_dir, "cache-" + name), Path.Combine(_dir, "root-" + name));
        _sessions.Add(s);
        return s;
    }

    private sealed record Game(ModuleSession Host, HostSession H, QueueDispatcher D, string Code, string Verifier, byte[] Key, string ModulePath);

    private async Task<Game> HostGame()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var path = fx.Build();
        var s = Session(path, "host");
        s.SetRoller((n, sides) => Enumerable.Repeat(sides == 10000 ? GuestSessionTests.SecretRoll : 4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { new PlayerDef("p1", "Alice", new[] { "red" }) });
        s.NewRules("t");
        s.UpdateRules(_ => GuestSessionTests.Pack(s.Archive.Sha256));
        GuestSessionTests.AddRedSecrets(s);

        var host = await NewClient();
        var code = await host.ReserveCodeAsync();
        var verifier = Secrets.AuthVerifier(Password, code);
        var listing = new LobbyListing("", "Friday", "Alice", s.Archive.Sha256, "Fixture", new[] { "Red", "Blue" }, new[] { "Blue" },
            1, 2, true, Protocol.ProtocolVersion);
        await host.CreateRoomAsync(code, verifier, listing);
        var key = Secrets.GameKey(Password, code);
        var t = new RelayTransport(host, key);
        _disposables.Add(t);
        var d = new QueueDispatcher();
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var h = new HostSession(s, new IntentExecutor(s, c), t, Password, code, listing, d.Post);
        _disposables.Add(h);
        d.Pump();
        return new Game(s, h, d, code, verifier, key, path);
    }

    [Fact]
    public async Task Relay_SeesNoPlaintext()
    {
        var game = await HostGame();
        var guestClient = await NewClient();
        await guestClient.JoinRoomAsync(game.Code, game.Verifier);
        var gt = new RelayTransport(guestClient, game.Key);
        _disposables.Add(gt);
        var bobSession = Session(game.ModulePath, "bob");
        var bob = new GuestSession(bobSession, gt, game.Code, "Bob", game.D.Post, new RejoinTokens(Path.Combine(_dir, "tokens")));
        _disposables.Add(bob);
        var actions = new RemoteActions(bob);

        Until(game.D, () => bob.State == GuestState.ChoosingSide);
        Assert.True(bob.ChooseSides(new[] { "blue" }));
        Until(game.D, () => bob.State == GuestState.Playing && bobSession.Game != null);
        actions.Chat("Attack at dawn");
        actions.EditLedger(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 9, "topped up");
        Until(game.D, () => Ledger.Get(bobSession.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water") == 9
            && bobSession.Game!.State.Log.Any(x => x.Kind == LogKind.Chat && x.Text.Contains("Attack at dawn")));
        Assert.Contains(bobSession.Game!.State.Pieces.Values, p => PieceInfo.Name(p) == "Blue 1");

        // What the relay read and wrote: frames exist, game payloads among them, and none of it is readable game data. The capture
        // runs on the relay's threads: wait until it has the frames before checking them.
        int Opened() => _capture.Frames.ToList().Count(f =>
        {
            try
            {
                return Framing.Decode<RelayMessage>(f) switch { Send s => s.Sealed, Deliver dl => dl.Sealed, _ => null } is { } b
                    && RelayTransport.TryOpen(game.Key, b) != null;
            }
            catch (ProtocolException) { return false; }
        });
        Until(game.D, () => _capture.Frames.Count > 10 && Opened() >= 4);
        var frames = _capture.Frames.ToList();
        Assert.True(frames.Count > 10, $"only {frames.Count} frames captured");
        var markers = GuestSessionTests.SecretMarkers.Concat(new[] { "Blue 1", "Red 1", "Red 2", "Attack at dawn", "topped up", "Red Depot", "Mixed supply" }).ToList();
        var opened = 0;
        foreach (var f in frames)
        {
            var text = Encoding.UTF8.GetString(f);
            foreach (var m in markers) Assert.DoesNotContain(m, text);
            RelayMessage msg;
            try { msg = Framing.Decode<RelayMessage>(f); }
            catch (ProtocolException) { continue; }
            var sealed_ = msg switch { Send s => s.Sealed, Deliver dl => dl.Sealed, _ => null };
            if (sealed_ == null) continue;
            var raw = Encoding.UTF8.GetString(sealed_);
            foreach (var m in markers) Assert.DoesNotContain(m, raw);
            // Only the players' key opens it (proof these frames carried the game).
            if (RelayTransport.TryOpen(game.Key, sealed_) != null) opened++;
        }
        Assert.True(opened >= 4, $"only {opened} game payloads seen");
        Assert.Equal(0, gt.Dropped);
    }

    [Fact]
    public async Task WrongPassword_Refused_ByRelay()
    {
        var game = await HostGame();
        var eve = await NewClient();
        var e = await Assert.ThrowsAsync<RelayException>(() => eve.JoinRoomAsync(game.Code, Secrets.AuthVerifier("guess", game.Code)));
        Assert.Equal("Wrong code or password", e.Message);
        Assert.Single(game.H.Seats.All);
    }

    // ---- relay-side frame capture ----

    internal sealed class FrameCapture
    {
        public ConcurrentQueue<byte[]> Frames { get; } = new();
    }

    /// <summary>
    /// Installs a websocket handshake ahead of the relay's own and wraps every accepted socket, so the frames the relay reads and writes
    /// are recorded exactly as the relay sees them (after TLS).
    /// </summary>
    private sealed class CaptureFilter(FrameCapture capture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseWebSockets();
            app.Use(async (ctx, nxt) =>
            {
                if (ctx.Features.Get<IHttpWebSocketFeature>() is { IsWebSocketRequest: true } f)
                    ctx.Features.Set<IHttpWebSocketFeature>(new CapturingFeature(f, capture));
                await nxt(ctx);
            });
            next(app);
        };
    }

    private sealed class CapturingFeature(IHttpWebSocketFeature inner, FrameCapture capture) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => inner.IsWebSocketRequest;

        public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) =>
            new CapturingSocket(await inner.AcceptAsync(context), capture);
    }

    private sealed class CapturingSocket(WebSocket inner, FrameCapture capture) : WebSocket
    {
        private readonly MemoryStream _in = new();

        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override WebSocketState State => inner.State;
        public override string? SubProtocol => inner.SubProtocol;
        public override void Abort() => inner.Abort();
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => inner.CloseAsync(s, d, ct);
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => inner.CloseOutputAsync(s, d, ct);
        public override void Dispose() => inner.Dispose();

        private void Took(ReadOnlySpan<byte> data, bool end)
        {
            lock (_in)
            {
                _in.Write(data);
                if (!end) return;
                capture.Frames.Enqueue(_in.ToArray());
                _in.SetLength(0);
            }
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var r = await inner.ReceiveAsync(buffer, ct);
            if (r.MessageType != WebSocketMessageType.Close) Took(buffer.AsSpan(0, r.Count), r.EndOfMessage);
            return r;
        }

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var r = await inner.ReceiveAsync(buffer, ct);
            if (r.MessageType != WebSocketMessageType.Close) Took(buffer.Span[..r.Count], r.EndOfMessage);
            return r;
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            capture.Frames.Enqueue(buffer.ToArray());
            return inner.SendAsync(buffer, type, end, ct);
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            capture.Frames.Enqueue(buffer.ToArray());
            return inner.SendAsync(buffer, type, end, ct);
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType type, WebSocketMessageFlags flags, CancellationToken ct)
        {
            capture.Frames.Enqueue(buffer.ToArray());
            return inner.SendAsync(buffer, type, flags, ct);
        }
    }
}
