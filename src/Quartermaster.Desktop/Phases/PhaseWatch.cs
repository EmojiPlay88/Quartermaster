using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Phases;

/// <summary>
/// Calls <c>changed</c> on the UI thread, debounced 150 ms, whenever the game changes, is replaced, the rules pack changes or the player's
/// view changes (WinForms PhaseWatcher). Shared by the tracker strip and the phase panel. Events may arrive from any thread.
/// </summary>
public sealed class PhaseWatch : IDisposable
{
    readonly ModuleSession _s;
    readonly Debouncer _debounce;
    GameSession? _attached;

    public PhaseWatch(ModuleSession s, Action changed)
    {
        _s = s;
        _debounce = new Debouncer(TimeSpan.FromMilliseconds(150), changed, ex => System.Diagnostics.Trace.WriteLine("phase view: " + ex.Message));   // as WinForms PhaseWatcher
        s.GameReplaced += OnReplaced;
        s.RulesChanged += Request;
        s.ViewChanged += Request;   // also a new viewer (hot-seat handover)
        Attach();
    }

    void Attach()
    {
        if (_attached != null) { _attached.Changed -= OnChanged; _attached.Rewound -= Request; }
        _attached = _s.Game;
        if (_attached != null) { _attached.Changed += OnChanged; _attached.Rewound += Request; }
    }

    void OnReplaced() { Attach(); Request(); }
    void OnChanged(GameCommand _) => Request();

    /// <summary>Schedules a refresh.</summary>
    public void Request() => _debounce.Request();

    /// <summary>Refreshes now (UI thread).</summary>
    public void Flush() => _debounce.Flush();

    public void Dispose()
    {
        _s.GameReplaced -= OnReplaced;
        _s.RulesChanged -= Request;
        _s.ViewChanged -= Request;
        if (_attached != null) { _attached.Changed -= OnChanged; _attached.Rewound -= Request; _attached = null; }
        _debounce.Dispose();
    }
}
