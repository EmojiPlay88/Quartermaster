using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Render;
using SkiaSharp;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// The big hover preview of one counter (WinForms CounterPreviewTip): the counter at <see cref="CounterImages.PreviewSize"/> (about 200 px on
/// its long side) with the unit's name under it, in the tooltip colours, 16 px below right of the cursor and flipped at the screen edges
/// (<see cref="PreviewPlacement"/>). The picture is rendered fresh at that size on a worker so it stays sharp. It never takes focus or the
/// pointer. Only pieces in <see cref="ModuleSession.View"/> are shown. <see cref="Show"/> on hover, <see cref="Hide"/> on leave, scroll or
/// click. UI thread only; never throws.
/// </summary>
public sealed class CounterPreview : IDisposable
{
    const double Pad = 6;
    static readonly IBrush Info = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xE1));   // SystemColors.Info
    static readonly IBrush InfoText = Brushes.Black;                                       // SystemColors.InfoText

    readonly ModuleSession _s;
    readonly Popup _popup;
    readonly Image _image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
    readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        Foreground = InfoText, FontSize = 12, Margin = new Thickness(0, Pad, 0, 0), IsHitTestVisible = false,
    };
    readonly Border _box;
    Bitmap? _bmp;
    string? _key;     // what is shown or being rendered; null when hidden
    int _generation;  // written on the UI thread, read by the render task
    volatile bool _stopped;
    readonly HashSet<Task> _renders = new();   // every render not finished yet (a superseded one may still be drawing); waited for by Dispose
    int _renderCount;

    public CounterPreview(ModuleSession s)
    {
        _s = s;
        _box = new Border
        {
            Background = Info, BorderBrush = InfoText, BorderThickness = new Thickness(1), Padding = new Thickness(Pad - 1),
            IsHitTestVisible = false, Focusable = false,
            Child = new StackPanel { Children = { _image, _text }, IsHitTestVisible = false },
        };
        _popup = new Popup
        {
            Child = _box, IsLightDismissEnabled = false, Focusable = false, IsHitTestVisible = false, ShouldUseOverlayLayer = false,
            Placement = PlacementMode.AnchorAndGravity, PlacementAnchor = PopupAnchor.TopLeft, PlacementGravity = PopupGravity.BottomRight,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.None,
        };
    }

    /// <summary>True when the preview is on screen.</summary>
    public bool IsOpen => _popup.IsOpen;
    /// <summary>The unit's name shown under the picture (tests).</summary>
    public string? Label { get; private set; }
    /// <summary>The piece shown (tests).</summary>
    public string? PieceId { get; private set; }
    /// <summary>The picture's size in pixels (tests).</summary>
    public PixelSize PictureSize => _bmp?.PixelSize ?? default;
    /// <summary>Where the preview's top-left was put, in screen pixels (tests).</summary>
    public PixelPoint ScreenPosition { get; private set; }
    /// <summary>Tests: when set, a render task waits for it before it starts drawing.</summary>
    internal ManualResetEventSlim? RenderGate { get; set; }
    /// <summary>True once <see cref="Dispose"/> ran (tests).</summary>
    internal bool Stopped => _stopped;
    /// <summary>Render tasks not finished yet (tests).</summary>
    internal int InFlight { get { lock (_renders) return _renders.Count(t => !t.IsCompleted); } }
    /// <summary>Renders actually started (tests).</summary>
    internal int Renders => Volatile.Read(ref _renderCount);

    /// <summary>Shows the piece's preview near <paramref name="pointer"/> (coordinates of <paramref name="anchor"/>). The same piece, state and
    /// name as already shown: nothing happens (it does not follow the cursor, as WinForms).</summary>
    public void Show(Control anchor, Point pointer, string pieceId, string label)
    {
        try
        {
            if (_stopped || _s.Disposed) return;
            if (_s.View?.Pieces.TryGetValue(pieceId, out var piece) != true || piece == null) { Hide(); return; }   // hidden or gone
            var key = CounterImages.Key(piece, default) + "\u001f" + label;
            if (key == _key) return;
            Hide();
            if (TopLevel.GetTopLevel(anchor) is not { } top) return;
            _key = key;
            var gen = Interlocked.Increment(ref _generation);
            var copy = new PieceInstance { Id = piece.Id, TypeString = piece.TypeString, TraitStates = new List<string>(piece.TraitStates), Moved = piece.Moved };
            var cursor = anchor.PointToScreen(pointer);
            var screen = top.Screens?.ScreenFromPoint(cursor) ?? top.Screens?.ScreenFromVisual(anchor) ?? top.Screens?.Primary;
            var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            var scaling = screen?.Scaling ?? top.RenderScaling;
            var counters = _s.Counters;
            var size = CounterImages.PreviewSize(counters.NativeBounds(copy), new SKSizeI(area.Width, area.Height));
            if (counters.TryGetCached(copy, size) is { } hit) { Place(CounterThumbnails.ToBitmap(hit), anchor, cursor, area, scaling, pieceId, label); return; }
            var gate = RenderGate;
            var render = Task.Run(() =>
            {
                try
                {
                    gate?.Wait(10000);
                    // Hidden, superseded or closed meanwhile: never touch a disposed session's archive.
                    if (_stopped || gen != Volatile.Read(ref _generation) || _s.Disposed) return;
                    Interlocked.Increment(ref _renderCount);
                    var bmp = CounterThumbnails.ToBitmap(counters.Render(copy, size));   // the SKImage is the shared cache's: never disposed here
                    if (bmp == null) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_stopped && gen == _generation && _key == key) Place(bmp, anchor, cursor, area, scaling, pieceId, label);
                        else bmp.Dispose();
                    });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("counter preview: " + ex); }
            });
            lock (_renders)
            {
                _renders.RemoveWhere(t => t.IsCompleted);
                _renders.Add(render);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Hide(); }
    }

    void Place(Bitmap? bmp, Control anchor, PixelPoint cursor, PixelRect area, double scaling, string pieceId, string label)
    {
        if (bmp == null) return;
        try
        {
            var old = _bmp;
            _bmp = bmp;
            _image.Source = bmp;
            old?.Dispose();   // no longer shown
            var w = bmp.PixelSize.Width / scaling;
            var h = bmp.PixelSize.Height / scaling;
            _image.Width = w;
            _image.Height = h;
            _text.Text = label;
            _text.MaxWidth = Math.Max(w, 120);   // WinForms: the name wraps at max(picture width, 120)
            _box.Measure(Size.Infinity);
            var dip = _box.DesiredSize;
            var px = new SKSizeI((int)Math.Ceiling(dip.Width * scaling), (int)Math.Ceiling(dip.Height * scaling));
            var at = PreviewPlacement.Place(new SKPointI(cursor.X, cursor.Y), px, new SKRectI(area.X, area.Y, area.Right, area.Bottom));
            ScreenPosition = new PixelPoint(at.X, at.Y);
            Label = label;
            PieceId = pieceId;
            _popup.PlacementTarget = anchor;
            _popup.PlacementRect = new Rect(anchor.PointToClient(ScreenPosition), new Size(1, 1));
            _popup.IsOpen = false;   // Show hid it already; reopening places it afresh
            _popup.IsOpen = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Hide(); }
    }

    public void Hide()
    {
        _key = null;
        Interlocked.Increment(ref _generation);
        Label = null;
        PieceId = null;
        try
        {
            if (_popup.IsOpen) _popup.IsOpen = false;
            _image.Source = null;
            _bmp?.Dispose();
            _bmp = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>Hides the preview for good and waits briefly (2 s in all) for every render in flight, superseded ones too (as
    /// <see cref="CounterThumbnails.StopWorker"/>), so the session can be disposed afterwards. Later <see cref="Show"/> calls do nothing. Safe to
    /// call twice.</summary>
    public void Dispose()
    {
        _stopped = true;
        Hide();
        Task[] all;
        lock (_renders)
        {
            all = _renders.ToArray();
            _renders.Clear();
        }
        try { Task.WaitAll(all, 2000); }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException) { }
    }
}
