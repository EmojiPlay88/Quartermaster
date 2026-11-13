using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartermaster.Net;
using Quartermaster.Relay;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// A real relay (TLS, self-signed certificate) running in this test process on a free loopback port, with its data in a temp folder
/// (as Piece6bEndToEndTests). Never the real relay. Clients made through <see cref="ClientAsync"/> are disposed with it.
/// </summary>
public sealed class InProcessRelay : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly string _dir;
    readonly List<RelayClient> _clients = new();
    bool _disposed;

    InProcessRelay(WebApplication app, string dir, Uri uri)
    {
        _app = app;
        _dir = dir;
        Uri = uri;
    }

    /// <summary>wss://127.0.0.1:{port}/qm</summary>
    public Uri Uri { get; }

    /// <summary>The address as typed into the relay field ("127.0.0.1:port").</summary>
    public string Address => $"{Uri.Host}:{Uri.Port}";

    public static async Task<InProcessRelay> StartAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var app = RelayApp.Build([], b =>
        {
            b.Logging.ClearProviders();
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Relay:BindAddress"] = "loopback",
                ["Relay:DataDir"] = Path.Combine(dir, "relay"),
            });
        });
        await app.StartAsync();
        return new InProcessRelay(app, dir, new Uri($"wss://127.0.0.1:{port}/qm"));
    }

    /// <summary>A connected client that trusts the relay's certificate and pins it in <paramref name="pins"/> (so a later client using the
    /// same store is not asked).</summary>
    public async Task<RelayClient> ClientAsync(PinStore pins)
    {
        var c = new RelayClient(pins, new[] { TimeSpan.FromMilliseconds(20) });
        lock (_clients) _clients.Add(c);
        await Task.Run(() => c.ConnectAsync(Uri, _ => true, CancellationToken.None));
        return c;
    }

    /// <summary>Stops the relay (a test may stop it while windows are still connected); a second call does nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        RelayClient[] clients;
        lock (_clients) clients = _clients.ToArray();
        foreach (var c in clients) await c.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
