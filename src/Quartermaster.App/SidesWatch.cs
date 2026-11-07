using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Hot-seat: calls <c>refresh</c> once on the UI thread (coalesced) when the session's viewing sides change, so a page that lists units
/// or counts what the player may see never keeps the previous player's units after a handover. A page that is hidden or has no handle
/// yet gets <c>markDirty</c> instead (it rebuilds when shown). Never throws into the session's event.
/// </summary>
sealed class SidesWatch : IDisposable
{
    readonly Control _owner;
    readonly ModuleSession _s;
    readonly Action _refresh;
    readonly Action _markDirty;
    readonly object _gate = new();
    HashSet<string>? _last;
    bool _queued, _disposed;

    public SidesWatch(Control owner, ModuleSession s, Action refresh, Action markDirty)
    {
        _owner = owner; _s = s; _refresh = refresh; _markDirty = markDirty;
        _last = s.ViewSides == null ? null : new HashSet<string>(s.ViewSides);
        s.ViewChanged += OnViewChanged;
    }

    void OnViewChanged()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                var now = _s.ViewSides;
                if (now == null ? _last == null : _last != null && _last.SetEquals(now)) return;
                _last = now == null ? null : new HashSet<string>(now);
                if (_queued) return;
                _queued = true;
            }
            if (_owner.IsDisposed) return;
            if (!_owner.IsHandleCreated) { Done(); _markDirty(); return; }
            _owner.BeginInvoke(() =>
            {
                Done();
                if (_disposed || _owner.IsDisposed) return;
                try
                {
                    if (_owner.Visible) _refresh(); else _markDirty();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("view refresh: " + ex.Message); }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { Done(); }
    }

    void Done()
    {
        lock (_gate) _queued = false;
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _s.ViewChanged -= OnViewChanged;
    }
}
