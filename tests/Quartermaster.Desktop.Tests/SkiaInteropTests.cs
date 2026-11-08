using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Quartermaster.Core;
using Quartermaster.Render;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// Version gate guard: Avalonia's Skia backend and Render.MapRenderer share one SkiaSharp (4.154.0) and one native libSkiaSharp.
/// A map drawn through Avalonia's own SKCanvas must come out pixel-identical to the same map drawn into a plain SKBitmap of the same colour type.
/// </summary>
public sealed class SkiaInteropTests : IDisposable
{
    const int W = 320, H = 240;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-desktop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string DemoVmod([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", "Riverland-Demo.vmod"));

    [Fact]
    public void Process_loads_a_single_SkiaSharp_4_154()
    {
        Assert.Equal(new Version(4, 154, 0, 0), typeof(SKCanvas).Assembly.GetName().Version);
        Assert.Single(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "SkiaSharp");
    }

    [AvaloniaFact]
    public void Avalonia_text_renders_with_the_pinned_HarfBuzz()
    {
        // HarfBuzzSharp keeps AssemblyVersion 1.0.0.0; the file version names the package.
        Assert.Equal("14.2.0", System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(HarfBuzzSharp.Buffer).Assembly.Location).FileVersion);
        var window = new Window { Width = 200, Height = 60, Background = Brushes.White,
            Content = new TextBlock { Text = "Quartermaster", FontSize = 20, Foreground = Brushes.Black } };
        window.Show();
        using var frame = window.CaptureRenderedFrame();
        window.Close();
        var dark = TestHelpers.Pixels(frame!).Px.Count(c => c.Red < 128 && c.Green < 128 && c.Blue < 128);
        Assert.True(dark > 50, $"only {dark} dark pixels: text did not render");
    }

    [AvaloniaFact]
    public void MapRenderer_draws_into_the_Avalonia_Skia_canvas()
    {
        using var s = ModuleSession.Open(DemoVmod(), Path.Combine(_tmp, "cache"), null);
        s.StartNewGame(Difficulty.FullyAuto);
        var map = s.Model.Maps[0];
        var tiles = new Dictionary<string, TileSet>();
        foreach (var b in map.Boards)
            tiles[BoardTiler.TileKey(b)] = s.Tiler.EnsureTilesAsync(b, null, CancellationToken.None).GetAwaiter().GetResult();
        var renderer = new MapRenderer(s.Images, new PieceRenderer(s.Images));
        var view = new Viewport();
        var size = new SKSize(W, H);
        FitView.ZoomToFit(view, map, size);
        var state = s.View!;
        var options = new MapDrawOptions(false, new HashSet<string>(), null);
        void Draw(SKCanvas c) => renderer.Draw(c, map, tiles, state, s.Model, view, size, options);

        using var expected = TestHelpers.ReferenceBitmap(W, H);
        using (var c = new SKCanvas(expected)) { c.Clear(SKColors.Magenta); Draw(c); }

        var control = new SkiaHost(Draw);
        var window = new Window { Width = W, Height = H, Content = control, Background = Brushes.Magenta };
        window.Show();
        using var frame = window.CaptureRenderedFrame();
        window.Close();

        Assert.NotNull(frame);
        Assert.True(control.Leases >= 1, "Avalonia never handed out its SKCanvas");
        Assert.Null(control.Error);
        // The reference must use the surface's colour type (see TestHelpers.ReferenceBitmap): Skia's filtered image draws differ between
        // Bgra8888 and Rgba8888 destinations, and the native type differs by OS (Bgra8888 on Windows/Linux, Rgba8888 on macOS).
        Assert.Equal((SKImageInfo.PlatformColorType, SKAlphaType.Premul), (control.SurfaceColorType, control.SurfaceAlphaType));
        var actual = TestHelpers.Pixels(frame!);
        Assert.Equal(W, actual.Width);
        Assert.Equal(H, actual.Height);
        var colours = actual.Px.Distinct().Count();
        Assert.True(colours > 50, $"frame has only {colours} colours: the map did not draw");
        TestHelpers.AssertSamePixels(actual, expected, 0);
    }

    sealed class SkiaHost(Action<SKCanvas> draw) : Control
    {
        readonly Action<SKCanvas> _draw = draw;
        public int Leases;
        public Exception? Error;
        public SKColorType? SurfaceColorType;
        public SKAlphaType? SurfaceAlphaType;

        public override void Render(DrawingContext context) => context.Custom(new Op(this, new Rect(Bounds.Size)));

        sealed class Op(SkiaHost host, Rect bounds) : ICustomDrawOperation
        {
            public Rect Bounds => bounds;
            public bool HitTest(Point p) => false;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                try
                {
                    var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>() ?? throw new InvalidOperationException("no Skia lease");
                    using var api = lease.Lease();
                    host.Leases++;
                    using (var px = api.SkSurface?.PeekPixels())
                    {
                        host.SurfaceColorType = px?.ColorType;
                        host.SurfaceAlphaType = px?.AlphaType;
                    }
                    host._draw(api.SkCanvas);
                }
                catch (Exception ex) { host.Error = ex; }
            }
        }
    }
}
