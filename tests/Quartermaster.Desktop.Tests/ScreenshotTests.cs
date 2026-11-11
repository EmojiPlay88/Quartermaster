using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Quartermaster.Desktop.Tests;

/// <summary>Task 8: the --screenshot-main flag renders headless and writes a PNG. In the non-parallel "MacFlag" collection: the states open real
/// (headless) formula windows and set the static <see cref="Quartermaster.Desktop.Shell.ScreenshotStates.Target"/>.</summary>
[Collection("MacFlag")]
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

    [AvaloniaTheory]
    [InlineData("menu")]
    [InlineData("hover")]
    [InlineData("los")]
    [InlineData("log")]
    [InlineData("phases")]
    [InlineData("nextphase")]
    [InlineData("stepback")]
    [InlineData("formula")]
    [InlineData("cheatsheet")]
    [InlineData("batch")]
    [InlineData("alerts")]
    [InlineData("tree")]
    [InlineData("floatlog")]
    [InlineData("floatphases")]
    public void Screenshot_state_flag_writes_png(string state)
    {
        var png = Path.Combine(Path.GetTempPath(), "qm-shot-state-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var code = Program.RunScreenshot(new[] { Demo("Riverland-Demo.vmod"), "--new", "--rules", Demo("Riverland-Demo.qrules"), "--screenshot-state", state, "--screenshot-main", png });
            Assert.Equal(0, code);
            Assert.True(new FileInfo(png).Length > 1000);
        }
        finally { try { File.Delete(png); } catch (IOException) { } }
    }

    [AvaloniaTheory]
    [InlineData("settings")]
    [InlineData("settings-general")]
    [InlineData("settings-game")]
    [InlineData("settings-display")]
    [InlineData("settings-map")]
    [InlineData("settings-dice")]
    [InlineData("settings-toolbar")]
    [InlineData("settings-keyboard")]
    [InlineData("settings-folders")]
    public void Settings_states_render(string state)
    {
        var png = Path.Combine(Path.GetTempPath(), "qm-shot-state-" + Guid.NewGuid().ToString("N") + ".png");
        var layout = Path.Combine(Path.GetTempPath(), "qm-shot-layout-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("QUARTERMASTER_LAYOUT_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("QUARTERMASTER_LAYOUT_ROOT", layout);
            var code = Program.RunScreenshot(new[] { Demo("Riverland-Demo.vmod"), "--new", "--rules", Demo("Riverland-Demo.qrules"), "--screenshot-state", state, "--screenshot-main", png });
            Assert.Equal(0, code);
            Assert.True(new FileInfo(png).Length > 1000);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUARTERMASTER_LAYOUT_ROOT", previous);
            try { File.Delete(png); } catch (IOException) { }
            try { Directory.Delete(layout, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void Apply_starts_without_previous_target()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qm-shot-target-" + Guid.NewGuid().ToString("N"));
        using var fx = Quartermaster.TestFixtures.PhaseFixture.Module();
        var dialogs = new FakeDialogs();
        var w = new MainWindow(dialogs, Path.Combine(tmp, "a"), null) { Width = 1024, Height = 768 };
        var empty = new MainWindow(dialogs, Path.Combine(tmp, "b"), null) { Width = 1024, Height = 768 };
        try
        {
            w.Show();
            empty.Show();
            Assert.True(w.OpenModule(fx.Build()));
            var s = w.Session!;
            w.StartSetup(0, Quartermaster.Core.Difficulty.FullyAuto);
            s.NewRules("t");
            s.UpdateRules(_ => Quartermaster.TestFixtures.PhaseFixture.Pack(s.Archive.Sha256));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Null(Quartermaster.Desktop.Shell.ScreenshotStates.Apply("floatphases", w));
            Assert.NotNull(Quartermaster.Desktop.Shell.ScreenshotStates.Target);

            // A later state (here one that fails at once) never pictures the window an earlier state left behind.
            Assert.NotNull(Quartermaster.Desktop.Shell.ScreenshotStates.Apply("los", empty));
            Assert.Null(Quartermaster.Desktop.Shell.ScreenshotStates.Target);
        }
        finally
        {
            Quartermaster.Desktop.Shell.ScreenshotStates.Cleanup(w);
            w.ForceClose();
            empty.ForceClose();
            try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [AvaloniaFact]
    public void Unknown_screenshot_state_fails()
    {
        var png = Path.Combine(Path.GetTempPath(), "qm-shot-state-" + Guid.NewGuid().ToString("N") + ".png");
        Assert.Equal(1, Program.RunScreenshot(new[] { Demo("Riverland-Demo.vmod"), "--new", "--screenshot-state", "nope", "--screenshot-main", png }));
    }

    [Fact]
    public void Missing_arguments_fail_with_usage_code()
    {
        Assert.Equal(1, Program.RunScreenshot(new[] { "--screenshot-main", "x.png" }));
    }
}
