using System.Globalization;
using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Documents;

/// <summary>
/// Windows OCR per language (stage 4c, Recognizers): a language Windows OCR has is read by Windows OCR, any other by Tesseract.
/// The list is Windows' languages, then the downloaded Tesseract languages (never the bundled English, which stays hidden while
/// Windows OCR is in use). Owns both engines.
/// </summary>
public sealed class OcrRouter : IOcrRecognizer, IOcrLanguageList, IDisposable
{
    private readonly IOcrRecognizer _windows;
    private readonly TesseractRecognizer _tesseract;
    private readonly string[] _windowsLanguages;
    private readonly object _refresh = new();
    private volatile Listing _listing;
    private int _disposed;

    private sealed record Listing(string[] Languages, Dictionary<string, string> Names);

    public OcrRouter(IOcrRecognizer windows, TesseractRecognizer tesseract)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _tesseract = tesseract ?? throw new ArgumentNullException(nameof(tesseract));
        _windowsLanguages = [.. windows.Languages];
        _listing = Compute();
        _tesseract.LanguagesChanged += OnTesseractChanged;
    }

    public string Name => "Windows";
    public int MaxImageDimension => Math.Min(_windows.MaxImageDimension, _tesseract.MaxImageDimension);
    public IReadOnlyList<string> Languages => _listing.Languages;
    public string? DefaultLanguage => _windows.DefaultLanguage;
    public string UnavailableMessage => _windows.UnavailableMessage;

    /// <summary>Raised, on the download service's thread, after a downloaded language came or went.</summary>
    public event Action? LanguagesChanged;

    /// <summary>Windows: the culture's English name; Tesseract: the catalog name. " (Windows)" / " (Tesseract)" when two listed names clash.</summary>
    public string DisplayName(string language) =>
        _listing.Names.TryGetValue(language ?? "", out var name) ? name : BaseName(language ?? "");

    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct) =>
        IsWindows(language)
            ? _windows.RecognizeAsync(gray, layout, language, ct)
            : _tesseract.RecognizeAsync(gray, layout, language, ct);

    private bool IsWindows(string? language) => _windowsLanguages.Contains(language ?? "", StringComparer.OrdinalIgnoreCase);

    private string BaseName(string language) => IsWindows(language) ? WindowsName(language) : _tesseract.DisplayName(language);

    private static string WindowsName(string tag)
    {
        try { return CultureInfo.GetCultureInfo(tag, predefinedOnly: true).EnglishName; }
        catch (ArgumentException) { return tag; } // CultureNotFoundException: not a culture this OS knows
    }

    private Listing Compute()
    {
        var langs = _windowsLanguages
            .Concat(_tesseract.DownloadedLanguages.Where(l => !IsWindows(l)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var bases = langs.ToDictionary(l => l, BaseName, StringComparer.OrdinalIgnoreCase);
        var clashing = bases.Values.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (lang, name) in bases)
            names[lang] = clashing.Contains(name) ? name + (IsWindows(lang) ? " (Windows)" : " (Tesseract)") : name;
        return new Listing(langs, names);
    }

    private void OnTesseractChanged()
    {
        lock (_refresh) _listing = Compute();
        LanguagesChanged?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _tesseract.LanguagesChanged -= OnTesseractChanged;
        _tesseract.Dispose();
        (_windows as IDisposable)?.Dispose();
    }
}
