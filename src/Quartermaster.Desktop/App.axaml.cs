using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Quartermaster.Desktop.Launcher;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Per-OS Tahoma fallback chain (the XAML default is plain Tahoma).
        Resources["Classic.FontFamily"] = new Avalonia.Media.FontFamily(Theme.ClassicFonts.Current);
        Resources["Classic.FontSize"] = Theme.ClassicFonts.SizePx;
    }

    /// <summary>True when startup shows the launcher: no module path and no screenshot run (<see cref="StartupDecision"/>); a module argument
    /// opens the game window directly, as before.</summary>
    internal static bool UsesLauncher(StartupArgs args) =>
        args.ScreenshotPath == null && StartupDecision.For(args.ModulePath, smoke: false) == StartupAction.Launcher;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Command line [<vmod>] [--setup <index>|--new]; applied once the window is up, so its dialogs have an owner.
            var args = StartupArgs.Parse(desktop.Args ?? Array.Empty<string>(), m => System.Diagnostics.Trace.WriteLine(m));
            if (UsesLauncher(args)) StartLauncherLoop(desktop);
            else StartDirect(desktop, args);
        }
        base.OnFrameworkInitializationCompleted();
    }

    static void StartDirect(IClassicDesktopStyleApplicationLifetime desktop, StartupArgs args)
    {
        var window = new MainWindow();
        desktop.MainWindow = window;
        if (args.ModulePath != null)
        {
            void Once(object? sender, EventArgs e)
            {
                window.Opened -= Once;
                window.ApplyStartup(args);
            }
            window.Opened += Once;
        }
    }

    /// <summary>
    /// Launcher ↔ game window loop. The launcher keeps its settings and module cache in the app data folder, the same root
    /// <c>new MainWindow()</c> uses (its cache is <see cref="ModuleSession.DefaultCacheRoot"/>), with a fresh store each time it shows,
    /// as WinForms ShowLauncher.
    /// </summary>
    static void StartLauncherLoop(IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // the loop decides when the app ends, not the last window closing
        var loop = new LaunchLoop(
            justPlayed =>
            {
                var root = AppDataRoot.Path;
                var store = new SettingsStore(root);
                store.Load(null);
                return new LauncherWindow(store, root, new Dialogs(), justPlayed);
            },
            () => new MainWindow(),
            w => desktop.MainWindow = w,
            () => desktop.Shutdown());
        // Once the lifetime runs (its MainWindow is set by the loop); RunAsync never throws: failures are traced and end the app.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = loop.RunAsync());
    }
}
