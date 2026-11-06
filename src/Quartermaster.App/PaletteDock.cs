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
    bool _shown = true; // the show/hide toggle (View, Counter palette)
    ModuleSession? _session;

    public PaletteDock(Form main, WindowManager wm, Panel host, Splitter splitter, Action<Action> guard)
    {
        _main = main; _wm = wm; Host = host; Splitter = splitter; _guard = guard;
    }

    public Panel Host { get; }
    public Splitter Splitter { get; }
    public PalettePanel? Panel { get; private set; }
    public PaletteFloatForm? Float { get; private set; }

    /// <summary>Whether the palette is wanted on screen (only has an effect in game master mode).</summary>
    public bool Shown => _shown && _gm;

    /// <summary>Remembers the session; the panel (with its scroll timer and thumbnail worker) is built only once game master mode is on.</summary>
    public void Create(ModuleSession session)
    {
        _session = session;
        EnsurePanel();
        Apply();
    }

    void EnsurePanel()
    {
        if (!_gm || _session == null || Panel != null) return;
        Panel = new PalettePanel(_session) { Dock = DockStyle.Fill };
        Panel.PopToggleRequested += () => _guard(() => { if (Float == null) PopOut(); else DockBack(); });
        Host.Controls.Add(Panel);
    }

    /// <summary>Toggles the palette; a floating palette is docked back first and hidden.</summary>
    public void ToggleShown()
    {
        _shown = !_shown;
        if (!_shown && Float != null) DockBack();
        Apply();
    }

    public void Destroy()
    {
        _session = null;
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
        EnsurePanel();
        Apply();
    }

    void Apply() => Host.Visible = Splitter.Visible = Panel != null && Float == null && _gm && _shown;

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
