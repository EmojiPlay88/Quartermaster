using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Vassal;
using Quartermaster.Desktop.Shell;

namespace Quartermaster.Desktop;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--screenshot-main", StringComparer.Ordinal)) return RunScreenshot(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The app builder; also used by the Avalonia designer.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();

    /// <summary>
    /// <c>--screenshot-main &lt;png&gt;</c>: draws the main window headless (Avalonia.Headless with Skia drawing; no real window on any OS)
    /// for <c>&lt;vmod&gt; [--setup N|--new]</c> (default: a new game), writes the PNG and returns 0 (1 on any failure, message on stderr).
    /// Runs inside an already running Avalonia app (tests) or starts its own headless one.
    /// </summary>
    internal static int RunScreenshot(string[] args)
    {
        var warnings = new List<string>();
        var startup = StartupArgs.Parse(args, warnings.Add);
        foreach (var w in warnings) Console.Error.WriteLine(w);
        if (startup.ScreenshotPath is not { } png || startup.ModulePath is not { } module)
        {
            Console.Error.WriteLine("Usage: <vmod> --screenshot-main <png> [--setup N|--new] [--rules <qrules>] [--screenshot-state menu|hover|los|log|rules]");
            return 1;
        }
        if (Application.Current == null)
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

        var cache = Path.Combine(Path.GetTempPath(), "qm-shot-" + Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            window = new MainWindow(new QuietDialogs(), cache, null) { Width = 1280, Height = 800 };
            window.Show();
            if (!window.OpenModule(module)) return 1;
            // No chooser without a window to answer it: the default is a new game from the module.
            if (startup.RulesPath != null) window.Session?.OpenRules(startup.RulesPath);
            else if (startup.NewRulesName != null) window.Session?.NewRules(startup.NewRulesName);
            if (startup.SetupIndex is { } idx) window.StartSetup(idx); else window.StartSetup(null);
            var until = DateTime.UtcNow.AddSeconds(120);
            while (!window.MapTabs.WhenTiled().IsCompleted)
            {
                if (DateTime.UtcNow > until) { Console.Error.WriteLine("Timed out preparing the map."); return 1; }
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Dispatcher.UIThread.RunJobs();
            using (window.CaptureRenderedFrame()) { } // first frame lays out the final size
            Dispatcher.UIThread.RunJobs();
            if (startup.ScreenshotState is { } state && ScreenshotStates.Apply(state, window) is { } problem) { Console.Error.WriteLine(problem); return 1; }
            using var frame = window.CaptureRenderedFrame();
            if (frame == null) { Console.Error.WriteLine("No frame was rendered."); return 1; }
            if (Path.GetDirectoryName(Path.GetFullPath(png)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            frame.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            return File.Exists(png) && new FileInfo(png).Length > 0 ? 0 : 1;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
            or DllNotFoundException or TypeInitializationException or TimeoutException or NotSupportedException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        finally
        {
            window?.ForceClose();
            try { Directory.Delete(cache, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Dialogs for the screenshot run: nothing is ever shown; warnings go to stderr, questions are answered No.</summary>
    sealed class QuietDialogs : IDialogs
    {
        public Task<string?> OpenFileAsync(Avalonia.Controls.Window owner, string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(Avalonia.Controls.Window owner, string title, FileFilter filter, string defaultExtension, string? suggestedFileName = null) => Task.FromResult<string?>(null);
        public Task<bool> AskYesNoAsync(Avalonia.Controls.Window owner, string message, string caption) => Task.FromResult(false);
        public Task<bool?> AskYesNoCancelAsync(Avalonia.Controls.Window owner, string message, string caption) => Task.FromResult<bool?>(null);
        public Task<string?> AskTextAsync(Avalonia.Controls.Window owner, string title, string prompt, string initial) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>?> EditPropertySheetAsync(Avalonia.Controls.Window owner, string title,
            IReadOnlyList<(string Name, string Value)> rows, Func<string, string?>? managedValue) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task ShowWarningAsync(Avalonia.Controls.Window owner, string message, string caption)
        {
            Console.Error.WriteLine(message);
            return Task.CompletedTask;
        }
        public Task<SetupChoice?> ChooseSetupAsync(Avalonia.Controls.Window owner, ModuleModel model, bool preselectNew, Difficulty difficulty) => Task.FromResult<SetupChoice?>(null);
        public Task<DiceChoice?> AskDiceAsync(Avalonia.Controls.Window owner, string title, bool multi) => Task.FromResult<DiceChoice?>(null);
        public Task<TurnChoice?> AskTurnAsync(Avalonia.Controls.Window owner, TurnState current) => Task.FromResult<TurnChoice?>(null);
        public Task<bool> ConfirmOpenDecisionsAsync(Avalonia.Controls.Window owner, IReadOnlyList<Decision> open, Action<Decision>? jumpTo = null) =>
            Task.FromResult(false);   // Go back
        public Task<Quartermaster.Render.MissingInputResult> AskMissingAsync(Avalonia.Controls.Window owner, Quartermaster.Render.ModuleSession s, Quartermaster.Render.IActionPort port, Quartermaster.Core.PendingRun run) => Task.FromResult(Quartermaster.Render.MissingInputResult.Cancel);
    }
}
