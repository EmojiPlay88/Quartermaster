using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Quartermaster.Desktop.Tests;

/// <summary>Task 8: the --screenshot-main flag renders headless and writes a PNG.</summary>
public sealed class ScreenshotTests
{
    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    [AvaloniaFact]
    public void Screenshot_flag_writes_png_headless()
    {
        var png = Path.Combine(Path.GetTempPath(), "qm-shot-test-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var code = Program.RunScreenshot(new[] { Demo("Riverland-Demo.vmod"), "--new", "--screenshot-main", png });
            Assert.Equal(0, code);
            Assert.True(File.Exists(png));
            var bytes = File.ReadAllBytes(png);
            Assert.True(bytes.Length > 1000);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]); // PNG signature
            // The run is headless: the platform is Avalonia.Headless, so no OS window can have been created.
            var probe = new Window();
            try { Assert.Equal("HeadlessWindowImpl", probe.PlatformImpl?.GetType().Name); }
            finally { probe.Close(); }
        }
        finally { try { File.Delete(png); } catch (IOException) { } }
    }

    [Fact]
    public void Missing_arguments_fail_with_usage_code()
    {
        Assert.Equal(1, Program.RunScreenshot(new[] { "--screenshot-main", "x.png" }));
    }
}
