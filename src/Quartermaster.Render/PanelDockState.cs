namespace Quartermaster.Render;

public readonly record struct RectangleI(int X, int Y, int W, int H);

/// <summary>
/// Dock/float state of one panel (phases, log): whether the user wants it shown, whether it floats, and where the floating window sits.
/// UI-free; the host (WinForms or Avalonia) reflects it in real windows.
/// </summary>
public sealed class PanelDockState(string key, Func<RectangleI> defaultFloat)
{
    bool _wanted = true, _floating, _maximized;
    RectangleI? _bounds;
    int _dpi;

    public string Key { get; } = key;
    public bool Wanted { get => _wanted; set => Set(ref _wanted, value); }
    public bool Floating => _floating;
    public RectangleI? FloatBounds { get => _bounds; set { if (_bounds != value) { _bounds = value; Changed?.Invoke(); } } }
    public event Action? Changed;

    void Set(ref bool f, bool v) { if (f == v) return; f = v; Changed?.Invoke(); }

    /// <summary>Show or hide; when floating, dock back instead.</summary>
    public void Toggle()
    {
        if (_floating) { _wanted = true; DockBack(); return; }
        Wanted = !_wanted;
    }

    /// <summary>Makes sure the panel is wanted; it stays where it was (docked or floating).</summary>
    public void Reveal() => Wanted = true;

    public void PopOut()
    {
        if (_floating) return;
        _bounds ??= defaultFloat();
        _floating = true;
        Changed?.Invoke();
    }

    public void DockBack() => Set(ref _floating, false);

    public void Load(SavedWindow? saved)
    {
        if (saved is not { } s) return;
        _floating = s.Floating;
        _bounds = s.Bounds.IsEmpty ? null : new RectangleI(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height);
        _maximized = s.Maximized; _dpi = s.Dpi;
        Changed?.Invoke();
    }

    public SavedWindow? Save()
        => _bounds is not { } b ? null : new SavedWindow(_floating, new System.Drawing.Rectangle(b.X, b.Y, b.W, b.H), _maximized, _dpi);

    public static RectangleI PhasesDefault(RectangleI main) => new(main.X + main.W - 340, main.Y + 120, 320, 520);
    public static RectangleI OutlinerDefault(RectangleI main) => new(main.X + main.W - 460, main.Y + 100, 440, 520);
    public static RectangleI LogDefault(RectangleI main) => new(main.X + 40, main.Y + main.H - 320, Math.Max(480, main.W - 80), 260);
}
