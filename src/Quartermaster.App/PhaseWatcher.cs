using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Calls <c>changed</c> on the UI thread, debounced (150 ms unless <c>debounceMs</c> says otherwise), whenever the game changes, is replaced, or the rules pack changes.
/// Shared by the tracker strip and the phase panel. Never throws into the game's event.
/// </summary>
sealed class PhaseWatcher : IDisposable
{
    readonly Control _host;
    readonly ModuleSession _s;
    readonly Action _changed;
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };
    GameSession? _attached;
    bool _disposed;

    public PhaseWatcher(Control host, ModuleSession s, Action changed, int debounceMs = 150)
    {
        _host = host; _s = s; _changed = changed;
        _debounce.Interval = debounceMs;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (_disposed || _host.IsDisposed) return;
            try { _changed(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("phase view: " + ex.Message); }
        };
        _s.GameReplaced += OnReplaced;
        _s.RulesChanged += Request;
        Attach();
    }

    void Attach()
    {
        if (_attached != null) _attached.Changed -= OnChanged;
        if (_attached != null) _attached.Rewound -= OnRewound;
        _attached = _s.Game;
        if (_attached != null) _attached.Changed += OnChanged;
        if (_attached != null) _attached.Rewound += OnRewound;
    }

    void OnReplaced() { Attach(); Request(); }
    void OnChanged(Quartermaster.Core.GameCommand _) => Request();
    void OnRewound() => Request();

    /// <summary>Schedules a refresh (events may arrive from any thread).</summary>
    public void Request()
    {
        try
        {
            if (_disposed || _host.IsDisposed) return;
            if (_host.InvokeRequired)
            {
                if (_host.IsHandleCreated) _host.BeginInvoke(() => { if (!_disposed && !_host.IsDisposed) { _debounce.Stop(); _debounce.Start(); } });
            }
            else { _debounce.Stop(); _debounce.Start(); }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* shutting down */ }
    }

    /// <summary>Runs the refresh now (also used by smoke and right after Next Phase).</summary>
    public void Flush()
    {
        _debounce.Stop();
        if (_disposed || _host.IsDisposed) return;
        try { _changed(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("phase view: " + ex.Message); }
    }

    public void Dispose()
    {
        _disposed = true;
        _s.GameReplaced -= OnReplaced;
        _s.RulesChanged -= Request;
        if (_attached != null) _attached.Changed -= OnChanged;
        if (_attached != null) _attached.Rewound -= OnRewound;
        _debounce.Dispose();
    }
}
