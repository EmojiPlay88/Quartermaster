using Avalonia;
using Avalonia.Controls;
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
/// Esc to cancel a drag. As in WinForms, what moves is filtered only by <see cref="MapDragController.MovableIds"/> (move-locked
/// Immobile pieces stay) and by the action port (<see cref="LocalActions"/> blocks viewer copies and replays) — never by side.
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
    bool _contextPress;

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

    /// <summary>The window's dialogs, game-master switch and shortcut texts for the right-click menu; null: no dialogs (prompts count as
    /// cancelled, nothing is deleted, no property sheet).</summary>
    public MapMenuServices? MenuServices { get; set; }

    /// <summary>The right-click menu while it is open (tests).</summary>
    internal ContextMenu? OpenMenu { get; private set; }

    /// <summary>The last menu command, which may still wait for a dialog (tests).</summary>
    internal Task? MenuCommand { get; private set; }

    /// <summary>A short message for the status bar ("Select a counter first"), as MapCanvas.DropMessage.</summary>
    public event Action<string>? StatusMessage;

    /// <summary>Scrolls to the selected counters (WinForms MapCanvas.CentreOnSelection); says so when nothing is selected.</summary>
    public void CentreOnSelection()
    {
        if (State is { } st && MapViewTools.SelectionCentre(st, Map.Id, _selected) is { } c) CentreOn(c);
        else StatusMessage?.Invoke("Select a counter first");
    }

    /// <summary>Runs a menu command as WinForms MapWindow.Run: failures (also those after an asynchronous dialog) are logged, not thrown.</summary>
    internal void RunMenuCommand(Func<Task> command)
    {
        Task? t = null;
        GuardCommand(() => t = command());
        if (t != null) MenuCommand = Observe(t);
    }

    async Task Observe(Task t)
    {
        try { await t; }
        catch (Exception) when (t.IsFaulted) { GuardCommand(() => t.GetAwaiter().GetResult()); }   // rethrown inside the guard: logged
    }

    void GuardCommand(Action a)
    {
        if (Guarded != null) { Guarded(a); return; }
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Session.Log.Add(DiagnosticSeverity.Error, "command", "A command failed: " + ex.Message, Map.Name);
        }
    }

    /// <summary>Opens the right-click menu for what is under <paramref name="p"/> (WinForms MapCanvas.ContextRequested → MapWindow.OnContext).</summary>
    void OpenContextMenu(Point p) => OpenContextMenu(PieceAt(p));

    /// <summary>Opens the right-click menu of a counter (null: the map menu) at the pointer.</summary>
    internal void OpenContextMenu(string? pieceId)
    {
        OpenMenu?.Close();
        ContextMenu? menu = null;
        GuardCommand(() => menu = MapContextMenu.Build(this, pieceId));
        if (menu == null) return;
        menu.Closed += (_, _) => { if (ReferenceEquals(OpenMenu, menu)) OpenMenu = null; };
        OpenMenu = menu;
        menu.Open(this);
    }

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
        HidePopup();
        if (removed > 0) SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    void SelectionEdited()
    {
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // View refresh state, written from whichever thread raised Session.ViewChanged: guarded by _viewLock.
    readonly object _viewLock = new();
    IReadOnlySet<string>? _lastSides;
    bool _viewQueued;
    bool _sidesChanged;
    GameSession? _attachedGame;

    /// <summary>Handler runs of the coalesced view refresh (tests).</summary>
    internal int ViewRefreshRuns { get; private set; }

    /// <summary>Runs on the UI thread: now when already there, else queued (WinForms MapWindow.OnUi).</summary>
    static void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a);
    }

    /// <summary>Follows the session's current game (WinForms MapWindow.Attach).</summary>
    void HookGame()
    {
        if (_attachedGame != null) { _attachedGame.Changed -= OnGameChanged; _attachedGame.Rewound -= OnGameRewound; }
        _attachedGame = _topLevel == null ? null : Session.Game;
        if (_attachedGame != null) { _attachedGame.Changed += OnGameChanged; _attachedGame.Rewound += OnGameRewound; }
    }

    // As WinForms MapWindow.OnGameChanged / OnRewound: any game change (a command from anyone, an undo, a redo, a step back) prunes the
    // selection and cancels a captured drag, whose snapshot is now stale. This view's own drop ends its drag before executing, so it is
    // unaffected.
    void OnGameChanged(GameCommand _) => OnUi(PruneIfAttached);
    void OnGameRewound() => OnUi(PruneIfAttached);

    /// <summary>A new or loaded game (WinForms MapWindow.OnGameReplaced): follow it, prune, cancel any drag.</summary>
    void OnGameReplaced() => OnUi(() => { HookGame(); PruneIfAttached(); });

    void PruneIfAttached()
    {
        if (_topLevel == null) return;
        PruneSelection();
        RefreshLastMoved();   // also after an undo or a new game
    }

    /// <summary>
    /// As WinForms MapWindow.OnViewChanged (hot-seat only: a new viewer or a rules edit can hide what is selected). Does nothing in solo
    /// (no view sides now or before). Bursts, from any thread, coalesce into one queued UI-thread run; that run prunes and cancels a drag
    /// only when the view sides really changed, otherwise it repaints and forgets selected pieces the view no longer has.
    /// </summary>
    void OnSessionViewChanged()
    {
        var sides = Session.ViewSides;
        lock (_viewLock)
        {
            if (sides == null && _lastSides == null) return;
            if (!ReferenceEquals(sides, _lastSides)) _sidesChanged = true;
            _lastSides = sides;
            if (_viewQueued) return;
            _viewQueued = true;
        }
        Dispatcher.UIThread.Post(RefreshFromSession);
    }

    void RefreshFromSession()
    {
        bool sidesChanged;
        lock (_viewLock)
        {
            _viewQueued = false;
            sidesChanged = _sidesChanged;
            _sidesChanged = false;
        }
        ViewRefreshRuns++;
        if (_topLevel == null) return;   // detached meanwhile
        if (sidesChanged) { PruneSelection(); return; }
        HidePopup();   // WinForms prunes on every view change, which closes the popup
        var st = State;
        if (_selected.RemoveWhere(id => st == null || !st.Pieces.ContainsKey(id)) > 0) SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    MapDrawOptions Options(IReadOnlySet<string>? hidden, string? hover = null) =>
        new(ShowGrid, _selected, hover, hidden, HighlightPieceIds: _highlightLast ? _lastMoved : null, HideAllPieces: _hideCounters, DeviceScale: DeviceScale);

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

    /// <summary>The counter under <paramref name="p"/>; none while counters are hidden (WinForms MapCanvas.PieceAt): no hover, selection,
    /// drag or counter menu then.</summary>
    string? PieceAt(Point p)
    {
        if (State is not { } st || _hideCounters) return null;
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
        _contextPress = false;    // a menu press released outside the view is forgotten
        Focus();
        HidePopup();
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
        if (LosPressed(e, pt)) return;
        // Right press, or Ctrl+click on macOS (never add-to-selection there): select the counter now, open the menu on release (MapCanvas).
        if (props.IsRightButtonPressed
            || (ShortcutGestures.IsMac && props.IsLeftButtonPressed && (e.KeyModifiers & KeyModifiers.Control) != 0))
        {
            var hit = PieceAt(pt.Position);
            if (hit != null && !_selected.Contains(hit)) SelectOnly(hit);
            _contextPress = true;
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
        if (e.ClickCount == 2) ToggleStack(pt.Position);
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
        if (LosMoved(p)) { e.Handled = true; return; }
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
            return;
        }
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed && !props.IsMiddleButtonPressed) UpdateHover(p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        var left = e.InitialPressMouseButton == MouseButton.Left;
        if (LosReleased(e)) return;
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
        if (_contextPress && (left || e.InitialPressMouseButton == MouseButton.Right))
        {
            _contextPress = false;
            e.Handled = true;
            OpenContextMenu(p);
            return;
        }
        if (!left) return;
        _leftDown = false;
        e.Handled = true;
        if (_bandActive)
        {
            _bandActive = false;
            e.Pointer.Capture(null);
            if (State is { } st && !_hideCounters && Math.Abs(p.X - _downPos.X) + Math.Abs(p.Y - _downPos.Y) >= 3)
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
        if (_los is { Dragging: true } los) { los.End(); InvalidateVisual(); }   // capture taken away mid-line: keep what was drawn
    }

    void EndPan()
    {
        _panning = false;
        Cursor = _space ? new Cursor(StandardCursorType.Hand) : RestingCursor;
    }

    // ---------- keys ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && LosEscape()) { e.Handled = true; return; }
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
            if (!_panning) Cursor = RestingCursor;
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
        HidePopup();
        _hoverStack = null;
        if (!_drag.Begin(st, grabbed, _selected, MapPoint(_downPos), View, ClientSize, RenderWithout, DeviceScale)) { InvalidateVisual(); return; }
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
