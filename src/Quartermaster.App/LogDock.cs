using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Owns where the log panel lives: the collapsible bottom dock of the main window, or a floating <see cref="LogFloatForm"/> registered with the
/// <see cref="WindowManager"/> under the layout key "log". The panel instance moves between the two.
/// </summary>
sealed class LogDock
{
    readonly Form _main;
    readonly WindowManager _wm;
    readonly ToolStripMenuItem _menu;
    readonly Action<string> _centre;
    readonly Action<Action> _guard;
    readonly PanelDockState _state;

    public LogDock(Form main, WindowManager wm, Panel host, Splitter splitter, ToolStripMenuItem menu, Action<string> centre, Action<Action> guard)
    {
        _state = new PanelDockState("log", () => PanelDockState.LogDefault(new RectangleI(main.Left, main.Top, main.Width, main.Height)));
        _main = main; _wm = wm; Host = host; Splitter = splitter; _menu = menu; _centre = centre; _guard = guard;
    }

    public Panel Host { get; }
    public Splitter Splitter { get; }
    public LogPanel? Panel { get; private set; }
    public LogFloatForm? Float { get; private set; }

    /// <summary>Whether the user wants the bottom dock shown (View, Log).</summary>
    public bool Wanted { get => _state.Wanted; set { _state.Wanted = value; Apply(); } }

    public void Create(ModuleSession session)
    {
        Panel = new LogPanel(session, _centre) { Dock = DockStyle.Fill };
        Panel.PopToggleRequested += () => _guard(() => { if (Float == null) PopOut(); else DockBack(); });
        Host.Controls.Add(Panel);
        Apply();
    }

    public void Destroy()
    {
        var f = Float;
        Float = null;
        _state.DockBack();
        f?.CloseForReal();
        Panel?.Dispose();
        Panel = null;
        Host.Controls.Clear();
        Host.Visible = Splitter.Visible = false;
        _menu.Checked = false;
    }

    public void Apply()
    {
        var docked = Panel != null && Float == null && _state.Wanted;
        Host.Visible = Splitter.Visible = docked;
        _menu.Checked = Panel != null && (Float != null || _state.Wanted);
    }

    /// <summary>View, Log: show or collapse the bottom panel; when it is floating, bring it back to the bottom dock instead.</summary>
    public void Toggle()
    {
        if (Panel == null) return;
        if (Float != null) { _state.Toggle(); DockBack(); return; }
        _state.Toggle();
        Apply();
    }

    public void PopOut()
    {
        if (Panel == null || Float != null) return;
        var f = new LogFloatForm(DockBack);
        Host.Controls.Remove(Panel);
        Panel.Dock = DockStyle.Fill;
        f.Controls.Add(Panel);
        Panel.SetFloating(true);
        Float = f;
        _state.FloatBounds = null; // default is recomputed from the main window each time, as before
        _state.PopOut();
        _wm.Register(f, "log");
        var saved = _wm.Saved("log") is { Bounds.IsEmpty: false } s ? s
            : new SavedWindow(true, new Rectangle(_state.FloatBounds!.Value.X, _state.FloatBounds.Value.Y, _state.FloatBounds.Value.W, _state.FloatBounds.Value.H), false, _main.DeviceDpi);
        _wm.ShowFloating(f, saved);
        Apply();
    }

    public void DockBack()
    {
        if (Float is not { } f) return;
        Float = null;
        _state.DockBack();
        if (Panel != null)
        {
            f.Controls.Remove(Panel);
            Host.Controls.Add(Panel);
            Panel.SetFloating(false);
        }
        f.CloseForReal();
        Apply();
    }
}
