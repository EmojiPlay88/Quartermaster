using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Relay;

namespace Quartermaster.Render.Tests;

/// <summary>The turn mailbox service against the real relay (TLS loopback, temp data folder); invented players Alice (p1) and Bob (p2).</summary>
public sealed class MailboxServiceTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-mailbox-svc-" + Guid.NewGuid().ToString("N"));
    private readonly string _gameId = Guid.NewGuid().ToString("N");
    private readonly List<MailboxService> _services = new();
    private WebApplication? _app;
    private int _port;
    private int _n;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _services) s.Stop();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task StartRelay()
    {
        _app = RelayApp.Build([], b =>
        {
            b.Logging.ClearProviders();
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:Port"] = _port.ToString(),
                ["Relay:DataDir"] = Path.Combine(_dir, "relay"),
            });
        });
        await _app.StartAsync();
    }

    private string RelayText => $"wss://127.0.0.1:{_port}/qm";

    private sealed class Probe
    {
        public readonly ConcurrentQueue<string> Statuses = new();
        public readonly ConcurrentQueue<(TurnFile T, string Item)> Turns = new();
        public int Connects;
    }

    private (MailboxService S, Probe P) Service(TimeProvider? time = null)
    {
        var p = new Probe();
        var s = new MailboxService(_ =>
        {
            Interlocked.Increment(ref p.Connects);
            return new RelayClient(new PinStore(Path.Combine(_dir, $"pins{Interlocked.Increment(ref _n)}.json")),
                new[] { TimeSpan.FromMilliseconds(20) });
        }, a => a(), time, _ => true);
        s.Status += m => p.Statuses.Enqueue(m);
        s.TurnWaiting += (t, item) => p.Turns.Enqueue((t, item));
        _services.Add(s);
        return (s, p);
    }

    private TurnIdentity Identity(string who, string player)
    {
        var id = TurnIdentity.Load(_gameId, Path.Combine(_dir, "turns-" + who), null);
        id.LocalPlayerId = player;
        id.Relay = RelayText;
        id.UseMailbox = true;
        return id;
    }

    private TurnFile Turn(string fromName = "Alice", string? to = "p2", string startHash = "start-1") =>
        new(TurnFiles.Version, "aa11", null, _gameId, "p1", fromName, to, DateTime.UtcNow, new GameState { GameId = _gameId },
            startHash, Array.Empty<GameCommand>(), "end-1");

    private async Task<RelayClient> RawClient()
    {
        var c = new RelayClient(new PinStore(Path.Combine(_dir, $"raw{Interlocked.Increment(ref _n)}.json")), new[] { TimeSpan.FromMilliseconds(20) });
        await c.ConnectAsync(new Uri(RelayText), _ => true, CancellationToken.None);
        return c;
    }

    private string BoxOf(string player, string password = "pw") => Secrets.MailboxBoxId(Secrets.MailboxBoxKey(password, _gameId), player);

    private static async Task Until(Func<bool> cond, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Send_ThenWatch_RaisesTurnWaiting_WithSendersName()
    {
        await StartRelay();
        var (alice, ap) = Service();
        Assert.True(await alice.SendAsync(Identity("alice", "p1"), Turn(), "pw"));
        Assert.Empty(ap.Statuses);

        var clock = new FakeTimeProvider();
        var (bob, bp) = Service(clock);
        bob.Watch(_gameId, Identity("bob", "p2"), "pw");
        await Until(() => !bp.Turns.IsEmpty);
        await bob.CurrentPoll;
        var (t, item) = Assert.Single(bp.Turns);
        Assert.Equal("Alice", t.FromName);
        Assert.Equal("start-1", t.StartHash);
        Assert.True(MailboxLimits.IsItemId(item));
        Assert.Empty(bp.Statuses);

        // The next poll does not raise the same item again.
        int polls = bob.Polls;
        clock.Advance(MailboxService.PollInterval);
        await Until(() => bob.Polls > polls);
        Assert.Single(bp.Turns);

        // Alice's own box stays empty: the turn went to Bob's.
        await using var raw = await RawClient();
        Assert.Empty(await raw.MailboxListAsync([BoxOf("p1")]));
        Assert.Single(await raw.MailboxListAsync([BoxOf("p2")]));

        await bob.DeleteAsync(item);
        Assert.Empty(await raw.MailboxListAsync([BoxOf("p2")]));
        Assert.Empty(bp.Statuses);

        // No recipient: refused before anything is sent.
        int connects = ap.Connects;
        Assert.False(await alice.SendAsync(Identity("alice", "p1"), Turn(to: null), "pw"));
        Assert.Equal(MailboxService.NoRecipientText, Assert.Single(ap.Statuses));
        Assert.Equal(connects, ap.Connects);
    }

    [Fact]
    public async Task WrongPassword_Ignored_StatusOnce()
    {
        await StartRelay();
        var box = BoxOf("p2");
        await using (var raw = await RawClient())
        {
            var wrongKey = Secrets.MailboxSealKey("other", _gameId);
            await raw.MailboxPutAsync(box, Secrets.Seal(wrongKey, TurnFiles.ToBytes(Turn())));
            await raw.MailboxPutAsync(box, RandomNumberGenerator.GetBytes(200));
        }

        var clock = new FakeTimeProvider();
        var (bob, bp) = Service(clock);
        bob.Watch(_gameId, Identity("bob", "p2"), "pw");
        await Until(() => bob.Polls >= 1);
        Assert.Empty(bp.Turns);
        Assert.Equal(MailboxService.UnreadableText, Assert.Single(bp.Statuses));

        clock.Advance(MailboxService.PollInterval);
        await Until(() => bob.Polls >= 2);
        Assert.Empty(bp.Turns);
        Assert.Single(bp.Statuses);

        // A good turn in the same box still comes through.
        var (alice, _) = Service();
        Assert.True(await alice.SendAsync(Identity("alice", "p1"), Turn(), "pw"));
        clock.Advance(MailboxService.PollInterval);
        await Until(() => bob.Polls >= 3);
        Assert.Equal("Alice", Assert.Single(bp.Turns).T.FromName);
        Assert.Single(bp.Statuses);
    }

    [Fact]
    public async Task RelayDown_NoCrash_StatusOnce_RecoversNextPoll()
    {
        var clock = new FakeTimeProvider();
        var (bob, bp) = Service(clock);
        bob.Watch(_gameId, Identity("bob", "p2"), "pw");
        await Until(() => bob.Polls >= 1);
        var status = Assert.Single(bp.Statuses);
        Assert.StartsWith("Turn mailbox: ", status);

        clock.Advance(MailboxService.PollInterval);
        await Until(() => bob.Polls >= 2);
        Assert.Single(bp.Statuses);

        // Less than a full interval: no poll.
        clock.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(100);
        Assert.Equal(2, bob.Polls);

        await StartRelay();
        var (alice, _) = Service();
        Assert.True(await alice.SendAsync(Identity("alice", "p1"), Turn(), "pw"));
        clock.Advance(TimeSpan.FromMinutes(1));
        await Until(() => bob.Polls >= 3);
        Assert.Equal("Alice", Assert.Single(bp.Turns).T.FromName);
        Assert.Single(bp.Statuses);

        // After Stop nothing polls any more.
        bob.Stop();
        clock.Advance(MailboxService.PollInterval * 3);
        await Task.Delay(100);
        Assert.Equal(3, bob.Polls);
    }

    [Fact]
    public async Task Played_Item_Deleted()
    {
        await StartRelay();
        var (alice, _) = Service();
        Assert.True(await alice.SendAsync(Identity("alice", "p1"), Turn(startHash: "played"), "pw"));
        Assert.True(await alice.SendAsync(Identity("alice", "p1"), Turn(startHash: "fresh"), "pw"));

        var id = Identity("bob", "p2");
        id.RecordPlayed("played");
        var (bob, bp) = Service(new FakeTimeProvider());
        bob.Watch(_gameId, id, "pw");
        await using var raw = await RawClient();
        await Until(() => raw.MailboxListAsync([BoxOf("p2")]).Result.Count == 1);
        Assert.Equal("fresh", Assert.Single(bp.Turns).T.StartHash);
        Assert.Empty(bp.Statuses);
    }

    [Fact]
    public async Task Oversized_Refused_WithCopy()
    {
        // Random text does not compress: the sealed turn is larger than one relay frame. No relay runs: nothing may connect.
        var big = Convert.ToBase64String(RandomNumberGenerator.GetBytes(MailboxLimits.MaxSealedBytes + 1024 * 1024));
        var (alice, ap) = Service();
        Assert.False(await alice.SendAsync(Identity("alice", "p1"), Turn(fromName: big), "pw"));
        Assert.Equal("This turn is too large for the mailbox; send the file another way.", Assert.Single(ap.Statuses));
        Assert.Equal(0, ap.Connects);
        Assert.Equal(MailboxService.TooLargeText, await alice.TrySendAsync(Identity("alice", "p1"), Turn(fromName: big), "pw"));
    }

    [Fact]
    public async Task TrySend_RelayDown_GivesTheReason()
    {
        var (alice, _) = Service();
        var why = await alice.TrySendAsync(Identity("alice", "p1"), Turn(), "pw");
        Assert.NotNull(why);
        Assert.StartsWith("Turn mailbox: ", why);
    }

    /// <summary>Seals like a sender does: the game's key, the recipient's box id as associated data.</summary>
    private byte[] SealFor(string box, TurnFile t) => Secrets.Seal(Secrets.MailboxSealKey("pw", _gameId), TurnFiles.ToBytes(t), box);

    [Fact]
    public async Task ItemMovedToAnotherBox_DoesNotOpen()
    {
        await StartRelay();
        var (alice, _) = Service();
        Assert.True(await alice.SendAsync(Identity("alice", "p1"), Turn(to: "p3"), "pw"));
        await using (var raw = await RawClient())
        {
            // The relay (or anyone holding the blob) copies Carol's item into Bob's box: same key, other box.
            var item = Assert.Single(await raw.MailboxListAsync([BoxOf("p3")]));
            var blob = await raw.MailboxGetAsync(BoxOf("p3"), item.ItemId);
            await raw.MailboxPutAsync(BoxOf("p2"), blob);
            Assert.ThrowsAny<CryptographicException>(() =>
                Secrets.Open(Secrets.MailboxSealKey("pw", _gameId), blob, (int)TurnFiles.MaxPlain, BoxOf("p2")));
            Assert.NotEmpty(Secrets.Open(Secrets.MailboxSealKey("pw", _gameId), blob, (int)TurnFiles.MaxPlain, BoxOf("p3")));
        }
        var (bob, bp) = Service(new FakeTimeProvider());
        bob.Watch(_gameId, Identity("bob", "p2"), "pw");
        await Until(() => bob.Polls >= 1);
        Assert.Empty(bp.Turns);
        Assert.Equal(MailboxService.UnreadableText, Assert.Single(bp.Statuses));
    }

    [Fact]
    public async Task WrongGame_OtherRecipient_OwnTurn_SkippedQuietly()
    {
        await StartRelay();
        var box = BoxOf("p2");
        await using (var raw = await RawClient())
        {
            await raw.MailboxPutAsync(box, SealFor(box, Turn(startHash: "other-game") with { GameId = Guid.NewGuid().ToString("N") }));
            await raw.MailboxPutAsync(box, SealFor(box, Turn(to: "p3", startHash: "for-carol")));
            await raw.MailboxPutAsync(box, SealFor(box, Turn(startHash: "own") with { FromPlayerId = "p2" }));
            await raw.MailboxPutAsync(box, SealFor(box, Turn(startHash: "good")));
        }
        var clock = new FakeTimeProvider();
        var (bob, bp) = Service(clock);
        bob.Watch(_gameId, Identity("bob", "p2"), "pw");
        await Until(() => bob.Polls >= 1);
        Assert.Equal("good", Assert.Single(bp.Turns).T.StartHash);
        Assert.Empty(bp.Statuses);
        clock.Advance(MailboxService.PollInterval);
        await Until(() => bob.Polls >= 2);
        Assert.Single(bp.Turns);   // skipped items are not looked at again
        Assert.Empty(bp.Statuses);
    }

    [Fact]
    public async Task SetIdentity_PlayedTurnDeleted_NotRaisedAgain()
    {
        await StartRelay();
        var clock = new FakeTimeProvider();
        var (bob, bp) = Service(clock);
        bob.Watch(_gameId, Identity("bob", "p2"), "pw");
        await Until(() => bob.Polls >= 1);
        // The turn was replayed here meanwhile: the app hands the watch the identity that remembers it.
        var played = Identity("bob", "p2");
        played.RecordPlayed("start-1");
        bob.SetIdentity(played);
        var (alice, _) = Service();
        Assert.True(await alice.SendAsync(Identity("alice", "p1"), Turn(), "pw"));
        clock.Advance(MailboxService.PollInterval);
        await using var raw = await RawClient();
        await Until(() => raw.MailboxListAsync([BoxOf("p2")]).Result.Count == 0);
        Assert.Empty(bp.Turns);
        Assert.Empty(bp.Statuses);
    }

    [Fact]
    public async Task Stop_RightAfterWatch_NoStatus_NoTurn_NoThrow()
    {
        await StartRelay();
        var (alice, _) = Service();
        Assert.True(await alice.SendAsync(Identity("alice", "p1"), Turn(), "pw"));
        var (bob, bp) = Service(new FakeTimeProvider());
        bob.Watch(_gameId, Identity("bob", "p2"), "pw");
        bob.Stop();
        bob.Stop();   // twice is fine
        await Task.Delay(300);
        Assert.Empty(bp.Turns);
        Assert.Empty(bp.Statuses);
        await bob.DeleteAsync(new string('a', 32));   // nothing watched: nothing happens
    }
}
