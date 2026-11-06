using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Quartermaster.App;

/// <summary>MDI child showing one chart image: wheel zoom at the cursor, drag to pan, Fit and 100% buttons.</summary>
sealed class ChartWindow : Form
{
    sealed class ChartCanvas : SKControl
    {
        readonly ModuleSession _s;
        readonly ChartDef _chart;
        readonly Viewport _view = new();
        bool _initial, _panning;
        Point _last;

        public ChartCanvas(ModuleSession s, ChartDef chart)
        {
            _s = s; _chart = chart;
            DoubleBuffered = true;
            BackColor = SystemColors.AppWorkspace;
        }

        SKSize Natural
        {
            get
            {
                var sz = _s.Images.SizeOf(_chart.FileName);
                return new SKSize(Math.Max(1, sz.Width), Math.Max(1, sz.Height));
            }
        }

        public void Fit()
        {
            var n = Natural;
            var c = new SKSize(Math.Max(Width, 1), Math.Max(Height, 1));
            _view.Zoom = Math.Min(c.Width / n.Width, c.Height / n.Height);
            _view.Pan = new SKPoint(n.Width / 2f - c.Width / 2f / _view.Zoom, n.Height / 2f - c.Height / 2f / _view.Zoom);
            Invalidate();
        }

        public void ActualSize()
        {
            var n = Natural;
            var c = new SKSize(Math.Max(Width, 1), Math.Max(Height, 1));
            _view.Zoom = 1f;
            _view.Pan = new SKPoint(n.Width / 2f - c.Width / 2f, n.Height / 2f - c.Height / 2f);
            Invalidate();
        }

        protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
        {
            base.OnPaintSurface(e);
            var c = e.Surface.Canvas;
            c.Clear(new SKColor(SystemColors.AppWorkspace.R, SystemColors.AppWorkspace.G, SystemColors.AppWorkspace.B));
            if (!_initial && e.Info.Width > 1) { _initial = true; Fit(); }
            var img = _s.Images.Get(_chart.FileName, _view.Zoom);
            var n = Natural;
            c.Save();
            c.Scale(_view.Zoom);
            c.Translate(-_view.Pan.X, -_view.Pan.Y);
            using var paint = new SKPaint { IsAntialias = true };
            var sampling = _view.Zoom < 1f ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKFilterMode.Linear);
            c.DrawImage(img, SKRect.Create(0, 0, n.Width, n.Height), sampling, paint);
            c.Restore();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _view.ZoomAt(new SKPoint(e.X, e.Y), e.Delta > 0 ? 1.25f : 0.8f);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button is MouseButtons.Left or MouseButtons.Middle) { _panning = true; _last = e.Location; Capture = true; Cursor = Cursors.SizeAll; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_panning) return;
            _view.Pan = new SKPoint(_view.Pan.X - (e.X - _last.X) / _view.Zoom, _view.Pan.Y - (e.Y - _last.Y) / _view.Zoom);
            _last = e.Location;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_panning) { _panning = false; Capture = false; Cursor = Cursors.Default; }
        }
    }

    readonly ChartCanvas _canvas;

    public ChartWindow(ModuleSession s, ChartDef c)
    {
        Chart = c;
        Text = c.Name;
        ShowIcon = false;
        _canvas = new ChartCanvas(s, c) { Dock = DockStyle.Fill };
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        var fit = new ToolStripButton("Fit");
        fit.Click += (_, _) => _canvas.Fit();
        var actual = new ToolStripButton("100%");
        actual.Click += (_, _) => _canvas.ActualSize();
        bar.Items.AddRange(new ToolStripItem[] { fit, actual });
        Controls.Add(_canvas);
        Controls.Add(bar);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ChartDef Chart { get; }
}
