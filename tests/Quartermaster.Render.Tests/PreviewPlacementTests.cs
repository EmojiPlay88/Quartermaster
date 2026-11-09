using Quartermaster.Render;
using SkiaSharp;

namespace Quartermaster.Render.Tests;

/// <summary>Where the big counter preview goes (moved from WinForms CounterPreviewTip.Place): cursor + 16, flipped at the screen edges.</summary>
public class PreviewPlacementTests
{
    static readonly SKRectI Screen = new(0, 0, 1920, 1040);

    [Fact]
    public void Preview_flips_at_screen_edges()
    {
        var size = new SKSizeI(212, 250);
        // Room on both sides: below right of the cursor.
        Assert.Equal(new SKPointI(116, 116), PreviewPlacement.Place(new SKPointI(100, 100), size, Screen));
        // Right edge: flipped to the left of the cursor.
        Assert.Equal(new SKPointI(1800 - 16 - 212, 116), PreviewPlacement.Place(new SKPointI(1800, 100), size, Screen));
        // Bottom edge: moved up so its bottom is the screen's bottom (not flipped above the cursor).
        Assert.Equal(new SKPointI(116, 1040 - 250), PreviewPlacement.Place(new SKPointI(100, 1000), size, Screen));
        // Both, near the bottom right corner.
        Assert.Equal(new SKPointI(1900 - 16 - 212, 1040 - 250), PreviewPlacement.Place(new SKPointI(1900, 1030), size, Screen));
        // Never off the left or top of the working area.
        var small = new SKRectI(50, 30, 300, 200);
        Assert.Equal(new SKPointI(50, 30), PreviewPlacement.Place(new SKPointI(100, 100), size, small));
        // A second monitor left of the primary: negative coordinates.
        var left = new SKRectI(-1920, 0, 0, 1080);
        Assert.Equal(new SKPointI(-100 - 16 - 212, 116), PreviewPlacement.Place(new SKPointI(-100, 100), size, left));
    }

    [Fact]
    public void Preview_just_fits_stays_below_right()
    {
        var size = new SKSizeI(100, 100);
        // x + w == right and y + h == bottom: still fits, not flipped.
        Assert.Equal(new SKPointI(1820, 940), PreviewPlacement.Place(new SKPointI(1804, 924), size, Screen));
        Assert.Equal(new SKPointI(1805 - 16 - 100, 1040 - 100), PreviewPlacement.Place(new SKPointI(1805, 925), size, Screen));
    }
}
