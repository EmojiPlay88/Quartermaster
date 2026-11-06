using System.IO.Compression;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Vassal.Tests;

public class SaveFileReaderTests
{
    [Fact]
    public void Deobfuscate_ReversesObfuscate()
    {
        var data = new byte[500];
        new Random(42).NextBytes(data);
        var obf = Obfuscation.Obfuscate(data, 0x5A);
        Assert.NotEqual(data, obf);
        Assert.Equal(data, Obfuscation.Deobfuscate(obf));
    }

    [Fact]
    public void Deobfuscate_PassesThroughPlain()
    {
        var plain = System.Text.Encoding.ASCII.GetBytes("begin_save");
        Assert.Equal(plain, Obfuscation.Deobfuscate(plain));
    }

    [Fact]
    public void Deobfuscate_MalformedHex_ReturnsInputUnchanged()
    {
        var bad = System.Text.Encoding.ASCII.GetBytes("!VCSK5Azz1");
        Assert.Equal(bad, Obfuscation.Deobfuscate(bad));
    }

    [Fact]
    public void Read_SplitsOnEsc()
    {
        var cmds = new[] { "begin_save", "", "+/1/piece;;;a.png;A/null;10;20;1;0", "end_save" };
        using var fx = new FixtureModule().AddSave("one.vsav", cmds);
        using var vmod = new ZipArchive(File.OpenRead(fx.Build()));
        using var vsav = vmod.GetEntry("one.vsav")!.Open();
        var ms = new MemoryStream();
        vsav.CopyTo(ms);
        ms.Position = 0;
        Assert.Equal(cmds, SaveFileReader.Read(ms).Commands);
    }

    [Fact]
    public void Read_MissingSavedGameEntry_ThrowsInvalidData()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            z.CreateEntry("other").Open().Dispose();
        ms.Position = 0;
        var ex = Assert.Throws<InvalidDataException>(() => SaveFileReader.Read(ms));
        Assert.Contains("savedGame", ex.Message);
    }

    [Fact]
    public void MinimalFixture_IsWellFormedAndContainsImages()
    {
        System.Xml.Linq.XDocument.Parse(FixtureModule.MinimalBuildFile());
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile());
        var path = fx.Build();
        using (var z = ZipFile.OpenRead(path))
        {
            Assert.NotNull(z.GetEntry("buildFile.xml"));
            Assert.NotNull(z.GetEntry("images/board.png"));
            Assert.NotNull(z.GetEntry("images/unit.svg"));
        }
        fx.Dispose();
        Assert.False(File.Exists(path));
    }
}
