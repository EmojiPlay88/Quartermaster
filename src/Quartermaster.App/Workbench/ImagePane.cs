using System.Drawing.Drawing2D;
using Quartermaster.Render.Ocr;

namespace Quartermaster.App;

/// <summary>
/// Shows a scan with zoom (fit, 25–400 %) and pan, and lets the user drag a region (image pixels) with handles to adjust it.
/// Left-drag selects or adjusts; a click outside the region clears it; middle- or right-drag pans; Ctrl+wheel zooms.
/// The bitmap is owned by the caller.
/// </summary>
sealed class ImagePane : ScrollableControl
{
    const int HandleSize = 7;
    Bitmap? _image;
    Rectangle? _region;
    double _zoom = 1;
    bool _fit = true;
    string? _message;

    // drag state
    RegionHandle _drag = RegionHandle.None;
    bool _selecting, _panning;
    Point _downImage, _downView, _panStart;
    Rectangle _startRegion;

    public ImagePane()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        AutoScroll = true;
        BackColor = SystemColors.AppWorkspace;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    public Bitmap? Image => _image;

    /// <summary>The selected region in image pixels (null = the whole image).</summary>
    public Rectangle? SelectedRegion => _region;

    public double Zoom => _zoom;
    public bool FitMode => _fit;

    /// <summary>The user finished dragging or adjusting the region (or cleared it).</summary>
    public event Action<Rectangle?>? SelectionChanged;
    public event Action? ZoomChanged;

    /// <summary>Text shown instead of an image (loading, an error); null hides it.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Message
    {
        get => _message;
        set { _message = value; Invalidate(); }
    }

    public void SetImage(Bitmap? image, Rectangle? region)
    {
        _image = image;
        _region = region;
        _drag = RegionHandle.None;
        _selecting = _panning = false;
        if (Capture) Capture = false;
        if (_fit) ApplyFit(); else UpdateScroll();
        Invalidate();
    }

    /// <summary>Sets the region without raising <see cref="SelectionChanged"/>.</summary>
    public void SetRegion(Rectangle? region)
    {
        _region = region;
        Invalidate();
    }

    /// <summary>Sets the region as a finished drag would (raises <see cref="SelectionChanged"/>); smoke and keyboard use.</summary>
    public void CommitRegion(Rectangle? region)
    {
        _region = region is { } r && _image != null ? Rectangle.Intersect(r, new Rectangle(Point.Empty, _image.Size)) : null;
        if (_region is { Width: <= 0 } or { Height: <= 0 }) _region = null;
        Invalidate();
        SelectionChanged?.Invoke(_region);
    }

    public void FitToWindow()
    {
        _fit = true;
        ApplyFit();
        ZoomChanged?.Invoke();
    }

    /// <summary>Sets the zoom (clamped 25–400 %), keeping the image point under <paramref name="anchor"/> (view px) in place.</summary>
    public void SetZoom(double zoom, Point? anchor = null)
    {
        zoom = ScanSessionModel.ClampZoom(zoom);
        var a = anchor ?? new Point(ClientSize.Width / 2, ClientSize.Height / 2);
        var img = ViewToImageF(a);
        _fit = false;
        _zoom = zoom;
        UpdateScroll();
        if (_image != null) AutoScrollPosition = ScanSessionModel.ScrollForZoom(img, a, _zoom, _image.Size, ClientSize);
        Invalidate();
        ZoomChanged?.Invoke();
    }

    void ApplyFit()
    {
        _zoom = _image == null ? 1 : ScanSessionModel.FitZoom(_image.Size, ClientSize);
        UpdateScroll();
        Invalidate();
    }

    void UpdateScroll()
    {
        AutoScrollMinSize = _image == null ? Size.Empty
            : new Size((int)Math.Ceiling(_image.Width * _zoom), (int)Math.Ceiling(_image.Height * _zoom));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_fit && _image != null) ApplyFit();
    }

    // ---------- coordinates ----------

    /// <summary>Top-left of the image in view pixels (centred when smaller than the view).</summary>
    PointF Origin()
    {
        if (_image == null) return PointF.Empty;
        // AutoScrollPosition is negative when scrolled; the model takes the positive offset.
        return new PointF((float)ScanSessionModel.ImageOrigin(_image.Width, _zoom, ClientSize.Width, -AutoScrollPosition.X),
                          (float)ScanSessionModel.ImageOrigin(_image.Height, _zoom, ClientSize.Height, -AutoScrollPosition.Y));
    }

    PointF ViewToImageF(Point v) => _image == null ? PointF.Empty
        : ScanSessionModel.ViewToImage(v, new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y), _zoom, _image.Size, ClientSize);

    Point ViewToImage(Point v)
    {
        var p = ViewToImageF(v);
        return new Point((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    RectangleF ImageToView(Rectangle r)
    {
        var o = Origin();
        return new RectangleF((float)(o.X + r.X * _zoom), (float)(o.Y + r.Y * _zoom), (float)(r.Width * _zoom), (float)(r.Height * _zoom));
    }

    // ---------- painting ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        if (_image == null || _message != null)
        {
            var text = _message ?? "";
            if (text.Length > 0)
                TextRenderer.DrawText(g, text, Font, new Rectangle(12, 12, Math.Max(1, ClientSize.Width - 24), Math.Max(1, ClientSize.Height - 24)),
                    SystemColors.ControlText, TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
            if (_image == null) return;
        }
        try
        {
            var dest = ImageToView(new Rectangle(Point.Empty, _image.Size));
            g.InterpolationMode = _zoom >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            // Draw only the visible part (large scans).
            var vis = RectangleF.Intersect(dest, e.ClipRectangle);
            if (vis.Width > 0 && vis.Height > 0)
            {
                var src = new RectangleF((float)((vis.X - dest.X) / _zoom), (float)((vis.Y - dest.Y) / _zoom), (float)(vis.Width / _zoom), (float)(vis.Height / _zoom));
                g.DrawImage(_image, vis, src, GraphicsUnit.Pixel);
            }
            if (_region is { } r)
            {
                var rv = ImageToView(r);
                using (var dim = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                using (var outside = new System.Drawing.Region(dest))
                {
                    outside.Exclude(rv);
                    g.FillRegion(dim, outside);
                }
                using var pen = new Pen(Color.FromArgb(0, 102, 204), 2);
                g.DrawRectangle(pen, rv.X, rv.Y, rv.Width, rv.Height);
                foreach (var h in Handles(rv))
                {
                    g.FillRectangle(Brushes.White, h);
                    g.DrawRectangle(Pens.Black, h.X, h.Y, h.Width, h.Height);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            // GDI+ refuses some draws (huge zoomed images, locked display): skip the frame rather than crash.
        }
    }

    static IEnumerable<RectangleF> Handles(RectangleF r)
    {
        float s = HandleSize, hs = s / 2;
        foreach (var x in new[] { r.Left, r.Left + r.Width / 2, r.Right })
            foreach (var y in new[] { r.Top, r.Top + r.Height / 2, r.Bottom })
                if (!(x == r.Left + r.Width / 2 && y == r.Top + r.Height / 2))
                    yield return new RectangleF(x - hs, y - hs, s, s);
    }

    // ---------- mouse ----------

    double Tolerance => (HandleSize / 2.0 + 2) / _zoom;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_image == null) return;
        if (e.Button is MouseButtons.Middle or MouseButtons.Right)
        {
            _panning = true;
            _panStart = new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y);
            _downView = e.Location;
            Cursor = Cursors.SizeAll;
            Capture = true;
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _downView = e.Location;
        _downImage = ViewToImage(e.Location);
        _drag = _region is { } r ? RegionEdit.HitTest(r, ViewToImageF(e.Location), Tolerance) : RegionHandle.None;
        _startRegion = _region ?? Rectangle.Empty;
        _selecting = _drag == RegionHandle.None;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_image == null) return;
        if (_panning)
        {
            AutoScrollPosition = new Point(_panStart.X - (e.X - _downView.X), _panStart.Y - (e.Y - _downView.Y));
            Invalidate();
            return;
        }
        var p = ViewToImage(e.Location);
        if (_selecting && e.Button == MouseButtons.Left)
        {
            if (Math.Abs(e.X - _downView.X) + Math.Abs(e.Y - _downView.Y) < 3) return;
            _region = RegionEdit.FromDrag(_downImage, p, _image.Size);
            Invalidate();
            return;
        }
        if (_drag != RegionHandle.None && e.Button == MouseButtons.Left)
        {
            _region = RegionEdit.Adjust(_startRegion, _drag, p.X - _downImage.X, p.Y - _downImage.Y, _image.Size);
            Invalidate();
            return;
        }
        Cursor = (_region is { } r ? RegionEdit.HitTest(r, ViewToImageF(e.Location), Tolerance) : RegionHandle.None) switch
        {
            RegionHandle.N or RegionHandle.S => Cursors.SizeNS,
            RegionHandle.E or RegionHandle.W => Cursors.SizeWE,
            RegionHandle.NW or RegionHandle.SE => Cursors.SizeNWSE,
            RegionHandle.NE or RegionHandle.SW => Cursors.SizeNESW,
            RegionHandle.Move => Cursors.SizeAll,
            _ => Cursors.Cross,
        };
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Capture = false;
        if (_panning) { _panning = false; Cursor = Cursors.Cross; return; }
        if (_image == null || e.Button != MouseButtons.Left) return;
        if (_selecting)
        {
            _selecting = false;
            var moved = Math.Abs(e.X - _downView.X) + Math.Abs(e.Y - _downView.Y) >= 3;
            _region = moved ? RegionEdit.FromDrag(_downImage, ViewToImage(e.Location), _image.Size) : null; // a click clears
            Invalidate();
            var before = _startRegion.IsEmpty ? (Rectangle?)null : _startRegion;
            if (_region != before) SelectionChanged?.Invoke(_region); // a click that changes nothing does not cancel a read
            return;
        }
        if (_drag != RegionHandle.None)
        {
            var changed = _region != _startRegion;
            _drag = RegionHandle.None;
            if (changed) SelectionChanged?.Invoke(_region);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0 && _image != null)
        {
            SetZoom(ScanSessionModel.StepZoom(_zoom, e.Delta > 0 ? 1 : -1), e.Location);
            return;
        }
        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Escape || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && _region != null) { CommitRegion(null); e.Handled = true; }
    }
}
