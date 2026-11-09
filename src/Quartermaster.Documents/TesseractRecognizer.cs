using System.Runtime.InteropServices;
using Quartermaster.Documents.Tesseract;
using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Documents;

/// <summary>Tesseract OCR over the bundled tessdata folder. One engine per language, calls serialized; never downloads.</summary>
public sealed class TesseractRecognizer : IOcrRecognizer, IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, TessApiHandle> _engines = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public TesseractRecognizer(string? tessdataPath = null)
    {
        _path = tessdataPath ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
        Languages = Directory.Exists(_path)
            ? Directory.GetFiles(_path, "*.traineddata").Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        DefaultLanguage = Languages.Contains("eng") ? "eng" : Languages.FirstOrDefault();
    }

    public string Name => "Tesseract";
    public int MaxImageDimension => 10000;
    public IReadOnlyList<string> Languages { get; }
    public string? DefaultLanguage { get; }
    public string UnavailableMessage => "No OCR language data was found. Reinstall Quartermaster to restore English.";

    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gray);
        ct.ThrowIfCancellationRequested();
        if (Languages.Count == 0) throw new OcrUnavailableException(UnavailableMessage);
        if (!Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            throw new OcrUnavailableException(OcrReader.LanguageNotInstalledMessage(language));
        int w = gray.Width, h = gray.Height;
        var pixels = ToGray8(gray); // copied now; the caller's bitmap is not kept
        int psm = layout == OcrLayout.SingleLine ? TessApi.PsmSingleLine : TessApi.PsmSingleBlock;
        return Task.Run<IReadOnlyList<OcrWord>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
                return Run(Engine(language), pixels, w, h, psm);
            }
        }, ct);
    }

    private TessApiHandle Engine(string language)
    {
        if (_engines.TryGetValue(language, out var e)) return e;
        var api = TessApiHandle.Create();
        try
        {
            if (api.IsInvalid) throw new OcrUnavailableException(UnavailableMessage);
            if (TessApi.TessBaseAPIInit3(api.DangerousGetHandle(), _path, language) != 0)
                throw new OcrUnavailableException(OcrReader.LanguageNotInstalledMessage(language));
        }
        catch { api.Dispose(); throw; }
        return _engines[language] = api;
    }

    private static unsafe IReadOnlyList<OcrWord> Run(TessApiHandle api, byte[] pixels, int w, int h, int psm)
    {
        var a = api.DangerousGetHandle();
        TessApi.TessBaseAPISetPageSegMode(a, psm);
        fixed (byte* p = pixels) TessApi.TessBaseAPISetImage(a, p, w, h, 1, w);
        try
        {
            if (TessApi.TessBaseAPIRecognize(a, IntPtr.Zero) != 0) return [];
            var words = Words(a);
            if (psm == TessApi.PsmSingleLine && LooksNumeric(words))
            {
                // A short numeric cell: re-read restricted to number characters (Tesseract reads a 7 as "/" otherwise).
                TessApi.TessBaseAPISetVariable(a, "tessedit_char_whitelist", NumberChars);
                try
                {
                    if (TessApi.TessBaseAPIRecognize(a, IntPtr.Zero) == 0)
                    {
                        var again = Words(a);
                        if (again.Count > 0) return again;
                    }
                }
                finally { TessApi.TessBaseAPISetVariable(a, "tessedit_char_whitelist", ""); }
            }
            return words;
        }
        finally { TessApi.TessBaseAPIClear(a); }
    }

    private const string NumberChars = "0123456789.,-+%:";

    /// <summary>True when the reading has a digit and no letters other than the usual digit look-alikes (I, l, O, o).</summary>
    private static bool LooksNumeric(IReadOnlyList<OcrWord> words)
    {
        bool digit = false;
        foreach (var w in words)
            foreach (char c in w.Text)
            {
                if (char.IsAsciiDigit(c)) digit = true;
                else if (char.IsAsciiLetter(c) && "IlOo".IndexOf(c) < 0) return false;
            }
        return digit;
    }

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
        bool got = Monitor.TryEnter(_gate, TimeSpan.FromSeconds(2));
        try
        {
            if (_disposed || !got) return; // a read still runs after 2 s: give up quietly, the handles' finalizers free the engines
            _disposed = true;
            foreach (var e in _engines.Values) e.Dispose();
            _engines.Clear();
        }
        finally { if (got) Monitor.Exit(_gate); }
    }
}
