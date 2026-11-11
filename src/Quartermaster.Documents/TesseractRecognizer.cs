using System.Runtime.InteropServices;
using Quartermaster.Documents.Tesseract;
using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Documents;

/// <summary>
/// Tesseract OCR over the bundled tessdata folder plus, when given an <see cref="OcrLanguageService"/>, the verified downloaded
/// languages (bundled wins on a clash). The list is recomputed when the service reports a change; engines already loaded stay,
/// and a removed language's engine is disposed at removal (after a running read) or by the next read. One engine per language,
/// calls serialized; never downloads.
/// Table-cell reads (single line) that end or start in a dangling slash or bar after a digit, such as "6/" for 67 (Tesseract
/// misreads a 7 that way), are read once more restricted to number characters, and that reading is used if it is not shorter.
/// Readings with a symbol that has a meaning ("1/2", "(2)", "5*") are never re-read. An interior slash that is a misread 7
/// ("3/7" for 37) cannot be told from a fraction, so it is left alone.
/// </summary>
public sealed class TesseractRecognizer : IOcrRecognizer, IOcrLanguageList, IDisposable
{
    private readonly string _path;
    private readonly string[] _bundled;
    private readonly OcrLanguageService? _downloads;
    private readonly Func<string, Task>? _beforeRemove;
    private readonly object _refresh = new();
    private volatile LanguageSet _set;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TessApiHandle> _engines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<TessApiHandle> _create;
    private volatile bool _disposed;

    /// <summary>How long Dispose waits for a running read before giving up (the read then frees the engines itself).</summary>
    internal TimeSpan DisposeWait { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Test hook: takes the read gate so reads queue behind it; dispose the result to release.</summary>
    internal async Task<IDisposable> HoldGateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        return new GateHold(_gate);
    }

    private sealed class GateHold(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }

    /// <summary>All listed codes (bundled first) and the downloaded ones among them; replaced whole, never changed.</summary>
    private sealed record LanguageSet(string[] All, string[] Downloaded);

    /// <param name="tessdataPath">The bundled folder; null is the app's tessdata/.</param>
    /// <param name="downloads">The app's language download service; null lists the bundled folder only.</param>
    public TesseractRecognizer(string? tessdataPath = null, OcrLanguageService? downloads = null) : this(tessdataPath, TessApiHandle.Create, downloads) { }

    /// <summary>Test seam: <paramref name="create"/> makes the native engine (a failing one simulates a missing library).</summary>
    internal TesseractRecognizer(string? tessdataPath, Func<TessApiHandle> create, OcrLanguageService? downloads = null)
    {
        _create = create;
        _path = tessdataPath ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
        _bundled = Directory.Exists(_path)
            ? Directory.GetFiles(_path, "*.traineddata").Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        _downloads = downloads;
        // Hashes the downloaded files not yet verified in this process on the calling thread (once per file: the service caches it).
        _set = Compute();
        if (downloads is not null)
        {
            downloads.LanguagesChanged += OnDownloadsChanged;
            downloads.BeforeRemove = _beforeRemove = BeforeRemoveAsync;
        }
    }

    public string Name => "Tesseract";
    public int MaxImageDimension => 10000;
    public IReadOnlyList<string> Languages => _set.All;
    public string? DefaultLanguage => _set.All.FirstOrDefault(l => l == "eng") ?? _set.All.FirstOrDefault();

    /// <summary>The listed codes that come from the downloads folder (never the bundled ones).</summary>
    internal IReadOnlyList<string> DownloadedLanguages => _set.Downloaded;

    /// <summary>Raised on the service's thread after a download or removal changed <see cref="Languages"/>.</summary>
    public event Action? LanguagesChanged;

    /// <summary>The catalog's English name, or the code itself.</summary>
    public string DisplayName(string language) => OcrLanguageCatalog.Find(language)?.Name ?? language;

    private bool IsBundled(string code) => _bundled.Contains(code, StringComparer.OrdinalIgnoreCase);

    private LanguageSet Compute()
    {
        var downloaded = _downloads?.InstalledCodes().Where(c => !IsBundled(c)).ToArray() ?? [];
        return new LanguageSet([.. _bundled, .. downloaded], downloaded);
    }

    private void OnDownloadsChanged()
    {
        if (_disposed) return;
        lock (_refresh) _set = Compute(); // serialized: an older, slower refresh cannot overwrite a newer one
        LanguagesChanged?.Invoke();
    }

    /// <summary>Awaited by the service before it deletes <paramref name="code"/>: waits for a running read, then frees that engine.</summary>
    private async Task BeforeRemoveAsync(string code)
    {
        if (IsBundled(code)) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_engines.Remove(code, out var e)) e.Dispose();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Frees the engines of languages no longer listed (the gate is held).</summary>
    private void PruneEngines(string[] listed)
    {
        foreach (var code in _engines.Keys.Where(k => !listed.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _engines[code].Dispose();
            _engines.Remove(code);
        }
    }
    public string UnavailableMessage => "No OCR language data was found. Reinstall Quartermaster to restore English.";

    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gray);
        ct.ThrowIfCancellationRequested();
        var languages = _set.All;
        if (languages.Length == 0) throw new OcrUnavailableException(UnavailableMessage);
        var canonical = languages.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase))
            ?? throw new OcrUnavailableException(OcrReader.LanguageNotInstalledMessage(language));
        language = canonical;
        int w = gray.Width, h = gray.Height;
        var pixels = ToGray8(gray); // copied now; the caller's bitmap is not kept
        int psm = layout == OcrLayout.SingleLine ? TessApi.PsmSingleLine : TessApi.PsmSingleBlock;
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Queued reads wait on the semaphore without holding a pool thread; only the read that owns the gate runs on a worker.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            return await Task.Run<IReadOnlyList<OcrWord>>(() =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this); // Dispose may have returned (timed out) since the gate check
                var listed = _set.All;
                PruneEngines(listed);
                if (!listed.Contains(language, StringComparer.OrdinalIgnoreCase)) // removed while this read waited for the gate
                    throw new OcrUnavailableException(OcrReader.LanguageNotInstalledMessage(language));
                return Run(Engine(language), pixels, w, h, psm);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            if (_disposed) FreeEngines(); // Dispose gave up waiting for this read: free the engines now (the gate is still held)
            _gate.Release();
        }
    }

    internal int EngineCount => _engines.Count;

    private void FreeEngines()
    {
        foreach (var e in _engines.Values) e.Dispose();
        _engines.Clear();
    }

    private unsafe TessApiHandle Engine(string language)
    {
        if (_engines.TryGetValue(language, out var e)) return e;
        TessApiHandle api;
        try { api = _create(); }
        catch (Exception ex) when (IsLoadFailure(ex)) { throw NotStarted(ex); }
        try
        {
            if (api.IsInvalid) throw new OcrUnavailableException(UnavailableMessage);
            var file = IsBundled(language) ? Path.Combine(_path, language + ".traineddata") : _downloads?.VerifiedPath(language);
            if (file is null) throw new OcrUnavailableException(OcrReader.LanguageNotInstalledMessage(language));
            var data = File.ReadAllBytes(file);
            int rc;
            try
            {
                fixed (byte* d = data)
                    rc = TessApi.TessBaseAPIInit5(api.DangerousGetHandle(), d, data.Length, language, 3, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, 0, 0);
            }
            catch (Exception ex) when (IsLoadFailure(ex)) { throw NotStarted(ex); }
            if (rc != 0) throw new OcrUnavailableException(OcrReader.LanguageNotInstalledMessage(language));
        }
        catch { api.Dispose(); throw; }
        return _engines[language] = api;
    }

    private static bool IsLoadFailure(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException;

    private static OcrUnavailableException NotStarted(Exception ex) =>
        new($"OCR could not be started on this computer: {(ex.InnerException ?? ex).Message}");

    private static unsafe IReadOnlyList<OcrWord> Run(TessApiHandle api, byte[] pixels, int w, int h, int psm)
    {
        var a = api.DangerousGetHandle();
        TessApi.TessBaseAPISetPageSegMode(a, psm);
        fixed (byte* p = pixels) TessApi.TessBaseAPISetImage(a, p, w, h, 1, w);
        try
        {
            if (TessApi.TessBaseAPIRecognize(a, IntPtr.Zero) != 0) return [];
            var words = Words(a);
            if (psm == TessApi.PsmSingleLine && HasDanglingSlash(words))
            {
                // A dangling slash ("6/" for 67): re-read restricted to number characters.
                TessApi.TessBaseAPISetVariable(a, "tessedit_char_whitelist", NumberChars);
                try
                {
                    if (TessApi.TessBaseAPIRecognize(a, IntPtr.Zero) == 0)
                    {
                        var again = Words(a);
                        if (again.Count > 0 && Length(again) >= Length(words)) return again;
                    }
                }
                finally { TessApi.TessBaseAPISetVariable(a, "tessedit_char_whitelist", ""); }
            }
            return words;
        }
        finally { TessApi.TessBaseAPIClear(a); }
    }

    private const string NumberChars = "0123456789.,-+%:";
    private static int Length(IReadOnlyList<OcrWord> words) => words.Sum(w => w.Text.Length);

    /// <summary>True when a word has a digit and starts or ends with a slash or bar.</summary>
    private static bool HasDanglingSlash(IReadOnlyList<OcrWord> words) =>
        words.Any(w => w.Text.Length > 1 && w.Text.Any(char.IsAsciiDigit) && ("/|\\".Contains(w.Text[^1]) || "/|\\".Contains(w.Text[0])));

    private static IReadOnlyList<OcrWord> Words(IntPtr a)
    {
        var words = new List<OcrWord>();
        var it = TessApi.TessBaseAPIGetIterator(a);
        if (it == IntPtr.Zero) return words;
        try
        {
            do
            {
                var tp = TessApi.TessResultIteratorGetUTF8Text(it, TessApi.RilWord);
                if (tp == IntPtr.Zero) continue;
                string text;
                try { text = (Marshal.PtrToStringUTF8(tp) ?? "").Trim(); }
                finally { TessApi.TessDeleteText(tp); }
                if (text.Length == 0) continue;
                var page = TessApi.TessResultIteratorGetPageIterator(it); // capi returns the result iterator itself (a view): do not delete it
                if (page == IntPtr.Zero) continue;
                if (TessApi.TessPageIteratorBoundingBox(page, TessApi.RilWord, out int l, out int t, out int r, out int b) != 0)
                    words.Add(new OcrWord(text, l, t, r - l, b - t));
            } while (TessApi.TessResultIteratorNext(it, TessApi.RilWord) != 0);
        }
        finally { TessApi.TessResultIteratorDelete(it); }
        return words;
    }

    private static byte[] ToGray8(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        var res = new byte[w * h];
        SKBitmap? conv = null;
        try
        {
            var bmp = src;
            if (src.ColorType is not (SKColorType.Gray8 or SKColorType.Bgra8888 or SKColorType.Rgba8888))
            {
                conv = src.Copy(SKColorType.Bgra8888);
                bmp = conv ?? throw new InvalidOperationException("Cannot convert the image for OCR.");
            }
            var span = bmp.GetPixelSpan();
            int stride = bmp.RowBytes;
            int bpp = bmp.BytesPerPixel;
            bool bgr = bmp.ColorType == SKColorType.Bgra8888;
            for (int y = 0; y < h; y++)
            {
                int o = y * stride;
                for (int x = 0; x < w; x++, o += bpp)
                {
                    if (bpp == 1) { res[y * w + x] = span[o]; continue; }
                    int rr = bgr ? span[o + 2] : span[o], g = span[o + 1], bb = bgr ? span[o] : span[o + 2], al = span[o + 3];
                    int lum = (rr * 299 + g * 587 + bb * 114) / 1000;
                    if (bmp.AlphaType == SKAlphaType.Premul) lum += 255 - al; // composite on white
                    else lum = (lum * al + 255 * (255 - al)) / 255;
                    res[y * w + x] = (byte)Math.Min(255, lum);
                }
            }
        }
        finally { conv?.Dispose(); }
        return res;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; // before taking the gate: no read starts after Dispose returns, even when waiting for the gate times out
        if (_downloads is not null)
        {
            _downloads.LanguagesChanged -= OnDownloadsChanged;
            if (_downloads.BeforeRemove == _beforeRemove) _downloads.BeforeRemove = null;
        }
        if (!_gate.Wait(DisposeWait)) return; // a read still runs: it frees the engines when it ends
        try { FreeEngines(); }
        finally { _gate.Release(); }
    }
}
