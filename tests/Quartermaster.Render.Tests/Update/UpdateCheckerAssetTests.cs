using Quartermaster.Render.Update;
using Xunit;

namespace Quartermaster.Render.Tests.Update;

public class UpdateCheckerAssetTests
{
    static readonly Version V = new(0, 2, 0);

    [Theory]
    [InlineData(PackageKind.WinZip, "Quartermaster-0.2.0-win-x64.zip")]
    [InlineData(PackageKind.WinSetup, "Quartermaster-0.2.0-win-x64.zip")]
    [InlineData(PackageKind.MacApp, "Quartermaster-0.2.0-osx-arm64.zip")]
    [InlineData(PackageKind.LinuxTar, "Quartermaster-0.2.0-linux-x64.tar.gz")]
    [InlineData(PackageKind.LinuxAppImage, "Quartermaster-0.2.0-x86_64.AppImage")]
    [InlineData(PackageKind.LinuxDeb, "quartermaster_0.2.0_amd64.deb")]
    [InlineData(PackageKind.LinuxRpm, "quartermaster-0.2.0-1.x86_64.rpm")]
    public void AssetName_EachKind(PackageKind kind, string expected) => Assert.Equal(expected, UpdateChecker.AssetName(V, kind));

    static string Release(params PackageKind[] kinds)
    {
        var assets = new List<string>();
        foreach (var k in kinds)
            foreach (var n in new[] { UpdateChecker.AssetName(V, k), UpdateChecker.AssetName(V, k) + ".sha256" })
                if (!assets.Exists(a => a.Contains($"\"{n}\"")))
                    assets.Add($"{{\"name\":\"{n}\",\"browser_download_url\":\"https://example.test/{n}\",\"size\":100}}");
        return $"{{\"tag_name\":\"quartermaster-v0.2.0\",\"assets\":[{string.Join(",", assets)}]}}";
    }

    static readonly PackageKind[] All = Enum.GetValues<PackageKind>();

    [Fact]
    public void Parse_MacKind_PicksOsxZipOnly()
    {
        var i = UpdateChecker.Parse(Release(All), PackageKind.MacApp);
        Assert.NotNull(i);
        Assert.Equal("https://example.test/Quartermaster-0.2.0-osx-arm64.zip", i!.ZipUrl.ToString());
        Assert.Equal("https://example.test/Quartermaster-0.2.0-osx-arm64.zip.sha256", i.ShaUrl.ToString());
        Assert.Equal(PackageKind.MacApp, i.Kind);
        Assert.Equal("Quartermaster-0.2.0-osx-arm64.zip", i.AssetName);
    }

    [Fact]
    public void Parse_KindAssetMissing_Null() =>
        Assert.Null(UpdateChecker.Parse(Release(PackageKind.WinZip), PackageKind.LinuxAppImage));

    [Fact]
    public void Parse_NoKind_StillWinZip()
    {
        var i = UpdateChecker.Parse(Release(All));
        Assert.NotNull(i);
        Assert.Equal(PackageKind.WinZip, i!.Kind);
        Assert.Equal("Quartermaster-0.2.0-win-x64.zip", i.AssetName);
    }
}
