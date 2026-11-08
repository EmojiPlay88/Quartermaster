using Avalonia;
using Avalonia.Input;
using Quartermaster.Desktop.Input;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Map;

/// <summary>
/// The map tools of the WinForms MapCanvas (MapCanvas.Los.cs, MapCanvas.Tools.cs): line of sight, hide counters and the last-moved
/// highlight. The line and the highlight are drawn in the view's own recorded picture, so they stay sharp on HiDPI.
/// </summary>
public sealed partial class MapView
{
    LosTool? _los;
    bool _swallowContext;
    bool _hideCounters;
    bool _highlightLast;
    IReadOnlySet<string> _lastMoved = new HashSet<string>();

    /// <summary>Raised when the line-of-sight mode is switched on or off.</summary>
    public event Action? LosChanged;

    /// <summary>Raised when counters are hidden or shown.</summary>
    public event Action<bool>? HideCountersChanged;

    /// <summary>Line-of-sight mode: left-drag measures between two grid locations, Esc or right-click cancels the line.</summary>
    public bool LosEnabled
    {
        get => _los != null;
        set
        {
            if (value == LosEnabled) return;
            _los = value ? new LosTool(Map) : null;
            _swallowContext = false;
            Cursor = RestingCursor;
            if (!value) StatusMessage?.Invoke("");
            InvalidateVisual();
            LosChanged?.Invoke();
        }
    }

    /// <summary>The line's measurement text ("0605 → 0805: 2 hexes"), null without a line.</summary>
    public string? LosStatus => _los?.Status();

    /// <summary>True while a line exists or is being dragged.</summary>
    internal bool HasLosLine => _los is { } l && (l.HasLine || l.Dragging);

    /// <summary>Draws only the board (grid, trails) without counters; clears the selection, the hover and any drag.</summary>
    public bool HideCounters
    {
        get => _hideCounters;
        set
        {
            if (_hideCounters == value) return;
            _hideCounters = value;
            if (value)
            {
                if (_drag.Active) CancelDrag(); else _drag.ClearIncoming();
                if (_selected.Count > 0) { _selected.Clear(); SelectionChanged?.Invoke(this, EventArgs.Empty); }
                _hoverStack = null;
                HidePopup();
                StatusMessage?.Invoke("Counters hidden — press H to show");
            }
            else StatusMessage?.Invoke("Counters shown");
            HideCountersChanged?.Invoke(value);
            InvalidateVisual();
        }
    }

    /// <summary>Outlines the counters of the most recent move on this map.</summary>
    public bool HighlightLastMoved
    {
        get => _highlightLast;
        set
        {
            _highlightLast = value;
            RefreshLastMoved();
        }
    }

    /// <summary>The counters currently outlined as last moved (empty while the highlight is off).</summary>
    internal IReadOnlySet<string> LastMovedIds => _highlightLast ? _lastMoved : new HashSet<string>();

    /// <summary>Recomputes the last-moved set from the command list (after the game changed).</summary>
    public void RefreshLastMoved()
    {
        _lastMoved = _highlightLast ? LastMoved.For(Session.Game, Map.Id) : new HashSet<string>();
        InvalidateVisual();
    }

    /// <summary>The tool.los command.</summary>
    public void ToggleLos() => LosEnabled = !LosEnabled;

    /// <summary>The view.hideCounters command.</summary>
    public void ToggleHideCounters() => HideCounters = !HideCounters;

    Cursor? RestingCursor => _los != null ? new Cursor(StandardCursorType.Cross) : null;

    /// <summary>Left button with the tool on starts a line; right button cancels one. True when the press was consumed.</summary>
    bool LosPressed(PointerPressedEventArgs e, PointerPoint pt)
    {
        if (_los is not { } los) return false;
        // Ctrl+click is a right click on macOS.
        var right = pt.Properties.IsRightButtonPressed
            || (ShortcutGestures.IsMac && pt.Properties.IsLeftButtonPressed && (e.KeyModifiers & KeyModifiers.Control) != 0);
        if (right && (los.HasLine || los.Dragging))
        {
            los.Cancel();
            _swallowContext = true;
            if (e.Pointer.Captured == this) e.Pointer.Capture(null);
            StatusMessage?.Invoke("");
            InvalidateVisual();
            e.Handled = true;
            return true;
        }
        if (right || !pt.Properties.IsLeftButtonPressed) return false;
        los.Begin(MapPoint(pt.Position));
        e.Pointer.Capture(this);
        StatusMessage?.Invoke(los.Status() ?? "");
        InvalidateVisual();
        e.Handled = true;
        return true;
    }

    bool LosMoved(Point p)
    {
        if (_los is not { Dragging: true } los) return false;
        los.Update(MapPoint(p));
        StatusMessage?.Invoke(los.Status() ?? "");
        InvalidateVisual();
        return true;
    }

    bool LosReleased(PointerReleasedEventArgs e)
    {
        if (_swallowContext && (e.InitialPressMouseButton == MouseButton.Right || (ShortcutGestures.IsMac && e.InitialPressMouseButton == MouseButton.Left)))
        {
            _swallowContext = false;
            e.Handled = true;
            return true;
        }
        if (_los is not { Dragging: true } los || e.InitialPressMouseButton != MouseButton.Left) return false;
        los.End();
        if (e.Pointer.Captured == this) e.Pointer.Capture(null);
        e.Handled = true;
        return true;
    }

    bool LosEscape()
    {
        if (_los is not { } los || !(los.HasLine || los.Dragging)) return false;
        los.Cancel();
        StatusMessage?.Invoke("");
        InvalidateVisual();
        return true;
    }

    void DrawLos(SKCanvas c) => _los?.Draw(c, View);
}
