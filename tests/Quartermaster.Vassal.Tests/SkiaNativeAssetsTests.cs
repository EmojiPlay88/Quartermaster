using Quartermaster.TestFixtures;

namespace Quartermaster.Vassal.Tests;

/// <summary>Linux CI needs libSkiaSharp.so in this test output, and every SkiaSharp package must be one version.</summary>
public class SkiaNativeAssetsTests
{
    [Fact]
    public void Linux_native_ships_and_one_SkiaSharp_version_resolves() =>
        Assert.Empty(SkiaNativeAssets.Problems(typeof(SkiaNativeAssetsTests).Assembly));
}
