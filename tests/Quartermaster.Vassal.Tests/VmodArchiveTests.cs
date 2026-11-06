using System.Security.Cryptography;
using System.Text;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Vassal.Tests;

public class VmodArchiveTests
{
    [Fact]
    public void Open_ParsesRootAndHashes()
    {
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile());
        var path = fx.Build();
        using var a = VmodArchive.Open(path);
        Assert.Equal("GameModule", a.Root.ShortName);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), a.Sha256);
        Assert.Equal(path, a.Path);
        Assert.Equal("images/board.png", VmodArchive.ImagePath("board.png"));
    }

    [Fact]
    public void Open_WhileAnotherHandleIsOpen_Succeeds()
    {
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile());
        var path = fx.Build();
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var a = VmodArchive.Open(path);
        Assert.Equal("GameModule", a.Root.ShortName);
    }

    [Fact]
    public void Parse_DecodesEntitiesAndOddEntryNames()
    {
        var xml = "<VASSAL.build.GameModule name=\"A &amp; B — ü\"/>";
        using var fx = new FixtureModule().WithBuildFile(xml)
            .AddFile("images/my map.png", new byte[] { 1, 2, 3 })
            .AddFile("Rules_A&B.pdf", new byte[] { 9, 8 });
        using var a = VmodArchive.Open(fx.Build());
        Assert.Equal("A & B — ü", a.Root.Attr("name"));
        Assert.True(a.HasEntry("images/my map.png"));
        Assert.True(a.HasEntry("Rules_A&B.pdf"));
        Assert.True(a.HasEntry("IMAGES/MY MAP.PNG"));
        Assert.False(a.HasEntry("nope"));
        using (var s = a.OpenEntry("images/my map.png")) { Assert.True(s.CanSeek); Assert.Equal(new byte[] { 1, 2, 3 }, ((MemoryStream)s).ToArray()); }
        using (var s = a.OpenEntry("Rules_A&B.pdf")) Assert.Equal(new byte[] { 9, 8 }, ((MemoryStream)s).ToArray());
        Assert.Contains("Rules_A&B.pdf", a.EntryNames);
    }

    [Fact]
    public void Open_FallsBackToBuildFileWithoutExtension()
    {
        using var fx = new FixtureModule().AddFile("buildFile", Encoding.UTF8.GetBytes("<a.B.Root/>"));
        using var a = VmodArchive.Open(fx.Build());
        Assert.Equal("Root", a.Root.ShortName);
    }

    [Fact]
    public void Open_WithoutBuildFile_Throws()
    {
        using var fx = new FixtureModule().AddFile("x.txt", new byte[] { 1 });
        var ex = Assert.Throws<InvalidDataException>(() => VmodArchive.Open(fx.Build()));
        Assert.Equal("Not a VASSAL module: no buildFile.xml", ex.Message);
    }

    [Fact]
    public void OpenEntry_Missing_Throws()
    {
        using var fx = new FixtureModule().WithBuildFile("<a.Root/>");
        using var a = VmodArchive.Open(fx.Build());
        Assert.Throws<FileNotFoundException>(() => a.OpenEntry("missing"));
    }
}
