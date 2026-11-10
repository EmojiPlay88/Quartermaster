using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Render.Ocr;
using SkiaSharp;
using SDPoint = System.Drawing.Point;
using SDPointF = System.Drawing.PointF;
using SDRect = System.Drawing.Rectangle;
using SDSize = System.Drawing.Size;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Shows a scan with zoom (fit, 25–400 %) and pan, and lets the user drag a region (image pixels) with handles to adjust it.
/// Left-drag selects or adjusts; a click outside the region clears it; middle- or right-drag pans; Ctrl+wheel (Cmd on macOS) zooms
/// at the cursor, the plain wheel scrolls. Scrollbars appear when the zoomed image is larger than the view.
/// The bitmap is owned by the caller: the pane never disposes it. The caller must not change its pixels while it is shown; disposing it
/// is safe (the pane keeps its own reference to the pixel buffer for frames still in flight).
/// Zoom is device pixels per image pixel, so the image is drawn at zoom x render scaling in device-independent units.
/// </summary>
public sealed class ImagePane : Panel
{
    const int HandleSize = 7;
    const int WheelStep = 48; // px per wheel notch
    static readonly SKColor Accent = new(0, 102, 204);

    SKBitmap? _image;
    SDSize _imageSize; // read once: the caller may dispose the bitmap while it is still the pane's image
    SKImage? _skImage; // wraps the bitmap's pixels (no copy, own reference); see PinnedImage. Replaced in SetImage.
    SDRect? _region;
    double _zoom = 1;
    bool _fit = true;
    string? _message;
    SDPoint _scroll; // positive offset in view px

    RegionHandle _drag = RegionHandle.None;
    bool _selecting, _panning;
    SDPoint _downImage, _downView, _panStart;
    SDRect _startRegion;

    static readonly Cursor CrossCursor = new(StandardCursorType.Cross), MoveCursor = new(StandardCursorType.SizeAll),
        NsCursor = new(StandardCursorType.SizeNorthSouth), WeCursor = new(StandardCursorType.SizeWestEast),
        NwseCursor = new(StandardCursorType.TopLeftCorner), NeswCursor = new(StandardCursorType.TopRightCorner);

    const double BarThickness = 16; // DIPs
    readonly ScrollBar _hBar = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Height = BarThickness, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, IsVisible = false, Focusable = false };
    readonly ScrollBar _vBar = new() { Orientation = Avalonia.Layout.Orientation.Vertical, Width = BarThickness, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch, IsVisible = false, Focusable = false };
    bool _syncingBars;
    readonly Surface _surface;

    // Panel.Render is sealed: the picture is drawn by this child, which fills the pane.
    sealed class Surface(ImagePane owner) : Control
    {
        public override void Render(DrawingContext context) => owner.Draw(context);
    }

    internal void Redraw()
    {
        InvalidateVisual();
        _surface?.InvalidateVisual();
    }

    public ImagePane()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = CrossCursor;
        Background = Brushes.Transparent; // so the pane gets the pointer everywhere
        _surface = new Surface(this) { IsHitTestVisible = false };
        Children.Add(_surface);
        Children.Add(_hBar);
        Children.Add(_vBar);
        _hBar.ValueChanged += (_, _) => BarMoved();
        _vBar.ValueChanged += (_, _) => BarMoved();
    }

    void BarMoved()
    {
        if (_syncingBars) return;
        _scroll = new SDPoint((int)Math.Round(_hBar.Value), (int)Math.Round(_vBar.Value));
        ClampScroll();
        Redraw();
    }

    /// <summary>The zoomed image size in view px: what the scrollbars scroll over.</summary>
    public SDSize ScrollExtent => _image == null ? SDSize.Empty : Content();

    /// <summary>The scroll position in view px.</summary>
    public SDPoint ScrollOffset => _scroll;

    public bool HasHorizontalScrollBar => _hOn;
    public bool HasVerticalScrollBar => _vOn;
    bool _hOn, _vOn;

    SDSize Content() => new((int)Math.Ceiling(_imageSize.Width * _zoom - 1e-6), (int)Math.Ceiling(_imageSize.Height * _zoom - 1e-6));

    SDSize FullSize => new((int)Math.Round(Bounds.Width * Scaling), (int)Math.Round(Bounds.Height * Scaling));

    /// <summary>Which scrollbars the zoomed image needs and the view left over (view px), as an AutoScroll panel works it out.</summary>
    (bool H, bool V, SDSize View) Metrics()
    {
        var full = FullSize;
        if (_image == null) return (false, false, full);
        var c = Content();
        int bar = (int)Math.Round(BarThickness * Scaling);
        bool h = false, v = false;
        for (int i = 0; i < 3; i++)
        {
            bool nh = c.Width > full.Width - (v ? bar : 0), nv = c.Height > full.Height - (h ? bar : 0);
            if (nh == h && nv == v) break;
            h = nh; v = nv;
        }
        return (h, v, new SDSize(Math.Max(1, full.Width - (v ? bar : 0)), Math.Max(1, full.Height - (h ? bar : 0))));
    }

    /// <summary>Shows or hides the scrollbars and syncs their ranges to the zoom and scroll position.</summary>
    void SyncBars()
    {
        var (h, v, view) = Metrics();
        ClampScroll();
        _syncingBars = true;
        try
        {
            _hOn = h;
            _vOn = v;
            // ScrollBar.Visibility decides its own IsVisible (Auto hides a bar with nothing to scroll): force it.
            _hBar.Visibility = h ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden;
            _vBar.Visibility = v ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden;
            _hBar.IsVisible = h;
            _vBar.IsVisible = v;
            _hBar.Margin = new Thickness(0, 0, v ? BarThickness : 0, 0);
            _vBar.Margin = new Thickness(0, 0, 0, h ? BarThickness : 0);
            if (_image != null)
            {
                var c = Content();
                _hBar.Minimum = 0; _hBar.Maximum = Math.Max(0, c.Width - view.Width); _hBar.ViewportSize = view.Width; _hBar.Value = _scroll.X;
                _hBar.SmallChange = 48; _hBar.LargeChange = view.Width;
                _vBar.Minimum = 0; _vBar.Maximum = Math.Max(0, c.Height - view.Height); _vBar.ViewportSize = view.Height; _vBar.Value = _scroll.Y;
                _vBar.SmallChange = 48; _vBar.LargeChange = view.Height;
            }
        }
        finally { _syncingBars = false; }
    }

    public SKBitmap? Image => _image;

    /// <summary>The selected region in image pixels (null = the whole image).</summary>
    public SDRect? SelectedRegion => _region;

    public double Zoom => _zoom;
    public bool FitMode => _fit;

    /// <summary>The user finished dragging or adjusting the region (or cleared it).</summary>
    public event Action<SDRect?>? SelectionChanged;
    public event Action? ZoomChanged;

    /// <summary>Text shown instead of an image (loading, an error); null hides it.</summary>
    public string? Message
    {
        get => _message;
        set { _message = value; Redraw(); }
    }

    double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>The image view size in device pixels (the pane minus any scrollbars).</summary>
    SDSize ViewSize => Metrics().View;

    SDSize ImageSize => _imageSize;

    public void SetImage(SKBitmap? image, SDRect? region)
    {
        _skImage?.Dispose();
        _skImage = null;
        _image = image;
        _imageSize = image == null ? SDSize.Empty : new SDSize(image.Width, image.Height);
        _skImage = image == null ? null : PinnedImage(image); // now, while the pixels are certainly alive
        _region = region;
        _drag = RegionHandle.None;
        _selecting = _panning = false;
        _scroll = SDPoint.Empty;
        if (_fit) ApplyFit();
        else SyncBars();
        Redraw();
    }

    /// <summary>Sets the region without raising <see cref="SelectionChanged"/>.</summary>
    public void SetRegion(SDRect? region)
    {
        _region = region;
        Redraw();
    }

    /// <summary>Sets the region as a finished drag would (raises <see cref="SelectionChanged"/>); smoke and keyboard use.</summary>
    public void CommitRegion(SDRect? region)
    {
        _region = region is { } r && _image != null ? SDRect.Intersect(r, new SDRect(SDPoint.Empty, ImageSize)) : null;
        if (_region is { Width: <= 0 } or { Height: <= 0 }) _region = null;
        Redraw();
        SelectionChanged?.Invoke(_region);
    }

    public void FitToWindow()
    {
        _fit = true;
        ApplyFit();
        ZoomChanged?.Invoke();
    }

    /// <summary>Sets the zoom (clamped 25–400 %), keeping the image point under <paramref name="anchor"/> (view DIPs) in place.</summary>
    public void SetZoom(double zoom, Point? anchor = null)
    {
        zoom = ScanSessionModel.ClampZoom(zoom);
        var view = ViewSize;
        var a = anchor is { } p ? new SDPoint((int)Math.Round(p.X * Scaling), (int)Math.Round(p.Y * Scaling)) : new SDPoint(view.Width / 2, view.Height / 2);
        var img = ViewToImageF(a);
        _fit = false;
        _zoom = zoom;
        _scroll = _image == null ? SDPoint.Empty : ScanSessionModel.ScrollForZoom(img, a, _zoom, ImageSize, ViewSize);
        SyncBars();
        Redraw();
        ZoomChanged?.Invoke();
    }

    void ApplyFit()
    {
        _zoom = _image == null ? 1 : ScanSessionModel.FitZoom(ImageSize, FullSize);
        _scroll = SDPoint.Empty;
        SyncBars();
        Redraw();
    }

    void ClampScroll()
    {
        if (_image == null) { _scroll = SDPoint.Empty; return; }
        var view = ViewSize;
        var c = Content();
        int maxX = Math.Max(0, c.Width - view.Width);
        int maxY = Math.Max(0, c.Height - view.Height);
        _scroll = new SDPoint(Math.Clamp(_scroll.X, 0, maxX), Math.Clamp(_scroll.Y, 0, maxY));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _image != null)
        {
            if (_fit) ApplyFit(); else SyncBars();
        }
    }

    // ---------- coordinates ----------

    SDPointF Origin() => _image == null ? SDPointF.Empty
        : new SDPointF((float)ScanSessionModel.ImageOrigin(_imageSize.Width, _zoom, ViewSize.Width, _scroll.X),
                       (float)ScanSessionModel.ImageOrigin(_imageSize.Height, _zoom, ViewSize.Height, _scroll.Y));

    SDPointF ViewToImageF(SDPoint v) => _image == null ? SDPointF.Empty : ScanSessionModel.ViewToImage(v, _scroll, _zoom, ImageSize, ViewSize);

    SDPoint ViewToImage(SDPoint v)
    {
        var p = ViewToImageF(v);
        return new SDPoint((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    SKRect ImageToView(SDRect r)
    {
        var o = Origin();
        return new SKRect((float)(o.X + r.X * _zoom), (float)(o.Y + r.Y * _zoom), (float)(o.X + r.Right * _zoom), (float)(o.Y + r.Bottom * _zoom));
    }

    SDPoint ToView(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var s = Scaling;
        return new SDPoint((int)Math.Round(p.X * s), (int)Math.Round(p.Y * s));
    }

    // ---------- painting ----------

    static readonly IBrush Workspace = new SolidColorBrush(Color.FromRgb(171, 171, 171));

    void Draw(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Workspace, bounds);
        var vs = ViewSize;
        var viewBounds = new Rect(0, 0, vs.Width / Scaling, vs.Height / Scaling);
        if (_image == null || _message != null)
        {
            var text = _message ?? "";
            if (text.Length > 0)
            {
                var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.Black)
                {
                    MaxTextWidth = Math.Max(1, Bounds.Width - 24),
                    MaxTextHeight = Math.Max(1, Bounds.Height - 24),
                };
                context.DrawText(ft, new Point(12, 12));
            }
            if (_image == null) return;
        }
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        // Zero-copy and pinned: see PinnedImage (SKImage.FromBitmap would copy a mutable bitmap: +4 bytes per pixel on a big scan).
        if (_skImage == null) return;
        using var recorder = new SKPictureRecorder();
        var c = recorder.BeginRecording(new SKRect(0, 0, (float)viewBounds.Width, (float)viewBounds.Height));
        try { Scene(c); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* skip the frame rather than crash */ }
        context.Custom(new SkiaDrawOperation(recorder.EndRecording(), viewBounds, _ => { }));
    }

    /// <summary>
    /// An image over the bitmap's pixels without copying them. It holds its own reference to the pixel buffer (a subset bitmap shares the
    /// buffer, which is reference counted), released when the last picture or image using it dies: a queued frame that still draws the
    /// old image stays valid even if the caller disposes the bitmap right after SetImage.
    /// </summary>
    static SKImage? PinnedImage(SKBitmap bitmap)
    {
        var pin = new SKBitmap();
        if (!bitmap.ExtractSubset(pin, new SKRectI(0, 0, bitmap.Width, bitmap.Height))) { pin.Dispose(); return null; }
        var image = SKImage.FromPixels(pin.PeekPixels(), static (_, ctx) => ((SKBitmap)ctx!).Dispose(), pin);
        if (image == null) pin.Dispose();
        return image;
    }

    void Scene(SKCanvas c)
    {
        // Work in device pixels (zoom is per device pixel); the canvas is in DIPs.
        float inv = (float)(1 / Scaling);
        c.Scale(inv, inv);
        var dest = ImageToView(new SDRect(SDPoint.Empty, ImageSize));
        var sampling = _zoom >= 1 ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        c.DrawImage(_skImage, dest, sampling);
        if (_region is not { } r) return;
        var rv = ImageToView(r);
        // Dim the image outside the region: four bands around it.
        rv.Intersect(dest);
        using (var dim = new SKPaint { Color = new SKColor(0, 0, 0, 70) })
        {
            c.DrawRect(new SKRect(dest.Left, dest.Top, dest.Right, rv.Top), dim);
            c.DrawRect(new SKRect(dest.Left, rv.Bottom, dest.Right, dest.Bottom), dim);
            c.DrawRect(new SKRect(dest.Left, rv.Top, rv.Left, rv.Bottom), dim);
            c.DrawRect(new SKRect(rv.Right, rv.Top, dest.Right, rv.Bottom), dim);
        }
        using var pen = new SKPaint { Color = Accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = false };
        c.DrawRect(rv, pen);
        using var fill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        using var line = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        foreach (var h in Handles(rv))
        {
            c.DrawRect(h, fill);
            c.DrawRect(h, line);
        }
    }

    static IEnumerable<SKRect> Handles(SKRect r)
    {
        float s = HandleSize, hs = s / 2;
        foreach (var x in new[] { r.Left, r.Left + r.Width / 2, r.Right })
            foreach (var y in new[] { r.Top, r.Top + r.Height / 2, r.Bottom })
                if (!(x == r.Left + r.Width / 2 && y == r.Top + r.Height / 2))
                    yield return new SKRect(x - hs, y - hs, x + hs, y + hs);
    }

    // ---------- pointer ----------

    double Tolerance => (HandleSize / 2.0 + 2) / _zoom;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_image == null) return;
        var props = e.GetCurrentPoint(this).Properties;
        var view = ToView(e);
        var vs = ViewSize;
        if (view.X >= vs.Width || view.Y >= vs.Height) return; // on a scrollbar
        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed)
        {
            _panning = true;
            _panStart = _scroll;
            _downView = view;
            Cursor = MoveCursor;
            Grab(e.Pointer);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        _downView = view;
        _downImage = ViewToImage(view);
        _drag = _region is { } r ? RegionEdit.HitTest(r, ViewToImageF(view), Tolerance) : RegionHandle.None;
        _startRegion = _region ?? SDRect.Empty;
        _selecting = _drag == RegionHandle.None;
        Grab(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_image == null) return;
        var view = ToView(e);
        if (_panning)
        {
            _scroll = new SDPoint(_panStart.X - (view.X - _downView.X), _panStart.Y - (view.Y - _downView.Y));
            SyncBars();
            Redraw();
            return;
        }
        var p = ViewToImage(view);
        if (_selecting)
        {
            if (Math.Abs(view.X - _downView.X) + Math.Abs(view.Y - _downView.Y) < 3) return;
            _region = RegionEdit.FromDrag(_downImage, p, ImageSize);
            Redraw();
            return;
        }
        if (_drag != RegionHandle.None)
        {
            _region = RegionEdit.Adjust(_startRegion, _drag, p.X - _downImage.X, p.Y - _downImage.Y, ImageSize);
            Redraw();
            return;
        }
        var hit = _region is { } r ? RegionEdit.HitTest(r, ViewToImageF(view), Tolerance) : RegionHandle.None;
        Cursor = hit switch
        {
            RegionHandle.N or RegionHandle.S => NsCursor,
            RegionHandle.E or RegionHandle.W => WeCursor,
            RegionHandle.NW or RegionHandle.SE => NwseCursor,
            RegionHandle.NE or RegionHandle.SW => NeswCursor,
            RegionHandle.Move => MoveCursor,
            _ => CrossCursor,
        };
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        try { Released(e); }
        finally { e.Pointer.Capture(null); } // after the gesture state is settled: releasing capture raises capture-lost, which abandons a gesture
    }

    void Released(PointerReleasedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            Cursor = CrossCursor;
            return;
        }
        if (_image == null || e.InitialPressMouseButton != MouseButton.Left) return;
        var view = ToView(e);
        if (_selecting)
        {
            _selecting = false;
            var moved = Math.Abs(view.X - _downView.X) + Math.Abs(view.Y - _downView.Y) >= 3;
            _region = moved ? RegionEdit.FromDrag(_downImage, ViewToImage(view), ImageSize) : null; // a click clears
            Redraw();
            var before = _startRegion.IsEmpty ? (SDRect?)null : _startRegion;
            if (_region != before) SelectionChanged?.Invoke(_region); // a click that changes nothing does not cancel a read
            return;
        }
        if (_drag != RegionHandle.None)
        {
            // The last move may not have reached the release point.
            var p = ViewToImage(view);
            _region = RegionEdit.Adjust(_startRegion, _drag, p.X - _downImage.X, p.Y - _downImage.Y, ImageSize);
            var changed = _region != _startRegion;
            _drag = RegionHandle.None;
            Redraw();
            if (changed) SelectionChanged?.Invoke(_region);
        }
    }

    // Capturing again while already captured would raise a capture-lost on this very control and end the gesture.
    void Grab(IPointer pointer)
    {
        if (!ReferenceEquals(pointer.Captured, this)) pointer.Capture(this);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (ReferenceEquals(e.Pointer.Captured, this)) return; // still ours
        // Capture lost mid-gesture (window deactivated, popup): abandon it and put the region back as it was.
        if (_selecting || _drag != RegionHandle.None) _region = _startRegion.IsEmpty ? null : _startRegion;
        _selecting = false;
        _drag = RegionHandle.None;
        _panning = false;
        Cursor = CrossCursor;
        Redraw();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_image == null) return;
        var primary = ShortcutGestures.IsMac ? KeyModifiers.Meta : KeyModifiers.Control;
        if ((e.KeyModifiers & primary) != 0)
        {
            if (e.Delta.Y != 0) SetZoom(ScanSessionModel.StepZoom(_zoom, e.Delta.Y > 0 ? 1 : -1), e.GetPosition(this));
            e.Handled = true;
            return;
        }
        // Plain wheel scrolls (Shift: sideways).
        double dy = e.Delta.Y, dx = e.Delta.X;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && dx == 0) (dx, dy) = (dy, 0);
        _scroll = new SDPoint(_scroll.X - (int)Math.Round(dx * WheelStep), _scroll.Y - (int)Math.Round(dy * WheelStep));
        SyncBars();
        Redraw();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _region != null) { CommitRegion(null); e.Handled = true; }
    }
}
