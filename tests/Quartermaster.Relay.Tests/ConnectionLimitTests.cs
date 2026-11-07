using System.Net.WebSockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartermaster.Net;
using static Quartermaster.Relay.Tests.RelayHarness;

namespace Quartermaster.Relay.Tests;

public class ConnectionLimitTests
{
    [Fact]
    public async Task Hello_Timeout_Closes()
    {
        await using var relay = await Start();
        var silent = await relay.Connect(role: null);
        var greeted = await relay.Connect("browser");
        relay.Time.Advance(TimeSpan.FromSeconds(9));
        await greeted.AssertSilent();
        relay.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await silent.ReceiveClose());
        await greeted.AssertSilent();
    }

    [Fact]
    public async Task PreHello_FrameCap()
    {
        await using var relay = await Start();
        var c = await relay.Connect(role: null);
        await c.SendRaw(new byte[4 * 1024 + 1]);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await c.ReceiveClose());

        // After Hello, larger frames are fine.
        var d = await relay.Connect("host");
        await d.SendRaw(new byte[64 * 1024]); // garbage, ignored
        await d.AssertSilent();
    }

    [Fact]
    public async Task PerIp_ConnectionCap()
    {
        await using var relay = await Start();
        var mine = new List<RelayTestClient>();
        for (int i = 0; i < Limits.ConnectionsPerIp; i++) mine.Add(await relay.Connect("browser", "203.0.113.77"));
        await Assert.ThrowsAnyAsync<Exception>(() => relay.Connect("browser", "203.0.113.77"));
        await (await relay.Connect("browser", "203.0.113.78")).AssertSilent();

        // A freed slot can be used again.
        await mine[0].DisposeAsync();
        await Eventually(async () =>
        {
            try { await relay.Connect("browser", "203.0.113.77"); return true; }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException) { return false; }
        });
    }

    [Fact]
    public async Task Ipv6_LimitsBucketBy64()
    {
        await using var relay = await Start();
        for (int i = 1; i <= Limits.ConnectionsPerIp; i++) await relay.Connect("browser", $"2001:db8::{i:x}");
        await Assert.ThrowsAnyAsync<Exception>(() => relay.Connect("browser", "2001:db8::ffff:1"));
        await (await relay.Connect("browser", "2001:db8:0:1::1")).AssertSilent();
        Assert.Equal("2001:db8::/64", Connection.Bucket(System.Net.IPAddress.Parse("2001:db8::abcd")));
        Assert.Equal("203.0.113.5", Connection.Bucket(System.Net.IPAddress.Parse("203.0.113.5")));
        Assert.Equal("203.0.113.5", Connection.Bucket(System.Net.IPAddress.Parse("::ffff:203.0.113.5")));
    }

    [Fact]
    public void SlowReceiver_Aborted_QueueDropped()
    {
        var c = new Connection("x", TimeProvider.System);
        for (int i = 0; i < 8 && !c.Closing; i++) c.Post(new Deliver("g1", new byte[5 * 1024 * 1024]));
        Assert.True(c.Closing);
        Assert.True(c.Aborted.IsCancellationRequested);
        Assert.False(c.Outgoing.TryRead(out _));
    }

    [Fact]
    public async Task Kestrel_MaxConcurrentConnections_Configured()
    {
        await using var relay = await Start();
        var k = relay.App.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.Equal(2000, k.Limits.MaxConcurrentConnections);
        Assert.Equal(2000, new RelayOptions().MaxConnections);
    }
}
