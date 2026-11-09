using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render.Tests;

public sealed class TempCache : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "qm-render-" + Guid.NewGuid().ToString("N"));
    public TempCache() => Directory.CreateDirectory(Dir);
    public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } }
}

public class ImageAndTileTests
{
    private static VmodArchive Open(FixtureModule fx) => VmodArchive.Open(fx.WithBuildFile(FixtureModule.MinimalBuildFile()).Build());

    [Fact]
    public void MissingImage_ReturnsPlaceholderAndLogsOnce()
    {
        using var fx = new FixtureModule(); using var tc = new TempCache();
        using var arch = Open(fx);
        var log = new DiagnosticLog();
        var src = new ImageSource(arch, tc.Dir, log);
        var a = src.Get("nope.png");
        var b = src.Get("nope.png");
        Assert.Equal(64, a.Width);
        Assert.Equal(64, a.Height);
        Assert.NotNull(b);
        Assert.Single(log.Items, d => d.Category == "image");
    }

    [Fact]
    public void Svg_RendersAtScaleBucket()
    {
        using var fx = new FixtureModule().AddSvg("s.svg"); using var tc = new TempCache();
        using var arch = Open(fx);
        var src = new ImageSource(arch, tc.Dir, new DiagnosticLog());
        var img = src.Get("s.svg", 2f);
        Assert.Equal(64, img.Width);
        Assert.Equal(64, img.Height);
        Assert.Equal(new SKSizeI(32, 32), src.SizeOf("s.svg"));
    }

    [Fact]
    public void ImageWithoutExtension_DecodedBySniffing()
    {
        using var fx = new FixtureModule(); using var tc = new TempCache();
        using var raw = new FixtureModule().AddImage("x.png", 10, 6);
        var bytes = new MemoryStream();
        using (var a2 = Open(raw)) a2.OpenEntry("images/x.png").CopyTo(bytes);
        fx.AddFile("images/noext", bytes.ToArray());
        using var arch = Open(fx);
        var src = new ImageSource(arch, tc.Dir, new DiagnosticLog());
        Assert.Equal(10, src.Get("noext").Width);
        Assert.Equal(new SKSizeI(10, 6), src.SizeOf("noext"));
    }

    private static BoardDef Board(string name, string image, int w, int h) => new(name, image, w, h, null, new PointD(0, 0));

    [Fact]
    public async Task Tiler_failure_nobody_awaits_is_not_an_unobserved_task_exception()
    {
        // A UI caller whose continuation never runs (window gone, app closing) must not leave a faulted task for the finalizer.
        using var fx = new FixtureModule().AddImage("big.png", 600, 400); using var tc = new TempCache();
        using var arch = Open(fx);
        var blocked = Path.Combine(tc.Dir, "blocked");
        File.WriteAllText(blocked, "a file where the cache folder should be");
        var tiler = new BoardTiler(arch, blocked, new DiagnosticLog());
        var seen = new List<Exception>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.ToString().Contains(nameof(BoardTiler))) lock (seen) seen.Add(e.Exception);
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            StartAndForget(tiler);
            for (var i = 0; i < 50 && seen.Count == 0; i++)
            {
                await Task.Delay(20);
                GC.Collect(); GC.WaitForPendingFinalizers();
            }
            Assert.Empty(seen);
            // Awaiting callers still get the failure.
            await Assert.ThrowsAnyAsync<Exception>(() => tiler.EnsureTilesAsync(Board("Big", "big.png", 600, 400), null, CancellationToken.None));
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void StartAndForget(BoardTiler tiler)
    {
        var t = tiler.EnsureTilesAsync(Board("Big", "big.png", 600, 400), null, CancellationToken.None);
        ((IAsyncResult)t).AsyncWaitHandle.WaitOne(5000);
    }

    [Fact]
    public async Task Tiler_ProducesExpectedGridAndLevels()
    {
        using var fx = new FixtureModule().AddImage("big.png", 3000, 2000); using var tc = new TempCache();
        using var arch = Open(fx);
        var tiler = new BoardTiler(arch, tc.Dir, new DiagnosticLog());
        var ts = await tiler.EnsureTilesAsync(Board("Big", "big.png", 3000, 2000), null, CancellationToken.None);
        Assert.Equal(3000, ts.Width);
        Assert.Equal(2000, ts.Height);
        Assert.Equal(2, ts.Levels);
        Assert.Equal(6, ts.TilesX(0));
        Assert.Equal(4, ts.TilesY(0));
        for (var l = 0; l <= 2; l++)
            for (var ty = 0; ty < ts.TilesY(l); ty++)
                for (var tx = 0; tx < ts.TilesX(l); tx++)
                {
                    var path = ts.TilePath(l, tx, ty);
                    Assert.True(File.Exists(path), path);
                    using var bmp = SKBitmap.Decode(path);
                    Assert.InRange(bmp.Width, 1, 512);
                    Assert.InRange(bmp.Height, 1, 512);
                }
        Assert.False(File.Exists(ts.TilePath(0, 6, 0)));
        Assert.Equal(1, tiler.DecodeCount);
    }

    [Fact]
    public async Task Tiler_SameBoardNameDifferentImages_NeverShareOrOverwrite()
    {
        using var fx = new FixtureModule().AddImage("a.b.png", 700, 300).AddImage("a_b.png", 900, 400); using var tc = new TempCache();
        using var arch = Open(fx);
        var b1 = Board("Main", "a.b.png", 700, 300);
        var b2 = Board("Main", "a_b.png", 900, 400);
        var first = new BoardTiler(arch, tc.Dir, new DiagnosticLog());
        var t1 = await first.EnsureTilesAsync(b1, null, CancellationToken.None);
        var t2 = await first.EnsureTilesAsync(b2, null, CancellationToken.None);
        Assert.NotEqual(t1.Root, t2.Root);
        Assert.Equal((700, 300), (t1.Width, t1.Height));
        Assert.Equal((900, 400), (t2.Width, t2.Height));
        Assert.True(File.Exists(t1.TilePath(0, 1, 0)));
        Assert.True(File.Exists(t2.TilePath(0, 1, 0)));
        Assert.NotEqual(BoardTiler.TileKey(b1), BoardTiler.TileKey(b2));
        Assert.Equal(2, first.DecodeCount);

        var second = new BoardTiler(arch, tc.Dir, new DiagnosticLog());
        var h1 = await second.EnsureTilesAsync(b1, null, CancellationToken.None);
        var h2 = await second.EnsureTilesAsync(b2, null, CancellationToken.None);
        Assert.Equal(0, second.DecodeCount);
        Assert.Equal((700, 300), (h1.Width, h1.Height));
        Assert.Equal((900, 400), (h2.Width, h2.Height));
    }

    [Fact]
    public async Task Tiler_SecondRunIsCacheHit()
    {
        using var fx = new FixtureModule().AddImage("big.png", 1300, 700); using var tc = new TempCache();
        using var arch = Open(fx);
        var board = Board("B/ad:name", "big.png", 1300, 700);
        var first = new BoardTiler(arch, tc.Dir, new DiagnosticLog());
        await first.EnsureTilesAsync(board, null, CancellationToken.None);
        Assert.Equal(1, first.DecodeCount);
        var second = new BoardTiler(arch, tc.Dir, new DiagnosticLog());
        var ts = await second.EnsureTilesAsync(board, null, CancellationToken.None);
        Assert.Equal(0, second.DecodeCount);
        Assert.Equal(1300, ts.Width);
        Assert.Equal(1, ts.Levels);
        Assert.True(File.Exists(ts.TilePath(1, 0, 0)));
    }

    [Fact]
    public async Task Tiler_IncompletePyramidWithoutMarkerIsRebuilt()
    {
        using var fx = new FixtureModule().AddImage("b.png", 600, 300); using var tc = new TempCache();
        using var arch = Open(fx);
        var board = Board("B", "b.png", 600, 300);
        var t1 = new BoardTiler(arch, tc.Dir, new DiagnosticLog());
        var ts = await t1.EnsureTilesAsync(board, null, CancellationToken.None);
        File.Delete(Path.Combine(ts.Root, "done.json"));
        File.Delete(ts.TilePath(0, 1, 0));
        var t2 = new BoardTiler(arch, tc.Dir, new DiagnosticLog());
        await t2.EnsureTilesAsync(board, null, CancellationToken.None);
        Assert.Equal(1, t2.DecodeCount);
        Assert.True(File.Exists(ts.TilePath(0, 1, 0)));
    }

    [Fact]
    public async Task Tiler_MissingImage_GivesPlaceholderTileSetAndOneDiagnostic()
    {
        using var fx = new FixtureModule(); using var tc = new TempCache();
        using var arch = Open(fx);
        var log = new DiagnosticLog();
        var tiler = new BoardTiler(arch, tc.Dir, log);
        var ts = await tiler.EnsureTilesAsync(Board("B", "gone.png", 400, 300), null, CancellationToken.None);
        await tiler.EnsureTilesAsync(Board("B", "gone.png", 400, 300), null, CancellationToken.None);
        Assert.Equal(400, ts.Width);
        Assert.True(ts.IsPlaceholder);
        Assert.Single(log.Items);
    }

    [Theory]
    [InlineData(1f, 0)]
    [InlineData(0.5f, 1)]
    [InlineData(0.3f, 1)]
    [InlineData(0.25f, 2)]
    [InlineData(4f, 0)]
    public void TileSet_ChooseLevel(float zoom, int expected)
    {
        var ts = new TileSet("x", 3000, 2000, 2);
        Assert.Equal(expected, ts.ChooseLevel(zoom));
    }

    [Fact]
    public void TileSet_ChooseLevel_ClampsToLevels()
        => Assert.Equal(0, new TileSet("x", 800, 600, 0).ChooseLevel(0.25f));

    [Fact]
    public void TileLevel_uses_device_pixels()
    {
        var ts = new TileSet("x", 3000, 2000, 2);
        // HiDPI: zoom 0.5 on a 2x canvas needs the same pixels as zoom 1 at 1x.
        Assert.Equal(MapRenderer.TileLevel(ts, 1f, 1f), MapRenderer.TileLevel(ts, 0.5f, 2f));
        Assert.Equal(0, MapRenderer.TileLevel(ts, 0.5f, 2f));
        Assert.Equal(MapRenderer.TileLevel(ts, 0.375f), MapRenderer.TileLevel(ts, 0.25f, 1.5f));
        // Default scale 1 (WinForms) is exactly ChooseLevel; nonsense scales fall back to 1.
        Assert.Equal(ts.ChooseLevel(0.5f), MapRenderer.TileLevel(ts, 0.5f));
        Assert.Equal(1, MapRenderer.TileLevel(ts, 0.5f, 0f));
        Assert.Equal(1, MapRenderer.TileLevel(ts, 0.5f, float.NaN));
    }
}
