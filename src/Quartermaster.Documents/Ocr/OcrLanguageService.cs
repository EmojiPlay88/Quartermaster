using System.Diagnostics;
using System.Security.Cryptography;
using Quartermaster.Render;

namespace Quartermaster.Documents;

public enum OcrLanguageState { Bundled, Installed, NotInstalled, Damaged, Queued, Downloading }

/// <summary>One catalog language as the UI shows it. <see cref="Progress"/> (0..1) is only meaningful while Downloading; <see cref="Error"/> is the last failure for that code.</summary>
public sealed record OcrLanguageStatus(OcrLanguageEntry Entry, OcrLanguageState State, double Progress, string? Error);

/// <summary>
/// Downloads tessdata_fast language files on request, one at a time, into the user's OCR folder. A file counts only when its length and SHA-256
/// equal the catalog's. Nothing touches the network until <see cref="Enqueue"/> is called. UI-free; events can fire on any thread.
/// </summary>
public sealed class OcrLanguageService : IDisposable
{
    public const string FeedVariable = "QUARTERMASTER_OCR_FEED";

    const int MaxRedirects = 5;
    const string Extension = ".traineddata";
    static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    readonly IReadOnlyList<OcrLanguageEntry> _catalog;
    readonly Dictionary<string, OcrLanguageEntry> _byCode;
    readonly HttpClient _http;
    readonly Action<string>? _log;
    readonly string? _feed;
    readonly object _gate = new();

    readonly List<string> _queue = [];
    readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (long Length, DateTime Written, bool Ok)> _verified = new(StringComparer.OrdinalIgnoreCase);
    string? _current;
    double _progress;
    CancellationTokenSource? _cts;
    bool _workerRunning;
    bool _disposed;
    TaskCompletionSource _idle = Completed();

    public OcrLanguageService(string? folder = null, HttpMessageHandler? handler = null, Action<string>? log = null)
        : this(folder, handler, log, OcrLanguageCatalog.Entries) { }

    internal OcrLanguageService(string? folder, HttpMessageHandler? handler, Action<string>? log, IReadOnlyList<OcrLanguageEntry> catalog)
    {
        Folder = folder ?? AppDataRoot.Sub("OcrLanguages");
        _log = log;
        _catalog = catalog;
        _byCode = catalog.ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);
        var feed = Environment.GetEnvironmentVariable(FeedVariable);
        _feed = string.IsNullOrWhiteSpace(feed) ? null : feed;

        if (handler is null)
        {
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(30) });
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: false);
        }
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Quartermaster/" + AppVersion.Text);

        DeleteStaleParts();
    }

    public string Folder { get; }

    public Func<string, Task>? BeforeRemove { get; set; }

    public event Action? LanguagesChanged;
    public event Action? StatusChanged;

    /// <summary>Completes when the queue is empty and nothing is downloading.</summary>
    public Task Idle { get { lock (_gate) return _idle.Task; } }

    public IReadOnlyList<OcrLanguageStatus> Statuses()
    {
        var result = new List<OcrLanguageStatus>(_catalog.Count);
        lock (_gate)
        {
            foreach (var e in _catalog)
            {
                _errors.TryGetValue(e.Code, out var error);
                if (e.Bundled) { result.Add(new(e, OcrLanguageState.Bundled, 0, error)); continue; }
                if (string.Equals(_current, e.Code, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new(e, OcrLanguageState.Downloading, _progress, error));
                    continue;
                }
                if (_queue.Contains(e.Code, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(new(e, OcrLanguageState.Queued, 0, error));
                    continue;
                }
                var state = FileState(e);
                result.Add(new(e, state, 0, error));
            }
        }
        return result;
    }

    public IReadOnlyList<string> InstalledCodes()
    {
        lock (_gate)
            return _catalog.Where(e => !e.Bundled && FileState(e) == OcrLanguageState.Installed).Select(e => e.Code).ToList();
    }

    public string? VerifiedPath(string code)
    {
        if (!_byCode.TryGetValue(code ?? "", out var e) || e.Bundled) return null;
        lock (_gate) return FileState(e) == OcrLanguageState.Installed ? FinalPath(e) : null;
    }

    public void Enqueue(IEnumerable<string> codes)
    {
        bool start = false;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var code in codes)
            {
                if (!_byCode.TryGetValue(code ?? "", out var e) || e.Bundled) continue;
                if (string.Equals(_current, e.Code, StringComparison.OrdinalIgnoreCase)) continue;
                if (_queue.Contains(e.Code, StringComparer.OrdinalIgnoreCase)) continue;
                if (FileState(e) == OcrLanguageState.Installed) continue;
                _errors.Remove(e.Code);
                _queue.Add(e.Code);
            }
            if (_queue.Count > 0 && !_workerRunning)
            {
                _workerRunning = true;
                _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                start = true;
            }
        }
        StatusChanged?.Invoke();
        if (start) _ = Task.Run(Worker);
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            _queue.Clear();
            _cts?.Cancel();
        }
        StatusChanged?.Invoke();
    }

    public async Task RemoveAsync(string code)
    {
        if (!_byCode.TryGetValue(code ?? "", out var e) || e.Bundled) return;
        var before = BeforeRemove;
        if (before is not null) await before(e.Code).ConfigureAwait(false);

        var path = FinalPath(e);
        bool existed;
        try
        {
            existed = File.Exists(path);
            if (existed) File.Delete(path);
        }
        catch (Exception ex)
        {
            Fail(e.Code, "Could not remove the file: " + ex.Message);
            return;
        }
        lock (_gate) { _verified.Remove(path); _errors.Remove(e.Code); }
        StatusChanged?.Invoke();
        if (existed) LanguagesChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Clear();
            _cts?.Cancel();
        }
        _http.Dispose();
    }

    // ---- worker ----

    async Task Worker()
    {
        while (true)
        {
            OcrLanguageEntry entry;
            CancellationToken ct;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _current = null;
                    _cts = null;
                    _workerRunning = false;
                    _idle.TrySetResult();
                    return;
                }
                entry = _byCode[_queue[0]];
                _queue.RemoveAt(0);
                _current = entry.Code;
                _progress = 0;
                _cts = new CancellationTokenSource();
                ct = _cts.Token;
            }
            StatusChanged?.Invoke();
            try { await Download(entry, ct).ConfigureAwait(false); }
            catch (Exception ex) { Fail(entry.Code, "Unexpected error: " + ex.Message); }
            finally
            {
                lock (_gate) { _current = null; _cts?.Dispose(); _cts = null; }
                StatusChanged?.Invoke();
            }
        }
    }

    async Task Download(OcrLanguageEntry e, CancellationToken ct)
    {
        var final = FinalPath(e);
        var part = final + ".part";
        try
        {
            Directory.CreateDirectory(Folder);
            using (var source = await OpenSource(e, ct).ConfigureAwait(false))
            {
                string hash;
                long total = 0;
                using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    var buf = new byte[81920];
                    var clock = Stopwatch.StartNew();
                    int n;
                    while ((n = await source.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > e.Bytes) throw new InvalidDataException($"The file is larger than expected ({e.Bytes:N0} bytes).");
                        hasher.AppendData(buf, 0, n);
                        await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        if (clock.Elapsed >= ProgressInterval)
                        {
                            clock.Restart();
                            lock (_gate) _progress = (double)total / e.Bytes;
                            StatusChanged?.Invoke();
                        }
                    }
                    hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
                }
                if (total != e.Bytes)
                    throw new InvalidDataException($"The download was incomplete ({total:N0} of {e.Bytes:N0} bytes).");
                if (!string.Equals(hash, e.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded file does not match the expected checksum.");
            }
            File.Move(part, final, overwrite: true);
            lock (_gate) { _verified.Remove(final); _errors.Remove(e.Code); }
        }
        catch (OperationCanceledException)
        {
            DeletePart(part);
            lock (_gate) _errors.Remove(e.Code);
            return;
        }
        catch (Exception ex)
        {
            DeletePart(part);
            Fail(e.Code, Describe(ex));
            return;
        }
        LanguagesChanged?.Invoke();
    }

    async Task<Stream> OpenSource(OcrLanguageEntry e, CancellationToken ct)
    {
        if (_feed is not null)
            return new FileStream(Path.Combine(_feed, e.Code + Extension), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        var uri = new Uri(OcrLanguageCatalog.Url(e.Code));
        for (var hop = 0; ; hop++)
        {
            var response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            try
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } loc)
                {
                    if (hop >= MaxRedirects) throw new HttpRequestException("Too many redirects.");
                    var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                    if (next.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException("Refused a redirect to a non-HTTPS address.");
                    uri = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"The server answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd() + ".");
                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return new OwningStream(stream, response);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    // ---- helpers ----

    string FinalPath(OcrLanguageEntry e) => Path.Combine(Folder, e.Code + Extension);

    /// <summary>Installed / Damaged / NotInstalled from the file on disk. Caller holds the gate. A file is hashed once per (length, last-write).</summary>
    OcrLanguageState FileState(OcrLanguageEntry e)
    {
        var path = FinalPath(e);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return OcrLanguageState.NotInstalled;
            var len = info.Length;
            var written = info.LastWriteTimeUtc;
            if (_verified.TryGetValue(path, out var c) && c.Length == len && c.Written == written)
                return c.Ok ? OcrLanguageState.Installed : OcrLanguageState.Damaged;
            var ok = false;
            if (len == e.Bytes)
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                ok = string.Equals(Convert.ToHexStringLower(SHA256.HashData(fs)), e.Sha256, StringComparison.OrdinalIgnoreCase);
            }
            _verified[path] = (len, written, ok);
            return ok ? OcrLanguageState.Installed : OcrLanguageState.Damaged;
        }
        catch (IOException) { return OcrLanguageState.Damaged; }
        catch (UnauthorizedAccessException) { return OcrLanguageState.Damaged; }
    }

    void Fail(string code, string message)
    {
        lock (_gate) _errors[code] = message;
        try { _log?.Invoke($"OCR language {code}: {message}"); } catch { }
        StatusChanged?.Invoke();
    }

    static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: System.Net.Sockets.SocketException } => "Could not reach the server. Check the network connection.",
        HttpRequestException h => h.Message,
        IOException io => "Could not write the file: " + io.Message,
        UnauthorizedAccessException u => "Could not write to the folder: " + u.Message,
        _ => ex.Message,
    };

    static void DeletePart(string part)
    {
        try { File.Delete(part); } catch { }
    }

    void DeleteStaleParts()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var f in Directory.EnumerateFiles(Folder, "*" + Extension + ".part")) DeletePart(f);
        }
        catch { }
    }

    static TaskCompletionSource Completed()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    /// <summary>Wraps a response stream so disposing it also disposes the response.</summary>
    sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); owner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
