using System.Collections.Concurrent;
using Quartermaster.Render.Ocr;

namespace Quartermaster.Documents;

/// <summary>
/// Picks the OCR engine at run time (port stage 4a, Scope rulings). Not Windows: Tesseract. Windows: Windows OCR, loaded from
/// the ocr-windows/ plugin folder, unless <see cref="EngineVariable"/> is "tesseract", the plugin fails to load, or Windows has
/// no OCR language installed; then Tesseract (bundled English) and one log line. "windows" forces Windows OCR even with no
/// language, so its unavailable message shows. With the app's language download service (stage 4c): Windows OCR with at least one
/// language becomes an <see cref="OcrRouter"/> (Windows languages to Windows OCR, downloaded ones to Tesseract), and every Tesseract
/// fallback also lists the downloads. "windows" still means Windows OCR only.
/// </summary>
public static class OcrEngines
{
    public const string EngineVariable = "QUARTERMASTER_OCR_ENGINE";   // "tesseract" | "windows"
    public const string PluginFolder = "ocr-windows";

    private const string PluginAssembly = "Quartermaster.OcrWindows";
    private const string PluginType = "Quartermaster.OcrWindows.WindowsRecognizer";

    /// <summary>
    /// One entry per plugin path for the life of the process (the plugin is never unloaded and its files cannot change while the
    /// app runs). A failure, of the load or of the recognizer's constructor, is kept too: later calls fail the same way without
    /// a second load context or a second copy of WinRT.Runtime.
    /// </summary>
    private sealed class PluginEntry(string dll, Func<string, Type> loadType)
    {
        public readonly Lazy<Type> Type = new(() => loadType(dll)); // a load failure is cached with it
        public volatile Exception? CreateFailure;
    }

    private static readonly ConcurrentDictionary<string, PluginEntry> Loaded = new(StringComparer.OrdinalIgnoreCase);

    public static IOcrRecognizer Create(string? baseDir = null, Action<string>? log = null, OcrLanguageService? downloads = null)
    {
        var dir = baseDir ?? AppContext.BaseDirectory;
        return Choose(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable(EngineVariable),
            () => LoadWindows(dir, log), () => new TesseractRecognizer(null, downloads), log, downloads);
    }

    /// <param name="downloads">When set, Windows OCR with a language is wrapped in an <see cref="OcrRouter"/> whose Tesseract lists these downloads.</param>
    internal static IOcrRecognizer Choose(bool isWindows, string? engineVariable, Func<IOcrRecognizer?> loadWindows, Func<IOcrRecognizer> tesseract, Action<string>? log,
        OcrLanguageService? downloads = null)
    {
        if (!isWindows) return tesseract();
        var asked = engineVariable?.Trim();
        if (string.Equals(asked, "tesseract", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke($"OCR: using Tesseract ({EngineVariable}=tesseract).");
            return tesseract();
        }
        var windows = loadWindows();
        if (windows is null) return tesseract(); // LoadWindows has logged why
        if (windows.Languages.Count == 0 && !string.Equals(asked, "windows", StringComparison.OrdinalIgnoreCase))
        {
            (windows as IDisposable)?.Dispose();
            log?.Invoke("OCR: Windows has no OCR language installed; using Tesseract.");
            return tesseract();
        }
        if (downloads is not null && windows.Languages.Count > 0 && !string.Equals(asked, "windows", StringComparison.OrdinalIgnoreCase))
            return new OcrRouter(windows, new TesseractRecognizer(null, downloads));
        return windows;
    }

    internal static IOcrRecognizer? LoadWindows(string baseDir, Action<string>? log) => LoadWindows(baseDir, log, LoadType);

    /// <summary>Test seam: <paramref name="loadType"/> stands in for the plugin load context.</summary>
    internal static IOcrRecognizer? LoadWindows(string baseDir, Action<string>? log, Func<string, Type> loadType)
    {
        var dll = Path.GetFullPath(Path.Combine(baseDir, PluginFolder, PluginAssembly + ".dll"));
        try
        {
            if (!File.Exists(dll))
            {
                log?.Invoke($"OCR: the Windows OCR plugin was not found ({PluginFolder}/{PluginAssembly}.dll); using Tesseract.");
                return null;
            }
            var entry = Loaded.GetOrAdd(dll, p => new PluginEntry(p, loadType));
            var type = entry.Type.Value;
            if (entry.CreateFailure is { } failed) throw failed;
            try { return (IOcrRecognizer)Activator.CreateInstance(type)!; }
            catch (Exception ex)
            {
                entry.CreateFailure = ex is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : ex;
                throw;
            }
        }
        catch (Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : ex;
            log?.Invoke($"OCR: the Windows OCR plugin could not be loaded ({inner.GetType().Name}: {inner.Message}); using Tesseract.");
            return null;
        }
    }

    private static Type LoadType(string dll)
    {
        var context = new OcrPluginLoadContext(dll);
        var asm = context.LoadFromAssemblyPath(dll);
        var type = asm.GetType(PluginType, throwOnError: true)!;
        if (!typeof(IOcrRecognizer).IsAssignableFrom(type))
            throw new InvalidCastException($"{PluginType} does not implement this app's IOcrRecognizer.");
        return type;
    }
}
