using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Map;

/// <summary>
/// One map: board tiles, grids and pieces drawn by the shared <see cref="MapRenderer"/>, with pan and zoom copied from the WinForms
/// MapCanvas. <see cref="View"/> works in Avalonia DIPs (control-relative); Avalonia's canvas applies the render scaling, so the map is
/// rasterised at device resolution. Selection, rubber band and piece drags live in the input part (MapView.Input.cs).
/// </summary>
public sealed partial class MapView : Control
{
    /// <summary>Workspace grey behind "no game" messages (WinForms SystemColors.AppWorkspace).</summary>
    static readonly SKColor Workspace = new(0xAB, 0xAB, 0xAB);
    /// <summary>DIPs one wheel/trackpad scroll unit pans (Avalonia's ScrollViewer uses the same 50).</summary>
    const double ScrollStep = 50;
    /// <summary>Largest wheel delta one event may zoom by (precise trackpads send big deltas; keeps Cmd+scroll zoom smooth).</summary>
    const double MaxZoomNotches = 3;

    readonly MapRenderer _renderer;
    readonly PieceRenderer _pieces;
    readonly MapDragController _drag;
    readonly HashSet<string> _selected = new();
    Dictionary<string, TileSet> _tiles = new();
    double _zoomStep = 1.2;
    bool _initialView;
    TopLevel? _topLevel;
    int _playbackReported;

    public MapView(ModuleSession session, MapDef map)
    {
        Session = session;
        Map = map;
        _pieces = new PieceRenderer(session.Images);
        _renderer = new MapRenderer(session.Images, _pieces);
        _drag = new MapDragController(session, map, _pieces, Guard);
        _drag.Aborted += CancelDrag;
        Focusable = true;
        ClipToBounds = true;
    }

    public ModuleSession Session { get; }
    public MapDef Map { get; }
    /// <summary>Map-to-screen transform in DIPs relative to this control.</summary>
    public Viewport View { get; } = new();

    /// <summary>Wheel zoom factor per notch (WinForms default 1.2, settings clamp 1.05..2).</summary>
    public double ZoomStep { get => _zoomStep; set => _zoomStep = Math.Clamp(value, 1.05, 2); }

    /// <summary>Forces grids on for all boards; false (default) draws only grids the module marks visible.</summary>
    public bool ShowGrid { get; set { field = value; InvalidateVisual(); } }

    /// <summary>0..1 while board tiles are being prepared; null once ready.</summary>
    public double? Preparing { get; set { field = value; InvalidateVisual(); } }

    /// <summary>Raised whenever pan or zoom changes (and when tiles arrive), like MapCanvas.ViewportChanged.</summary>
    public event EventHandler? ViewportChanged;

    /// <summary>What this player may see (the game state itself in solo).</summary>
    GameState? State => Session.View;

    /// <summary>Device pixels per DIP of the window this view is in (1 until attached).</summary>
    float DeviceScale => (float)(_topLevel?.RenderScaling ?? 1);

    SKSize ClientSize => new((float)Math.Max(Bounds.Width, 1), (float)Math.Max(Bounds.Height, 1));

    /// <summary>Stores tiles under <see cref="BoardTiler.TileKey"/>.</summary>
    public void SetTiles(string tileKey, TileSet ts)
    {
        _tiles = new Dictionary<string, TileSet>(_tiles) { [tileKey] = ts };
        InvalidateVisual();
        NotifyViewport();
    }

    /// <summary>The map point under a DIP point of this control.</summary>
    public PointD ScreenToMap(Point p) => View.ToMap(new SKPoint((float)p.X, (float)p.Y));

    /// <summary>Fits every board into the view (may zoom past 100%), centred.</summary>
    public void ZoomToFit()
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        _initialView = true;
        FitView.ZoomToFit(View, Map, ClientSize);
        ViewChanged();
    }

    /// <summary>Scrolls so the map point is in the middle of the view (zoom unchanged).</summary>
    public void CentreOn(PointD p)
    {
        FitView.Center(View, p, ClientSize);
        ViewChanged();
    }

    /// <summary>Sets the zoom (clamped) around the middle of the view.</summary>
    public void ZoomCentered(float zoom)
    {
        var size = ClientSize;
        var centre = View.ToMap(new SKPoint(size.Width / 2, size.Height / 2));
        View.Zoom = zoom;
        FitView.Center(View, centre, size);
        ViewChanged();
    }

    void ViewChanged()
    {
        InvalidateVisual();
        NotifyViewport();
    }

    void NotifyViewport() => ViewportChanged?.Invoke(this, EventArgs.Empty);

    // ---------- painting ----------

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_initialView || e.NewSize.Width <= 1 || e.NewSize.Height <= 1) return;
        // First show, as MapCanvas: fit the map, but never zoom in past 100%.
        _initialView = true;
        var size = ClientSize;
        FitView.ZoomToFit(View, Map, size);
        if (View.Zoom > 1f)
        {
            View.Zoom = 1f;
            var x = FitView.Extent(Map);
            FitView.Center(View, new PointD(x.X + x.W / 2, x.Y + x.H / 2), size);
        }
        ViewChanged();
    }

    public override void Render(DrawingContext context)
    {
        var size = new SKSize((float)Bounds.Width, (float)Bounds.Height);
        if (size.Width <= 0 || size.Height <= 0) return;
        // Recorded here on the UI thread (the game state is only touched here), replayed on Avalonia's render thread.
        using var recorder = new SKPictureRecorder();
        var c = recorder.BeginRecording(new SKRect(0, 0, size.Width, size.Height));
        try
        {
            Scene(c, size);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Session.Log.AddOnce("paint:" + ex.GetType().Name, DiagnosticSeverity.Error, "render", "Map drawing failed: " + ex.Message, Map.Name);
        }
        context.Custom(new SkiaDrawOperation(recorder.EndRecording(), new Rect(Bounds.Size), PlaybackFailed));
    }

    /// <summary>Called on the render thread when replaying the picture fails: logged once, on the UI thread.</summary>
    internal void PlaybackFailed(Exception ex)
    {
        if (Interlocked.Exchange(ref _playbackReported, 1) != 0) return;
        Dispatcher.UIThread.Post(() => Session.Log.AddOnce("playback:" + ex.GetType().Name, DiagnosticSeverity.Error, "render",
            "Map drawing failed: " + ex.Message, Map.Name));
    }

    void Scene(SKCanvas c, SKSize size)
    {
        var st = State;
        if (st == null)
        {
            c.Clear(Workspace);
            Message(c, size, "No game in progress");
            return;
        }
        if (!Session.CanSeeMap(Map.Id))
        {
            c.Clear(Workspace);
            Message(c, size, "This map belongs to another side");
            return;
        }
        if (_drag.IsStale(View, ClientSize, DeviceScale))
        {
            _drag.End();
            Dispatcher.UIThread.Post(CancelDrag); // release the pointer outside the render pass
        }
        if (_drag.Active) _drag.Draw(c, View, st);
        else _renderer.Draw(c, Map, _tiles, st, Session.Model, View, size, Options(null));
        DrawBand(c);
        if (Preparing is { } p) Message(c, size, $"Preparing map… {(int)Math.Round(p * 100)}%");
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

    // ---------- pan and zoom ----------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        if (_drag.Active) return; // zoom/pan is ignored mid-drag: the snapshot is only valid for the view the drag started in
        var d = e.Delta;
        if (ShortcutGestures.IsMac && (e.KeyModifiers & KeyModifiers.Meta) == 0)
        {
            // macOS: two-finger scroll pans both axes (natural direction as the OS delivers it); pinch and Cmd+scroll zoom.
            Scroll(d.X, d.Y);
            return;
        }
        // Windows/Linux as MapCanvas (and Cmd+scroll on macOS): vertical wheel zooms at the cursor, horizontal wheel pans.
        if (d.Y != 0)
        {
            var at = e.GetPosition(this);
            View.ZoomAt(new SKPoint((float)at.X, (float)at.Y), (float)Math.Pow(_zoomStep, Math.Clamp(d.Y, -MaxZoomNotches, MaxZoomNotches)));
        }
        Scroll(d.X, 0);
    }

    /// <summary>Pans by scroll units the way a ScrollViewer does (offset -= delta * 50 DIPs).</summary>
    void Scroll(double dx, double dy)
    {
        if (dx == 0 && dy == 0) { ViewChanged(); return; }
        View.Pan = new SKPoint((float)(View.Pan.X - dx * ScrollStep / View.Zoom), (float)(View.Pan.Y - dy * ScrollStep / View.Zoom));
        ViewChanged();
    }

    /// <summary>Trackpad pinch (macOS): Delta.X is the magnification increment, so the zoom factor is 1 + Delta.X.</summary>
    void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        e.Handled = true;
        if (_drag.Active || !(1 + e.Delta.X > 0)) return;
        var at = e.GetPosition(this);
        View.ZoomAt(new SKPoint((float)at.X, (float)at.Y), (float)(1 + e.Delta.X));
        ViewChanged();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AddHandler(PointerTouchPadGestureMagnifyEvent, OnMagnify);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null) _topLevel.ScalingChanged += OnScalingChanged;
        lock (_viewLock) _lastSides = Session.ViewSides;
        Session.ViewChanged += OnSessionViewChanged;
        Session.GameReplaced += OnGameReplaced;
        HookGame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RemoveHandler(PointerTouchPadGestureMagnifyEvent, OnMagnify);
        if (_topLevel != null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        Session.ViewChanged -= OnSessionViewChanged;
        Session.GameReplaced -= OnGameReplaced;
        HookGame();   // _topLevel is null: unhooks
        CancelDrag();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Moved to a monitor with another scaling: redraw with tiles and SVG rasters for the new device resolution.</summary>
    void OnScalingChanged(object? sender, EventArgs e) => InvalidateVisual();
}
