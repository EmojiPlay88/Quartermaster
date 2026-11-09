using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Top-level window for a popped-out panel (WinForms LogFloatForm / PhaseFloatForm). Closing it, as the panel's Dock button, returns the
/// panel to the main window (<paramref name="dockBack"/>); the window itself closes only through <see cref="CloseForReal"/> or when its owner goes.
/// </summary>
public sealed class FloatPanelWindow : Window
{
    readonly Action _dockBack;
    bool _closingForReal;

    public FloatPanelWindow(Control content, string title, Action dockBack)
    {
        _dockBack = dockBack;
        Title = title;
        Content = content;
        ShowInTaskbar = false;
        Width = 480;
        Height = 300;
    }

    /// <summary>Closes the window without sending the panel home first (the host already moved it, or is shutting down).</summary>
    public void CloseForReal()
    {
        _closingForReal = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closingForReal && !e.Cancel && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(_dockBack);
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>Outer bounds in screen pixels (what <see cref="PanelDockState"/> and the layout store keep).</summary>
    public RectangleI PixelBounds
    {
        get
        {
            var s = RenderScaling;
            return new RectangleI(Position.X, Position.Y, (int)Math.Round(Width * s), (int)Math.Round(Height * s));
        }
        set
        {
            var s = RenderScaling;
            Position = new PixelPoint(value.X, value.Y);
            Width = Math.Max(1, value.W / s);
            Height = Math.Max(1, value.H / s);
        }
    }
}

/// <summary>
/// Moves one panel between its dock host and a <see cref="FloatPanelWindow"/> as its <see cref="PanelDockState"/> says (log, phases).
/// The state owns what is wanted; this class only reflects it. Floating bounds are restored through <see cref="ScreenFit.Restore"/>.
/// </summary>
public sealed class PanelFloat
{
    readonly PanelDockState _state;
    readonly string _title;
    readonly Decorator _host;
    readonly Func<Window?> _owner;
    readonly Func<IReadOnlyList<ScreenInfo>> _screens;
    Control? _content;
    bool _busy;

    public PanelFloat(PanelDockState state, string title, Decorator host, Func<Window?> owner, Func<IReadOnlyList<ScreenInfo>> screens)
    {
        _state = state;
        _title = title;
        _host = host;
        _owner = owner;
        _screens = screens;
        _state.Changed += Reconcile;
    }

    /// <summary>The window while the panel floats, else null.</summary>
    public FloatPanelWindow? Window { get; private set; }

    /// <summary>The panel sits in the dock host (not floating).</summary>
    public bool Docked => _content != null && Window == null;

    /// <summary>Takes the panel over (dock host or floating, as the state says).</summary>
    public void Attach(Control content)
    {
        _content = content;
        Reconcile();
    }

    /// <summary>Lets the panel go: saves where the window was, closes it and empties the dock host. The state is docked again.</summary>
    public void Detach()
    {
        CaptureBounds();
        _content = null;
        _busy = true;
        try { _state.DockBack(); }
        finally { _busy = false; }
        CloseWindow();
        _host.Child = null;
    }

    /// <summary>Copies the window's current bounds into the state (before saving or docking back).</summary>
    public void CaptureBounds()
    {
        if (Window is not { } w) return;
        _busy = true;
        try { _state.FloatBounds = w.PixelBounds; }
        finally { _busy = false; }
    }

    /// <summary>The panel's Pop out / Dock button.</summary>
    public void Toggle()
    {
        if (_content == null) return;
        if (_state.Floating) _state.DockBack();
        else { _state.Reveal(); _state.PopOut(); }
    }

    /// <summary>Screens now: working area and DPI (scaling x 96) in pixels; one 1920x1080 stand-in when the platform reports none.</summary>
    public static IReadOnlyList<ScreenInfo> ScreensOf(Window? w)
    {
        var all = w?.Screens?.All;
        if (all == null || all.Count == 0) return [new ScreenInfo(new Rectangle(0, 0, 1920, 1080), 96)];
        return all.Select(s => new ScreenInfo(new Rectangle(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height),
            (int)Math.Round(s.Scaling * 96))).ToList();
    }

    /// <summary>The bounds to open the window at: the saved ones moved onto a screen that exists now.</summary>
    internal RectangleI Placement()
    {
        var b = _state.FloatBounds ?? _state.Save()?.Bounds.ToRectangleI() ?? new RectangleI(100, 100, 480, 300);
        var screens = _screens();
        var primary = screens[0];
        var fitted = ScreenFit.Restore(new Rectangle(b.X, b.Y, b.W, b.H), _state.Save()?.Dpi ?? 0, screens, primary);
        return new RectangleI(fitted.X, fitted.Y, fitted.Width, fitted.Height);
    }

    void Reconcile()
    {
        if (_busy) return;
        var want = _content != null && _state.Floating;
        if (want && Window == null) Open();
        else if (!want && Window != null) CloseWindow();
        if (!want && _content != null && !ReferenceEquals(_host.Child, _content)) _host.Child = _content;
    }

    void Open()
    {
        if (_content == null) return;
        var bounds = Placement();
        _busy = true;
        try { _state.FloatBounds = bounds; }
        finally { _busy = false; }
        _host.Child = null;
        var w = new FloatPanelWindow(_content, _title, () => { if (_state.Floating) _state.DockBack(); });
        Window = w;
        var owner = _owner();
        if (owner != null) w.Show(owner); else w.Show();
        w.PixelBounds = bounds;
    }

    void CloseWindow()
    {
        if (Window is not { } w) return;
        CaptureBounds();
        Window = null;
        w.Content = null;
        w.CloseForReal();
    }
}

static class RectangleExtensions
{
    public static RectangleI ToRectangleI(this Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}
