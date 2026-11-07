using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Owns where the outliner lives: a host in the right-hand column of the main window (between the phase panel and the palette) or a
/// floating <see cref="OutlinerFloatForm"/> registered with the <see cref="WindowManager"/> under the layout key "outliner".
/// </summary>
sealed class OutlinerDock
{
    readonly Form _main;
    readonly WindowManager _wm;
    readonly ToolStripMenuItem _menu;
    readonly Action<Action> _guard;
    readonly Action<bool> _wantedChanged;
    bool _wanted;

    public OutlinerDock(Form main, WindowManager wm, Panel host, ToolStripMenuItem menu, bool wanted, Action<bool> wantedChanged, Action<Action> guard)
    {
        _main = main; _wm = wm; Host = host; _menu = menu; _wanted = wanted; _wantedChanged = wantedChanged; _guard = guard;
    }

    public Panel Host { get; }
    public OutlinerPanel? Panel { get; private set; }
    public OutlinerFloatForm? Float { get; private set; }

    public bool Wanted { get => _wanted; set { _wanted = value; Apply(); } }

    public void Create(OutlinerPanel panel)
    {
        Panel = panel;
        Panel.Dock = DockStyle.Fill;
        Panel.PopToggleRequested += () => _guard(() => { if (Float == null) PopOut(); else DockBack(); });
        Host.Controls.Add(Panel);
        Apply();
    }

    public void Destroy()
    {
        var f = Float;
        Float = null;
        f?.CloseForReal();
        Panel?.Dispose();
        Panel = null;
        Host.Controls.Clear();
        Host.Visible = false;
        _menu.Checked = false;
    }

    public void Apply()
    {
        var docked = Panel != null && Float == null && _wanted;
        Host.Visible = docked;
        _menu.Checked = Panel != null && (Float != null || _wanted);
    }

    /// <summary>View, Outliner: show or hide the dock; when floating, bring it back to the dock instead.</summary>
    public void Toggle()
    {
        if (Panel == null) return;
        if (Float != null) { _wanted = true; DockBack(); _wantedChanged(true); return; }
        _wanted = !_wanted;
        Apply();
        _wantedChanged(_wanted);
    }

    public void PopOut()
    {
        if (Panel == null || Float != null) return;
        var f = new OutlinerFloatForm(DockBack);
        Host.Controls.Remove(Panel);
        Panel.Dock = DockStyle.Fill;
        f.Controls.Add(Panel);
        Panel.SetFloating(true);
        Float = f;
        _wm.Register(f, "outliner");
        var saved = _wm.Saved("outliner") is { Bounds.IsEmpty: false } s ? s
            : new SavedWindow(true, new Rectangle(_main.Right - 460, _main.Top + 100, 440, 520), false, _main.DeviceDpi);
        _wm.ShowFloating(f, saved);
        Apply();
    }

    public void DockBack()
    {
        if (Float is not { } f) return;
        Float = null;
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
