using System.Runtime.Loader;
using Quartermaster.Documents;
using Quartermaster.Render.Ocr;

namespace Quartermaster.Desktop.Tests;

/// <summary>Tests that set QUARTERMASTER_OCR_ENGINE run alone.</summary>
[CollectionDefinition(nameof(OcrEngineVariable), DisableParallelization = true)]
public sealed class OcrEngineVariable;

/// <summary>The Windows OCR plugin as the app loads it: from ocr-windows/ beside the app (Windows builds only).</summary>
[Collection(nameof(OcrEngineVariable))]
public class OcrPluginTests
{
    private static void SkipOffWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows OCR plugin exists only in Windows builds.");

    [Fact]
    public void Create_loads_windows_recognizer_from_ocr_windows_folder()
    {
        SkipOffWindows();
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, OcrEngines.PluginFolder, "Quartermaster.OcrWindows.dll")));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, OcrEngines.PluginFolder, "Quartermaster.Render.dll")));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, OcrEngines.PluginFolder, "SkiaSharp.dll")));
        var old = Environment.GetEnvironmentVariable(OcrEngines.EngineVariable);
        Environment.SetEnvironmentVariable(OcrEngines.EngineVariable, "windows"); // also on a Windows without an OCR language
        try
        {
            var log = new List<string>();
            var engine = OcrEngines.Create(log: log.Add);
            try
            {
                Assert.Equal("Windows", engine.Name);
                Assert.Empty(log);
                var type = engine.GetType();
                Assert.Equal("Quartermaster.OcrWindows.WindowsRecognizer", type.FullName);
                Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(type.Assembly));
                // One IOcrRecognizer: the plugin's interface is the default context's Render.
                Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IOcrRecognizer).Assembly));
                Assert.Contains(typeof(IOcrRecognizer), type.GetInterfaces());
                Assert.Same(typeof(SkiaSharp.SKBitmap).Assembly,
                    type.GetMethod(nameof(IOcrRecognizer.RecognizeAsync))!.GetParameters()[0].ParameterType.Assembly);
                // The plugin's private assemblies load in its own context; Render and SkiaSharp never do.
                var names = AssemblyLoadContext.GetLoadContext(type.Assembly)!.Assemblies.Select(a => a.GetName().Name).ToList();
                Assert.Contains("Quartermaster.OcrWindows", names);
                Assert.Contains("WinRT.Runtime", names);
                Assert.Contains("Microsoft.Windows.SDK.NET", names);
                Assert.DoesNotContain("Quartermaster.Render", names);
                Assert.DoesNotContain("SkiaSharp", names);
                // Loaded once per process: a second Create gives the same type.
                var again = OcrEngines.Create(log: log.Add);
                Assert.Same(type, again.GetType());
                (again as IDisposable)?.Dispose();
            }
            finally { (engine as IDisposable)?.Dispose(); }
        }
        finally { Environment.SetEnvironmentVariable(OcrEngines.EngineVariable, old); }
    }

    [Fact]
    public void Create_with_env_tesseract_gives_tesseract()
    {
        SkipOffWindows();
        var old = Environment.GetEnvironmentVariable(OcrEngines.EngineVariable);
        Environment.SetEnvironmentVariable(OcrEngines.EngineVariable, "tesseract");
        try
        {
            var log = new List<string>();
            var engine = OcrEngines.Create(log: log.Add);
            try
            {
                Assert.Equal("Tesseract", engine.Name);
                Assert.IsType<TesseractRecognizer>(engine);
                Assert.Single(log);
            }
            finally { (engine as IDisposable)?.Dispose(); }
        }
        finally { Environment.SetEnvironmentVariable(OcrEngines.EngineVariable, old); }
    }
}
