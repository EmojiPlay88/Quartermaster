using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render.Tests;

public class CounterImagesTests
{
    private static PieceEnv Env()
    {
        var e = new PieceEnv();
        e.Fx.AddImage("red.png", 32, 32, PieceEnv.Red);
        return e.Open();
    }

    private static PieceInstance Red() => PieceEnv.Piece("p", PieceEnv.Basic("red.png"), "");

    [Fact]
    public void Key_ChangesWithTraitState()
    {
        var a = PieceEnv.Piece("p", "t", "1", "x");
        var b = PieceEnv.Piece("p", "t", "2", "x");
        Assert.NotEqual(CounterImages.Key(a, CounterImages.RowSize), CounterImages.Key(b, CounterImages.RowSize));
        Assert.Equal(CounterImages.Key(a, CounterImages.RowSize), CounterImages.Key(PieceEnv.Piece("q", "t", "1", "x"), CounterImages.RowSize));
    }

    [Fact]
    public void Key_ChangesWithSize()
    {
        var a = PieceEnv.Piece("p", "t", "1");
        Assert.NotEqual(CounterImages.Key(a, new SKSizeI(64, 48)), CounterImages.Key(a, new SKSizeI(200, 150)));
    }

    [Fact]
    public void PreviewSize_SmallCounter_200LongSide()
    {
        var s = CounterImages.PreviewSize(SKRect.Create(0, 0, 40, 40), new SKSizeI(1920, 1080));
        Assert.Equal(new SKSizeI(200, 200), s);
    }

    [Fact]
    public void PreviewSize_BigCounter_TwiceNative()
    {
        var s = CounterImages.PreviewSize(SKRect.Create(0, 0, 150, 150), new SKSizeI(1920, 1080));
        Assert.Equal(new SKSizeI(300, 300), s);
    }

    [Fact]
    public void PreviewSize_CappedToScreen()
    {
        var s = CounterImages.PreviewSize(SKRect.Create(0, 0, 150, 150), new SKSizeI(300, 300));
        Assert.Equal(new SKSizeI(240, 240), s);
    }

    [Fact]
    public void PreviewSize_KeepsAspect()
    {
        var s = CounterImages.PreviewSize(SKRect.Create(0, 0, 60, 30), new SKSizeI(1920, 1080));
        Assert.Equal(new SKSizeI(200, 100), s);
    }

    [Fact]
    public void Render_FixtureSvgPiece_HasPixels()
    {
        using var e = new PieceEnv().Open();
        var ci = new CounterImages(e.Images);
        var p = PieceEnv.Piece("p", PieceEnv.Basic("unit.svg"), "");
        var img = ci.Render(p, CounterImages.RowSize);
        Assert.NotNull(img);
        Assert.Equal(64, img!.Width);
        Assert.Equal(48, img.Height);
        using var bmp = SKBitmap.FromImage(img);
        Assert.True(bmp.GetPixel(32, 24).Alpha > 0);
        Assert.Equal(0, bmp.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void Render_Preview_RendersAtScaleSharp()
    {
        using var e = Env();
        var ci = new CounterImages(e.Images);
        var img = ci.Render(Red(), new SKSizeI(200, 200))!;
        using var bmp = SKBitmap.FromImage(img);
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(100, 100));
    }

    [Fact]
    public void Render_SecondCall_Cached()
    {
        using var e = Env();
        var ci = new CounterImages(e.Images);
        var p = Red();
        Assert.Null(ci.TryGetCached(p, CounterImages.RowSize));
        var a = ci.Render(p, CounterImages.RowSize);
        Assert.Same(a, ci.Render(p, CounterImages.RowSize));
        Assert.Same(a, ci.TryGetCached(p, CounterImages.RowSize));
        ci.Clear();
        Assert.Null(ci.TryGetCached(p, CounterImages.RowSize));
    }

    [Fact]
    public void NativeBounds_MatchesImageSize()
    {
        using var e = Env();
        var ci = new CounterImages(e.Images);
        var b = ci.NativeBounds(Red());
        Assert.Equal(32, b.Width, 1);
        Assert.Equal(32, b.Height, 1);
        Assert.Equal(new SKSizeI(200, 200), CounterImages.PreviewSize(b, new SKSizeI(1920, 1080)));
    }

    [Fact]
    public void Render_UndrawablePiece_Null()
    {
        using var e = Env();
        var ci = new CounterImages(e.Images);
        Assert.Null(ci.Render(null!, CounterImages.RowSize));
        Assert.Null(ci.Render(Red(), new SKSizeI(0, 0)));
    }
}
