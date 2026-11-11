using SkiaSharp;

namespace Quartermaster.Render.Ocr;

public enum OcrLayout { Block, SingleLine }

/// <summary>Thrown when the OCR engine has no data for the requested language (none installed, or not that one).</summary>
public sealed class OcrUnavailableException(string message) : Exception(message);

public interface IOcrRecognizer
{
    string Name { get; }                       // "Tesseract", "Windows"
    int MaxImageDimension { get; }             // largest side the engine accepts
    IReadOnlyList<string> Languages { get; }   // tags the engine has data for, e.g. "eng"
    string? DefaultLanguage { get; }
    string UnavailableMessage { get; }         // shown when Languages is empty
    /// <summary>Word boxes in <paramref name="gray"/>'s pixels. Called off the UI thread; the bitmap is not kept.</summary>
    Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct);
}

/// <summary>
/// Optional: an engine whose language list can change while the app runs (downloaded languages), with names for the UI. The
/// Windows OCR plugin does not implement it, so it stays a separate interface.
/// </summary>
public interface IOcrLanguageList
{
    /// <summary>Raised, on any thread, after <see cref="IOcrRecognizer.Languages"/> changed.</summary>
    event Action? LanguagesChanged;
    /// <summary>The name shown for <paramref name="language"/> (the engine tag stays what is passed to RecognizeAsync).</summary>
    string DisplayName(string language);
}
