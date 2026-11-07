using Quartermaster.Net;

namespace Quartermaster.Net.Tests;

public class RelayClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-net-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan[] Fast = { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40) };
    private static readonly LobbyListing L = new("ABC234", "Game", "Host", "sha", "Title",
        new[] { "Red", "Blue" }, new[] { "Blue" }, 1, 2, false, 1);

    public RelayClientTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private PinStore Pins() => new(Path.Combine(_dir, "pins.json"));

    private static async Task<T> Within<T>(Task<T> t, int ms = 10000)
    {
        if (await Task.WhenAny(t, Task.Delay(ms)) != t) throw new TimeoutException();
        return await t;
    }

    private static async Task Until(Func<bool> cond, int ms = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(10); }
    }

    [Fact]
    public void PinStore_RoundTrip_AndCorruptFile()
    {
        var path = Path.Combine(_dir, "p.json");
        var s = new PinStore(path);
        Assert.Null(s.Get("h:1"));
        s.Set("h:1", "AA");
        Assert.Equal("AA", new PinStore(path).Get("h:1"));
        File.WriteAllText(path, "{{garbage");
        Assert.Null(new PinStore(path).Get("h:1"));
    }

    [Fact]
    public async Task FirstConnect_PinsFingerprint()
    {
        var cert = TestRelay.MakeCert();
        await using var relay = new TestRelay(cert);
        await relay.StartAsync();
        var pins = Pins();
        CertificateCheck? seen = null;
        await using var c = new RelayClient(pins, Fast);
        await c.ConnectAsync(relay.Uri, chk => { seen = chk; return true; }, CancellationToken.None);
        Assert.NotNull(seen);
        Assert.Null(seen!.PinnedFingerprint);
        Assert.Equal(TestRelay.Fingerprint(cert), seen.Fingerprint);
        Assert.Equal($"127.0.0.1:{relay.Port}", seen.HostPort);
        Assert.Equal(TestRelay.Fingerprint(cert), pins.Get($"127.0.0.1:{relay.Port}"));
        Assert.Equal("ABC234", await Within(c.ReserveCodeAsync()));

        // second connection with the pin: no prompt
        await using var c2 = new RelayClient(Pins(), Fast);
        await c2.ConnectAsync(relay.Uri, _ => throw new Exception("must not ask"), CancellationToken.None);
        Assert.Equal("ABC234", await Within(c2.ReserveCodeAsync()));
    }

    [Fact]
    public async Task UnknownCert_Declined_NotPinned_NoThrow()
    {
        await using var relay = new TestRelay(TestRelay.MakeCert());
        await relay.StartAsync();
        var pins = Pins();
        await using var c = new RelayClient(pins, new[] { TimeSpan.FromHours(1) });
        string? reason = null;
        c.Disconnected += r => reason = r;
        await c.ConnectAsync(relay.Uri, _ => false, CancellationToken.None);
        await Until(() => reason != null);
        Assert.Null(pins.Get($"127.0.0.1:{relay.Port}"));
        await Assert.ThrowsAsync<RelayException>(() => c.ReserveCodeAsync());
    }

    [Fact]
    public async Task ChangedFingerprint_AsksAndRefusesByDefault()
    {
        var cert1 = TestRelay.MakeCert();
        var relay = new TestRelay(cert1);
        await relay.StartAsync();
        var port = relay.Port;
        var pins = Pins();
        await using (var c = new RelayClient(pins, Fast))
            await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await relay.StopAsync();

        var cert2 = TestRelay.MakeCert();
        await using var relay2 = new TestRelay(cert2, port);
        await relay2.StartAsync();
        CertificateCheck? seen = null;
        string? reason = null;
        await using var c3 = new RelayClient(Pins(), new[] { TimeSpan.FromHours(1) });
        c3.Disconnected += r => reason = r;
        await c3.ConnectAsync(relay2.Uri, chk => { seen = chk; return false; }, CancellationToken.None);
        Assert.NotNull(seen);
        Assert.Equal(TestRelay.Fingerprint(cert1), seen!.PinnedFingerprint);
        Assert.Equal(TestRelay.Fingerprint(cert2), seen.Fingerprint);
        Assert.Equal(TestRelay.Fingerprint(cert1), Pins().Get($"127.0.0.1:{port}"));
        Assert.NotNull(reason);

        // accepting the change updates the pin
        await using var c4 = new RelayClient(Pins(), Fast);
        await c4.ConnectAsync(relay2.Uri, _ => true, CancellationToken.None);
        Assert.Equal(TestRelay.Fingerprint(cert2), Pins().Get($"127.0.0.1:{port}"));
    }

    [Fact]
    public async Task HostFlow_CreateUpdateList()
    {
        await using var relay = new TestRelay(TestRelay.MakeCert());
        await relay.StartAsync();
        relay.Listings.Add(L);
        await using var c = new RelayClient(Pins(), Fast);
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        var code = await Within(c.ReserveCodeAsync());
        await Within(Task.Run(async () => { await c.CreateRoomAsync(code, "v", L); return 0; }));
        await c.UpdateListingAsync(L with { Players = 2 });
        var list = await Within(c.ListLobbiesAsync("sha"));
        Assert.Single(list);
        Assert.Equal("Game", list[0].Name);
        Assert.Empty(await Within(c.ListLobbiesAsync("other")));
        Assert.Contains(relay.Received, m => m is Hello { Role: "host", Version: Protocol.ProtocolVersion });
        Assert.Contains(relay.Received, m => m is UpdateListing { Listing.Players: 2 });
    }

    [Fact]
    public async Task Guest_JoinSendDeliverAndNotifications()
    {
        await using var relay = new TestRelay(TestRelay.MakeCert());
        await relay.StartAsync();
        await using var c = new RelayClient(Pins(), Fast);
        byte[]? got = null; string? from = null; string? left = null; bool hostLeft = false;
        c.Delivered += (f, b) => { from = f; got = b; };
        c.MemberLeft += m => left = m;
        c.HostLeft += () => hostLeft = true;
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        Assert.Equal("m1", await Within(c.JoinRoomAsync("ABC234", "ok-verifier")));
        await c.SendAsync(null, new byte[] { 1, 2, 3 });
        await Until(() => relay.Received.Any(m => m is Send { ToMemberId: null }));
        await relay.PushAllAsync(new Deliver("host", new byte[] { 9, 8 }));
        await relay.PushAllAsync(new MemberLeft("m7"));
        await relay.PushAllAsync(new HostLeft());
        await Until(() => got != null && left != null && hostLeft);
        Assert.Equal("host", from);
        Assert.Equal(new byte[] { 9, 8 }, got);
        Assert.Equal("m7", left);
        Assert.Contains(relay.Received, m => m is Hello { Role: "guest" });
    }

    [Fact]
    public async Task JoinRoom_Refused_Throws()
    {
        await using var relay = new TestRelay(TestRelay.MakeCert());
        await relay.StartAsync();
        await using var c = new RelayClient(Pins(), Fast);
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        var e = await Assert.ThrowsAsync<RelayException>(() => c.JoinRoomAsync("ABC234", "bad"));
        Assert.Equal("Wrong code or password", e.Message);
        // connection is still usable
        Assert.Equal("m1", await Within(c.JoinRoomAsync("ABC234", "ok-verifier")));
    }

    [Fact]
    public async Task Reconnect_RejoinsRoom()
    {
        var cert = TestRelay.MakeCert();
        var relay = new TestRelay(cert);
        await relay.StartAsync();
        var port = relay.Port;
        await using var c = new RelayClient(Pins(), Fast);
        int disconnects = 0, reconnects = 0;
        c.Disconnected += _ => Interlocked.Increment(ref disconnects);
        c.Reconnected += () => Interlocked.Increment(ref reconnects);
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await Within(c.JoinRoomAsync("ABC234", "ok-verifier"));
        await relay.StopAsync();
        await Until(() => disconnects >= 1);

        await using var relay2 = new TestRelay(cert, port);
        await relay2.StartAsync();
        await Until(() => reconnects >= 1);
        await Until(() => relay2.Received.Any(m => m is JoinRoom { Code: "ABC234", AuthVerifier: "ok-verifier" }));
        Assert.Contains(relay2.Received, m => m is Hello { Role: "guest" });
        // usable after reconnect
        await c.SendAsync("m2", new byte[] { 5 });
        await Until(() => relay2.Received.Any(m => m is Send { ToMemberId: "m2" }));
    }

    [Fact]
    public async Task Reconnect_HostRecreatesRoom()
    {
        var cert = TestRelay.MakeCert();
        var relay = new TestRelay(cert);
        await relay.StartAsync();
        var port = relay.Port;
        await using var c = new RelayClient(Pins(), Fast);
        int reconnects = 0;
        c.Reconnected += () => Interlocked.Increment(ref reconnects);
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await c.CreateRoomAsync("ABC234", "v", L);
        await relay.StopAsync();
        await using var relay2 = new TestRelay(cert, port);
        await relay2.StartAsync();
        await Until(() => reconnects >= 1);
        await Until(() => relay2.Received.Any(m => m is CreateRoom { Code: "ABC234", AuthVerifier: "v" }));
    }

    [Fact]
    public async Task Disconnected_RaisedOnRelayStop()
    {
        var relay = new TestRelay(TestRelay.MakeCert());
        await relay.StartAsync();
        await using var c = new RelayClient(Pins(), new[] { TimeSpan.FromHours(1) });
        string? reason = null;
        c.Disconnected += r => reason = r;
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await Within(c.ReserveCodeAsync());
        await relay.StopAsync();
        await Until(() => reason != null);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        await Assert.ThrowsAsync<RelayException>(() => c.ReserveCodeAsync());
    }

    [Fact]
    public async Task Connect_Unreachable_NoThrow_RaisesDisconnected()
    {
        await using var c = new RelayClient(Pins(), new[] { TimeSpan.FromHours(1) });
        string? reason = null;
        c.Disconnected += r => reason = r;
        await c.ConnectAsync(new Uri("wss://127.0.0.1:1/qm"), _ => true, CancellationToken.None);
        await Until(() => reason != null);
    }

    [Fact]
    public async Task UpdateListing_Refused_LinkStaysUp_EventRaised()
    {
        await using var relay = new TestRelay(TestRelay.MakeCert());
        await relay.StartAsync();
        relay.RefuseUpdates = true;
        await using var c = new RelayClient(Pins(), new[] { TimeSpan.FromHours(1) });
        string? refused = null, dropped = null;
        c.RequestRefused += r => refused = r;
        c.Disconnected += r => dropped = r;
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await c.CreateRoomAsync("ABC234", "v", L);
        await c.UpdateListingAsync(L);
        await Until(() => refused != null);
        Assert.Equal("Slow down", refused);
        Assert.Null(dropped);
        Assert.Equal("ABC234", await Within(c.ReserveCodeAsync()));
    }

    [Fact]
    public async Task Hello_IsAlwaysTheFirstMessage_UnderConcurrentPosts()
    {
        await using var relay = new TestRelay(TestRelay.MakeCert());
        await relay.StartAsync();
        await using var c = new RelayClient(Pins(), Fast);
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => c.SendAsync("m", new[] { (byte)i }))));
        await Until(() => relay.Received.Count(m => m is Send) == 20);
        Assert.IsType<Hello>(relay.Received.First());
        Assert.Single(relay.Received.OfType<Hello>());
    }

    private async Task<(TestRelay relay2, RelayClient client, Func<int> reconnects)> GuestThenRestart(int joinDelay, int nextId)
    {
        var cert = TestRelay.MakeCert();
        var relay = new TestRelay(cert);
        await relay.StartAsync();
        var c = new RelayClient(Pins(), Fast);
        int reconnects = 0, disconnects = 0;
        c.Reconnected += () => Interlocked.Increment(ref reconnects);
        c.Disconnected += _ => Interlocked.Increment(ref disconnects);
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await Within(c.JoinRoomAsync("ABC234", "ok-verifier"));
        await relay.StopAsync();
        await Until(() => disconnects >= 1);
        var relay2 = new TestRelay(cert, relay.Port) { JoinDelayMs = joinDelay, Ids = nextId };
        relay2.Listings.Add(L);
        await relay2.StartAsync();
        return (relay2, c, () => reconnects);
    }

    [Fact]
    public async Task Send_DuringRejoin_WaitsForRejoinAnswer()
    {
        var (relay2, c, reconnects) = await GuestThenRestart(400, 0);
        await using var _r = relay2; await using var _c = c;
        // Many concurrent senders hammer the client while it reconnects; none may overtake Hello + JoinRoom.
        var spam = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                try { await c.SendAsync("m2", new byte[] { 1 }); break; }
                catch (RelayException) { await Task.Delay(1); }
            }
        })).ToArray();
        await Within(Task.WhenAll(spam).ContinueWith(_ => 0));
        await Until(() => relay2.Received.Count(m => m is Send) == 8);
        var order = relay2.Received.Select(m => m.GetType()).ToList();
        Assert.Equal(new[] { typeof(Hello), typeof(JoinRoom) }, order.Take(2));
        await Until(() => reconnects() == 1);
    }

    [Fact]
    public async Task Rejoin_NoAnswer_TimesOut_AndRetries()
    {
        var cert = TestRelay.MakeCert();
        var relay = new TestRelay(cert);
        await relay.StartAsync();
        await using var c = new RelayClient(Pins(), Fast, TimeSpan.FromMilliseconds(150));
        int disconnects = 0;
        c.Disconnected += _ => Interlocked.Increment(ref disconnects);
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await Within(c.JoinRoomAsync("ABC234", "ok-verifier"));
        await relay.StopAsync();
        await Until(() => disconnects >= 1);
        await using var relay2 = new TestRelay(cert, relay.Port) { JoinDelayMs = 5000 };
        await relay2.StartAsync();
        await Until(() => relay2.Received.Count(m => m is JoinRoom) >= 2);
    }

    [Fact]
    public async Task RefusedPost_DoesNotFailUnrelatedPendingRequest()
    {
        await using var relay = new TestRelay(TestRelay.MakeCert()) { RefuseUpdates = true, ListDelayMs = 400 };
        await relay.StartAsync();
        relay.Listings.Add(L);
        await using var c = new RelayClient(Pins(), Fast);
        string? refused = null;
        c.RequestRefused += r => refused = r;
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        var list = c.ListLobbiesAsync("sha");
        await Until(() => relay.Received.Any(m => m is ListLobbies));
        await c.UpdateListingAsync(L);
        await Until(() => refused != null);
        Assert.Single(await Within(list));
    }

    [Fact]
    public async Task Request_DuringRejoin_IsNotMisroutedByRejoinReply()
    {
        var (relay2, c, reconnects) = await GuestThenRestart(400, 0);
        await using var _r = relay2; await using var _c = c;
        IReadOnlyList<LobbyListing> list;
        while (true)
        {
            try { list = await Within(c.ListLobbiesAsync("sha")); break; }
            catch (RelayException) { await Task.Delay(5); }
        }
        Assert.Single(list);
        await Until(() => reconnects() == 1);
        Assert.Equal("ABC234", await Within(c.ReserveCodeAsync()));
    }

    [Fact]
    public async Task MemberId_Exposed_AndUpdatedOnRejoin()
    {
        var cert = TestRelay.MakeCert();
        var relay = new TestRelay(cert);
        await relay.StartAsync();
        // The first retry waits long enough for relay2 to be listening, so the rejoin lands on relay2 rather than racing its start.
        await using var c = new RelayClient(Pins(), new[] { TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(40) });
        int reconnects = 0;
        c.Reconnected += () => Interlocked.Increment(ref reconnects);
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await Within(c.JoinRoomAsync("ABC234", "ok-verifier"));
        var firstId = Assert.Single(relay.JoinedIds);
        Assert.Equal(firstId, c.MemberId);
        await relay.StopAsync();
        await using var relay2 = new TestRelay(cert, relay.Port) { Ids = 4 };
        await relay2.StartAsync();
        await Until(() => Volatile.Read(ref reconnects) >= 1);
        // Under load a rejoin may be retried; the client must hold the id relay2 issued last.
        await Until(() => !relay2.JoinedIds.IsEmpty && c.MemberId == relay2.JoinedIds.Last());
        Assert.Equal(relay2.JoinedIds.Last(), c.MemberId);
        Assert.NotEqual(firstId, c.MemberId);
    }

    [Fact]
    public async Task InFlightRequest_FailsWhenLinkDrops_ThenWorksAfterReconnect()
    {
        var cert = TestRelay.MakeCert();
        var relay = new TestRelay(cert);
        await relay.StartAsync();
        await using var c = new RelayClient(Pins(), Fast);
        int reconnects = 0;
        c.Reconnected += () => Interlocked.Increment(ref reconnects);
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await relay.StopAsync();
        await Assert.ThrowsAsync<RelayException>(() => c.ReserveCodeAsync());
        await using var relay2 = new TestRelay(cert, relay.Port);
        await relay2.StartAsync();
        await Until(() => reconnects == 1);
        Assert.Equal("ABC234", await Within(c.ReserveCodeAsync()));
    }

    [Fact]
    public async Task Role_BrowserThenHost_ReconnectsAsHost_AndRejoinUsesLatestListing()
    {
        var cert = TestRelay.MakeCert();
        var relay = new TestRelay(cert);
        await relay.StartAsync();
        await using var c = new RelayClient(Pins(), Fast);
        int reconnects = 0, disconnects = 0;
        c.Reconnected += () => Interlocked.Increment(ref reconnects);
        c.Disconnected += _ => Interlocked.Increment(ref disconnects);
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await Within(c.ListLobbiesAsync("sha"));
        await c.CreateRoomAsync("ABC234", "v", L);
        await c.UpdateListingAsync(L with { Players = 2 });
        await relay.StopAsync();
        await Until(() => disconnects >= 1);
        await using var relay2 = new TestRelay(cert, relay.Port);
        await relay2.StartAsync();
        await Until(() => reconnects == 1);
        Assert.Contains(relay2.Received, m => m is Hello { Role: "host" });
        Assert.Contains(relay2.Received, m => m is CreateRoom { Draft.Players: 2 });
    }

    [Fact]
    public async Task Disposal_Behaviour()
    {
        await using var relay = new TestRelay(TestRelay.MakeCert());
        await relay.StartAsync();
        var c = new RelayClient(Pins(), Fast);
        await c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await Assert.ThrowsAsync<RelayException>(() => c.ConnectAsync(relay.Uri, _ => true, CancellationToken.None));
        await c.DisposeAsync();
        await Assert.ThrowsAsync<RelayException>(() => c.ReserveCodeAsync());
        await Assert.ThrowsAsync<RelayException>(() => c.SendAsync(null, new byte[1]));
        await c.DisposeAsync();

        var c2 = new RelayClient(Pins(), Fast);
        var connecting = c2.ConnectAsync(relay.Uri, _ => true, CancellationToken.None);
        await c2.DisposeAsync();
        await connecting;
        await Assert.ThrowsAsync<RelayException>(() => c2.ReserveCodeAsync());
    }
}
