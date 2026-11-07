using System.Security.Cryptography;
using System.Text.Json;
using Quartermaster.Net;

namespace Quartermaster.Relay;

/// <summary>
/// The turn mailbox (spec §7): encrypted blobs in boxes named by an opaque 64-hex id, under
/// <c>{DataDir}/mailbox/{box}/{itemId}.bin</c> with a small <c>index.json</c> per box. The relay never reads the blobs.
/// Limits: <see cref="MailboxLimits.MaxItemsPerBox"/> items per box, <see cref="RelayOptions.MailboxMaxBytes"/> in total,
/// <see cref="MailboxLimits.UploadsPerHour"/> uploads per hour per address; items expire after
/// <see cref="RelayOptions.MailboxExpiryDays"/> days (swept hourly). Box ids, contents and per-box sizes are never logged.
/// </summary>
public sealed class Mailbox : IDisposable
{
    public static readonly TimeSpan SweepEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan UploadWindow = TimeSpan.FromHours(1);
    private const string IndexName = "index.json", ItemExt = ".bin", TmpExt = ".tmp";
    private const string StoreFailed = "The mailbox could not store the item.";

    internal sealed record Entry(string ItemId, long Size, DateTime StoredUtc);

    private readonly object _lock = new();
    private readonly string _root;
    private readonly long _maxBytes;
    private readonly TimeSpan _expiry;
    private readonly TimeProvider _time;
    private readonly ILogger<Mailbox> _log;
    private readonly Dictionary<string, List<Entry>> _boxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _uploads = new(StringComparer.Ordinal);
    private readonly ITimer _sweeper;
    private long _totalBytes;

    public Mailbox(RelayOptions options, TimeProvider time, ILogger<Mailbox> log)
    {
        _root = Path.Combine(options.DataDir, "mailbox");
        _maxBytes = Math.Max(0, options.MailboxMaxBytes);
        _expiry = TimeSpan.FromDays(Math.Max(1, options.MailboxExpiryDays));
        _time = time;
        _log = log;
        lock (_lock)
        {
            try { LoadLocked(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("The mailbox folder could not be read ({Error}); starting empty.", e.GetType().Name);
            }
            ExpireLocked();
        }
        _log.LogInformation("Mailbox ready ({Count} waiting).", Count);
        _sweeper = time.CreateTimer(_ => Sweep(), null, SweepEvery, SweepEvery);
    }

    public void Dispose() => _sweeper.Dispose();

    /// <summary>Total stored bytes (all boxes).</summary>
    public long TotalBytes { get { lock (_lock) return _totalBytes; } }

    /// <summary>Number of stored items (all boxes).</summary>
    public int Count { get { lock (_lock) return _boxes.Values.Sum(b => b.Count); } }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

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

        var now = _time.GetUtcNow();
        if (!_uploads.TryGetValue(bucket, out var recent)) _uploads[bucket] = recent = new Queue<DateTimeOffset>();
        while (recent.Count > 0 && now - recent.Peek() >= UploadWindow) recent.Dequeue();
        if (recent.Count >= MailboxLimits.UploadsPerHour) return new Refused(MailboxLimits.TooManyUploads);
        recent.Enqueue(now);

        long size = sealed_.Length;
        if (_totalBytes + size > _maxBytes) ExpireLocked(); // expired items make room first
        _boxes.TryGetValue(box, out var entries);
        if ((entries?.Count ?? 0) >= MailboxLimits.MaxItemsPerBox || _totalBytes + size > _maxBytes)
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
            return new Refused(StoreFailed);
        }
        _boxes[box] = updated;
        _totalBytes += size;
        return new MailboxStored(id);
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
            foreach (var (bucket, q) in _uploads.ToList())
            {
                while (q.Count > 0 && now - q.Peek() >= UploadWindow) q.Dequeue();
                if (q.Count == 0) _uploads.Remove(bucket);
            }
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
        var kept = entries.Where(e => !ids.Contains(e.ItemId)).ToList();
        _totalBytes -= entries.Where(e => ids.Contains(e.ItemId)).Sum(e => e.Size);
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

    /// <summary>Rebuilds the store from disk: index entries need their file; stray files and temp files are removed.</summary>
    private void LoadLocked()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var dir in Directory.GetDirectories(_root))
        {
            var box = Path.GetFileName(dir);
            if (!MailboxLimits.IsBoxId(box)) continue;
            foreach (var tmp in Directory.GetFiles(dir, "*" + TmpExt)) TryDelete(tmp);

            var files = Directory.GetFiles(dir, "*" + ItemExt)
                .Select(f => (Id: Path.GetFileNameWithoutExtension(f), Path: f))
                .Where(f => MailboxLimits.IsItemId(f.Id))
                .ToDictionary(f => f.Id, f => f.Path, StringComparer.Ordinal);
            var index = ReadIndex(Path.Combine(dir, IndexName));
            var entries = new List<Entry>();
            if (index is not null)
            {
                foreach (var e in index)
                    if (e is not null && MailboxLimits.IsItemId(e.ItemId) && files.TryGetValue(e.ItemId, out var f)
                        && entries.All(x => x.ItemId != e.ItemId))
                        entries.Add(new Entry(e.ItemId, new FileInfo(f).Length, DateTime.SpecifyKind(e.StoredUtc, DateTimeKind.Utc)));
            }
            else
            {
                // Lost or damaged index: keep the files, dated by their write time.
                foreach (var (id, f) in files)
                    entries.Add(new Entry(id, new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)));
            }
            entries = entries.OrderBy(e => e.StoredUtc).TakeLast(MailboxLimits.MaxItemsPerBox).ToList();
            var keep = entries.Select(e => e.ItemId).ToHashSet(StringComparer.Ordinal);
            foreach (var (id, f) in files)
                if (!keep.Contains(id)) TryDelete(f);
            if (entries.Count == 0)
            {
                TryDeleteDir(dir);
                continue;
            }
            if (index is null || index.Count != entries.Count) SaveIndex(box, entries);
            _boxes[box] = entries;
            _totalBytes += entries.Sum(e => e.Size);
        }
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
