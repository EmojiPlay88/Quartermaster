using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Quartermaster.Net;
using Quartermaster.Relay;

namespace Quartermaster.Net.Tests;

/// <summary>RelayClient against the real relay (Kestrel + TLS + self-signed certificate, in process).</summary>
public sealed class RealRelayTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-net-real-" + Guid.NewGuid().ToString("N"));
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
        _app = RelayApp.Build([], b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Relay:Port"] = port.ToString(),
            ["Relay:DataDir"] = Path.Combine(_dir, "relay"),
        }));
        await _app.StartAsync();
        _uri = new Uri($"wss://127.0.0.1:{port}/qm");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<RelayClient> NewClient()
    {
        var c = new RelayClient(new PinStore(Path.Combine(_dir, $"pins{_n++}.json")), new[] { TimeSpan.FromMilliseconds(20) });
        await c.ConnectAsync(_uri, _ => true, CancellationToken.None);
        return c;
    }

    private static async Task Until(Func<bool> cond, int ms = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(10); }
    }

    private static LobbyListing Draft() => new("", "Friday", "Red player", "aa11", "Mod",
        new[] { "Red", "Blue" }, new[] { "Blue" }, 1, 2, true, Protocol.ProtocolVersion);

    [Fact]
    public async Task HostDrops_RejoinsWithKey_GuestGetsHostBack_HostGetsMembers_ImpostorRefused()
    {
        const string pw = "pw";
        await using var guest = await NewClient();
        var host = await NewClient();
        var code = await host.ReserveCodeAsync();
        var verifier = Secrets.AuthVerifier(pw, code);
        await host.CreateRoomAsync(code, verifier, Draft());
        var key = host.HostKey;
        Assert.NotNull(key);

        bool hostLeft = false, hostBack = false;
        guest.HostLeft += () => hostLeft = true;
        guest.HostBack += () => hostBack = true;
        var memberId = await guest.JoinRoomAsync(code, verifier);
        Assert.Equal(memberId, guest.MemberId);
        Assert.Single(await guest.ListLobbiesAsync("aa11"));

        await host.DisposeAsync();
        await Until(() => hostLeft);

        // an impostor who knows the code and password but not the host key cannot take the slot
        await using (var impostor = await NewClient())
        {
            var e = await Assert.ThrowsAsync<RelayException>(() => impostor.CreateRoomAsync(code, verifier, Draft()));
            Assert.Equal("Wrong code or password", e.Message);
        }

        await using var host2 = await NewClient();
        IReadOnlyList<string>? members = null;
        byte[]? got = null;
        host2.MembersListed += m => members = m;
        host2.Delivered += (_, b) => got = b;
        await host2.CreateRoomAsync(code, verifier, Draft(), key);
        Assert.Equal(key, host2.HostKey);
        await Until(() => hostBack && members != null);
        Assert.Equal(new[] { memberId }, members);

        await guest.SendAsync(null, new byte[] { 7 });
        await Until(() => got != null);
        Assert.Equal(new byte[] { 7 }, got);
    }

    [Fact]
    public async Task HostKey_GeneratedOncePerRoom_AndDiffersBetweenRooms()
    {
        await using var h1 = await NewClient();
        await using var h2 = await NewClient();
        var c1 = await h1.ReserveCodeAsync();
        var c2 = await h2.ReserveCodeAsync();
        await h1.CreateRoomAsync(c1, Secrets.AuthVerifier("a", c1), Draft());
        await h2.CreateRoomAsync(c2, Secrets.AuthVerifier("a", c2), Draft());
        Assert.NotEqual(h1.HostKey, h2.HostKey);
    }
}
