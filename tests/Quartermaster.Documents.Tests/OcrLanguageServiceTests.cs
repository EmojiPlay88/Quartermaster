using System.Net;
using System.Security.Cryptography;

namespace Quartermaster.Documents.Tests;

/// <summary>The OCR language download service against invented small files, a stub handler and temp folders. No real network, no real profile.</summary>
public class OcrLanguageServiceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "qm-ocrlang-" + Guid.NewGuid().ToString("N"));
    readonly byte[] _aaa = Bytes(1000, 1);
    readonly byte[] _bbb = Bytes(2000, 2);
    readonly OcrLanguageEntry[] _catalog;
    string Folder => Path.Combine(_root, "langs");

    public OcrLanguageServiceTests()
    {
        Directory.CreateDirectory(_root);
        _catalog =
        [
            new("eng", "English", 500, new string('0', 64), true),
            new("aaa", "Alpha", _aaa.Length, Hash(_aaa), false),
            new("bbb", "Beta", _bbb.Length, Hash(_bbb), false),
        ];
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    static byte[] Bytes(int n, int seed) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 + seed)).ToArray();
    static string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    OcrLanguageService Make(Stub handler, Action<string>? log = null, string? folder = null) =>
        new(folder ?? Folder, handler, log, _catalog);

    Task<HttpResponseMessage> Serve(HttpRequestMessage req, CancellationToken ct)
    {
        var data = req.RequestUri!.AbsoluteUri.Contains("/aaa.") ? _aaa : _bbb;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
    }

    static void Wait(Task t) => Assert.True(t.Wait(TimeSpan.FromSeconds(20)), "service did not become idle");

    static OcrLanguageStatus Status(OcrLanguageService s, string code) => s.Statuses().Single(x => x.Entry.Code == code);

    [Fact]
    public void Verified_download_is_installed_and_raises_LanguagesChanged()
    {
        using var s = Make(new Stub(Serve));
        var raised = 0;
        s.LanguagesChanged += () => Interlocked.Increment(ref raised);
        s.Enqueue(["aaa"]);
        Wait(s.Idle);
        Assert.Equal(OcrLanguageState.Installed, Status(s, "aaa").State);
        Assert.Equal(["aaa"], s.InstalledCodes());
        Assert.Equal(Path.Combine(Folder, "aaa.traineddata"), s.VerifiedPath("aaa"));
        Assert.Equal(_aaa, File.ReadAllBytes(s.VerifiedPath("aaa")!));
        Assert.Equal(1, raised);
        Assert.Empty(Directory.GetFiles(Folder, "*.part"));
    }

    [Fact]
    public void Truncated_stream_keeps_nothing()
    {
        using var s = Make(new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_aaa[..400]) })));
        s.Enqueue(["aaa"]);
        Wait(s.Idle);
        var st = Status(s, "aaa");
        Assert.Equal(OcrLanguageState.NotInstalled, st.State);
        Assert.False(string.IsNullOrEmpty(st.Error));
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public void Wrong_hash_file_is_damaged()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, "aaa.traineddata"), Bytes(_aaa.Length, 9));
        using var s = Make(new Stub(Serve));
        Assert.Equal(OcrLanguageState.Damaged, Status(s, "aaa").State);
        Assert.DoesNotContain("aaa", s.InstalledCodes());
        Assert.Null(s.VerifiedPath("aaa"));
    }

    [Fact]
    public void Hash_mismatch_download_is_not_kept()
    {
        using var s = Make(new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(_aaa.Length, 9)) })));
        s.Enqueue(["aaa"]);
        Wait(s.Idle);
        Assert.Equal(OcrLanguageState.NotInstalled, Status(s, "aaa").State);
        Assert.Contains("checksum", Status(s, "aaa").Error);
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public void Stale_part_files_are_deleted_on_start()
    {
        Directory.CreateDirectory(Folder);
        var part = Path.Combine(Folder, "aaa.traineddata.part");
        File.WriteAllBytes(part, [1, 2, 3]);
        using var s = Make(new Stub(Serve));
        Assert.False(File.Exists(part));
    }

    [Fact]
    public void Cancel_deletes_part_and_clears_queue()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var s = Make(new Stub((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream(started)) })));
        s.Enqueue(["aaa", "bbb"]);
        Wait(started.Task);
        Assert.Equal(OcrLanguageState.Downloading, Status(s, "aaa").State);
        Assert.Equal(OcrLanguageState.Queued, Status(s, "bbb").State);
        s.CancelAll();
        Wait(s.Idle);
        Assert.Empty(Directory.GetFiles(Folder));
        Assert.All(s.Statuses().Where(x => !x.Entry.Bundled), x => { Assert.Equal(OcrLanguageState.NotInstalled, x.State); Assert.Null(x.Error); });
    }

    [Fact]
    public void Queue_runs_one_at_a_time_in_order()
    {
        var order = new List<string>();
        int active = 0, maxActive = 0;
        using var s = Make(new Stub(async (req, ct) =>
        {
            var now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);
            lock (order) order.Add(req.RequestUri!.Segments[^1]);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref active);
            return await Serve(req, ct);
        }));
        s.Enqueue(["bbb", "aaa"]);
        Wait(s.Idle);
        Assert.Equal(["bbb.traineddata", "aaa.traineddata"], order);
        Assert.Equal(1, maxActive);
        Assert.Equal(2, s.InstalledCodes().Count);
    }

    [Fact]
    public void Http_error_names_status()
    {
        using var s = Make(new Stub((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        s.Enqueue(["aaa"]);
        Wait(s.Idle);
        Assert.Contains("404", Status(s, "aaa").Error);
        Assert.Equal(OcrLanguageState.NotInstalled, Status(s, "aaa").State);
    }

    [Fact]
    public void Non_https_redirect_is_refused()
    {
        var calls = 0;
        using var s = Make(new Stub((_, _) =>
        {
            calls++;
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("http://example.invalid/aaa.traineddata");
            return Task.FromResult(r);
        }));
        s.Enqueue(["aaa"]);
        Wait(s.Idle);
        Assert.Equal(1, calls);
        Assert.False(string.IsNullOrEmpty(Status(s, "aaa").Error));
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public void Connect_timeout_is_an_error_not_a_cancel()
    {
        var logs = new List<string>();
        using var s = Make(new Stub((_, _) => throw new TaskCanceledException("timed out", new TimeoutException())), logs.Add);
        s.Enqueue(["aaa"]);
        Wait(s.Idle);
        var st = Status(s, "aaa");
        Assert.Equal(OcrLanguageState.NotInstalled, st.State);
        Assert.Contains("timed out", st.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(logs);
    }

    [Fact]
    public void Verified_download_is_cached_without_rehash()
    {
        using var s = Make(new Stub(Serve));
        s.Enqueue(["aaa"]);
        Wait(s.Idle);
        // Same length and last-write as the verified file: a cache hit means it is not hashed again.
        var path = Path.Combine(Folder, "aaa.traineddata");
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, Bytes(_aaa.Length, 9));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(OcrLanguageState.Installed, Status(s, "aaa").State);
    }

    [Fact]
    public void Https_redirect_is_followed()
    {
        using var s = Make(new Stub((req, ct) =>
        {
            if (req.RequestUri!.Host == "raw.githubusercontent.com")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://example.invalid/aaa.traineddata");
                return Task.FromResult(r);
            }
            return Serve(req, ct);
        }));
        s.Enqueue(["aaa"]);
        Wait(s.Idle);
        Assert.Equal(OcrLanguageState.Installed, Status(s, "aaa").State);
    }

    [Fact]
    public void Write_failure_reports_and_cleans_up()
    {
        Directory.CreateDirectory(Folder);
        // A directory squatting on the .part name makes the write fail for "aaa" only.
        Directory.CreateDirectory(Path.Combine(Folder, "aaa.traineddata.part"));
        var logs = new List<string>();
        using var s = Make(new Stub(Serve), logs.Add);
        s.Enqueue(["aaa", "bbb"]);
        Wait(s.Idle);
        Assert.False(string.IsNullOrEmpty(Status(s, "aaa").Error));
        Assert.Equal(OcrLanguageState.NotInstalled, Status(s, "aaa").State);
        Assert.Single(logs);
        Assert.Equal(OcrLanguageState.Installed, Status(s, "bbb").State);
        Assert.False(File.Exists(Path.Combine(Folder, "aaa.traineddata")));
    }

    [Fact]
    public void Folder_that_is_a_file_reports_an_error()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "x");
        var logs = new List<string>();
        using var s = Make(new Stub(Serve), logs.Add, blocker);
        s.Enqueue(["aaa"]);
        Wait(s.Idle);
        Assert.False(string.IsNullOrEmpty(Status(s, "aaa").Error));
        Assert.Single(logs);
    }

    [Fact]
    public void Feed_folder_replaces_the_network()
    {
        var feed = Path.Combine(_root, "feed");
        Directory.CreateDirectory(feed);
        File.WriteAllBytes(Path.Combine(feed, "aaa.traineddata"), _aaa);
        var calls = 0;
        var old = Environment.GetEnvironmentVariable(OcrLanguageService.FeedVariable);
        Environment.SetEnvironmentVariable(OcrLanguageService.FeedVariable, feed);
        try
        {
            using var s = Make(new Stub((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
            s.Enqueue(["aaa"]);
            Wait(s.Idle);
            Assert.Equal(0, calls);
            Assert.Equal(OcrLanguageState.Installed, Status(s, "aaa").State);
        }
        finally { Environment.SetEnvironmentVariable(OcrLanguageService.FeedVariable, old); }
    }

    [Fact]
    public void Bundled_eng_cannot_be_removed_or_enqueued()
    {
        var calls = 0;
        using var s = Make(new Stub((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        s.Enqueue(["eng"]);
        Wait(s.Idle);
        Wait(s.RemoveAsync("eng"));
        Assert.Equal(0, calls);
        Assert.Equal(OcrLanguageState.Bundled, Status(s, "eng").State);
        Assert.DoesNotContain("eng", s.InstalledCodes());
    }

    [Fact]
    public void Remove_awaits_BeforeRemove_then_deletes_and_raises()
    {
        using var s = Make(new Stub(Serve));
        s.Enqueue(["aaa"]);
        Wait(s.Idle);
        var gate = new TaskCompletionSource();
        var raised = 0;
        s.LanguagesChanged += () => raised++;
        s.BeforeRemove = _ => gate.Task;
        var remove = s.RemoveAsync("aaa");
        Assert.False(remove.IsCompleted);
        Assert.True(File.Exists(Path.Combine(Folder, "aaa.traineddata")));
        gate.SetResult();
        Wait(remove);
        Assert.False(File.Exists(Path.Combine(Folder, "aaa.traineddata")));
        Assert.Equal(1, raised);
        Assert.Equal(OcrLanguageState.NotInstalled, Status(s, "aaa").State);
    }

    [Fact]
    public void User_agent_is_generic()
    {
        HttpRequestMessage? seen = null;
        using var s = Make(new Stub((req, ct) => { seen = req; return Serve(req, ct); }));
        s.Enqueue(["aaa"]);
        Wait(s.Idle);
        Assert.NotNull(seen);
        Assert.StartsWith("Quartermaster/", seen.Headers.UserAgent.ToString());
        Assert.Equal(["User-Agent"], seen.Headers.Select(h => h.Key).ToArray());
    }

    sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => f(request, ct);
    }

    /// <summary>Yields a few bytes, signals, then blocks until cancelled.</summary>
    sealed class BlockingStream(TaskCompletionSource started) : Stream
    {
        bool _first = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_first) { _first = false; buffer.Span[..10].Clear(); started.TrySetResult(); return 10; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
