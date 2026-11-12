using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Quartermaster.Desktop.Launcher;
using Quartermaster.Desktop.Shell;
using Quartermaster.Desktop.Updates;
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
    /// opens the game window first (still inside the loop, so it has Back to launcher).</summary>
    internal static bool UsesLauncher(StartupArgs args) =>
        args.ScreenshotPath == null && StartupDecision.For(args.ModulePath, smoke: false) == StartupAction.Launcher;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Command line [<vmod>] [--setup <index>|--new]; applied once the window is up, so its dialogs have an owner.
            var args = StartupArgs.Parse(desktop.Args ?? Array.Empty<string>(), m => System.Diagnostics.Trace.WriteLine(m));
            if (UsesLauncher(args)) StartLoop(desktop, null);
            else if (args.ScreenshotPath == null) StartLoop(desktop, args);   // WinForms: LauncherHook ??= ShowLauncher, so a direct start loops too
            else StartPlain(desktop);   // screenshot args (normally taken by Program.RunScreenshot first): no loop, no launcher
        }
        base.OnFrameworkInitializationCompleted();
    }

    static void StartPlain(IClassicDesktopStyleApplicationLifetime desktop) => desktop.MainWindow = new MainWindow();

    /// <summary>
    /// Launcher ↔ game window loop. The launcher keeps its settings and module cache in the app data folder, the same root
    /// <c>new MainWindow()</c> uses (its cache is <see cref="ModuleSession.DefaultCacheRoot"/>), with a fresh store each time it shows,
    /// as WinForms ShowLauncher. With <paramref name="direct"/> (a module on the command line) the game window comes first and gets the
    /// command line once it is open; Back to launcher then shows the launcher with that module selected.
    /// </summary>
    static void StartLoop(IClassicDesktopStyleApplicationLifetime desktop, StartupArgs? direct)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // the loop decides when the app ends, not the last window closing
        // One instance for the run (Offered / Later / checked-this-run carry across launchers); no feed unless the test feed is set.
        var updates = DesktopUpdates.FromEnvironment();
        var loop = new LaunchLoop(
            justPlayed =>
            {
                var root = AppDataRoot.Path;
                var store = new SettingsStore(root);
                store.Load(null);
                return new LauncherWindow(store, root, new Dialogs(), justPlayed, updates);
            },
            () => new MainWindow { Updates = updates },
            w => desktop.MainWindow = w,
            () => desktop.Shutdown());
        // Once the lifetime runs (its MainWindow is set by the loop); RunAsync never throws: failures are traced and end the app.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = direct is { } args ? loop.RunAsync(w => w.ApplyStartup(args)) : loop.RunAsync());
    }
}
