using System.Security.Cryptography;
using System.Text.Json;
using Quartermaster.Net;

namespace Quartermaster.Relay;

/// <summary>
/// The turn mailbox (spec §7): encrypted blobs in boxes named by an opaque 64-hex id, under
/// <c>{DataDir}/mailbox/{box}/{itemId}.bin</c> with a small <c>index.json</c> per box. The relay never reads the blobs.
/// Limits: <see cref="MailboxLimits.MaxItemsPerBox"/> items per box; <see cref="RelayOptions.MailboxMaxItems"/> items and
/// <see cref="RelayOptions.MailboxMaxBytes"/> charged bytes in total (each item counts at least
/// <see cref="MinItemCharge"/>, each box <see cref="BoxCharge"/> more, so many tiny items cannot exhaust disk, inodes or memory);
/// per address <see cref="MailboxLimits.UploadsPerHour"/> uploads per hour (refused attempts count too) and
/// <see cref="MailboxLimits.UploadBytesPerDay"/> stored bytes per 24 hours. Items expire after
/// <see cref="RelayOptions.MailboxExpiryDays"/> days (swept hourly). Box ids, contents and per-box sizes are never logged.
/// </summary>
public sealed class Mailbox : IDisposable
{
    public static readonly TimeSpan SweepEvery = TimeSpan.FromHours(1);
    /// <summary>Least an item counts against <see cref="RelayOptions.MailboxMaxBytes"/> (one disk block).</summary>
    public const long MinItemCharge = 4096;
    /// <summary>What a box's folder and index count against <see cref="RelayOptions.MailboxMaxBytes"/>.</summary>
    public const long BoxCharge = 8192;
    private static readonly TimeSpan UploadWindow = TimeSpan.FromHours(1), UploadBytesWindow = TimeSpan.FromHours(24);
    private const string IndexName = "index.json", ItemExt = ".bin", TmpExt = ".tmp";

    internal sealed record Entry(string ItemId, long Size, DateTime StoredUtc);

    private readonly object _lock = new();
    private readonly string _root;
    private readonly long _maxBytes;
    private readonly int _maxItems;
    private readonly TimeSpan _expiry;
    private readonly TimeProvider _time;
    private readonly ILogger<Mailbox> _log;
    private readonly Dictionary<string, List<Entry>> _boxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _uploads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<(DateTimeOffset At, long Bytes)>> _uploadBytes = new(StringComparer.Ordinal);
    private readonly ITimer _sweeper;
    private long _charged;
    private int _items;

    public Mailbox(RelayOptions options, TimeProvider time, ILogger<Mailbox> log)
    {
        _root = Path.Combine(options.DataDir, "mailbox");
        _maxBytes = Math.Max(0, options.MailboxMaxBytes);
        _maxItems = Math.Max(0, options.MailboxMaxItems);
        _expiry = TimeSpan.FromDays(Math.Max(1, options.MailboxExpiryDays));
        _time = time;
        _log = log;
        lock (_lock)
        {
            LoadLocked();
            ExpireLocked();
        }
        _log.LogInformation("Mailbox ready ({Count} waiting).", Count);
        _sweeper = time.CreateTimer(_ => Sweep(), null, SweepEvery, SweepEvery);
    }

    public void Dispose() => _sweeper.Dispose();

    /// <summary>Bytes charged against <see cref="RelayOptions.MailboxMaxBytes"/> (all boxes).</summary>
    public long ChargedBytes { get { lock (_lock) return _charged; } }

    /// <summary>Number of stored items (all boxes).</summary>
    public int Count { get { lock (_lock) return _items; } }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static long Charge(long size) => Math.Max(size, MinItemCharge);

    /// <summary>Answers one mailbox request from a connection in the address bucket (reply or <see cref="Refused"/>).</summary>
    public RelayMessage Handle(string bucket, RelayMessage request)
    {
        lock (_lock)
        {
            return request switch
            {
                MailboxPut m => Put(bucket, m.Box, m.Sealed),
                MailboxList m => List(m.Boxes),
                MailboxGet m => Get(m.Box, m.ItemId),
                MailboxDelete m => Delete(m.Box, m.ItemId),
                _ => new Refused("Not a mailbox request."),
            };
        }
    }

    public static bool IsRequest(RelayMessage m) => m is MailboxPut or MailboxList or MailboxGet or MailboxDelete;

    private RelayMessage Put(string bucket, string box, byte[] sealed_)
    {
        if (!MailboxLimits.IsBoxId(box)) return new Refused(MailboxLimits.BadBox);
        if (sealed_.Length == 0 || sealed_.Length > MailboxLimits.MaxSealedBytes) return new Refused(MailboxLimits.BadItem);
        long size = sealed_.Length;

        // Per address: attempts per hour (refused attempts count too) and stored bytes per 24 h.
        var now = _time.GetUtcNow();
        var recent = Recent(bucket, now);
        var bytes = RecentBytes(bucket, now);
        if (recent.Count >= MailboxLimits.UploadsPerHour || bytes.Sum(b => b.Bytes) + size > MailboxLimits.UploadBytesPerDay)
            return new Refused(MailboxLimits.TooManyUploads);
        recent.Enqueue(now);

        _boxes.TryGetValue(box, out var entries);
        if (entries is not null)
        {
            // This box's own stale items never block it.
            var old = entries.Where(e => Expired(e, Now)).ToList();
            if (old.Count > 0)
            {
                RemoveLocked(box, old);
                _boxes.TryGetValue(box, out entries);
            }
        }
        long cost = Charge(size) + (entries is null ? BoxCharge : 0);
        if (_charged + cost > _maxBytes || _items + 1 > _maxItems)
        {
            ExpireLocked(); // expired items make room first
            _boxes.TryGetValue(box, out entries);
            cost = Charge(size) + (entries is null ? BoxCharge : 0);
        }
        if ((entries?.Count ?? 0) >= MailboxLimits.MaxItemsPerBox || _charged + cost > _maxBytes || _items + 1 > _maxItems)
            return new Refused(MailboxLimits.Full);

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var dir = BoxDir(box);
        var path = ItemPath(box, id);
        var entry = new Entry(id, size, Now);
        var updated = new List<Entry>(entries ?? []) { entry };
        try
        {
            CreateDir(_root);
            CreateDir(dir);
            WriteAtomic(path, sealed_);
            SaveIndex(box, updated);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("A mailbox item could not be stored ({Error}).", e.GetType().Name);
            TryDelete(path);
            if (entries is null) TryDeleteDir(dir);
            return new Refused(MailboxLimits.StoreFailed);
        }
        _boxes[box] = updated;
        _charged += cost;
        _items++;
        bytes.Enqueue((now, size));
        return new MailboxStored(id);
    }

    private Queue<DateTimeOffset> Recent(string bucket, DateTimeOffset now)
    {
        if (!_uploads.TryGetValue(bucket, out var q)) _uploads[bucket] = q = new Queue<DateTimeOffset>();
        while (q.Count > 0 && now - q.Peek() >= UploadWindow) q.Dequeue();
        return q;
    }

    private Queue<(DateTimeOffset At, long Bytes)> RecentBytes(string bucket, DateTimeOffset now)
    {
        if (!_uploadBytes.TryGetValue(bucket, out var q)) _uploadBytes[bucket] = q = new Queue<(DateTimeOffset, long)>();
        while (q.Count > 0 && now - q.Peek().At >= UploadBytesWindow) q.Dequeue();
        return q;
    }

    private RelayMessage List(IReadOnlyList<string> boxes)
    {
        if (boxes.Count > MailboxLimits.MaxBoxesPerList || boxes.Any(b => !MailboxLimits.IsBoxId(b)))
            return new Refused(MailboxLimits.BadBox);
        var now = Now;
        var items = new List<MailboxItemInfo>();
        foreach (var box in boxes.Distinct(StringComparer.Ordinal))
            if (_boxes.TryGetValue(box, out var entries))
                items.AddRange(entries.Where(e => !Expired(e, now)).Select(e => new MailboxItemInfo(box, e.ItemId, e.Size, e.StoredUtc)));
        return new MailboxItems(items.OrderBy(i => i.StoredUtc).ToList());
    }

    private RelayMessage Get(string box, string itemId)
    {
        if (!MailboxLimits.IsBoxId(box)) return new Refused(MailboxLimits.BadBox);
        if (Find(box, itemId) is not { } entry) return new Refused(MailboxLimits.NoSuchItem);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(ItemPath(box, entry.ItemId)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Lost on disk: forget it.
            _log.LogWarning("A mailbox item could not be read ({Error}).", e.GetType().Name);
            RemoveLocked(box, [entry]);
            return new Refused(MailboxLimits.NoSuchItem);
        }
        if (bytes.Length == 0 || bytes.Length > MailboxLimits.MaxSealedBytes) return new Refused(MailboxLimits.ReadFailed);
        return new MailboxItem(box, entry.ItemId, bytes);
    }

    private RelayMessage Delete(string box, string itemId)
    {
        if (!MailboxLimits.IsBoxId(box)) return new Refused(MailboxLimits.BadBox);
        if (Find(box, itemId) is not { } entry) return new Refused(MailboxLimits.NoSuchItem);
        RemoveLocked(box, [entry]);
        return new MailboxDeleted(entry.ItemId);
    }

    private Entry? Find(string box, string itemId)
    {
        if (!MailboxLimits.IsItemId(itemId) || !_boxes.TryGetValue(box, out var entries)) return null;
        var now = Now;
        return entries.FirstOrDefault(e => e.ItemId == itemId && !Expired(e, now));
    }

    private bool Expired(Entry e, DateTime now) => now - e.StoredUtc >= _expiry;

    /// <summary>Removes expired items and forgets old upload counts. Runs hourly; callable from tests.</summary>
    public void Sweep()
    {
        lock (_lock)
        {
            int removed = ExpireLocked();
            var now = _time.GetUtcNow();
            foreach (var bucket in _uploads.Keys.ToList())
                if (Recent(bucket, now).Count == 0) _uploads.Remove(bucket);
            foreach (var bucket in _uploadBytes.Keys.ToList())
                if (RecentBytes(bucket, now).Count == 0) _uploadBytes.Remove(bucket);
            if (removed > 0) _log.LogInformation("Mailbox: {Count} expired items removed.", removed);
        }
    }

    private int ExpireLocked()
    {
        var now = Now;
        int removed = 0;
        foreach (var (box, entries) in _boxes.ToList())
        {
            var old = entries.Where(e => Expired(e, now)).ToList();
            if (old.Count == 0) continue;
            RemoveLocked(box, old);
            removed += old.Count;
        }
        return removed;
    }

    /// <summary>Drops entries from memory and disk; an emptied box loses its folder.</summary>
    private void RemoveLocked(string box, IReadOnlyCollection<Entry> gone)
    {
        if (!_boxes.TryGetValue(box, out var entries)) return;
        var ids = gone.Select(g => g.ItemId).ToHashSet(StringComparer.Ordinal);
        var removed = entries.Where(e => ids.Contains(e.ItemId)).ToList();
        var kept = entries.Where(e => !ids.Contains(e.ItemId)).ToList();
        _charged -= removed.Sum(e => Charge(e.Size));
        _items -= removed.Count;
        if (kept.Count == 0) _charged -= BoxCharge;
        try
        {
            if (kept.Count == 0) _boxes.Remove(box);
            else
            {
                _boxes[box] = kept;
                SaveIndex(box, kept);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("A mailbox index could not be saved ({Error}).", e.GetType().Name);
        }
        foreach (var id in ids) TryDelete(ItemPath(box, id));
        if (kept.Count == 0) TryDeleteDir(BoxDir(box));
    }

    // ---- disk ----

    private string BoxDir(string box) => Path.Combine(_root, box);
    private string ItemPath(string box, string id) => Path.Combine(_root, box, id + ItemExt);

    /// <summary>
    /// Rebuilds the store from disk: index entries need their file; stray, temp, empty and over-large files are removed.
    /// Folders that are not box ids are left alone. A box that cannot be read is skipped (its files stay on disk).
    /// </summary>
    private void LoadLocked()
    {
        string[] dirs;
        try
        {
            if (!Directory.Exists(_root)) return;
            dirs = Directory.GetDirectories(_root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("The mailbox folder could not be read ({Error}); the mailbox starts empty.", e.GetType().Name);
            return;
        }
        int skipped = 0;
        foreach (var dir in dirs)
        {
            var box = Path.GetFileName(dir);
            if (!MailboxLimits.IsBoxId(box)) continue;
            try { LoadBox(box, dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                skipped++;
                _log.LogWarning("A mailbox box could not be read ({Error}); it was skipped.", e.GetType().Name);
            }
        }
        if (skipped > 0) _log.LogWarning("Mailbox: {Count} boxes skipped at start-up.", skipped);
    }

    private void LoadBox(string box, string dir)
    {
        foreach (var tmp in Directory.GetFiles(dir, "*" + TmpExt)) TryDelete(tmp);

        var files = new Dictionary<string, (string Path, long Size)>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(dir, "*" + ItemExt))
        {
            var id = Path.GetFileNameWithoutExtension(f);
            if (!MailboxLimits.IsItemId(id)) continue;
            var size = new FileInfo(f).Length;
            if (size == 0 || size > MailboxLimits.MaxSealedBytes) { TryDelete(f); continue; } // cannot be sent back
            files[id] = (f, size);
        }
        var index = ReadIndex(Path.Combine(dir, IndexName));
        var entries = new List<Entry>();
        if (index is not null)
        {
            foreach (var e in index)
                if (e is not null && MailboxLimits.IsItemId(e.ItemId) && files.TryGetValue(e.ItemId, out var f)
                    && entries.All(x => x.ItemId != e.ItemId))
                    entries.Add(new Entry(e.ItemId, f.Size, DateTime.SpecifyKind(e.StoredUtc, DateTimeKind.Utc)));
        }
        else
        {
            // Lost or damaged index: keep the files, dated by their write time.
            foreach (var (id, f) in files)
                entries.Add(new Entry(id, f.Size, File.GetLastWriteTimeUtc(f.Path)));
        }
        entries = entries.OrderBy(e => e.StoredUtc).TakeLast(MailboxLimits.MaxItemsPerBox).ToList();
        var keep = entries.Select(e => e.ItemId).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, f) in files)
            if (!keep.Contains(id)) TryDelete(f.Path);
        if (entries.Count == 0)
        {
            TryDeleteDir(dir);
            return;
        }
        if (index is null || index.Count != entries.Count) SaveIndex(box, entries);
        _boxes[box] = entries;
        _charged += BoxCharge + entries.Sum(e => Charge(e.Size));
        _items += entries.Count;
    }

    private static List<Entry>? ReadIndex(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllBytes(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private void SaveIndex(string box, List<Entry> entries) =>
        WriteAtomic(Path.Combine(BoxDir(box), IndexName), JsonSerializer.SerializeToUtf8Bytes(entries));

    /// <summary>Writes to a temp file (0600 on Unix) and moves it over the target.</summary>
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + TmpExt;
        TryDelete(tmp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, options))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        File.Move(tmp, path, true);
    }

    private static void CreateDir(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
