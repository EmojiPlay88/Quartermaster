using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

/// <summary>MapView: draws through the shared MapRenderer, pans and zooms like the WinForms MapCanvas. Headless only.
/// Wheel handling depends on ShortcutGestures.IsMac, so the class sets it explicitly and lives in the MacFlag collection.</summary>
[Collection("MacFlag")]
public sealed class MapViewTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    const int W = 800, H = 600;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-mapview-" + Guid.NewGuid().ToString("N"));
    ModuleSession? _s;

    public void Dispose()
    {
        ShortcutGestures.IsMac = _wasMac;
        _s?.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string DemoVmod([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", "Riverland-Demo.vmod"));

    (MapView View, Window Window, Dictionary<string, TileSet> Tiles) Open(double scaling = 1, bool mac = false)
    {
        ShortcutGestures.IsMac = mac;
        _s = ModuleSession.Open(DemoVmod(), Path.Combine(_tmp, "cache"), null);
        _s.StartNewGame(Difficulty.FullyAuto);
        var map = _s.Model.Maps[0];
        var view = new MapView(_s, map);
        var tiles = new Dictionary<string, TileSet>();
        foreach (var b in map.Boards)
        {
            var ts = _s.Tiler.EnsureTilesAsync(b, null, CancellationToken.None).GetAwaiter().GetResult();
            tiles[BoardTiler.TileKey(b)] = ts;
            view.SetTiles(BoardTiler.TileKey(b), ts);
        }
        var window = new Window { Width = W, Height = H, Content = view };
        if (scaling != 1) window.SetRenderScaling(scaling);
        window.Show();
        return (view, window, tiles);
    }

    /// <summary>The renderer drawing natively at <paramref name="scale"/> device pixels per DIP (tile levels and SVG rasters chosen for it).</summary>
    SKBitmap Expected(MapView view, Dictionary<string, TileSet> tiles, float scale)
    {
        var bmp = new SKBitmap(new SKImageInfo((int)(W * scale), (int)(H * scale), SKColorType.Bgra8888, SKAlphaType.Premul));
        using var c = new SKCanvas(bmp);
        c.Scale(scale);
        new MapRenderer(_s!.Images, new PieceRenderer(_s.Images))
            .Draw(c, view.Map, tiles, _s.View!, _s.Model, view.View, new SKSize(W, H), new MapDrawOptions(false, new HashSet<string>(), null, DeviceScale: scale));
        return bmp;
    }

    static int CountDiffs((int Width, int Height, SKColor[] Px) actual, SKBitmap expected, int tolerance)
    {
        var diff = 0;
        for (var y = 0; y < actual.Height; y++)
            for (var x = 0; x < actual.Width; x++)
            {
                var a = actual.Px[y * actual.Width + x];
                var e = expected.GetPixel(x, y);
                if (Math.Abs(a.Red - e.Red) > tolerance || Math.Abs(a.Green - e.Green) > tolerance ||
                    Math.Abs(a.Blue - e.Blue) > tolerance || Math.Abs(a.Alpha - e.Alpha) > tolerance) diff++;
            }
        return diff;
    }

    static SKPoint P(double x, double y) => new((float)x, (float)y);

    [AvaloniaFact]
    public void Renders_same_pixels_as_renderer()
    {
        var (view, window, tiles) = Open();
        view.ZoomToFit();
        using var frame = window.CaptureRenderedFrame();
        window.Close();

        var actual = TestHelpers.Pixels(frame!);
        Assert.Equal((W, H), (actual.Width, actual.Height));
        Assert.True(actual.Px.Distinct().Count() > 50, "the map did not draw");
        using var expected = Expected(view, tiles, 1);
        Assert.Equal(0, CountDiffs(actual, expected, 2));
    }

    [AvaloniaFact]
    public void First_show_fits_the_map_capped_at_100_percent()
    {
        var (view, window, _) = Open();
        window.Close();
        var fit = new Viewport();
        FitView.ZoomToFit(fit, view.Map, new SKSize(W, H));
        Assert.Equal(Math.Min(fit.Zoom, 1f), view.View.Zoom, 4);
    }

    [AvaloniaFact]
    public void Wheel_zooms_at_cursor()
    {
        var (view, window, _) = Open();
        view.ZoomToFit();
        var z0 = view.View.Zoom;
        var m0 = view.View.ToMap(P(400, 300));
        var changed = 0;
        view.ViewportChanged += (_, _) => changed++;

        window.MouseWheel(new Point(400, 300), new Vector(0, 1));
        window.Close();

        Assert.Equal(z0 * 1.2f, view.View.Zoom, 4);
        var m1 = view.View.ToMap(P(400, 300));
        Assert.InRange(m1.X - m0.X, -0.5, 0.5);
        Assert.InRange(m1.Y - m0.Y, -0.5, 0.5);
        Assert.True(changed > 0, "ViewportChanged not raised");
    }

    [AvaloniaFact]
    public void Horizontal_wheel_pans()
    {
        var (view, window, _) = Open();
        view.ZoomToFit();
        var z0 = view.View.Zoom;
        var pan0 = view.View.Pan;

        window.MouseWheel(new Point(400, 300), new Vector(-1, 0));
        window.Close();

        // Avalonia's convention (ScrollViewer): offset -= Delta.X * 50 DIPs.
        Assert.Equal(pan0.X + 50 / z0, view.View.Pan.X, 3);
        Assert.Equal(pan0.Y, view.View.Pan.Y);
        Assert.Equal(z0, view.View.Zoom);
    }

    [AvaloniaFact]
    public void Windows_large_precise_wheel_delta_is_clamped()
    {
        var (view, window, _) = Open();
        view.ZoomCentered(0.5f);
        window.MouseWheel(new Point(400, 300), new Vector(0, 10));
        window.Close();
        Assert.Equal(0.5f * (float)Math.Pow(1.2, 3), view.View.Zoom, 4);
    }

    [AvaloniaFact]
    public void Mac_scroll_pans_both_axes()
    {
        var (view, window, _) = Open(mac: true);
        view.ZoomToFit();
        var z = view.View.Zoom;
        var pan0 = view.View.Pan;

        window.MouseWheel(new Point(400, 300), new Vector(-1, 0.5));
        window.Close();

        Assert.Equal(z, view.View.Zoom);
        // Natural direction as delivered: the view offset moves against the delta, like a ScrollViewer.
        Assert.Equal(pan0.X + 50 / z, view.View.Pan.X, 3);
        Assert.Equal(pan0.Y - 25 / z, view.View.Pan.Y, 3);
    }

    [AvaloniaFact]
    public void Mac_cmd_wheel_zooms_at_cursor()
    {
        var (view, window, _) = Open(mac: true);
        view.ZoomToFit();
        var z0 = view.View.Zoom;
        var m0 = view.View.ToMap(P(250, 350));

        window.MouseWheel(new Point(250, 350), new Vector(0, 1), RawInputModifiers.Meta);
        window.Close();

        Assert.Equal(z0 * 1.2f, view.View.Zoom, 4);
        var m1 = view.View.ToMap(P(250, 350));
        Assert.InRange(m1.X - m0.X, -0.5, 0.5);
        Assert.InRange(m1.Y - m0.Y, -0.5, 0.5);
    }

    [AvaloniaFact]
    public void Pinch_zooms_at_cursor()
    {
        var (view, window, _) = Open();
        view.ZoomToFit();
        var z0 = view.View.Zoom;
        var m0 = view.View.ToMap(P(300, 200));

        view.RaiseEvent(new PointerDeltaEventArgs(InputElement.PointerTouchPadGestureMagnifyEvent, view, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true),
            window, new Point(300, 200), 0, new PointerPointProperties(), KeyModifiers.None, new Vector(0.5, 0)));
        window.Close();

        Assert.Equal(z0 * 1.5f, view.View.Zoom, 4);
        var m1 = view.View.ToMap(P(300, 200));
        Assert.InRange(m1.X - m0.X, -0.5, 0.5);
        Assert.InRange(m1.Y - m0.Y, -0.5, 0.5);
    }

    [AvaloniaFact]
    public void Middle_drag_pans()
    {
        var (view, window, _) = Open();
        view.ZoomToFit();
        var z = view.View.Zoom;
        var pan0 = view.View.Pan;

        window.MouseDown(new Point(400, 300), MouseButton.Middle);
        window.MouseMove(new Point(450, 280), RawInputModifiers.MiddleMouseButton);
        window.MouseUp(new Point(450, 280), MouseButton.Middle);
        window.Close();

        Assert.Equal(pan0.X - 50 / z, view.View.Pan.X, 3);
        Assert.Equal(pan0.Y + 20 / z, view.View.Pan.Y, 3);
    }

    [AvaloniaFact]
    public void Space_and_left_drag_pans()
    {
        var (view, window, _) = Open();
        view.ZoomToFit();
        var z = view.View.Zoom;
        view.Focus();
        var pan0 = view.View.Pan;

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.MouseDown(new Point(400, 300), MouseButton.Left);
        window.MouseMove(new Point(380, 330), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(380, 330), MouseButton.Left);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        // Without Space a left drag on the map does not pan.
        var pan1 = view.View.Pan;
        window.MouseDown(new Point(400, 300), MouseButton.Left);
        window.MouseMove(new Point(380, 330), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(380, 330), MouseButton.Left);
        window.Close();

        Assert.Equal(pan0.X + 20 / z, pan1.X, 3);
        Assert.Equal(pan0.Y - 30 / z, pan1.Y, 3);
        Assert.Equal(pan1, view.View.Pan);
    }

    [AvaloniaFact]
    public void Pointer_to_map_is_exact_at_scale_2()
    {
        var (view, window, tiles) = Open(scaling: 2);
        Assert.Equal(2, window.RenderScaling);
        view.ZoomToFit();
        var dip = new Point(123.5, 77.25);
        var m = view.ScreenToMap(dip);
        var expectedMap = view.View.ToMap(P(dip.X, dip.Y));
        Assert.Equal(expectedMap.X, m.X, 6);
        Assert.Equal(expectedMap.Y, m.Y, 6);

        using var frame = window.CaptureRenderedFrame();
        var actual = TestHelpers.Pixels(frame!);
        Assert.Equal((2 * W, 2 * H), (actual.Width, actual.Height));
        using var expected = Expected(view, tiles, 2);
        // Counter centre: drawn at device resolution, so it matches the renderer drawing at 2x.
        var st = _s!.View!;
        var stack = st.StacksOn(view.Map.Id).First(k => k.PieceIds.Count == 1);
        var sp = view.View.ToScreen(stack.Pos);
        int cx = (int)Math.Round(sp.X * 2), cy = (int)Math.Round(sp.Y * 2);
        var a = actual.Px[cy * actual.Width + cx];
        var e = expected.GetPixel(cx, cy);
        Assert.True(Math.Abs(a.Red - e.Red) <= 2 && Math.Abs(a.Green - e.Green) <= 2 && Math.Abs(a.Blue - e.Blue) <= 2, $"centre {a} vs {e}");
        // The whole frame matches the renderer drawing natively at 2x (tile level and SVG rasters for 2x): nothing was upscaled.
        Assert.Equal(0, CountDiffs(actual, expected, 2));

        // The wheel uses DIP coordinates at scale 2: the map point under the cursor stays put.
        var m0 = view.View.ToMap(P(400, 300));
        window.MouseWheel(new Point(400, 300), new Vector(0, 1));
        window.Close();
        var m1 = view.View.ToMap(P(400, 300));
        Assert.InRange(m1.X - m0.X, -0.5, 0.5);
        Assert.InRange(m1.Y - m0.Y, -0.5, 0.5);
    }

    [AvaloniaFact]
    public void Fractional_scale_1_5_matches_native_renderer()
    {
        var (view, window, tiles) = Open(scaling: 1.5);
        view.ZoomCentered(0.4f); // 1x would pick tile level 1; 0.4 * 1.5 = 0.6 needs level 0
        using var frame = window.CaptureRenderedFrame();
        window.Close();
        var actual = TestHelpers.Pixels(frame!);
        Assert.Equal((1200, 900), (actual.Width, actual.Height));
        using var expected = Expected(view, tiles, 1.5f);
        Assert.Equal(0, CountDiffs(actual, expected, 2));
    }

    [AvaloniaFact]
    public void Counters_rasterised_for_device_pixels_at_scale_2()
    {
        var (view, window, tiles) = Open(scaling: 2);
        view.ZoomCentered(1f); // SVG bucket 1 at 1x; the 2x device needs bucket 2
        var st = _s!.View!;
        view.CentreOn(st.StacksOn(view.Map.Id).First(k => k.PieceIds.Count == 1).Pos);
        using var frame = window.CaptureRenderedFrame();
        window.Close();
        using var expected = Expected(view, tiles, 2);
        Assert.Equal(0, CountDiffs(TestHelpers.Pixels(frame!), expected, 2));
    }

    [Fact]
    public void PieceRenderer_rasterises_svg_counters_for_the_device_scale()
    {
        _s = ModuleSession.Open(DemoVmod(), Path.Combine(_tmp, "cache"), null);
        _s.StartNewGame(Difficulty.FullyAuto);
        var pieces = new PieceRenderer(_s.Images);
        var p = _s.View!.Pieces.Values.First(x => x.MapId == _s.Model.Maps[0].Id);
        var type = pieces.Types.Get(p.TypeString);
        // Zoom 1 on a 2x canvas (device scale 2) must equal zoom 2 drawn directly: both need the 2x SVG raster.
        using var hidpi = new SKBitmap(200, 200);
        using (var c = new SKCanvas(hidpi)) { c.Clear(SKColors.White); c.Scale(2); pieces.Draw(c, p, type, new PointD(50, 50), 1f, PieceDrawFlags.None, 2f); }
        using var direct = new SKBitmap(200, 200);
        using (var c = new SKCanvas(direct)) { c.Clear(SKColors.White); pieces.Draw(c, p, type, new PointD(100, 100), 2f, PieceDrawFlags.None); }
        var diff = 0;
        for (var y = 0; y < 200; y++)
            for (var x = 0; x < 200; x++)
            {
                SKColor a = hidpi.GetPixel(x, y), e = direct.GetPixel(x, y);
                if (Math.Abs(a.Red - e.Red) > 2 || Math.Abs(a.Green - e.Green) > 2 || Math.Abs(a.Blue - e.Blue) > 2) diff++;
            }
        Assert.Equal(0, diff);
    }

    [AvaloniaFact]
    public void Scaling_change_redraws_at_the_new_resolution()
    {
        var (view, window, tiles) = Open();
        view.ZoomCentered(0.4f);
        using (window.CaptureRenderedFrame()) { }
        window.SetRenderScaling(2);
        using var frame = window.CaptureRenderedFrame();
        window.Close();
        var actual = TestHelpers.Pixels(frame!);
        using var expected = Expected(view, tiles, 2);
        Assert.Equal(0, CountDiffs(actual, expected, 2));
    }

    [AvaloniaFact]
    public void Playback_failure_is_logged_once_on_the_UI_thread()
    {
        var (view, window, _) = Open();
        var before = _s!.Log.Items.Count;
        int? loggedOn = null;
        _s.Log.Added += _ => loggedOn = Environment.CurrentManagedThreadId;
        Task.Run(() => { view.PlaybackFailed(new InvalidOperationException("boom")); view.PlaybackFailed(new InvalidOperationException("boom")); }).Wait();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Close();

        var added = _s.Log.Items.Skip(before).ToList();
        var d = Assert.Single(added);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("Map drawing failed: boom", d.Message);
        Assert.Equal(Environment.CurrentManagedThreadId, loggedOn);
    }

    [AvaloniaFact]
    public void Clipped_to_its_bounds_inside_a_larger_window()
    {
        _s = ModuleSession.Open(DemoVmod(), Path.Combine(_tmp, "cache"), null);
        _s.StartNewGame(Difficulty.FullyAuto);
        var view = new MapView(_s, _s.Model.Maps[0]) { Width = 200, Height = 150 };
        var panel = new Canvas { Background = Avalonia.Media.Brushes.Magenta };
        Canvas.SetLeft(view, 50); Canvas.SetTop(view, 40);
        panel.Children.Add(view);
        var window = new Window { Width = 400, Height = 300, Content = panel };
        window.Show();
        using var frame = window.CaptureRenderedFrame();
        window.Close();
        var px = TestHelpers.Pixels(frame!);
        Assert.Equal(SKColors.Magenta, px.Px[10 * px.Width + 10]);
        Assert.Equal(SKColors.Magenta, px.Px[250 * px.Width + 300]);
        Assert.NotEqual(SKColors.Magenta, px.Px[100 * px.Width + 150]);
    }
}
