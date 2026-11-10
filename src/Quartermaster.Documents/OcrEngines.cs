using System.Collections.Concurrent;
using Quartermaster.Render.Ocr;

namespace Quartermaster.Documents;

/// <summary>
/// Picks the OCR engine at run time (port stage 4a, Scope rulings). Not Windows: Tesseract. Windows: Windows OCR, loaded from
/// the ocr-windows/ plugin folder, unless <see cref="EngineVariable"/> is "tesseract", the plugin fails to load, or Windows has
/// no OCR language installed; then Tesseract (bundled English) and one log line. "windows" forces Windows OCR even with no
/// language, so its unavailable message shows.
/// </summary>
public static class OcrEngines
{
    public const string EngineVariable = "QUARTERMASTER_OCR_ENGINE";   // "tesseract" | "windows"
    public const string PluginFolder = "ocr-windows";

    private const string PluginAssembly = "Quartermaster.OcrWindows";
    private const string PluginType = "Quartermaster.OcrWindows.WindowsRecognizer";

    /// <summary>One load context per plugin path for the life of the process (the plugin is never unloaded).</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Type>> Loaded = new(StringComparer.OrdinalIgnoreCase);

    public static IOcrRecognizer Create(string? baseDir = null, Action<string>? log = null)
    {
        var dir = baseDir ?? AppContext.BaseDirectory;
        return Choose(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable(EngineVariable),
            () => LoadWindows(dir, log), () => new TesseractRecognizer(), log);
    }

    internal static IOcrRecognizer Choose(bool isWindows, string? engineVariable, Func<IOcrRecognizer?> loadWindows, Func<IOcrRecognizer> tesseract, Action<string>? log)
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
        return windows;
    }

    internal static IOcrRecognizer? LoadWindows(string baseDir, Action<string>? log)
    {
        var dll = Path.GetFullPath(Path.Combine(baseDir, PluginFolder, PluginAssembly + ".dll"));
        try
        {
            if (!File.Exists(dll))
            {
                log?.Invoke($"OCR: the Windows OCR plugin was not found ({PluginFolder}/{PluginAssembly}.dll); using Tesseract.");
                return null;
            }
            var type = Loaded.GetOrAdd(dll, p => new Lazy<Type>(() => LoadType(p))).Value;
            return (IOcrRecognizer)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            Loaded.TryRemove(dll, out _); // a later Create tries again
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
