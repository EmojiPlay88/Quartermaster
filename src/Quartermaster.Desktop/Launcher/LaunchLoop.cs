using Avalonia.Controls;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Launcher;

/// <summary>
/// The app's window loop without a module argument (WinForms Program.Main): launcher → game window → (File → Back to launcher) → launcher
/// again, one window at a time. Closing the launcher, or closing the game window any other way, ends the app.
/// </summary>
public sealed class LaunchLoop
{
    readonly Func<string?, LauncherWindow> _launcher;
    readonly Func<MainWindow> _game;
    readonly Action<Window> _setMainWindow;
    readonly Action _shutdown;

    /// <param name="launcher">A new launcher; the argument is the module just played (selected whatever the setting says), null the first time.</param>
    /// <param name="game">A new game window.</param>
    /// <param name="setMainWindow">Makes the window the app's main window (the desktop lifetime's MainWindow).</param>
    /// <param name="shutdown">Ends the app; called once, when the loop stops.</param>
    public LaunchLoop(Func<string?, LauncherWindow> launcher, Func<MainWindow> game, Action<Window> setMainWindow, Action shutdown)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _setMainWindow = setMainWindow ?? throw new ArgumentNullException(nameof(setMainWindow));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
    }

    /// <summary>Runs the loop until the app should end, then calls the shutdown action (also when something in the loop failed).</summary>
    public async Task RunAsync()
    {
        try
        {
            string? justPlayed = null;
            while (true)
            {
                var launcher = _launcher(justPlayed);
                var launcherClosed = ClosedTask(launcher);
                _setMainWindow(launcher);
                launcher.Show();
                await launcherClosed;   // a choice closes the launcher too: the game window opens only once it is gone
                var request = await launcher.Result;
                if (request == null) return;

                var game = _game();
                game.LauncherAvailable = true;
                game.AutoTour = true;
                var gameClosed = ClosedTask(game);
                _setMainWindow(game);
                game.Show();
                await game.StartFromLaunchAsync(request);
                await gameClosed;
                if (!game.ReturnToLauncher) return;
                justPlayed = game.OpenedModulePath ?? request.ModulePath;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("launch loop: " + ex);
        }
        finally
        {
            _shutdown();
        }
    }

    static Task ClosedTask(Window w)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        w.Closed += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }
}
