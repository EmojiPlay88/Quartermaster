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
}
