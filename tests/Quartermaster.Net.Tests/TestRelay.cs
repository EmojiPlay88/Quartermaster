using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartermaster.Net;

namespace Quartermaster.Net.Tests;

/// <summary>Minimal in-test relay: TLS + /qm websocket speaking the Task 1 messages.</summary>
public sealed class TestRelay : IAsyncDisposable
{
    public static X509Certificate2 MakeCert()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);
    }

    public static string Fingerprint(X509Certificate2 c) => Convert.ToHexString(SHA256.HashData(c.RawData));

    private readonly X509Certificate2 _cert;
    private WebApplication? _app;
    private readonly ConcurrentDictionary<WebSocket, string> _sockets = new();
    public int Port { get; }
    public ConcurrentQueue<RelayMessage> Received { get; } = new();
    public List<LobbyListing> Listings { get; } = new();
    public string Password = "ok-verifier";

    public Uri Uri => new($"wss://127.0.0.1:{Port}/qm");

    public TestRelay(X509Certificate2 cert, int port = 0)
    {
        _cert = cert;
        if (port == 0)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
        }
        Port = port;
    }

    public async Task StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, Port, l =>
        {
            l.Protocols = HttpProtocols.Http1;
            l.UseHttps(_cert);
        }));
        b.WebHost.ConfigureKestrel(o => { });
        b.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(200));
        var app = b.Build();
        app.UseWebSockets();
        app.Map("/qm", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            _sockets[ws] = "";
            try { await Serve(ws); }
            catch { }
            finally { _sockets.TryRemove(ws, out _); }
        });
        await app.StartAsync();
        _app = app;
    }

    private async Task Serve(WebSocket ws)
    {
        var buf = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            var msg = Framing.Decode<RelayMessage>(ms.ToArray());
            Received.Enqueue(msg);
            RelayMessage? reply = msg switch
            {
                ReserveCode => new CodeReserved("ABC234"),
                CreateRoom c => new RoomCreated(c.Code),
                ListLobbies q => new Lobbies(Listings.Where(x => x.ModuleSha256 == q.ModuleSha256).ToList()),
                JoinRoom j => j.AuthVerifier == Password ? new Joined("m1") : new Refused("Wrong code or password"),
                Ping => new Pong(),
                _ => null,
            };
            if (reply != null) await Push(ws, reply);
        }
    }

    private static Task Push(WebSocket ws, RelayMessage m) =>
        ws.SendAsync(Framing.Encode(m), WebSocketMessageType.Text, true, CancellationToken.None);

    public async Task PushAllAsync(RelayMessage m)
    {
        foreach (var ws in _sockets.Keys.ToArray()) await Push(ws, m);
    }

    public int ConnectionCount => _sockets.Count;

    public async Task StopAsync()
    {
        if (_app == null) return;
        foreach (var ws in _sockets.Keys) ws.Abort();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
