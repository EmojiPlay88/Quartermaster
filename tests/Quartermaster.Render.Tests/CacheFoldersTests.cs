using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class CacheFoldersTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-cache-" + Guid.NewGuid().ToString("N"));
    static readonly string ShaA = new('a', 64), ShaB = new('b', 64);

    public CacheFoldersTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    void Put(string rel, int n)
    {
        var p = Path.Combine(_tmp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[n]);
    }

    [Fact]
    public void Clear_SkipsOpenModule_AndNonModuleFolders()
    {
        Put(ShaA + "/tiles/t.png", 100);
        Put(ShaA + "/docs/d.pdf", 10);
        Put(ShaB + "/tiles/t.png", 200);
        Put("notes/tiles/keep.txt", 5);          // not a 64-hex module folder
        Put("settings.json", 1);

        var r = CacheFolders.Clear(_tmp, "tiles", Path.Combine(_tmp, ShaA));

        Assert.Equal(200, r.Freed);
        Assert.True(r.SkippedOpen);
        Assert.True(File.Exists(Path.Combine(_tmp, ShaA, "tiles", "t.png")));
        Assert.False(Directory.Exists(Path.Combine(_tmp, ShaB, "tiles")));
        Assert.True(File.Exists(Path.Combine(_tmp, "notes", "tiles", "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_tmp, ShaA, "docs", "d.pdf")));

        Assert.Equal((100L, 10L), CacheFolders.Sizes(_tmp));
        CacheFolders.Clear(_tmp, "docs", null);
        Assert.False(Directory.Exists(Path.Combine(_tmp, ShaA, "docs")));
        Assert.Equal((0L, 0L), CacheFolders.Sizes(Path.Combine(_tmp, "missing")));
    }

    [Fact]
    public void Clear_UnknownKind_DeletesNothing()
    {
        Put(ShaA + "/other/x", 5);
        Assert.Equal((0L, false), CacheFolders.Clear(_tmp, "other", null));
        Assert.True(File.Exists(Path.Combine(_tmp, ShaA, "other", "x")));
    }
}
