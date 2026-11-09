using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Desktop.Map;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Documents;

/// <summary>A floating window showing one chart image: wheel zoom at the cursor, drag to pan, Fit and 100% buttons (WinForms ChartWindow).</summary>
public sealed class ChartWindow : Window
{
    readonly ChartCanvas _canvas;
    readonly Button _fit = new() { Content = "Fit" };
    readonly Button _actual = new() { Content = "100%" };

    public ChartWindow(ModuleSession s, ChartDef chart)
    {
        Chart = chart;
        Title = chart.Name;
        ShowInTaskbar = false;
        Width = 640;
        Height = 480;
        _canvas = new ChartCanvas(s, chart);
        _fit.Click += (_, _) => Fit();
        _actual.Click += (_, _) => ActualSize();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(2) };
        bar.Children.Add(_fit);
        bar.Children.Add(_actual);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_canvas);
        Content = dock;
    }

    public ChartDef Chart { get; }

    /// <summary>Zoom and pan of the picture (tests).</summary>
    internal Viewport View => _canvas.View;
    internal Control Canvas => _canvas;
    internal Button FitButton => _fit;
    internal Button ActualButton => _actual;

    /// <summary>Zooms so the whole image shows, centred.</summary>
    public void Fit() => _canvas.Fit();

    /// <summary>Zoom 1 with the image centred.</summary>
    public void ActualSize() => _canvas.ActualSize();

    internal void Wheel(Point at, double delta) => _canvas.Wheel(at, delta);
    internal void DragBegin(Point at) => _canvas.DragBegin(at);
    internal void DragMove(Point at) => _canvas.DragMove(at);
    internal void DragEnd() => _canvas.DragEnd();
    internal bool Panning => _canvas.Panning;
    internal static float DeviceZoom(float zoom, double renderScaling) => ChartCanvas.DeviceZoom(zoom, renderScaling);

    sealed class ChartCanvas : Control
    {
        /// <summary>WinForms SystemColors.AppWorkspace (the classic theme's workspace colour).</summary>
        static readonly SKColor Workspace = new(0xAB, 0xAB, 0xAB);

        readonly ModuleSession _s;
        readonly ChartDef _chart;
        bool _initial;
        Point _last;

        public ChartCanvas(ModuleSession s, ChartDef chart)
        {
            _s = s;
            _chart = chart;
            Focusable = true;
            ClipToBounds = true;
        }

        public Viewport View { get; } = new();
        public bool Panning { get; private set; }

        SKSize Natural
        {
            get
            {
                var sz = _s.Images.SizeOf(_chart.FileName);
                return new SKSize(Math.Max(1, sz.Width), Math.Max(1, sz.Height));
            }
        }

        SKSize ControlSize => new((float)Math.Max(Bounds.Width, 1), (float)Math.Max(Bounds.Height, 1));

        public void Fit() => Fit(ControlSize);

        void Fit(SKSize c)
        {
            var n = Natural;
            View.Zoom = Math.Min(c.Width / n.Width, c.Height / n.Height);
            View.Pan = new SKPoint(n.Width / 2f - c.Width / 2f / View.Zoom, n.Height / 2f - c.Height / 2f / View.Zoom);
            InvalidateVisual();
        }

        public void ActualSize()
        {
            var n = Natural;
            var c = ControlSize;
            View.Zoom = 1f;
            View.Pan = new SKPoint(n.Width / 2f - c.Width / 2f, n.Height / 2f - c.Height / 2f);
            InvalidateVisual();
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var r = base.ArrangeOverride(finalSize);
            if (!_initial && finalSize.Width > 1 && finalSize.Height > 1) { _initial = true; Fit(new SKSize((float)finalSize.Width, (float)finalSize.Height)); }
            return r;
        }

        public override void Render(DrawingContext context)
        {
            if (Bounds.Width < 1 || Bounds.Height < 1) return;
            using var recorder = new SKPictureRecorder();
            var c = recorder.BeginRecording(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
            try { Draw(c); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("chart paint failed: " + ex.Message); }
            context.Custom(new SkiaDrawOperation(recorder.EndRecording(), new Rect(Bounds.Size), ex => System.Diagnostics.Trace.WriteLine("chart replay failed: " + ex.Message)));
        }

        void Draw(SKCanvas c)
        {
            c.Clear(Workspace);
            var device = DeviceZoom(View.Zoom, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
            var img = _s.Images.Get(_chart.FileName, device);
            var n = Natural;
            c.Save();
            c.Scale(View.Zoom);
            c.Translate(-View.Pan.X, -View.Pan.Y);
            using var paint = new SKPaint { IsAntialias = true };
            var sampling = device < 1f ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKFilterMode.Linear);
            c.DrawImage(img, SKRect.Create(0, 0, n.Width, n.Height), sampling, paint);
            c.Restore();
        }

        /// <summary>Image pixels per device pixel: the view zoom (per DIP) times the screen scaling.</summary>
        internal static float DeviceZoom(float zoom, double renderScaling) => zoom * (float)renderScaling;

        public void Wheel(Point at, double delta)
        {
            if (delta == 0) return;
            View.ZoomAt(new SKPoint((float)at.X, (float)at.Y), delta > 0 ? 1.25f : 0.8f);
            InvalidateVisual();
        }

        public void DragBegin(Point at) { Panning = true; _last = at; Cursor = new Cursor(StandardCursorType.SizeAll); }

        public void DragMove(Point at)
        {
            if (!Panning) return;
            View.Pan = new SKPoint(View.Pan.X - (float)(at.X - _last.X) / View.Zoom, View.Pan.Y - (float)(at.Y - _last.Y) / View.Zoom);
            _last = at;
            InvalidateVisual();
        }

        public void DragEnd() { if (!Panning) return; Panning = false; Cursor = null; }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            Wheel(e.GetPosition(this), e.Delta.Y);
            e.Handled = true;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var p = e.GetCurrentPoint(this).Properties;
            if (!p.IsLeftButtonPressed && !p.IsMiddleButtonPressed) return;
            e.Pointer.Capture(this);
            DragBegin(e.GetPosition(this));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            DragMove(e.GetPosition(this));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!Panning) return;
            e.Pointer.Capture(null);
            DragEnd();
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            DragEnd();
        }
    }
}
