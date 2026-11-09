using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Map;

/// <summary>
/// Small mini-map of the active map tab (WinForms OverviewWindow): the coarsest tile level of each board (grey where a board has no tiles),
/// plus a red rectangle for what the tab's view shows. Click or drag centres that view there. Follows the active tab.
/// </summary>
public sealed class OverviewWindow : Window
{
    readonly MapTabs _tabs;
    readonly OverviewView _view = new();
    MapView? _target;

    public OverviewWindow(MapTabs tabs)
    {
        _tabs = tabs;
        Title = "Overview";
        ShowInTaskbar = false;
        Width = 260;
        Height = 240;
        Content = _view;
        tabs.ActiveViewChanged += OnActiveChanged;
        SetTarget(tabs.ActiveView);
    }

    /// <summary>The map view the overview shows (tests).</summary>
    internal MapView? Target => _target;
    internal OverviewView Canvas => _view;

    void OnActiveChanged() => SetTarget(_tabs.ActiveView);

    void SetTarget(MapView? v)
    {
        if (ReferenceEquals(v, _target)) return;
        if (_target != null) _target.ViewportChanged -= OnViewport;
        _target = v;
        if (v != null) v.ViewportChanged += OnViewport;
        Title = v == null ? "Overview" : "Overview - " + v.Map.Name;
        _view.Target = v;
    }

    void OnViewport(object? sender, EventArgs e) => _view.InvalidateVisual();

    protected override void OnClosed(EventArgs e)
    {
        _tabs.ActiveViewChanged -= OnActiveChanged;
        if (_target != null) _target.ViewportChanged -= OnViewport;
        _target = null;
        _view.Target = null;
        _view.DisposeImages();
        base.OnClosed(e);
    }
}

/// <summary>The overview's picture and mouse handling.</summary>
public sealed class OverviewView : Control
{
    /// <summary>WinForms SystemColors.AppWorkspace.</summary>
    static readonly SKColor Workspace = new(0xAB, 0xAB, 0xAB);
    static readonly SKColor BlankBoard = new(0xC0, 0xC0, 0xC0);

    readonly Dictionary<string, SKImage?> _images = new();
    bool _dragging;

    public MapView? Target { get; set { field = value; InvalidateVisual(); } }

    SizeD ControlSize => new(Math.Max(Bounds.Width, 1), Math.Max(Bounds.Height, 1));

    RectD Extent => Target != null ? FitView.Extent(Target.Map) : new RectD(0, 0, 1, 1);

    SKSize TargetSize => Target is { } t ? new SKSize((float)Math.Max(t.Bounds.Width, 1), (float)Math.Max(t.Bounds.Height, 1)) : new SKSize(1, 1);

    /// <summary>The red rectangle in control pixels (what the target view shows); null without a target.</summary>
    internal RectD? ViewportRectPx() =>
        Target is { } t ? OverviewGeometry.ViewportRect(t.View.VisibleMapRect(TargetSize), OverviewGeometry.Fit(Extent, ControlSize)) : null;

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        using var recorder = new SKPictureRecorder();
        var c = recorder.BeginRecording(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
        try { DrawMap(c, Bounds.Width, Bounds.Height); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("overview paint failed: " + ex.Message); }
        context.Custom(new SkiaDrawOperation(recorder.EndRecording(), new Rect(Bounds.Size), ex => System.Diagnostics.Trace.WriteLine("overview replay failed: " + ex.Message)));
    }

    /// <summary>Draws the picture for a control of the given size (also used by tests).</summary>
    internal void DrawMap(SKCanvas c, double w, double h)
    {
        c.Clear(Workspace);
        if (Target is not { } t) return;
        var e = FitView.Extent(t.Map);
        var f = OverviewGeometry.Fit(e, new SizeD(w, h));
        SKRect ToScreen(double x0, double y0, double x1, double y1) =>
            new((float)(f.OffsetX + (x0 - e.X) * f.Scale), (float)(f.OffsetY + (y0 - e.Y) * f.Scale), (float)(f.OffsetX + (x1 - e.X) * f.Scale), (float)(f.OffsetY + (y1 - e.Y) * f.Scale));

        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        foreach (var b in t.Map.Boards)
        {
            var ox = b.Offset.X;
            var oy = b.Offset.Y;
            t.Tiles.TryGetValue(BoardTiler.TileKey(b), out var ts);
            if (ts == null || ts.IsPlaceholder)
            {
                using var blank = new SKPaint { Color = BlankBoard };
                c.DrawRect(ToScreen(ox, oy, ox + b.Width, oy + b.Height), blank);
                continue;
            }
            var level = OverviewGeometry.TileLevel(ts);   // coarsest level: at most 1024 px on its long side
            var sx = ts.Width / (double)ts.LevelWidth(level);
            var sy = ts.Height / (double)ts.LevelHeight(level);
            for (var ty = 0; ty < ts.TilesY(level); ty++)
                for (var tx = 0; tx < ts.TilesX(level); tx++)
                {
                    var img = Tile(ts.TilePath(level, tx, ty));
                    if (img == null) continue;
                    c.DrawImage(img, ToScreen(ox + tx * TileSet.TileSize * sx, oy + ty * TileSet.TileSize * sy,
                        ox + (tx * TileSet.TileSize + img.Width) * sx, oy + (ty * TileSet.TileSize + img.Height) * sy), sampling);
                }
        }

        var v = t.View.VisibleMapRect(TargetSize);
        var r = ToScreen(v.X, v.Y, v.X + v.W, v.Y + v.H);
        using var fill = new SKPaint { Color = new SKColor(0xFF, 0x00, 0x00, 0x20) };
        using var edge = new SKPaint { Color = new SKColor(0xFF, 0x00, 0x00), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        c.Save();
        c.ClipRect(new SKRect(0, 0, (float)w, (float)h));
        c.DrawRect(r, fill);
        c.DrawRect(r, edge);
        c.Restore();
    }

    SKImage? Tile(string path)
    {
        if (_images.TryGetValue(path, out var hit)) return hit;
        SKImage? img = null;
        try { if (File.Exists(path)) img = SKImage.FromEncodedData(path); }
        catch (IOException) { }
        if (img != null) _images[path] = img;   // only a hit is cached: tiles may still be being written
        return img;
    }

    internal void DisposeImages()
    {
        foreach (var img in _images.Values) img?.Dispose();
        _images.Clear();
    }

    // ---------- input ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        PanTo(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) PanTo(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    /// <summary>Centres the target on the map point under a control point.</summary>
    internal void PanTo(Point at)
    {
        try
        {
            if (Target is not { } t) return;
            var fit = OverviewGeometry.Fit(Extent, ControlSize);
            if (fit.Scale <= 0) return;
            var e = Extent;
            t.CentreOn(OverviewGeometry.ToMap(new PointD(at.X, at.Y), fit, new SizeD(e.W, e.H)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("overview pan failed: " + ex.Message);
        }
    }
}
