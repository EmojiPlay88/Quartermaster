using Avalonia.Threading;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Runs <paramref name="run"/> on the UI thread once, <paramref name="delay"/> after the last <see cref="Request"/> (WinForms PhaseWatcher's timer).
/// <see cref="Request"/> may be called from any thread. Never throws into the caller; a failing <paramref name="run"/> is reported to <paramref name="onError"/> (if any) and later requests still run.
/// </summary>
public sealed class Debouncer(TimeSpan delay, Action run, Action<Exception>? onError = null) : IDisposable
{
    DispatcherTimer? _timer;
    bool _disposed;

    /// <summary>Schedules (or postpones) the run.</summary>
    public void Request()
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) Restart();
        else Dispatcher.UIThread.Post(Restart);
    }

    void Restart()
    {
        if (_disposed) return;
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = delay };
            _timer.Tick += (_, _) => { _timer.Stop(); Run(); };
        }
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Runs now and cancels a pending run (UI thread).</summary>
    public void Flush()
    {
        _timer?.Stop();
        Run();
    }

    void Run()
    {
        if (_disposed) return;
        try { run(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A refresh must never take the game down; the owner decides where the failure is recorded.
            try { onError?.Invoke(ex); } catch (Exception e2) when (e2 is not OutOfMemoryException) { }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Stop();
        _timer = null;
    }
}
