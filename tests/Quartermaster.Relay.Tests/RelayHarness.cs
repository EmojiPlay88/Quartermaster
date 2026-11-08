using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Quartermaster.Net;

namespace Quartermaster.Relay.Tests;

/// <summary>Captures every log line (all levels, all categories), including exception text.</summary>
public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }
    public string All => string.Join("\n", Lines);

    private sealed class Logger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {state} {exception}");
    }
}

/// <summary>An in-process relay on a TestServer with a fake clock.</summary>
public sealed class RelayHarness : IAsyncDisposable
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    public LogCapture Logs { get; } = new();
    public string DataDir { get; private init; } = Path.Combine(Path.GetTempPath(), "qm-relay-tests", Guid.NewGuid().ToString("N"));
    /// <summary>Leave DataDir on disposal (a later harness reuses it, e.g. to test a restart).</summary>
    public bool KeepData { get; set; }
    private WebApplication _app = null!;
    public WebApplication App => _app;
    private TestServer _server = null!;
    private readonly List<RelayTestClient> _clients = [];

    /// <param name="settings">Extra <c>Relay:*</c> settings.</param>
    /// <param name="dataDir">Reuse a data folder (default: a fresh one).</param>
    /// <param name="start">Start time of the fake clock (default 2026-11-07 12:00 UTC).</param>
    public static async Task<RelayHarness> Start(IDictionary<string, string?>? settings = null, string? dataDir = null, DateTimeOffset? start = null)
    {
        var h = dataDir is null ? new RelayHarness() : new RelayHarness { DataDir = dataDir };
        if (start is { } t) h.Time.SetUtcNow(t);
        h._app = RelayApp.Build([], b =>
        {
            b.WebHost.UseTestServer();
            var config = new Dictionary<string, string?> { ["Relay:DataDir"] = h.DataDir, ["Relay:BindAddress"] = "loopback" };
            foreach (var (k, v) in settings ?? new Dictionary<string, string?>()) config[k] = v;
            b.Configuration.AddInMemoryCollection(config);
            b.Services.AddSingleton<TimeProvider>(h.Time);
            b.Logging.AddProvider(h.Logs);
            b.Logging.AddFilter<LogCapture>(null, LogLevel.Trace);
            b.Logging.SetMinimumLevel(LogLevel.Trace);
        });
        await h._app.StartAsync();
        h._server = h._app.GetTestServer();
        return h;
    }

    /// <summary>Connects from the given address; sends Hello(version 1, role) unless role is null.</summary>
    public async Task<RelayTestClient> Connect(string? role = "host", string ip = "203.0.113.10")
    {
        var wsc = _server.CreateWebSocketClient();
        wsc.ConfigureRequest = r => r.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        var ws = await wsc.ConnectAsync(new Uri(_server.BaseAddress, "qm"), CancellationToken.None);
        var c = new RelayTestClient(ws);
        _clients.Add(c);
        if (role is not null)
        {
            await c.Send(new Hello(Protocol.ProtocolVersion, role));
            await c.AssertSilent(); // Hello processed before the test moves the clock
        }
        return c;
    }

    public static string Verifier(string password, string code) => Secrets.AuthVerifier(password, code);

    public static LobbyListing Draft(string name = "Friday game", string sha = "aa11", string host = "Red player") =>
        new("", name, host, sha, "Test Module", ["Red", "Blue"], ["Blue"], 1, 2, true, Protocol.ProtocolVersion);

    /// <summary>Reserves a code and creates a room with the given password; returns the code.</summary>
    public static readonly string HostKey = new string('a', 64);

    public static async Task<string> CreateRoom(RelayTestClient host, string password = "pw", LobbyListing? draft = null, string? hostKey = null)
    {
        await host.Send(new ReserveCode());
        var code = (await host.Receive<CodeReserved>()).Code;
        await host.Send(new CreateRoom(code, Verifier(password, code), hostKey ?? HostKey, draft ?? Draft()));
        Assert.Equal(code, (await host.Receive<RoomCreated>()).Code);
        return code;
    }

    public async Task<IReadOnlyList<LobbyListing>> List(string sha = "aa11")
    {
        var b = await Connect("browser", "198.51.100.200");
        await b.Send(new ListLobbies(sha));
        var result = (await b.Receive<Lobbies>()).Listings;
        await b.DisposeAsync();
        return result;
    }

    /// <summary>Polls (real time) until the condition holds; for effects of disconnects processed asynchronously.</summary>
    public static async Task Eventually(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (await condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail("Condition not met in time.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients) await c.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (!KeepData) try { Directory.Delete(DataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class RelayTestClient(WebSocket ws) : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public WebSocket Socket => ws;

    public Task Send(RelayMessage m) => SendRaw(Framing.Encode(m));

    public async Task SendRaw(byte[] bytes)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
    }

    /// <summary>Next message, or null when the relay closed the connection.</summary>
    public async Task<RelayMessage?> ReceiveOrClose()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buf = new byte[1024 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return Framing.Decode<RelayMessage>(ms.ToArray());
        }
    }

    public async Task<RelayMessage> Receive() =>
        await ReceiveOrClose() ?? throw new InvalidOperationException($"Closed: {ws.CloseStatus} {ws.CloseStatusDescription}");

    public async Task<T> Receive<T>() where T : RelayMessage => Assert.IsType<T>(await Receive());

    /// <summary>Reads (discarding messages) until the relay closes; returns the close status.</summary>
    public async Task<WebSocketCloseStatus?> ReceiveClose()
    {
        while (await ReceiveOrClose() is not null) { }
        return ws.CloseStatus;
    }

    /// <summary>Asserts nothing is queued for this client: a Ping is answered by a Pong with nothing before it.</summary>
    public async Task AssertSilent()
    {
        await Send(new Ping());
        Assert.IsType<Pong>(await Receive());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException) { }
        ws.Dispose();
    }
}
