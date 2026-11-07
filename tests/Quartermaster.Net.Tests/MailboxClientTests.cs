using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Quartermaster.Net;
using Quartermaster.Relay;

namespace Quartermaster.Net.Tests;

/// <summary>Mailbox key derivations, and the RelayClient mailbox calls against the real relay (TLS loopback).</summary>
public sealed class MailboxClientTests : IAsyncLifetime
{
    private const string GameId = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-net-mailbox-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Secrets_Mailbox_DerivationsStable()
    {
        // Pinned once (PBKDF2-SHA256, 200,000 iterations; SHA-256 box ids). Changing them orphans every stored turn.
        var boxKey = Secrets.MailboxBoxKey("", GameId);
        Assert.Equal("ab610a99bfa254c2378f2e49e68bfdc6c343c028fcf0238c321c2eff3b27b0e2", Convert.ToHexString(boxKey).ToLowerInvariant());
        Assert.Equal("c96552d3031946d147b5aaf3713d1f8de7eeefe99b5df7037abff73f2315e0c7",
            Convert.ToHexString(Secrets.MailboxSealKey("", GameId)).ToLowerInvariant());
        Assert.Equal("ca772fdcdb6948e20cf5c203f030e75f6de30c9414a7151d1e9e5e1d22ec0b40", Secrets.MailboxBoxId(boxKey, "p1"));

        var withPassword = Secrets.MailboxBoxKey("correct horse", GameId);
        Assert.Equal("c89ad3f59313e191174bda85e153f5317ebf8bd3111530b79dc034495a477bd2", Convert.ToHexString(withPassword).ToLowerInvariant());
        Assert.Equal("9e17e73486b0725da7cf233bd658ff2071d9b8ef9c1f69aa1b9cb561a36367ce",
            Convert.ToHexString(Secrets.MailboxSealKey("correct horse", GameId)).ToLowerInvariant());
        Assert.Equal("4d1b9e5bedd48e0a0057814f48cd934be7eaa9da489110f1beaa46877a5e52fc", Secrets.MailboxBoxId(withPassword, "p1"));
        Assert.Equal("db1ce0be8f2a8ffe36c33d5869d8aaa581783bc9fed6cc58a33c04523f73801e", Secrets.MailboxBoxId(withPassword, "Blåbär"));

        // Box ids are what the relay accepts; box key and seal key differ; other games and players get other boxes.
        Assert.True(MailboxLimits.IsBoxId(Secrets.MailboxBoxId(boxKey, "p1")));
        Assert.NotEqual(boxKey, Secrets.MailboxSealKey("", GameId));
        Assert.NotEqual(Secrets.MailboxBoxId(boxKey, "p1"), Secrets.MailboxBoxId(boxKey, "p2"));
        Assert.NotEqual(Secrets.MailboxBoxId(boxKey, "p1"),
            Secrets.MailboxBoxId(Secrets.MailboxBoxKey("", Guid.NewGuid().ToString()), "p1"));
        Assert.Throws<ArgumentException>(() => Secrets.MailboxBoxId(new byte[16], "p1"));
    }

    [Fact]
    public async Task Client_Mailbox_AgainstRealRelay()
    {
        var boxKey = Secrets.MailboxBoxKey("pw", GameId);
        var sealKey = Secrets.MailboxSealKey("pw", GameId);
        var blue = Secrets.MailboxBoxId(boxKey, "blue-player");
        var red = Secrets.MailboxBoxId(boxKey, "red-player");
        var turn = Encoding.UTF8.GetBytes("{\"turn\":\"Red to Blue\"}");

        await using var sender = await NewClient();
        var id = await sender.MailboxPutAsync(blue, Secrets.Seal(sealKey, turn));
        Assert.True(MailboxLimits.IsItemId(id));

        await using var receiver = await NewClient();
        var items = await receiver.MailboxListAsync([blue, red]);
        var info = Assert.Single(items);
        Assert.Equal((blue, id), (info.Box, info.ItemId));
        Assert.True(info.Size > 28);
        Assert.Equal(DateTimeKind.Utc, info.StoredUtc.Kind);

        var sealed_ = await receiver.MailboxGetAsync(blue, id);
        Assert.Equal(info.Size, sealed_.Length);
        Assert.Equal(turn, Secrets.Open(sealKey, sealed_));
        Assert.ThrowsAny<CryptographicException>(() => Secrets.Open(Secrets.MailboxSealKey("wrong", GameId), sealed_));

        await receiver.MailboxDeleteAsync(blue, id);
        Assert.Empty(await receiver.MailboxListAsync([blue]));

        // Refusals surface as RelayException with the relay's text; the link stays up.
        var missing = await Assert.ThrowsAsync<RelayException>(() => receiver.MailboxGetAsync(blue, id));
        Assert.Equal(MailboxLimits.NoSuchItem, missing.Message);
        Assert.Equal(MailboxLimits.NoSuchItem, (await Assert.ThrowsAsync<RelayException>(() => receiver.MailboxDeleteAsync(blue, id))).Message);

        // Bad input is refused before it is sent.
        Assert.Equal(MailboxLimits.BadBox, (await Assert.ThrowsAsync<RelayException>(() => receiver.MailboxPutAsync("nope", [1]))).Message);
        Assert.Equal(MailboxLimits.BadBox, (await Assert.ThrowsAsync<RelayException>(() => receiver.MailboxListAsync([blue.ToUpperInvariant()]))).Message);
        Assert.Equal(MailboxLimits.BadBox, (await Assert.ThrowsAsync<RelayException>(
            () => receiver.MailboxListAsync(Enumerable.Repeat(blue, MailboxLimits.MaxBoxesPerList + 1).ToList()))).Message);
        Assert.Equal(MailboxLimits.BadItem, (await Assert.ThrowsAsync<RelayException>(() => receiver.MailboxPutAsync(blue, []))).Message);
        Assert.Equal(MailboxLimits.BadItem, (await Assert.ThrowsAsync<RelayException>(
            () => receiver.MailboxPutAsync(blue, new byte[MailboxLimits.MaxSealedBytes + 1]))).Message);
        Assert.Empty(await receiver.MailboxListAsync([blue]));

        // A connection that started with the mailbox can still host a room, and a host in a room can use the mailbox
        // without disturbing the room.
        await using var host = await NewClient();
        Assert.Empty(await host.MailboxListAsync([red]));
        var code = await host.ReserveCodeAsync();
        var verifier = Secrets.AuthVerifier("pw", code);
        await host.CreateRoomAsync(code, verifier, new LobbyListing("", "Friday", "Red player", "aa11", "Mod",
            ["Red", "Blue"], ["Blue"], 1, 2, true, Protocol.ProtocolVersion));
        await using var guest = await NewClient();
        var got = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Delivered += (_, b) => got.TrySetResult(b);
        await guest.JoinRoomAsync(code, verifier);
        var id2 = await guest.MailboxPutAsync(red, Secrets.Seal(sealKey, turn));
        Assert.Equal(id2, Assert.Single(await host.MailboxListAsync([red])).ItemId);
        await guest.SendAsync(null, [42]);
        Assert.Equal([42], await got.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
