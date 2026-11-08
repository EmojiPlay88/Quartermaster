using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Map;

/// <summary>
/// Pointer and key input copied from the WinForms MapCanvas: pan (middle button, Space + left), click selection, add-to-selection with
/// Ctrl (Cmd on macOS), rubber band on empty map, dragging the selection with snapping and stacking (<see cref="MapDragController"/>),
/// Esc to cancel a drag. Only pieces the viewer may act on (<see cref="ModuleSession.CanAct"/>) move.
/// </summary>
public sealed partial class MapView
{
    bool _space;
    bool _panning;
    Point _panLast;
    bool _leftDown;
    Point _downPos;
    string? _downPiece;
    bool _downWasSelected;
    bool _bandActive;
    PointD _bandStart, _bandNow;
    IPointer? _dragPointer;

    /// <summary>The selected pieces (click, add-modifier click, rubber band).</summary>
    public IReadOnlySet<string> SelectedPieceIds => _selected;

    /// <summary>Raised whenever <see cref="SelectedPieceIds"/> changed.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>True while pieces are being dragged on this map.</summary>
    public bool IsDragging => _drag.Active;

    /// <summary>Settings "snap on drop" (default on); stacking onto pieces applies either way.</summary>
    public bool Snap { get => _drag.Snap; set => _drag.Snap = value; }

    /// <summary>DIPs the pointer must travel with the button down before a piece drag starts (WinForms default 4).</summary>
    public double DragThreshold { get; set => field = Math.Max(0, value); } = 4;

    /// <summary>Runs a game-changing action inside the owning window's error guard. Null: failures are logged to the session log.</summary>
    public Action<Action>? Guarded { get; set; }

    /// <summary>Supplies the action port that executes drops (one undoable move gesture, OnMove formulas); null result = plain commands.</summary>
    public Func<IActionPort?>? Actions { get => _drag.Actions; set => _drag.Actions = value; }

    void Guard(Action a)
    {
        if (Guarded != null) { Guarded(a); return; }
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Session.Log.Add(DiagnosticSeverity.Error, "map", "The move failed: " + ex.Message, Map.Name);
        }
    }

    /// <summary>Selects just this piece (null clears).</summary>
    public void SelectOnly(string? id)
    {
        if (_selected.Count == (id == null ? 0 : 1) && (id == null || _selected.Contains(id))) return;
        _selected.Clear();
        if (id != null) _selected.Add(id);
        SelectionEdited();
    }

    /// <summary>Drops selected ids that no longer exist (after commands or a new game); a running drag is cancelled since its snapshot
    /// is out of date.</summary>
    public void PruneSelection()
    {
        var st = State;
        var removed = _selected.RemoveWhere(id => st == null || !st.Pieces.ContainsKey(id));
        if (_drag.Active) CancelDrag(); else _drag.ClearIncoming();
        if (removed > 0) SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    void SelectionEdited()
    {
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The game, the view sides or the rules changed (any thread): prune and repaint on the UI thread.</summary>
    void OnSessionViewChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) PruneSelection();
        else Dispatcher.UIThread.Post(PruneSelection);
    }

    MapDrawOptions Options(IReadOnlySet<string>? hidden) => new(ShowGrid, _selected, null, hidden, DeviceScale: DeviceScale);

    void DrawBand(SKCanvas c)
    {
        if (!_bandActive) return;
        var a = View.ToScreen(_bandStart);
        var b = View.ToScreen(_bandNow);
        var r = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        using var fill = new SKPaint { Color = new SKColor(0x00, 0x78, 0xD7, 0x30) };
        using var edge = new SKPaint { Color = new SKColor(0x00, 0x78, 0xD7), Style = SKPaintStyle.Stroke };
        c.DrawRect(r, fill);
        c.DrawRect(r, edge);
    }

    // ---------- pointer ----------

    PointD MapPoint(Point p) => ScreenToMap(p);

    /// <summary>The add-to-selection modifier: Ctrl on Windows/Linux, Cmd on macOS.</summary>
    static bool AddModifier(KeyModifiers m) => (m & (ShortcutGestures.IsMac ? KeyModifiers.Meta : KeyModifiers.Control)) != 0;

    string? PieceAt(Point p)
    {
        if (State is not { } st) return null;
        try
        {
            return HitTester.PieceAt(st, Map, Session.Model, Session.Images, MapPoint(p), _pieces.Types);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Hit-testing must never take down a pointer handler.
            Session.Log.AddOnce("hit:" + ex.GetType().Name, DiagnosticSeverity.Error, "render", "Hit-testing failed: " + ex.Message, Map.Name);
            return null;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_drag.Active) return; // a drag owns the pointer until it ends
        Focus();
        var pt = e.GetCurrentPoint(this);
        var props = pt.Properties;
        _downPos = pt.Position;
        // Same pan gestures as MapCanvas: middle button, or left button while Space is held.
        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && _space))
        {
            _panning = true;
            _panLast = pt.Position;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Handled = true;
            return;
        }
        if (props.IsRightButtonPressed)
        {
            var hit = PieceAt(pt.Position);
            if (hit != null && !_selected.Contains(hit)) SelectOnly(hit);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        e.Handled = true;
        _leftDown = true;
        var add = AddModifier(e.KeyModifiers);
        _downPiece = PieceAt(pt.Position);
        _downWasSelected = _downPiece != null && _selected.Contains(_downPiece);
        if (_downPiece != null)
        {
            if (add)
            {
                if (!_selected.Remove(_downPiece)) _selected.Add(_downPiece);
                SelectionEdited();
            }
            else if (!_downWasSelected) SelectOnly(_downPiece);
        }
        else
        {
            if (!add && _selected.Count > 0) { _selected.Clear(); SelectionEdited(); }
            _bandActive = true;
            _bandStart = _bandNow = MapPoint(pt.Position);
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_drag.Active)
        {
            if (State is { } st) _drag.Update(st, MapPoint(p));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_panning)
        {
            View.Pan = new SKPoint((float)(View.Pan.X - (p.X - _panLast.X) / View.Zoom), (float)(View.Pan.Y - (p.Y - _panLast.Y) / View.Zoom));
            _panLast = p;
            e.Handled = true;
            ViewChanged();
            return;
        }
        if (_bandActive)
        {
            _bandNow = MapPoint(p);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_leftDown && _downPiece != null &&
            (Math.Abs(p.X - _downPos.X) > DragThreshold || Math.Abs(p.Y - _downPos.Y) > DragThreshold))
        {
            BeginDrag(p, e.Pointer);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        var left = e.InitialPressMouseButton == MouseButton.Left;
        if (_drag.Active)
        {
            // A drop outside the view lands nowhere (the WinForms app hands such drags to other windows; stage 1 has one map).
            if (left) EndDrag(commit: new Rect(Bounds.Size).Contains(p), p);
            if (left) _leftDown = false;
            e.Handled = true;
            return;
        }
        if (_panning && (e.InitialPressMouseButton == MouseButton.Middle || left))
        {
            EndPan();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (!left) return;
        _leftDown = false;
        e.Handled = true;
        if (_bandActive)
        {
            _bandActive = false;
            e.Pointer.Capture(null);
            if (State is { } st && Math.Abs(p.X - _downPos.X) + Math.Abs(p.Y - _downPos.Y) >= 3)
            {
                var r = SelectionRect.FromCorners(_bandStart, _bandNow);
                var added = false;
                foreach (var id in SelectionRect.PieceIdsIn(st, Map, r, Session.Images)) added |= _selected.Add(id);
                if (added) SelectionEdited();
            }
            InvalidateVisual();
        }
        else if (_downPiece != null && _downWasSelected && !AddModifier(e.KeyModifiers) && _selected.Count > 1)
        {
            SelectOnly(_downPiece); // plain click inside a multi-selection narrows it
        }
        _downPiece = null;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_panning) EndPan();
        if (_drag.Active) EndDrag(commit: false, default); // capture taken away (window switch, dialog): cancel
        if (_bandActive) { _bandActive = false; InvalidateVisual(); }
    }

    void EndPan()
    {
        _panning = false;
        Cursor = _space ? new Cursor(StandardCursorType.Hand) : null;
    }

    // ---------- keys ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _drag.Active)
        {
            EndDrag(commit: false, default);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space)
        {
            _space = true;
            if (!_panning) Cursor = new Cursor(StandardCursorType.Hand);
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _space = false;
            if (!_panning) Cursor = null;
            e.Handled = true;
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _space = false;
    }

    // ---------- drag ----------

    void BeginDrag(Point at, IPointer pointer)
    {
        if (State is not { } st || _downPiece == null) return;
        var grabbed = _downPiece;
        _downPiece = null;
        // Another side's piece never moves; from a mixed selection only the pieces this viewer may act on are dragged.
        if (!Session.CanAct(grabbed)) { InvalidateVisual(); return; }
        var movable = _selected.Where(Session.CanAct).ToList();
        if (!_drag.Begin(st, grabbed, movable, MapPoint(_downPos), View, ClientSize, RenderWithout, DeviceScale)) { InvalidateVisual(); return; }
        _dragPointer = pointer;
        pointer.Capture(this);
        _drag.Update(st, MapPoint(at));
        InvalidateVisual();
    }

    /// <summary>Renders the scene once with the given pieces left out (the drag snapshot), at device resolution so it stays sharp on HiDPI.</summary>
    SKImage? RenderWithout(IReadOnlySet<string> hidden)
    {
        if (State is not { } st) return null;
        var size = ClientSize;
        var k = DeviceScale;
        using var surface = SKSurface.Create(new SKImageInfo(Math.Max(1, (int)Math.Ceiling(size.Width * k)), Math.Max(1, (int)Math.Ceiling(size.Height * k))));
        if (surface == null) return null;
        surface.Canvas.Scale(k);
        _renderer.Draw(surface.Canvas, Map, _tiles, st, Session.Model, View, size, Options(hidden));
        return surface.Snapshot();
    }

    /// <summary>The one way out of a drag: frees the snapshot and sprites, releases the pointer, repaints. Safe to call twice.</summary>
    void CancelDrag()
    {
        _drag.End();
        var p = _dragPointer;
        _dragPointer = null;
        if (p?.Captured == this) p.Capture(null);
        InvalidateVisual();
    }

    void EndDrag(bool commit, Point at)
    {
        var st = State;
        if (commit && st != null) _drag.Commit(st, MapPoint(at)); else _drag.End();
        CancelDrag();
        if (commit) PruneSelection();
    }
}
