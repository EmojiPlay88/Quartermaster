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
/// A map drawn through Avalonia's own SKCanvas must come out pixel-identical to the same map drawn into a plain SKBitmap.
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
        var dark = Pixels(frame!).Px.Count(c => c.Red < 128 && c.Green < 128 && c.Blue < 128);
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

        using var expected = new SKBitmap(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(expected)) { c.Clear(SKColors.Magenta); Draw(c); }

        var control = new SkiaHost(Draw);
        var window = new Window { Width = W, Height = H, Content = control, Background = Brushes.Magenta };
        window.Show();
        using var frame = window.CaptureRenderedFrame();
        window.Close();

        Assert.NotNull(frame);
        Assert.True(control.Leases >= 1, "Avalonia never handed out its SKCanvas");
        Assert.Null(control.Error);
        var actual = Pixels(frame!);
        Assert.Equal(W, actual.Width);
        Assert.Equal(H, actual.Height);
        var colours = new HashSet<SKColor>();
        var diff = 0;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var a = actual.Px[y * W + x];
                var e = expected.GetPixel(x, y);
                colours.Add(a);
                if (a != e) diff++;
            }
        Assert.True(colours.Count > 50, $"frame has only {colours.Count} colours: the map did not draw");
        Assert.Equal(0, diff);
    }

    /// <summary>The captured frame as SKColors (headless frames are Rgba8888).</summary>
    static (int Width, int Height, SKColor[] Px) Pixels(Bitmap frame)
    {
        Assert.Equal(PixelFormat.Rgba8888, frame.Format);
        var w = frame.PixelSize.Width; var h = frame.PixelSize.Height;
        var bytes = new byte[w * h * 4];
        unsafe
        {
            fixed (byte* p = bytes)
                frame.CopyPixels(new PixelRect(0, 0, w, h), (nint)p, bytes.Length, w * 4);
        }
        var px = new SKColor[w * h];
        for (var i = 0; i < px.Length; i++) px[i] = new SKColor(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2], bytes[i * 4 + 3]);
        return (w, h, px);
    }

    sealed class SkiaHost(Action<SKCanvas> draw) : Control
    {
        readonly Action<SKCanvas> _draw = draw;
        public int Leases;
        public Exception? Error;

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
                    host._draw(api.SkCanvas);
                }
                catch (Exception ex) { host.Error = ex; }
            }
        }
    }
}
