using System.Security.Cryptography;
using Quartermaster.Net;
using static Quartermaster.Relay.Tests.RelayHarness;

namespace Quartermaster.Relay.Tests;

/// <summary>Runs only on Unix (file modes); skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix file modes only.";
    }
}

public class MailboxTests
{
    private static string Box(string seed) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
    private static readonly string BoxA = Box("a"), BoxB = Box("b");

    private static byte[] Blob(int size, byte fill = 7) => Enumerable.Repeat(fill, size).ToArray();

    private static async Task<string> Put(RelayTestClient c, string box, byte[] data)
    {
        await c.Send(new MailboxPut(box, data));
        var id = (await c.Receive<MailboxStored>()).ItemId;
        Assert.True(MailboxLimits.IsItemId(id));
        return id;
    }

    private static async Task<string> Refused(RelayTestClient c, RelayMessage m)
    {
        await c.Send(m);
        return (await c.Receive<Refused>()).Reason;
    }

    private static async Task<IReadOnlyList<MailboxItemInfo>> List(RelayTestClient c, params string[] boxes)
    {
        await c.Send(new MailboxList(boxes));
        return (await c.Receive<MailboxItems>()).Items;
    }

    private static string BoxDir(RelayHarness relay, string box) => Path.Combine(relay.DataDir, "mailbox", box);

    [Fact]
    public async Task PutListGetDelete_RoundTrip()
    {
        await using var relay = await Start();
        var c = await relay.Connect("mailbox");
        var one = await Put(c, BoxA, [1, 2, 3]);
        relay.Time.Advance(TimeSpan.FromMinutes(1));
        var two = await Put(c, BoxA, Blob(1000));
        var three = await Put(c, BoxB, [9]);
        Assert.Equal(3, new[] { one, two, three }.Distinct().Count());

        var all = await List(c, BoxA, BoxB);
        Assert.Equal([one, two, three], all.Select(i => i.ItemId));
        Assert.Equal(new MailboxItemInfo(BoxA, one, 3, relay.Time.GetUtcNow().UtcDateTime.AddMinutes(-1)), all[0]);
        Assert.Equal(1000, all[1].Size);
        Assert.Equal(BoxB, all[2].Box);
        Assert.Equal([one, two], (await List(c, BoxA)).Select(i => i.ItemId));
        Assert.Empty(await List(c, Box("nobody")));
        Assert.Empty(await List(c));

        await c.Send(new MailboxGet(BoxA, one));
        var item = await c.Receive<MailboxItem>();
        Assert.Equal((BoxA, one), (item.Box, item.ItemId));
        Assert.Equal([1, 2, 3], item.Sealed);
        Assert.True(File.Exists(Path.Combine(BoxDir(relay, BoxA), one + ".bin")));

        // An item belongs to its box: the right id under another box is not found.
        Assert.Equal(MailboxLimits.NoSuchItem, await Refused(c, new MailboxGet(BoxB, one)));

        await c.Send(new MailboxDelete(BoxA, one));
        Assert.Equal(one, (await c.Receive<MailboxDeleted>()).ItemId);
        Assert.False(File.Exists(Path.Combine(BoxDir(relay, BoxA), one + ".bin")));
        Assert.Equal(MailboxLimits.NoSuchItem, await Refused(c, new MailboxGet(BoxA, one)));
        Assert.Equal(MailboxLimits.NoSuchItem, await Refused(c, new MailboxDelete(BoxA, one)));
        Assert.Equal([two], (await List(c, BoxA)).Select(i => i.ItemId));

        // Emptying a box removes its folder.
        await c.Send(new MailboxDelete(BoxB, three));
        await c.Receive<MailboxDeleted>();
        Assert.False(Directory.Exists(BoxDir(relay, BoxB)));

        // Host, guest and browser connections may use the mailbox too, also while in a room.
        var host = await relay.Connect("host", "198.51.100.1");
        await CreateRoom(host);
        await Put(host, BoxA, [5]);
        var browser = await relay.Connect("browser", "198.51.100.2");
        Assert.Equal(2, (await List(browser, BoxA)).Count);
        await host.AssertSilent();

        // Box ids never reach the log.
        Assert.DoesNotContain(BoxA, relay.Logs.All);
        Assert.DoesNotContain(BoxB, relay.Logs.All);
    }

    [Fact]
    public async Task Expires_After30Days()
    {
        await using var relay = await Start();
        var c = await relay.Connect("mailbox");
        var id = await Put(c, BoxA, [1, 2]);
        var file = Path.Combine(BoxDir(relay, BoxA), id + ".bin");

        relay.Time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1));
        Assert.Single(await List(c, BoxA));
        await c.Send(new MailboxGet(BoxA, id));
        await c.Receive<MailboxItem>();
        Assert.True(File.Exists(file));

        // Expired items are gone at once; the hourly sweep deletes their files.
        relay.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(await List(c, BoxA));
        Assert.Equal(MailboxLimits.NoSuchItem, await Refused(c, new MailboxGet(BoxA, id)));
        relay.Time.Advance(TimeSpan.FromHours(1));
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(BoxDir(relay, BoxA)));
    }

    [Fact]
    public async Task Expiry_Configurable()
    {
        await using var relay = await Start(new Dictionary<string, string?> { ["Relay:MailboxExpiryDays"] = "2" });
        var c = await relay.Connect("mailbox");
        await Put(c, BoxA, [1]);
        relay.Time.Advance(TimeSpan.FromDays(2));
        Assert.Empty(await List(c, BoxA));
    }

    [Fact]
    public async Task BoxCap_50_Refused()
    {
        await using var relay = await Start();
        var a = await relay.Connect("mailbox", "203.0.113.1");
        var b = await relay.Connect("mailbox", "203.0.113.2");
        for (int i = 0; i < MailboxLimits.MaxItemsPerBox; i++) await Put(i % 2 == 0 ? a : b, BoxA, [(byte)i]);
        Assert.Equal(MailboxLimits.Full, await Refused(b, new MailboxPut(BoxA, [1])));
        Assert.Equal(MailboxLimits.MaxItemsPerBox, (await List(a, BoxA)).Count);
        await Put(b, BoxB, [1]); // other boxes are not affected

        // Taking one out makes room again.
        var first = (await List(a, BoxA))[0].ItemId;
        await a.Send(new MailboxDelete(BoxA, first));
        await a.Receive<MailboxDeleted>();
        await Put(b, BoxA, [1]);
    }

    [Fact]
    public async Task TotalBytes_Cap_Refused_MailboxFull()
    {
        await using var relay = await Start(new Dictionary<string, string?> { ["Relay:MailboxMaxBytes"] = "1000" });
        var c = await relay.Connect("mailbox");
        await Put(c, BoxA, Blob(600));
        Assert.Equal(MailboxLimits.Full, await Refused(c, new MailboxPut(BoxB, Blob(401))));
        await Put(c, BoxB, Blob(400)); // exactly at the cap

        // Expired items make room first.
        relay.Time.Advance(TimeSpan.FromDays(30));
        await Put(c, BoxB, Blob(600));
        Assert.Equal(MailboxLimits.Full, await Refused(c, new MailboxPut(BoxA, Blob(401))));
    }

    [Fact]
    public async Task UploadRate_30PerHourPerAddress()
    {
        await using var relay = await Start();
        var c = await relay.Connect("mailbox", "2001:db8:1:2::1");
        for (int i = 0; i < MailboxLimits.UploadsPerHour; i++) await Put(c, i < 25 ? BoxA : BoxB, [1]);
        Assert.Equal(MailboxLimits.TooManyUploads, await Refused(c, new MailboxPut(BoxB, [1])));

        // Same /64 shares the budget, even on a new connection; another address does not.
        var neighbour = await relay.Connect("mailbox", "2001:db8:1:2::99");
        Assert.Equal(MailboxLimits.TooManyUploads, await Refused(neighbour, new MailboxPut(BoxB, [1])));
        var other = await relay.Connect("mailbox", "2001:db8:1:3::1");
        await Put(other, BoxB, [1]);

        // Reads and deletes are not uploads.
        Assert.Equal(25, (await List(c, BoxA)).Count);

        relay.Time.Advance(TimeSpan.FromHours(1));
        await Put(c, BoxB, [1]);
    }

    [Fact]
    public async Task BadBoxId_Refused()
    {
        await using var relay = await Start();
        var c = await relay.Connect("mailbox");
        foreach (var bad in new[] { "", BoxA.ToUpperInvariant(), BoxA[..63], BoxA + "0", BoxA[..63] + "g", "../" + BoxA[3..] })
        {
            Assert.Equal(MailboxLimits.BadBox, await Refused(c, new MailboxPut(bad, [1])));
            Assert.Equal(MailboxLimits.BadBox, await Refused(c, new MailboxList([bad])));
            Assert.Equal(MailboxLimits.BadBox, await Refused(c, new MailboxGet(bad, new string('0', 32))));
            Assert.Equal(MailboxLimits.BadBox, await Refused(c, new MailboxDelete(bad, new string('0', 32))));
        }
        var many = Enumerable.Range(0, MailboxLimits.MaxBoxesPerList + 1).Select(i => Box(i.ToString())).ToList();
        Assert.Equal(MailboxLimits.BadBox, await Refused(c, new MailboxList(many)));
        Assert.Empty(await List(c, many.Take(MailboxLimits.MaxBoxesPerList).ToArray()));

        // Item ids are the relay's 32 hex; anything else is simply not there.
        foreach (var bad in new[] { "", "../index", new string('0', 31), new string('A', 32) })
            Assert.Equal(MailboxLimits.NoSuchItem, await Refused(c, new MailboxGet(BoxA, bad)));

        // Empty and over-large blobs are refused.
        Assert.Equal(MailboxLimits.BadItem, await Refused(c, new MailboxPut(BoxA, [])));
        Assert.Equal(MailboxLimits.BadItem, await Refused(c, new MailboxPut(BoxA, Blob(MailboxLimits.MaxSealedBytes + 1))));
        Assert.False(Directory.Exists(Path.Combine(relay.DataDir, "mailbox")));
        await c.AssertSilent();
    }

    [Fact]
    public async Task LargestItem_RoundTrips()
    {
        await using var relay = await Start();
        var c = await relay.Connect("mailbox");
        var big = RandomNumberGenerator.GetBytes(MailboxLimits.MaxSealedBytes);
        var id = await Put(c, BoxA, big);
        await c.Send(new MailboxGet(BoxA, id));
        Assert.Equal(big, (await c.Receive<MailboxItem>()).Sealed);
    }

    [Fact]
    public async Task Survives_Restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-relay-tests", Guid.NewGuid().ToString("N"));
        string keep, gone;
        DateTime stored;
        await using (var relay = await Start(dataDir: dir))
        {
            relay.KeepData = true;
            var c = await relay.Connect("mailbox");
            keep = await Put(c, BoxA, [4, 5, 6]);
            gone = await Put(c, BoxA, [7]);
            await Put(c, BoxB, [8]);
            await c.Send(new MailboxDelete(BoxA, gone));
            await c.Receive<MailboxDeleted>();
            stored = (await List(c, BoxA))[0].StoredUtc;
        }
        // Leftovers of an interrupted write: a temp file and a file the index does not know.
        var boxA = Path.Combine(dir, "mailbox", BoxA);
        File.WriteAllBytes(Path.Combine(boxA, new string('1', 32) + ".bin.tmp"), [1]);
        File.WriteAllBytes(Path.Combine(boxA, new string('2', 32) + ".bin"), [1]);
        // A box whose index is lost keeps its files.
        File.Delete(Path.Combine(dir, "mailbox", BoxB, "index.json"));
        foreach (var f in Directory.GetFiles(Path.Combine(dir, "mailbox", BoxB))) File.SetLastWriteTimeUtc(f, stored);

        try
        {
            await using var relay = await Start(dataDir: dir, start: new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
            var c = await relay.Connect("mailbox");
            var items = await List(c, BoxA);
            Assert.Equal([new MailboxItemInfo(BoxA, keep, 3, stored)], items);
            await c.Send(new MailboxGet(BoxA, keep));
            Assert.Equal([4, 5, 6], (await c.Receive<MailboxItem>()).Sealed);
            Assert.Equal(new[] { "index.json", keep + ".bin" }.Order(StringComparer.Ordinal), Directory.GetFiles(boxA).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal));
            Assert.Single(await List(c, BoxB));
            Assert.True(File.Exists(Path.Combine(dir, "mailbox", BoxB, "index.json")));

            // The total survives too: the expiry still counts from the original upload.
            relay.Time.Advance(TimeSpan.FromDays(29));
            Assert.Empty(await List(c, BoxA));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task IdleRelay_LoadsAtStart_AndSweeps()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-relay-tests", Guid.NewGuid().ToString("N"));
        await using (var relay = await Start(dataDir: dir))
        {
            relay.KeepData = true;
            var c = await relay.Connect("mailbox");
            await Put(c, BoxA, [1]);
        }
        try
        {
            // No connection at all: the mailbox is loaded at start-up and the hourly sweep still runs.
            await using var relay = await Start(dataDir: dir);
            Assert.Contains("Mailbox ready (1 waiting)", relay.Logs.All);
            relay.Time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromHours(1));
            Assert.False(Directory.Exists(Path.Combine(dir, "mailbox", BoxA)));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [UnixFact]
    public async Task Files_0600_Dirs_0700()
    {
        await using var relay = await Start();
        var c = await relay.Connect("mailbox");
        var id = await Put(c, BoxA, [1]);
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode Dir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode File600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(Dir, File.GetUnixFileMode(Path.Combine(relay.DataDir, "mailbox")));
        Assert.Equal(Dir, File.GetUnixFileMode(BoxDir(relay, BoxA)));
        Assert.Equal(File600, File.GetUnixFileMode(Path.Combine(BoxDir(relay, BoxA), id + ".bin")));
        Assert.Equal(File600, File.GetUnixFileMode(Path.Combine(BoxDir(relay, BoxA), "index.json")));
    }
}
