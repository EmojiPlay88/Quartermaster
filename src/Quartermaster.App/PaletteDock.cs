using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Owns where the counter palette lives: a right-hand dock of the main window (shown only in game master mode) or a floating
/// <see cref="PaletteFloatForm"/> registered with the <see cref="WindowManager"/> under the layout key "palette".
/// </summary>
sealed class PaletteDock
{
    readonly Form _main;
    readonly WindowManager _wm;
    readonly Action<Action> _guard;
    bool _gm;

    public PaletteDock(Form main, WindowManager wm, Panel host, Splitter splitter, Action<Action> guard)
    {
        _main = main; _wm = wm; Host = host; Splitter = splitter; _guard = guard;
    }

    public Panel Host { get; }
    public Splitter Splitter { get; }
    public PalettePanel? Panel { get; private set; }
    public PaletteFloatForm? Float { get; private set; }

    public void Create(ModuleSession session)
    {
        Panel = new PalettePanel(session) { Dock = DockStyle.Fill };
        Panel.PopToggleRequested += () => _guard(() => { if (Float == null) PopOut(); else DockBack(); });
        Host.Controls.Add(Panel);
        Apply();
    }

    public void Destroy()
    {
        var f = Float;
        Float = null;
        f?.CloseForReal();
        Panel?.StopWorker(); // before the session's archive goes away
        Panel?.Dispose();
        Panel = null;
        Host.Controls.Clear();
        Host.Visible = Splitter.Visible = false;
    }

    /// <summary>Game master mode on or off; turning it off docks the palette back and hides it.</summary>
    public void SetGameMaster(bool gm)
    {
        _gm = gm;
        if (!gm && Float != null) DockBack();
        Apply();
    }

    void Apply() => Host.Visible = Splitter.Visible = Panel != null && Float == null && _gm;

    public void PopOut()
    {
        if (Panel == null || Float != null) return;
        var f = new PaletteFloatForm(DockBack);
        Host.Controls.Remove(Panel);
        f.Controls.Add(Panel);
        Panel.SetFloating(true);
        Float = f;
        _wm.Register(f, "palette");
        var saved = _wm.Saved("palette") is { Bounds.IsEmpty: false } s ? s
            : new SavedWindow(true, new Rectangle(_main.Right - 320, _main.Top + 120, 300, 480), false, _main.DeviceDpi);
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
