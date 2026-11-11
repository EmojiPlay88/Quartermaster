using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Documents.Tests;

/// <summary>Run-time OCR engine choice (Scope rulings) with fake engines; runs on every OS.</summary>
public class OcrEnginesTests
{
    private sealed class FakeEngine(string name, params string[] languages) : IOcrRecognizer, IDisposable
    {
        public bool Disposed { get; private set; }
        public string Name => name;
        public int MaxImageDimension => 1000;
        public IReadOnlyList<string> Languages => languages;
        public string? DefaultLanguage => languages.FirstOrDefault();
        public string UnavailableMessage => name + " has no language.";
        public Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OcrWord>>([]);
        public void Dispose() => Disposed = true;
    }

    private sealed class Calls
    {
        public int Windows, Tesseract;
        public readonly List<string> Log = [];
    }

    private static IOcrRecognizer Choose(bool isWindows, string? env, IOcrRecognizer? windows, Calls calls) =>
        OcrEngines.Choose(isWindows, env,
            () => { calls.Windows++; return windows; },
            () => { calls.Tesseract++; return new FakeEngine("Tesseract", "eng"); },
            calls.Log.Add);

    [Fact]
    public void Choose_not_windows_is_tesseract()
    {
        var calls = new Calls();
        var e = Choose(false, null, new FakeEngine("Windows", "en-US"), calls);
        Assert.Equal("Tesseract", e.Name);
        Assert.Equal(0, calls.Windows); // the plugin is never touched off Windows
        Assert.Empty(calls.Log);
        // ...even when the variable asks for Windows OCR.
        Assert.Equal("Tesseract", Choose(false, "windows", new FakeEngine("Windows", "en-US"), calls).Name);
        Assert.Equal(0, calls.Windows);
    }

    [Fact]
    public void Choose_windows_with_language_is_windows()
    {
        var calls = new Calls();
        var win = new FakeEngine("Windows", "en-US");
        Assert.Same(win, Choose(true, null, win, calls));
        Assert.Equal(0, calls.Tesseract);
        Assert.Empty(calls.Log);
    }

    [Fact]
    public void Choose_windows_without_language_falls_back_and_disposes()
    {
        var calls = new Calls();
        var win = new FakeEngine("Windows");
        var e = Choose(true, null, win, calls);
        Assert.Equal("Tesseract", e.Name);
        Assert.True(win.Disposed);
        Assert.Single(calls.Log);
        Assert.Contains("Tesseract", calls.Log[0]);
    }

    [Fact]
    public void Choose_plugin_missing_falls_back_and_logs_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-ocr-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new List<string>();
            int tess = 0;
            var e = OcrEngines.Choose(true, null, () => OcrEngines.LoadWindows(dir, log.Add),
                () => { tess++; return new FakeEngine("Tesseract", "eng"); }, log.Add);
            Assert.Equal("Tesseract", e.Name);
            Assert.Equal(1, tess);
            Assert.Single(log);
            Assert.Contains("Tesseract", log[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_windows_from_a_broken_plugin_is_null_with_one_line()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-ocr-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, OcrEngines.PluginFolder));
        try
        {
            File.WriteAllText(Path.Combine(dir, OcrEngines.PluginFolder, "Quartermaster.OcrWindows.dll"), "not an assembly");
            var log = new List<string>();
            Assert.Null(OcrEngines.LoadWindows(dir, log.Add));
            Assert.Single(log);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Choose_env_tesseract_wins()
    {
        var calls = new Calls();
        var e = Choose(true, "tesseract", new FakeEngine("Windows", "en-US"), calls);
        Assert.Equal("Tesseract", e.Name);
        Assert.Equal(0, calls.Windows);
        Assert.Single(calls.Log);
        Assert.Equal("Tesseract", Choose(true, " TESSERACT ", new FakeEngine("Windows", "en-US"), new Calls()).Name);
    }

    [Fact]
    public void Choose_env_windows_forces_windows_without_language()
    {
        var calls = new Calls();
        var win = new FakeEngine("Windows");
        Assert.Same(win, Choose(true, "windows", win, calls));
        Assert.False(win.Disposed);
        Assert.Equal(0, calls.Tesseract);
        // The plugin cannot be loaded at all: Tesseract after all, so the panel still has an engine.
        var calls2 = new Calls();
        Assert.Equal("Tesseract", Choose(true, "windows", null, calls2).Name);
    }

    [Fact]
    public void Choose_unknown_env_value_is_ignored()
    {
        var calls = new Calls();
        var win = new FakeEngine("Windows", "en-US");
        Assert.Same(win, Choose(true, "paper", win, calls));
    }

    // ---- downloaded languages (stage 4c) ----

    private static IOcrRecognizer Choose(bool isWindows, string? env, IOcrRecognizer? windows, Calls calls, OcrLanguageService? downloads) =>
        OcrEngines.Choose(isWindows, env,
            () => { calls.Windows++; return windows; },
            () => { calls.Tesseract++; return new FakeEngine("Tesseract", "eng"); },
            calls.Log.Add, downloads);

    [Fact]
    public void Windows_with_languages_and_downloads_gives_router()
    {
        using var fx = new OcrDownloadsFixture();
        fx.Install();
        using var svc = fx.Service();
        var calls = new Calls();
        var win = new FakeEngine("Windows", "en-US");
        var e = Choose(true, null, win, calls, svc);
        var router = Assert.IsType<OcrRouter>(e);
        Assert.Equal("Windows", router.Name);
        Assert.Equal(["en-US", "deu"], router.Languages);
        Assert.Equal(0, calls.Tesseract);
        Assert.Empty(calls.Log);
        Assert.False(win.Disposed);
        router.Dispose();
        Assert.True(win.Disposed);
        // Without a download service the plain Windows engine stays, as before.
        Assert.Same(win, Choose(true, null, win, new Calls(), null));
    }

    [Fact]
    public void Windows_without_languages_falls_back_to_tesseract_with_downloads()
    {
        using var fx = new OcrDownloadsFixture();
        fx.Install();
        using var svc = fx.Service();
        var calls = new Calls();
        var win = new FakeEngine("Windows");
        var e = Choose(true, null, win, calls, svc);
        Assert.Equal("Tesseract", e.Name);                    // the wholesale fallback, unchanged
        Assert.Equal(1, calls.Tesseract);
        Assert.True(win.Disposed);
        Assert.Single(calls.Log);
        // Create hands the service to its Tesseract: a folder without the plugin falls back (or is Tesseract off Windows).
        var dir = Path.Combine(fx.Root, "app");
        Directory.CreateDirectory(dir);
        using var real = (IDisposable)OcrEngines.Create(dir, null, svc);
        var tess = Assert.IsType<TesseractRecognizer>(real);
        Assert.Equal(["eng", "deu"], tess.Languages);
    }

    [Fact]
    public void Engine_variable_tesseract_and_windows_are_unchanged()
    {
        using var fx = new OcrDownloadsFixture();
        using var svc = fx.Service();
        var calls = new Calls();
        var e = Choose(true, "tesseract", new FakeEngine("Windows", "en-US"), calls, svc);
        Assert.Equal("Tesseract", e.Name);
        Assert.IsType<FakeEngine>(e);
        Assert.Equal(0, calls.Windows);
        var win = new FakeEngine("Windows", "en-US");
        Assert.Same(win, Choose(true, "windows", win, new Calls(), svc));   // Windows only, no router
        var bare = new FakeEngine("Windows");
        Assert.Same(bare, Choose(true, "windows", bare, new Calls(), svc));
        Assert.IsType<FakeEngine>(Choose(false, null, win, new Calls(), svc)); // not Windows: Tesseract
    }

    /// <summary>A plugin type whose constructor fails (as when WinRT is missing on an old Windows).</summary>
    public sealed class ThrowingRecognizer : IOcrRecognizer
    {
        public static int Constructed;
        public ThrowingRecognizer() { Interlocked.Increment(ref Constructed); throw new PlatformNotSupportedException("no WinRT here"); }
        public string Name => "Windows";
        public int MaxImageDimension => 1000;
        public IReadOnlyList<string> Languages => [];
        public string? DefaultLanguage => null;
        public string UnavailableMessage => "";
        public Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OcrWord>>([]);
    }

    private static string FakePluginDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-ocr-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, OcrEngines.PluginFolder));
        File.WriteAllText(Path.Combine(dir, OcrEngines.PluginFolder, "Quartermaster.OcrWindows.dll"), "stand-in");
        return dir;
    }

    [Fact]
    public void Constructor_failure_is_cached_without_loading_the_plugin_again()
    {
        var dir = FakePluginDir();
        try
        {
            int loads = 0;
            var log = new List<string>();
            int before = ThrowingRecognizer.Constructed;
            Type Load(string _) { loads++; return typeof(ThrowingRecognizer); }
            Assert.Null(OcrEngines.LoadWindows(dir, log.Add, Load));
            Assert.Null(OcrEngines.LoadWindows(dir, log.Add, Load));
            Assert.Equal(1, loads);                                      // one load context per process
            Assert.Equal(1, ThrowingRecognizer.Constructed - before);    // the failure is kept, not retried
            Assert.Equal(2, log.Count);                                  // one line per call
            Assert.All(log, l => Assert.Contains("no WinRT here", l));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_failure_is_cached_without_a_second_load_context()
    {
        var dir = FakePluginDir();
        try
        {
            int loads = 0;
            var log = new List<string>();
            Type Load(string _) { loads++; throw new BadImageFormatException("broken plugin"); }
            Assert.Null(OcrEngines.LoadWindows(dir, log.Add, Load));
            Assert.Null(OcrEngines.LoadWindows(dir, log.Add, Load));
            Assert.Equal(1, loads);
            Assert.Equal(2, log.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
