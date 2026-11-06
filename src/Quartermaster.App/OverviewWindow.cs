using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Quartermaster.App;

/// <summary>Small mini-map of the most recently active map window: coarsest tile level plus a red rectangle for what that window shows. Click or drag pans the map.</summary>
sealed class OverviewWindow : Form
{
    readonly MainForm _host;
    readonly SKControl _view = new() { Dock = DockStyle.Fill, BackColor = SystemColors.AppWorkspace };
    readonly Dictionary<string, SKImage?> _images = new();
    MapWindow? _target;
    bool _dragging;

    public OverviewWindow(MainForm host)
    {
        _host = host;
        Text = "Overview";
        ShowIcon = false;
        ClientSize = new Size(240, 200);
        Controls.Add(_view);
        _view.PaintSurface += OnPaintSurface;
        _view.MouseDown += OnMouseDown;
        _view.MouseMove += OnMouseMove;
        _view.MouseUp += (_, _) => _dragging = false;
        _host.ActiveMapChanged += OnActiveMapChanged;
        SetTarget(_host.LastActiveMap);
    }

    public MapWindow? Target => _target;

    void OnActiveMapChanged() => SetTarget(_host.LastActiveMap);

    void SetTarget(MapWindow? map)
    {
        if (ReferenceEquals(map, _target)) return;
        Unhook();
        _target = map != null && !map.IsDisposed ? map : null;
        if (_target != null)
        {
            _target.Canvas.ViewportChanged += OnViewportChanged;
            _target.FormClosed += OnTargetClosed;
        }
        Text = _target == null ? "Overview" : "Overview - " + _target.Map.Name;
        Redraw();
    }

    void Unhook()
    {
        if (_target == null) return;
        _target.Canvas.ViewportChanged -= OnViewportChanged;
        _target.FormClosed -= OnTargetClosed;
        _target = null;
    }

    void OnTargetClosed(object? sender, FormClosedEventArgs e)
    {
        Unhook();
        BeginInvoke(() => { if (!IsDisposed) SetTarget(_host.LastActiveMap); });
    }

    void OnViewportChanged()
    {
        if (IsDisposed) return;
        Redraw();
    }

    void Redraw()
    {
        if (!IsDisposed && _view.IsHandleCreated) _view.Invalidate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _host.ActiveMapChanged -= OnActiveMapChanged;
        Unhook();
        foreach (var img in _images.Values) img?.Dispose();
        _images.Clear();
        base.OnFormClosed(e);
    }

    // ---------- geometry: map extent fitted into the control ----------

    /// <summary>Scale and top-left of the fitted map extent, in control pixels.</summary>
    (double Scale, double X, double Y, RectD Extent) Fit(int w, int h)
    {
        var e = _target != null ? FitView.Extent(_target.Map) : new RectD(0, 0, 1, 1);
        var s = Math.Min(Math.Max(w - 8, 1) / e.W, Math.Max(h - 8, 1) / e.H);
        return (s, (w - e.W * s) / 2, (h - e.H * s) / 2, e);
    }

    // ---------- painting ----------

    void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        try
        {
            DrawMap(e.Surface.Canvas, e.Info.Width, e.Info.Height);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("overview paint failed: " + ex.Message);
        }
    }

    internal void DrawMap(SKCanvas c, int w, int h)
    {
        c.Clear(new SKColor(SystemColors.AppWorkspace.R, SystemColors.AppWorkspace.G, SystemColors.AppWorkspace.B));
        if (_target == null) return;
        var (s, fx, fy, ext) = Fit(w, h);
        SKRect ToScreen(double x0, double y0, double x1, double y1) =>
            new((float)(fx + (x0 - ext.X) * s), (float)(fy + (y0 - ext.Y) * s), (float)(fx + (x1 - ext.X) * s), (float)(fy + (y1 - ext.Y) * s));

        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        foreach (var b in _target.Map.Boards)
        {
            var ox = b.Offset.X;
            var oy = b.Offset.Y;
            _target.Canvas.Tiles.TryGetValue(BoardTiler.TileKey(b), out var ts);
            if (ts == null || ts.IsPlaceholder)
            {
                using var blank = new SKPaint { Color = new SKColor(0xC0, 0xC0, 0xC0) };
                c.DrawRect(ToScreen(ox, oy, ox + b.Width, oy + b.Height), blank);
                continue;
            }
            var level = ts.Levels; // coarsest level: at most 1024 px on its long side
            var sx = ts.Width / (double)ts.LevelWidth(level);
            var sy = ts.Height / (double)ts.LevelHeight(level);
            for (var ty = 0; ty < ts.TilesY(level); ty++)
                for (var tx = 0; tx < ts.TilesX(level); tx++)
                {
                    var img = Tile(ts.TilePath(level, tx, ty));
                    if (img == null) continue;
                    var dest = ToScreen(ox + tx * TileSet.TileSize * sx, oy + ty * TileSet.TileSize * sy,
                        ox + (tx * TileSet.TileSize + img.Width) * sx, oy + (ty * TileSet.TileSize + img.Height) * sy);
                    c.DrawImage(img, dest, sampling);
                }
        }

        var v = _target.Canvas.View.VisibleMapRect(new SKSize(Math.Max(_target.Canvas.Width, 1), Math.Max(_target.Canvas.Height, 1)));
        var r = ToScreen(v.X, v.Y, v.X + v.W, v.Y + v.H);
        using var fill = new SKPaint { Color = new SKColor(0xFF, 0x00, 0x00, 0x20) };
        using var edge = new SKPaint { Color = new SKColor(0xFF, 0x00, 0x00), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        c.Save();
        c.ClipRect(new SKRect(0, 0, w, h));
        c.DrawRect(r, fill);
        c.DrawRect(r, edge);
        c.Restore();
    }

    SKImage? Tile(string path)
    {
        if (_images.TryGetValue(path, out var hit)) return hit;
        SKImage? img = null;
        try
        {
            if (File.Exists(path)) img = SKImage.FromEncodedData(path);
        }
        catch (IOException) { }
        // Only cache a hit: tiles may still be being written.
        if (img != null) _images[path] = img;
        return img;
    }

    // ---------- input ----------

    void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        PanTo(e.Location);
    }

    void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragging && e.Button == MouseButtons.Left) PanTo(e.Location);
    }

    void PanTo(Point at)
    {
        try
        {
            if (_target == null) return;
            var (s, fx, fy, ext) = Fit(_view.Width, _view.Height);
            if (s <= 0) return;
            var p = new PointD(ext.X + (at.X - fx) / s, ext.Y + (at.Y - fy) / s);
            _target.Canvas.CentreOn(new PointD(Math.Clamp(p.X, ext.X, ext.X + ext.W), Math.Clamp(p.Y, ext.Y, ext.Y + ext.H)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("overview pan failed: " + ex.Message);
        }
    }

    /// <summary>Smoke: pans the target as a click at the control's centre would, and reports whether the viewport moved.</summary>
    internal bool SmokeClick(double fx, double fy)
    {
        if (_target == null) return false;
        var before = _target.Canvas.View.Pan;
        var (s, ox, oy, ext) = Fit(_view.Width, _view.Height);
        var p = new PointD(ext.X + (fx * _view.Width - ox) / s, ext.Y + (fy * _view.Height - oy) / s);
        _target.Canvas.CentreOn(p);
        return _target.Canvas.View.Pan != before;
    }

    public void SaveScreenshot(string path)
    {
        using var bmp = new SKBitmap(Math.Max(_view.Width, 1), Math.Max(_view.Height, 1));
        using (var c = new SKCanvas(bmp)) DrawMap(c, bmp.Width, bmp.Height);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        Program.AppLog("smoke: overview screenshot saved to " + path);
    }
}
