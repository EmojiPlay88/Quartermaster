using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Quartermaster.App;

/// <summary>Skia map view: rendering, pan/zoom, selection, drag and drop, hover. Menus and game commands live in <see cref="MapWindow"/>.</summary>
sealed class MapCanvas : SKControl
{
    readonly ModuleSession _s;
    readonly MapRenderer _renderer;
    readonly PieceRenderer _pieces;
    readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 600 };
    readonly HashSet<string> _selected = new();
    Dictionary<string, TileSet> _tiles = new();
    CounterDetailPopup? _popup;
    string? _hoverStack;
    bool _initialView;
    bool _space;
    bool _panning;
    Point _panLast;
    Point _lastMove = new(int.MinValue, int.MinValue);
    Point _downPos;
    string? _downPiece;
    bool _downWasSelected;
    bool _bandActive;
    PointD _bandStart, _bandNow;
    readonly MapDragController _drag;
    SKSize _sceneSize;
    bool _repaintPending;
    bool _paintFailed;

    public MapCanvas(ModuleSession s, MapDef map)
    {
        _s = s;
        Map = map;
        _pieces = new PieceRenderer(s.Images);
        _renderer = new MapRenderer(s.Images, _pieces);
        _drag = new MapDragController(s, map, _pieces, Guard);
        AllowDrop = true;
        TabStop = true;
        DoubleBuffered = true;
        BackColor = SystemColors.AppWorkspace;
        _hoverTimer.Tick += (_, _) => ShowPopup();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public MapDef Map { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Viewport View { get; } = new();
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    /// <summary>Forces grids on for all boards; false (default) draws only grids the module marks visible.</summary>
    public bool ShowGrid { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlySet<string> Selected => _selected;
    /// <summary>0..1 while board tiles are being prepared; null once ready.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double? Preparing { get; set; }

    /// <summary>Runs a game-changing action inside the owning window's error guard.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<Action>? Guarded { get; set; }

    void Guard(Action a) { if (Guarded != null) Guarded(a); else a(); }

    public event Action<string, string>? StatusChanged;
    /// <summary>Right click: the piece under the cursor (or null) and the client point.</summary>
    public event Action<string?, Point>? ContextRequested;

    GameState? State => _s.Game?.State;

    /// <summary>Stores tiles under <see cref="BoardTiler.TileKey"/>.</summary>
    public void SetTiles(string tileKey, TileSet ts)
    {
        _tiles = new Dictionary<string, TileSet>(_tiles) { [tileKey] = ts };
        Invalidate();
    }

    public void SelectOnly(string? id)
    {
        _selected.Clear();
        if (id != null) _selected.Add(id);
        Invalidate();
    }

    /// <summary>Drops selected ids that no longer exist (after commands or a new game).</summary>
    public void PruneSelection()
    {
        var st = State;
        _selected.RemoveWhere(id => st == null || !st.Pieces.ContainsKey(id));
        HidePopup();
        Invalidate();
    }

    public void ZoomTo100() => ZoomCentered(1f);
    public void ZoomFit()
    {
        FitView.ZoomToFit(View, Map, new SKSize(Math.Max(Width, 1), Math.Max(Height, 1)));
        ReportStatus(null);
        Invalidate();
    }

    void ZoomCentered(float zoom)
    {
        var size = new SKSize(Math.Max(Width, 1), Math.Max(Height, 1));
        var centre = View.ToMap(new SKPoint(size.Width / 2, size.Height / 2));
        View.Zoom = zoom;
        FitView.Center(View, centre, size);
        ReportStatus(null);
        Invalidate();
    }

    /// <summary>Coalesced repaint for high-frequency input (pan, band, drag): at most one queued until it paints.</summary>
    void Request()
    {
        if (_repaintPending && Visible) return;
        _repaintPending = true;
        Invalidate();
    }

    // ---------- painting ----------

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        _repaintPending = false;
        Scene(e.Surface.Canvas, new SKSize(e.Info.Width, e.Info.Height));
    }

    /// <summary>Renders the whole view (used by paint and by the smoke screenshot).</summary>
    public SKBitmap Snapshot()
    {
        var bmp = new SKBitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        using var canvas = new SKCanvas(bmp);
        Scene(canvas, new SKSize(bmp.Width, bmp.Height));
        return bmp;
    }

    void Scene(SKCanvas c, SKSize size)
    {
        try
        {
            _sceneSize = size;
            if (!_initialView && size.Width > 1 && size.Height > 1)
            {
                _initialView = true;
                FitView.ZoomToFit(View, Map, size);
                if (View.Zoom > 1f)
                {
                    View.Zoom = 1f;
                    var e = FitView.Extent(Map);
                    FitView.Center(View, new PointD(e.X + e.W / 2, e.Y + e.H / 2), size);
                }
                ReportStatus(null);
            }
            var st = State;
            if (st == null)
            {
                c.Clear(new SKColor(SystemColors.AppWorkspace.R, SystemColors.AppWorkspace.G, SystemColors.AppWorkspace.B));
                Message(c, size, "No game in progress");
                return;
            }
            if (_drag.IsStale(View, size)) _drag.End();
            if (_drag.Active)
                _drag.Draw(c, View, st);
            else
            {
                _renderer.Draw(c, Map, _tiles, st, _s.Model, View, size, new MapDrawOptions(ShowGrid, _selected, _hoverStack));
                if (_drag.Incoming) _drag.Draw(c, View, st);
            }
            DrawBand(c);
            if (Preparing is { } p) Message(c, size, $"Preparing map… {(int)Math.Round(p * 100)}%");
            _paintFailed = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_paintFailed) return;
            _paintFailed = true;
            Program.AppLog("map paint failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "render", "The map could not be drawn: " + ex.Message, Map.Name);
        }
    }

    static void Message(SKCanvas c, SKSize size, string text)
    {
        using var font = new SKFont(SKTypeface.FromFamilyName("Tahoma") ?? SKTypeface.Default, 16);
        var w = font.MeasureText(text);
        var box = SKRect.Create((size.Width - w) / 2 - 16, size.Height / 2 - 22, w + 32, 40);
        using var back = new SKPaint { Color = new SKColor(0xF0, 0xF0, 0xF0) };
        using var edge = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke };
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        c.DrawRect(box, back);
        c.DrawRect(box, edge);
        c.DrawText(text, box.Left + 16, box.MidY + 6, SKTextAlign.Left, font, ink);
    }

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

    // ---------- mouse ----------

    PointD MapPoint(Point p) => View.ToMap(new SKPoint(p.X, p.Y));

    string? PieceAt(Point p)
    {
        if (State is not { } st) return null;
        try
        {
            return HitTester.PieceAt(st, Map, _s.Model, _s.Images, MapPoint(p), _pieces.Types);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Hit-testing must never take down a mouse handler.
            _s.Log.AddOnce("hit:" + ex.GetType().Name, DiagnosticSeverity.Error, "render", "Hit-testing failed: " + ex.Message, Map.Name);
            return null;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_drag.Active) return; // a drag owns the mouse until it ends
        Focus();
        HidePopup();
        _downPos = e.Location;
        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && _space))
        {
            _panning = true;
            _panLast = e.Location;
            Cursor = Cursors.SizeAll;
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            var hit = PieceAt(e.Location);
            if (hit != null && !_selected.Contains(hit)) SelectOnly(hit);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        var ctrl = (ModifierKeys & Keys.Control) != 0;
        _downPiece = PieceAt(e.Location);
        _downWasSelected = _downPiece != null && _selected.Contains(_downPiece);
        if (_downPiece != null)
        {
            if (ctrl)
            {
                if (!_selected.Remove(_downPiece)) _selected.Add(_downPiece);
            }
            else if (!_downWasSelected) SelectOnly(_downPiece);
        }
        else
        {
            if (!ctrl) _selected.Clear();
            _bandActive = true;
            _bandStart = _bandNow = MapPoint(e.Location);
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Location == _lastMove) return;
        _lastMove = e.Location;
        ReportStatus(e.Location);

        if (_drag.Active)
        {
            if (!ClientRectangle.Contains(e.Location)) { HandOffToOle(); return; }
            if (State is { } dst) _drag.Update(dst, MapPoint(e.Location));
            Request();
            return;
        }
        if (_panning)
        {
            View.Pan = new SKPoint(View.Pan.X - (e.X - _panLast.X) / View.Zoom, View.Pan.Y - (e.Y - _panLast.Y) / View.Zoom);
            _panLast = e.Location;
            Request();
            return;
        }
        if (_bandActive)
        {
            _bandNow = MapPoint(e.Location);
            Request();
            return;
        }
        if (e.Button == MouseButtons.Left && _downPiece != null)
        {
            var ds = SystemInformation.DragSize;
            if (Math.Abs(e.X - _downPos.X) > ds.Width / 2 || Math.Abs(e.Y - _downPos.Y) > ds.Height / 2) BeginDrag(e.Location);
            return;
        }
        if (e.Button == MouseButtons.None) UpdateHover(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag.Active)
        {
            if (e.Button == MouseButtons.Left) EndDrag(commit: true, e.Location);
            return;
        }
        if (_panning && (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Left))
        {
            _panning = false;
            Cursor = Cursors.Default;
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            ContextRequested?.Invoke(PieceAt(e.Location), e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (_bandActive)
        {
            _bandActive = false;
            if (State is { } st && Math.Abs(e.X - _downPos.X) + Math.Abs(e.Y - _downPos.Y) >= 3)
            {
                var r = SelectionRect.FromCorners(_bandStart, _bandNow);
                foreach (var id in SelectionRect.PieceIdsIn(st, Map, r, _s.Images)) _selected.Add(id);
            }
            Invalidate();
        }
        else if (_downPiece != null && _downWasSelected && (ModifierKeys & Keys.Control) == 0 && _selected.Count > 1)
        {
            SelectOnly(_downPiece); // plain click inside a multi-selection narrows it
        }
        _downPiece = null;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || State is not { } st) return;
        var hit = PieceAt(e.Location);
        if (hit == null || !st.Pieces.TryGetValue(hit, out var p) || p.StackId == null || !st.Stacks.TryGetValue(p.StackId, out var stack)) return;
        var game = _s.Game;
        Guard(() => game?.Execute(new SetStackExpanded(stack.Id, !stack.Expanded)));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_drag.Active) return; // zoom is ignored mid-drag: the snapshot is only valid for the view the drag started in
        HidePopup();
        View.ZoomAt(new SKPoint(e.X, e.Y), (float)Math.Pow(1.2, e.Delta / 120.0));
        ReportStatus(e.Location);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_popup != null && _popup.Bounds.Contains(Cursor.Position)) return;
        HidePopup();
        if (_hoverStack != null) { _hoverStack = null; Invalidate(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && _drag.Active) { EndDrag(commit: false, default); e.Handled = true; return; }
        if (e.KeyCode == Keys.Space) { _space = true; Cursor = Cursors.Hand; e.Handled = true; }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space) { _space = false; if (!_panning) Cursor = Cursors.Default; }
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _space = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_drag.Active && !Capture) EndDrag(commit: false, default); // capture stolen (Alt+Tab, dialog): cancel
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_drag.Active) EndDrag(commit: false, default); // the snapshot no longer matches the client area
    }

    void ReportStatus(Point? at)
    {
        var loc = "-";
        if (at is { } p)
        {
            var m = MapPoint(p);
            loc = $"{(int)Math.Round(m.X)}, {(int)Math.Round(m.Y)}";
            foreach (var b in Map.Boards)
            {
                var lx = m.X - b.Offset.X;
                var ly = m.Y - b.Offset.Y;
                if (lx < 0 || ly < 0 || lx >= b.Width || ly >= b.Height) continue;
                var name = b.Grid?.LocationName(new PointD(lx, ly)) ?? "";
                if (name.Length > 0) loc += "  " + name;
                break;
            }
        }
        StatusChanged?.Invoke(loc, $"{Math.Round(View.Zoom * 100)}%");
    }

    // ---------- drag and drop ----------
    // In-window drags use mouse capture (no OLE, so no lag); leaving the client area hands the drag to OLE so it can land in another window.

    void BeginDrag(Point at)
    {
        if (State is not { } st || _downPiece == null) return;
        var grabbed = _downPiece;
        _downPiece = null;
        HidePopup();
        _hoverStack = null;
        var size = SceneSize();
        if (!_drag.Begin(st, grabbed, _selected, MapPoint(_downPos), View, size, RenderWithout)) return;
        Capture = true;
        _drag.Update(st, MapPoint(at));
        Request();
    }

    SKSize SceneSize() => _sceneSize.Width > 1 && _sceneSize.Height > 1 ? _sceneSize : new SKSize(Math.Max(Width, 1), Math.Max(Height, 1));

    /// <summary>Renders the scene once with the given pieces left out (the drag snapshot).</summary>
    SKImage? RenderWithout(IReadOnlySet<string> hidden)
    {
        if (State is not { } st) return null;
        var size = SceneSize();
        using var surface = SKSurface.Create(new SKImageInfo((int)size.Width, (int)size.Height));
        if (surface == null) return null;
        _renderer.Draw(surface.Canvas, Map, _tiles, st, _s.Model, View, size, new MapDrawOptions(ShowGrid, _selected, null, hidden));
        return surface.Snapshot();
    }

    void EndDrag(bool commit, Point at)
    {
        var st = State;
        if (commit && st != null) _drag.Commit(st, MapPoint(at)); else _drag.End();
        Capture = false;
        Invalidate();
        if (commit) PruneSelection();
    }

    /// <summary>Cursor left the window while dragging: switch to OLE so the drop can land on another MapWindow.</summary>
    void HandOffToOle()
    {
        var info = _drag.Info;
        _drag.End();
        Capture = false;
        Invalidate();
        if (info == null) return;
        MapDragController.Current = info;
        var data = new DataObject();
        data.SetData(MapDragController.Format, string.Join("\n", info.Ids));
        try { DoDragDrop(data, DragDropEffects.Move); }
        finally
        {
            MapDragController.Current = null;
            _drag.ClearIncoming();
            Invalidate();
        }
    }

    bool CanAccept(DragEventArgs e) => MapDragController.Current is { } d && d.Session == _s && e.Data?.GetDataPresent(MapDragController.Format) == true;

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        OnDragOver(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (!CanAccept(e)) { e.Effect = DragDropEffects.None; return; }
        e.Effect = DragDropEffects.Move;
        if (State is not { } st || MapDragController.Current is not { } d) return;
        _drag.UpdateIncoming(d, st, MapPoint(PointToClient(new Point(e.X, e.Y))), View.Zoom);
        Request();
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _drag.ClearIncoming();
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (CanAccept(e) && State is { } st && _s.Game != null && _drag.Info != null)
            _drag.Commit(st, MapPoint(PointToClient(new Point(e.X, e.Y))));
        else
            _drag.ClearIncoming();
        Invalidate();
    }

    // ---------- hover popup ----------

    void UpdateHover(Point p)
    {
        string? stackId = null;
        if (PieceAt(p) is { } id && State is { } st && st.Pieces.TryGetValue(id, out var pc)) stackId = pc.StackId;
        if (stackId == _hoverStack) return;
        _hoverStack = stackId;
        HidePopup();
        _hoverTimer.Stop();
        if (stackId != null) _hoverTimer.Start();
        Invalidate();
    }

    void ShowPopup()
    {
        _hoverTimer.Stop();
        if (_hoverStack == null || _panning || _bandActive || State is not { } st || !st.Stacks.TryGetValue(_hoverStack, out var stack)) return;
        HidePopup();
        _popup = CounterDetailPopup.Create(_s, st, stack, _pieces);
        _popup.ShowAt(Cursor.Position);
    }

    public void HidePopup()
    {
        _hoverTimer.Stop();
        _popup?.Close();
        _popup?.Dispose();
        _popup = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HidePopup();
            _drag.Dispose();
            _hoverTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
