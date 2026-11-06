using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Owns where the phase panel lives: a right-hand dock of the main window (beside the counter palette's host) or a floating
/// <see cref="PhaseFloatForm"/> registered with the <see cref="WindowManager"/> under the layout key "phases".
/// The dock is only shown while the loaded rules pack has phases.
/// </summary>
sealed class PhaseDock
{
    readonly Form _main;
    readonly WindowManager _wm;
    readonly ToolStripMenuItem _menu;
    readonly Action<Action> _guard;
    readonly Func<bool> _available;
    bool _wanted = true;

    public PhaseDock(Form main, WindowManager wm, Panel host, Splitter splitter, ToolStripMenuItem menu, Func<bool> available, Action<Action> guard)
    {
        _main = main; _wm = wm; Host = host; Splitter = splitter; _menu = menu; _available = available; _guard = guard;
    }

    public Panel Host { get; }
    public Splitter Splitter { get; }
    public PhasePanel? Panel { get; private set; }
    public PhaseFloatForm? Float { get; private set; }

    public void Create(PhasePanel panel)
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
        Host.Visible = Splitter.Visible = false;
        _menu.Checked = false;
    }

    /// <summary>Re-evaluates visibility (also after the rules pack changed).</summary>
    public void Apply()
    {
        var has = Panel != null && _available();
        var docked = has && Float == null && _wanted;
        Host.Visible = Splitter.Visible = docked;
        _menu.Checked = has && (Float != null || _wanted);
    }

    /// <summary>View, Phase panel: show or hide the dock; when floating, bring it back to the dock instead.</summary>
    public void Toggle()
    {
        if (Panel == null) return;
        if (Float != null) { _wanted = true; DockBack(); return; }
        _wanted = !_wanted;
        Apply();
    }

    public void PopOut()
    {
        if (Panel == null || Float != null) return;
        var f = new PhaseFloatForm(DockBack);
        Host.Controls.Remove(Panel);
        Panel.Dock = DockStyle.Fill;
        f.Controls.Add(Panel);
        Panel.SetFloating(true);
        Float = f;
        _wm.Register(f, "phases");
        var saved = _wm.Saved("phases") is { Bounds.IsEmpty: false } s ? s
            : new SavedWindow(true, new Rectangle(_main.Right - 340, _main.Top + 120, 320, 520), false, _main.DeviceDpi);
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
