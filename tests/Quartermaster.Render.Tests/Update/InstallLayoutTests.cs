using Quartermaster.Render.Update;
using Xunit;

namespace Quartermaster.Render.Tests.Update;

public class InstallLayoutTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "qm-layout-" + Guid.NewGuid().ToString("N"));
    public InstallLayoutTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    static readonly Version V = new(0, 2, 0);
    static string? NoEnv(string _) => null;

    [Fact]
    public void Detect_NoMarker_Null() => Assert.Null(InstallLayout.Detect(_root, NoEnv));

    [Fact]
    public void Detect_WinZip_FolderLayout()
    {
        PackageFile.Write(_root, new PackageInfo(V, PackageKind.WinZip));
        var l = InstallLayout.Detect(_root, NoEnv);
        Assert.NotNull(l);
        Assert.EndsWith("Quartermaster.exe", l!.ExePath);
        Assert.Null(l.UnitPath);
        Assert.Equal(_root, l.AppDir);
        Assert.Equal(V, l.Version);
        Assert.True(l.SelfUpdates);
    }

    [Fact]
    public void Detect_MacApp_UnitIsBundle()
    {
        var bundle = Path.Combine(_root, "X", "Quartermaster.app");
        var dir = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(dir);
        PackageFile.Write(dir, new PackageInfo(V, PackageKind.MacApp));
        var l = InstallLayout.Detect(dir, NoEnv);
        Assert.NotNull(l);
        Assert.Equal(bundle, l!.UnitPath);
        Assert.Equal(Path.Combine(dir, "Quartermaster"), l.ExePath);
    }

    [Fact]
    public void Detect_MacApp_NotInBundle_Null()
    {
        var dir = Path.Combine(_root, "a", "b", "c");
        Directory.CreateDirectory(dir);
        PackageFile.Write(dir, new PackageInfo(V, PackageKind.MacApp));
        Assert.Null(InstallLayout.Detect(dir, NoEnv));
    }

    [Fact]
    public void Detect_AppImage_UsesEnv()
    {
        var img = Path.Combine(_root, "Quartermaster.AppImage");
        File.WriteAllText(img, "x");
        PackageFile.Write(_root, new PackageInfo(V, PackageKind.LinuxAppImage));
        var l = InstallLayout.Detect(_root, k => k == "APPIMAGE" ? img : null);
        Assert.NotNull(l);
        Assert.Equal(img, l!.UnitPath);
        Assert.Equal(img, l.ExePath);
    }

    [Fact]
    public void Detect_AppImage_NoEnv_Null()
    {
        PackageFile.Write(_root, new PackageInfo(V, PackageKind.LinuxAppImage));
        Assert.Null(InstallLayout.Detect(_root, NoEnv));
        Assert.Null(InstallLayout.Detect(_root, _ => " "));
        Assert.Null(InstallLayout.Detect(_root, _ => Path.Combine(_root, "missing.AppImage")));
    }

    [Theory]
    [InlineData(PackageKind.LinuxDeb)]
    [InlineData(PackageKind.LinuxRpm)]
    public void Detect_Deb_NotSelfUpdating(PackageKind k)
    {
        PackageFile.Write(_root, new PackageInfo(V, k));
        var l = InstallLayout.Detect(_root, NoEnv);
        Assert.NotNull(l);
        Assert.False(l!.SelfUpdates);
        Assert.EndsWith("Quartermaster", l.ExePath);
    }

    [Fact]
    public void PackageFile_RoundTrip_AllKinds()
    {
        foreach (var k in Enum.GetValues<PackageKind>())
        {
            PackageFile.Write(_root, new PackageInfo(V, k));
            Assert.Equal(new PackageInfo(V, k), PackageFile.Read(_root));
            Assert.Equal(k, PackageFile.ParseKind(PackageFile.KindText(k)));
        }
        Assert.Equal("win-zip", PackageFile.KindText(PackageKind.WinZip));
        Assert.Equal("linux-appimage", PackageFile.KindText(PackageKind.LinuxAppImage));
    }

    [Fact]
    public void PackageFile_BadJsonOrUnknownKind_Null()
    {
        var f = Path.Combine(_root, PackageFile.FileName);
        File.WriteAllText(f, "not json");
        Assert.Null(PackageFile.Read(_root));
        File.WriteAllText(f, "{\"version\":\"0.2.0\",\"package\":\"nope\"}");
        Assert.Null(PackageFile.Read(_root));
        File.WriteAllText(f, "{\"version\":\"0.2\",\"package\":\"win-zip\"}");
        Assert.Null(PackageFile.Read(_root));
        Assert.Null(PackageFile.Read(Path.Combine(_root, "nodir")));
    }
}
