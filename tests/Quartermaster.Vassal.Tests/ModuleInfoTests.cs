using System.Text;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Vassal.Tests;

public class ModuleInfoTests
{
    [Fact]
    public void ModuleInfo_ReadsTitleVersion()
    {
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile())
            .AddFile("moduledata", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><data version=\"1\"><version>2.5</version><VassalVersion>3.7.0</VassalVersion><name>Synthetic Game</name></data>"));
        var info = ModuleInfo.Read(fx.Build());
        Assert.Equal(new ModuleInfo("Synthetic Game", "2.5"), info);
    }

    [Fact]
    public void ModuleInfo_NoModuledata_UsesBuildFileRootAttributes()
    {
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile());
        Assert.Equal(new ModuleInfo("Fixture", "1.0"), ModuleInfo.Read(fx.Build()));
    }

    [Fact]
    public void BadMetadata_FallsBackToFileName()
    {
        using var fx = new FixtureModule().WithBuildFile("not xml at all <<<")
            .AddFile("moduledata", Encoding.UTF8.GetBytes("<data><name>"));
        var path = fx.Build();
        Assert.Equal(new ModuleInfo(Path.GetFileNameWithoutExtension(path), ""), ModuleInfo.Read(path));
        var missing = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".vmod");
        Assert.Equal(Path.GetFileNameWithoutExtension(missing), ModuleInfo.Read(missing).Title);
        var junk = Path.Combine(Path.GetTempPath(), "junk-" + Guid.NewGuid().ToString("N") + ".vmod");
        File.WriteAllText(junk, "not a zip");
        try { Assert.Equal(new ModuleInfo(Path.GetFileNameWithoutExtension(junk), ""), ModuleInfo.Read(junk)); }
        finally { File.Delete(junk); }
    }
}
