using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Quartermaster.Net;
using static Quartermaster.Relay.Tests.RelayHarness;

namespace Quartermaster.Relay.Tests;

public class RelayTests
{
    [Fact]
    public async Task Hello_WrongVersion_Refused()
    {
        await using var relay = await Start();
        var c = await relay.Connect(role: null);
        await c.Send(new Hello(2, "guest"));
        var refused = await c.Receive<Refused>();
        Assert.Equal(Protocol.MismatchMessage(Protocol.ProtocolVersion), refused.Reason);
        Assert.NotNull(await c.ReceiveClose());

        // Anything before Hello is refused and closed too.
        var d = await relay.Connect(role: null);
        await d.Send(new ReserveCode());
        Assert.IsType<Refused>(await d.Receive());
        Assert.NotNull(await d.ReceiveClose());

        // Ping/Pong after a proper Hello.
        var e = await relay.Connect("browser");
        await e.AssertSilent();
    }

    [Fact]
    public async Task ReserveThenCreate_ListingVisibleToBrowser()
    {
        await using var relay = await Start();
        var host = await relay.Connect("host");
        await host.Send(new ReserveCode());
        var code = (await host.Receive<CodeReserved>()).Code;
        Assert.True(GameCode.IsValid(code));

        // A draft may carry any code; the relay fills its own.
        var draft = Draft() with { Code = "ZZZZZZ" };
        await host.Send(new CreateRoom(code, Verifier("pw", code), draft));
        Assert.Equal(code, (await host.Receive<RoomCreated>()).Code);

        var browser = await relay.Connect("browser", "198.51.100.7");
        await browser.Send(new ListLobbies("aa11"));
        var l = Assert.Single((await browser.Receive<Lobbies>()).Listings);
        Assert.Equal(draft with { Code = code, Sides = l.Sides, FreeSides = l.FreeSides }, l);
        Assert.Equal(["Red", "Blue"], l.Sides);
        Assert.Equal(["Blue"], l.FreeSides);

        // A reservation is held 60 s, then the code can no longer be used.
        await host.Send(new ReserveCode());
        var code2 = (await host.Receive<CodeReserved>()).Code;
        Assert.NotEqual(code, code2);
        var host2 = await relay.Connect("host");
        await host2.Send(new ReserveCode());
        var code3 = (await host2.Receive<CodeReserved>()).Code;
        relay.Time.Advance(TimeSpan.FromSeconds(61));
        await host2.Send(new CreateRoom(code3, Verifier("pw", code3), Draft()));
        Assert.IsType<Refused>(await host2.Receive());

        // A reservation belongs to the connection that made it.
        var host3 = await relay.Connect("host");
        await host3.Send(new ReserveCode());
        var code4 = (await host3.Receive<CodeReserved>()).Code;
        var thief = await relay.Connect("host", "192.0.2.99");
        await thief.Send(new CreateRoom(code4, Verifier("pw", code4), Draft()));
        Assert.IsType<Refused>(await thief.Receive());
    }

    [Fact]
    public async Task ListLobbies_FiltersByModule()
    {
        await using var relay = await Start();
        var a = await relay.Connect("host");
        var b = await relay.Connect("host");
        var codeA = await CreateRoom(a, draft: Draft("A game", sha: "aa11"));
        var codeB = await CreateRoom(b, draft: Draft("B game", sha: "bb22"));

        Assert.Equal([codeA], (await relay.List("aa11")).Select(l => l.Code));
        Assert.Equal([codeB], (await relay.List("bb22")).Select(l => l.Code));
        Assert.Empty(await relay.List("cc33"));
    }

    [Fact]
    public async Task Listing_ExpiresWithoutRefresh_AndOnHostLeave()
    {
        await using var relay = await Start();
        var host = await relay.Connect("host");
        var code = await CreateRoom(host);

        relay.Time.Advance(TimeSpan.FromSeconds(60));
        await host.Send(new UpdateListing(Draft("Refreshed")));
        await host.AssertSilent(); // processed
        relay.Time.Advance(TimeSpan.FromSeconds(80));
        var l = Assert.Single(await relay.List());
        Assert.Equal("Refreshed", l.Name);
        Assert.Equal(code, l.Code);

        relay.Time.Advance(TimeSpan.FromSeconds(11)); // 91 s since the refresh
        Assert.Empty(await relay.List());

        // Refreshing again brings it back while the host is connected.
        await host.Send(new UpdateListing(Draft("Back")));
        await host.AssertSilent();
        Assert.Single(await relay.List());

        // The host leaving removes the listing at once.
        await host.DisposeAsync();
        await Eventually(async () => (await relay.List()).Count == 0);
    }

    [Fact]
    public async Task UpdateListing_OnlyByHost()
    {
        await using var relay = await Start();
        var host = await relay.Connect("host");
        var code = await CreateRoom(host, "pw");
        var guest = await relay.Connect("guest", "198.51.100.1");
        await guest.Send(new JoinRoom(code, Verifier("pw", code)));
        await guest.Receive<Joined>();
        var browser = await relay.Connect("browser", "198.51.100.2");

        relay.Time.Advance(TimeSpan.FromSeconds(10));
        await guest.Send(new UpdateListing(Draft("Hijacked") with { Code = code }));
        await browser.Send(new UpdateListing(Draft("Hijacked") with { Code = code }));
        await guest.AssertSilent();
        await browser.AssertSilent();
        relay.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("Friday game", Assert.Single(await relay.List()).Name);

        await host.Send(new UpdateListing(Draft("Renamed")));
        await host.AssertSilent();
        Assert.Equal("Renamed", Assert.Single(await relay.List()).Name);
    }

    [Fact]
    public async Task Listing_LimitsPerIp_NameLength()
    {
        await using var relay = await Start();
        var longName = new string('N', 60);
        var first = await relay.Connect("host", "203.0.113.50");
        var code = await CreateRoom(first, draft: Draft(longName));
        Assert.Equal(new string('N', 40), Assert.Single(await relay.List()).Name);

        // Refresh at most every 5 s: an early update shows up only once 5 s have passed.
        relay.Time.Advance(TimeSpan.FromSeconds(2));
        await first.Send(new UpdateListing(Draft("Early")));
        await first.AssertSilent();
        Assert.Equal(new string('N', 40), Assert.Single(await relay.List()).Name);
        relay.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("Early", Assert.Single(await relay.List()).Name);

        // 20 lobbies per address.
        for (int i = 1; i < 20; i++)
            await CreateRoom(await relay.Connect("host", "203.0.113.50"));
        var extra = await relay.Connect("host", "203.0.113.50");
        await extra.Send(new ReserveCode());
        var c21 = (await extra.Receive<CodeReserved>()).Code;
        await extra.Send(new CreateRoom(c21, Verifier("pw", c21), Draft()));
        Assert.Contains("Too many", Assert.IsType<Refused>(await extra.Receive()).Reason);
        Assert.Equal(20, (await relay.List()).Count);

        // Another address is not affected.
        await CreateRoom(await relay.Connect("host", "203.0.113.51"));
        Assert.Equal(21, (await relay.List()).Count);
        Assert.Contains(code, (await relay.List()).Select(l => l.Code));
    }

    [Fact]
    public async Task CreateAndJoin_RoutesSealedBytes()
    {
        await using var relay = await Start();
        var host = await relay.Connect("host");
        var code = await CreateRoom(host, "secret");
        var guest = await relay.Connect("guest", "198.51.100.1");
        await guest.Send(new JoinRoom(code.ToLowerInvariant(), Verifier("secret", code)));
        var id = (await guest.Receive<Joined>()).MemberId;

        byte[] toHost = [1, 2, 3, 250];
        await guest.Send(new Send(null, toHost));
        var d = await host.Receive<Deliver>();
        Assert.Equal(id, d.FromMemberId);
        Assert.Equal(toHost, d.Sealed);

        byte[] toGuest = [9, 8, 7];
        await host.Send(new Send(id, toGuest));
        var g = await guest.Receive<Deliver>();
        Assert.Equal(toGuest, g.Sealed);
        Assert.NotEqual(id, g.FromMemberId);
    }

    [Fact]
    public async Task WrongPassword_RefusedSameMessage()
    {
        await using var relay = await Start();
        var host = await relay.Connect("host");
        var code = await CreateRoom(host, "right");

        var g1 = await relay.Connect("guest", "198.51.100.1");
        await g1.Send(new JoinRoom(code, Verifier("wrong", code)));
        var wrongPassword = (await g1.Receive<Refused>()).Reason;

        var other = code == "222222" ? "333333" : "222222";
        var g2 = await relay.Connect("guest", "198.51.100.1");
        await g2.Send(new JoinRoom(other, Verifier("right", other)));
        var wrongCode = (await g2.Receive<Refused>()).Reason;

        var g3 = await relay.Connect("guest", "198.51.100.1");
        await g3.Send(new JoinRoom(code, "not-hex"));
        var garbage = (await g3.Receive<Refused>()).Reason;

        Assert.Equal("Wrong code or password", wrongPassword);
        Assert.Equal(wrongPassword, wrongCode);
        Assert.Equal(wrongPassword, garbage);

        // The open lobby (empty password) works with the empty-password verifier only.
        var openHost = await relay.Connect("host");
        var openCode = await CreateRoom(openHost, "");
        var g4 = await relay.Connect("guest", "198.51.100.1");
        await g4.Send(new JoinRoom(openCode, Verifier("", openCode)));
        await g4.Receive<Joined>();

        // Seven members per room (host + 6).
        for (int i = 0; i < 5; i++)
        {
            var g = await relay.Connect("guest", "198.51.100.1");
            await g.Send(new JoinRoom(openCode, Verifier("", openCode)));
            await g.Receive<Joined>();
        }
        var eighth = await relay.Connect("guest", "198.51.100.1");
        await eighth.Send(new JoinRoom(openCode, Verifier("", openCode)));
        Assert.Contains("full", Assert.IsType<Refused>(await eighth.Receive()).Reason);
    }

    [Fact]
    public async Task Send_ToMember_OnlyThatMemberGetsIt()
    {
        await using var relay = await Start();
        var host = await relay.Connect("host");
        var code = await CreateRoom(host);
        var a = await relay.Connect("guest", "198.51.100.1");
        await a.Send(new JoinRoom(code, Verifier("pw", code)));
        var idA = (await a.Receive<Joined>()).MemberId;
        var b = await relay.Connect("guest", "198.51.100.2");
        await b.Send(new JoinRoom(code, Verifier("pw", code)));
        var idB = (await b.Receive<Joined>()).MemberId;
        Assert.NotEqual(idA, idB);

        await host.Send(new Send(idA, [42]));
        Assert.Equal([42], (await a.Receive<Deliver>()).Sealed);
        await b.AssertSilent();

        // A guest cannot address another guest directly.
        await a.Send(new Send(idB, [43]));
        await a.AssertSilent();
        await b.AssertSilent();
        await host.AssertSilent();
    }

    [Fact]
    public async Task HostLeft_NotifiesGuests()
    {
        await using var relay = await Start();
        var host = await relay.Connect("host");
        var code = await CreateRoom(host);
        var a = await relay.Connect("guest", "198.51.100.1");
        await a.Send(new JoinRoom(code, Verifier("pw", code)));
        await a.Receive<Joined>();
        var b = await relay.Connect("guest", "198.51.100.2");
        await b.Send(new JoinRoom(code, Verifier("pw", code)));
        var idB = (await b.Receive<Joined>()).MemberId;

        await b.DisposeAsync();
        Assert.Equal(idB, (await host.Receive<MemberLeft>()).MemberId);

        await host.DisposeAsync();
        await a.Receive<HostLeft>();
    }

    [Fact]
    public async Task Host_Reconnects_SameRoom()
    {
        await using var relay = await Start();
        var host = await relay.Connect("host");
        var code = await CreateRoom(host, "pw");
        var guest = await relay.Connect("guest", "198.51.100.1");
        await guest.Send(new JoinRoom(code, Verifier("pw", code)));
        var id = (await guest.Receive<Joined>()).MemberId;

        await host.DisposeAsync();
        await guest.Receive<HostLeft>();
        relay.Time.Advance(TimeSpan.FromHours(23));

        // Wrong password cannot take the room.
        var impostor = await relay.Connect("host", "192.0.2.1");
        await impostor.Send(new CreateRoom(code, Verifier("nope", code), Draft()));
        Assert.Equal("Wrong code or password", (await impostor.Receive<Refused>()).Reason);

        var back = await relay.Connect("host");
        await back.Send(new CreateRoom(code, Verifier("pw", code), Draft("Again")));
        Assert.Equal(code, (await back.Receive<RoomCreated>()).Code);
        Assert.Equal("Again", Assert.Single(await relay.List()).Name);

        // A second host cannot take a room whose host is present.
        var second = await relay.Connect("host");
        await second.Send(new CreateRoom(code, Verifier("pw", code), Draft()));
        Assert.IsType<Refused>(await second.Receive());

        await guest.Send(new Send(null, [5]));
        var d = await back.Receive<Deliver>();
        Assert.Equal(id, d.FromMemberId);

        // Without its host for over 24 h the room closes; its code is gone.
        await back.DisposeAsync();
        await guest.Receive<HostLeft>();
        relay.Time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(2));
        Assert.NotNull(await guest.ReceiveClose());
        var late = await relay.Connect("guest");
        await late.Send(new JoinRoom(code, Verifier("pw", code)));
        Assert.Equal("Wrong code or password", (await late.Receive<Refused>()).Reason);
    }

    [Fact]
    public async Task Oversized_Closed()
    {
        await using var relay = await Start();
        var c = await relay.Connect("host");
        await c.SendRaw(new byte[Framing.MaxFrame + 1]);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await c.ReceiveClose());

        // Garbage of normal size is ignored, not fatal.
        var d = await relay.Connect("host");
        await d.SendRaw(Encoding.UTF8.GetBytes("{not json"));
        await d.AssertSilent();
    }

    [Fact]
    public async Task RateLimit_Closes()
    {
        await using var relay = await Start();
        // A burst of 200 is fine (clock frozen).
        var ok = await relay.Connect("host");
        for (int i = 0; i < 198; i++) await ok.Send(new Ping());
        for (int i = 0; i < 198; i++) await ok.Receive<Pong>();
        relay.Time.Advance(TimeSpan.FromSeconds(1)); // +50
        for (int i = 0; i < 50; i++) await ok.Send(new Ping());
        for (int i = 0; i < 50; i++) await ok.Receive<Pong>();

        var flood = await relay.Connect("host");
        for (int i = 0; i < 260; i++)
        {
            try { await flood.Send(new Ping()); }
            catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException) { break; }
        }
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await flood.ReceiveClose());
    }

    [Fact]
    public async Task SelfSignedCert_CreatedOnce_FingerprintLogged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-relay-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var opts = new RelayOptions { DataDir = dir };
            var first = Certificates.LoadOrCreate(opts);
            Assert.True(first.Created);
            Assert.True(first.Certificate.HasPrivateKey);
            Assert.NotNull(first.Certificate.GetECDsaPublicKey());
            Assert.Equal(first.Certificate.GetCertHashString(HashAlgorithmName.SHA256), first.Fingerprint);
            var file = Path.Combine(dir, Certificates.FileName);
            var written = File.GetLastWriteTimeUtc(file);

            var second = Certificates.LoadOrCreate(opts);
            Assert.False(second.Created);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.Equal(written, File.GetLastWriteTimeUtc(file));
            Assert.Single(Directory.GetFiles(dir));

            // A configured .pfx is used as is.
            var pfx = Path.Combine(dir, "mine.pfx");
            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var req = new CertificateRequest("CN=Configured", key, HashAlgorithmName.SHA256);
                using var own = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
                File.WriteAllBytes(pfx, own.Export(X509ContentType.Pkcs12, "pfxpw"));
            }
            var configured = Certificates.LoadOrCreate(new RelayOptions { DataDir = dir, CertificatePath = pfx, CertificatePassword = "pfxpw" });
            Assert.False(configured.Created);
            Assert.Equal("CN=Configured", configured.Certificate.Subject);

            // Startup (real Kestrel, TLS) logs the fingerprint and serves that certificate.
            var logs = new LogCapture();
            var app = RelayApp.Build([], b =>
            {
                b.Configuration["Relay:DataDir"] = dir;
                b.Configuration["Relay:Port"] = "0";
                b.Logging.AddProvider(logs);
            });
            await app.StartAsync();
            try
            {
                Assert.Contains($"Relay certificate fingerprint: {first.Fingerprint}", logs.All);
                var address = new Uri(app.Urls.First());
                string? seen = null;
                using var ws = new ClientWebSocket();
                ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    seen = cert?.GetCertHashString(HashAlgorithmName.SHA256);
                    return true;
                };
                await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{address.Port}/qm"), CancellationToken.None);
                Assert.Equal(first.Fingerprint, seen);
                await ws.SendAsync(Framing.Encode<RelayMessage>(new Hello(1, "browser")), WebSocketMessageType.Text, true, CancellationToken.None);
                await ws.SendAsync(Framing.Encode<RelayMessage>(new Ping()), WebSocketMessageType.Text, true, CancellationToken.None);
                var buf = new byte[4096];
                var r = await ws.ReceiveAsync(buf, new CancellationTokenSource(10_000).Token);
                Assert.IsType<Pong>(Framing.Decode<RelayMessage>(buf.AsSpan(0, r.Count)));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Relay_NeverLogsOrStoresPayload()
    {
        await using var relay = await Start();
        var marker = Encoding.UTF8.GetBytes("PAYLOAD-MARKER-7f3a9c");
        var host = await relay.Connect("host");
        var code = await CreateRoom(host);
        var guest = await relay.Connect("guest", "198.51.100.1");
        await guest.Send(new JoinRoom(code, Verifier("pw", code)));
        var id = (await guest.Receive<Joined>()).MemberId;
        await guest.Send(new Send(null, marker));
        Assert.Equal(marker, (await host.Receive<Deliver>()).Sealed);
        await host.Send(new Send(id, marker));
        Assert.Equal(marker, (await guest.Receive<Deliver>()).Sealed);
        await host.Send(new Send("nobody", marker));
        await guest.SendRaw(Encoding.UTF8.GetBytes("{\"$t\":\"Send\",\"Sealed\":\"PAYLOAD-MARKER-7f3a9c\"")); // malformed
        await guest.DisposeAsync();
        await host.Receive<MemberLeft>();
        await host.AssertSilent();

        var logs = relay.Logs.All;
        Assert.NotEmpty(relay.Logs.Lines);
        Assert.DoesNotContain("PAYLOAD-MARKER", logs);
        Assert.DoesNotContain(Convert.ToBase64String(marker), logs);
        Assert.DoesNotContain(Verifier("pw", code), logs, StringComparison.OrdinalIgnoreCase);

        foreach (var f in Directory.Exists(relay.DataDir) ? Directory.GetFiles(relay.DataDir, "*", SearchOption.AllDirectories) : [])
        {
            var bytes = File.ReadAllBytes(f);
            Assert.False(bytes.AsSpan().IndexOf(marker) >= 0, $"{f} holds the payload");
        }
    }
}
